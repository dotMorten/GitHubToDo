using GitHubToDo.Core;

namespace GitHubToDo.Services;

internal static class CopilotSummarizer
{
    public static Task<string> SummarizeAsync(IReadOnlyList<UnreadSummarySource> sources, string systemPrompt,
        string login, IProgress<string> progress, IProgress<string> content, CancellationToken ct) =>
        CopilotAi.RunAsync((client, token) =>
            UnreadSummary.GenerateAsync(sources, _ => true,
                (text, fragments, partToken) => CopilotAi.GenerateAsync(client, systemPrompt, text, partToken, fragments),
                progress, content, token), login, progress, ct);
}
