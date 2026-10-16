using System.Text.Json;

namespace GitHubToDo.Core;

internal static class GitHubHttp
{
    public static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    public static DateTimeOffset RetryAt(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Date is { } date) return date;
        if (response.Headers.RetryAfter?.Delta is { } delta) return DateTimeOffset.UtcNow + delta;
        if (long.TryParse(Header(response, "X-RateLimit-Reset"), out var reset))
            return DateTimeOffset.FromUnixTimeSeconds(reset).AddSeconds(1);
        return DateTimeOffset.UtcNow.AddMinutes(1);
    }

    public static string ErrorMessage(string body, string fallback)
    {
        if (!body.TrimStart().StartsWith('{')) return fallback;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()! : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
