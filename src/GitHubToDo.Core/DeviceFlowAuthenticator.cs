using System.Net.Http.Headers;
using System.Text.Json;

namespace GitHubToDo.Core;

public sealed class DeviceFlowAuthenticator(HttpClient http, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<DeviceCode> RequestCodeAsync(string clientId, bool includePrivate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("Configure your GitHub OAuth application's client ID in Settings first.");
        using var response = await PostAsync("https://github.com/login/device/code",
            new() { ["client_id"] = clientId.Trim(), ["scope"] = includePrivate ? "repo" : "" }, ct);
        ThrowOAuthError(response.RootElement);
        var root = response.RootElement;
        var uri = new Uri(root.GetProperty("verification_uri").GetString()!);
        if (uri.Scheme != "https" || uri.Host != "github.com")
            throw new GitHubException("GitHub returned an untrusted verification URL.");
        return new(root.GetProperty("device_code").GetString()!,
            root.GetProperty("user_code").GetString()!, uri,
            root.GetProperty("expires_in").GetInt32(), root.GetProperty("interval").GetInt32());
    }

    public async Task<OAuthToken> PollAsync(string clientId, DeviceCode code, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow().AddSeconds(code.ExpiresIn);
        var interval = Math.Max(1, code.Interval);
        while (clock.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), clock, ct);
            if (clock.GetUtcNow() >= deadline)
                break;
            using var response = await PostAsync("https://github.com/login/oauth/access_token", new()
            {
                ["client_id"] = clientId,
                ["device_code"] = code.Code,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, ct);
            var root = response.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.GetString() == "authorization_pending")
                    continue;
                if (error.GetString() == "slow_down")
                {
                    interval += 5;
                    continue;
                }
                ThrowOAuthError(root);
            }
            return ParseToken(root);
        }
        throw new GitHubException("The sign-in code expired. Start sign-in again.");
    }

    public async Task<OAuthToken> RefreshAsync(string clientId, OAuthToken token, CancellationToken ct)
    {
        if (token.RefreshToken is null ||
            token.RefreshExpiresAt is { } expires && expires <= clock.GetUtcNow())
            throw new GitHubException("Your session has expired. Sign in again.", 401);
        using var response = await PostAsync("https://github.com/login/oauth/access_token", new()
        {
            ["client_id"] = clientId,
            ["refresh_token"] = token.RefreshToken,
            ["grant_type"] = "refresh_token"
        }, ct);
        ThrowOAuthError(response.RootElement);
        return ParseToken(response.RootElement);
    }

    private OAuthToken ParseToken(JsonElement root)
    {
        if (!root.TryGetProperty("access_token", out var token) || string.IsNullOrWhiteSpace(token.GetString()))
            throw new GitHubException("GitHub did not return an access token.");
        var now = clock.GetUtcNow();
        return new(token.GetString()!,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            root.TryGetProperty("expires_in", out var expiry) ? now.AddSeconds(expiry.GetInt32()) : null,
            root.TryGetProperty("refresh_token_expires_in", out var refreshExpiry) ? now.AddSeconds(refreshExpiry.GetInt32()) : null);
    }

    private async Task<JsonDocument> PostAsync(string url, Dictionary<string, string> data, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("GitHubToDo/1.0");
        request.Content = new FormUrlEncodedContent(data);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GitHubException($"GitHub sign-in failed (HTTP {(int)response.StatusCode}). Check OAuth configuration or try again.", (int)response.StatusCode);
        return JsonDocument.Parse(body);
    }

    private static void ThrowOAuthError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
            return;
        var description = root.TryGetProperty("error_description", out var text) ? text.GetString() : error.GetString();
        throw new GitHubException($"GitHub sign-in: {description}");
    }
}
