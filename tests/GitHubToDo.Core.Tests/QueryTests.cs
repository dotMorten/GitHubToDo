using GitHubToDo.Core;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class QueryTests
{
    [TestMethod]
    public void NewAccountsStartWithItemsInvolvingMeWithoutATypeFilter()
    {
        var account = new AccountSnapshot();
        var query = account.Queries[0];
        Assert.AreEqual("Items involving me", query.Name);
        Assert.AreEqual("state:open archived:false involves:@me", query.Expression);
        Assert.AreEqual("state:open archived:false involves:viewer", QueryParser.Parse(query.Expression, "viewer").Search);
    }

    [TestMethod]
    public void NewAccountsStartWithItemsAssignedToMeWithoutATypeFilter()
    {
        var account = new AccountSnapshot();
        var query = account.Queries.Single(query => query.Name == "Items assigned to me");
        Assert.AreEqual("assignee:@me state:open archived:false", query.Expression);
        Assert.AreEqual("assignee:viewer state:open archived:false", QueryParser.Parse(query.Expression, "viewer").Search);
    }

    [TestMethod]
    public void ReviewQueryResolvesViewerAndSort()
    {
        var query = QueryParser.Parse("is:pr review-requested:@me state:open archived:false sort:updated-desc", "octocat");
        Assert.AreEqual("is:pr review-requested:octocat state:open archived:false", query.Search);
        Assert.AreEqual("updated", query.Sort);
        Assert.AreEqual("desc", query.Order);
    }

    [TestMethod]
    [DataRow("closed:@today", "closed:2026-10-26")]
    [DataRow("created:>@today-30d", "created:>2026-09-26")]
    [DataRow("created:>@today-1y", "created:>2025-10-26")]
    [DataRow("updated:>=@today-2w", "updated:>=2026-10-12")]
    [DataRow("merged:<=@today+1m", "merged:<=2026-11-26")]
    [DataRow("-CLOSED:@TODAY", "-CLOSED:2026-10-26")]
    [DataRow("closed:@today-7d..@today", "closed:2026-10-19..2026-10-26")]
    [DataRow("closed:2026-01-01..@today", "closed:2026-01-01..2026-10-26")]
    [DataRow("created:@today-1y..*", "created:2025-10-26..*")]
    [DataRow("closed:\"@today\"", "closed:\"2026-10-26\"")]
    public void RelativeDateQualifiersResolveBeforeSearching(string expression, string expected)
    {
        Assert.AreEqual(expected, QueryParser.Parse(expression, "viewer", new(2026, 10, 26)).Search);
        Assert.IsEmpty(QueryValidator.Validate(expression));
    }

    [TestMethod]
    public void RelativeDatesRollForwardWithoutChangingTheSavedExpression()
    {
        var saved = new SavedQuery { Expression = "closed:@today author:@me" };
        Assert.AreEqual("closed:2026-10-26 author:viewer", QueryParser.Parse(saved.Expression, "viewer", new(2026, 10, 26)).Search);
        Assert.AreEqual("closed:2026-10-27 author:viewer", QueryParser.Parse(saved.Expression, "viewer", new(2026, 10, 27)).Search);
        Assert.AreEqual("closed:@today author:@me", saved.Expression);
    }

    [TestMethod]
    public void RelativeDateArithmeticUsesCalendarMonthsAndYears()
    {
        Assert.AreEqual("created:>2024-02-29", QueryParser.Parse("created:>@today-1m", "viewer", new(2024, 3, 31)).Search);
        Assert.AreEqual("created:>2023-02-28", QueryParser.Parse("created:>@today-1y", "viewer", new(2024, 2, 29)).Search);
    }

    [TestMethod]
    public void RelativeDatesRespectGroupingUrlsAndLiteralSearchText()
    {
        const string expression = "(closed:@today OR merged:>@today-1y) label:\"closed:@today\" \"created:>@today-30d\"";
        const string expected = "( closed:2026-10-26 OR merged:>2025-10-26 ) label:\"closed:@today\" \"created:>@today-30d\"";
        var url = "https://github.com/issues?q=" + Uri.EscapeDataString(expression);
        Assert.AreEqual(expected, QueryParser.Parse(url, "viewer", new(2026, 10, 26)).Search);
    }

    [TestMethod]
    [DataRow("closed:@today-30")]
    [DataRow("created:>@today-1q")]
    [DataRow("updated:@today-nope")]
    [DataRow("merged:@today-999999999999999d")]
    [DataRow("created:@today+2147483647w")]
    [DataRow("created:@today-10000y")]
    public void InvalidRelativeDatesGiveAnExplicitError(string expression)
    {
        Assert.Throws<ArgumentException>(() => QueryParser.Parse(expression, "viewer", new(2026, 10, 26)));
        Assert.IsTrue(QueryValidator.Validate(expression).Any(issue => issue.IsError));
    }

    [TestMethod]
    public void UrlDecodesExactlyOnce()
    {
        var input = "https://github.com/issues/search?q=state%3Aopen+archived%3Afalse+involves%3A%40me+sort%3Aupdated-desc";
        Assert.AreEqual("state:open archived:false involves:octocat",
            QueryParser.Parse(input, "octocat").Search);
        Assert.AreEqual("\"a+b%20c\" involves:@me",
            QueryParser.ExtractExpression("https://github.com/issues?q=%22a%2Bb%2520c%22+involves%3A%40me"));
    }

    [TestMethod]
    public void QuotedTextIsNotBlindlyRewritten()
    {
        var query = QueryParser.Parse("\"involves:@me\" author:@me -mentions:@me label:\"needs review\"", "viewer");
        Assert.AreEqual("\"involves:@me\" author:viewer -mentions:viewer label:\"needs review\"", query.Search);
    }

    [TestMethod]
    public void ReviewsShortcutBecomesSearch()
    {
        Assert.AreEqual(QueryParser.ReviewsQuery, QueryParser.ExtractExpression("https://github.com/pulls/reviews"));
    }

    [TestMethod]
    public void CustomMixedQueryDoesNotAcquireTypeFilter()
    {
        Assert.AreEqual("repo:owner/repo state:open", QueryParser.Parse("repo:owner/repo state:open", "viewer").Search);
    }

    [TestMethod]
    public void MyPrUrlAlwaysUsesLatestUpdatesFirst()
    {
        var parsed = QueryParser.Parse("https://github.com/pulls/search?q=is%3Apr+author%3A%40me+sort%3Aupdated-desc+is%3Aopen", "viewer");
        Assert.AreEqual("is:pr author:viewer is:open", parsed.Search);
        Assert.AreEqual("desc", QueryParser.Parse("is:issue sort:created-asc", "viewer").Order);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("https://example.com/issues?q=test")]
    [DataRow("http://github.com/issues?q=test")]
    [DataRow("https://github.com/owner/repo")]
    [DataRow("https://github.com/issues?q=")]
    [DataRow("sort:updated-desc")]
    [DataRow("is:pr \"unclosed")]
    public void InvalidQueriesHaveExplicitErrors(string input)
    {
        Assert.Throws<ArgumentException>(() => QueryParser.Parse(input, "viewer"));
    }

    [TestMethod]
    public void EscapedQuotesAndQuotedQualifiersArePreserved()
    {
        var input = "\"a \\\"quote\\\" involves:@me\" author:@me";
        Assert.AreEqual("\"a \\\"quote\\\" involves:@me\" author:viewer", QueryParser.Parse(input, "viewer").Search);
    }

    [TestMethod]
    [DataRow("is:issue")]
    [DataRow("is:issue sort:created-asc")]
    [DataRow("sort:comments-desc is:issue sort:magic")]
    [DataRow("is:issue SORT:relevance")]
    public void SuppliedSortQualifiersAreIgnored(string expression)
    {
        var parsed = QueryParser.Parse(expression, "viewer");
        Assert.AreEqual("is:issue", parsed.Search);
        Assert.AreEqual("updated", parsed.Sort);
        Assert.AreEqual("desc", parsed.Order);
    }

    [TestMethod]
    public void IgnoringSortDoesNotRemoveQuotedSearchText()
    {
        var parsed = QueryParser.Parse("is:issue \"sort:created-asc\" sort:comments", "viewer");
        Assert.AreEqual("is:issue \"sort:created-asc\"", parsed.Search);
        Assert.AreEqual("updated", parsed.Sort);
    }

    [TestMethod]
    public void SortWithinBooleanGroupsIsIgnoredWithoutBreakingParenthesesOrViewerQualifiers()
    {
        var parsed = QueryParser.Parse("(is:pr author:@me sort:created-asc) OR (is:issue assignee:@me sort:comments)", "viewer");
        Assert.AreEqual("( is:pr author:viewer ) OR ( is:issue assignee:viewer )", parsed.Search);
        Assert.AreEqual("updated", parsed.Sort);
        Assert.AreEqual("desc", parsed.Order);
        Assert.AreEqual("is:pr \"()\"",
            QueryParser.Parse("is:pr (sort:created) ((sort:comments)) \"()\"", "viewer").Search);
    }
}
