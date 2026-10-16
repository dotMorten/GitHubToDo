using GitHubToDo.Core;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class ReadStateTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static Activity Comment(string id, string? author, int seconds = 0, string? login = null) =>
        new(id, ActivityKind.Comment, author, login ?? author, Start.AddSeconds(seconds), id, "");
    private static TrackedItem Item(params Activity[] activities) =>
        new() { Id = "item", AuthorId = "other", AuthorLogin = "other", CreatedAt = Start, HasBaseline = true, Activities = [.. activities] };

    [TestMethod]
    public void InitialHydrationIsUnknown()
    {
        Assert.AreEqual(ReadStatus.Unknown, ReadStatePolicy.Evaluate(new(), "me", []));
    }

    [TestMethod]
    public void OtherActivityRequiresExplicitAcknowledgement()
    {
        var item = Item(Comment("one", "other"));
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, "me", []));
        item.AcknowledgedIds.Add("one");
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "me", []));
        item.Activities.Add(Comment("two", "other", 1));
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, "me", []));
    }

    [TestMethod]
    public void OwnLatestAutomaticallyReadsButDoesNotPermanentlyAcknowledge()
    {
        var item = Item(Comment("one", "other"), Comment("two", "me", 1));
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "me", []));
        Assert.IsEmpty(item.AcknowledgedIds);
        item.Activities.Add(Comment("three", "other", 2));
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, "me", []));
    }

    [TestMethod]
    public void IgnoredAccountsAreGlobalCaseInsensitiveAndStableAcrossRename()
    {
        var item = Item(Comment("human", "me"), Comment("bot", "42", 1, "bot[bot]"));
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "me", [new("BOT[BOT]")]));
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "me", [new("old-name", "42")]));
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, "me", []));
    }

    [TestMethod]
    public void AllIgnoredIsReadButUnignoreRevealsActivity()
    {
        var item = Item(Comment("bot", "42", 0, "bot"));
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "me", [new("bot")]));
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, "me", []));
    }

    [TestMethod]
    public void MixedAuthorTimestampTieDoesNotAutoRead()
    {
        var item = Item(Comment("one", "me"), Comment("two", "other"));
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, "me", []));
    }

    [TestMethod]
    public void UnknownAuthorsAreNotImplicitlyIgnored()
    {
        var activity = new Activity("one", ActivityKind.Comment, null, null, Start, "", "");
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(Item(activity), "me", [new("ghost")]));
    }

    [TestMethod]
    public void NoCommentsGetInitialMarkerOnlyOnce()
    {
        var item = Item();
        item.HasBaseline = false;
        ReadStatePolicy.ApplyActivity(item, [], Start);
        Assert.HasCount(1, item.Activities);
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, "me", []));
        item.AcknowledgedIds.Add(item.Activities[0].Id);
        ReadStatePolicy.ApplyActivity(item, [], Start.AddHours(1));
        Assert.HasCount(1, item.Activities);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "me", []));
    }

    [TestMethod]
    public void OwnNoCommentItemStartsRead()
    {
        var item = new TrackedItem { Id = "mine", AuthorId = "me", CreatedAt = Start };
        ReadStatePolicy.ApplyActivity(item, [], Start);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "me", []));
    }

    [TestMethod]
    public void EditedActivityRetainsPublicationTimeAndAcknowledgement()
    {
        var item = Item(Comment("one", "other"));
        item.AcknowledgedIds.Add("one");
        ReadStatePolicy.ApplyActivity(item, [Comment("one", "other", 100) with { Body = "edited" }], Start.AddDays(1));
        Assert.AreEqual(Start, item.Activities[0].PublishedAt);
        Assert.AreEqual("edited", item.Activities[0].Body);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "me", []));
    }

    [TestMethod]
    public void DeletionDoesNotCreateInitialMarkerAfterBaseline()
    {
        var item = Item(Comment("one", "other"));
        ReadStatePolicy.ApplyActivity(item, [], Start);
        Assert.IsEmpty(item.Activities);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "me", []));
    }

    [TestMethod]
    public void LateDiscoveredOldActivityIsNotReadByWallClock()
    {
        var item = Item(Comment("new", "other", 100));
        item.AcknowledgedIds.Add("new");
        item.Activities.Add(Comment("late", "other", 1));
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, "me", []));
    }
}
