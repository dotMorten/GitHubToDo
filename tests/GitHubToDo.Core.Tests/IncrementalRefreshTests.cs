using GitHubToDo.Core;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class IncrementalRefreshTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-15T22:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TrackedItem Incoming(string id, DateTimeOffset updatedAt)
    {
        var item = FakeClient.NewItem();
        item.Id = id;
        item.UpdatedAt = updatedAt;
        return item;
    }

    private static AccountSnapshot Cached(Clock clock, int count = 2)
    {
        var account = new AccountSnapshot { User = new("1", "viewer") };
        for (var index = 0; index < count; index++)
        {
            var item = Incoming($"item-{index}", clock.Now.AddHours(-2));
            item.HasBaseline = true;
            item.ActivityCheckedAt = clock.Now.AddMinutes(-1);
            item.Activities = [new($"{item.Id}:old", ActivityKind.Comment, "other", "other", item.UpdatedAt, "cached", "")];
            item.AcknowledgedIds.Add($"{item.Id}:old");
            account.Items.Add(item.Id, item);
        }
        foreach (var query in account.Queries)
        {
            query.ItemIds = account.Items.Keys.ToList();
            query.TotalCount = count;
            query.LastRefreshedAt = clock.Now.AddMinutes(-2);
            query.LastFullRefreshedAt = clock.Now.AddMinutes(-10);
        }
        return account;
    }

    private static async Task<WorkspaceService> OpenAsync(TestDatabase database, FakeClient client, Clock clock, AccountSnapshot account)
    {
        await database.Store.SaveAsync(account);
        var workspace = new WorkspaceService(database.Store, client, clock);
        await workspace.OpenAsync(account.User);
        return workspace;
    }

    [TestMethod]
    public async Task QuietPollKeepsFiveHundredCachedItemsWithoutRequestingTheirDiscussion()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var expressions = new List<string>();
        var client = new FakeClient
        {
            Search = (expression, _) =>
            {
                expressions.Add(expression);
                return Task.FromResult(new SearchResult([], 0, false));
            }
        };
        var workspace = await OpenAsync(database, client, clock, Cached(clock, 500));
        await workspace.PollAsync();
        Assert.AreEqual(0, client.ActivityCalls);
        Assert.HasCount(3, expressions);
        Assert.IsTrue(expressions.All(expression => expression.Contains("updated:>=2026-10-15T21:53:00Z") &&
            expression.EndsWith("sort:updated-desc")));
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.ItemIds.Count == 500 && query.TotalCount == 500));
        Assert.IsTrue(workspace.Account.Items.Values.All(item => item.AcknowledgedIds.Contains($"{item.Id}:old")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ManualRefreshDoesNotTurn347OldCachedItemsIntoAFullScan(bool changed)
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock, 347);
        foreach (var query in account.Queries) query.LastFullRefreshedAt = clock.Now.AddHours(-1);
        foreach (var item in account.Items.Values) item.ActivityCheckedAt = clock.Now.AddHours(-1);
        account.Items["item-1"].IsFlagged = true;
        var expressions = new List<string>();
        var client = new FakeClient
        {
            Search = (expression, _) =>
            {
                expressions.Add(expression);
                return Task.FromResult(new SearchResult(changed ? [Incoming("item-0", clock.Now)] : [], changed ? 1 : 0, false));
            }
        };
        var workspace = await OpenAsync(database, client, clock, account);
        workspace.PreferredItemId = "item-0";
        await workspace.CheckForUpdatesAsync();
        Assert.AreEqual(changed ? 1 : 0, client.ActivityCalls,
            "Manual refresh must fetch changed discussion only, not all 347 stale items.");
        Assert.IsTrue(expressions.All(expression => expression.Contains("updated:>=")));
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.ItemIds.Count == 347 &&
            query.LastFullRefreshedAt == clock.Now.AddHours(-1)));
    }

    [TestMethod]
    public async Task RetryingFailedSearchMembershipDoesNotReloadUnchangedDiscussionsOnManualRefresh()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock, 347);
        foreach (var query in account.Queries) query.Error = "Previous search failed";
        foreach (var item in account.Items.Values) item.ActivityCheckedAt = clock.Now.AddHours(-1);
        var client = new FakeClient
        {
            Search = (_, _) => Task.FromResult(new SearchResult(
                account.Items.Values.Select(item => Incoming(item.Id, item.UpdatedAt)).ToArray(), 347, false))
        };
        var workspace = await OpenAsync(database, client, clock, account);
        await workspace.CheckForUpdatesAsync();
        Assert.AreEqual(0, client.ActivityCalls);
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.Error is null && query.ItemIds.Count == 347));
    }

    [TestMethod]
    public async Task OverlappingUnchangedMatchesDoNotReloadHistoryOrReplaceMembershipAndTotals()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var client = new FakeClient
        {
            Search = (_, _) => Task.FromResult(new SearchResult([Incoming("item-0", clock.Now.AddHours(-2))], 1, false))
        };
        var workspace = await OpenAsync(database, client, clock, Cached(clock));
        await workspace.PollAsync();
        Assert.AreEqual(0, client.ActivityCalls);
        var saved = await database.Store.LoadAsync(client.User);
        Assert.IsTrue(saved.Queries.All(query => query.ItemIds.Count == 2 && query.TotalCount == 2 &&
            query.LastRefreshedAt == clock.Now && query.LastFullRefreshedAt == clock.Now.AddMinutes(-10)));
        Assert.Contains("item-0:old", saved.Items["item-0"].AcknowledgedIds);
    }

    [TestMethod]
    public async Task OnlyChangedAndNewItemsHydrateOnceAcrossOverlappingQueries()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var hydrated = new List<string>();
        var client = new FakeClient
        {
            Search = (_, _) => Task.FromResult(new SearchResult(
                [Incoming("item-0", clock.Now), Incoming("new", clock.Now)], 2, false)),
            Activity = (item, _) =>
            {
                hydrated.Add(item.Id);
                return Task.FromResult<IReadOnlyList<Activity>>(
                    [new("new-comment", ActivityKind.Comment, "other", "other", clock.Now, "new", "")]);
            }
        };
        var workspace = await OpenAsync(database, client, clock, Cached(clock));
        await workspace.PollAsync();
        CollectionAssert.AreEquivalent(new[] { "item-0", "new" }, hydrated);
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.ItemIds.Count == 3));
        Assert.Contains("item-0:old", workspace.Account.Items["item-0"].AcknowledgedIds);
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(workspace.Account.Items["item-0"], "1", []));
    }

    [TestMethod]
    public async Task FullReconciliationDetectsReviewActivityEvenWithoutAnUpdatedTimestamp()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var expressions = new List<string>();
        var account = Cached(clock);
        var client = new FakeClient
        {
            Search = (expression, _) =>
            {
                expressions.Add(expression);
                return Task.FromResult(new SearchResult(
                    account.Items.Values.Select(item => Incoming(item.Id, item.UpdatedAt)).ToArray(), 2, false));
            },
            Activity = (_, _) => Task.FromResult<IReadOnlyList<Activity>>(
                [new("new-review", ActivityKind.Review, "other", "other", clock.Now, "APPROVED", "")])
        };
        var workspace = await OpenAsync(database, client, clock, account);
        clock.Now += TimeSpan.FromMinutes(30);
        await workspace.PollAsync();
        Assert.IsTrue(expressions.All(expression => !expression.Contains("updated:>=")));
        Assert.AreEqual(2, client.ActivityCalls);
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.LastFullRefreshedAt == clock.Now));
        Assert.IsTrue(workspace.Account.Items.Values.All(item =>
            item.Activities.Single().Kind == ActivityKind.Review &&
            ReadStatePolicy.Evaluate(item, "1", []) == ReadStatus.Unread));
    }

    [TestMethod]
    public async Task EmptyDeltaRetainsMembershipUntilTheFullReconciliationRemovesDepartedItems()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var client = new FakeClient { Search = (_, _) => Task.FromResult(new SearchResult([], 0, false)) };
        var workspace = await OpenAsync(database, client, clock, Cached(clock));
        await workspace.PollAsync();
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.ItemIds.Count == 2));
        clock.Now += TimeSpan.FromMinutes(30);
        await workspace.PollAsync();
        Assert.IsTrue(workspace.Account.Queries.All(query => query.ItemIds.Count == 0 && query.TotalCount == 0));
        Assert.HasCount(2, workspace.Account.Items, "Removing query membership must not discard read history.");
    }

    [TestMethod]
    [DataRow("failure")]
    [DataRow("partial")]
    [DataRow("capped")]
    public async Task UnsuccessfulDeltaDoesNotAdvanceTheWatermarkOrEraseMembership(string failure)
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var client = new FakeClient
        {
            Search = (_, _) => failure == "failure" ? throw new GitHubException("offline") :
                Task.FromResult(new SearchResult([], failure == "capped" ? 1001 : 1, failure == "partial"))
        };
        var workspace = await OpenAsync(database, client, clock, Cached(clock));
        await workspace.PollAsync();
        var restored = await database.Store.LoadAsync(client.User);
        Assert.IsTrue(restored.Queries.All(query => query.LastRefreshedAt == clock.Now.AddMinutes(-2) &&
            query.LastFullRefreshedAt == clock.Now.AddMinutes(-10) && query.ItemIds.Count == 2 && query.Error is not null));
    }

    [TestMethod]
    public async Task PendingAndFailedItemsRetryWithoutReloadingUnchangedSelectionOrFlags()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock, 4);
        account.Items["item-0"].HasBaseline = false;
        account.Items["item-1"].ActivityError = "offline";
        account.Items["item-3"].IsFlagged = true;
        var flagged = Incoming("flagged", clock.Now.AddHours(-2));
        flagged.HasBaseline = true;
        flagged.ActivityCheckedAt = clock.Now;
        flagged.IsFlagged = true;
        account.Items.Add(flagged.Id, flagged);
        var hydrated = new List<string>();
        var client = new FakeClient
        {
            Search = (_, _) => Task.FromResult(new SearchResult([], 0, false)),
            Activity = (item, _) =>
            {
                hydrated.Add(item.Id);
                return Task.FromResult<IReadOnlyList<Activity>>([]);
            }
        };
        var workspace = await OpenAsync(database, client, clock, account);
        workspace.PreferredItemId = "item-2";
        await workspace.PollAsync();
        CollectionAssert.AreEquivalent(new[] { "item-0", "item-1" }, hydrated);
        Assert.IsTrue(workspace.Account!.Items["flagged"].IsFlagged);
        hydrated.Clear();
        clock.Now += TimeSpan.FromMinutes(30);
        await workspace.PollAsync();
        CollectionAssert.AreEquivalent(new[] { "item-3", "flagged" }, hydrated,
            "Flagged items inside and outside saved queries still receive periodic reconciliation.");
    }

    [TestMethod]
    public async Task ManualRefreshDuringAPollQueuesOneFullPassAndCoalescesOtherRequests()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expressions = new List<string>();
        var client = new FakeClient
        {
            Search = async (expression, ct) =>
            {
                expressions.Add(expression);
                if (expressions.Count == 1)
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(ct);
                }
                return expression.Contains("updated:>=") ? new SearchResult([], 0, false) :
                    new SearchResult([Incoming("item-0", clock.Now.AddHours(-2))], 1, false);
            }
        };
        var workspace = await OpenAsync(database, client, clock, Cached(clock, 1));
        var poll = workspace.PollAsync();
        await entered.Task;
        Assert.AreSame(poll, workspace.RefreshAsync());
        Assert.AreSame(poll, workspace.RefreshAsync());
        release.SetResult();
        await poll;
        Assert.HasCount(6, expressions);
        Assert.IsTrue(expressions.Take(3).All(expression => expression.Contains("updated:>=")));
        Assert.IsTrue(expressions.Skip(3).All(expression => !expression.Contains("updated:>=")));
        Assert.AreEqual(1, client.ActivityCalls);
    }

    [TestMethod]
    public async Task OlderCachedPayloadsAcquireAPersistedFullRefreshAnchorWithoutAHeavyStartup()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock);
        foreach (var query in account.Queries) query.LastFullRefreshedAt = null;
        var client = new FakeClient { Search = (_, _) => Task.FromResult(new SearchResult([], 0, false)) };
        var workspace = await OpenAsync(database, client, clock, account);
        var anchor = clock.Now.AddMinutes(-2);
        await workspace.PollAsync();
        clock.Now += TimeSpan.FromMinutes(2);
        var reopened = new WorkspaceService(database.Store, client, clock);
        await reopened.OpenAsync(client.User);
        await reopened.PollAsync();
        Assert.AreEqual(0, client.ActivityCalls);
        Assert.IsTrue(reopened.Account!.Queries.All(query => query.LastFullRefreshedAt == anchor));
    }

    [TestMethod]
    public async Task CancelledDiscussionFetchRetriesEvenWhenTheNextDeltaIsEmpty()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            Search = (_, _) => Task.FromResult(new SearchResult([Incoming("item-0", clock.Now)], 1, false)),
            Activity = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            }
        };
        var workspace = await OpenAsync(database, client, clock, Cached(clock, 1));
        using var cancellation = new CancellationTokenSource();
        var poll = workspace.PollAsync(cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await poll);
        client.Search = (_, _) => Task.FromResult(new SearchResult([], 0, false));
        client.Activity = (_, _) => Task.FromResult<IReadOnlyList<Activity>>([]);
        await workspace.PollAsync();
        Assert.AreEqual(2, client.ActivityCalls);
        Assert.AreEqual(clock.Now, workspace.Account!.Items["item-0"].ActivityCheckedAt);
    }

    [TestMethod]
    public async Task FailedDiscussionFetchRetriesFromCacheAfterItsSearchWatermarkAdvances()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var client = new FakeClient
        {
            Search = (_, _) => Task.FromResult(new SearchResult([Incoming("item-0", clock.Now)], 1, false)),
            Activity = (_, _) => throw new GitHubException("offline")
        };
        var workspace = await OpenAsync(database, client, clock, Cached(clock, 1));
        await workspace.PollAsync();
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.LastRefreshedAt == clock.Now));
        Assert.AreEqual("offline", workspace.Account.Items["item-0"].ActivityError);
        client.Search = (_, _) => Task.FromResult(new SearchResult([], 0, false));
        client.Activity = (_, _) => Task.FromResult<IReadOnlyList<Activity>>([]);
        await workspace.PollAsync();
        Assert.AreEqual(2, client.ActivityCalls);
        Assert.IsNull(workspace.Account.Items["item-0"].ActivityError);
        Assert.Contains("item-0:old", workspace.Account.Items["item-0"].AcknowledgedIds);
    }

    [TestMethod]
    public async Task ClockRollbackDoesNotSendASearchWindowInTheFuture()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var expressions = new List<string>();
        var client = new FakeClient
        {
            Search = (expression, _) =>
            {
                expressions.Add(expression);
                return Task.FromResult(new SearchResult([], 0, false));
            }
        };
        var workspace = await OpenAsync(database, client, clock, Cached(clock));
        clock.Now -= TimeSpan.FromMinutes(5);
        await workspace.PollAsync();
        Assert.IsTrue(expressions.All(expression => !expression.Contains("updated:>=")));
    }

    [TestMethod]
    public async Task CappedIncrementalMembershipKeepsTheNewestCachedItemsInsteadOfBlindlyPrependingResults()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock, 1000);
        account.Items["item-0"].UpdatedAt = clock.Now.AddMinutes(-1);
        account.Items["item-0"].ActivityCheckedAt = clock.Now;
        var client = new FakeClient
        {
            Search = (_, _) => Task.FromResult(new SearchResult([Incoming("new", clock.Now.AddMinutes(-3))], 1, false))
        };
        var workspace = await OpenAsync(database, client, clock, account);
        await workspace.PollAsync();
        foreach (var query in workspace.Account!.Queries)
        {
            Assert.HasCount(1000, query.ItemIds);
            Assert.AreEqual("item-0", query.ItemIds[0]);
            Assert.AreEqual("new", query.ItemIds[1]);
            Assert.IsTrue(query.IsIncomplete);
        }
        Assert.HasCount(1001, workspace.Account.Items, "Evicting capped membership must not discard cached read history.");
    }
}
