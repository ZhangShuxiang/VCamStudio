using System.Runtime.InteropServices;

namespace VCamStudio.Shared;

/// <summary>
/// Cross-process frame protocol shared by the WPF producer and the Media
/// Foundation media source running inside the Windows Frame Server.
/// </summary>
public static class FrameProtocol
{
    public const int Magic = 0x4D414356; // "VCAM" little endian
    public const int Version = 1;

    /// <summary>Bytes reserved for the header before the BGRA payload.</summary>
    public const int HeaderSize = 4096;

    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;

    public const int MaxFrameBytes = MaxWidth * MaxHeight * 4;

    public const long MappingSize = HeaderSize + MaxFrameBytes;

    // Share with authenticated users and local system. The mapping is created by
    // the Frame Server service; the interactive user must be able to open it.
    public const string SecurityDescriptor = "D:(A;;GA;;;AU)(A;;GA;;;SY)";
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
public struct FrameHeader
{
    public int Magic;
    public int Version;
    public int HeaderSize;
    public int Width;
    public int Height;
    public int Stride;
    public int Fps;
    public int Sequence;
    public long FrameNumber;
    public long TimestampMs;
    public int DataLength;
    public int Flags;
    public long Reserved;
}
