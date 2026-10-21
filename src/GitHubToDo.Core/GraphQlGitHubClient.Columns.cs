using System.Globalization;
using System.Text.Json;

namespace GitHubToDo.Core;

public sealed partial class GraphQlGitHubClient
{
    private readonly GitHubClient publicColumns = new(http, _ => Task.FromResult(""), store);
    private const string ProjectValueFields =
        "__typename ... on ProjectV2ItemFieldSingleSelectValue { name } " +
        "... on ProjectV2ItemFieldTextValue { text } ... on ProjectV2ItemFieldNumberValue { number } " +
        "... on ProjectV2ItemFieldMultiSelectValue { value }";

    public async Task<IReadOnlyDictionary<string, BatchResult<ItemColumnMetadata>>> GetColumnMetadataBatchAsync(
        IReadOnlyList<TrackedItem> items, bool includeProjects, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, BatchResult<ItemColumnMetadata>>();
        foreach (var batch in items.DistinctBy(item => item.Id).Chunk(GitHubBatching.ItemsPerRequest))
        {
            var work = batch.Select(item => new ColumnWork(item)).ToArray();
            while (work.Any(job => job.Error is null && !job.Page.Done))
            {
                var request = new GraphRequest();
                foreach (var job in work.Where(job => job.Error is null && !job.Page.Done))
                {
                    var index = request.Parts.Count;
                    var id = request.Variable($"id{index}", "ID!", job.Item.Id);
                    var after = request.Variable($"a{index}", "String", job.Page.Cursor);
                    var fields = $"milestone {{ title }} labels(first: 100, after: {after}) {{ {PageFields} nodes {{ name }} }}";
                    request.Add($"n{index}", $"node(id: {id}) {{ __typename " +
                        $"... on Issue {{ issueType {{ name }} {fields} }} ... on PullRequest {{ {fields} }} }}", job, node =>
                    {
                        RequireColumnItem(node);
                        job.IssueType = node.TryGetProperty("issueType", out var issueType) ? Text(issueType, "name") : null;
                        job.Milestone = Text(node.GetProperty("milestone"), "title");
                        var labels = node.GetProperty("labels");
                        foreach (var label in Nodes(labels))
                            job.Labels.Add(label.GetProperty("name").GetString()!);
                        job.Page.Advance(labels);
                    });
                }
                await ExecuteAsync(request, cancellationToken);
            }
            foreach (var job in work.Where(job => job.Error is GitHubException { StatusCode: 403 } error &&
                error.Message.Contains("OAuth App access restrictions", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var metadata = await publicColumns.GetColumnMetadataAsync(job.Item, cancellationToken);
                    job.Labels.Clear();
                    job.Labels.UnionWith(metadata.Labels);
                    job.IssueType = metadata.IssueType;
                    job.Milestone = metadata.Milestone;
                    job.Error = null;
                }
                catch (Exception error) when (BatchErrors.IsRecoverable(error, cancellationToken))
                {
                    // Anonymous access only recovers public metadata; private/inaccessible items remain errors.
                    job.Error = error;
                    if (error is GitHubException { StatusCode: 429 })
                    {
                        foreach (var pending in work.Where(pending => pending.Error is not null))
                            pending.Error = error;
                        break;
                    }
                }
            }
            if (includeProjects)
                await ReadProjectColumnsAsync(work.Where(job => job.Error is null).ToArray(), cancellationToken);
            foreach (var job in work)
            {
                var projects = job.ProjectError is null && includeProjects
                    ? job.Projects.Values.ToArray() : job.Item.ColumnMetadata?.Projects ?? [];
                results[job.Item.Id] = new(job.Error is not null ? null : new(
                    job.Labels.Order(StringComparer.OrdinalIgnoreCase).ToArray(), job.IssueType, job.Milestone,
                    projects, includeProjects && job.ProjectError is null ||
                        job.Item.ColumnMetadata?.IncludesProjects == true, job.ProjectError?.Message), job.Error);
            }
        }
        return results;
    }

    private async Task ReadProjectColumnsAsync(ColumnWork[] columns, CancellationToken ct)
    {
        var work = columns.Select(column => new ProjectColumnWork(column)).ToArray();
        while (work.Any(job => job.Error is null && !job.Page.Done))
        {
            var request = new GraphRequest();
            foreach (var job in work.Where(job => job.Error is null && !job.Page.Done))
            {
                var index = request.Parts.Count;
                var id = request.Variable($"id{index}", "ID!", job.Column.Item.Id);
                var after = request.Variable($"a{index}", "String", job.Page.Cursor);
                var fields = $"projectItems(first: 100, after: {after}, includeArchived: true) {{ {PageFields} nodes {{ " +
                    "project { id title number owner { ... on User { login } ... on Organization { login } } } " +
                    $"priority: fieldValueByName(name: \"Priority\") {{ {ProjectValueFields} }} " +
                    $"effort: fieldValueByName(name: \"Effort\") {{ {ProjectValueFields} }} }} }}";
                request.Add($"p{index}", $"node(id: {id}) {{ __typename ... on Issue {{ {fields} }} " +
                    $"... on PullRequest {{ {fields} }} }}", job, node =>
                {
                    RequireColumnItem(node);
                    var connection = node.GetProperty("projectItems");
                    foreach (var item in Nodes(connection))
                    {
                        var project = item.GetProperty("project");
                        if (project.ValueKind != JsonValueKind.Object)
                            throw new GitHubException("A GitHub project is inaccessible. Previously cached project values are retained.", 403);
                        var projectId = project.GetProperty("id").GetString()!;
                        var owner = Text(project.GetProperty("owner"), "login");
                        var name = $"{owner}/{project.GetProperty("title").GetString()} (#{project.GetProperty("number").GetInt32()})";
                        job.Column.Projects[projectId] = new(projectId, name,
                            ProjectValue(item.GetProperty("priority")), ProjectValue(item.GetProperty("effort")));
                    }
                    job.Page.Advance(connection);
                });
            }
            await ExecuteAsync(request, ct);
        }
        foreach (var job in work)
        {
            if (job.Error is GitHubException { StatusCode: 401 or 429 })
                job.Column.Error = job.Error;
            job.Column.ProjectError = job.Error is GitHubException { StatusCode: 403 } error
                ? new GitHubException("Project fields require read:project access. Enable GitHub Projects in Settings and sign in again. " + error.Message, 403)
                : job.Error;
        }
    }

    private static void RequireColumnItem(JsonElement node)
    {
        if (Text(node, "__typename") is not ("Issue" or "PullRequest"))
            throw new GitHubException("The item is missing or inaccessible. Cached column values are retained.", 404);
    }

    private static string? ProjectValue(JsonElement value) => Text(value, "__typename") switch
    {
        null => null,
        "ProjectV2ItemFieldSingleSelectValue" => Text(value, "name"),
        "ProjectV2ItemFieldTextValue" => Text(value, "text"),
        "ProjectV2ItemFieldMultiSelectValue" => Text(value, "value"),
        "ProjectV2ItemFieldNumberValue" => value.GetProperty("number") is { ValueKind: JsonValueKind.Number } number
            ? number.GetDecimal().ToString(CultureInfo.CurrentCulture) : null,
        var type => throw new GitHubException($"Unsupported project field value: {type}. Cached project values are retained.")
    };

    private sealed class ColumnWork(TrackedItem item) : Work
    {
        public TrackedItem Item { get; } = item;
        public Page Page { get; } = new();
        public HashSet<string> Labels { get; } = new(StringComparer.Ordinal);
        public string? IssueType { get; set; }
        public string? Milestone { get; set; }
        public Dictionary<string, ProjectColumnValue> Projects { get; } = [];
        public Exception? ProjectError { get; set; }
    }

    private sealed class ProjectColumnWork(ColumnWork column) : Work
    {
        public ColumnWork Column { get; } = column;
        public Page Page { get; } = new();
    }
}
