using System.Reflection;

namespace GitHubToDo.Services;

/// <summary>Values baked in at build time (official release builds set GITHUBTODO_CLIENT_ID).</summary>
internal static class BuildInfo
{
    /// <summary>The official GitHub OAuth app client ID, or null for self-built copies that bring their own.</summary>
    public static string? ClientId { get; } = typeof(BuildInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "GitHubClientId")?.Value is { Length: > 0 } id ? id.Trim() : null;
}
