using GitHubToDo.Core;

namespace GitHubToDo.Services;

/// <summary>Generates search queries with the on-device Windows AI language model.</summary>
internal static class LocalAiQueryWriter
{
    public static Task<GeneratedQuery> GenerateAsync(string description, Func<string, CancellationToken, Task<QueryCheck>> check,
        IProgress<QueryAttempt> progress, CancellationToken ct) =>
        Task.Run(() => LocalAi.RunAsync((model, token) =>
            QueryGenerator.GenerateAsync(description,
                (system, user, generationToken) => LocalAi.GenerateAsync(model, system, user, generationToken),
                check, DateOnly.FromDateTime(DateTime.Now), progress, QueryGenerator.DefaultAttempts, token), ct), ct);
}
