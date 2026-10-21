using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHubToDo.Core;

namespace GitHubToDo.ViewModels;

public sealed class QueryRow : ObservableObject
{
    private SavedQuery _query;
    private int _unreadCount;
    private int _pendingCount;
    public SavedQuery Query => _query;
    public string Id => Query.Id;
    public string Name => Query.Name;
    public bool IsSavedQuery => Id != WorkspaceRows.FollowUpsQueryId;
    public int UnreadCount => _unreadCount;
    public int PendingCount => _pendingCount;
    public string Badge => (IsSavedQuery ? "" : $"{Query.TotalCount} items · ") +
        $"{UnreadCount}" + (PendingCount > 0 ? $" · {PendingCount} pending" : "");
    public string SidebarBadge => !IsSavedQuery ? Query.TotalCount.ToString() :
        PendingCount > 0 ? $"{UnreadCount} · …" : UnreadCount.ToString();
    public bool IsSidebarBadgeVisible => !IsSavedQuery || IsUnread || PendingCount > 0;
    public bool IsUnread => UnreadCount > 0;

    public QueryRow(SavedQuery query, AccountSnapshot account)
    {
        _query = query;
        Update(query, account);
    }

    public void Update(SavedQuery query, AccountSnapshot account)
    {
        var oldName = Name;
        var oldTotal = Query.TotalCount;
        SetProperty(ref _query, query, nameof(Query));
        if (oldName != Name) OnPropertyChanged(nameof(Name));
        var ids = query.ItemIds.Distinct().ToArray();
        var unread = ids.Count(id => account.Items.TryGetValue(id, out var item) &&
            ReadStatePolicy.Evaluate(item, account.User.Id, account.IgnoredAccounts) == ReadStatus.Unread);
        var pending = ids.Count(id => !account.Items.TryGetValue(id, out var item) || !item.HasBaseline);
        var wasUnread = IsUnread;
        var badgeChanged = SetProperty(ref _unreadCount, unread, nameof(UnreadCount));
        badgeChanged |= oldTotal != query.TotalCount;
        badgeChanged |= SetProperty(ref _pendingCount, pending, nameof(PendingCount));
        if (badgeChanged)
        {
            OnPropertyChanged(nameof(Badge));
            OnPropertyChanged(nameof(SidebarBadge));
            OnPropertyChanged(nameof(IsSidebarBadgeVisible));
        }
        if (wasUnread != IsUnread) OnPropertyChanged(nameof(IsUnread));
    }
}

public sealed class ActivityRow : ObservableObject
{
    private Activity _activity;
    private bool _isUnread;
    private bool _isFlagged;
    public Activity Activity => _activity;
    public string Id => Activity.Id;
    public string AutomationId => $"UnreadActivity_{Id}";
    public string Body => Activity.Body;
    public bool IsUnread => _isUnread;
    public bool IsFlagged => _isFlagged;
    public string FlagAutomationId => $"FlagComment_{Id}";
    public string ReadActionAutomationId => $"ReadComment_{Id}";
    public string ReadActionLabel => IsUnread ? "Mark comment read" : "Mark comment unread";
    public string AccessibleName => $"{Metadata}, {(IsUnread ? "unread" : "read")}" + (IsFlagged ? ", flagged for follow-up" : "");
    public string Metadata => $"{Activity.Kind} · @{Activity.AuthorLogin ?? "unknown"} · {Activity.PublishedAt.ToLocalTime():g}";
    public string Author => Activity.AuthorLogin ?? "unknown";
    public string Action => Verb(Activity.Kind);
    public string When => FriendlyTime(Activity.PublishedAt);
    public string Timestamp => Activity.PublishedAt.ToLocalTime().ToString("f");

    public static string Verb(ActivityKind kind) => kind switch
    {
        ActivityKind.InitialItem => "opened this",
        ActivityKind.Review => "reviewed",
        ActivityKind.ReviewComment => "commented on the code",
        _ => "commented"
    };

    public static string FriendlyTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today) return $"Today at {local:t}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday at {local:t}";
        return local.Year == today.Year ? local.ToString("MMM d, t") : local.ToString("MMM d, yyyy");
    }

    public static string ShortTime(DateTimeOffset? time)
    {
        if (time is not { } value) return "";
        var local = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today) return local.ToString("t");
        if (local.Date > today.AddDays(-7)) return local.ToString("ddd");
        return local.Year == today.Year ? local.ToString("MMM d") : local.ToString("d");
    }

    public ActivityRow(Activity activity, bool isUnread = false, bool isFlagged = false)
    {
        _activity = activity;
        _isUnread = isUnread;
        _isFlagged = isFlagged;
    }

    public void Update(Activity activity, bool isUnread, bool isFlagged)
    {
        var changed = SetProperty(ref _isUnread, isUnread, nameof(IsUnread));
        if (changed) OnPropertyChanged(nameof(ReadActionLabel));
        changed |= SetProperty(ref _isFlagged, isFlagged, nameof(IsFlagged));
        if (SetProperty(ref _activity, activity, nameof(Activity)))
        {
            OnPropertyChanged(nameof(Body));
            OnPropertyChanged(nameof(Metadata));
            OnPropertyChanged(nameof(Author));
            OnPropertyChanged(nameof(Action));
            OnPropertyChanged(nameof(When));
            OnPropertyChanged(nameof(Timestamp));
            changed = true;
        }
        if (changed) OnPropertyChanged(nameof(AccessibleName));
    }
}

public sealed class ItemRow : ObservableObject
{
    private TrackedItem _item;
    private string _title = "";
    private string _metadata = "";
    private string _readLabel = "";
    private string _freshness = "";
    private string _latest = "";
    private string _latestBody = "";
    private bool _isUnread;
    private bool _canMarkRead;
    private bool _canMarkUnread;
    private bool _isSelected;
    private string _kind = "";
    private string _repositoryNumber = "";
    private string _author = "";
    private string _state = "";
    private string _stateKey = "";
    private string _subtitle = "";
    private string _shortTime = "";
    private DateTimeOffset? _latestActivityAt;
    private string _unreadSummary = "";
    private string _discussionSummary = "";
    private int? _unreadCommentCount;
    private bool _isFlagged;
    private string _followUpStatus = "";
    private string _labels = "";
    private string _issueType = "";
    private string _milestone = "";
    private string _priority = "";
    private string _effort = "";
    private string _columnMetadataStatus = "";
    public string Labels => _labels;
    public string IssueType => _issueType;
    public string Milestone => _milestone;
    public string Priority => _priority;
    public string Effort => _effort;
    public string ColumnMetadataStatus => _columnMetadataStatus;
    public ObservableCollection<ActivityRow> DiscussionActivities { get; } = [];
    public ObservableCollection<ActivityRow> UnreadActivities { get; } = [];
    public string UnreadSummary => _unreadSummary;
    public string DiscussionSummary => _discussionSummary;
    public int? UnreadCommentCount => _unreadCommentCount;
    public string UnreadCountText => UnreadCommentCount?.ToString() ?? "…";
    public string UnreadCountDescription => UnreadCommentCount is { } count
        ? $"{count} unread comments and reviews; ignored accounts are excluded."
        : "Unread comment count pending — activity has not loaded yet.";
    public string UnreadCountAutomationId => $"UnreadCount_{Id}";
    public bool IsFlagged => _isFlagged;
    public bool HasFollowUps => Item.HasFollowUps;
    public string FollowUpStatus => _followUpStatus;
    public string ItemFlagLabel => IsFlagged ? "Unflag item" : "Flag item";
    public TrackedItem Item => _item;
    public string Id => Item.Id;
    public string AutomationId => $"Item_{Id}";
    public string Kind => _kind;
    public string RepositoryNumber => _repositoryNumber;
    public string Author => _author;
    public string AuthorName => Item.AuthorLogin ?? "unknown";
    public string State => _state;
    public string StateKey => _stateKey;
    public string Subtitle => _subtitle;
    public string ActivityLine => _activityLine;
    private string _activityLine = "";
    public string ShortTime => _shortTime;
    public bool ShowsUnreadCount => UnreadCommentCount is null or > 0;
    public DateTimeOffset? LatestActivityAt => _latestActivityAt;
    public string ActivityTime => LatestActivityAt?.ToLocalTime().ToString("g") ?? "—";
    public string Title => _title;
    public string Metadata => _metadata;
    public string ReadLabel => _readLabel;
    public string Freshness => _freshness;
    public string Latest => _latest;
    public string LatestBody => _latestBody;
    public bool IsUnread => _isUnread;
    public bool CanMarkRead => _canMarkRead;
    public bool CanMarkUnread => _canMarkUnread;
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (SetProperty(ref _isSelected, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }
    public string AccessibleName => $"{Title}, {ReadLabel}, {Metadata}, {UnreadCountDescription}" +
        (HasFollowUps ? ", flagged for follow-up" : "") + (IsSelected ? ", selected" : "");

    public UnreadSummarySource? CreateUnreadSummarySource() => Item.HasBaseline && UnreadActivities.Count > 0
        ? new($"{RepositoryNumber}: {Title}", Item.Url, UnreadActivities.Select(row => row.Activity).ToArray(),
            AllCommentsUnread: UnreadActivities.Count == DiscussionActivities.Count)
        : null;

    public UnreadSummarySource? CreateEntireSummarySource() => Item.HasBaseline
        ? new($"{RepositoryNumber}: {Title}", Item.Url,
            [new Activity($"{Id}:description", ActivityKind.InitialItem, Item.AuthorId, Item.AuthorLogin,
                Item.CreatedAt, Item.Body, Item.Url), .. DiscussionActivities.Select(row => row.Activity)],
            EntireDiscussion: true)
        : null;

    /// <summary>
    /// A summary describes the unread excerpt captured when generation started, not a live unread
    /// filter. Acknowledging that excerpt does not change its meaning. New discussion activity or
    /// edits/deletions within the captured excerpt do; edits to unrelated read history do not.
    /// </summary>
    public bool IsUnreadSummaryCurrent(UnreadSummarySource source, IReadOnlySet<string> knownDiscussionIds)
    {
        if (!Item.HasBaseline || source.EntireDiscussion ||
            source.Item != $"{RepositoryNumber}: {Title}" || source.Url != Item.Url)
            return false;
        var current = DiscussionActivities.ToDictionary(row => row.Id, row => row.Activity);
        return current.Keys.All(knownDiscussionIds.Contains) &&
            source.Activities.All(activity => current.TryGetValue(activity.Id, out var present) && present == activity);
    }

    public ItemRow(TrackedItem item, AccountSnapshot account)
    {
        _item = item;
        Update(item, account);
    }

    public void Update(TrackedItem item, AccountSnapshot account)
    {
        SetProperty(ref _item, item, nameof(Item));
        var columns = item.ColumnMetadata;
        var missing = item.ColumnsError is null ? "…" : "Unavailable";
        SetProperty(ref _labels, columns is null ? missing :
            columns.Labels.Count == 0 ? "—" : string.Join(", ", columns.Labels), nameof(Labels));
        SetProperty(ref _issueType, columns is null ? missing : columns.IssueType ?? "—", nameof(IssueType));
        SetProperty(ref _milestone, columns is null ? missing : columns.Milestone ?? "—", nameof(Milestone));
        SetProperty(ref _priority, ProjectColumn(columns, value => value.Priority, missing), nameof(Priority));
        SetProperty(ref _effort, ProjectColumn(columns, value => value.Effort, missing), nameof(Effort));
        SetProperty(ref _columnMetadataStatus,
            $"{Labels}\nType: {IssueType}\nMilestone: {Milestone}\nPriority: {Priority}\nEffort: {Effort}\n" +
            (item.ColumnsError ?? columns?.ProjectError ??
                (item.ColumnsCheckedAt is { } checkedAt ? $"Columns checked {checkedAt.ToLocalTime():g}" : "Column metadata not loaded.")),
            nameof(ColumnMetadataStatus));
        SetProperty(ref _kind, item.IsPullRequest ? "PR" : "Issue", nameof(Kind));
        SetProperty(ref _repositoryNumber, $"{item.Repository} #{item.Number}", nameof(RepositoryNumber));
        if (SetProperty(ref _author, $"@{item.AuthorLogin ?? "unknown"}", nameof(Author))) OnPropertyChanged(nameof(AuthorName));
        SetProperty(ref _state, item.State, nameof(State));
        SetProperty(ref _stateKey, (item.IsPullRequest ? "pr-" : "issue-") +
            (item.State.Equals("merged", StringComparison.OrdinalIgnoreCase) ? "merged" :
             item.State.Equals("closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "open"), nameof(StateKey));
        var nameChanged = SetProperty(ref _title, item.Title, nameof(Title));
        if (SetProperty(ref _isFlagged, item.IsFlagged, nameof(IsFlagged)))
            OnPropertyChanged(nameof(ItemFlagLabel));
        if (SetProperty(ref _followUpStatus, item.HasFollowUps
            ? $"{(item.IsFlagged ? "Item flagged · " : "")}{item.FlaggedActivityIds.Count} flagged comment(s). Flags stay set when read."
            : "", nameof(FollowUpStatus)))
        {
            OnPropertyChanged(nameof(HasFollowUps));
            nameChanged = true;
        }
        nameChanged |= SetProperty(ref _metadata,
            $"{(item.IsPullRequest ? "Pull request" : "Issue")} · {item.Repository} #{item.Number} · {item.State} · @{item.AuthorLogin ?? "unknown"}",
            nameof(Metadata));
        var status = ReadStatePolicy.Evaluate(item, account.User.Id, account.IgnoredAccounts);
        nameChanged |= SetProperty(ref _readLabel, status.ToString(), nameof(ReadLabel));
        SetProperty(ref _isUnread, status == ReadStatus.Unread, nameof(IsUnread));
        SetProperty(ref _canMarkRead, status == ReadStatus.Unread, nameof(CanMarkRead));
        SetProperty(ref _canMarkUnread, status == ReadStatus.Read, nameof(CanMarkUnread));
        SetProperty(ref _freshness, (item.ActivityError ?? item.MetadataError) is { } activityError ? $"Stale — {activityError}" :
            item.ActivityCheckedAt is { } time ? $"Activity checked {time.ToLocalTime():g}" :
            "Activity pending — read state unknown", nameof(Freshness));
        var latest = ReadStatePolicy.Eligible(item, account.IgnoredAccounts)
            .OrderByDescending(activity => activity.PublishedAt).FirstOrDefault();
        if (SetProperty(ref _latestActivityAt, latest?.PublishedAt, nameof(LatestActivityAt)))
            OnPropertyChanged(nameof(ActivityTime));
        SetProperty(ref _shortTime, ActivityRow.ShortTime(latest?.PublishedAt ?? item.UpdatedAt), nameof(ShortTime));
        SetProperty(ref _subtitle, $"{item.Repository} #{item.Number}" + (latest is null
            ? $" · opened by @{item.AuthorLogin ?? "unknown"}"
            : $" · @{latest.AuthorLogin ?? "unknown"} {ActivityRow.Verb(latest.Kind)}"), nameof(Subtitle));
        SetProperty(ref _activityLine, latest is null
            ? $"Opened by @{item.AuthorLogin ?? "unknown"}"
            : $"@{latest.AuthorLogin ?? "unknown"} {ActivityRow.Verb(latest.Kind)}", nameof(ActivityLine));
        SetProperty(ref _latest, latest is null ? "No relevant published activity" :
            $"{latest.Kind} · @{latest.AuthorLogin ?? "unknown"} · {latest.PublishedAt.ToLocalTime():g}", nameof(Latest));
        SetProperty(ref _latestBody, latest?.Body ?? "", nameof(LatestBody));
        var existing = DiscussionActivities.ToDictionary(row => row.Id);
        var unreadIds = ReadStatePolicy.UnreadActivity(item, account.User.Id, account.IgnoredAccounts)
            .Select(activity => activity.Id).ToHashSet();
        var discussion = ReadStatePolicy.DiscussionActivity(item, account.IgnoredAccounts)
            .Select(activity =>
            {
                if (!existing.TryGetValue(activity.Id, out var row))
                    return new ActivityRow(activity, unreadIds.Contains(activity.Id), item.FlaggedActivityIds.Contains(activity.Id));
                row.Update(activity, unreadIds.Contains(activity.Id), item.FlaggedActivityIds.Contains(activity.Id));
                return row;
            }).ToArray();
        var unread = discussion.Where(activity => activity.IsUnread).ToArray();
        WorkspaceRows.Synchronize(DiscussionActivities, discussion);
        WorkspaceRows.Synchronize(UnreadActivities, unread);
        if (SetProperty(ref _unreadCommentCount, item.HasBaseline ? unread.Length : null, nameof(UnreadCommentCount)))
        {
            OnPropertyChanged(nameof(UnreadCountText));
            OnPropertyChanged(nameof(UnreadCountDescription));
            OnPropertyChanged(nameof(ShowsUnreadCount));
            nameChanged = true;
        }
        SetProperty(ref _unreadSummary, status == ReadStatus.Unknown ? "Activity is loading; unread comments are not known yet." :
            unread.Length > 0 ? $"{unread.Length} unread comment(s) and review(s), oldest first." :
            status == ReadStatus.Unread ? (item.IsMarkedUnread ? "Manually marked unread. No unread comments or reviews." :
                "This item has not been marked read. No unread comments or reviews.") :
            discussion.Length > 0 ? "No unread comments or reviews." :
            "No unread comments or reviews.", nameof(UnreadSummary));
        SetProperty(ref _discussionSummary, status == ReadStatus.Unknown ? "Activity is loading; comments are not known yet." :
            discussion.Length > 0 ? $"All {discussion.Length} comment(s) and review(s), oldest first · {unread.Length} unread. Ignored accounts are excluded." :
            "No comments or submitted reviews from non-ignored accounts.", nameof(DiscussionSummary));
        if (nameChanged)
        {
            OnPropertyChanged(nameof(AccessibleName));
        }

    }

    private static string ProjectColumn(ItemColumnMetadata? metadata, Func<ProjectColumnValue, string?> value, string missing)
    {
        if (metadata is null) return missing;
        if (!metadata.IncludesProjects) return metadata.ProjectError is null ? "Project access required" : "Unavailable";
        var values = metadata.Projects.Where(project => !string.IsNullOrWhiteSpace(value(project)))
            .OrderBy(project => project.Project, StringComparer.CurrentCultureIgnoreCase)
            .Select(project => metadata.Projects.Count > 1 ? $"{project.Project}: {value(project)}" : value(project));
        return string.Join("; ", values) is { Length: > 0 } text ? text : "—";
    }
}

public sealed class DiscussionPreview
{
    private ItemRow? _item;
    public ObservableCollection<ActivityRow> Activities { get; } = [];

    public void Update(ItemRow? item)
    {
        var reset = !ReferenceEquals(_item, item);
        _item = item;
        var available = item?.DiscussionActivities.ToDictionary(row => row.Id) ?? [];
        var retained = reset ? [] : Activities.Where(row => available.ContainsKey(row.Id)).Select(row => available[row.Id]).ToArray();
        var retainedIds = retained.Select(row => row.Id).ToHashSet();
        var added = item?.DiscussionActivities ?? [];
        WorkspaceRows.Synchronize(Activities, retained.Concat(added.Where(row => !retainedIds.Contains(row.Id))).ToArray());
    }

    public ItemReadSnapshot? ReadThrough(int index)
    {
        if (_item is null || index < 0) return null;
        var ids = Activities.Take(index + 1).Where(row => row.IsUnread).Select(row => row.Id).ToArray();
        return ids.Length == 0 ? null : new(_item.Id, ids);
    }
}

public sealed class WorkspaceRows
{
    public const string FollowUpsQueryId = WorkspaceService.FollowUpsQueryId;
    public ObservableCollection<QueryRow> Queries { get; } = [];
    public ObservableCollection<ItemRow> Items { get; } = [];
    public ObservableCollection<ItemRow> SelectedItems { get; } = [];

    public void UpdateSelection(IEnumerable<ItemRow> selected)
    {
        var ids = selected.Select(row => row.Id).ToHashSet();
        ids.IntersectWith(Items.Select(row => row.Id));
        foreach (var row in SelectedItems.Where(row => !ids.Contains(row.Id))) row.IsSelected = false;
        foreach (var row in Items) row.IsSelected = ids.Contains(row.Id);
        Synchronize(SelectedItems, Items.Where(row => ids.Contains(row.Id)).ToArray());
    }

    public void UpdateQueries(AccountSnapshot account)
    {
        var existing = Queries.ToDictionary(row => row.Id);
        var desired = new List<QueryRow>();
        var flagged = account.Items.Values.Where(item => item.HasFollowUps).Select(item => item.Id).ToList();
        var followUps = new SavedQuery { Id = FollowUpsQueryId, Name = "Follow-ups", ItemIds = flagged, TotalCount = flagged.Count };
        foreach (var query in account.Queries.Append(followUps))
        {
            if (existing.TryGetValue(query.Id, out var row))
                row.Update(query, account);
            else
                row = new QueryRow(query, account);
            desired.Add(row);
        }
        Synchronize(Queries, desired);
    }

    public void UpdateItems(SavedQuery? query, AccountSnapshot? account, string? filter = null)
    {
        filter = filter?.Trim();
        var existing = Items.ToDictionary(row => row.Id);
        var desired = new List<ItemRow>();
        if (query is not null && account is not null)
        {
            foreach (var id in query.ItemIds.Distinct())
            {
                if (!account.Items.TryGetValue(id, out var item)) continue;
                if (existing.TryGetValue(id, out var row))
                    row.Update(item, account);
                else
                    row = new ItemRow(item, account);
                if (!string.IsNullOrEmpty(filter) && !Matches(row, filter)) continue;
                desired.Add(row);
            }
        }
        Synchronize(Items, Order(desired));
        UpdateSelection(SelectedItems.ToArray());
    }

    public const string DefaultSortKey = "Updated";
    public string SortKey { get; private set; } = DefaultSortKey;
    public bool SortDescending { get; private set; } = true;

    /// <summary>Changes the sort and reorders the current rows in place, preserving instances and selection.</summary>
    public void SetSort(string key, bool descending)
    {
        SortKey = key;
        SortDescending = descending;
        Synchronize(Items, Order(Items.ToArray()));
    }

    private ItemRow[] Order(IReadOnlyList<ItemRow> rows)
    {
        var position = new Dictionary<ItemRow, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < rows.Count; i++) position[rows[i]] = i;
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<ItemRow> ordered = SortKey switch
        {
            "Title" => By(rows, row => row.Title, comparer),
            "Unread" => By(rows, row => row.UnreadCommentCount ?? -1, Comparer<int>.Default),
            "Repository" => By(rows, row => row.Item.Repository, comparer).ThenBy(row => row.Item.Number),
            "Author" => By(rows, row => row.Author, comparer),
            "Labels" => By(rows, row => row.Labels, comparer),
            "IssueType" => By(rows, row => row.IssueType, comparer),
            "Milestone" => By(rows, row => row.Milestone, comparer),
            "Priority" => By(rows, row => row.Priority, comparer),
            "Effort" => By(rows, row => row.Effort, comparer),
            "State" => By(rows, row => row.StateKey, comparer),
            "Flag" => By(rows, row => row.HasFollowUps ? 1 : 0, Comparer<int>.Default),
            // Items without relevant activity always follow dated items.
            _ => rows.OrderBy(row => row.LatestActivityAt is null ? 1 : 0)
                .ThenBy(row => row.LatestActivityAt ?? default, SortDescending ? Comparer<DateTimeOffset>.Create((a, b) => b.CompareTo(a)) : Comparer<DateTimeOffset>.Default),
        };
        // Ties: most recent activity first, then saved-query order.
        return ordered.ThenBy(row => row.LatestActivityAt is null ? 1 : 0)
            .ThenByDescending(row => row.LatestActivityAt ?? default)
            .ThenBy(row => position[row]).ToArray();
    }

    private IOrderedEnumerable<ItemRow> By<TKey>(IEnumerable<ItemRow> rows, Func<ItemRow, TKey> key, IComparer<TKey> comparer) =>
        SortDescending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    private static bool Matches(ItemRow row, string filter) =>
        row.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        row.RepositoryNumber.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        row.Author.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        row.Labels.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        row.IssueType.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        row.Milestone.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        row.Priority.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        row.Effort.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        row.Subtitle.Contains(filter, StringComparison.CurrentCultureIgnoreCase);


    internal static void Synchronize<T>(ObservableCollection<T> rows, IReadOnlyList<T> desired) where T : class
    {
        var retained = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var index = rows.Count - 1; index >= 0; index--)
            if (!retained.Contains(rows[index])) rows.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < rows.Count && ReferenceEquals(rows[index], desired[index])) continue;
            var oldIndex = rows.IndexOf(desired[index]);
            if (oldIndex >= 0) rows.Move(oldIndex, index);
            else rows.Insert(index, desired[index]);
        }
    }
}
