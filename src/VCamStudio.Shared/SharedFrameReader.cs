using System.Runtime.InteropServices;

namespace VCamStudio.Shared;

/// <summary>
/// Consumer side of the frame bridge. It owns the named section, which is created
/// first by the media source (running as a service) and then opened by the UI.
/// </summary>
public sealed unsafe class SharedFrameReader : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _mapping;
    private IntPtr _view;
    private byte[] _frameBuffer;
    private int _width;
    private int _height;
    private int _fps;
    private long _lastSequence = -1;
    private bool _hasFrame;

    public SharedFrameReader(int slot, int defaultWidth, int defaultHeight, int defaultFps)
    {
        _width = Math.Clamp(defaultWidth, 160, FrameProtocol.MaxWidth);
        _height = Math.Clamp(defaultHeight, 120, FrameProtocol.MaxHeight);
        _fps = Math.Clamp(defaultFps, 1, 60);
        _frameBuffer = new byte[_width * _height * 4];

        if (!TryCreate(CameraSlots.GetMappingName(slot), out _mapping, out _view) &&
            !TryCreate(CameraSlots.GetLocalMappingName(slot), out _mapping, out _view))
        {
            throw new InvalidOperationException(
                "Unable to create the virtual camera frame section. Make sure the VCam Studio components are installed correctly.");
        }

        var header = (FrameHeader*)_view;
        if (header->Magic != FrameProtocol.Magic || header->Version != FrameProtocol.Version ||
            header->HeaderSize != FrameProtocol.HeaderSize)
        {
            new Span<byte>((void*)_view, FrameProtocol.HeaderSize).Clear();
            header->Magic = FrameProtocol.Magic;
            header->Version = FrameProtocol.Version;
            header->HeaderSize = FrameProtocol.HeaderSize;
            header->Width = _width;
            header->Height = _height;
            header->Stride = _width * 4;
            header->Fps = _fps;
            header->Sequence = 0;
        }
        else
        {
            _width = header->Width;
            _height = header->Height;
            _fps = header->Fps;
        }
    }

    private static bool TryCreate(string name, out IntPtr mapping, out IntPtr view)
    {
        mapping = IntPtr.Zero;
        view = IntPtr.Zero;

        IntPtr sd = IntPtr.Zero;
        try
        {
            if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    FrameProtocol.SecurityDescriptor, 1, out sd, out _))
            {
                return false;
            }

            var attributes = new NativeMethods.SecurityAttributes
            {
                Length = Marshal.SizeOf<NativeMethods.SecurityAttributes>(),
                SecurityDescriptor = sd,
                InheritHandle = 0
            };

            mapping = NativeMethods.CreateFileMappingW(
                NativeMethods.InvalidHandleValue,
                ref attributes,
                NativeMethods.PageReadWrite,
                0,
                (uint)FrameProtocol.MappingSize,
                name);

            if (mapping == IntPtr.Zero)
                return false;

            view = NativeMethods.MapViewOfFile(mapping, NativeMethods.FileMapRead | NativeMethods.FileMapWrite, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                NativeMethods.CloseHandle(mapping);
                mapping = IntPtr.Zero;
                return false;
            }

            return true;
        }
        finally
        {
            if (sd != IntPtr.Zero)
                NativeMethods.LocalFree(sd);
        }
    }

    public int Width => _width;
    public int Height => _height;
    public int Fps => _fps;
    public bool HasFrame => _hasFrame;

    public ReadOnlySpan<byte> Frame
    {
        get
        {
            lock (_gate)
            {
                return _hasFrame ? _frameBuffer.AsSpan(0, _width * _height * 4) : ReadOnlySpan<byte>.Empty;
            }
        }
    }

    /// <summary>
    /// Copies the newest frame from the shared section. Returns true when at least
    /// one frame has been published by the UI.
    /// </summary>
    public bool Update()
    {
        lock (_gate)
        {
            if (_view == IntPtr.Zero)
                return false;

            var header = (FrameHeader*)_view;
            if (header->Magic != FrameProtocol.Magic || header->Version != FrameProtocol.Version)
                return false;

            int sequence1 = Volatile.Read(ref header->Sequence);
            if ((sequence1 & 1) != 0)
                return _hasFrame;

            if (sequence1 == (int)_lastSequence)
                return _hasFrame;

            Thread.MemoryBarrier();

            int width = header->Width;
            int height = header->Height;
            int stride = header->Stride;
            int length = header->DataLength;
            int fps = header->Fps;

            if (width <= 0 || height <= 0 || stride < width * 4 || length <= 0 ||
                length > FrameProtocol.MaxFrameBytes || width > FrameProtocol.MaxWidth || height > FrameProtocol.MaxHeight)
            {
                return _hasFrame;
            }

            int required = stride * height;
            if (length < required)
                return _hasFrame;

            if (_frameBuffer.Length < required)
                _frameBuffer = new byte[required];

            Marshal.Copy((IntPtr)((byte*)_view + FrameProtocol.HeaderSize), _frameBuffer, 0, required);

            Thread.MemoryBarrier();
            int sequence2 = Volatile.Read(ref header->Sequence);
            if (sequence1 != sequence2 || (sequence2 & 1) != 0)
                return _hasFrame;

            _width = width;
            _height = height;
            _fps = fps <= 0 ? _fps : fps;
            _lastSequence = sequence2;
            _hasFrame = true;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_view != IntPtr.Zero)
            {
                NativeMethods.UnmapViewOfFile(_view);
                _view = IntPtr.Zero;
            }

            if (_mapping != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_mapping);
                _mapping = IntPtr.Zero;
            }

            _hasFrame = false;
        }
    }
}
