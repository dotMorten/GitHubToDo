namespace GitHubToDo.Services;

internal sealed class LocalAiSession<TModel>(Func<Task<TModel>> create, Action<TModel> dispose) where TModel : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TModel? _model;

    public async Task<TResult> RunAsync<TResult>(
        Func<TModel, CancellationToken, Task<TResult>> request, CancellationToken ct,
        Func<CancellationToken, Task>? prepare = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (prepare is not null) await prepare(ct);
            ct.ThrowIfCancellationRequested();
            // Finish native creation even when cancelled, so its result is always owned and disposed.
            _model ??= await create();
            ct.ThrowIfCancellationRequested();
            var result = await request(_model, ct);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            ReleaseModel();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try { ReleaseModel(); }
        finally { _gate.Release(); }
    }

    private void ReleaseModel()
    {
        var model = _model;
        _model = null;
        if (model is not null) dispose(model);
    }
}

internal sealed class LocalAiDownloadRequiredException() : InvalidOperationException(
    "Windows needs to download the local AI model. Confirm the download to continue.");
