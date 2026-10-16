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
    public async Task ExistingPrCachePrimesMetadataIncludingOnlyNonemptyReviewBodiesInTotalComments()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock, 1);
        var item = account.Items["item-0"];
        item.IsPullRequest = true;
        item.IsFlagged = true;
        item.Activities.AddRange(
        [
            new("inline:1", ActivityKind.ReviewComment, "other", "other", item.UpdatedAt, "Inline comment", ""),
            new("review:1", ActivityKind.Review, "other", "other", item.UpdatedAt, "APPROVED: ", ""),
            new("review:2", ActivityKind.Review, "other", "other", item.UpdatedAt, "COMMENTED: Review body", "")
        ]);
        var client = new FakeClient { Metadata = cached => new(cached.UpdatedAt, 1, 2, 3, "review:2", cached.UpdatedAt) };
        var workspace = await OpenAsync(database, client, clock, account);
        await workspace.CheckForUpdatesAsync(WorkspaceService.FollowUpsQueryId);
        var saved = (await database.Store.LoadAsync(client.User)).Items[item.Id];
        Assert.AreEqual(1, client.MetadataCalls);
        Assert.AreEqual(0, client.ActivityCalls);
        Assert.HasCount(4, saved.Activities);
        Assert.AreEqual(saved.ActivityMetadata, saved.LoadedActivityMetadata);
        Assert.IsNotNull(saved.LoadedActivityMetadata);
    }

    [TestMethod]
    public async Task MetadataFailureAndRecoveryPreserveCachedDiscussionWithoutDownloadingIt()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock, 1);
        account.Items["item-0"].IsFlagged = true;
        var failing = true;
        var client = new FakeClient
        {
            Search = (_, _) => Task.FromResult(new SearchResult([], 0, false)),
            Metadata = item => failing ? throw new HttpRequestException("Temporary metadata failure") :
                new(item.UpdatedAt, 1, 0, 1)
        };
        var workspace = await OpenAsync(database, client, clock, account);
        await workspace.CheckForUpdatesAsync(WorkspaceService.FollowUpsQueryId);
        var failed = (await database.Store.LoadAsync(client.User)).Items["item-0"];
        Assert.AreEqual("Temporary metadata failure", failed.MetadataError);
        Assert.IsNull(failed.ActivityError);
        Assert.HasCount(1, failed.Activities);
        Assert.AreEqual(0, client.ActivityCalls);

        failing = false;
        await workspace.PollAsync();
        var recovered = (await database.Store.LoadAsync(client.User)).Items["item-0"];
        Assert.IsNull(recovered.MetadataError);
        Assert.HasCount(1, recovered.Activities);
        Assert.IsTrue(recovered.IsFlagged);
        Assert.Contains("item-0:old", recovered.AcknowledgedIds);
        Assert.AreEqual(2, client.MetadataCalls);
        Assert.AreEqual(0, client.ActivityCalls);
    }

    [TestMethod]
    public async Task IncompleteMembershipDoesNotRestartFullSearchOrReloadHistoryOnEveryPoll()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock);
        foreach (var query in account.Queries)
        {
            query.IsIncomplete = true;
            query.Error = "incomplete search";
            query.LastRefreshedAt = null;
            query.LastFullRefreshedAt = null;
            query.LastFullSearchAttemptAt = clock.Now.AddMinutes(-2);
        }
        var searches = new List<string>();
        var client = new FakeClient
        {
            Search = (expression, _) =>
            {
                searches.Add(expression);
                return Task.FromResult(new SearchResult([], 0, false));
            }
        };
        var workspace = await OpenAsync(database, client, clock, account);
        await workspace.PollAsync();
        clock.Now += TimeSpan.FromMinutes(2);
        await workspace.PollAsync();
        Assert.HasCount(account.Queries.Count * 2, searches);
        Assert.IsTrue(searches.All(expression => expression.Contains("updated:>=")));
        Assert.AreEqual(0, client.ActivityCalls);
        Assert.AreEqual(0, client.MetadataCalls);
        var saved = await database.Store.LoadAsync(client.User);
        Assert.IsTrue(saved.Queries.All(query => query.IsIncomplete && query.Error is not null &&
            query.ItemIds.Count == 2 && query.LastRefreshedAt == clock.Now));
    }

    [TestMethod]
    public async Task FailedMembershipRetriesDoNotScheduleUnchangedDiscussions()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock, 500);
        foreach (var query in account.Queries)
        {
            query.Error = "previous failure";
            query.LastFullRefreshedAt = clock.Now.AddHours(-1);
        }
        var searches = new List<string>();
        var client = new FakeClient
        {
            Search = (expression, _) =>
            {
                searches.Add(expression);
                return Task.FromResult(new SearchResult(
                    account.Items.Values.Select(item => Incoming(item.Id, item.UpdatedAt)).ToArray(), 500, false));
            }
        };
        var workspace = await OpenAsync(database, client, clock, account);
        await workspace.PollAsync();
        clock.Now += TimeSpan.FromMinutes(2);
        await workspace.PollAsync();
        Assert.IsTrue(searches.Take(account.Queries.Count).All(expression => !expression.Contains("updated:>=")));
        Assert.IsTrue(searches.Skip(account.Queries.Count).All(expression => expression.Contains("updated:>=")));
        Assert.AreEqual(0, client.ActivityCalls);
    }

    [TestMethod]
    public async Task ActiveQueryRefreshLeavesOtherSearchesAndUnrelatedPendingItemsUntouched()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock, 3);
        var active = account.Queries[0];
        active.ItemIds = ["item-0"];
        active.TotalCount = 1;
        account.Queries[1].ItemIds = ["item-0", "item-1"];
        account.Items["item-1"].HasBaseline = false;
        account.Items["item-2"].IsFlagged = true;
        account.Items["item-2"].ActivityError = "previous failure";
        var before = account.Queries.Skip(1).Select(query =>
            (query.Id, query.LastRefreshedAt, query.LastFullRefreshedAt, query.TotalCount, string.Join(",", query.ItemIds))).ToArray();
        var searches = new List<string>();
        var hydrated = new List<string>();
        var client = new FakeClient
        {
            Search = (expression, _) =>
            {
                searches.Add(expression);
                return Task.FromResult(new SearchResult([Incoming("item-0", clock.Now)], 1, false));
            },
            Activity = (item, _) =>
            {
                hydrated.Add(item.Id);
                return Task.FromResult<IReadOnlyList<Activity>>(
                    [new("new-comment", ActivityKind.Comment, "other", "other", clock.Now, "new", "")]);
            }
        };
        var workspace = await OpenAsync(database, client, clock, account);
        await workspace.CheckForUpdatesAsync(active.Id);
        Assert.HasCount(1, searches);
        Assert.AreEqual($"({QueryParser.Parse(active.Expression, account.User.Login).Search}) " +
            "updated:>=2026-10-15T21:53:00Z sort:updated-desc", searches[0]);
        CollectionAssert.AreEqual(new[] { "item-0" }, hydrated);
        var saved = await database.Store.LoadAsync(client.User);
        Assert.AreEqual(clock.Now, saved.Queries[0].LastRefreshedAt);
        CollectionAssert.AreEqual(before, saved.Queries.Skip(1).Select(query =>
            (query.Id, query.LastRefreshedAt, query.LastFullRefreshedAt, query.TotalCount, string.Join(",", query.ItemIds))).ToArray());
        Assert.IsFalse(saved.Items["item-1"].HasBaseline);
        Assert.AreEqual("previous failure", saved.Items["item-2"].ActivityError);
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(saved.Items["item-0"], saved.User.Id, []),
            "Overlapping views still share the refreshed item's activity and read state.");
    }

    [TestMethod]
    public async Task FollowUpsRefreshOnlyChecksFlaggedItemsWithoutRunningSavedSearches()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock, 3);
        account.Items["item-0"].IsFlagged = true;
        account.Items["item-1"].FlaggedActivityIds.Add("item-1:old");
        account.Items["item-2"].HasBaseline = false;
        var hydrated = new List<string>();
        var searches = new List<string>();
        var client = new FakeClient
        {
            Search = (expression, _) =>
            {
                searches.Add(expression);
                return Task.FromResult(new SearchResult([], 0, false));
            },
            Activity = (item, _) =>
            {
                hydrated.Add(item.Id);
                return Task.FromResult<IReadOnlyList<Activity>>([]);
            }
        };
        var workspace = await OpenAsync(database, client, clock, account);
        await workspace.CheckForUpdatesAsync(WorkspaceService.FollowUpsQueryId);
        Assert.IsEmpty(searches);
        Assert.AreEqual(2, client.MetadataCalls);
        Assert.IsEmpty(hydrated, "Unchanged flagged items need only a metadata check.");
        var saved = await database.Store.LoadAsync(client.User);
        Assert.IsTrue(saved.Queries.All(query => query.LastRefreshedAt == clock.Now.AddMinutes(-2)));
        Assert.IsFalse(saved.Items["item-2"].HasBaseline);
        Assert.IsTrue(saved.Items["item-0"].IsFlagged);
        Assert.Contains("item-1:old", saved.Items["item-1"].FlaggedActivityIds);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshRequestedDuringAnotherViewRefreshRetainsItsScope(bool pollNext)
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var account = Cached(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searches = new List<string>();
        var client = new FakeClient
        {
            Search = async (expression, ct) =>
            {
                searches.Add(expression);
                if (searches.Count == 1)
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(ct);
                }
                return new SearchResult([], 0, false);
            }
        };
        var workspace = await OpenAsync(database, client, clock, account);
        var first = workspace.CheckForUpdatesAsync(account.Queries[0].Id);
        await entered.Task;
        Assert.AreSame(first, workspace.CheckForUpdatesAsync(account.Queries[0].Id));
        var next = pollNext ? workspace.PollAsync() : workspace.CheckForUpdatesAsync(account.Queries[1].Id);
        release.SetResult();
        await Task.WhenAll(first, next);
        Assert.HasCount(pollNext ? account.Queries.Count + 1 : 2, searches);
        if (!pollNext)
        {
            Assert.StartsWith($"({QueryParser.Parse(account.Queries[1].Expression, account.User.Login).Search}) ", searches[1]);
            Assert.AreEqual(clock.Now.AddMinutes(-2), workspace.Account!.Queries[2].LastRefreshedAt);
        }
        else
            Assert.IsTrue(workspace.Account!.Queries.All(query => query.LastRefreshedAt == clock.Now));
    }

    [TestMethod]
    public async Task MissingActiveQueryReportsAnErrorInsteadOfRefreshingEverything()
    {
        using var database = new TestDatabase();
        var clock = new Clock();
        var client = new FakeClient();
        var workspace = await OpenAsync(database, client, clock, Cached(clock));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.CheckForUpdatesAsync("deleted-query"));
        Assert.AreEqual(0, client.ActivityCalls);
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.LastRefreshedAt == clock.Now.AddMinutes(-2)));
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
        Assert.HasCount(workspace.Account!.Queries.Count, expressions);
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
            Metadata = item => new(item.UpdatedAt, 0, 1, 1),
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
        Assert.IsEmpty(hydrated, "Unchanged flagged items must not reload their discussions.");
        Assert.IsGreaterThanOrEqualTo(2, client.MetadataCalls,
            "Flagged items inside and outside saved queries still receive lightweight metadata checks.");
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
        var queryCount = workspace.Account!.Queries.Count;
        Assert.HasCount(queryCount * 2, expressions);
        Assert.IsTrue(expressions.Take(queryCount).All(expression => expression.Contains("updated:>=")));
        Assert.IsTrue(expressions.Skip(queryCount).All(expression => !expression.Contains("updated:>=")));
        Assert.AreEqual(0, client.ActivityCalls, "Full membership reconciliation must not reload unchanged discussion.");
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
