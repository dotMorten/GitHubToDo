using GitHubToDo.Core;

namespace GitHubToDo.Services;

internal static class CopilotQueryWriter
{
    public static Task<GeneratedQuery> GenerateAsync(string description, string login, Func<string, CancellationToken, Task<QueryCheck>> check,
        IProgress<QueryAttempt> progress, IProgress<string> status, CancellationToken ct) =>
        CopilotAi.RunAsync((client, token) =>
            QueryGenerator.GenerateAsync(description,
                (system, user, generationToken) => CopilotAi.GenerateAsync(client, system, user, generationToken),
                check, DateOnly.FromDateTime(DateTime.Now), progress, QueryGenerator.DefaultAttempts, token),
            login, status, ct);
}
