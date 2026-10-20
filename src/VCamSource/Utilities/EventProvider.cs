using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VCamSource.Utilities;

/// <summary>
/// A deliberately tiny logging facade. Attribute access is very chatty, so info
/// messages are dropped; warnings and errors go to the shared diagnostic file.
/// </summary>
internal static class EventProvider
{
    public static void LogInfo(string? message = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? filePath = null)
    {
        // Intentionally ignored. Set a breakpoint here when diagnosing attribute traffic.
    }

    public static void LogWarning(string? message = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? filePath = null)
        => SourceLog.Write("WARN", $"{FileName(filePath)}::{methodName}: {message}");

    public static void LogError(string? message = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? filePath = null)
        => SourceLog.Write("ERROR", $"{FileName(filePath)}::{methodName}: {message}");

    public static void LogVerbose(string? message = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? filePath = null)
    {
        // Intentionally ignored.
    }

    public static void Log(TraceLevel level, string? message = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? filePath = null)
    {
        if (level >= TraceLevel.Warning)
            SourceLog.Write(level.ToString().ToUpperInvariant(), $"{FileName(filePath)}::{methodName}: {message}");
    }

    private static string FileName(string? path) => path == null ? "VCamSource" : Path.GetFileNameWithoutExtension(path);
}
