using System.Diagnostics;
using System.IO;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace GitHubToDo.Services;

internal static class CopilotAi
{
    private static CopilotClient CreateClient()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitHubToDo", "CopilotSummary");
        Directory.CreateDirectory(directory);
        return new CopilotClient(new CopilotClientOptions { UseLoggedInUser = true, WorkingDirectory = directory });
    }

    private static async Task VerifyAccountAsync(CopilotClient client, string login, CancellationToken ct)
    {
        var auth = await client.GetAuthStatusAsync(ct);
        if (!auth.IsAuthenticated)
            throw new InvalidOperationException("Sign in to Copilot CLI with 'copilot login', then check availability again.");
        if (string.IsNullOrEmpty(login) || !string.Equals(auth.Login, login, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sign in to Copilot CLI with the same GitHub account as this app, then check availability again.");
    }

    public static async Task<(bool Available, string Description)> GetAvailabilityAsync(string login, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(login))
            return (false, "Sign in to GitHub to check Copilot access.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var client = CreateClient();
            await client.StartAsync(timeout.Token);
            await VerifyAccountAsync(client, login, timeout.Token);
            await using var session = await CreateSessionAsync(client, "Check account availability.", false, timeout.Token);
#pragma warning disable GHCP001 // Account metadata is exposed through the SDK's preview RPC API.
            var account = await session.Rpc.GitHubAuth.GetStatusAsync(timeout.Token);
#pragma warning restore GHCP001
            var available = account.IsAuthenticated && !string.IsNullOrWhiteSpace(account.CopilotPlan) &&
                !string.Equals(account.CopilotPlan, "none", StringComparison.OrdinalIgnoreCase) &&
                (await client.ListModelsAsync(timeout.Token)).Count > 0;
            return available
                ? (true, $"Copilot is available for @{login}. Uses your Copilot CLI sign-in and usage allowance; prompts are sent to the cloud.")
                : (false, $"Copilot chat is not available for @{login}. Check your subscription and organization policy.");
        }
        catch (Exception) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return (false, "Copilot availability check timed out. Check your connection and try again.");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Copilot availability check failed: {ex.Message}");
            return (false, $"Copilot is unavailable: {LocalAiErrors.Describe(ex)}");
        }
    }

    public static async Task<TResult> RunAsync<TResult>(
        Func<CopilotClient, CancellationToken, Task<TResult>> request, string login, IProgress<string> status, CancellationToken ct)
    {
        status.Report("Connecting to GitHub Copilot...");
        await using var client = CreateClient();
        await client.StartAsync(ct);
        await VerifyAccountAsync(client, login, ct);
        status.Report("Preparing the GitHub Copilot cloud model...");

        return await request(client, ct);
    }

    private static Task<CopilotSession> CreateSessionAsync(CopilotClient client, string system, bool streaming, CancellationToken ct) =>
        client.CreateSessionAsync(new SessionConfig
        {
            Streaming = streaming,
            AvailableTools = [],
#pragma warning disable GHCP001 // The SDK's deny-only permission callback uses its preview decision type.
            OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Text generation cannot use tools.")),
#pragma warning restore GHCP001
            EnableConfigDiscovery = false,
            SkipCustomInstructions = true,
            EnableFileHooks = false,
            EnableHostGitOperations = false,
            EnableSkills = false,
            EnableSessionStore = false,
            SkipEmbeddingRetrieval = true,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = system }
        }, ct);

    public static async Task<string> GenerateAsync(CopilotClient client, string system, string user,
        CancellationToken ct, IProgress<string>? fragments = null)
    {
        await using var session = await CreateSessionAsync(client, system, fragments is not null, ct);
        using var subscription = session.On<AssistantMessageDeltaEvent>(message =>
        {
            if (!ct.IsCancellationRequested)
                fragments?.Report(message.Data.DeltaContent);
        });
        try
        {
            var response = await session.SendAndWaitAsync(new MessageOptions { Prompt = user },
                TimeSpan.FromMinutes(2), ct);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(response?.Data.Content))
                throw new InvalidOperationException("GitHub Copilot returned no response.");
            return response.Data.Content;
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await session.AbortAsync(cleanup.Token); }
            catch (Exception ex) { Trace.TraceError($"Copilot abort failed: {ex.Message}"); }
            throw;
        }
    }
}
