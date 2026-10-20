using VCamStudio.Models;

namespace VCamStudio.Services;

public static class MediaTypeDetector
{
    private static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    ];

    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".ts"
    ];

    private static readonly HashSet<string> ImageExtensionSet =
        new(ImageExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> VideoExtensionSet =
        new(VideoExtensions, StringComparer.OrdinalIgnoreCase);

    public static MediaKind Detect(string path)
    {
        string extension = Path.GetExtension(path);
        if (ImageExtensionSet.Contains(extension))
            return MediaKind.Image;
        if (VideoExtensionSet.Contains(extension))
            return MediaKind.Video;
        return MediaKind.None;
    }

    public static string BuildDialogFilter()
    {
        string imagePattern = ToPattern(ImageExtensions);
        string videoPattern = ToPattern(VideoExtensions);
        return $"支持的素材|{imagePattern};{videoPattern}|图片|{imagePattern}|视频|{videoPattern}|所有文件|*.*";
    }

    private static string ToPattern(IEnumerable<string> extensions)
        => string.Join(";", extensions.Select(extension => "*" + extension));
}
