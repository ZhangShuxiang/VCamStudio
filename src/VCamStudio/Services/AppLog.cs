using VCamStudio.Shared;

namespace VCamStudio.Services;

public static class AppLog
{
    private static readonly string PrimaryDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VCamStudio",
        "logs");

    private static readonly string FallbackDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private static volatile string _activeDirectory = PrimaryDirectory;

    public static string DirectoryPath => _activeDirectory;
    public static string FilePath => Path.Combine(_activeDirectory, "vcamstudio.log");

    public static void Write(string level, string message)
    {
        _activeDirectory = FileLogger.Write(
            PrimaryDirectory,
            FallbackDirectory,
            "vcamstudio.log",
            level,
            message);
    }
}
