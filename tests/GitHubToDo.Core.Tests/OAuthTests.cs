using System.Net;
using GitHubToDo.Core;

namespace GitHubToDo.Core.Tests;

[TestClass]
public sealed class OAuthTests
{
    [TestMethod]
    public async Task DeviceRequestUsesOwnClientIdAndExplicitPrivateScope()
    {
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            Assert.AreEqual("https://github.com/login/device/code", request.RequestUri!.AbsoluteUri);
            var form = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("client_id=application-client-id", form);
            Assert.Contains("scope=repo", form);
            Assert.DoesNotContain("client_secret", form);
            return FakeHandler.Json("""
                {"device_code":"test-code","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}
                """);
        }));
        var code = await new DeviceFlowAuthenticator(http).RequestCodeAsync("application-client-id", true, default);
        Assert.AreEqual("ABCD-EFGH", code.UserCode);
        Assert.AreEqual(5, code.Interval);
    }

    [TestMethod]
    public async Task PendingAndSlowDownHonorServerInterval()
    {
        var clock = new AdvancingClock();
        var calls = 0;
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(FakeHandler.Json(++calls switch
        {
            1 => "{\"error\":\"authorization_pending\"}",
            2 => "{\"error\":\"slow_down\"}",
            _ => "{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"expires_in\":3600,\"refresh_token_expires_in\":7200}"
        }))));
        var authenticator = new DeviceFlowAuthenticator(http, clock);
        var token = await authenticator.PollAsync("id", new("code", "USER-CODE", new("https://github.com/login/device"), 900, 5), default);
        CollectionAssert.AreEqual(new[] { 5d, 5d, 10d }, clock.Delays.ToArray());
        Assert.AreEqual("test-access", token.AccessToken);
        Assert.AreEqual(clock.GetUtcNow().AddHours(1), token.ExpiresAt);
    }

    [TestMethod]
    public async Task ExpiredCodeDoesNotPollAfterDeadline()
    {
        var calls = 0;
        using var http = new HttpClient(new FakeHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(FakeHandler.Json("{}"));
        }));
        var authenticator = new DeviceFlowAuthenticator(http, new AdvancingClock());
        await Assert.ThrowsAsync<GitHubException>(() => authenticator.PollAsync("id",
            new("code", "USER", new("https://github.com/login/device"), 3, 5), default));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task DenialIsAnExplicitFailure()
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(
            FakeHandler.Json("{\"error\":\"access_denied\",\"error_description\":\"Authorization denied\"}"))));
        await Assert.ThrowsAsync<GitHubException>(() => new DeviceFlowAuthenticator(http, new AdvancingClock()).PollAsync("id",
            new("code", "USER", new("https://github.com/login/device"), 900, 1), default));
    }

    [TestMethod]
    public async Task CancellationStopsDevicePolling()
    {
        using var http = new HttpClient(new FakeHandler((_, _) => throw new AssertFailedException("No request expected.")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new DeviceFlowAuthenticator(http).PollAsync("id",
            new("code", "USER", new("https://github.com/login/device"), 900, 1), cancellation.Token));
    }

    [TestMethod]
    public async Task RefreshSupportsRotationWithoutClientSecret()
    {
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("grant_type=refresh_token", form);
            Assert.Contains("refresh_token=old-refresh", form);
            Assert.DoesNotContain("client_secret", form);
            return FakeHandler.Json("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}");
        }));
        var result = await new DeviceFlowAuthenticator(http).RefreshAsync("id", new("old-access", "old-refresh"), default);
        Assert.AreEqual("new-refresh", result.RefreshToken);
    }

    [TestMethod]
    public async Task FailedOAuthHttpStatusDoesNotBecomeSuccess()
    {
        using var http = new HttpClient(new FakeHandler((_, _) =>
            Task.FromResult(FakeHandler.Json("{}", HttpStatusCode.BadRequest))));
        await Assert.ThrowsAsync<GitHubException>(() => new DeviceFlowAuthenticator(http).RequestCodeAsync("id", false, default));
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public List<double> Delays { get; } = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime.TotalSeconds);
            now += dueTime;
            return new TimerAdapter(new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        }
        private sealed class TimerAdapter(Timer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }
}
