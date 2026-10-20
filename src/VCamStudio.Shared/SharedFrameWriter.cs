using System.Buffers;
using System.Runtime.InteropServices;

namespace VCamStudio.Shared;

/// <summary>
/// Producer side of the frame bridge. It attaches to the section created by the
/// COM media source and publishes the newest composed BGRA frame using a seqlock.
/// </summary>
public sealed unsafe class SharedFrameWriter : IDisposable
{
    private IntPtr _mapping;
    private IntPtr _view;

    public bool IsOpen => _view != IntPtr.Zero;

    public bool TryOpen(int slot)
    {
        CloseHandles();

        if (TryOpenCore(CameraSlots.GetMappingName(slot), out var mapping, out var view) ||
            TryOpenCore(CameraSlots.GetLocalMappingName(slot), out mapping, out view))
        {
            _mapping = mapping;
            _view = view;

            var header = (FrameHeader*)_view;
            if (header->Magic != FrameProtocol.Magic || header->Version != FrameProtocol.Version)
            {
                CloseHandles();
                return false;
            }

            return true;
        }

        return false;
    }

    private static bool TryOpenCore(string name, out IntPtr mapping, out IntPtr view)
    {
        mapping = NativeMethods.OpenFileMappingW(NativeMethods.FileMapRead | NativeMethods.FileMapWrite, false, name);
        if (mapping == IntPtr.Zero)
        {
            view = IntPtr.Zero;
            return false;
        }

        view = NativeMethods.MapViewOfFile(mapping, NativeMethods.FileMapRead | NativeMethods.FileMapWrite, 0, 0, UIntPtr.Zero);
        if (view == IntPtr.Zero)
        {
            NativeMethods.CloseHandle(mapping);
            mapping = IntPtr.Zero;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Publishes one frame. The span must contain exactly stride * height bytes of
    /// top-down BGRA (Pbgra32 with opaque alpha) pixels.
    /// </summary>
    public void Write(ReadOnlySpan<byte> pixels, int width, int height, int stride, int fps, long frameNumber)
    {
        if (!IsOpen || width <= 0 || height <= 0 || stride < width * 4)
            return;

        long required = (long)stride * height;
        if (required > FrameProtocol.MaxFrameBytes || required > pixels.Length)
            return;

        var header = (FrameHeader*)_view;
        if (header->Magic != FrameProtocol.Magic)
        {
            // The media source may have been re-created; drop the stale view so
            // the producer can attach to the new section on its next attempt.
            CloseHandles();
            return;
        }

        int sequence = Volatile.Read(ref header->Sequence);
        if ((sequence & 1) != 0)
            sequence++;

        // Mark the section as being written, then publish the frame.
        Volatile.Write(ref header->Sequence, sequence + 1);
        Thread.MemoryBarrier();

        header->Width = width;
        header->Height = height;
        header->Stride = stride;
        header->Fps = fps;
        header->DataLength = (int)required;
        header->FrameNumber = frameNumber;
        header->TimestampMs = Environment.TickCount64;
        header->Flags = 0;

        byte* destination = (byte*)_view + FrameProtocol.HeaderSize;
        ref byte source = ref MemoryMarshal.GetReference(pixels);
        fixed (byte* sourcePtr = &source)
        {
            Buffer.MemoryCopy(sourcePtr, destination, FrameProtocol.MaxFrameBytes, required);
        }

        Thread.MemoryBarrier();
        Volatile.Write(ref header->Sequence, sequence + 2);
    }

    public void Close()
    {
        CloseHandles();
    }

    private void CloseHandles()
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
    }

    public void Dispose() => CloseHandles();
}
