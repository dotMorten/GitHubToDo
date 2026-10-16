using GitHubToDo.Core;
using Microsoft.Data.Sqlite;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class UnreadOverrideTests
{
    private static FakeClient OwnLatestClient() => new()
    {
        Activity = (_, _) => Task.FromResult<IReadOnlyList<Activity>>(
        [
            new("question", ActivityKind.Comment, "other", "other", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "question", ""),
            new("answer", ActivityKind.Review, "1", "viewer", DateTimeOffset.Parse("2026-01-02T00:00:00Z"), "answer", "")
        ])
    };

    private static ItemReadSnapshot Snapshot(TrackedItem item) =>
        new(item.Id, item.Activities.Select(activity => activity.Id).ToArray());

    [TestMethod]
    public async Task AManuallyUnreadOwnCommentOverridesAutomaticReadForOnlyThatComment()
    {
        using var database = new TestDatabase();
        var client = OwnLatestClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account!.Items["item"], client.User.Id, []));
        await workspace.MarkCommentsUnreadAsync(new("item", ["answer"]));
        var item = workspace.Account.Items["item"];
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, client.User.Id, []));
        CollectionAssert.AreEqual(new[] { "answer" },
            ReadStatePolicy.UnreadActivity(item, client.User.Id, []).Select(activity => activity.Id).ToArray());
        await workspace.RefreshAsync();
        Assert.Contains("answer", workspace.Account.Items["item"].ManuallyUnreadIds);
        var restored = await database.Store.LoadAsync(client.User);
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(restored.Items["item"], client.User.Id, []));
        await workspace.MarkCommentsReadAsync(new("item", ["answer"]));
        Assert.IsEmpty(workspace.Account.Items["item"].ManuallyUnreadIds);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account.Items["item"], client.User.Id, []));
    }

    [TestMethod]
    public async Task MarkingAnOwnLatestItemUnreadPersistsUntilItsDiscussionIsReadAgain()
    {
        using var database = new TestDatabase();
        var client = OwnLatestClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        await workspace.SetFollowUpAsync("item", null, true);
        await workspace.SetFollowUpAsync("item", "answer", true);
        await workspace.MarkUnreadAsync([Snapshot(workspace.Account!.Items["item"])]);
        await workspace.RefreshAsync();
        var restored = await database.Store.LoadAsync(client.User);
        Assert.IsTrue(restored.Items["item"].IsMarkedUnread);
        Assert.HasCount(2, ReadStatePolicy.UnreadActivity(restored.Items["item"], client.User.Id, []));
        await workspace.MarkCommentsReadAsync(new("item", ["question"]));
        Assert.IsTrue(workspace.Account.Items["item"].IsMarkedUnread);
        Assert.HasCount(1, ReadStatePolicy.UnreadActivity(workspace.Account.Items["item"], client.User.Id, []));
        await workspace.MarkCommentsReadAsync(new("item", ["answer"]));
        Assert.IsFalse(workspace.Account.Items["item"].IsMarkedUnread);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account.Items["item"], client.User.Id, []));
        Assert.IsTrue(workspace.Account.Items["item"].IsFlagged);
        Assert.Contains("answer", workspace.Account.Items["item"].FlaggedActivityIds);
    }

    [TestMethod]
    public async Task MarkUnreadRestoresAcknowledgedCommentsAndMarkReadClearsTheOverride()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        await workspace.MarkReadAsync([Snapshot(workspace.Account!.Items["item"])]);
        await workspace.MarkCommentsUnreadAsync(new("item", ["comment:1"]));
        Assert.DoesNotContain("comment:1", workspace.Account.Items["item"].AcknowledgedIds);
        Assert.Contains("comment:1", workspace.Account.Items["item"].ManuallyUnreadIds);
        await workspace.MarkUnreadAsync([Snapshot(workspace.Account.Items["item"])]);
        Assert.IsTrue(workspace.Account.Items["item"].IsMarkedUnread);
        await workspace.MarkReadAsync([Snapshot(workspace.Account.Items["item"])]);
        Assert.IsFalse(workspace.Account.Items["item"].IsMarkedUnread);
        Assert.IsEmpty(workspace.Account.Items["item"].ManuallyUnreadIds);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account.Items["item"], client.User.Id, []));
        await workspace.OpenAsync(new("another", "another"));
        Assert.IsEmpty(workspace.Account!.Items);
    }

    [TestMethod]
    public async Task AnItemCanBeMarkedUnreadEvenWhenAllItsCommentsAreIgnored()
    {
        using var database = new TestDatabase();
        var client = new FakeClient();
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        await workspace.SetIgnoredAsync(["other"]);
        await workspace.MarkUnreadAsync([Snapshot(workspace.Account!.Items["item"])]);
        var item = workspace.Account.Items["item"];
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, client.User.Id, workspace.Account.IgnoredAccounts));
        Assert.IsEmpty(ReadStatePolicy.UnreadActivity(item, client.User.Id, workspace.Account.IgnoredAccounts));
        await workspace.MarkReadAsync([Snapshot(item)]);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account.Items["item"], client.User.Id, workspace.Account.IgnoredAccounts));
    }

    [TestMethod]
    public async Task ReadingAllLaterCommentsAlsoAcknowledgesAnOlderInitialItemMarker()
    {
        using var database = new TestDatabase();
        var client = new FakeClient { Activity = (_, _) => Task.FromResult<IReadOnlyList<Activity>>([]) };
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        var initialId = workspace.Account!.Items["item"].Activities.Single().Id;
        client.Activity = (_, _) => Task.FromResult<IReadOnlyList<Activity>>(
            [new("later", ActivityKind.Comment, "other", "other", DateTimeOffset.UtcNow, "later", "")]);
        await workspace.RefreshAsync();
        await workspace.MarkCommentsReadAsync(new("item", ["later"]));
        Assert.Contains(initialId, workspace.Account.Items["item"].AcknowledgedIds);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account.Items["item"], client.User.Id, []));
    }

    [TestMethod]
    public async Task BulkMarkUnreadRollsBackWhenPersistenceFails()
    {
        using var database = new TestDatabase();
        var client = new FakeClient
        {
            Search = (_, _) =>
            {
                var second = FakeClient.NewItem();
                second.Id = "second";
                return Task.FromResult(new SearchResult([FakeClient.NewItem(), second], 2, false));
            }
        };
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(client.User);
        await workspace.RefreshAsync();
        await workspace.MarkReadAsync(workspace.Account!.Items.Values.Select(Snapshot));
        await using var connection = new SqliteConnection($"Data Source={database.PathName}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailUnread BEFORE INSERT ON Items WHEN NEW.Id='second' BEGIN SELECT RAISE(ABORT,'simulated disk error'); END;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => workspace.MarkUnreadAsync(workspace.Account.Items.Values.Select(Snapshot)));
        var restored = await database.Store.LoadAsync(client.User);
        Assert.IsTrue(workspace.Account.Items.Values.All(item => !item.IsMarkedUnread && item.AcknowledgedIds.Count == 1));
        Assert.IsTrue(restored.Items.Values.All(item => !item.IsMarkedUnread && item.AcknowledgedIds.Count == 1));
    }

    [TestMethod]
    public async Task CommentCommandsRejectEmptyOrUnavailableActivities()
    {
        using var database = new TestDatabase();
        var workspace = new WorkspaceService(database.Store, new FakeClient());
        await workspace.OpenAsync(new("1", "viewer"));
        await workspace.RefreshAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.MarkCommentsUnreadAsync(new("item", [])));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.MarkCommentsReadAsync(new("item", ["missing"])));
        Assert.IsEmpty(workspace.Account!.Items["item"].ManuallyUnreadIds);
    }
}
