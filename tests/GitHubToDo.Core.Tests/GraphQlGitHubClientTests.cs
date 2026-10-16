using System.Net;
using System.Text.Json;
using GitHubToDo.Core;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class GraphQlGitHubClientTests
{
    private const string Time = "2026-01-01T00:00:00Z";
    private TestDatabase database = null!;
    private HttpClient? http;
    private readonly List<JsonElement> requests = [];
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        database = new();
        await database.Store.GetSettingAsync("initialize");
    }

    [TestCleanup]
    public void Cleanup()
    {
        http?.Dispose();
        database.Dispose();
    }

    [TestMethod]
    public async Task ThreeHundredUnchangedItemsReconcileWithSearchPagesOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var account = new AccountSnapshot { User = new("1", "viewer"), Queries = [new() { Expression = "is:issue" }] };
        for (var index = 0; index < 300; index++)
        {
            var item = Tracked($"cached-{index}");
            item.UpdatedAt = DateTimeOffset.Parse(Time);
            item.HasBaseline = true;
            item.ActivityCheckedAt = now.AddHours(-1);
            account.Items.Add(item.Id, item);
        }
        account.Queries[0].ItemIds = account.Items.Keys.ToList();
        account.Queries[0].LastRefreshedAt = now.AddHours(-1);
        account.Queries[0].LastFullRefreshedAt = now.AddHours(-1);
        await database.Store.SaveAsync(account);
        var client = Client((request, call) =>
        {
            Assert.DoesNotContain("node(id:", request.GetProperty("query").GetString()!);
            return Response(new { s0 = Search(Enumerable.Range((call - 1) * 100, 100)
                .Select(index => Item($"cached-{index}")).ToArray(), 300, call < 3 ? $"page{call}" : null) });
        });
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(account.User);
        await workspace.PollAsync();
        Assert.HasCount(3, requests, "Only the three membership pages, with no discussion or metadata requests.");
        Assert.IsTrue(workspace.Account!.Items.Values.All(item => item.LoadedActivityMetadata is not null));
    }

    [TestMethod]
    public async Task MetadataChecksBatchOneHundredItemsWithoutDownloadingBodies()
    {
        var client = Client((request, _) =>
        {
            var document = request.GetProperty("query").GetString()!;
            Assert.DoesNotContain("body", document);
            Assert.DoesNotContain("comments(first:", document);
            var ids = request.GetProperty("variables").EnumerateObject().ToArray();
            Assert.IsLessThanOrEqualTo(100, ids.Length);
            return Response(ids.ToDictionary(property => $"n{property.Name[2..]}", _ =>
                new { __typename = "PullRequest", updatedAt = Time, comments = new { totalCount = 2 },
                    totalCommentsCount = 4, reviews = new { totalCount = 1, nodes = new[] {
                        new { databaseId = 2, fullDatabaseId = 2, updatedAt = Time } } } }));
        });
        var items = Enumerable.Range(0, 201).Select(index => Tracked($"pr-{index}", true)).ToArray();
        var result = await client.GetActivityMetadataBatchAsync([.. items, items[0]], default);
        Assert.HasCount(3, requests);
        Assert.HasCount(201, result);
        Assert.IsTrue(result.Values.All(value => value.Error is null && value.Value!.ReviewCount == 1 &&
            value.Value.LatestReviewId == "review:2"));
    }

    [TestMethod]
    public async Task MetadataErrorsRemainPerItemWithoutDiscardingSuccessfulChecks()
    {
        var client = Client((_, _) => Response(new
        {
            n0 = new { __typename = "Issue", updatedAt = Time, comments = new { totalCount = 3 } },
            n1 = (object?)null
        }, new[] { new { type = "FORBIDDEN", message = "Item no longer accessible", path = new[] { "n1" } } }));
        var results = await client.GetActivityMetadataBatchAsync([Tracked("good"), Tracked("forbidden")], default);
        Assert.HasCount(1, requests);
        Assert.AreEqual(3, results["good"].Value!.CommentCount);
        Assert.IsNull(results["good"].Error);
        Assert.IsNotNull(results["forbidden"].Error);
        Assert.IsNull(results["forbidden"].Value);
    }

    [TestMethod]
    public async Task NewCommentOnFiveHundredCommentThreadFetchesOnlyOneRecentPage()
    {
        var item = Tracked("large");
        item.HasBaseline = true;
        item.ActivityCheckedAt = DateTimeOffset.Parse(Time).AddDays(1);
        item.ActivityMetadata = new(item.ActivityCheckedAt.Value.AddDays(1), 501, 0, 501);
        item.Activities = Enumerable.Range(1, 500).Select(id => new Activity($"comment:{id}", ActivityKind.Comment,
            "2", "other", DateTimeOffset.Parse(Time), "Cached comment", "")).ToList();
        var client = Client((request, _) =>
        {
            Assert.Contains("orderBy: {field: UPDATED_AT, direction: DESC}", request.GetProperty("query").GetString()!);
            return Response(new { n0 = ActivityNode(false,
                [Comment(501, created: "2026-01-03T00:00:00Z"), Comment(500)], [], "old-history-not-needed") });
        });
        var result = await client.GetActivityAsync(item, default);
        Assert.HasCount(1, requests);
        Assert.HasCount(501, result);
        Assert.Contains("comment:501", result.Select(activity => activity.Id));
    }

    [TestMethod]
    public async Task ConcurrentDeletionDuringIncrementalCommentsReconcilesRatherThanRetainingDeletedContent()
    {
        var item = Tracked("changed");
        item.HasBaseline = true;
        item.ActivityCheckedAt = DateTimeOffset.Parse(Time).AddDays(1);
        item.ActivityMetadata = new(item.ActivityCheckedAt.Value.AddDays(1), 2, 0, 2);
        item.Activities = [new("comment:1", ActivityKind.Comment, "2", "other", DateTimeOffset.Parse(Time), "Deleted", "")];
        var client = Client((request, call) =>
        {
            if (call == 1)
                return Response(new { n0 = ActivityNode(false,
                    [Comment(2, created: "2026-01-03T00:00:00Z"), Comment(3, created: "2026-01-03T00:00:00Z")], []) });
            Assert.DoesNotContain("orderBy:", request.GetProperty("query").GetString()!);
            return Response(new { n0 = ActivityNode(false, [Comment(2), Comment(3)], []) });
        });
        var result = await client.GetActivityAsync(item, default);
        Assert.HasCount(2, requests);
        CollectionAssert.AreEquivalent(new[] { "comment:2", "comment:3" }, result.Select(activity => activity.Id).ToArray());
    }

    [TestMethod]
    public async Task DecreasedCommentCountDownloadsCompleteHistoryWithoutRetainingDeletedComments()
    {
        var item = Tracked("deletion");
        item.HasBaseline = true;
        item.ActivityCheckedAt = DateTimeOffset.Parse(Time).AddDays(1);
        item.ActivityMetadata = new(item.ActivityCheckedAt.Value.AddDays(1), 1, 0, 1);
        item.Activities = Enumerable.Range(1, 2).Select(id => new Activity($"comment:{id}", ActivityKind.Comment,
            "2", "other", DateTimeOffset.Parse(Time), "Cached", "")).ToList();
        var client = Client((request, _) =>
        {
            Assert.DoesNotContain("orderBy:", request.GetProperty("query").GetString()!);
            return Response(new { n0 = ActivityNode(false, [Comment(2)], []) });
        });
        var result = await client.GetActivityAsync(item, default);
        Assert.HasCount(1, requests);
        Assert.AreEqual("comment:2", result.Single().Id);
    }

    [TestMethod]
    public async Task FailedIncrementalFallbackNeverPublishesPartiallyMergedDiscussion()
    {
        var item = Tracked("fallback-failure");
        item.HasBaseline = true;
        item.ActivityCheckedAt = DateTimeOffset.Parse(Time).AddDays(1);
        item.ActivityMetadata = new(item.ActivityCheckedAt.Value.AddDays(1), 2, 0, 2);
        item.Activities = [new("comment:1", ActivityKind.Comment, "2", "other", DateTimeOffset.Parse(Time), "Cached", "")];
        var client = Client((_, call) => call == 1 ?
            Response(new { n0 = ActivityNode(false, [Comment(2), Comment(3)], []) }) :
            Response(new { n0 = (object?)null },
                new[] { new { type = "FORBIDDEN", message = "Discussion no longer accessible", path = new[] { "n0" } } }));
        var result = (await client.GetActivityBatchAsync([item], default))[item.Id];
        Assert.HasCount(2, requests);
        Assert.IsNotNull(result.Error);
        Assert.IsNull(result.Value);
        Assert.AreEqual("comment:1", item.Activities.Single().Id);
    }

    [TestMethod]
    public async Task EditedOlderCommentUpdatesItsBodyWithoutDownloadingAllHistory()
    {
        var item = Tracked("edited");
        item.HasBaseline = true;
        item.ActivityCheckedAt = DateTimeOffset.Parse(Time).AddDays(1);
        item.ActivityMetadata = new(item.ActivityCheckedAt.Value.AddDays(1), 1, 0, 1);
        item.Activities = [new("comment:1", ActivityKind.Comment, "2", "other", DateTimeOffset.Parse(Time), "Old", "")];
        var comment = Comment(1);
        comment["updatedAt"] = "2026-01-03T00:00:00Z";
        comment["body"] = "Edited";
        var client = Client((_, _) => Response(new { n0 = ActivityNode(false, [comment], []) }));
        var result = await client.GetActivityAsync(item, default);
        Assert.HasCount(1, requests);
        Assert.AreEqual("Edited", result.Single().Body);
    }

    [TestMethod]
    public async Task SearchesShareOneRequestAndForceUpdatedDescendingWithoutCombiningMembership()
    {
        var client = Client((request, _) =>
        {
            var variables = request.GetProperty("variables");
            Assert.AreEqual("is:issue involves:viewer \"author:@me\" sort:updated-desc", variables.GetProperty("q0").GetString());
            Assert.AreEqual("is:pr review-requested:viewer sort:updated-desc", variables.GetProperty("q1").GetString());
            return Response(new { s0 = Search([Item("shared"), Item("issue")], 2), s1 = Search([Item("pr", true), Item("shared")], 2) });
        });
        var queries = new[]
        {
            new SavedQuery { Expression = "is:issue involves:@me \"author:@me\" sort:updated-desc" },
            new SavedQuery { Expression = "is:pr review-requested:@me sort:created-asc" }
        };
        var results = await client.SearchBatchAsync(queries, "viewer", default);
        Assert.HasCount(1, requests);
        CollectionAssert.AreEqual(new[] { "shared", "issue" }, results[queries[0].Id].Value!.Items.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "pr", "shared" }, results[queries[1].Id].Value!.Items.Select(item => item.Id).ToArray());
        Assert.IsTrue(results[queries[1].Id].Value!.Items[0].IsPullRequest);
    }

    [TestMethod]
    public async Task SearchBatchesAreBoundedAndPaginateEachAliasIndependently()
    {
        var client = Client((request, call) =>
        {
            var variables = request.GetProperty("variables");
            if (call == 1)
                return Response(new { s0 = Search([Item("a")], 2, "next"), s1 = Search([Item("b")], 1) });
            Assert.AreEqual("first sort:updated-desc", variables.GetProperty("q0").GetString());
            Assert.AreEqual("next", variables.GetProperty("a0").GetString());
            Assert.IsFalse(variables.TryGetProperty("q1", out _));
            return Response(new { s0 = Search([Item("c")], 2) });
        });
        var queries = new[] { new SavedQuery { Expression = "first" }, new SavedQuery { Expression = "second" } };
        var results = await client.SearchBatchAsync(queries, "viewer", default);
        Assert.HasCount(2, requests);
        Assert.HasCount(2, results[queries[0].Id].Value!.Items);
        Assert.HasCount(1, results[queries[1].Id].Value!.Items);
    }

    [TestMethod]
    public async Task SeventeenSearchesUseThreeBoundedRequests()
    {
        var client = Client((request, _) =>
        {
            var aliases = request.GetProperty("variables").EnumerateObject().Where(property => property.Name.StartsWith('q')).ToArray();
            Assert.IsLessThanOrEqualTo(GitHubBatching.SearchesPerRequest, aliases.Length);
            return Response(aliases.ToDictionary(property => "s" + property.Name[1..], _ => Search([], 0)));
        });
        var queries = Enumerable.Range(0, 17).Select(index => new SavedQuery { Expression = $"repo:o/r{index}" }).ToArray();
        var result = await client.SearchBatchAsync(queries, "viewer", default);
        Assert.HasCount(3, requests);
        Assert.IsTrue(result.Values.All(value => value.Error is null && value.Value!.Items.Count == 0));
    }

    [TestMethod]
    public async Task SearchStopsAtOneThousandAndReportsFullTotal()
    {
        var client = Client((_, call) => Response(new
        {
            s0 = Search(Enumerable.Range((call - 1) * 100, 100).Select(index => Item(index.ToString())).ToArray(), 1500, $"page{call}")
        }));
        var result = await client.SearchAsync("is:issue", "viewer", default);
        Assert.HasCount(10, requests);
        Assert.HasCount(1000, result.Items);
        Assert.AreEqual(1500, result.TotalCount);
    }

    [TestMethod]
    public async Task PreviewSearchUsesOneSmallPageAndReportsTotal()
    {
        var client = Client((request, _) =>
        {
            StringAssert.Contains(request.GetProperty("query").GetString(), "first: 20,");
            Assert.AreEqual("is:pr author:viewer sort:updated-desc", request.GetProperty("variables").GetProperty("q0").GetString());
            return Response(new { s0 = Search(Enumerable.Range(0, 20).Select(index => Item(index.ToString())).ToArray(), 640, "more") });
        });
        var result = await client.PreviewSearchAsync("is:pr author:@me", "viewer", 20, default);
        Assert.HasCount(1, requests);
        Assert.HasCount(20, result.Items);
        Assert.AreEqual(640, result.TotalCount);
    }

    [TestMethod]
    public async Task PartialGraphQlErrorsDoNotDiscardSuccessfulSearches()
    {
        var client = Client((_, _) => Response(new { s0 = Search([Item("ok")], 1), s1 = (object?)null },
            new[] { new { type = "UNPROCESSABLE", message = "Invalid search", path = new object[] { "s1", "nodes" } } }));
        var queries = new[] { new SavedQuery { Expression = "good" }, new SavedQuery { Expression = "bad" } };
        var results = await client.SearchBatchAsync(queries, "viewer", default);
        Assert.AreEqual("ok", results[queries[0].Id].Value!.Items.Single().Id);
        Assert.AreEqual("Invalid search", results[queries[1].Id].Error!.Message);
        Assert.IsNull(results[queries[1].Id].Value);
    }

    [TestMethod]
    public async Task InvalidLocalQueryDoesNotPreventOtherAliases()
    {
        var client = Client((_, _) => Response(new { s0 = Search([], 0) }));
        var queries = new[] { new SavedQuery { Expression = "\"unmatched" }, new SavedQuery { Expression = "valid" } };
        var results = await client.SearchBatchAsync(queries, "viewer", default);
        Assert.IsNotNull(results[queries[0].Id].Error);
        Assert.IsNull(results[queries[1].Id].Error);
        Assert.HasCount(1, requests);
    }

    [TestMethod]
    public async Task TenPullRequestsFetchAllThreeActivityKindsInOneRequest()
    {
        var client = Client((request, _) => Response(request.GetProperty("variables").EnumerateObject()
            .Where(property => property.Name.StartsWith("id", StringComparison.Ordinal))
            .ToDictionary(property => "n" + property.Name[2..], _ =>
                ActivityNode(true, [Comment(1)], [Review(2, [Comment(3)])]))));
        var items = Enumerable.Range(0, 10).Select(index => Tracked($"pr{index}", true)).ToArray();
        var results = await client.GetActivityBatchAsync(items, default);
        Assert.HasCount(1, requests, "Ten PRs should not require thirty individual REST stream requests.");
        foreach (var result in results.Values)
        {
            Assert.IsNull(result.Error);
            CollectionAssert.AreEquivalent(new[] { "comment:1", "review:2", "inline:3" }, result.Value!.Select(activity => activity.Id).ToArray());
        }
    }

    [TestMethod]
    public async Task ActivityBatchesDeduplicateItemsAndStayBounded()
    {
        var client = Client((request, _) =>
        {
            var aliases = request.GetProperty("variables").EnumerateObject()
                .Where(property => property.Name.StartsWith("id", StringComparison.Ordinal)).ToArray();
            Assert.IsLessThanOrEqualTo(GitHubBatching.ItemsPerRequest, aliases.Length);
            return Response(aliases.ToDictionary(property => "n" + property.Name[2..], _ => ActivityNode(true, [], [])));
        });
        var items = Enumerable.Range(0, 21).Select(index => Tracked($"pr{index}", true)).ToArray();
        var results = await client.GetActivityBatchAsync([.. items, items[0]], default);
        Assert.HasCount(3, requests);
        Assert.HasCount(21, results);
    }

    [TestMethod]
    public async Task ConversationReviewsAndNestedInlineRepliesAllPaginate()
    {
        var client = Client((request, call) =>
        {
            var variables = request.GetProperty("variables");
            if (call == 1)
                return Response(new
                {
                    n0 = ActivityNode(true, [Comment(1)], [Review(2, [Comment(3)], commentCursor: "inline1")], "comments1", "reviews1")
                });
            if (call == 2)
            {
                Assert.AreEqual("comments1", variables.GetProperty("c0").GetString());
                Assert.AreEqual("reviews1", variables.GetProperty("r0").GetString());
                Assert.AreEqual("review2", variables.GetProperty("id1").GetString());
                Assert.AreEqual("inline1", variables.GetProperty("a1").GetString());
                return Response(new
                {
                    n0 = ActivityNode(true, [Comment(4)], [Review(5, [Comment(6)])]),
                    n1 = new { __typename = "PullRequestReview", comments = Connection([Comment(7)], "inline2") }
                });
            }
            Assert.AreEqual("review2", variables.GetProperty("id0").GetString());
            Assert.AreEqual("inline2", variables.GetProperty("a0").GetString());
            return Response(new { n0 = new { __typename = "PullRequestReview", comments = Connection([Comment(8)]) } });
        });
        var result = await client.GetActivityAsync(Tracked("pr", true), default);
        Assert.HasCount(3, requests);
        CollectionAssert.AreEquivalent(new[]
        {
            "comment:1", "comment:4", "review:2", "review:5", "inline:3", "inline:6", "inline:7", "inline:8"
        }, result.Select(activity => activity.Id).ToArray());
    }

    [TestMethod]
    public async Task ReviewPagesContinueWhenConversationIsAlreadyComplete()
    {
        var client = Client((_, call) => Response(new
        {
            n0 = ActivityNode(true, [], [Review(call, [])], reviewCursor: call == 1 ? "next" : null)
        }));
        var activities = await client.GetActivityAsync(Tracked("pr", true), default);
        Assert.HasCount(2, requests);
        Assert.HasCount(2, activities);
        Assert.DoesNotContain("comments(first: 50", requests[1].GetProperty("query").GetString()!);
    }

    [TestMethod]
    public async Task IgnoredTailDoesNotHideOlderRelevantActivity()
    {
        var client = Client((_, call) => Response(new
        {
            n0 = ActivityNode(false,
                call == 1 ? [Comment(1)] : Enumerable.Range(call * 50, 50)
                    .Select(index => Comment(index, "bot", 3, "2026-01-03T00:00:00Z")).ToArray(),
                [], commentCursor: call < 3 ? $"page{call}" : null)
        }));
        var item = Tracked("issue");
        ReadStatePolicy.ApplyActivity(item, await client.GetActivityAsync(item, default), DateTimeOffset.UtcNow);
        Assert.HasCount(3, requests);
        Assert.HasCount(101, item.Activities);
        Assert.AreEqual(ReadStatus.Unread, ReadStatePolicy.Evaluate(item, "1", [new("bot", "3")]));
    }

    [TestMethod]
    public async Task PendingReviewsAndDraftCommentsAreExcludedAndSubmissionSetsPublicationTime()
    {
        var pending = Review(1, [Comment(2)], state: "PENDING", commentCursor: "must-not-fetch");
        var inline = Comment(4);
        var draft = Comment(5);
        draft["state"] = "PENDING";
        var submitted = Review(3, [inline, draft], submitted: "2026-01-03T00:00:00Z");
        var client = Client((_, _) => Response(new { n0 = ActivityNode(true, [], [pending, submitted]) }));
        var result = await client.GetActivityAsync(Tracked("pr", true), default);
        Assert.HasCount(1, requests);
        Assert.HasCount(2, result);
        Assert.AreEqual(DateTimeOffset.Parse("2026-01-03T00:00:00Z"), result.Single(activity => activity.Id == "inline:4").PublishedAt);
        Assert.AreEqual("review:3", result.Single(activity => activity.Kind == ActivityKind.Review).Id);
    }

    [TestMethod]
    public async Task NumericIdentitiesPreserveAcknowledgementsAndOwnLatestRule()
    {
        var large = Comment(1);
        large["databaseId"] = null;
        large["fullDatabaseId"] = "3000000000";
        var client = Client((_, _) => Response(new
        {
            n0 = ActivityNode(true, [large], [Review(2, [Comment(3)])])
        }));
        var item = Tracked("pr", true);
        item.HasBaseline = true;
        item.AcknowledgedIds = ["comment:3000000000", "review:2", "inline:3"];
        ReadStatePolicy.ApplyActivity(item, await client.GetActivityAsync(item, default), DateTimeOffset.UtcNow);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(item, "1", []));
        Assert.IsTrue(item.Activities.All(activity => activity.AuthorId == "2"));
        var own = Comment(4, "viewer", 1, "2026-01-04T00:00:00Z");
        var selfClient = Client((_, _) => Response(new { n0 = ActivityNode(false, [own], []) }));
        var selfItem = Tracked("self");
        ReadStatePolicy.ApplyActivity(selfItem, await selfClient.GetActivityAsync(selfItem, default), DateTimeOffset.UtcNow);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(selfItem, "1", []));
    }

    [TestMethod]
    public async Task FailedActivityPaginationRetainsStaleHistoryButCommitsSuccessfulSibling()
    {
        var oldTime = DateTimeOffset.Parse(Time);
        var stale = Tracked("stale");
        stale.HasBaseline = true;
        stale.ActivityCheckedAt = oldTime;
        stale.Activities = [new("comment:99", ActivityKind.Comment, "2", "other", oldTime, "cached", "")];
        stale.AcknowledgedIds = ["comment:99"];
        var account = new AccountSnapshot
        {
            User = new("1", "viewer"),
            Queries = [new() { Expression = "is:issue", ItemIds = ["fresh", "stale"] }],
            Items = new() { ["fresh"] = Tracked("fresh"), ["stale"] = stale }
        };
        await database.Store.SaveAsync(account);
        var client = Client((_, call) => call switch
        {
            1 => Response(new { s0 = Search([Item("fresh"), Item("stale")], 2) }),
            2 => Response(new
            {
                n0 = ActivityNode(false, [Comment(1)], []),
                n1 = ActivityNode(false, [Comment(2)], [], commentCursor: "next")
            }),
            _ => Response(new { n0 = (object?)null },
                new[] { new { type = "NOT_FOUND", message = "No longer accessible", path = new[] { "n0" } } })
        });
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(account.User);
        await workspace.RefreshAsync();
        var result = workspace.Account!.Items;
        Assert.HasCount(3, requests);
        Assert.AreEqual("comment:1", result["fresh"].Activities.Single().Id);
        Assert.IsNull(result["fresh"].ActivityError);
        Assert.AreEqual("comment:99", result["stale"].Activities.Single().Id);
        Assert.AreEqual(oldTime, result["stale"].ActivityCheckedAt);
        Assert.AreEqual("No longer accessible", result["stale"].ActivityError);
        CollectionAssert.AreEquivalent(new[] { "comment:99" }, result["stale"].AcknowledgedIds.ToArray());
    }

    [TestMethod]
    public async Task Http200RateLimitErrorsPauseSubsequentRequests()
    {
        var client = Client((_, _) => Response(null,
            new[] { new { type = "RATE_LIMITED", message = "API rate limit exceeded" } }));
        var first = await client.GetActivityBatchAsync([Tracked("a")], default);
        var second = await client.GetActivityBatchAsync([Tracked("b")], default);
        Assert.HasCount(1, requests);
        Assert.AreEqual(429, ((GitHubException)first["a"].Error!).StatusCode);
        Assert.AreEqual(429, ((GitHubException)second["b"].Error!).StatusCode);
        Assert.IsNull(first["a"].Value);
    }

    [TestMethod]
    public async Task ExhaustedRateLimitDoesNotPublishIncompleteHistory()
    {
        var client = Client((_, _) =>
        {
            var response = Response(new { n0 = ActivityNode(false, [Comment(1)], [], commentCursor: "next") });
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("Retry-After", "60");
            return response;
        });
        var result = (await client.GetActivityBatchAsync([Tracked("a")], default))["a"];
        Assert.HasCount(1, requests);
        Assert.IsNull(result.Value);
        Assert.AreEqual(429, ((GitHubException)result.Error!).StatusCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("repeated")]
    public async Task InvalidCursorsFailRatherThanLoopOrTreatHistoryAsComplete(string cursor)
    {
        var client = Client((_, _) => Response(new
        {
            n0 = new { __typename = "Issue", comments = new { nodes = new[] { Comment(1) }, pageInfo = new { hasNextPage = true, endCursor = cursor } } }
        }));
        var result = (await client.GetActivityBatchAsync([Tracked("a")], default))["a"];
        Assert.IsNotNull(result.Error);
        Assert.IsNull(result.Value);
        Assert.HasCount(cursor.Length == 0 ? 1 : 2, requests);
    }

    [TestMethod]
    public async Task CancellationDuringHttpIsNotConvertedToSuccessfulOrStaleData()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        http = new(new FakeHandler(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Response(null);
        }));
        var client = new GraphQlGitHubClient(http, _ => Task.FromResult("test-token"), database.Store);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetActivityBatchAsync([Tracked("a")], cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }

    [TestMethod]
    public async Task WorkspaceBatchesSearchesAndHydratesSharedItemsOnlyOnce()
    {
        var client = Client((_, call) => call == 1 ?
            Response(new { s0 = Search([Item("shared")], 1), s1 = Search([Item("shared")], 1), s2 = Search([Item("shared")], 1) }) :
            Response(new { n0 = ActivityNode(false, [Comment(1)], []) }));
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(new("1", "viewer"));
        await workspace.RefreshAsync();
        Assert.HasCount(2, requests, "Three overlapping queries and their shared activity should take two batched requests.");
        Assert.HasCount(1, workspace.Account!.Items);
        Assert.IsTrue(workspace.Account.Queries.All(query => query.ItemIds.SequenceEqual(["shared"])));
        await workspace.MarkReadAsync("shared", ["comment:1"]);
        Assert.AreEqual(ReadStatus.Read, ReadStatePolicy.Evaluate(workspace.Account.Items["shared"], "1", []));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QuietPollOrManualRefreshUsesOneBatchedSearchAndNoDiscussionRequests(bool manual)
    {
        var now = DateTimeOffset.UtcNow;
        var count = manual ? 347 : 500;
        var account = new AccountSnapshot { User = new("1", "viewer") };
        for (var index = 0; index < count; index++)
        {
            var item = Tracked($"cached-{index}");
            item.HasBaseline = true;
            item.ActivityCheckedAt = manual ? now.AddHours(-1) : now.AddMinutes(-1);
            account.Items.Add(item.Id, item);
        }
        foreach (var query in account.Queries)
        {
            query.ItemIds = account.Items.Keys.ToList();
            query.TotalCount = count;
            query.LastRefreshedAt = now.AddMinutes(-2);
            query.LastFullRefreshedAt = manual ? now.AddHours(-1) : now.AddMinutes(-10);
        }
        await database.Store.SaveAsync(account);
        var client = Client((request, _) =>
        {
            var variables = request.GetProperty("variables");
            foreach (var name in new[] { "q0", "q1", "q2" })
            {
                Assert.Contains("updated:>=", variables.GetProperty(name).GetString()!);
                Assert.EndsWith("sort:updated-desc", variables.GetProperty(name).GetString()!);
            }
            Assert.DoesNotContain("node(id:", request.GetProperty("query").GetString()!);
            return Response(new { s0 = Search([], 0), s1 = Search([], 0), s2 = Search([], 0) });
        });
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(account.User);
        if (manual) await workspace.CheckForUpdatesAsync();
        else await workspace.PollAsync();
        Assert.HasCount(1, requests, "A quiet poll must not add per-item discussion requests.");
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.ItemIds.Count == count && query.TotalCount == count));
    }

    [TestMethod]
    public async Task PartialPermissionFailureStillCommitsAccessibleActivity()
    {
        var client = Client((_, _) => Response(new { n0 = (object?)null, n1 = ActivityNode(false, [Comment(2)], []) },
            new[] { new { type = "FORBIDDEN", message = "Repository permission required", path = new[] { "n0", "comments" } } }));
        var result = await client.GetActivityBatchAsync([Tracked("private"), Tracked("public")], default);
        Assert.AreEqual(403, ((GitHubException)result["private"].Error!).StatusCode);
        Assert.IsNull(result["private"].Value);
        Assert.AreEqual("comment:2", result["public"].Value!.Single().Id);
    }

    [TestMethod]
    public async Task HttpAuthenticationErrorsEscapeWorkspaceRefresh()
    {
        var client = Client((_, _) => FakeHandler.Json("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized));
        var workspace = new WorkspaceService(database.Store, client);
        await workspace.OpenAsync(new("1", "viewer"));
        var error = await Assert.ThrowsAsync<GitHubException>(() => workspace.RefreshAsync());
        Assert.AreEqual(401, error.StatusCode);
        Assert.Contains("Bad credentials", workspace.Progress);
        Assert.HasCount(1, requests);
        Assert.IsTrue(workspace.Account!.Queries.All(query => query.Error is not null));
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("{\"errors\":\"invalid\"}")]
    [DataRow("{\"errors\":[null]}")]
    [DataRow("{\"data\":{\"n0\":null}}")]
    [DataRow("{malformed")]
    public async Task MalformedOrInaccessibleResponsesNeverBecomeEmptySuccessfulActivity(string body)
    {
        var client = Client((_, _) => FakeHandler.Json(body));
        var result = (await client.GetActivityBatchAsync([Tracked("a")], default))["a"];
        Assert.IsNotNull(result.Error);
        Assert.IsNull(result.Value);
        Assert.HasCount(1, requests);
    }

    [TestMethod]
    public async Task SearchPaginationFailureDoesNotPublishPartiallyRetrievedMembership()
    {
        var client = Client((_, call) => call == 1 ?
            Response(new { s0 = Search([Item("partial")], 2, "next"), s1 = Search([Item("complete")], 1) }) :
            Response(new { s0 = (object?)null },
                new[] { new { type = "FORBIDDEN", message = "Search no longer accessible", path = new[] { "s0" } } }));
        var queries = new[] { new SavedQuery { Expression = "first" }, new SavedQuery { Expression = "second" } };
        var result = await client.SearchBatchAsync(queries, "viewer", default);
        Assert.IsNull(result[queries[0].Id].Value);
        Assert.IsNotNull(result[queries[0].Id].Error);
        Assert.AreEqual("complete", result[queries[1].Id].Value!.Items.Single().Id);
    }

    private GraphQlGitHubClient Client(Func<JsonElement, int, HttpResponseMessage> respond)
    {
        http?.Dispose();
        http = new(new FakeHandler(async (request, ct) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("https://api.github.com/graphql", request.RequestUri!.AbsoluteUri);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement.Clone();
            requests.Add(root);
            TestContext.WriteLine("GRAPHQL_REQUEST:" + root.GetRawText());
            return respond(root, requests.Count);
        }));
        return new(http, _ => Task.FromResult("test-token"), database.Store);
    }

    private static HttpResponseMessage Response(object? data, object? errors = null) =>
        FakeHandler.Json(JsonSerializer.Serialize(errors is null ? new { data } : (object)new { data, errors }));
    private static object Connection(object[] nodes, string? cursor = null) =>
        new { nodes, pageInfo = new { hasNextPage = cursor is not null, endCursor = cursor } };
    private static object Search(object[] nodes, int total, string? cursor = null) =>
        new { nodes, issueCount = total, pageInfo = new { hasNextPage = cursor is not null, endCursor = cursor } };
    private static object ActivityNode(bool pr, object[] comments, object[] reviews, string? commentCursor = null, string? reviewCursor = null) =>
        new { __typename = pr ? "PullRequest" : "Issue", comments = Connection(comments, commentCursor), reviews = Connection(reviews, reviewCursor) };
    private static TrackedItem Tracked(string id, bool pr = false) =>
        new() { Id = id, Repository = "o/r", Number = 1, IsPullRequest = pr, AuthorId = "2", AuthorLogin = "other", CreatedAt = DateTimeOffset.Parse(Time) };
    private static object Item(string id, bool pr = false) => new
    {
        id, __typename = pr ? "PullRequest" : "Issue", number = 1, title = "Title", body = "Body",
        url = $"https://github.com/o/r/issues/1", createdAt = Time, updatedAt = Time,
        repository = new { nameWithOwner = "o/r" }, author = new { databaseId = 2, login = "other" },
        issueState = "OPEN", prState = "OPEN", comments = new { totalCount = 0 },
        totalCommentsCount = 0, reviews = new { totalCount = 0, nodes = Array.Empty<object>() }
    };
    private static Dictionary<string, object?> Comment(int id, string login = "other", int authorId = 2, string created = Time) => new()
    {
        ["id"] = $"comment{id}", ["databaseId"] = id, ["fullDatabaseId"] = id,
        ["createdAt"] = created, ["updatedAt"] = created, ["publishedAt"] = created, ["body"] = "Hello",
        ["url"] = "https://github.com/o/r/pull/1#comment", ["state"] = "SUBMITTED",
        ["author"] = new { databaseId = authorId, login }
    };
    private static object Review(int id, object[] comments, string state = "APPROVED", string submitted = Time, string? commentCursor = null) => new
    {
        id = $"review{id}", databaseId = id, fullDatabaseId = id, state, submittedAt = state == "PENDING" ? null : submitted,
        body = "", url = "https://github.com/o/r/pull/1#review", author = new { databaseId = 2, login = "other" },
        comments = Connection(comments, commentCursor)
    };
}
