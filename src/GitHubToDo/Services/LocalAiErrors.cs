using System.Runtime.InteropServices;

namespace GitHubToDo.Services;

internal static class LocalAiErrors
{
    private const int RpcFailed = unchecked((int)0x800706BE);
    private const int RpcUnavailable = unchecked((int)0x800706BA);
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>Turns platform AI failures into a single readable sentence.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        COMException { HResult: RpcFailed or RpcUnavailable } =>
            "the Windows AI model service stopped responding. Try again.",
        COMException { HResult: AccessDenied } or UnauthorizedAccessException =>
            "access to the on-device AI model was denied.",
        COMException error =>
            $"the Windows AI runtime failed (0x{error.HResult:X8}).",
        _ => FirstLine(ex.Message),
    };

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "unknown error." : line;
    }
}
