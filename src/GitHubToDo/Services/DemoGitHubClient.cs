#if DEBUG
using GitHubToDo.Core;

namespace GitHubToDo.Services;

/// <summary>Explicit opt-in fixture. Compiled out of Release; never calls GitHub.</summary>
internal sealed class DemoGitHubClient : IGitHubClient, IItemColumnClient
{
    internal static GitHubUser User { get; } = new("demo-viewer", "demo-user");
    private static readonly DateTimeOffset Published = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    // Keep '#' inside an ordinary string: excluded #if branches still scan
    // directive-looking lines inside raw string literals.
    private const string MarkdownSmokeContent = "## Demo Markdown smoke\n\n" + """
        **Bold smoke text** and *emphasis smoke text* with `inline code`.

        - First smoke item
        - Second smoke item

        > Quoted smoke activity

        ```text
        Deterministic fenced smoke code
        ```

        [Allowed GitHub link](https://github.com/dotMorten/GitHubToDo)

        [Blocked JavaScript link](javascript:void(0))

        [Blocked HTTP link](http://example.com/markdown-smoke)

        [Blocked file link](file:///C:/markdown-smoke.txt)

        ![Blocked remote smoke image](https://example.com/markdown-smoke.png)

        Selecting this item or activating a preview link never marks it read.
        """;

    private static TrackedItem Create(string id, int number, string title, bool pr) => new()
    {
        Id = id, Number = number, Title = title, Repository = "demo/sample", IsPullRequest = pr,
        AuthorId = "demo-external", AuthorLogin = "alex", State = "open",
        CreatedAt = Published.AddDays(-2), UpdatedAt = Published,
        Body = id == "demo-external-item"
            ? $"# {title}\n\n{MarkdownSmokeContent}"
            : $"# {title}\n\nAn isolated demo workspace.\n\nSelecting or opening this item does not mark it read.",
        Url = $"https://github.com/demo/sample/{(pr ? "pull" : "issues")}/{number}"
    };

    public Task<GitHubUser> GetUserAsync(CancellationToken cancellationToken) => Task.FromResult(User);
    public Task<GitHubUser> GetUserAsync(string login, CancellationToken cancellationToken) =>
        Task.FromResult(new GitHubUser(login.Equals("dependabot[bot]", StringComparison.OrdinalIgnoreCase)
            ? "demo-bot" : "demo-" + login.ToLowerInvariant(), login));

    public Task<SearchResult> SearchAsync(string expression, string login, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueryParser.Parse(expression, login);
        IReadOnlyList<TrackedItem> items =
        [
            Create("demo-external-item", 101, "External activity — initially unread", false),
            Create("demo-self-item", 102, "Your latest reply — automatically read", true),
            Create("demo-bot-item", 103, "Bot activity — ignore dependabot[bot] to read", true)
        ];
        return Task.FromResult(new SearchResult(items, items.Count, false));
    }

    public Task<IReadOnlyList<Activity>> GetActivityAsync(TrackedItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Activity> activities = item.Id switch
        {
            "demo-self-item" =>
            [
                new("demo-self-earlier", ActivityKind.Comment, "demo-external", "alex", Published.AddHours(-1), "Please review this.", item.Url),
                new("demo-self-latest", ActivityKind.Review, User.Id, User.Login, Published, "Approved by you. Latest own activity derives read status.", item.Url)
            ],
            "demo-bot-item" =>
            [
                new("demo-bot-latest", ActivityKind.ReviewComment, "demo-bot", "dependabot[bot]", Published.AddMinutes(30), "Dependency update is ready.", item.Url)
            ],
            _ =>
            [
                new("demo-external-latest", ActivityKind.Comment, "demo-external", "alex", Published,
                    "First unread comment: please check the repro before the next release.", item.Url),
                new("demo-external-followup", ActivityKind.Review, "demo-reviewer", "sam", Published.AddMinutes(20),
                    $"Follow-up review: the repro is confirmed. Please include the fix in the next release.\n\n{MarkdownSmokeContent}", item.Url)
            ]
        };
        return Task.FromResult(activities);
    }

    public Task<IReadOnlyDictionary<string, BatchResult<ItemColumnMetadata>>> GetColumnMetadataBatchAsync(
        IReadOnlyList<TrackedItem> items, bool includeProjects, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyDictionary<string, BatchResult<ItemColumnMetadata>>>(items.ToDictionary(item => item.Id,
            item => new BatchResult<ItemColumnMetadata>(new(
                item.IsPullRequest ? ["enhancement", "needs review"] : ["bug", "triage"],
                item.IsPullRequest ? null : "Bug", "Next release",
                [new("demo-project", "demo/Roadmap (#1)", "P1", "3")], true), null)));
    }
}
#endif
