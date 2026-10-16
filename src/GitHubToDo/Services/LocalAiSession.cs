namespace GitHubToDo.Services;

internal sealed class LocalAiSession<TModel>(Func<Task<TModel>> create, Action<TModel> dispose)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TResult> RunAsync<TResult>(
        Func<TModel, CancellationToken, Task<TResult>> request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            // Finish native creation even when cancelled, so its result is always owned and disposed.
            var model = await create();
            try
            {
                ct.ThrowIfCancellationRequested();
                return await request(model, ct);
            }
            finally
            {
                dispose(model);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
