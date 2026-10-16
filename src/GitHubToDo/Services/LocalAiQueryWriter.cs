using GitHubToDo.Core;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;

namespace GitHubToDo.Services;

/// <summary>Generates search queries with the on-device Windows AI language model.</summary>
internal static class LocalAiQueryWriter
{
    public static Task<GeneratedQuery> GenerateAsync(string description, Func<string, CancellationToken, Task<QueryCheck>> check,
        IProgress<QueryAttempt> progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            LocalAi.TryUnlock();
            var ready = LanguageModel.GetReadyState();
            LocalAi.Log($"LocalAiQueryWriter GetReadyState = {ready}");
            if (ready != AIFeatureReadyState.Ready)
                throw new InvalidOperationException("The local Windows AI model is not ready. No model will be downloaded.");
            using var model = await LocalAi.CreateModelAsync(ct);
            return await QueryGenerator.GenerateAsync(description,
                (system, user, token) => LocalAi.GenerateAsync(model, system, user, token),
                check, DateOnly.FromDateTime(DateTime.Now), progress, QueryGenerator.DefaultAttempts, ct);
        }, ct);
}
