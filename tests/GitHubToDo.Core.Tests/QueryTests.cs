using GitHubToDo.Core;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class QueryTests
{
    [TestMethod]
    public void ReviewQueryResolvesViewerAndSort()
    {
        var query = QueryParser.Parse("is:pr review-requested:@me state:open archived:false sort:updated-desc", "octocat");
        Assert.AreEqual("is:pr review-requested:octocat state:open archived:false", query.Search);
        Assert.AreEqual("updated", query.Sort);
        Assert.AreEqual("desc", query.Order);
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
