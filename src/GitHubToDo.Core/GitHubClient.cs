using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static GitHubToDo.Core.GitHubHttp;

namespace GitHubToDo.Core;

public sealed partial class GitHubClient(
    HttpClient http, Func<CancellationToken, Task<string>> accessToken, LocalStore store) : IGitHubClient
{
    private readonly SemaphoreSlim requestGate = new(1);
    private DateTimeOffset notBefore;

    public async Task<GitHubUser> GetUserAsync(CancellationToken cancellationToken)
    {
        var page = await GetPageAsync(new("https://api.github.com/user"), cancellationToken);
        using var document = JsonDocument.Parse(page.Body);
        return ParseUser(document.RootElement);
    }

    public async Task<GitHubUser> GetUserAsync(string login, CancellationToken cancellationToken)
    {
        var page = await GetPageAsync(new($"https://api.github.com/users/{Uri.EscapeDataString(login)}"), cancellationToken);
        using var document = JsonDocument.Parse(page.Body);
        return ParseUser(document.RootElement);
    }

    public async Task<SearchResult> SearchAsync(string expression, string login, CancellationToken cancellationToken)
    {
        var parsed = QueryParser.Parse(expression, login);
        var url = $"https://api.github.com/search/issues?q={Uri.EscapeDataString(parsed.Search)}&per_page=100";
        if (parsed.Sort is not null)
            url += $"&sort={parsed.Sort}&order={parsed.Order}";
        Uri? next = new(url);
        var items = new List<TrackedItem>();
        var total = 0;
        var incomplete = false;
        var visited = new HashSet<string>();
        while (next is not null && items.Count < 1000)
        {
            if (!visited.Add(next.AbsoluteUri))
                throw new GitHubException("GitHub returned a repeated search page; cached membership has been retained.");
            var page = await GetPageAsync(next, cancellationToken);
            using var document = JsonDocument.Parse(page.Body);
            var root = document.RootElement;
            total = root.GetProperty("total_count").GetInt32();
            incomplete |= root.GetProperty("incomplete_results").GetBoolean();
            foreach (var item in root.GetProperty("items").EnumerateArray())
                items.Add(ParseItem(item));
            next = NextPage(page.Link);
        }
        var distinct = items.DistinctBy(item => item.Id).Take(1000).ToArray();
        incomplete |= distinct.Length < Math.Min(total, 1000);
        return new(distinct, total, incomplete);
    }

    public async Task<IReadOnlyList<Activity>> GetActivityAsync(TrackedItem item, CancellationToken cancellationToken)
    {
        var repository = string.Join("/", item.Repository.Split('/').Select(Uri.EscapeDataString));
        var prefix = $"https://api.github.com/repos/{repository}";
        var comments = await GetArrayAsync(new($"{prefix}/issues/{item.Number}/comments?per_page=100"), cancellationToken);
        var activities = comments.Select(comment => ParseActivity(comment, ActivityKind.Comment,
            "comment", Date(comment, "created_at"))).ToList();
        if (!item.IsPullRequest)
            return activities;
        var reviews = await GetArrayAsync(new($"{prefix}/pulls/{item.Number}/reviews?per_page=100"), cancellationToken);
        var submitted = reviews.Where(review => Text(review, "state") != "PENDING" &&
                review.TryGetProperty("submitted_at", out var time) && time.ValueKind == JsonValueKind.String)
            .DistinctBy(review => review.GetProperty("id").GetRawText())
            .ToDictionary(review => review.GetProperty("id").GetRawText());
        foreach (var review in submitted.Values)
            activities.Add(ParseActivity(review, ActivityKind.Review, "review", Date(review, "submitted_at")));
        var inline = await GetArrayAsync(new($"{prefix}/pulls/{item.Number}/comments?per_page=100"), cancellationToken);
        foreach (var comment in inline)
        {
            var publishedAt = Date(comment, "created_at");
            if (comment.TryGetProperty("pull_request_review_id", out var reviewId) && reviewId.ValueKind == JsonValueKind.Number)
            {
                if (!submitted.TryGetValue(reviewId.GetRawText(), out var review))
                    continue;
                var submittedAt = Date(review, "submitted_at");
                if (submittedAt > publishedAt)
                    publishedAt = submittedAt;
            }
            activities.Add(ParseActivity(comment, ActivityKind.ReviewComment, "inline", publishedAt));
        }
        return activities;
    }

    private async Task<List<JsonElement>> GetArrayAsync(Uri url, CancellationToken cancellationToken)
    {
        var result = new List<JsonElement>();
        Uri? next = url;
        var visited = new HashSet<string>();
        while (next is not null)
        {
            if (!visited.Add(next.AbsoluteUri))
                throw new GitHubException("GitHub returned a repeated activity page. Activity is not complete.");
            var page = await GetPageAsync(next, cancellationToken);
            using var document = JsonDocument.Parse(page.Body);
            result.AddRange(document.RootElement.EnumerateArray().Select(element => element.Clone()));
            next = NextPage(page.Link);
        }
        return result;
    }

    private async Task<HttpCacheEntry> GetPageAsync(Uri uri, CancellationToken cancellationToken)
    {
        ValidateApiUri(uri);
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < notBefore)
                throw new GitHubException($"GitHub rate limit: retry after {notBefore.ToLocalTime():t}.", 429);
            var token = await accessToken(cancellationToken);
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            var key = $"{identity}:{uri.AbsoluteUri}";
            var cached = await store.GetCacheAsync(key, cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Authorization = new("Bearer", token);
                request.Headers.UserAgent.ParseAdd("GitHubToDo/1.0");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                if (cached?.ETag is not null)
                    request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
                else if (cached?.LastModified is not null)
                    request.Headers.TryAddWithoutValidation("If-Modified-Since", cached.LastModified);
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotModified)
                    return cached ?? throw new GitHubException("GitHub returned an unexpected empty cached response.");
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    var message = ErrorMessage(body, response.ReasonPhrase ?? "GitHub request failed.");
                    if (status == 429 || status == 403 && (response.Headers.RetryAfter is not null ||
                        Header(response, "X-RateLimit-Remaining") == "0" ||
                        message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
                    {
                        notBefore = RetryAt(response);
                        throw new GitHubException($"GitHub rate limit: retry after {notBefore.ToLocalTime():t}. {message}", status);
                    }
                    if (status >= 500 && attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                        continue;
                    }
                    var hint = status switch
                    {
                        401 => "Sign in again. ",
                        403 => "Check repository permissions and organization OAuth/SSO approval. ",
                        404 => "The item or account is missing, deleted, or inaccessible. ",
                        422 => "Check the query syntax and access to its repositories. ",
                        _ => ""
                    };
                    throw new GitHubException(hint + message, status);
                }
                var entry = new HttpCacheEntry(response.Headers.ETag?.ToString(),
                    response.Content.Headers.LastModified?.ToString("R"), body, Header(response, "Link"));
                await store.SetCacheAsync(key, entry, cancellationToken);
                if (Header(response, "X-RateLimit-Remaining") == "0")
                    notBefore = RetryAt(response);
                return entry;
            }
        }
        finally { requestGate.Release(); }
    }

    private static void ValidateApiUri(Uri uri)
    {
        if (uri.Scheme != "https" || uri.Host != "api.github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new GitHubException("Refused an untrusted GitHub API or pagination URL.");
    }

    private static Uri? NextPage(string? link)
    {
        if (link is null)
            return null;
        foreach (Match match in LinkPattern().Matches(link))
        {
            if (match.Groups[2].Value != "next")
                continue;
            if (!Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri))
                throw new GitHubException("GitHub returned an invalid pagination link.");
            ValidateApiUri(uri);
            return uri;
        }
        return null;
    }

    private static GitHubUser ParseUser(JsonElement user) =>
        new(user.GetProperty("id").GetRawText(), user.GetProperty("login").GetString()!);

    private static Activity ParseActivity(JsonElement element, ActivityKind kind, string prefix, DateTimeOffset time)
    {
        var author = element.GetProperty("user");
        var knownAuthor = author.ValueKind == JsonValueKind.Object;
        var body = Text(element, "body") ?? "";
        if (kind == ActivityKind.Review)
            body = $"{Text(element, "state")}: {body}";
        return new($"{prefix}:{element.GetProperty("id").GetRawText()}", kind,
            knownAuthor ? author.GetProperty("id").GetRawText() : null,
            knownAuthor ? Text(author, "login") : null, time, body, Text(element, "html_url") ?? "");
    }

    private static TrackedItem ParseItem(JsonElement element)
    {
        var repositoryUri = new Uri(element.GetProperty("repository_url").GetString()!);
        ValidateApiUri(repositoryUri);
        var author = element.GetProperty("user");
        return new()
        {
            Id = element.GetProperty("node_id").GetString()!,
            Repository = Uri.UnescapeDataString(repositoryUri.AbsolutePath["/repos/".Length..]),
            Number = element.GetProperty("number").GetInt32(),
            IsPullRequest = element.TryGetProperty("pull_request", out _),
            Title = element.GetProperty("title").GetString() ?? "",
            Body = Text(element, "body") ?? "",
            Url = element.GetProperty("html_url").GetString()!,
            State = element.GetProperty("state").GetString() ?? "",
            AuthorId = author.ValueKind == JsonValueKind.Object ? author.GetProperty("id").GetRawText() : null,
            AuthorLogin = author.ValueKind == JsonValueKind.Object ? Text(author, "login") : null,
            CreatedAt = Date(element, "created_at"),
            UpdatedAt = Date(element, "updated_at")
        };
    }

    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
    private static DateTimeOffset Date(JsonElement element, string key) =>
        element.GetProperty(key).GetDateTimeOffset();

    [GeneratedRegex("<([^>]+)>\\s*;\\s*rel=\"([^\"]+)\"")]
    private static partial Regex LinkPattern();
}
