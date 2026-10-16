using System.Net.Http.Json;
using System.Text.Json;
using static GitHubToDo.Core.GitHubHttp;

namespace GitHubToDo.Core;

public sealed class GraphQlGitHubClient(
    HttpClient http, Func<CancellationToken, Task<string>> accessToken, LocalStore store) : IGitHubClient, IActivityMetadataClient
{
    private const string AuthorFields = "author { login ... on User { databaseId } ... on Bot { databaseId } }";
    private const string CommentFields = "id databaseId fullDatabaseId createdAt updatedAt body url " + AuthorFields;
    private const string InlineFields = CommentFields + " state publishedAt";
    private const string PageFields = "pageInfo { hasNextPage endCursor }";
    private const string ItemFields = "id number title body url createdAt updatedAt repository { nameWithOwner } " + AuthorFields;
    private const string MetadataFields = "updatedAt comments { totalCount }";
    private const string ReviewMetadataFields =
        "totalCommentsCount reviews(last: 1, states: [APPROVED, CHANGES_REQUESTED, COMMENTED, DISMISSED]) " +
        "{ totalCount nodes { databaseId fullDatabaseId updatedAt } }";
    private const string SearchFields = "nodes { __typename ... on Issue { " + ItemFields +
        " comments { totalCount } issueState: state } ... on PullRequest { " + ItemFields +
        " comments { totalCount } " + ReviewMetadataFields + " prState: state } }";
    private readonly GitHubClient identities = new(http, accessToken, store);
    private readonly SemaphoreSlim requestGate = new(1);
    private DateTimeOffset notBefore;

    public Task<GitHubUser> GetUserAsync(CancellationToken cancellationToken) => identities.GetUserAsync(cancellationToken);
    public Task<GitHubUser> GetUserAsync(string login, CancellationToken cancellationToken) => identities.GetUserAsync(login, cancellationToken);

    public async Task<SearchResult> SearchAsync(string expression, string login, CancellationToken cancellationToken)
    {
        var query = new SavedQuery { Expression = expression };
        var result = (await SearchBatchAsync([query], login, cancellationToken))[query.Id];
        if (result.Error is { } error) throw error;
        return result.Value!;
    }

    public async Task<SearchResult> PreviewSearchAsync(string expression, string login, int count, CancellationToken cancellationToken)
    {
        var query = new SavedQuery { Expression = expression };
        var result = (await SearchBatchAsync([query], login, cancellationToken, Math.Clamp(count, 1, 100)))[query.Id];
        if (result.Error is { } error) throw error;
        return result.Value!;
    }

    public async Task<IReadOnlyList<Activity>> GetActivityAsync(TrackedItem item, CancellationToken cancellationToken)
    {
        var result = (await GetActivityBatchAsync([item], cancellationToken))[item.Id];
        if (result.Error is { } error) throw error;
        return result.Value!;
    }

    public Task<IReadOnlyDictionary<string, BatchResult<SearchResult>>> SearchBatchAsync(
        IReadOnlyList<SavedQuery> queries, string login, CancellationToken cancellationToken) =>
        SearchBatchAsync(queries, login, cancellationToken, 1000);

    private async Task<IReadOnlyDictionary<string, BatchResult<SearchResult>>> SearchBatchAsync(
        IReadOnlyList<SavedQuery> queries, string login, CancellationToken cancellationToken, int limit)
    {
        var results = new Dictionary<string, BatchResult<SearchResult>>();
        foreach (var batch in queries.Chunk(GitHubBatching.SearchesPerRequest))
        {
            var work = batch.Select(query => new SearchWork(query)).ToArray();
            foreach (var job in work)
            {
                try
                {
                    var parsed = QueryParser.Parse(job.Query.Expression, login);
                    job.Expression = parsed.Search + (parsed.Sort is null ? "" : $" sort:{parsed.Sort}-{parsed.Order}");
                }
                catch (ArgumentException exception) { job.Error = new GitHubException(exception.Message, 422); }
            }
            while (work.Any(job => job.Error is null && !job.Page.Done))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new GraphRequest();
                foreach (var job in work.Where(job => job.Error is null && !job.Page.Done))
                {
                    var index = request.Parts.Count;
                    var query = request.Variable($"q{index}", "String!", job.Expression);
                    var after = request.Variable($"a{index}", "String", job.Page.Cursor);
                    request.Add($"s{index}", $"search(query: {query}, type: ISSUE_ADVANCED, first: {Math.Min(100, limit)}, after: {after}) " +
                        "{ issueCount " + PageFields + " " + SearchFields + " }", job, connection =>
                    {
                        job.Total = connection.GetProperty("issueCount").GetInt32();
                        foreach (var node in Nodes(connection))
                            job.Items.TryAdd(node.GetProperty("id").GetString()!, ParseItem(node));
                        job.Page.Advance(connection);
                        if (job.Items.Count >= limit) job.Page.Done = true;
                    });
                }
                await ExecuteAsync(request, cancellationToken);
            }
            foreach (var job in work)
                results[job.Query.Id] = new(job.Error is null ?
                    new(job.Items.Values.Take(limit).ToArray(), job.Total, job.Items.Count < Math.Min(job.Total, limit)) : null, job.Error);
        }
        return results;
    }

    public async Task<IReadOnlyDictionary<string, BatchResult<IReadOnlyList<Activity>>>> GetActivityBatchAsync(
        IReadOnlyList<TrackedItem> items, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, BatchResult<IReadOnlyList<Activity>>>();
        foreach (var batch in items.DistinctBy(item => item.Id).Chunk(GitHubBatching.ItemsPerRequest))
        {
            var work = batch.Select(item => new ActivityWork(item)).ToArray();
            while (work.Any(job => job.Error is null && !job.Complete))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new GraphRequest();
                foreach (var job in work.Where(job => job.Error is null && !job.Complete))
                {
                    if ((!job.Comments.Done || !job.Reviews.Done) && request.Parts.Count < GitHubBatching.ItemsPerRequest)
                        AddItemRequest(request, job);
                    foreach (var review in job.CommentPages.Where(page => !page.Page.Done))
                    {
                        if (request.Parts.Count == GitHubBatching.ItemsPerRequest) break;
                        var index = request.Parts.Count;
                        var id = request.Variable($"id{index}", "ID!", review.Id);
                        var after = request.Variable($"a{index}", "String", review.Page.Cursor);
                        request.Add($"n{index}", $"node(id: {id}) {{ __typename ... on PullRequestReview {{ " +
                            $"comments(first: 20, after: {after}) {{ {PageFields} nodes {{ {InlineFields} }} }} }} }}",
                            job, node =>
                            {
                                RequireType(node, "PullRequestReview");
                                ReadInline(job, review, node.GetProperty("comments"));
                            });
                    }
                }
                await ExecuteAsync(request, cancellationToken);
            }
            foreach (var job in work)
                results[job.Item.Id] = new(job.Error is null ? job.Activities.Values.ToArray() : null, job.Error);
        }
        return results;
    }

    public async Task<IReadOnlyDictionary<string, BatchResult<ActivityMetadata>>> GetActivityMetadataBatchAsync(
        IReadOnlyList<TrackedItem> items, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, BatchResult<ActivityMetadata>>();
        foreach (var batch in items.DistinctBy(item => item.Id).Chunk(GitHubBatching.MetadataPerRequest))
        {
            var request = new GraphRequest();
            var work = new List<MetadataWork>();
            foreach (var item in batch)
            {
                var job = new MetadataWork(item);
                work.Add(job);
                var index = request.Parts.Count;
                var id = request.Variable($"id{index}", "ID!", item.Id);
                var type = item.IsPullRequest ? "PullRequest" : "Issue";
                request.Add($"n{index}", $"node(id: {id}) {{ __typename ... on {type} {{ {MetadataFields} " +
                    (item.IsPullRequest ? ReviewMetadataFields : "") + " } }", job, node =>
                    {
                        RequireType(node, type);
                        job.Metadata = ParseMetadata(node, item.IsPullRequest);
                    });
            }
            await ExecuteAsync(request, cancellationToken);
            foreach (var job in work)
                results[job.Item.Id] = new(job.Error is null ? job.Metadata : null, job.Error);
        }
        return results;
    }

    private static void AddItemRequest(GraphRequest request, ActivityWork job)
    {
        var index = request.Parts.Count;
        var id = request.Variable($"id{index}", "ID!", job.Item.Id);
        var comments = "";
        if (!job.Comments.Done)
        {
            var after = request.Variable($"c{index}", "String", job.Comments.Cursor);
            var order = job.IncrementalComments ? "orderBy: {field: UPDATED_AT, direction: DESC}, " : "";
            comments = $"comments(first: 50, {order}after: {after}) {{ {PageFields} nodes {{ {CommentFields} }} }}";
        }
        var reviews = "";
        if (!job.Reviews.Done)
        {
            var after = request.Variable($"r{index}", "String", job.Reviews.Cursor);
            reviews = $"reviews(first: 20, after: {after}) {{ {PageFields} nodes {{ " +
                $"id databaseId fullDatabaseId state submittedAt body url {AuthorFields} " +
                $"comments(first: 20) {{ {PageFields} nodes {{ {InlineFields} }} }} }} }}";
        }
        var type = job.Item.IsPullRequest ? "PullRequest" : "Issue";
        request.Add($"n{index}", $"node(id: {id}) {{ __typename ... on {type} {{ {comments} {reviews} }} }}", job, node =>
        {
            RequireType(node, job.Item.IsPullRequest ? "PullRequest" : "Issue");
            if (!job.Comments.Done)
            {
                var connection = node.GetProperty("comments");
                foreach (var comment in Nodes(connection))
                    AddActivity(job, comment, ActivityKind.Comment, "comment", Date(comment, "createdAt"));
                if (job.IncrementalComments && Nodes(connection).Any(comment =>
                    Date(comment, "updatedAt") < job.Item.ActivityCheckedAt!.Value.AddMinutes(-5)))
                    job.Comments.Done = true;
                else
                    job.Comments.Advance(connection);
                if (job.IncrementalComments && job.Comments.Done &&
                    job.Activities.Values.Count(activity => activity.Kind == ActivityKind.Comment) != job.Item.ActivityMetadata!.CommentCount)
                {
                    // A deletion or a concurrent change invalidates an append-only merge; reconcile this stream.
                    job.IncrementalComments = false;
                    job.Comments = new();
                    foreach (var activity in job.Activities.Values.Where(activity => activity.Kind == ActivityKind.Comment).ToArray())
                        job.Activities.Remove(activity.Id);
                }
            }
            if (!job.Reviews.Done)
            {
                var connection = node.GetProperty("reviews");
                foreach (var review in Nodes(connection))
                {
                    if (Text(review, "state") == "PENDING" || OptionalDate(review, "submittedAt") is not { } submitted)
                        continue;
                    AddActivity(job, review, ActivityKind.Review, "review", submitted);
                    var page = new ReviewComments(review.GetProperty("id").GetString()!, submitted);
                    ReadInline(job, page, review.GetProperty("comments"));
                    if (!page.Page.Done) job.CommentPages.Add(page);
                }
                job.Reviews.Advance(connection);
            }
        });
    }

    private static void ReadInline(ActivityWork job, ReviewComments review, JsonElement connection)
    {
        foreach (var comment in Nodes(connection))
        {
            if (Text(comment, "state") == "PENDING") continue;
            var published = OptionalDate(comment, "publishedAt") ?? Date(comment, "createdAt");
            if (review.SubmittedAt > published) published = review.SubmittedAt;
            AddActivity(job, comment, ActivityKind.ReviewComment, "inline", published);
        }
        review.Page.Advance(connection);
    }

    private async Task ExecuteAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await SendAsync(request, cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new GitHubException("GitHub returned an invalid GraphQL response.");
            if (root.TryGetProperty("errors", out var value) && value.ValueKind != JsonValueKind.Array)
                throw new GitHubException("GitHub returned an invalid GraphQL error response.");
            var errors = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
            if (errors.Any(error => error.ValueKind != JsonValueKind.Object))
                throw new GitHubException("GitHub returned an invalid GraphQL error response.");
            foreach (var part in request.Parts)
            {
                if (part.Owner.Error is not null) continue;
                var error = errors.FirstOrDefault(error => !error.TryGetProperty("path", out var path) ||
                    path.ValueKind != JsonValueKind.Array || path.GetArrayLength() == 0 ||
                    path[0].ValueKind != JsonValueKind.String || path[0].GetString() == part.Alias);
                if (error.ValueKind != JsonValueKind.Undefined)
                {
                    part.Owner.Error = GraphError(error);
                    continue;
                }
                try
                {
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                        !data.TryGetProperty(part.Alias, out var result) || result.ValueKind != JsonValueKind.Object)
                        throw new GitHubException("GitHub returned a missing or inaccessible result. Cached data has been retained.", 404);
                    part.Accept(result);
                }
                catch (Exception exception) when (exception is GitHubException or JsonException or
                    KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    part.Owner.Error = exception is GitHubException ? exception :
                        new GitHubException("GitHub returned invalid or incomplete data. " + exception.Message);
                }
            }
        }
        catch (Exception exception) when (BatchErrors.IsRecoverable(exception, cancellationToken))
        {
            foreach (var part in request.Parts) part.Owner.Error ??= exception;
        }
    }

    private async Task<JsonDocument> SendAsync(GraphRequest graph, CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < notBefore)
                throw new GitHubException($"GitHub rate limit: retry after {notBefore.ToLocalTime():t}.", 429);
            var token = await accessToken(cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql");
                request.Headers.Authorization = new("Bearer", token);
                request.Headers.UserAgent.ParseAdd("GitHubToDo/1.0");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                request.Content = JsonContent.Create(new GraphQlPayload(graph.Document, graph.Variables), CoreJsonContext.Default.GraphQlPayload);
                using var response = await http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    if (status >= 500 && attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
                        continue;
                    }
                    if (status == 429 || status == 403 && (response.Headers.RetryAfter is not null ||
                        Header(response, "X-RateLimit-Remaining") == "0" || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
                    {
                        notBefore = RetryAt(response);
                        throw new GitHubException($"GitHub rate limit: retry after {notBefore.ToLocalTime():t}.", 429);
                    }
                    var hint = status switch
                    {
                        401 => "Sign in again. ",
                        403 => "Check repository permissions and organization OAuth/SSO approval. ",
                        404 => "The item is missing, deleted, or inaccessible. ",
                        422 => "Check the query syntax and repository access. ",
                        _ => ""
                    };
                    throw new GitHubException(hint + ErrorMessage(body, response.ReasonPhrase ?? "GitHub request failed."), status);
                }
                JsonDocument document;
                try { document = JsonDocument.Parse(body); }
                catch (JsonException exception) { throw new GitHubException("GitHub returned invalid JSON. " + exception.Message); }
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    throw new GitHubException("GitHub returned an invalid GraphQL response.");
                }
                if (Header(response, "X-RateLimit-Remaining") == "0")
                    notBefore = RetryAt(response);
                if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("rateLimit", out var rate) && rate.ValueKind == JsonValueKind.Object &&
                    rate.TryGetProperty("remaining", out var remaining) && remaining.GetInt32() == 0 &&
                    OptionalDate(rate, "resetAt") is { } reset)
                    notBefore = reset.AddSeconds(1);
                return document;
            }
        }
        finally { requestGate.Release(); }
    }

    private GitHubException GraphError(JsonElement error)
    {
        var type = Text(error, "type");
        if (error.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object)
            type ??= Text(extensions, "code");
        var message = Text(error, "message") ?? "GitHub rejected the GraphQL request.";
        if (type == "RATE_LIMITED" || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            if (notBefore < DateTimeOffset.UtcNow) notBefore = DateTimeOffset.UtcNow.AddMinutes(1);
            return new($"GitHub rate limit: retry after {notBefore.ToLocalTime():t}. {message}", 429);
        }
        return new(message, type switch
        {
            "UNAUTHORIZED" => 401,
            "FORBIDDEN" or "INSUFFICIENT_SCOPES" => 403,
            "NOT_FOUND" => 404,
            _ => 422
        });
    }

    private static TrackedItem ParseItem(JsonElement node)
    {
        var type = Text(node, "__typename");
        if (type is not ("Issue" or "PullRequest"))
            throw new GitHubException("GitHub returned an unsupported search result; cached membership has been retained.");
        var author = Author(node);
        return new()
        {
            Id = node.GetProperty("id").GetString()!,
            Repository = node.GetProperty("repository").GetProperty("nameWithOwner").GetString()!,
            Number = node.GetProperty("number").GetInt32(),
            IsPullRequest = type == "PullRequest",
            Title = node.GetProperty("title").GetString()!,
            Body = node.GetProperty("body").GetString()!,
            Url = node.GetProperty("url").GetString()!,
            State = node.GetProperty(type == "Issue" ? "issueState" : "prState").GetString()!.ToLowerInvariant(),
            AuthorId = author.Id,
            AuthorLogin = author.Login,
            CreatedAt = Date(node, "createdAt"),
            UpdatedAt = Date(node, "updatedAt"),
            ActivityMetadata = ParseMetadata(node, type == "PullRequest")
        };
    }

    private static ActivityMetadata ParseMetadata(JsonElement node, bool isPullRequest)
    {
        var comments = node.GetProperty("comments").GetProperty("totalCount").GetInt32();
        var latestReview = isPullRequest ? Nodes(node.GetProperty("reviews")).LastOrDefault() : default;
        var reviewId = latestReview.ValueKind == JsonValueKind.Object
            ? LegacyId(latestReview, "fullDatabaseId") ?? LegacyId(latestReview, "databaseId") : null;
        return new(Date(node, "updatedAt"), comments,
            isPullRequest ? node.GetProperty("reviews").GetProperty("totalCount").GetInt32() : 0,
            isPullRequest ? node.GetProperty("totalCommentsCount").GetInt32() : comments,
            reviewId is null ? null : $"review:{reviewId}",
            latestReview.ValueKind == JsonValueKind.Object ? Date(latestReview, "updatedAt") : null);
    }

    private static void AddActivity(ActivityWork job, JsonElement node, ActivityKind kind, string prefix, DateTimeOffset time)
    {
        // Keep REST numeric activity IDs so existing acknowledgements survive the API switch.
        var id = LegacyId(node, "fullDatabaseId") ?? LegacyId(node, "databaseId") ??
            throw new GitHubException("GitHub omitted the activity's numeric identity. Cached read state has been retained.");
        var author = Author(node);
        var body = node.GetProperty("body").GetString()!;
        if (kind == ActivityKind.Review) body = $"{Text(node, "state")}: {body}";
        var activity = new Activity($"{prefix}:{id}", kind, author.Id, author.Login, time, body, node.GetProperty("url").GetString()!);
        job.Activities[activity.Id] = activity;
    }

    private static (string? Id, string? Login) Author(JsonElement node)
    {
        if (!node.TryGetProperty("author", out var author) || author.ValueKind == JsonValueKind.Null) return (null, null);
        return (LegacyId(author, "databaseId"), Text(author, "login"));
    }

    private static string? LegacyId(JsonElement node, string name) => node.TryGetProperty(name, out var value) ?
        value.ValueKind switch { JsonValueKind.Number => value.GetRawText(), JsonValueKind.String => value.GetString(), _ => null } : null;
    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset Date(JsonElement node, string name) => node.GetProperty(name).GetDateTimeOffset();
    private static DateTimeOffset? OptionalDate(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetDateTimeOffset() : null;
    private static IEnumerable<JsonElement> Nodes(JsonElement connection) =>
        connection.GetProperty("nodes").EnumerateArray().Select(node => node.ValueKind == JsonValueKind.Object ? node :
            throw new GitHubException("GitHub returned an inaccessible activity or search result. Cached data has been retained."));
    private static void RequireType(JsonElement node, string expected)
    {
        if (Text(node, "__typename") != expected)
            throw new GitHubException("GitHub returned an unexpected or inaccessible item. Activity is not complete.");
    }
    private abstract class Work
    {
        public Exception? Error { get; set; }
    }

    private sealed class SearchWork(SavedQuery query) : Work
    {
        public SavedQuery Query { get; } = query;
        public string Expression { get; set; } = "";
        public Page Page { get; } = new();
        public Dictionary<string, TrackedItem> Items { get; } = [];
        public int Total { get; set; }
    }

    private sealed class MetadataWork(TrackedItem item) : Work
    {
        public TrackedItem Item { get; } = item;
        public ActivityMetadata? Metadata { get; set; }
    }

    private sealed class ActivityWork : Work
    {
        public TrackedItem Item { get; }
        public bool IncrementalComments { get; set; }
        public Page Comments { get; set; } = new();
        public Page Reviews { get; }
        public List<ReviewComments> CommentPages { get; } = [];
        public Dictionary<string, Activity> Activities { get; } = [];
        public bool Complete => Comments.Done && Reviews.Done && CommentPages.All(review => review.Page.Done);

        public ActivityWork(TrackedItem item)
        {
            Item = item;
            Reviews = new() { Done = !item.IsPullRequest };
            IncrementalComments = item.HasBaseline && item.ActivityCheckedAt is not null &&
                item.ActivityMetadata is { } metadata &&
                metadata.CommentCount >= item.Activities.Count(activity => activity.Kind == ActivityKind.Comment);
            if (IncrementalComments)
                foreach (var activity in item.Activities.Where(activity => activity.Kind == ActivityKind.Comment))
                    Activities.Add(activity.Id, activity);
        }
    }

    private sealed class ReviewComments(string id, DateTimeOffset submittedAt)
    {
        public string Id { get; } = id;
        public DateTimeOffset SubmittedAt { get; } = submittedAt;
        public Page Page { get; } = new();
    }

    private sealed class Page
    {
        private readonly HashSet<string> visited = [];
        public string? Cursor { get; private set; }
        public bool Done { get; set; }
        public void Advance(JsonElement connection)
        {
            var info = connection.GetProperty("pageInfo");
            Done = !info.GetProperty("hasNextPage").GetBoolean();
            if (Done) return;
            var cursor = Text(info, "endCursor");
            if (string.IsNullOrEmpty(cursor) || !visited.Add(cursor))
                throw new GitHubException("GitHub returned a missing or repeated pagination cursor. Cached data has been retained.");
            Cursor = cursor;
        }
    }

    private sealed record RequestPart(string Alias, string Selection, Work Owner, Action<JsonElement> Accept);

    private sealed class GraphRequest
    {
        private readonly List<string> definitions = [];
        public Dictionary<string, string?> Variables { get; } = [];
        public List<RequestPart> Parts { get; } = [];
        public string Document => $"query({string.Join(", ", definitions)}) {{ rateLimit {{ remaining resetAt }} " +
            string.Join(" ", Parts.Select(part => $"{part.Alias}: {part.Selection}")) + " }";
        public string Variable(string name, string type, string? value)
        {
            definitions.Add($"${name}: {type}");
            Variables.Add(name, value);
            return "$" + name;
        }
        public void Add(string alias, string selection, Work owner, Action<JsonElement> accept) =>
            Parts.Add(new(alias, selection, owner, accept));
    }
}
