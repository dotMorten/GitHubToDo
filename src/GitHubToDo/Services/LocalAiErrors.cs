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
            "the Windows AI model service stopped responding. Check that the NPU/GPU drivers are current, then try again.",
        COMException { HResult: AccessDenied } or UnauthorizedAccessException =>
            "this app is not allowed to use the on-device model (Phi Silica is a Limited Access Feature).",
        _ => FirstLine(ex.Message),
    };

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "unknown error." : line;
    }
}
