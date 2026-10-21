using System.Net;
using System.Text;
using GitHubToDo.Core;
using Microsoft.Data.Sqlite;

namespace GitHubToDo.Core.Tests;

internal sealed class TestDatabase : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "GitHubToDoTests", Guid.NewGuid().ToString("N"));
    public string PathName => Path.Combine(directory, "test.db");
    public LocalStore Store { get; }
    public TestDatabase() => Store = new(PathName);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}

internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, string? next = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("ETag", "\"test\"");
        if (next is not null)
            response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
        return response;
    }
}

internal sealed class FakeClient : IGitHubClient, IActivityMetadataClient, IItemColumnClient
{
    public GitHubUser User { get; set; } = new("1", "viewer");
    public Func<string, CancellationToken, Task<SearchResult>>? Search { get; set; }
    public Func<TrackedItem, CancellationToken, Task<IReadOnlyList<Activity>>>? Activity { get; set; }
    public Func<TrackedItem, ActivityMetadata>? Metadata { get; set; }
    public int ActivityCalls { get; private set; }
    public int MetadataCalls { get; private set; }
    public Func<TrackedItem, bool, BatchResult<ItemColumnMetadata>>? Columns { get; set; }
    public List<string[]> ColumnBatches { get; } = [];
    public Task<IReadOnlyDictionary<string, BatchResult<ItemColumnMetadata>>> GetColumnMetadataBatchAsync(
        IReadOnlyList<TrackedItem> items, bool includeProjects, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ColumnBatches.Add(items.Select(item => item.Id).ToArray());
        return Task.FromResult<IReadOnlyDictionary<string, BatchResult<ItemColumnMetadata>>>(items.ToDictionary(
            item => item.Id, item => Columns?.Invoke(item, includeProjects) ??
                new BatchResult<ItemColumnMetadata>(new(["bug"], "Bug", "v1", [], includeProjects), null)));
    }
    public Task<IReadOnlyDictionary<string, BatchResult<ActivityMetadata>>> GetActivityMetadataBatchAsync(
        IReadOnlyList<TrackedItem> items, CancellationToken cancellationToken)
    {
        MetadataCalls += items.Count;
        return Task.FromResult<IReadOnlyDictionary<string, BatchResult<ActivityMetadata>>>(items.ToDictionary(
            item => item.Id, item => new BatchResult<ActivityMetadata>(Metadata?.Invoke(item) ?? new(item.UpdatedAt,
                item.Activities.Count(activity => activity.Kind == ActivityKind.Comment),
                item.Activities.Count(activity => activity.Kind == ActivityKind.Review),
                item.Activities.Count(activity => activity.Kind is ActivityKind.Comment or ActivityKind.ReviewComment) +
                item.Activities.Count(activity => activity.Kind == ActivityKind.Review &&
                    !string.IsNullOrWhiteSpace(activity.Body[(activity.Body.IndexOf(':') + 1)..]))), null)));
    }
    public Task<GitHubUser> GetUserAsync(CancellationToken cancellationToken) => Task.FromResult(User);
    public Task<GitHubUser> GetUserAsync(string login, CancellationToken cancellationToken) => Task.FromResult(new GitHubUser(login, login));
    public Task<SearchResult> SearchAsync(string expression, string login, CancellationToken cancellationToken) =>
        Search?.Invoke(expression, cancellationToken) ?? Task.FromResult(new SearchResult([NewItem()], 1, false));
    public Task<IReadOnlyList<Activity>> GetActivityAsync(TrackedItem item, CancellationToken cancellationToken)
    {
        ActivityCalls++;
        return Activity?.Invoke(item, cancellationToken) ??
            Task.FromResult<IReadOnlyList<Activity>>([new("comment:1", ActivityKind.Comment, "other", "other",
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "Hello", "https://github.com/o/r/issues/1#comment")]);
    }
    public static TrackedItem NewItem() => new()
    {
        Id = "item", Repository = "o/r", Number = 1, Title = "Test issue", AuthorId = "other",
        AuthorLogin = "other", CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        Url = "https://github.com/o/r/issues/1"
    };
}
