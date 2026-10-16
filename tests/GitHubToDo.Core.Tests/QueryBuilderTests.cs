using GitHubToDo.Core;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class QueryBuilderTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    [TestMethod]
    public void EveryQualifierIsDocumentedWithAValidExample()
    {
        Assert.IsGreaterThan(25, QueryLanguage.Qualifiers.Count);
        foreach (var doc in QueryLanguage.Qualifiers)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(doc.Summary), doc.Name);
            Assert.IsNotEmpty(doc.Examples, doc.Name);
            foreach (var example in doc.Examples)
                Assert.IsEmpty(QueryValidator.Validate(doc.Name == "sort" ? "is:pr " + example : example), $"{doc.Name}: {example}");
            if (doc.Kind == QualifierValueKind.Choice) Assert.IsNotNull(doc.Values, doc.Name);
        }
        var reference = QueryLanguage.Reference();
        StringAssert.Contains(reference, "review-requested:USER");
        Assert.DoesNotContain("- sort:", reference);
    }

    [TestMethod]
    public void StarterQueriesAndUrlsValidate()
    {
        foreach (var query in SavedQuery.Defaults())
            Assert.IsEmpty(QueryValidator.Validate(query.Expression), query.Expression);
        Assert.IsEmpty(QueryValidator.Validate("https://github.com/pulls/reviews"));
        Assert.IsEmpty(QueryValidator.Validate("(repo:dotnet/maui OR repo:dotnet/runtime) -label:\"needs info\" crash in:title comments:5..20 created:>=2026-01-01"));
    }

    [TestMethod]
    [DataRow("is:pr athor:@me", "Did you mean author:?")]
    [DataRow("is:pull", "is:pull is not supported")]
    [DataRow("state:merged", "Use one of: open, closed")]
    [DataRow("repo:runtime", "OWNER/NAME")]
    [DataRow("updated:>last-week", "needs a date")]
    [DataRow("comments:many", "needs a number")]
    [DataRow("label:", "needs a value")]
    [DataRow("(author:@me OR assignee:@me", "not closed")]
    [DataRow("is:pr \"unclosed", "unmatched quote")]
    public void ValidatorExplainsMistakes(string query, string? expected)
    {
        var issues = QueryValidator.Validate(query);
        if (expected is null) { Assert.IsEmpty(issues); return; }
        Assert.IsTrue(issues.Any(issue => issue.Message.Contains(expected, StringComparison.Ordinal)),
            string.Join(" | ", issues.Select(issue => issue.Message)));
    }

    [TestMethod]
    public void QuotedTextIsNotTreatedAsQualifier()
    {
        Assert.IsEmpty(QueryValidator.Validate("\"foo:bar\" is:issue"));
        Assert.AreEqual("label:\"good first issue\"", QueryLanguage.Find("label")!.Token("good first issue"));
        Assert.AreEqual("-author:app/dependabot", QueryLanguage.Find("author")!.Token("app/dependabot", exclude: true));
    }

    [TestMethod]
    [DataRow("NAME: My PRs\nQUERY: is:pr author:@me state:open", "My PRs", "is:pr author:@me state:open")]
    [DataRow("```\nName: Bugs\nQuery: `is:issue label:bug`\n```", "Bugs", "is:issue label:bug")]
    [DataRow("**QUERY:** \"is:pr review-requested:@me\"", null, "is:pr review-requested:@me")]
    [DataRow("is:issue assignee:@me", null, "is:issue assignee:@me")]
    [DataRow("I cannot help with that.", null, null)]
    public void ResponsesAreParsedTolerantly(string response, string? name, string? query)
    {
        var parsed = QueryGenerator.ParseResponse(response);
        Assert.AreEqual(name, parsed.Name);
        Assert.AreEqual(query, parsed.Query);
    }

    [TestMethod]
    public async Task GeneratorFeedsValidationAndGitHubFailuresBack()
    {
        var replies = new Queue<string>([
            "NAME: Mine\nQUERY: is:pr athor:@me",
            "NAME: Mine\nQUERY: is:pr author:@me repo:bad/repo",
            "NAME: Mine\nQUERY: is:pr author:@me state:open"
        ]);
        var prompts = new List<string>();
        var checks = new List<string>();
        var progress = new List<QueryAttempt>();
        var result = await QueryGenerator.GenerateAsync("my open pull requests",
            (system, user, _) =>
            {
                StringAssert.Contains(system, "2026-10-15");
                StringAssert.Contains(system, "involves:USER");
                prompts.Add(user);
                return Task.FromResult(replies.Dequeue());
            },
            (query, _) =>
            {
                checks.Add(query);
                return Task.FromResult(query.Contains("bad/repo") ? new QueryCheck("Repository not found") : new QueryCheck(null, 7));
            },
            Today, new SynchronousProgress(progress));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("is:pr author:@me state:open", result.Expression);
        Assert.AreEqual("Mine", result.Name);
        Assert.AreEqual(7, result.TotalCount);
        Assert.HasCount(3, progress);
        // The misspelt qualifier is rejected offline, without a GitHub request.
        CollectionAssert.AreEqual(new[] { "is:pr author:@me repo:bad/repo", "is:pr author:@me state:open" }, checks);
        StringAssert.Contains(prompts[1], "Did you mean author:?");
        StringAssert.Contains(prompts[2], "Repository not found");
        Assert.IsFalse(prompts[0].Contains("Earlier attempts", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EmptyResultsAreRetriedOnceThenAcceptedWhenRepeated()
    {
        var calls = 0;
        var result = await QueryGenerator.GenerateAsync("closed bugs from 1999",
            (_, user, _) => { calls++; return Task.FromResult("QUERY: is:issue label:bug created:<2000-01-01"); },
            (_, _) => Task.FromResult(new QueryCheck(null, 0)), Today);
        Assert.AreEqual(2, calls);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("Valid; matches nothing right now.", result.Attempts[^1].Outcome);
    }

    [TestMethod]
    public async Task GeneratorStopsAfterMaximumAttemptsAndKeepsLastCandidate()
    {
        var result = await QueryGenerator.GenerateAsync("anything",
            (_, _, _) => Task.FromResult("QUERY: is:pull"),
            (_, _) => throw new AssertFailedException("Invalid queries must not reach GitHub."), Today, maxAttempts: 2);
        Assert.IsFalse(result.Succeeded);
        Assert.HasCount(2, result.Attempts);
        Assert.AreEqual("is:pull", result.Expression);
    }

    private sealed class SynchronousProgress(List<QueryAttempt> list) : IProgress<QueryAttempt>
    {
        public void Report(QueryAttempt value) => list.Add(value);
    }
}
