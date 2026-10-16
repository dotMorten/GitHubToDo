using System.Collections.Specialized;
using System.Text.Json;
using GitHubToDo.Core;
using GitHubToDo.ViewModels;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class WorkspaceRowsTests
{
    private static AccountSnapshot PendingAccount(int count = 30)
    {
        var account = new AccountSnapshot { User = new("viewer", "viewer") };
        for (var index = 0; index < count; index++)
        {
            var item = FakeClient.NewItem();
            item.Id = $"item:{index}";
            item.Number = index + 1;
            account.Items.Add(item.Id, item);
            foreach (var query in account.Queries) query.ItemIds.Add(item.Id);
        }
        return account;
    }

    private static AccountSnapshot Snapshot(AccountSnapshot account) =>
        JsonSerializer.Deserialize<AccountSnapshot>(JsonSerializer.Serialize(account))!;

    [TestMethod]
    public void PendingCountdownUpdatesRowsWithoutResettingCollectionsOrReplacingSelection()
    {
        var account = PendingAccount();
        var rows = new WorkspaceRows();
        rows.UpdateQueries(account);
        rows.UpdateItems(account.Queries[0], account);
        var queries = rows.Queries.ToArray();
        var items = rows.Items.ToArray();
        var selected = rows.Items[15];
        var notifications = new List<NotifyCollectionChangedAction>();
        rows.Queries.CollectionChanged += (_, args) => notifications.Add(args.Action);
        rows.Items.CollectionChanged += (_, args) => notifications.Add(args.Action);
        for (var index = 0; index < items.Length; index++)
        {
            account = Snapshot(account);
            ReadStatePolicy.ApplyActivity(account.Items[items[index].Id],
                [new($"comment:{index}", ActivityKind.Comment, "other", "other",
                    DateTimeOffset.Parse("2026-01-02T00:00:00Z").AddMinutes(-index), "new comment", "")],
                DateTimeOffset.UtcNow);
            rows.UpdateQueries(account);
            rows.UpdateItems(account.Queries[0], account);
            Assert.AreEqual(items.Length - index - 1, rows.Queries[0].PendingCount);
            Assert.AreEqual(index + 1, rows.Queries[0].UnreadCount);
            for (var rowIndex = 0; rowIndex < items.Length; rowIndex++)
                Assert.AreSame(items[rowIndex], rows.Items[rowIndex]);
            for (var queryIndex = 0; queryIndex < queries.Length; queryIndex++)
                Assert.AreSame(queries[queryIndex], rows.Queries[queryIndex]);
            Assert.AreSame(selected, rows.Items.Single(row => row.Id == selected.Id));
        }
        Assert.IsEmpty(notifications, "Hydration must not send Reset, Remove, Add, Replace, or Move for unchanged membership.");
        Assert.AreEqual(ReadStatus.Unread.ToString(), selected.ReadLabel);
        Assert.IsTrue(selected.Item.HasBaseline);
    }

    [TestMethod]
    public void HydrationNotifiesBindingsAndAccessibleNamesOnTheExistingRow()
    {
        var account = PendingAccount(2);
        var rows = new WorkspaceRows();
        rows.UpdateQueries(account);
        rows.UpdateItems(account.Queries[0], account);
        var changed = new List<string?>();
        var untouched = new List<string?>();
        var queryChanges = new List<string?>();
        rows.Items[0].PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        rows.Items[1].PropertyChanged += (_, args) => untouched.Add(args.PropertyName);
        rows.Queries[0].PropertyChanged += (_, args) => queryChanges.Add(args.PropertyName);
        account = Snapshot(account);
        ReadStatePolicy.ApplyActivity(account.Items["item:0"],
            [new("comment:0", ActivityKind.Comment, "other", "other", DateTimeOffset.UtcNow, "hello", "")],
            DateTimeOffset.UtcNow);
        rows.UpdateQueries(account);
        rows.UpdateItems(account.Queries[0], account);
        Assert.Contains(nameof(ItemRow.ReadLabel), changed);
        Assert.Contains(nameof(ItemRow.IsUnread), changed);
        Assert.Contains(nameof(ItemRow.AccessibleName), changed);
        Assert.Contains(nameof(ItemRow.Freshness), changed);
        Assert.Contains(nameof(QueryRow.Badge), queryChanges);
        Assert.Contains(nameof(QueryRow.PendingCount), queryChanges);
        Assert.IsEmpty(untouched.Where(property => property != nameof(ItemRow.Item)));
    }

    [TestMethod]
    public void MembershipChangesUseGranularOperationsAndRetainUnaffectedRows()
    {
        var account = PendingAccount(3);
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var retained = rows.Items[2];
        var notifications = new List<NotifyCollectionChangedAction>();
        rows.Items.CollectionChanged += (_, args) => notifications.Add(args.Action);
        var added = FakeClient.NewItem();
        added.Id = "added";
        account.Items.Add(added.Id, added);
        account.Queries[0].ItemIds = ["item:2", "added", "item:0"];
        rows.UpdateItems(account.Queries[0], account);
        CollectionAssert.AreEqual(new[] { "item:2", "added", "item:0" }, rows.Items.Select(row => row.Id).ToArray());
        Assert.AreSame(retained, rows.Items[0]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, notifications);
        Assert.Contains(NotifyCollectionChangedAction.Remove, notifications);
        Assert.Contains(NotifyCollectionChangedAction.Move, notifications);
        Assert.Contains(NotifyCollectionChangedAction.Add, notifications);
    }

    [TestMethod]
    public void QueryRenameAndReorderKeepQueryIdentityAndUpdateName()
    {
        var account = PendingAccount(1);
        var rows = new WorkspaceRows();
        rows.UpdateQueries(account);
        var first = rows.Queries[0];
        var notifications = new List<NotifyCollectionChangedAction>();
        rows.Queries.CollectionChanged += (_, args) => notifications.Add(args.Action);
        account = Snapshot(account);
        var query = account.Queries[0];
        account.Queries.RemoveAt(0);
        query.Name = "Renamed query";
        account.Queries.Add(query);
        rows.UpdateQueries(account);
        Assert.AreSame(first, rows.Queries[2]);
        Assert.AreEqual("Renamed query", first.Name);
        Assert.IsTrue(notifications.All(action => action == NotifyCollectionChangedAction.Move));
    }

    [TestMethod]
    public void ReadAndIgnoreChangesUpdateTheSameRowsInEveryQuery()
    {
        var account = PendingAccount(1);
        var item = account.Items["item:0"];
        ReadStatePolicy.ApplyActivity(item,
            [new("comment:1", ActivityKind.Comment, "bot-id", "bot", DateTimeOffset.UtcNow, "hello", "")],
            DateTimeOffset.UtcNow);
        var rows = new WorkspaceRows();
        rows.UpdateQueries(account);
        rows.UpdateItems(account.Queries[0], account);
        var row = rows.Items[0];
        account = Snapshot(account);
        account.IgnoredAccounts.Add(new("bot", "bot-id"));
        rows.UpdateQueries(account);
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreSame(row, rows.Items[0]);
        Assert.IsFalse(row.IsUnread);
        Assert.IsTrue(rows.Queries.All(query => query.UnreadCount == 0));
        account.IgnoredAccounts.Clear();
        account.Items[row.Id].AcknowledgedIds.Add("comment:1");
        rows.UpdateQueries(account);
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreSame(row, rows.Items[0]);
        Assert.AreEqual("Read", row.ReadLabel);
    }

    [TestMethod]
    public void LatestRelevantActivityDescendingOverridesSearchOrderAndUpdatedAt()
    {
        var account = PendingAccount(3);
        var time = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
        for (var index = 0; index < 3; index++)
        {
            var item = account.Items[$"item:{index}"];
            item.UpdatedAt = time.AddDays(10 - index);
            var latest = time.AddDays(index == 0 ? 0 : index == 1 ? 2 : 1);
            ReadStatePolicy.ApplyActivity(item,
            [
                new($"latest:{index}", ActivityKind.Comment, "other", "other", latest, "latest", ""),
                new($"older:{index}", ActivityKind.Comment, "other", "other", time.AddDays(-1), "older", "")
            ], time);
        }
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        CollectionAssert.AreEqual(new[] { "item:1", "item:2", "item:0" }, rows.Items.Select(row => row.Id).ToArray());
        CollectionAssert.AreEqual(new[] { time.AddDays(2), time.AddDays(1), time },
            rows.Items.Select(row => row.LatestActivityAt!.Value).ToArray());
        var notifications = new List<NotifyCollectionChangedAction>();
        rows.Items.CollectionChanged += (_, args) => notifications.Add(args.Action);
        account = Snapshot(account);
        account.Queries[0].ItemIds.Reverse();
        rows.UpdateItems(account.Queries[0], account);
        Assert.IsEmpty(notifications, "Server reordering must not disturb the latest-activity ordering.");
    }

    [TestMethod]
    public void HydrationAndNewActivityMoveExistingRowsWithoutReplacingSelection()
    {
        var account = PendingAccount(2);
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var selected = rows.Items[0];
        var other = rows.Items[1];
        var notifications = new List<NotifyCollectionChangedAction>();
        rows.Items.CollectionChanged += (_, args) => notifications.Add(args.Action);
        var time = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
        ReadStatePolicy.ApplyActivity(account.Items[other.Id],
            [new("comment:1", ActivityKind.Comment, "other", "other", time, "first", "")], time);
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreSame(other, rows.Items[0]);
        Assert.AreSame(selected, rows.Items[1]);
        account = Snapshot(account);
        ReadStatePolicy.ApplyActivity(account.Items[selected.Id],
            [new("comment:2", ActivityKind.Comment, "other", "other", time.AddDays(1), "newest", "")], time);
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreSame(selected, rows.Items[0]);
        Assert.AreSame(other, rows.Items[1]);
        CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Move, NotifyCollectionChangedAction.Move }, notifications);
    }

    [TestMethod]
    public void IgnoringAndUnignoringLatestAuthorReordersByRemainingRelevantActivity()
    {
        var account = PendingAccount(2);
        var time = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
        ReadStatePolicy.ApplyActivity(account.Items["item:0"],
        [
            new("human", ActivityKind.Comment, "other", "other", time, "older human", ""),
            new("bot", ActivityKind.Comment, "bot", "bot", time.AddDays(2), "newer bot", "")
        ], time);
        ReadStatePolicy.ApplyActivity(account.Items["item:1"],
            [new("middle", ActivityKind.Review, "other", "other", time.AddDays(1), "review", "")], time);
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var first = rows.Items[0];
        Assert.AreEqual("item:0", first.Id);
        account.IgnoredAccounts.Add(new("bot", "bot"));
        rows.UpdateItems(account.Queries[0], account);
        CollectionAssert.AreEqual(new[] { "item:1", "item:0" }, rows.Items.Select(row => row.Id).ToArray());
        Assert.AreSame(first, rows.Items[1]);
        Assert.AreEqual(time, first.LatestActivityAt);
        account.IgnoredAccounts.Clear();
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreSame(first, rows.Items[0]);
        Assert.AreEqual(time.AddDays(2), first.LatestActivityAt);
    }

    [TestMethod]
    public void EqualTimestampsKeepQueryOrderAndMissingRelevantActivitySortsLast()
    {
        var account = PendingAccount(5);
        var time = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
        foreach (var index in new[] { 0, 1, 3 })
            ReadStatePolicy.ApplyActivity(account.Items[$"item:{index}"],
                [new($"comment:{index}", ActivityKind.Comment, "other", "other", time, "", "")], time);
        ReadStatePolicy.ApplyActivity(account.Items["item:4"],
            [new("bot", ActivityKind.Comment, "bot", "bot", time.AddDays(10), "", "")], time);
        account.IgnoredAccounts.Add(new("bot", "bot"));
        account.Items["item:2"].UpdatedAt = time.AddDays(20);
        account.Queries[0].ItemIds = ["item:2", "item:3", "item:4", "item:1", "item:0"];
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        CollectionAssert.AreEqual(new[] { "item:3", "item:1", "item:0", "item:2", "item:4" },
            rows.Items.Select(row => row.Id).ToArray());
        Assert.IsNull(rows.Items[3].LatestActivityAt);
        Assert.IsNull(rows.Items[4].LatestActivityAt);
    }

    [TestMethod]
    public void MultipleSelectionSurvivesHydrationMovesAndPrunesOnlyRemovedRows()
    {
        var account = PendingAccount(3);
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var first = rows.Items[0];
        var second = rows.Items[1];
        rows.UpdateSelection([first, second, first]);
        Assert.HasCount(2, rows.SelectedItems);
        Assert.IsTrue(first.IsSelected && second.IsSelected);
        ReadStatePolicy.ApplyActivity(account.Items[second.Id],
            [new("new", ActivityKind.Comment, "other", "other", DateTimeOffset.UtcNow, "new", "")], DateTimeOffset.UtcNow);
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreSame(second, rows.Items[0]);
        Assert.AreSame(second, rows.SelectedItems[0]);
        Assert.AreSame(first, rows.SelectedItems[1]);
        account.Queries[0].ItemIds.Remove(first.Id);
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreSame(second, rows.SelectedItems.Single());
        Assert.IsFalse(first.IsSelected);
        rows.UpdateSelection([]);
        Assert.IsEmpty(rows.SelectedItems);
        Assert.IsFalse(second.IsSelected);
    }

    [TestMethod]
    public void SelectAllAndClearAccountKeepSelectionConsistent()
    {
        var account = PendingAccount(20);
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        rows.UpdateSelection(rows.Items);
        Assert.HasCount(20, rows.SelectedItems);
        Assert.IsTrue(rows.Items.All(row => row.IsSelected));
        var selected = rows.SelectedItems.ToArray();
        rows.UpdateItems(null, null);
        Assert.IsEmpty(rows.SelectedItems);
        Assert.IsTrue(selected.All(row => !row.IsSelected));
    }

    [TestMethod]
    public void UnreadPreviewFiltersAcknowledgedIgnoredAndInitialEventsInChronologicalOrder()
    {
        var account = PendingAccount(1);
        var item = account.Items.Values.Single();
        var time = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
        item.HasBaseline = true;
        item.Activities =
        [
            new("new-review", ActivityKind.Review, "other", "other", time.AddMinutes(3), "APPROVED", ""),
            new("initial", ActivityKind.InitialItem, "other", "other", time.AddDays(-2), "initial", ""),
            new("ignored", ActivityKind.Comment, "bot", "bot", time.AddMinutes(4), "bot", ""),
            new("seen", ActivityKind.Comment, "other", "other", time, "already seen", ""),
            new("new-inline", ActivityKind.ReviewComment, "other", "other", time.AddMinutes(1), "inline reply", "")
        ];
        item.AcknowledgedIds.Add("seen");
        account.IgnoredAccounts.Add(new("BOT", "bot"));
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var row = rows.Items.Single();
        CollectionAssert.AreEqual(new[] { "new-inline", "new-review" },
            row.UnreadActivities.Select(activity => activity.Id).ToArray());
        var retained = row.UnreadActivities[0];
        var notifications = new List<NotifyCollectionChangedAction>();
        row.UnreadActivities.CollectionChanged += (_, args) => notifications.Add(args.Action);
        account = Snapshot(account);
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreSame(retained, row.UnreadActivities[0]);
        Assert.IsEmpty(notifications);
        account.Items[item.Id].AcknowledgedIds.Add("new-inline");
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreEqual("new-review", row.UnreadActivities.Single().Id);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, notifications);
    }

    [TestMethod]
    public void AutomaticReadAndUnknownHistoryDoNotExposeAnUnreadConversation()
    {
        var account = PendingAccount(1);
        var item = account.Items.Values.Single();
        var time = DateTimeOffset.UtcNow;
        item.Activities =
        [
            new("other", ActivityKind.Comment, "other", "other", time, "question", ""),
            new("own", ActivityKind.Review, account.User.Id, account.User.Login, time.AddMinutes(1), "answer", "")
        ];
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var row = rows.Items.Single();
        Assert.IsEmpty(row.UnreadActivities);
        Assert.Contains("loading", row.UnreadSummary);
        item.HasBaseline = true;
        rows.UpdateItems(account.Queries[0], account);
        Assert.IsEmpty(row.UnreadActivities);
        Assert.AreEqual("Read", row.ReadLabel);
        item.Activities.Add(new("later", ActivityKind.Comment, "other", "other", time.AddMinutes(2), "follow-up", ""));
        rows.UpdateItems(account.Queries[0], account);
        Assert.HasCount(3, row.UnreadActivities, "Automatic read must not silently acknowledge any activity.");
    }

    [TestMethod]
    public void UnreadCommentCountIsUnknownUntilHydratedAndNotifiesWhenAcknowledged()
    {
        var account = PendingAccount(1);
        var item = account.Items.Values.Single();
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var row = rows.Items.Single();
        Assert.IsNull(row.UnreadCommentCount);
        Assert.AreEqual("…", row.UnreadCountText);
        Assert.Contains("pending", row.UnreadCountDescription);
        var time = DateTimeOffset.UtcNow;
        var changes = new List<string?>();
        row.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        ReadStatePolicy.ApplyActivity(item,
        [
            new("comment", ActivityKind.Comment, "other", "other", time, "question", ""),
            new("review", ActivityKind.Review, "reviewer", "reviewer", time.AddMinutes(1), "APPROVED", "")
        ], time);
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreEqual(2, row.UnreadCommentCount);
        Assert.AreEqual("2", row.UnreadCountText);
        Assert.Contains(nameof(ItemRow.UnreadCommentCount), changes);
        Assert.Contains(nameof(ItemRow.UnreadCountText), changes);
        Assert.Contains(nameof(ItemRow.AccessibleName), changes);
        var retained = row.DiscussionActivities.ToArray();
        item.AcknowledgedIds.Add("comment");
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreEqual(1, row.UnreadCommentCount);
        Assert.IsFalse(retained[0].IsUnread);
        Assert.IsTrue(retained[1].IsUnread);
        item.AcknowledgedIds.Add("review");
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreEqual(0, row.UnreadCommentCount);
        Assert.AreEqual("0", row.UnreadCountText);
        Assert.IsEmpty(row.UnreadActivities);
        Assert.AreEqual("No unread comments or reviews.", row.UnreadSummary);
        CollectionAssert.AreEqual(retained, row.DiscussionActivities.ToArray());
        Assert.IsTrue(row.DiscussionActivities.All(activity => !activity.IsUnread));
    }

    [TestMethod]
    public void DiscussionIncludesReadHistoryButExcludesIgnoredAndInitialMarkers()
    {
        var account = PendingAccount(1);
        var item = account.Items.Values.Single();
        var time = DateTimeOffset.UtcNow;
        item.HasBaseline = true;
        item.Activities =
        [
            new("new", ActivityKind.ReviewComment, "other", "other", time.AddMinutes(1), "new reply", ""),
            new("initial", ActivityKind.InitialItem, "other", "other", time.AddDays(-2), "initial", ""),
            new("seen", ActivityKind.Comment, "other", "other", time, "read comment", ""),
            new("bot", ActivityKind.Comment, "bot", "bot", time.AddMinutes(2), "bot comment", "")
        ];
        item.AcknowledgedIds.Add("seen");
        account.IgnoredAccounts.Add(new("BOT", "bot"));
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var row = rows.Items.Single();
        CollectionAssert.AreEqual(new[] { "seen", "new" }, row.DiscussionActivities.Select(activity => activity.Id).ToArray());
        Assert.AreSame(row.DiscussionActivities[1], row.UnreadActivities.Single());
        Assert.AreEqual(1, row.UnreadCommentCount);
        Assert.Contains("All 2", row.DiscussionSummary);
        account.IgnoredAccounts.Clear();
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreEqual(2, row.UnreadCommentCount);
        Assert.HasCount(3, row.DiscussionActivities);
    }

    [TestMethod]
    public void OwnLatestActivityRetainsFullDiscussionWithZeroUnreadComments()
    {
        var account = PendingAccount(1);
        var item = account.Items.Values.Single();
        var time = DateTimeOffset.UtcNow;
        ReadStatePolicy.ApplyActivity(item,
        [
            new("other", ActivityKind.Comment, "other", "other", time, "question", ""),
            new("own", ActivityKind.Comment, account.User.Id, account.User.Login, time.AddMinutes(1), "answer", "")
        ], time);
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var row = rows.Items.Single();
        Assert.AreEqual(0, row.UnreadCommentCount);
        Assert.IsEmpty(row.UnreadActivities);
        Assert.HasCount(2, row.DiscussionActivities);
        Assert.IsTrue(row.DiscussionActivities.All(activity => !activity.IsUnread));
        Assert.IsEmpty(item.AcknowledgedIds, "Showing discussion must not create acknowledgements.");
    }

    [TestMethod]
    public void UnreadInitialItemHasZeroCommentsAndAnExplicitEmptyDiscussion()
    {
        var account = PendingAccount(1);
        var item = account.Items.Values.Single();
        ReadStatePolicy.ApplyActivity(item, [], DateTimeOffset.UtcNow);
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var row = rows.Items.Single();
        Assert.AreEqual("Unread", row.ReadLabel);
        Assert.AreEqual(0, row.UnreadCommentCount);
        Assert.IsEmpty(row.DiscussionActivities);
        Assert.Contains("No comments", row.DiscussionSummary);
    }

    [TestMethod]
    public void DiscussionRowsAndCountRemainStableOnUnchangedAndStaleRefresh()
    {
        var account = PendingAccount(1);
        var item = account.Items.Values.Single();
        ReadStatePolicy.ApplyActivity(item,
            [new("comment", ActivityKind.Comment, "other", "other", DateTimeOffset.UtcNow, "cached unread", "")], DateTimeOffset.UtcNow);
        var rows = new WorkspaceRows();
        rows.UpdateItems(account.Queries[0], account);
        var row = rows.Items.Single();
        var activityRow = row.DiscussionActivities.Single();
        var notifications = new List<NotifyCollectionChangedAction>();
        row.DiscussionActivities.CollectionChanged += (_, args) => notifications.Add(args.Action);
        row.UnreadActivities.CollectionChanged += (_, args) => notifications.Add(args.Action);
        account = Snapshot(account);
        account.Items[item.Id].ActivityError = "offline";
        rows.UpdateItems(account.Queries[0], account);
        Assert.AreSame(activityRow, row.DiscussionActivities.Single());
        Assert.AreSame(activityRow, row.UnreadActivities.Single());
        Assert.AreEqual(1, row.UnreadCommentCount);
        Assert.Contains("Stale", row.Freshness);
        Assert.IsEmpty(notifications);
    }

    [TestMethod]
    public void ReadingCommentsRetainsPreviewRowsAndOnlySnapshotsTheReachedPrefix()
    {
        var account = PendingAccount(1);
        var item = account.Items.Values.Single();
        var time = DateTimeOffset.UtcNow;
        ReadStatePolicy.ApplyActivity(item,
        [
            new("one", ActivityKind.Comment, "other", "other", time, "first", ""),
            new("two", ActivityKind.Review, "other", "other", time.AddMinutes(1), "second", "")
        ], time);
        var row = new ItemRow(item, account);
        var preview = new DiscussionPreview();
        preview.Update(row);
        var first = preview.Activities[0];
        Assert.IsNull(preview.ReadThrough(-1));
        var snapshot = preview.ReadThrough(0)!;
        CollectionAssert.AreEqual(new[] { "one" }, snapshot.ActivityIds.ToArray());
        item.AcknowledgedIds.Add("one");
        row.Update(item, account);
        preview.Update(row);
        Assert.AreSame(first, preview.Activities[0]);
        Assert.IsFalse(first.IsUnread);
        Assert.HasCount(2, preview.Activities);
        Assert.AreEqual(1, row.UnreadCommentCount);
        CollectionAssert.AreEqual(new[] { "two" }, preview.ReadThrough(1)!.ActivityIds.ToArray());
        item.AcknowledgedIds.Add("two");
        row.Update(item, account);
        preview.Update(row);
        Assert.HasCount(2, preview.Activities);
        Assert.IsNull(preview.ReadThrough(1));
        preview.Update(null);
        preview.Update(row);
        Assert.HasCount(2, preview.Activities, "Reopening an already-read item still shows the full discussion.");
    }

    [TestMethod]
    public void NewlyDiscoveredCommentsAppendWithoutMovingTheDiscussionBeingRead()
    {
        var account = PendingAccount(1);
        var item = account.Items.Values.Single();
        var time = DateTimeOffset.UtcNow;
        ReadStatePolicy.ApplyActivity(item,
            [new("original", ActivityKind.Comment, "other", "other", time, "on screen", "")], time);
        var row = new ItemRow(item, account);
        var preview = new DiscussionPreview();
        preview.Update(row);
        var observed = preview.ReadThrough(0)!;
        item.Activities.Add(new("late", ActivityKind.Comment, "other", "other", time.AddDays(-1), "late discovery", ""));
        row.Update(item, account);
        preview.Update(row);
        CollectionAssert.AreEqual(new[] { "original", "late" }, preview.Activities.Select(activity => activity.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "original" }, observed.ActivityIds.ToArray());
        preview.Update(null);
        preview.Update(row);
        CollectionAssert.AreEqual(new[] { "late", "original" }, preview.Activities.Select(activity => activity.Id).ToArray());
    }

    [TestMethod]
    public void FollowUpsViewIncludesReadItemsOutsideQueriesAndUpdatesWithoutAReset()
    {
        var account = PendingAccount(2);
        var item = account.Items["item:0"];
        ReadStatePolicy.ApplyActivity(item,
            [new("flagged", ActivityKind.Comment, "other", "other", DateTimeOffset.UtcNow, "follow up", "")], DateTimeOffset.UtcNow);
        item.AcknowledgedIds.Add("flagged");
        item.FlaggedActivityIds.Add("flagged");
        foreach (var query in account.Queries) query.ItemIds.Clear();
        var rows = new WorkspaceRows();
        rows.UpdateQueries(account);
        var followUps = rows.Queries.Single(query => !query.IsSavedQuery);
        Assert.AreEqual(0, followUps.UnreadCount);
        Assert.AreEqual(1, followUps.Query.TotalCount);
        rows.UpdateItems(followUps.Query, account);
        var row = rows.Items.Single();
        Assert.IsTrue(row.HasFollowUps);
        Assert.IsTrue(row.DiscussionActivities.Single().IsFlagged);
        Assert.Contains("flagged for follow-up", row.AccessibleName);
        Assert.AreEqual(0, row.UnreadCommentCount);
        Assert.HasCount(3, account.Queries, "The local view must not be saved as a GitHub search.");
        item.IsFlagged = true;
        rows.UpdateQueries(account);
        Assert.AreSame(followUps, rows.Queries.Single(query => !query.IsSavedQuery));
        item.FlaggedActivityIds.Clear();
        item.IsFlagged = false;
        rows.UpdateQueries(account);
        Assert.IsEmpty(followUps.Query.ItemIds);
    }

    [TestMethod]
    public void ReachedCommentSnapshotsDoNotShiftToTheNextCommentWhenRefreshDeletesAPrefix()
    {
        var account = PendingAccount(1);
        var item = account.Items.Values.Single();
        var time = DateTimeOffset.UtcNow;
        ReadStatePolicy.ApplyActivity(item,
        [
            new("reached", ActivityKind.Comment, "other", "other", time, "read to its bottom", ""),
            new("not-reached", ActivityKind.Comment, "other", "other", time.AddMinutes(1), "long unseen remainder", "")
        ], time);
        var row = new ItemRow(item, account);
        var preview = new DiscussionPreview();
        preview.Update(row);
        var reached = preview.ReadThrough(0)!;
        item.Activities.RemoveAll(activity => activity.Id == "reached");
        row.Update(item, account);
        preview.Update(row);
        Assert.AreEqual("not-reached", preview.Activities.Single().Id);
        CollectionAssert.AreEqual(new[] { "reached" }, reached.ActivityIds.ToArray());
        Assert.DoesNotContain("not-reached", reached.ActivityIds);
    }
}
