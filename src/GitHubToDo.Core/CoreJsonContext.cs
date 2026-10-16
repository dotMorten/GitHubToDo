using System.Text.Json.Serialization;

namespace GitHubToDo.Core;

/// <summary>Source-generated (trim-safe) JSON metadata. Default options keep the stored payload format unchanged.</summary>
[JsonSerializable(typeof(AccountSnapshot))]
[JsonSerializable(typeof(SavedQuery))]
[JsonSerializable(typeof(TrackedItem))]
[JsonSerializable(typeof(IgnoredAccount))]
[JsonSerializable(typeof(HttpCacheEntry))]
[JsonSerializable(typeof(OAuthToken))]
[JsonSerializable(typeof(IReadOnlyList<UnreadSummarySource>))]
[JsonSerializable(typeof(IReadOnlyList<Activity>))]
[JsonSerializable(typeof(GraphQlPayload))]
public sealed partial class CoreJsonContext : JsonSerializerContext;

public sealed record GraphQlPayload(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] Dictionary<string, string?> Variables);
