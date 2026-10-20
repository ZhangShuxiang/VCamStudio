using System.Text;

namespace VCamStudio.Shared;

/// <summary>
/// Best-effort rolling text log used by both the WPF process and the media
/// source. Each process supplies its preferred directory and a fallback next to
/// the executable, so logging never throws into a COM or UI call path.
/// </summary>
public static class FileLogger
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private static readonly object Gate = new();

    /// <summary>
    /// Appends one line to the first writable directory. Returns the directory
    /// that was used so callers can expose the actual log location to the user.
    /// </summary>
    public static string Write(
        string primaryDirectory,
        string fallbackDirectory,
        string fileName,
        string level,
        string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {level} {message}{Environment.NewLine}";
        lock (Gate)
        {
            if (TryAppend(primaryDirectory, fileName, line))
                return primaryDirectory;

            TryAppend(fallbackDirectory, fileName, line);
            return fallbackDirectory;
        }
    }

    private static bool TryAppend(string directory, string fileName, string line)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                string old = path + ".old";
                File.Delete(old);
                File.Move(path, old);
            }

            File.AppendAllText(path, line, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
