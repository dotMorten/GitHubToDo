using System.IO;
using System.Text.Json;
using Microsoft.Windows.AI.Text;
using Windows.ApplicationModel;
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

    public static async Task<LanguageModel> CreateModelAsync(CancellationToken ct)
    {
        try
        {
            var model = await LanguageModel.CreateAsync().AsTask(ct);
            Log("LanguageModel.CreateAsync succeeded");
            return model;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log($"LanguageModel.CreateAsync threw 0x{ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public static async Task<string> GenerateAsync(LanguageModel model, string system, string user, CancellationToken ct)
    {
        var prompt = system + "\n\n" + user;
        if (prompt.Length > MaxPromptChars)
            throw new InvalidOperationException("The prompt is larger than the local model's context window.");
        var operation = model.GenerateResponseAsync(prompt, new LanguageModelOptions { Temperature = 0.1f, TopK = 20, TopP = 0.9f });
        using var registration = ct.Register(() => { try { operation.Cancel(); } catch { } });
        LanguageModelResponseResult response;
        try { response = await operation; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
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
                throw new InvalidOperationException("Windows reports the on-device model (Phi Silica) as ready, but its model service fails every request (RPC 0x800706BE). This affects every app on this PC. Check Settings > Windows Update (including optional updates) for AI component updates, then restart."),
            _ => throw new InvalidOperationException($"The local model did not answer ({response.Status}{(response.ExtendedError is { } e ? $", 0x{e.HResult:X8}" : "")})."),
        };
    }

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
