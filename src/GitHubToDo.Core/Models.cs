namespace GitHubToDo.Core;

public sealed record GitHubUser(string Id, string Login);

public enum ActivityKind { InitialItem, Comment, ReviewComment, Review }
public enum ReadStatus { Unknown, Read, Unread }

public sealed record Activity(
    string Id, ActivityKind Kind, string? AuthorId, string? AuthorLogin,
    DateTimeOffset PublishedAt, string Body, string Url);

public sealed class TrackedItem
{
    public string Id { get; set; } = "";
    public string Repository { get; set; } = "";
    public int Number { get; set; }
    public bool IsPullRequest { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Url { get; set; } = "";
    public string State { get; set; } = "";
    public string? AuthorId { get; set; }
    public string? AuthorLogin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<Activity> Activities { get; set; } = [];
    public HashSet<string> AcknowledgedIds { get; set; } = [];
    public bool IsMarkedUnread { get; set; }
    public HashSet<string> ManuallyUnreadIds { get; set; } = [];
    public bool IsFlagged { get; set; }
    public HashSet<string> FlaggedActivityIds { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasFollowUps => IsFlagged || FlaggedActivityIds.Count > 0;
    public bool HasBaseline { get; set; }
    public DateTimeOffset? ActivityCheckedAt { get; set; }
    public string? ActivityError { get; set; }
}

public sealed class SavedQuery
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "";
    public List<string> ItemIds { get; set; } = [];
    public int TotalCount { get; set; }
    public bool IsIncomplete { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }
    public DateTimeOffset? LastFullRefreshedAt { get; set; }
    public string? Error { get; set; }

    public static List<SavedQuery> Defaults() =>
    [
        new() { Name = "Issues involving me", Expression = "is:issue state:open archived:false involves:@me sort:updated-desc" },
        new() { Name = "My pull requests", Expression = "is:pr author:@me state:open sort:updated-desc" },
        new() { Name = "Review requested", Expression = "is:pr review-requested:@me state:open archived:false sort:updated-desc" }
    ];
}

public sealed record IgnoredAccount(string Login, string? Id = null);

public sealed class AccountSnapshot
{
    public GitHubUser User { get; set; } = new("", "");
    public List<SavedQuery> Queries { get; set; } = SavedQuery.Defaults();
    public Dictionary<string, TrackedItem> Items { get; set; } = [];
    public List<IgnoredAccount> IgnoredAccounts { get; set; } = [];
}

public sealed record OAuthToken(
    string AccessToken, string? RefreshToken = null,
    DateTimeOffset? ExpiresAt = null, DateTimeOffset? RefreshExpiresAt = null);

public sealed record DeviceCode(string Code, string UserCode, Uri VerificationUri, int ExpiresIn, int Interval);
public sealed record SearchResult(IReadOnlyList<TrackedItem> Items, int TotalCount, bool IsIncomplete);
public sealed record ItemReadSnapshot(string ItemId, IReadOnlyList<string> ActivityIds);
public sealed record BatchResult<T>(T? Value, Exception? Error) where T : class;
public sealed record HttpCacheEntry(string? ETag, string? LastModified, string Body, string? Link);

public static class GitHubBatching
{
    public const int SearchesPerRequest = 8;
    public const int ItemsPerRequest = 10;
}

public interface ICredentialStore
{
    OAuthToken? Load();
    void Save(OAuthToken token);
    void Clear();
}

public interface IGitHubClient
{
    Task<GitHubUser> GetUserAsync(CancellationToken cancellationToken);
    Task<GitHubUser> GetUserAsync(string login, CancellationToken cancellationToken);
    Task<SearchResult> SearchAsync(string expression, string login, CancellationToken cancellationToken);
    Task<IReadOnlyList<Activity>> GetActivityAsync(TrackedItem item, CancellationToken cancellationToken);

    /// <summary>One cheap request for the first <paramref name="count"/> results and the total match count.</summary>
    async Task<SearchResult> PreviewSearchAsync(string expression, string login, int count, CancellationToken cancellationToken)
    {
        var result = await SearchAsync(expression, login, cancellationToken);
        return new(result.Items.Take(count).ToArray(), result.TotalCount, result.IsIncomplete);
    }

    async Task<IReadOnlyDictionary<string, BatchResult<SearchResult>>> SearchBatchAsync(
        IReadOnlyList<SavedQuery> queries, string login, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, BatchResult<SearchResult>>();
        foreach (var query in queries)
        {
            try { results[query.Id] = new(await SearchAsync(query.Expression, login, cancellationToken), null); }
            catch (Exception exception) when (BatchErrors.IsRecoverable(exception, cancellationToken))
            { results[query.Id] = new(null, exception); }
        }
        return results;
    }

    async Task<IReadOnlyDictionary<string, BatchResult<IReadOnlyList<Activity>>>> GetActivityBatchAsync(
        IReadOnlyList<TrackedItem> items, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, BatchResult<IReadOnlyList<Activity>>>();
        foreach (var item in items)
        {
            try { results[item.Id] = new(await GetActivityAsync(item, cancellationToken), null); }
            catch (Exception exception) when (BatchErrors.IsRecoverable(exception, cancellationToken))
            { results[item.Id] = new(null, exception); }
        }
        return results;
    }
}

internal static class BatchErrors
{
    public static bool IsRecoverable(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is GitHubException or HttpRequestException or TaskCanceledException;
}

public sealed class GitHubException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
