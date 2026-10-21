using GitHubToDo.Core;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class ColumnMetadataTests
{
    [TestMethod]
    public async Task OptionalColumnsAreLazyScopedCachedAndPersistedWithoutChangingReadState()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var item = FakeClient.NewItem();
        item.HasBaseline = true;
        item.AcknowledgedIds.Add("comment:1");
        item.IsFlagged = true;
        var other = FakeClient.NewItem();
        other.Id = "other";
        var account = new AccountSnapshot
        {
            User = client.User,
            Items = new() { [item.Id] = item, [other.Id] = other },
            Queries = [new() { Id = "active", ItemIds = [item.Id] }, new() { Id = "other", ItemIds = [other.Id] }]
        };
        await database.Store.SaveAsync(account);
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(account.User);
        await workspace.RefreshColumnsAsync("active");
        Assert.IsEmpty(client.ColumnBatches);

        workspace.ColumnMetadataEnabled = true;
        await workspace.RefreshColumnsAsync("active");
        CollectionAssert.AreEqual(new[] { item.Id }, client.ColumnBatches.Single());
        var saved = await database.Store.LoadAsync(account.User);
        Assert.AreEqual("Bug", saved.Items[item.Id].ColumnMetadata!.IssueType);
        Assert.IsNull(saved.Items[other.Id].ColumnMetadata);
        Assert.Contains("comment:1", saved.Items[item.Id].AcknowledgedIds);
        Assert.IsTrue(saved.Items[item.Id].IsFlagged);

        await workspace.RefreshColumnsAsync("active");
        Assert.HasCount(1, client.ColumnBatches, "Opening a menu or changing queries must not refetch fresh metadata.");
        workspace.IncludeProjectFields = true;
        await workspace.RefreshColumnsAsync("active");
        Assert.HasCount(2, client.ColumnBatches, "Enabling project fields must load missing project data.");
        Assert.IsTrue(workspace.Account!.Items[item.Id].ColumnMetadata!.IncludesProjects);
        await workspace.RefreshColumnsAsync("active", force: true);
        Assert.HasCount(3, client.ColumnBatches, "Manual refresh must refresh even recently cached fields.");
    }

    [TestMethod]
    public async Task ColumnErrorsKeepCachedValuesAndDoNotBecomeSuccessfulEmptyMetadata()
    {
        using var database = new TestDatabase();
        var client = new FakeClient { Columns = (_, _) => new(null, new GitHubException("Column access denied", 403)) };
        var item = FakeClient.NewItem();
        item.IsFlagged = true;
        item.ColumnMetadata = new(["cached"], "Bug", "v1", [], false);
        await database.Store.SaveAsync(new AccountSnapshot { User = client.User, Items = new() { [item.Id] = item }, Queries = [] });
        var workspace = new WorkspaceService(database.Store, client) { ColumnMetadataEnabled = true };
        await workspace.OpenAsync(client.User);
        Assert.AreEqual(1, await workspace.RefreshColumnsAsync(WorkspaceService.FollowUpsQueryId));
        var saved = await database.Store.LoadAsync(client.User);
        Assert.AreEqual("cached", saved.Items[item.Id].ColumnMetadata!.Labels.Single());
        Assert.AreEqual("Column access denied", saved.Items[item.Id].ColumnsError);
    }

    [TestMethod]
    public async Task SearchRefreshDoesNotDiscardPreviouslyFetchedColumns()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var item = FakeClient.NewItem();
        item.ColumnMetadata = new(["cached"], "Bug", "v1", [new("p", "Plan", "P1", "3")], true);
        await database.Store.SaveAsync(new AccountSnapshot { User = client.User, Items = new() { [item.Id] = item } });
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        Assert.AreEqual("cached", workspace.Account!.Items[item.Id].ColumnMetadata!.Labels.Single());
        Assert.AreEqual("P1", workspace.Account.Items[item.Id].ColumnMetadata!.Projects.Single().Priority);
        Assert.IsEmpty(client.ColumnBatches);
    }

    [TestMethod]
    public async Task RateLimitStopsRemainingColumnBatches()
    {
        using var database = new TestDatabase();
        var client = new FakeClient { Columns = (_, _) => new(null, new GitHubException("Try again later", 429)) };
        var items = Enumerable.Range(0, GitHubBatching.ItemsPerRequest + 1).Select(index =>
        {
            var item = FakeClient.NewItem();
            item.Id = $"item-{index}";
            return item;
        }).ToArray();
        await database.Store.SaveAsync(new AccountSnapshot
        {
            User = client.User,
            Items = items.ToDictionary(item => item.Id),
            Queries = [new() { Id = "active", ItemIds = items.Select(item => item.Id).ToList() }]
        });
        var workspace = new WorkspaceService(database.Store, client) { ColumnMetadataEnabled = true };
        await workspace.OpenAsync(client.User);
        Assert.AreEqual(GitHubBatching.ItemsPerRequest, await workspace.RefreshColumnsAsync("active"));
        Assert.HasCount(1, client.ColumnBatches);
        Assert.Contains("Refresh paused: Try again later", workspace.Progress);
    }

    [TestMethod]
    public async Task ColumnRefreshFinishesItsProgressMessage()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var item = FakeClient.NewItem();
        await database.Store.SaveAsync(new AccountSnapshot
        {
            User = client.User, Items = new() { [item.Id] = item },
            Queries = [new() { Id = "active", ItemIds = [item.Id] }]
        });
        var workspace = new WorkspaceService(database.Store, client) { ColumnMetadataEnabled = true };
        await workspace.OpenAsync(client.User);
        await workspace.RefreshColumnsAsync("active");
        Assert.StartsWith("Columns checked at ", workspace.Progress);
    }

    [TestMethod]
    public async Task OneDeniedBatchDoesNotLeaveLaterItemsWithoutMetadata()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var items = Enumerable.Range(0, GitHubBatching.ItemsPerRequest + 1).Select(index =>
        {
            var item = FakeClient.NewItem();
            item.Id = $"item-{index}";
            return item;
        }).ToArray();
        client.Columns = (item, _) => item.Id == items[^1].Id
            ? new(new(["bug"], null, null, [], false), null)
            : new(null, new GitHubException("Organization access denied", 403));
        await database.Store.SaveAsync(new AccountSnapshot
        {
            User = client.User, Items = items.ToDictionary(item => item.Id),
            Queries = [new() { Id = "active", ItemIds = items.Select(item => item.Id).ToList() }]
        });
        var workspace = new WorkspaceService(database.Store, client) { ColumnMetadataEnabled = true };
        await workspace.OpenAsync(client.User);
        Assert.AreEqual(GitHubBatching.ItemsPerRequest, await workspace.RefreshColumnsAsync("active"));
        Assert.HasCount(2, client.ColumnBatches);
        Assert.AreEqual("bug", workspace.Account!.Items[items[^1].Id].ColumnMetadata!.Labels.Single());
    }

    [TestMethod]
    public async Task FailedInitialColumnsCanRetryImmediatelyWhenShownAgain()
    {
        using var database = new TestDatabase();
        var client = new FakeClient { Columns = (_, _) => new(null, new GitHubException("Organization access denied", 403)) };
        var item = FakeClient.NewItem();
        await database.Store.SaveAsync(new AccountSnapshot
        {
            User = client.User, Items = new() { [item.Id] = item },
            Queries = [new() { Id = "active", ItemIds = [item.Id] }]
        });
        var workspace = new WorkspaceService(database.Store, client) { ColumnMetadataEnabled = true };
        await workspace.OpenAsync(client.User);
        await workspace.RefreshColumnsAsync("active");
        workspace.ColumnMetadataEnabled = false;
        client.Columns = null;
        workspace.ColumnMetadataEnabled = true;
        await workspace.RefreshColumnsAsync("active");
        Assert.HasCount(2, client.ColumnBatches);
        Assert.AreEqual("bug", workspace.Account!.Items[item.Id].ColumnMetadata!.Labels.Single());
        Assert.IsNull(workspace.Account.Items[item.Id].ColumnsError);
    }

    [TestMethod]
    public async Task HidingColumnsDiscardsALateFailureWithoutPublishingErrorState()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var item = FakeClient.NewItem();
        await database.Store.SaveAsync(new AccountSnapshot
        {
            User = client.User, Items = new() { [item.Id] = item },
            Queries = [new() { Id = "active", ItemIds = [item.Id] }]
        });
        var workspace = new WorkspaceService(database.Store, client) { ColumnMetadataEnabled = true };
        await workspace.OpenAsync(client.User);
        client.Columns = (_, _) =>
        {
            workspace.ColumnMetadataEnabled = false;
            return new(null, new GitHubException("Late access denial", 403));
        };
        Assert.AreEqual(0, await workspace.RefreshColumnsAsync("active"));
        var saved = await database.Store.LoadAsync(client.User);
        Assert.IsNull(saved.Items[item.Id].ColumnsError);
        Assert.IsNull(saved.Items[item.Id].ColumnsCheckedAt);
        Assert.DoesNotContain("error", workspace.Progress, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task CancelledColumnLoadDoesNotPersistAnError()
    {
        using var database = new TestDatabase();
        using var cancellation = new CancellationTokenSource();
        var client = new FakeClient();
        var item = FakeClient.NewItem();
        await database.Store.SaveAsync(new AccountSnapshot
        {
            User = client.User, Items = new() { [item.Id] = item },
            Queries = [new() { Id = "active", ItemIds = [item.Id] }]
        });
        var workspace = new WorkspaceService(database.Store, client) { ColumnMetadataEnabled = true };
        await workspace.OpenAsync(client.User);
        client.Columns = (_, _) =>
        {
            cancellation.Cancel();
            return new(null, new GitHubException("Late access denial", 403));
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => workspace.RefreshColumnsAsync("active", ct: cancellation.Token));
        var saved = await database.Store.LoadAsync(client.User);
        Assert.IsNull(saved.Items[item.Id].ColumnsError);
        Assert.IsNull(saved.Items[item.Id].ColumnsCheckedAt);
    }
}
