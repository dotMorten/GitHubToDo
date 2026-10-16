using System.Runtime.InteropServices;
using GitHubToDo.Services;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class LocalAiSessionTests
{
    private sealed class Model
    {
        public bool Disposed { get; set; }
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task CancelledCreationStillDisposesItsModelBeforeStartingTheNextRequest()
    {
        var creating = Signal();
        var finishCreation = new TaskCompletionSource<Model>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstModel = new Model();
        var creations = 0;
        var calls = 0;
        var session = new LocalAiSession<Model>(async () =>
        {
            if (++creations == 1)
            {
                creating.SetResult();
                return await finishCreation.Task;
            }
            Assert.IsTrue(firstModel.Disposed);
            return new Model();
        }, model => model.Disposed = true);
        using var cancellation = new CancellationTokenSource();
        var first = session.RunAsync((_, _) => { calls++; return Task.FromResult("unexpected"); }, cancellation.Token);
        await creating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var next = session.RunAsync((_, _) => Task.FromResult("next"), default);
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(next.IsCompleted);
        finishCreation.SetResult(firstModel);

        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        Assert.AreEqual("next", await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(firstModel.Disposed);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task CancelledGenerationRetainsExclusiveOwnershipUntilNativeWorkFinishesAndModelIsDisposed()
    {
        var started = Signal();
        var finish = Signal();
        var creations = 0;
        Model? firstModel = null;
        var session = new LocalAiSession<Model>(() =>
        {
            if (++creations > 1) Assert.IsTrue(firstModel!.Disposed);
            return Task.FromResult(new Model());
        }, model => model.Disposed = true);
        using var cancellation = new CancellationTokenSource();
        var first = session.RunAsync(async (model, ct) =>
        {
            firstModel = model;
            started.SetResult();
            await finish.Task;
            ct.ThrowIfCancellationRequested();
            return "cancelled result";
        }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var next = session.RunAsync((_, _) => Task.FromResult("next"), default);
        Assert.AreEqual(1, creations);
        Assert.IsFalse(next.IsCompleted);
        Assert.IsFalse(firstModel!.Disposed);
        finish.SetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        Assert.AreEqual("next", await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(firstModel.Disposed);
    }

    [TestMethod]
    public async Task CancellingAQueuedRequestDoesNotCreateAModelOrInterruptTheActiveRequest()
    {
        var started = Signal();
        var finish = Signal();
        var creations = 0;
        var session = new LocalAiSession<Model>(() =>
        {
            creations++;
            return Task.FromResult(new Model());
        }, model => model.Disposed = true);
        var active = session.RunAsync(async (_, _) =>
        {
            started.SetResult();
            await finish.Task;
            return "active";
        }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var queued = session.RunAsync((_, _) => Task.FromResult("unexpected"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        Assert.AreEqual(1, creations);
        Assert.IsFalse(active.IsCompleted);
        finish.SetResult();
        Assert.AreEqual("active", await active.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task GenerationFailureDisposesTheModelAndDoesNotPoisonTheQueue()
    {
        var models = new List<Model>();
        var session = new LocalAiSession<Model>(() =>
        {
            var model = new Model();
            models.Add(model);
            return Task.FromResult(model);
        }, model => model.Disposed = true);
        await Assert.ThrowsAsync<COMException>(() => session.RunAsync<string>((_, _) =>
            throw new COMException("Service failed", unchecked((int)0x800706BE)), default));
        Assert.IsTrue(models[0].Disposed);
        Assert.AreEqual("recovered", await session.RunAsync((_, _) => Task.FromResult("recovered"), default));
        Assert.IsTrue(models[1].Disposed);
    }

    [TestMethod]
    public async Task CreationFailureReleasesTheQueueForTheNextRequest()
    {
        var attempts = 0;
        var session = new LocalAiSession<Model>(() => ++attempts == 1
            ? Task.FromException<Model>(new COMException("Creation failed"))
            : Task.FromResult(new Model()), model => model.Disposed = true);
        await Assert.ThrowsAsync<COMException>(() =>
            session.RunAsync((_, _) => Task.FromResult("unexpected"), default));
        Assert.AreEqual("recovered", await session.RunAsync((_, _) => Task.FromResult("recovered"), default));
    }

    [TestMethod]
    [DataRow(unchecked((int)0x800706BE))]
    [DataRow(unchecked((int)0x800706BA))]
    public void RpcErrorsDoNotClaimToDiagnoseOtherAppsOrRequireAnOsRestart(int hresult)
    {
        Assert.AreEqual("the Windows AI model service stopped responding. Try again.",
            LocalAiErrors.Describe(new COMException("RPC failure", hresult)));
    }
}
