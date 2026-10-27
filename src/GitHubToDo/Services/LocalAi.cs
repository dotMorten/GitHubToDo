using System.IO;
using System.Runtime.InteropServices;
#if USE_AION_INSTRUCT_PREVIEW
using AionInstructPreview.Text;
#else
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;
#endif
using Windows.Foundation;
using Windows.Storage;

namespace GitHubToDo.Services;

/// <summary>Shared on-device <see cref="LanguageModel"/> helpers for summaries and query generation.</summary>
internal static class LocalAi
{
    public const int MaxPromptChars = 12000;
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
        Func<LanguageModel, CancellationToken, Task<TResult>> request, CancellationToken ct,
        bool allowModelDownload = false, IProgress<string>? progress = null) =>
        s_session.RunAsync(request, ct, token => EnsureReadyAsync(allowModelDownload, progress, token));

    public static async Task ReleaseAsync()
    {
        try { await s_session.ResetAsync(); }
        catch (Exception ex) { Log($"LanguageModel release failed 0x{ex.HResult:X8}: {ex.Message}"); }
    }

#if USE_AION_INSTRUCT_PREVIEW
    private static Task EnsureReadyAsync(bool allowModelDownload, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report("Loading Aion Instruct Preview. The first load may take several minutes…");
        return Task.CompletedTask;
    }
#else
    private static async Task EnsureReadyAsync(bool allowModelDownload, IProgress<string>? progress, CancellationToken ct)
    {
        var ready = LanguageModel.GetReadyState();
        Log($"LanguageModel.GetReadyState = {ready}");
        if (ready == AIFeatureReadyState.NotReady && !allowModelDownload)
            throw new LocalAiDownloadRequiredException();
        if (ready is not (AIFeatureReadyState.Ready or AIFeatureReadyState.NotReady))
            throw new InvalidOperationException($"The local Windows AI model is unavailable ({ready}).");
        ct.ThrowIfCancellationRequested();
        progress?.Report(ready == AIFeatureReadyState.NotReady
            ? "Downloading or preparing the local Windows AI model…"
            : "Preparing the local Windows AI model…");
        var operation = LanguageModel.EnsureReadyAsync();
        var completion = operation.AsTask();
        try
        {
            using var registration = ct.Register(() => CancelOperation(operation));
            var result = await completion;
            Log($"LanguageModel.EnsureReadyAsync = {result.Status}" +
                $" error 0x{result.Error?.HResult ?? 0:X8} extended 0x{result.ExtendedError?.HResult ?? 0:X8}" +
                $" package installation failed = {result.PackageInstallationFailed}");
            ct.ThrowIfCancellationRequested();
            if (result.Status != AIFeatureReadyResultState.Success)
            {
                var error = result.ExtendedError is { HResult: not 0 } ? result.ExtendedError : result.Error;
                if (error is { HResult: not 0 })
                    throw new COMException("Windows could not prepare the local AI model.", error.HResult);
                throw new InvalidOperationException($"Windows could not prepare the local AI model ({result.Status}).");
            }
            progress?.Report("Loading the local Windows AI model…");
        }
        finally
        {
            await DrainAndCloseAsync(operation, completion);
        }
    }
#endif

    private static async Task<LanguageModel> CreateModelAsync()
    {
        var operation = LanguageModel.CreateAsync();
        var completion = operation.AsTask();
        try
        {
            var model = await completion;
            Log("LanguageModel.CreateAsync succeeded");
#if USE_AION_INSTRUCT_PREVIEW
            Log("LanguageModel provider = Aion Instruct Preview");
#else
            try { Log($"LanguageModel identity = {model.ModelName}, version {model.ModelVersion}"); }
            catch (Exception ex) { Log($"LanguageModel identity unavailable 0x{ex.HResult:X8}"); }
#endif
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
            Exception? extendedError = null;
#if !USE_AION_INSTRUCT_PREVIEW
            extendedError = response.ExtendedError;
#endif
            Log($"GenerateResponseAsync = {response.Status} ({prompt.Length} chars){(extendedError is { } x ? $" extended 0x{x.HResult:X8} {x.Message}" : "")}");
            ct.ThrowIfCancellationRequested();
            return response.Status switch
            {
                LanguageModelResponseStatus.Complete => response.Text,
                LanguageModelResponseStatus.PromptBlockedByContentModeration or
                LanguageModelResponseStatus.ResponseBlockedByContentModeration => throw new InvalidOperationException("Content moderation blocked the local model's answer."),
                LanguageModelResponseStatus.PromptLargerThanContext => throw new InvalidOperationException("The prompt is larger than the local model's context window."),
                LanguageModelResponseStatus.BlockedByPolicy => throw new InvalidOperationException("Local AI is blocked by device policy."),
#if !USE_AION_INSTRUCT_PREVIEW
                LanguageModelResponseStatus.LowOnVram => throw new InvalidOperationException("NPU memory is low; try again shortly."),
                LanguageModelResponseStatus.BlockedByGameMode => throw new InvalidOperationException("Local AI is paused while Game Mode is active."),
#endif
                _ when extendedError is { HResult: unchecked((int)0x800706BE) or unchecked((int)0x800706BA) } =>
                    throw new COMException("The Windows AI model service stopped responding.", extendedError.HResult),
                _ => throw new InvalidOperationException($"The local model did not answer ({response.Status}{(extendedError is { } e ? $", 0x{e.HResult:X8}" : "")})."),
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
        catch (COMException ex)
        {
            Log($"LanguageModel disposal failed 0x{ex.HResult:X8}: {ex.Message}");
        }
    }

}
