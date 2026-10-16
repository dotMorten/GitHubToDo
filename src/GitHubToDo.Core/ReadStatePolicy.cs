namespace GitHubToDo.Core;

public static class ReadStatePolicy
{
    public static bool IsIgnored(Activity activity, IReadOnlyList<IgnoredAccount> ignored) =>
        ignored.Any(account =>
            (account.Id is not null && account.Id == activity.AuthorId) ||
            (activity.AuthorLogin is not null &&
             string.Equals(account.Login, activity.AuthorLogin, StringComparison.OrdinalIgnoreCase)));

    public static IReadOnlyList<Activity> Eligible(TrackedItem item, IReadOnlyList<IgnoredAccount> ignored) =>
        item.Activities.Where(activity => !IsIgnored(activity, ignored)).ToArray();

    public static ReadStatus Evaluate(TrackedItem item, string viewerId, IReadOnlyList<IgnoredAccount> ignored)
    {
        if (!item.HasBaseline)
            return ReadStatus.Unknown;
        if (item.IsMarkedUnread)
            return ReadStatus.Unread;
        var activities = Eligible(item, ignored);
        if (activities.Count == 0)
            return ReadStatus.Read;
        if (activities.Any(activity => item.ManuallyUnreadIds.Contains(activity.Id)))
            return ReadStatus.Unread;
        if (LatestIsOwn(activities, viewerId))
            return ReadStatus.Read;
        return activities.Any(activity => !item.AcknowledgedIds.Contains(activity.Id))
            ? ReadStatus.Unread : ReadStatus.Read;
    }

    public static IReadOnlyList<Activity> DiscussionActivity(TrackedItem item, IReadOnlyList<IgnoredAccount> ignored) =>
        item.HasBaseline
            ? Eligible(item, ignored).Where(activity => activity.Kind != ActivityKind.InitialItem)
                .OrderBy(activity => activity.PublishedAt).ToArray()
            : [];

    public static IReadOnlyList<Activity> UnreadActivity(TrackedItem item, string viewerId, IReadOnlyList<IgnoredAccount> ignored)
    {
        if (Evaluate(item, viewerId, ignored) != ReadStatus.Unread) return [];
        var includeUnacknowledged = item.IsMarkedUnread || !LatestIsOwn(Eligible(item, ignored), viewerId);
        return DiscussionActivity(item, ignored).Where(activity =>
            item.ManuallyUnreadIds.Contains(activity.Id) ||
            (includeUnacknowledged && !item.AcknowledgedIds.Contains(activity.Id))).ToArray();
    }

    private static bool LatestIsOwn(IReadOnlyList<Activity> activities, string viewerId)
    {
        if (activities.Count == 0) return false;
        var latestTime = activities.Max(activity => activity.PublishedAt);
        return activities.Where(activity => activity.PublishedAt == latestTime).All(activity => activity.AuthorId == viewerId);
    }

    public static void ApplyActivity(TrackedItem item, IReadOnlyList<Activity> published, DateTimeOffset checkedAt)
    {
        var previous = item.Activities.ToDictionary(activity => activity.Id);
        var current = published.DistinctBy(activity => activity.Id)
            .Select(activity => previous.TryGetValue(activity.Id, out var old)
                ? activity with { PublishedAt = old.PublishedAt }
                : activity).ToList();
        var initial = item.Activities.FirstOrDefault(activity => activity.Kind == ActivityKind.InitialItem);
        if (initial is not null)
            current.Add(initial);
        else if (!item.HasBaseline && current.Count == 0)
            current.Add(new($"initial:{item.Id}", ActivityKind.InitialItem, item.AuthorId,
                item.AuthorLogin, item.CreatedAt, "Item first discovered with no comments or reviews.", item.Url));
        item.Activities = current;
        item.HasBaseline = true;
        item.ActivityCheckedAt = checkedAt;
        item.ActivityError = null;
    }
}
