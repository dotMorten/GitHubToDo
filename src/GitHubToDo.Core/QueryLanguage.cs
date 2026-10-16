namespace GitHubToDo.Core;

public enum QualifierValueKind { Choice, User, Repository, Text, Date, Number }

/// <summary>Documentation for one GitHub issue/pull-request search qualifier.</summary>
public sealed record QualifierDoc(
    string Name, string Category, QualifierValueKind Kind, string Summary, string Syntax,
    IReadOnlyList<string> Examples, IReadOnlyList<string>? Values = null, bool SupportsMe = false)
{
    public string Token(string value, bool exclude = false)
    {
        value = value.Trim();
        if (value.Length == 0) throw new ArgumentException($"Enter a value for {Name}:.");
        if (value.Any(char.IsWhiteSpace) && !(value.StartsWith('"') && value.EndsWith('"') && value.Length > 1))
            value = $"\"{value.Replace("\"", "", StringComparison.Ordinal)}\"";
        return $"{(exclude ? "-" : "")}{Name}:{value}";
    }
}

/// <summary>
/// The subset of GitHub's issue and pull-request search syntax this app documents, validates and
/// offers to the local AI model. Sources: docs.github.com "Searching issues and pull requests".
/// </summary>
public static class QueryLanguage
{
    public const string Overview =
        "Combine qualifiers with spaces (implicit AND). Prefix a qualifier with - to exclude it. " +
        "Use OR and parentheses to group alternatives, e.g. (author:@me OR assignee:@me). " +
        "Quote values that contain spaces, e.g. label:\"good first issue\". Free words search titles and bodies. " +
        "@me means the signed-in account. Dates use YYYY-MM-DD with >, >=, <, <= or start..end ranges. " +
        "sort: is ignored: results are always the most recently updated first.";

    private static readonly string[] TrueFalse = ["true", "false"];

    public static IReadOnlyList<QualifierDoc> Qualifiers { get; } =
    [
        new("is", "Type and state", QualifierValueKind.Choice,
            "Filters by kind, state, visibility or lock status. Repeat it to combine, e.g. is:pr is:open.",
            "is:VALUE", ["is:pr is:open", "is:issue is:closed", "is:pr is:merged", "is:pr is:draft"],
            ["issue", "pr", "open", "closed", "merged", "unmerged", "draft", "locked", "unlocked", "public", "private"]),
        new("type", "Type and state", QualifierValueKind.Text,
            "Restricts to issues or pull requests. Also matches an organization's issue type, such as Bug.",
            "type:issue | type:pr | type:TYPE", ["type:pr", "type:issue", "type:Bug"]),
        new("state", "Type and state", QualifierValueKind.Choice,
            "Open or closed issues and pull requests.", "state:open | state:closed", ["state:open", "state:closed"],
            ["open", "closed"]),
        new("reason", "Type and state", QualifierValueKind.Choice,
            "Why an issue was closed.", "reason:completed | reason:\"not planned\"", ["is:closed reason:completed"],
            ["completed", "\"not planned\""]),
        new("draft", "Type and state", QualifierValueKind.Choice,
            "Draft pull requests (true) or pull requests ready for review (false).", "draft:true | draft:false",
            ["is:pr draft:false"], TrueFalse),
        new("archived", "Type and state", QualifierValueKind.Choice,
            "Whether the repository is archived. Use archived:false to hide archived repositories.",
            "archived:true | archived:false", ["archived:false"], TrueFalse),
        new("review", "Type and state", QualifierValueKind.Choice,
            "Pull-request review status.", "review:VALUE", ["is:pr review:approved", "is:pr review:changes_requested"],
            ["none", "required", "approved", "changes_requested"]),
        new("status", "Type and state", QualifierValueKind.Choice,
            "Combined commit status (CI checks) of a pull request.", "status:VALUE", ["is:pr status:failure"],
            ["pending", "success", "failure"]),
        new("linked", "Type and state", QualifierValueKind.Choice,
            "Issues linked to a pull request (linked:pr), or pull requests linked to an issue (linked:issue).",
            "linked:pr | linked:issue", ["is:issue -linked:pr"], ["pr", "issue"]),

        new("author", "People", QualifierValueKind.User,
            "Created by a user or app. Bots use app/NAME, e.g. author:app/dependabot.", "author:USER",
            ["author:@me", "-author:app/dependabot"], SupportsMe: true),
        new("assignee", "People", QualifierValueKind.User,
            "Assigned to a user.", "assignee:USER", ["assignee:@me"], SupportsMe: true),
        new("mentions", "People", QualifierValueKind.User,
            "Mentions a user with @username.", "mentions:USER", ["mentions:@me"], SupportsMe: true),
        new("commenter", "People", QualifierValueKind.User,
            "A user has commented.", "commenter:USER", ["commenter:@me"], SupportsMe: true),
        new("involves", "People", QualifierValueKind.User,
            "A user authored, is assigned, was mentioned or commented: a shortcut for all four.",
            "involves:USER", ["involves:@me"], SupportsMe: true),
        new("review-requested", "People", QualifierValueKind.User,
            "Review requested from a user, directly or through one of their teams.", "review-requested:USER",
            ["is:pr review-requested:@me"], SupportsMe: true),
        new("user-review-requested", "People", QualifierValueKind.User,
            "Review requested from the user directly, not through a team.", "user-review-requested:USER",
            ["is:pr user-review-requested:@me"], SupportsMe: true),
        new("reviewed-by", "People", QualifierValueKind.User,
            "Pull requests a user has reviewed.", "reviewed-by:USER", ["is:pr reviewed-by:@me"], SupportsMe: true),
        new("team-review-requested", "People", QualifierValueKind.Text,
            "Review requested from a team.", "team-review-requested:ORG/TEAM", ["team-review-requested:github/docs"]),
        new("team", "People", QualifierValueKind.Text,
            "Mentions a team.", "team:ORG/TEAM", ["team:github/docs"]),

        new("repo", "Where", QualifierValueKind.Repository,
            "In one repository. Combine several with OR.", "repo:OWNER/NAME",
            ["repo:microsoft/microsoft-ui-xaml", "(repo:dotnet/maui OR repo:dotnet/runtime)"]),
        new("org", "Where", QualifierValueKind.Text,
            "In any repository owned by an organization.", "org:ORG", ["org:microsoft"]),
        new("user", "Where", QualifierValueKind.Text,
            "In any repository owned by a user account.", "user:LOGIN", ["user:dotMorten"]),
        new("in", "Where", QualifierValueKind.Choice,
            "Where free-text words must appear.", "in:title | in:body | in:comments", ["crash in:title"],
            ["title", "body", "comments"]),
        new("language", "Where", QualifierValueKind.Text,
            "Repository language.", "language:LANGUAGE", ["language:csharp"]),

        new("label", "Labels and metadata", QualifierValueKind.Text,
            "Has a label. Repeat for all of several labels; quote names with spaces.", "label:NAME",
            ["label:bug", "label:\"good first issue\"", "-label:wontfix"]),
        new("milestone", "Labels and metadata", QualifierValueKind.Text,
            "In a milestone.", "milestone:NAME", ["milestone:\"v1.0\""]),
        new("no", "Labels and metadata", QualifierValueKind.Choice,
            "Missing metadata, such as no:assignee for unassigned items.", "no:VALUE", ["no:assignee", "no:label"],
            ["label", "milestone", "assignee", "project"]),
        new("head", "Labels and metadata", QualifierValueKind.Text,
            "Pull requests from a branch.", "head:BRANCH", ["head:feature/login"]),
        new("base", "Labels and metadata", QualifierValueKind.Text,
            "Pull requests into a branch.", "base:BRANCH", ["base:main"]),

        new("created", "Dates and counts", QualifierValueKind.Date,
            "Creation date.", "created:>=YYYY-MM-DD | created:START..END", ["created:>=2026-01-01"]),
        new("updated", "Dates and counts", QualifierValueKind.Date,
            "Last update date.", "updated:>=YYYY-MM-DD", ["updated:>=2026-06-01"]),
        new("closed", "Dates and counts", QualifierValueKind.Date,
            "Closing date.", "closed:<YYYY-MM-DD", ["closed:2026-01-01..2026-03-31"]),
        new("merged", "Dates and counts", QualifierValueKind.Date,
            "Pull-request merge date.", "merged:>=YYYY-MM-DD", ["is:pr merged:>=2026-06-01"]),
        new("comments", "Dates and counts", QualifierValueKind.Number,
            "Number of comments.", "comments:>N | comments:N..M", ["comments:>10"]),
        new("interactions", "Dates and counts", QualifierValueKind.Number,
            "Reactions plus comments.", "interactions:>N", ["interactions:>50"]),
        new("reactions", "Dates and counts", QualifierValueKind.Number,
            "Number of reactions.", "reactions:>N", ["reactions:>=20"]),

        new("sort", "Sorting", QualifierValueKind.Text,
            "Accepted but ignored: this app always shows the most recently updated items first.",
            "sort:updated-desc", ["sort:updated-desc"])
    ];

    private static readonly Dictionary<string, QualifierDoc> s_byName =
        Qualifiers.ToDictionary(doc => doc.Name, StringComparer.OrdinalIgnoreCase);

    public static QualifierDoc? Find(string name) => s_byName.GetValueOrDefault(name);

    /// <summary>Compact reference for a small local language model's context window.</summary>
    public static string Reference()
    {
        var lines = new List<string> { Overview, "Qualifiers:" };
        foreach (var doc in Qualifiers.Where(doc => doc.Name != "sort"))
        {
            var values = doc.Values is { Count: > 0 } ? $" Values: {string.Join(", ", doc.Values)}." : "";
            lines.Add($"- {doc.Syntax} — {doc.Summary}{values}{(doc.SupportsMe ? " Accepts @me." : "")}");
        }
        return string.Join('\n', lines);
    }
}
