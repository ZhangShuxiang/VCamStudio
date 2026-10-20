using System.Text.Json.Serialization;

namespace VCamStudio.Models;

public enum MediaKind
{
    None,
    Image,
    Video,
    TestPattern
}

public enum FitMode
{
    Contain,
    Cover,
    Stretch
}

public sealed class CameraConfig
{
    public string Title { get; set; } = "摄像头";
    public int Slot { get; set; }
    public string? MediaPath { get; set; }
    public MediaKind MediaKind { get; set; } = MediaKind.None;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public FitMode FitMode { get; set; } = FitMode.Contain;
    public bool Mirror { get; set; }

    /// <summary>Whether the on-screen preview is shown. Disabling it stops
    /// preview composition and releases its bitmaps without affecting the
    /// virtual camera output.</summary>
    public bool ShowPreview { get; set; } = true;
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public int SelectedIndex { get; set; }
    public List<CameraConfig> Tabs { get; set; } = [];
}

public sealed class ResolutionOption : IEquatable<ResolutionOption>
{
    public ResolutionOption(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public string Display => $"{Width} × {Height}";

    public bool Equals(ResolutionOption? other) => other != null && Width == other.Width && Height == other.Height;
    public override bool Equals(object? obj) => Equals(obj as ResolutionOption);
    public override int GetHashCode() => HashCode.Combine(Width, Height);
    public override string ToString() => Display;
}

public sealed class FitModeOption
{
    public FitModeOption(FitMode value, string display)
    {
        Value = value;
        Display = display;
    }

    public FitMode Value { get; }
    public string Display { get; }
    public override string ToString() => Display;
}

public sealed class FrameRateOption
{
    public FrameRateOption(int value) => Value = value;
    public int Value { get; }
    public string Display => $"{Value} fps";
    public override string ToString() => Display;
}

public enum InstallState
{
    Unknown,
    Ready,
    NotInstalled,
    NeedsUpdate
}
