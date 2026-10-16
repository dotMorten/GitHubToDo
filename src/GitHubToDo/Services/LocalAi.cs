using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage;

namespace GitHubToDo.Services;

/// <summary>Shared Phi Silica (<see cref="LanguageModel"/>) helpers for summaries and query generation.</summary>
internal static class LocalAi
{
    public const string LafFeatureId = "com.microsoft.windows.ai.languagemodel";
    public const string LocalConfigFileName = "githubtodo.local.json";
    public const int MaxPromptChars = 12000;
    private static int s_unlockAttempted;
    private static readonly object s_logLock = new();
    private static readonly LocalAiSession<LanguageModel> s_session = new(CreateModelAsync, DisposeModel);

    /// <summary>Appends a diagnostic line to LocalState\local-ai.log (no prompt or discussion content).</summary>
    public static void Log(string message)
    {
        try
        {
            lock (s_logLock)
                File.AppendAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "local-ai.log"),
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static Task<TResult> RunAsync<TResult>(
        Func<LanguageModel, CancellationToken, Task<TResult>> request, CancellationToken ct) =>
        s_session.RunAsync(request, ct);

    private static async Task<LanguageModel> CreateModelAsync()
    {
        TryUnlock();
        var ready = LanguageModel.GetReadyState();
        Log($"LanguageModel.GetReadyState = {ready}");
        if (ready != AIFeatureReadyState.Ready)
            throw new InvalidOperationException("The local Windows AI model is not ready. No model will be downloaded.");
        var operation = LanguageModel.CreateAsync();
        var completion = operation.AsTask();
        try
        {
            var model = await completion;
            Log("LanguageModel.CreateAsync succeeded");
            return model;
        }
        catch (Exception ex)
        {
            Log($"LanguageModel.CreateAsync threw 0x{ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            await DrainAndCloseAsync(operation, completion);
        }
    }

    public static async Task<string> GenerateAsync(LanguageModel model, string system, string user, CancellationToken ct,
        IProgress<string>? fragments = null)
    {
        ct.ThrowIfCancellationRequested();
        var prompt = system + "\n\n" + user;
        if (prompt.Length > MaxPromptChars)
            throw new InvalidOperationException("The prompt is larger than the local model's context window.");
        var operation = model.GenerateResponseAsync(prompt, new LanguageModelOptions { Temperature = 0.1f, TopK = 20, TopP = 0.9f });
        var completion = operation.AsTask();
        try
        {
            // Await native completion without a cancellable task wrapper before disposing its model.
            using var registration = ct.Register(() => CancelOperation(operation));
            if (fragments is not null)
                operation.Progress = (_, token) =>
                {
                    if (!ct.IsCancellationRequested) fragments.Report(token);
                };
            LanguageModelResponseResult response;
            try { response = await completion; }
            catch (Exception ex)
            {
                Log($"GenerateResponseAsync threw 0x{ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            Log($"GenerateResponseAsync = {response.Status} ({prompt.Length} chars){(response.ExtendedError is { } x ? $" extended 0x{x.HResult:X8} {x.Message}" : "")}");
            ct.ThrowIfCancellationRequested();
            return response.Status switch
            {
                LanguageModelResponseStatus.Complete => response.Text,
                LanguageModelResponseStatus.PromptBlockedByContentModeration or
                LanguageModelResponseStatus.ResponseBlockedByContentModeration => throw new InvalidOperationException("Content moderation blocked the local model's answer."),
                LanguageModelResponseStatus.PromptLargerThanContext => throw new InvalidOperationException("The prompt is larger than the local model's context window."),
                LanguageModelResponseStatus.BlockedByPolicy => throw new InvalidOperationException("Local AI is blocked by device policy."),
                LanguageModelResponseStatus.LowOnVram => throw new InvalidOperationException("NPU memory is low; try again shortly."),
                LanguageModelResponseStatus.BlockedByGameMode => throw new InvalidOperationException("Local AI is paused while Game Mode is active."),
                _ when response.ExtendedError is { HResult: unchecked((int)0x800706BE) or unchecked((int)0x800706BA) } =>
                    throw new COMException("The Windows AI model service stopped responding.", response.ExtendedError.HResult),
                _ => throw new InvalidOperationException($"The local model did not answer ({response.Status}{(response.ExtendedError is { } e ? $", 0x{e.HResult:X8}" : "")})."),
            };
        }
        finally
        {
            await DrainAndCloseAsync(operation, completion);
        }
    }

    private static void CancelOperation(IAsyncInfo operation)
    {
        try { operation.Cancel(); }
        catch (COMException ex)
        {
            Log($"AI cancellation failed 0x{ex.HResult:X8}: {ex.Message}");
        }
    }

    private static async Task DrainAndCloseAsync(IAsyncInfo operation, Task completion)
    {
        if (!completion.IsCompleted) CancelOperation(operation);
        try { await completion; }
        catch (Exception ex)
        {
            // Preserve the request's original error while still releasing its native operation.
            Log($"AI operation finished unsuccessfully during cleanup 0x{ex.HResult:X8}: {ex.Message}");
        }
        try
        {
            operation.Close();
            Log("AI operation closed");
        }
        catch (COMException ex)
        {
            Log($"AI operation close failed 0x{ex.HResult:X8}: {ex.Message}");
        }
    }

    private static void DisposeModel(LanguageModel model)
    {
        try
        {
            model.Dispose();
            Log("LanguageModel disposed");
        }
        catch (COMException ex) when (IsDisconnected(ex))
        {
            Log($"LanguageModel disposal failed 0x{ex.HResult:X8}: {ex.Message}");
        }
    }

    private static bool IsDisconnected(COMException ex) =>
        ex.HResult is unchecked((int)0x800706BE) or unchecked((int)0x800706BA) or
            unchecked((int)0x80010108) or unchecked((int)0x80000013);

    /// <summary>
    /// Optionally unlocks the Limited Access Feature from GITHUBTODO_LAF_TOKEN / GITHUBTODO_LAF_ATTESTATION
    /// or a git-ignored githubtodo.local.json (lafToken, lafAttestation) in LocalState. Otherwise the OS policy applies.
    /// </summary>
    public static void TryUnlock()
    {
        if (Interlocked.Exchange(ref s_unlockAttempted, 1) == 1) return;
        try
        {
            var token = Environment.GetEnvironmentVariable("GITHUBTODO_LAF_TOKEN");
            var attestation = Environment.GetEnvironmentVariable("GITHUBTODO_LAF_ATTESTATION");
            if (string.IsNullOrWhiteSpace(token))
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, LocalConfigFileName);
                if (!File.Exists(path)) return;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                token = document.RootElement.TryGetProperty("lafToken", out var t) ? t.GetString() : null;
                attestation = document.RootElement.TryGetProperty("lafAttestation", out var a) ? a.GetString() : null;
            }
            if (!string.IsNullOrWhiteSpace(token))
                Log($"TryUnlockFeature = {LimitedAccessFeatures.TryUnlockFeature(LafFeatureId, token, attestation ?? "").Status}");
            else Log("No LAF token configured; relying on OS policy.");
        }
        catch
        {
            // Unlocking is optional; readiness reporting explains any remaining restriction.
        }
    }
}
