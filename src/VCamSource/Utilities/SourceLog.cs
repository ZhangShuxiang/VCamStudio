using VCamStudio.Shared;

namespace VCamSource.Utilities;

internal static class SourceLog
{
    private static readonly string PrimaryDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VCamStudio", "logs");

    private static readonly string FallbackDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Write(string level, string message)
    {
        FileLogger.Write(
            PrimaryDirectory,
            FallbackDirectory,
            "vcamsource.log",
            level,
            message);
    }
}
