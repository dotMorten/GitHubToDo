using GitHubToDo.Core;
using Microsoft.Data.Sqlite;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class PersistenceAndWorkspaceTests
{
    private static async Task<AccountSnapshot> CachedQueriesAsync(TestDatabase database)
    {
        var account = new AccountSnapshot
        {
            User = new("1", "viewer"),
            Queries =
            [
                new() { Id = "first", Name = "First", Expression = "is:issue", ItemIds = ["first", "shared"] },
                new() { Id = "active", Name = "Active", Expression = "is:pr", ItemIds = ["active", "shared"] },
                new() { Id = "last", Name = "Last", Expression = "is:open", ItemIds = ["last"] }
            ],
            IgnoredAccounts = [new("ignored", "ignored-id")]
        };
        foreach (var id in new[] { "first", "active", "last", "shared", "orphan" })
        {
            var item = FakeClient.NewItem();
            item.Id = id;
            item.IsFlagged = id == "orphan";
            item.AcknowledgedIds.Add($"{id}:read");
            account.Items.Add(id, item);
        }
        await database.Store.SaveAsync(account);
        return account;
    }

    [TestMethod]
    public async Task ReopeningCachedAccountsDoesNotRewriteStoredData()
    {
        using var database = new TestDatabase();
        var original = await CachedQueriesAsync(database);
        await using var connection = new SqliteConnection($"Data Source={database.PathName}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER RejectAccountSave BEFORE INSERT ON Accounts
            BEGIN SELECT RAISE(ABORT,'An existing account must not be saved during load'); END;
            """;
        await command.ExecuteNonQueryAsync();
        var workspace = new WorkspaceService(database.Store, new FakeClient());

        await workspace.OpenAsync(original.User);

        Assert.HasCount(3, workspace.Account!.Queries);
        Assert.HasCount(5, workspace.Account.Items);
        Assert.IsTrue(workspace.Account.Items["orphan"].IsFlagged);
    }

    [TestMethod]
    public async Task OpeningNewAccountsPersistsTheirInitialQueryIds()
    {
        using var database = new TestDatabase();
        var user = new GitHubUser("new", "new");
        var workspace = new WorkspaceService(database.Store, new FakeClient());

        await workspace.OpenAsync(user);
        var originalIds = workspace.Account!.Queries.Select(query => query.Id).ToArray();
        await workspace.OpenAsync(user);

        CollectionAssert.AreEqual(originalIds, workspace.Account!.Queries.Select(query => query.Id).ToArray());
    }

    [TestMethod]
    public async Task CachedQueriesPublishAllHeadersThenLoadActiveFirstWithCompleteQueryItems()
    {
        using var database = new TestDatabase();
        var original = await CachedQueriesAsync(database);
        var snapshots = new List<AccountSnapshot>();
        await foreach (var snapshot in database.Store.LoadQueriesAsync(original.User, "active"))
            snapshots.Add(snapshot);

        Assert.HasCount(5, snapshots);
        Assert.IsEmpty(snapshots[0].Items);
        Assert.IsEmpty(snapshots[0].LoadedQueryIds);
        CollectionAssert.AreEqual(new[] { "active" }, snapshots[1].LoadedQueryIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { "active", "shared" }, snapshots[1].Items.Keys.ToArray());
        CollectionAssert.AreEquivalent(new[] { "first", "active" }, snapshots[2].LoadedQueryIds.ToArray());
        Assert.AreSame(snapshots[1].Items["shared"], snapshots[2].Items["shared"]);
        Assert.IsTrue(snapshots.Take(4).All(snapshot => snapshot.IsLoading));
        Assert.IsFalse(snapshots[^1].IsLoading);
        foreach (var snapshot in snapshots)
        {
            CollectionAssert.AreEqual(original.Queries.Select(query => query.Id).ToArray(),
                snapshot.Queries.Select(query => query.Id).ToArray());
            Assert.IsTrue(snapshot.Queries.Where(query => snapshot.LoadedQueryIds.Contains(query.Id))
                .All(query => query.ItemIds.All(snapshot.Items.ContainsKey)));
        }
        Assert.HasCount(2, snapshots[1].Items, "Later loads must not modify an already published dictionary.");
        Assert.HasCount(1, snapshots[1].LoadedQueryIds);
        CollectionAssert.AreEqual(original.Queries.Select(query => query.Id).ToArray(),
            snapshots[^1].Queries.Select(query => query.Id).ToArray());
        Assert.HasCount(original.Items.Count, snapshots[^1].Items);
        Assert.IsTrue(snapshots[^1].Items["orphan"].IsFlagged);
        Assert.Contains("shared:read", snapshots[^1].Items["shared"].AcknowledgedIds);
        Assert.AreEqual("ignored-id", snapshots[0].IgnoredAccounts.Single().Id);
    }

    [TestMethod]
    public async Task QueryHeadersArePublishedBeforeAnyCachedItemIsDeserialized()
    {
        using var database = new TestDatabase();
        var original = await CachedQueriesAsync(database);
        await using var connection = new SqliteConnection($"Data Source={database.PathName}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Items SET Payload='invalid JSON' WHERE Id='first'";
        await command.ExecuteNonQueryAsync();
        await using var loader = database.Store.LoadQueriesAsync(original.User).GetAsyncEnumerator();

        Assert.IsTrue(await loader.MoveNextAsync());
        Assert.HasCount(3, loader.Current.Queries);
        Assert.IsEmpty(loader.Current.Items);
        Assert.IsTrue(loader.Current.IsLoading);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(async () => await loader.MoveNextAsync());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CancellingProgressiveLoadReleasesStorageWithoutChangingSavedQueries(int publications)
    {
        using var database = new TestDatabase();
        var original = await CachedQueriesAsync(database);
        using var cancellation = new CancellationTokenSource();
        await using (var loader = database.Store.LoadQueriesAsync(original.User,
            cancellationToken: cancellation.Token).GetAsyncEnumerator())
        {
            for (var index = 0; index < publications; index++)
                Assert.IsTrue(await loader.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await loader.MoveNextAsync());
        }
        var restored = await database.Store.LoadAsync(original.User);
        Assert.HasCount(3, restored.Queries);
        Assert.HasCount(5, restored.Items);
    }

    [TestMethod]
    public async Task LaterCorruptQueryDoesNotPreventFirstQueryLoadingOrPersistPartialState()
    {
        using var database = new TestDatabase();
        var original = await CachedQueriesAsync(database);
        await using var connection = new SqliteConnection($"Data Source={database.PathName}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Items SET Payload='invalid JSON' WHERE Id='last'";
        await command.ExecuteNonQueryAsync();
        var workspace = new WorkspaceService(database.Store, new FakeClient());
        var published = new List<AccountSnapshot>();
        workspace.Changed += () =>
        {
            if (workspace.Account is { } account) published.Add(account);
        };

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => workspace.OpenAsync(original.User));

        Assert.IsEmpty(published[0].Items);
        CollectionAssert.AreEqual(new[] { "first" }, published[1].LoadedQueryIds.ToArray());
        Assert.HasCount(2, published[^1].LoadedQueryIds);
        Assert.IsNull(workspace.Account, "A failed load must not leave a partial snapshot available for saving.");
        command.CommandText = "SELECT COUNT(*) FROM SavedQueries";
        Assert.AreEqual(3L, (long)(await command.ExecuteScalarAsync())!);
    }

    [TestMethod]
    public async Task ClosingDuringProgressiveLoadDoesNotRepublishCachedAccount()
    {
        using var database = new TestDatabase();
        var original = await CachedQueriesAsync(database);
        var workspace = new WorkspaceService(database.Store, new FakeClient());
        var published = 0;
        workspace.Changed += () =>
        {
            if (workspace.Account is null) return;
            published++;
            workspace.Close();
        };

        await workspace.OpenAsync(original.User);

        Assert.AreEqual(1, published);
        Assert.IsNull(workspace.Account);
        Assert.HasCount(3, (await database.Store.LoadAsync(original.User)).Queries);
    }

    [TestMethod]
    public async Task CancelledAccountSwitchDoesNotInterruptTheCurrentProgressiveLoad()
    {
        using var database = new TestDatabase();
        var original = await CachedQueriesAsync(database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var workspace = new WorkspaceService(database.Store, new FakeClient());
        Task? replacement = null;
        workspace.Changed += () =>
        {
            if (workspace.Account is { IsLoading: true } && replacement is null)
                replacement = workspace.OpenAsync(new("other", "other"), cancellation.Token);
        };

        await workspace.OpenAsync(original.User);
        Assert.IsNotNull(replacement);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await replacement);

        Assert.AreEqual(original.User, workspace.Account!.User);
        Assert.IsFalse(workspace.Account.IsLoading);
        Assert.HasCount(3, workspace.Account.Queries);
        Assert.HasCount(5, workspace.Account.Items);
    }

    [TestMethod]
    public async Task MutationsDuringProgressiveLoadWaitForTheCompleteAccount()
    {
        using var database = new TestDatabase();
        var original = await CachedQueriesAsync(database);
        var workspace = new WorkspaceService(database.Store, new FakeClient()) { PreferredQueryId = "active" };
        Task? mutation = null;
        workspace.Changed += () =>
        {
            if (workspace.Account is { IsLoading: true } && mutation is null)
                mutation = workspace.SaveQueryAsync(null, "Added while loading", "is:issue");
        };

        await workspace.OpenAsync(original.User);
        Assert.IsNotNull(mutation);
        await mutation;

        var restored = await database.Store.LoadAsync(original.User);
        Assert.HasCount(4, restored.Queries);
        Assert.HasCount(5, restored.Items);
        Assert.AreEqual("Added while loading", restored.Queries[^1].Name);
    }

    private static async Task<WorkspaceService> BulkWorkspaceAsync(TestDatabase database)
    {
        var account = new AccountSnapshot { User = new("1", "viewer") };
        foreach (var id in new[] { "first", "second" })
        {
            var item = FakeClient.NewItem();
            item.Id = id;
            ReadStatePolicy.ApplyActivity(item,
                [new($"{id}:old", ActivityKind.Comment, "other", "other", item.CreatedAt, "observed", "")], item.CreatedAt);
            account.Items.Add(id, item);
            foreach (var query in account.Queries) query.ItemIds.Add(id);
        }
        await database.Store.SaveAsync(account);
        var workspace = new WorkspaceService(database.Store, new FakeClient());
        await workspace.OpenAsync(account.User);
        return workspace;
    }

    [TestMethod]
    public async Task AssignedItemsSearchIsSeededOnlyForNewAccounts()
    {
        using var database = new TestDatabase();
        var user = new GitHubUser("1", "viewer");
        var account = await database.Store.LoadAsync(user);
        var assigned = account.Queries.Single(query => query.Name == "Items assigned to me");
        Assert.AreEqual("assignee:@me state:open archived:false", assigned.Expression);
        account.Queries.Remove(assigned);
        await database.Store.SaveAsync(account);

        var reopened = await database.Store.LoadAsync(user);
        CollectionAssert.AreEqual(account.Queries.Select(query => query.Id).ToArray(),
            reopened.Queries.Select(query => query.Id).ToArray());
        Assert.IsFalse(reopened.Queries.Any(query => query.Name == "Items assigned to me"));
    }

    [TestMethod]
    public async Task BulkReadCommitsEverySelectedItemOnceAndSurvivesRestart()
    {
        using var database = new TestDatabase();
        var workspace = await BulkWorkspaceAsync(database);
        var notifications = 0;
        workspace.Changed += () => notifications++;
        await workspace.MarkReadAsync([new("first", ["first:old"]), new("second", ["second:old"])]);
        Assert.AreEqual(1, notifications);
        var restored = await database.Store.LoadAsync(workspace.Account!.User);
        foreach (var item in restored.Items.Values)
            Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, restored.User.Id, []));
        Assert.IsTrue(restored.Queries.All(query => query.ItemIds.Count == 2));
    }

    [TestMethod]
    public async Task BulkReadDoesNotAcknowledgeLateOrEqualTimestampActivityOnEitherItem()
    {
        using var database = new TestDatabase();
        var workspace = await BulkWorkspaceAsync(database);
        var snapshots = workspace.Account!.Items.Values.Select(item =>
            new ItemReadSnapshot(item.Id, item.Activities.Select(activity => activity.Id).ToArray())).ToArray();
        var updated = await database.Store.LoadAsync(workspace.Account.User);
        foreach (var item in updated.Items.Values)
            item.Activities.Add(new($"{item.Id}:late", ActivityKind.ReviewComment, "other", "other",
                item.CreatedAt, "discovered after the command snapshot", ""));
        await database.Store.SaveAsync(updated);
        await workspace.OpenAsync(updated.User);
        await workspace.MarkReadAsync(snapshots);
        foreach (var item in workspace.Account!.Items.Values)
        {
            Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, updated.User.Id, []));
            CollectionAssert.AreEqual(new[] { $"{item.Id}:late" },
                ReadStatePolicy.UnreadActivity(item, updated.User.Id, []).Select(activity => activity.Id).ToArray());
        }
    }

    [TestMethod]
    public async Task BulkReadRollsBackAllItemsWhenSavingTheSecondItemFails()
    {
        using var database = new TestDatabase();
        var workspace = await BulkWorkspaceAsync(database);
        await using var connection = new SqliteConnection($"Data Source={database.PathName}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailSecond BEFORE INSERT ON Items WHEN NEW.Id='second' BEGIN SELECT RAISE(ABORT,'simulated disk error'); END;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() =>
            workspace.MarkReadAsync([new("first", ["first:old"]), new("second", ["second:old"])]));
        var restored = await database.Store.LoadAsync(workspace.Account!.User);
        Assert.IsTrue(workspace.Account.Items.Values.All(item => item.AcknowledgedIds.Count == 0));
        Assert.IsTrue(restored.Items.Values.All(item => item.AcknowledgedIds.Count == 0));
    }

    [TestMethod]
    public async Task BulkReadRejectsUnknownOrPendingItemsWithoutPartialAcknowledgements()
    {
        using var database = new TestDatabase();
        var workspace = await BulkWorkspaceAsync(database);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            workspace.MarkReadAsync([new("first", ["first:old"]), new("missing", [])]));
        workspace.Account!.Items["second"].HasBaseline = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workspace.MarkReadAsync([new("first", ["first:old"]), new("second", ["second:old"])]));
        Assert.IsTrue(workspace.Account.Items.Values.All(item => item.AcknowledgedIds.Count == 0));
        var restored = await database.Store.LoadAsync(workspace.Account.User);
        Assert.IsTrue(restored.Items.Values.All(item => item.AcknowledgedIds.Count == 0));
    }

    [TestMethod]
    public async Task ReadAcknowledgementsAndQueryOrderSurviveRestart()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        Assert.AreEqual(1, client.ActivityCalls, "A shared item should hydrate once per cycle.");
        var item = workspace.Account!.Items["item"];
        await workspace.MarkReadAsync(item.Id, item.Activities.Select(activity => activity.Id));
        await workspace.MoveQueryAsync(workspace.Account.Queries[2].Id, -2);
        var reopened = new WorkspaceService(new LocalStore(database.PathName), client);
        await reopened.OpenAsync(client.User);
        Assert.AreEqual("Review requested", reopened.Account!.Queries[0].Name);
        foreach (var query in reopened.Account.Queries)
        {
            Assert.AreEqual("item", query.ItemIds.Single());
            Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(reopened.Account.Items["item"], client.User.Id, []));
        }
    }

    [TestMethod]
    public async Task SnapshotAcknowledgementDoesNotReadConcurrentNewActivity()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        var snapshot = workspace.Account!.Items["item"].Activities.Select(activity => activity.Id).ToArray();
        client.Metadata = item => new(item.UpdatedAt, 2, 0, 2);
        client.Activity = (_, _) => Task.FromResult<IReadOnlyList<Activity>>(
        [
            new("comment:1", ActivityKind.Comment, "other", "other", DateTimeOffset.UtcNow.AddHours(-1), "old", ""),
            new("comment:2", ActivityKind.Comment, "other", "other", DateTimeOffset.UtcNow, "new", "")
        ]);
        await workspace.RefreshAsync();
        await workspace.MarkReadAsync("item", snapshot);
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(workspace.Account.Items["item"], "1", []));
    }

    [TestMethod]
    public async Task QueryRemovalAndReentryPreserveAcknowledgements()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        await workspace.MarkReadAsync("item", ["comment:1"]);
        foreach (var id in workspace.Account!.Queries.Select(query => query.Id).ToArray())
            await workspace.DeleteQueryAsync(id);
        await workspace.SaveQueryAsync(null, "Custom", "is:issue");
        await workspace.RefreshAsync();
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account.Items["item"], "1", []));
    }

    [TestMethod]
    public async Task DeletedAllQueriesStayDeletedAndAccountsAreIsolated()
    {
        using var database = new TestDatabase();
        var account = new AccountSnapshot { User = new("1", "first"), Queries = [] };
        account.Items.Add("private", FakeClient.NewItem());
        await database.Store.SaveAsync(account);
        Assert.IsEmpty((await database.Store.LoadAsync(account.User)).Queries);
        var other = await database.Store.LoadAsync(new("2", "second"));
        Assert.HasCount(SavedQuery.Defaults().Count, other.Queries);
        Assert.IsEmpty(other.Items);
        Assert.IsEmpty(other.IgnoredAccounts);
    }

    [TestMethod]
    public async Task StorageFailuresDoNotPublishFalseAcknowledgements()
    {
        using var database = new TestDatabase();
        var workspace = new WorkspaceService(database.Store, new FakeClient());
        await workspace.OpenAsync(new("1", "viewer"));
        await workspace.RefreshAsync();
        await using var connection = new SqliteConnection($"Data Source={database.PathName}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailWrites BEFORE INSERT ON Items BEGIN SELECT RAISE(ABORT,'simulated disk error'); END;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => workspace.MarkReadAsync("item", ["comment:1"]));
        Assert.IsEmpty(workspace.Account!.Items["item"].AcknowledgedIds);
        var restored = await database.Store.LoadAsync(new("1", "viewer"));
        Assert.IsEmpty(restored.Items["item"].AcknowledgedIds);
    }

    [TestMethod]
    public async Task FailedSearchPreservesOldMembershipAndFlagsStale()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        client.Search = (_, _) => throw new GitHubException("invalid query", 422);
        await workspace.RefreshAsync();
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.ItemIds.Contains("item") && query.Error == "invalid query"));
    }

    [TestMethod]
    public async Task PartialSearchRetainsMembershipAndExplainsCoverage()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        client.Search = (_, _) => Task.FromResult(new SearchResult([], 100, true));
        await workspace.RefreshAsync();
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.ItemIds.Contains("item") && query.IsIncomplete && query.Error is not null));
    }

    [TestMethod]
    public async Task IgnoredChangesImmediatelyRecomputeAllQueries()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        await workspace.SetIgnoredAsync(["OTHER"]);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account!.Items["item"], "1", workspace.Account.IgnoredAccounts));
        await workspace.SetIgnoredAsync([]);
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(workspace.Account.Items["item"], "1", []));
    }

    [TestMethod]
    public async Task RefreshesCoalesceAndCancellationDoesNotEraseState()
    {
        using var database = new TestDatabase();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            Search = async (_, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new([], 0, false);
            }
        };
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        using var cancellation = new CancellationTokenSource();
        var first = workspace.RefreshAsync(cancellation.Token);
        await started.Task;
        Assert.AreSame(first, workspace.RefreshAsync(cancellation.Token));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
        Assert.HasCount(SavedQuery.Defaults().Count, workspace.Account!.Queries);
    }

    [TestMethod]
    public async Task ActivityChecksRunWhenSearchUpdatedAtDoesNotChange()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        await workspace.MarkReadAsync("item", ["comment:1"]);
        client.Metadata = item => new(item.UpdatedAt, 0, 1, 1);
        client.Activity = (_, _) => Task.FromResult<IReadOnlyList<Activity>>(
            [new("review:2", ActivityKind.Review, "other", "other", DateTimeOffset.UtcNow, "APPROVED", "")]);
        await workspace.RefreshAsync();
        Assert.AreEqual(2, client.ActivityCalls);
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(workspace.Account!.Items["item"], "1", []));
    }

    [TestMethod]
    public async Task RejectedTokenEscapesRefreshForSignInRepair()
    {
        using var database = new TestDatabase();
        var client = new FakeClient { Search = (_, _) => throw new GitHubException("Sign in again.", 401) };
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await Assert.ThrowsAsync<GitHubException>(() => workspace.RefreshAsync());
        Assert.Contains("Sign in again", workspace.Progress);
    }

    [TestMethod]
    public async Task OldBatchCannotRevokeOrOverwriteAReplacementAccount()
    {
        using var database = new TestDatabase();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            Search = async (_, _) =>
            {
                started.TrySetResult();
                await finish.Task;
                throw new GitHubException("Old account token revoked", 401);
            }
        };
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        var pending = workspace.RefreshAsync();
        await started.Task;
        await workspace.OpenAsync(new("replacement", "replacement"));
        finish.SetResult();
        await pending;
        Assert.AreEqual("replacement", workspace.Account!.User.Id);
        Assert.IsTrue(workspace.Account.Queries.All(query => query.Error is null && query.ItemIds.Count == 0));
        Assert.DoesNotContain("revoked", workspace.Progress);
    }

    [TestMethod]
    public async Task ReadingInitializedStorageDoesNotWriteToTheDatabase()
    {
        using var database = new TestDatabase();
        var original = await CachedQueriesAsync(database);
        await database.Store.SetSettingAsync("sample", "value");
        await using var connection = new SqliteConnection($"Data Source={database.PathName}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        var before = (long)(await command.ExecuteScalarAsync())!;

        Assert.AreEqual("value", await database.Store.GetSettingAsync("sample"));
        Assert.IsNull(await database.Store.GetSettingAsync("missing"));
        var restored = await database.Store.LoadAsync(original.User);

        Assert.HasCount(original.Items.Count, restored.Items);
        Assert.AreEqual(before, (long)(await command.ExecuteScalarAsync())!,
            "Reading an initialized store must not rewrite its schema version or other data.");
    }

    [TestMethod]
    public async Task FutureSchemaIsRejectedRatherThanDowngraded()
    {
        using var database = new TestDatabase();
        await database.Store.SetSettingAsync("sample", "value");
        await using var connection = new SqliteConnection($"Data Source={database.PathName}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=3";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => database.Store.GetSettingAsync("sample"));
    }

    [TestMethod]
    public async Task CommentAcknowledgementsPersistIndividuallyWithoutClearingFollowUps()
    {
        using var database = new TestDatabase();
        var client = new FakeClient
        {
            Activity = (_, _) => Task.FromResult<IReadOnlyList<Activity>>(
            [
                new("first", ActivityKind.Comment, "other", "other", DateTimeOffset.UtcNow.AddMinutes(-1), "first", ""),
                new("second", ActivityKind.ReviewComment, "other", "other", DateTimeOffset.UtcNow, "second", "")
            ])
        };
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        await workspace.SetFollowUpAsync("item", null, true);
        await workspace.SetFollowUpAsync("item", "first", true);
        await workspace.MarkReadAsync("item", ["first"]);
        var restored = await database.Store.LoadAsync(client.User);
        Assert.IsTrue(restored.Items["item"].IsFlagged);
        Assert.Contains("first", restored.Items["item"].FlaggedActivityIds);
        CollectionAssert.AreEqual(new[] { "second" },
            ReadStatePolicy.UnreadActivity(restored.Items["item"], client.User.Id, []).Select(activity => activity.Id).ToArray());
        await workspace.MarkReadAsync("item", ["second"]);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account!.Items["item"], client.User.Id, []));
        Assert.IsTrue(workspace.Account.Items["item"].HasFollowUps);
        await workspace.ClearFollowUpsAsync("item");
        Assert.IsFalse(workspace.Account.Items["item"].HasFollowUps);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account.Items["item"], client.User.Id, []));
    }

    [TestMethod]
    public async Task FollowUpsSurviveSearchReplacementAndKeepOutOfQueryItemsMonitored()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        await workspace.SetFollowUpAsync("item", null, true);
        await workspace.SetFollowUpAsync("item", "comment:1", true);
        await workspace.RefreshAsync();
        Assert.IsTrue(workspace.Account!.Items["item"].IsFlagged);
        Assert.Contains("comment:1", workspace.Account.Items["item"].FlaggedActivityIds);
        foreach (var id in workspace.Account.Queries.Select(query => query.Id).ToArray())
            await workspace.DeleteQueryAsync(id);
        var before = client.ActivityCalls;
        var metadataBefore = client.MetadataCalls;
        await workspace.RefreshAsync();
        Assert.AreEqual(before, client.ActivityCalls);
        Assert.AreEqual(metadataBefore + 1, client.MetadataCalls);
        var reopened = new WorkspaceService(database.Store, client);
        await reopened.OpenAsync(client.User);
        Assert.IsTrue(reopened.Account!.Items["item"].HasFollowUps);
        Assert.IsEmpty(reopened.Account.Queries);
        await reopened.SetFollowUpAsync("item", null, false);
        Assert.IsTrue(reopened.Account.Items["item"].HasFollowUps, "Clearing the item flag must retain comment flags.");
        await reopened.SetFollowUpAsync("item", "comment:1", false);
        Assert.IsFalse(reopened.Account.Items["item"].HasFollowUps);
    }

    [TestMethod]
    public async Task FollowUpFailuresAreAtomicAndInvalidCommentsCannotBeFlagged()
    {
        using var database = new TestDatabase();
        var workspace = await BulkWorkspaceAsync(database);
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.SetFollowUpAsync("first", "missing", true));
        await using var connection = new SqliteConnection($"Data Source={database.PathName}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailFlag BEFORE INSERT ON Items BEGIN SELECT RAISE(ABORT,'simulated disk error'); END;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => workspace.SetFollowUpAsync("first", "first:old", true));
        Assert.IsFalse(workspace.Account!.Items["first"].HasFollowUps);
        var restored = await database.Store.LoadAsync(workspace.Account.User);
        Assert.IsFalse(restored.Items["first"].HasFollowUps);
        Assert.IsEmpty(restored.Items["first"].AcknowledgedIds);
    }

    [TestMethod]
    public async Task FollowUpsAreAccountIsolatedAndInitialMarkersCannotBeFlaggedAsComments()
    {
        using var database = new TestDatabase();
        var workspace = await BulkWorkspaceAsync(database);
        await workspace.SetFollowUpAsync("first", null, true);
        await workspace.SetFollowUpAsync("first", "first:old", true);
        await workspace.OpenAsync(new("other-account", "other"));
        Assert.IsEmpty(workspace.Account!.Items);
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.SetFollowUpAsync("first", null, false));
        var original = await database.Store.LoadAsync(new("1", "viewer"));
        Assert.IsTrue(original.Items["first"].IsFlagged);
        Assert.Contains("first:old", original.Items["first"].FlaggedActivityIds);
        var item = original.Items["second"];
        item.HasBaseline = false;
        item.Activities.Clear();
        ReadStatePolicy.ApplyActivity(item, [], DateTimeOffset.UtcNow);
        await database.Store.SaveAsync(original);
        await workspace.OpenAsync(original.User);
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.SetFollowUpAsync("second", item.Activities.Single().Id, true));
    }

    [TestMethod]
    public async Task VersionOneDataMigratesWithEmptyFollowUpsAndExistingAcknowledgements()
    {
        using var database = new TestDatabase();
        var workspace = await BulkWorkspaceAsync(database);
        await workspace.MarkReadAsync("first", ["first:old"]);
        await using var connection = new SqliteConnection($"Data Source={database.PathName}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Items SET Payload=json_remove(Payload,'$.IsFlagged','$.FlaggedActivityIds'); PRAGMA user_version=1;";
        await command.ExecuteNonQueryAsync();
        var restored = await database.Store.LoadAsync(new("1", "viewer"));
        Assert.IsTrue(restored.Items.Values.All(item => !item.HasFollowUps));
        Assert.Contains("first:old", restored.Items["first"].AcknowledgedIds);
        command.CommandText = "PRAGMA user_version";
        Assert.AreEqual(2L, (long)(await command.ExecuteScalarAsync())!);
    }
}
