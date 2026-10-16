using System.Net;
using System.Text.Json;
using GitHubToDo.Core;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class GitHubClientTests
{
    private static object User(string login = "other", int id = 2) => new { id, login };
    private static object SearchItem(int id) => new
    {
        node_id = $"node:{id}", repository_url = "https://api.github.com/repos/o/r",
        number = id, title = $"Issue {id}", body = "body", html_url = $"https://github.com/o/r/issues/{id}",
        state = "open", user = User(), created_at = "2026-01-01T00:00:00Z", updated_at = "2026-01-01T00:00:00Z"
    };
    private static object Comment(int id, string login = "other") => new
    {
        id, user = User(login), body = $"message {id}", html_url = "https://github.com/o/r/issues/1",
        created_at = "2026-01-01T00:00:00Z", updated_at = "2026-02-01T00:00:00Z"
    };

    [TestMethod]
    [DataRow("")]
    [DataRow(" sort:updated-desc")]
    [DataRow(" sort:created-asc")]
    [DataRow(" sort:magic")]
    public async Task SearchFollowsPagesAndAlwaysRequestsUpdatedDescending(string sort)
    {
        using var database = new TestDatabase();
        var calls = 0;
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            calls++;
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            if (calls == 1)
            {
                Assert.Contains("author:viewer", query);
                Assert.Contains("sort=updated&order=desc", query);
            }
            return Task.FromResult(FakeHandler.Json(JsonSerializer.Serialize(new
            {
                total_count = 2, incomplete_results = false, items = new[] { SearchItem(calls) }
            }), next: calls == 1 ? "https://api.github.com/search/issues?page=2" : null));
        }));
        var client = new GitHubClient(http, _ => Task.FromResult("fake-token"), database.Store);
        var result = await client.SearchAsync("is:pr author:@me" + sort, "viewer", default);
        Assert.HasCount(2, result.Items);
        Assert.IsFalse(result.IsIncomplete);
    }

    [TestMethod]
    public async Task SearchStopsAtOneThousandAndReportsActualTotal()
    {
        using var database = new TestDatabase();
        var calls = 0;
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            var page = calls++;
            return Task.FromResult(FakeHandler.Json(JsonSerializer.Serialize(new
            {
                total_count = 1200, incomplete_results = false,
                items = Enumerable.Range(page * 100 + 1, 100).Select(SearchItem).ToArray()
            }), next: $"https://api.github.com/search/issues?page={calls + 1}"));
        }));
        var client = new GitHubClient(http, _ => Task.FromResult("fake-token"), database.Store);
        var result = await client.SearchAsync("is:issue", "viewer", default);
        Assert.HasCount(1000, result.Items);
        Assert.AreEqual(1200, result.TotalCount);
        Assert.AreEqual(10, calls);
    }

    [TestMethod]
    public async Task LongIgnoredTailDoesNotHideEarlierHumanActivity()
    {
        using var database = new TestDatabase();
        var calls = 0;
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            var page = calls++;
            return Task.FromResult(FakeHandler.Json(JsonSerializer.Serialize(page == 0
                ? new[] { Comment(1, "human") }
                : Enumerable.Range(page * 100, 100).Select(id => Comment(id, "bot")).ToArray()),
                next: calls < 4 ? $"https://api.github.com/repos/o/r/issues/1/comments?page={calls + 1}" : null));
        }));
        var client = new GitHubClient(http, _ => Task.FromResult("fake-token"), database.Store);
        var item = FakeClient.NewItem();
        var activity = await client.GetActivityAsync(item, default);
        ReadStatePolicy.ApplyActivity(item, activity, DateTimeOffset.UtcNow);
        Assert.AreEqual(4, calls);
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, "viewer", [new("bot")]));
    }

    [TestMethod]
    public async Task ReviewStreamsExcludeDraftsAndUseSubmissionTimeForInlineComments()
    {
        using var database = new TestDatabase();
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var content = path.EndsWith("/reviews") ? """
                [{"id":10,"state":"APPROVED","submitted_at":"2026-01-02T00:00:00Z","user":{"id":1,"login":"viewer"},"body":"","html_url":""},
                 {"id":11,"state":"PENDING","submitted_at":null,"user":{"id":2,"login":"other"},"body":"draft","html_url":""}]
                """ : path.Contains("/pulls/") ? """
                [{"id":20,"pull_request_review_id":10,"created_at":"2026-01-01T00:00:00Z","user":{"id":1,"login":"viewer"},"body":"inline","html_url":""},
                 {"id":21,"pull_request_review_id":11,"created_at":"2026-01-03T00:00:00Z","user":{"id":2,"login":"other"},"body":"draft inline","html_url":""}]
                """ : "[]";
            return Task.FromResult(FakeHandler.Json(content));
        }));
        var item = FakeClient.NewItem();
        item.IsPullRequest = true;
        var activity = await new GitHubClient(http, _ => Task.FromResult("fake-token"), database.Store).GetActivityAsync(item, default);
        Assert.HasCount(2, activity);
        Assert.IsTrue(activity.All(entry => entry.PublishedAt == DateTimeOffset.Parse("2026-01-02T00:00:00Z")));
        ReadStatePolicy.ApplyActivity(item, activity, DateTimeOffset.UtcNow);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "1", []));
    }

    [TestMethod]
    public async Task ConditionalFirstPageDoesNotSkipChangedLaterPages()
    {
        using var database = new TestDatabase();
        var secondRound = false;
        var requests = 0;
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            requests++;
            var secondPage = request.RequestUri!.Query.Contains("page=2");
            if (secondRound && !secondPage)
            {
                Assert.IsNotNull(request.Headers.IfNoneMatch.SingleOrDefault());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            return Task.FromResult(FakeHandler.Json(JsonSerializer.Serialize(
                new[] { Comment(secondPage ? secondRound ? 3 : 2 : 1) }),
                next: secondPage ? null : "https://api.github.com/repos/o/r/issues/1/comments?page=2"));
        }));
        var client = new GitHubClient(http, _ => Task.FromResult("fake-token"), database.Store);
        await client.GetActivityAsync(FakeClient.NewItem(), default);
        secondRound = true;
        var result = await client.GetActivityAsync(FakeClient.NewItem(), default);
        Assert.AreEqual(4, requests);
        Assert.IsTrue(result.Any(activity => activity.Id == "comment:3"));
    }

    [TestMethod]
    public async Task UntrustedPaginationCannotReceiveCredentials()
    {
        using var database = new TestDatabase();
        var calls = 0;
        using var http = new HttpClient(new FakeHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(FakeHandler.Json("[]", next: "https://evil.example/comments?page=2"));
        }));
        var client = new GitHubClient(http, _ => Task.FromResult("fake-token"), database.Store);
        await Assert.ThrowsAsync<GitHubException>(() => client.GetActivityAsync(FakeClient.NewItem(), default));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task RateLimitPausesWithoutRetryStorm()
    {
        using var database = new TestDatabase();
        var calls = 0;
        using var http = new HttpClient(new FakeHandler((_, _) =>
        {
            calls++;
            var response = FakeHandler.Json("{\"message\":\"rate limit exceeded\"}", HttpStatusCode.Forbidden);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        }));
        var client = new GitHubClient(http, _ => Task.FromResult("fake-token"), database.Store);
        await Assert.ThrowsAsync<GitHubException>(() => client.GetUserAsync(default));
        await Assert.ThrowsAsync<GitHubException>(() => client.GetUserAsync(default));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task AuthenticatedCacheCannotCrossTokenBoundary()
    {
        using var database = new TestDatabase();
        var token = "first-test-token";
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            if (token == "second-test-token")
                Assert.IsEmpty(request.Headers.IfNoneMatch);
            return Task.FromResult(FakeHandler.Json("{\"id\":1,\"login\":\"viewer\"}"));
        }));
        var client = new GitHubClient(http, _ => Task.FromResult(token), database.Store);
        await client.GetUserAsync(default);
        token = "second-test-token";
        await client.GetUserAsync(default);
    }

    [TestMethod]
    public async Task FailedSecondPageDoesNotReturnSuccessShapedPartialActivity()
    {
        using var database = new TestDatabase();
        var count = 0;
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(++count == 1
            ? FakeHandler.Json(JsonSerializer.Serialize(new[] { Comment(1) }), next: "https://api.github.com/repos/o/r/issues/1/comments?page=2")
            : FakeHandler.Json("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound))));
        var client = new GitHubClient(http, _ => Task.FromResult("fake-token"), database.Store);
        await Assert.ThrowsAsync<GitHubException>(() => client.GetActivityAsync(FakeClient.NewItem(), default));
    }
}
