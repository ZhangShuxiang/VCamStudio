using System.Runtime.InteropServices;
using DirectN;
using VCamSource.Utilities;
using VCamStudio.Shared;
using Constants = VCamSource.Utilities.Constants;

namespace VCamSource;

public sealed class CameraMediaStream : MFAttributes, IMFMediaStream2, IKsControl, IDisposable
{
    private readonly object _lock = new();
    private readonly CameraMediaSource _source;
    private readonly SlotOptions _options;
    private readonly int _index;
    private const int AllocatorSampleCount = 12;

    private IComObject<IMFMediaEventQueue>? _queue;
    private IComObject<IMFStreamDescriptor>? _descriptor;
    private IComObject<IMFVideoSampleAllocatorEx>? _allocator;
    private _MF_STREAM_STATE _state;
    private Guid _format = MFConstants.MFVideoFormat_NV12;
    private int _sampleCounter;
    private bool _disposed;

    public CameraMediaStream(CameraMediaSource source, int index, SlotOptions options)
    {
        _source = source;
        _index = index;
        _options = options;

        SetGUID(MFConstants.MF_DEVICESTREAM_STREAM_CATEGORY, KSMedia.PINNAME_VIDEO_CAPTURE).ThrowOnError();
        SetUINT32(MFConstants.MF_DEVICESTREAM_STREAM_ID, (uint)index).ThrowOnError();
        SetUINT32(MFConstants.MF_DEVICESTREAM_FRAMESERVER_SHARED, 1).ThrowOnError();
        SetUINT32(MFConstants.MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES,
            (uint)_MFFrameSourceTypes.MFFrameSourceTypes_Color).ThrowOnError();

        Functions.MFCreateEventQueue(out var queue).ThrowOnError();
        _queue = new ComObject<IMFMediaEventQueue>(queue);

        var mediaTypes = new IMFMediaType[2];

        // NV12 is listed first so most camera clients pick the native format.
        var nv12Type = MFFunctions.MFCreateMediaType();
        nv12Type.Object.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
        nv12Type.Object.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_NV12).ThrowOnError();
        nv12Type.SetSize(MFConstants.MF_MT_FRAME_SIZE, (uint)_options.Width, (uint)_options.Height);
        nv12Type.Object.SetUINT32(MFConstants.MF_MT_DEFAULT_STRIDE, (uint)_options.Width).ThrowOnError();
        nv12Type.Object.SetUINT32(MFConstants.MF_MT_INTERLACE_MODE, (uint)_MFVideoInterlaceMode.MFVideoInterlace_Progressive).ThrowOnError();
        nv12Type.Object.SetUINT32(MFConstants.MF_MT_ALL_SAMPLES_INDEPENDENT, 1).ThrowOnError();
        nv12Type.SetRatio(MFConstants.MF_MT_FRAME_RATE, (uint)_options.Fps, 1);
        nv12Type.Object.SetUINT32(MFConstants.MF_MT_AVG_BITRATE,
            (uint)(_options.Width * _options.Height * 3 * 8 * _options.Fps / 2)).ThrowOnError();
        nv12Type.SetRatio(MFConstants.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        mediaTypes[0] = nv12Type.Object;

        var rgbType = MFFunctions.MFCreateMediaType();
        rgbType.Object.SetGUID(MFConstants.MF_MT_MAJOR_TYPE, MFConstants.MFMediaType_Video).ThrowOnError();
        rgbType.Object.SetGUID(MFConstants.MF_MT_SUBTYPE, MFConstants.MFVideoFormat_RGB32).ThrowOnError();
        rgbType.SetSize(MFConstants.MF_MT_FRAME_SIZE, (uint)_options.Width, (uint)_options.Height);
        rgbType.Object.SetUINT32(MFConstants.MF_MT_DEFAULT_STRIDE, (uint)(_options.Width * 4)).ThrowOnError();
        rgbType.Object.SetUINT32(MFConstants.MF_MT_INTERLACE_MODE, (uint)_MFVideoInterlaceMode.MFVideoInterlace_Progressive).ThrowOnError();
        rgbType.Object.SetUINT32(MFConstants.MF_MT_ALL_SAMPLES_INDEPENDENT, 1).ThrowOnError();
        rgbType.SetRatio(MFConstants.MF_MT_FRAME_RATE, (uint)_options.Fps, 1);
        rgbType.Object.SetUINT32(MFConstants.MF_MT_AVG_BITRATE,
            (uint)(_options.Width * _options.Height * 4 * 8 * _options.Fps)).ThrowOnError();
        rgbType.SetRatio(MFConstants.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        mediaTypes[1] = rgbType.Object;

        Functions.MFCreateStreamDescriptor((uint)index, mediaTypes.Length, mediaTypes, out var descriptor).ThrowOnError();
        descriptor.GetMediaTypeHandler(out var handler).ThrowOnError();
        handler.SetCurrentMediaType(mediaTypes[0]).ThrowOnError();
        _descriptor = new ComObject<IMFStreamDescriptor>(descriptor);

        // The stream descriptor and current media type hold their own COM
        // references. The temporary wrappers are left to the GC.
    }

    public bool IsRunning => _state == _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING;

    public HRESULT Start(IMFMediaType? type)
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null || _state == _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING)
                return queue == null ? HRESULTS.MF_E_SHUTDOWN : HRESULTS.S_OK;

            try
            {
                if (type != null)
                {
                    type.GetGUID(MFConstants.MF_MT_SUBTYPE, out _format).ThrowOnError();
                    if (_allocator == null)
                        _allocator = MFFunctions.MFCreateVideoSampleAllocatorEx();

                    _allocator.Object.InitializeSampleAllocator(AllocatorSampleCount, type).ThrowOnError();
                }
                else if (_descriptor != null)
                {
                    _descriptor.Object.GetMediaTypeHandler(out var handler).ThrowOnError();
                    handler.GetCurrentMediaType(out var current).ThrowOnError();
                    current.GetGUID(MFConstants.MF_MT_SUBTYPE, out _format).ThrowOnError();

                    if (_allocator == null)
                        _allocator = MFFunctions.MFCreateVideoSampleAllocatorEx();

                    _allocator.Object.InitializeSampleAllocator(AllocatorSampleCount, current).ThrowOnError();
                }

                SourceLog.Write("INFO",
                    $"Stream started: {_options.Width}x{_options.Height}@{_options.Fps}, format={_format}, bounded allocator ({AllocatorSampleCount} samples).");

                queue.Object.QueueEventParamVar(
                    (uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEStreamStarted,
                    Guid.Empty,
                    HRESULTS.S_OK,
                    null).ThrowOnError();

                _state = _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING;
                return HRESULTS.S_OK;
            }
            catch (Exception ex)
            {
                SourceLog.Write("ERROR", "MediaStream.Start failed: " + ex);
                return HRESULTS.E_FAIL;
            }
        }
    }

    public HRESULT Stop()
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null)
                return HRESULTS.MF_E_SHUTDOWN;

            try
            {
                SourceLog.Write("INFO", $"Stream stopped for slot {_source.Slot + 1}.");
                _allocator?.Object.UninitializeSampleAllocator();
                queue.Object.QueueEventParamVar(
                    (uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEStreamStopped,
                    Guid.Empty,
                    HRESULTS.S_OK,
                    null).ThrowOnError();

                _state = _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED;
                return HRESULTS.S_OK;
            }
            catch (Exception ex)
            {
                SourceLog.Write("ERROR", "MediaStream.Stop failed: " + ex);
                return HRESULTS.E_FAIL;
            }
        }
    }

    public HRESULT SetAllocator(object allocator)
    {
        // This source reports UsesCustomAllocator and owns a small, bounded
        // allocator. Ignore a pipeline allocator so its lifetime cannot affect
        // our sample pool.
        return HRESULTS.S_OK;
    }

    public HRESULT Set3DManager(object manager) => HRESULTS.S_OK;

    public HRESULT GetEvent(uint flags, out IMFMediaEvent evt)
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null)
            {
                evt = null!;
                return HRESULTS.MF_E_SHUTDOWN;
            }

            return queue.Object.GetEvent(flags, out evt);
        }
    }

    public HRESULT BeginGetEvent(IMFAsyncCallback callback, object state)
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null)
                return HRESULTS.MF_E_SHUTDOWN;

            return queue.Object.BeginGetEvent(callback, state);
        }
    }

    public HRESULT EndGetEvent(IMFAsyncResult result, out IMFMediaEvent evt)
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null)
            {
                evt = null!;
                return HRESULTS.MF_E_SHUTDOWN;
            }

            return queue.Object.EndGetEvent(result, out evt);
        }
    }

    public HRESULT QueueEvent(uint type, Guid extendedType, HRESULT hrStatus, PROPVARIANT value)
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null)
                return HRESULTS.MF_E_SHUTDOWN;

            return queue.Object.QueueEventParamVar(type, extendedType, hrStatus, value);
        }
    }

    public HRESULT GetMediaSource(out IMFMediaSource mediaSource)
    {
        mediaSource = _source;
        return HRESULTS.S_OK;
    }

    public HRESULT GetStreamDescriptor(out IMFStreamDescriptor streamDescriptor)
    {
        var descriptor = _descriptor;
        if (descriptor == null)
        {
            streamDescriptor = null!;
            return HRESULTS.MF_E_SHUTDOWN;
        }

        streamDescriptor = descriptor.Object;
        return HRESULTS.S_OK;
    }

    public HRESULT RequestSample(object token)
    {
        lock (_lock)
        {
            var queue = _queue;
            if (queue == null)
                return HRESULTS.MF_E_SHUTDOWN;

            var allocator = _allocator;
            if (allocator == null)
                return HRESULTS.MF_E_SHUTDOWN;

            try
            {
                IMFSample? sample = null;
                HRESULT allocate = HRESULTS.E_FAIL;

                // The pipeline allocator is deliberately bounded. If it is
                // temporarily empty, release managed COM wrappers and retry;
                // never allocate an unbounded number of samples.
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    allocate = allocator.Object.AllocateSample(out sample);
                    if (allocate.IsSuccess)
                        break;

                    if (allocate.Value != unchecked((int)0xC00D4A3E))
                        break;

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(2);
                }

                if (allocate.IsError || sample == null)
                {
                    HRESULT failure = allocate.IsError ? allocate : HRESULTS.E_FAIL;
                    SourceLog.Write("WARN", $"Sample allocator empty after retries: {failure}");
                    return failure;
                }

                using var sampleObject = new ComObject<IMFSample>(sample);
                sample.GetBufferByIndex(0, out var buffer).ThrowOnError();
                try
                {
                    buffer.Lock(out var destination, IntPtr.Zero, IntPtr.Zero).ThrowOnError();
                    int written;
                    try
                    {
                        written = WriteFrame(destination);
                    }
                    finally
                    {
                        buffer.Unlock().ThrowOnError();
                    }

                    buffer.SetCurrentLength((uint)written).ThrowOnError();
                }
                finally
                {
                    Marshal.ReleaseComObject(buffer);
                }

                sample.SetSampleTime(Functions.MFGetSystemTime()).ThrowOnError();
                sample.SetSampleDuration(
                    Math.Max(1, 10_000_000L / Math.Max(1, _options.Fps))).ThrowOnError();

                if (token != null)
                    sample.SetUnknown(MFConstants.MFSampleExtension_Token, token).ThrowOnError();

                queue.Object.QueueEventParamUnk(
                    (uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MEMediaSample,
                    Guid.Empty,
                    HRESULTS.S_OK,
                    sample).ThrowOnError();

                if ((++_sampleCounter % 5) == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                return HRESULTS.S_OK;
            }
            catch (Exception ex)
            {
                SourceLog.Write("ERROR", "MediaStream.RequestSample failed: " + ex);
                return HRESULTS.E_FAIL;
            }
        }
    }

    private unsafe int WriteFrame(IntPtr destination)
    {
        bool hasFrame = _source.Reader.Update();
        int width = _options.Width;
        int height = _options.Height;

        if (hasFrame && (_source.Reader.Width != width || _source.Reader.Height != height))
        {
            // The UI changed resolution but the stream was not restarted. Keep the
            // negotiated media type and emit black instead of corrupt frames.
            hasFrame = false;
        }

        if (_format == MFConstants.MFVideoFormat_NV12)
        {
            int length = width * height * 3 / 2;
            byte* dst = (byte*)destination;
            if (hasFrame && _source.Reader.Frame.Length >= width * height * 4)
            {
                var frame = _source.Reader.Frame;
                fixed (byte* src = &MemoryMarshal.GetReference(frame))
                {
                    BgraToNv12(src, width, height, width * 4, dst);
                }
            }
            else
            {
                new Span<byte>(dst, width * height).Fill(16);
                new Span<byte>(dst + width * height, width * height / 2).Fill(128);
            }

            return length;
        }

        int rgbLength = width * height * 4;
        byte* rgbDst = (byte*)destination;
        if (hasFrame && _source.Reader.Frame.Length >= rgbLength)
        {
            var frame = _source.Reader.Frame;
            fixed (byte* src = &MemoryMarshal.GetReference(frame))
            {
                Buffer.MemoryCopy(src, rgbDst, rgbLength, rgbLength);
            }
        }
        else
        {
            new Span<byte>(rgbDst, rgbLength).Clear();
        }

        return rgbLength;
    }

    private static unsafe void BgraToNv12(byte* source, int width, int height, int sourceStride, byte* destination)
    {
        byte* yPlane = destination;
        byte* uvPlane = destination + width * height;

        for (int y = 0; y < height; y++)
        {
            byte* row = source + y * sourceStride;
            byte* yRow = yPlane + y * width;
            for (int x = 0; x < width; x++)
            {
                int b = row[x * 4 + 0];
                int g = row[x * 4 + 1];
                int r = row[x * 4 + 2];
                int value = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                yRow[x] = (byte)Math.Clamp(value, 16, 235);
            }
        }

        int chromaHeight = height / 2;
        int chromaWidth = width / 2;
        for (int y = 0; y < chromaHeight; y++)
        {
            byte* row0 = source + (y * 2) * sourceStride;
            byte* row1 = row0 + sourceStride;
            byte* uvRow = uvPlane + y * width;
            for (int x = 0; x < chromaWidth; x++)
            {
                int x0 = x * 8;
                int b = (row0[x0 + 0] + row0[x0 + 4] + row1[x0 + 0] + row1[x0 + 4]) / 4;
                int g = (row0[x0 + 1] + row0[x0 + 5] + row1[x0 + 1] + row1[x0 + 5]) / 4;
                int r = (row0[x0 + 2] + row0[x0 + 6] + row1[x0 + 2] + row1[x0 + 6]) / 4;

                int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                uvRow[x * 2 + 0] = (byte)Math.Clamp(u, 16, 240);
                uvRow[x * 2 + 1] = (byte)Math.Clamp(v, 16, 240);
            }
        }
    }

    public HRESULT SetStreamState(_MF_STREAM_STATE value)
    {
        if (_state == value)
            return HRESULTS.S_OK;

        switch (value)
        {
            case _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED:
                return Stop();

            case _MF_STREAM_STATE.MF_STREAM_STATE_PAUSED:
                return _state == _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING
                    ? HRESULTS.MF_E_INVALID_STATE_TRANSITION
                    : HRESULTS.S_OK;

            case _MF_STREAM_STATE.MF_STREAM_STATE_RUNNING:
                return Start(null);

            default:
                return HRESULTS.MF_E_INVALID_STATE_TRANSITION;
        }
    }

    public HRESULT GetStreamState(out _MF_STREAM_STATE value)
    {
        value = _state;
        return HRESULTS.S_OK;
    }

    public HRESULT KsProperty(ref KSIDENTIFIER property, uint propertyLength, nint propertyData, uint dataLength, out uint bytesReturned)
    {
        bytesReturned = 0;
        return HRESULT.FromWin32(Constants.ERROR_SET_NOT_FOUND);
    }

    public HRESULT KsMethod(ref KSIDENTIFIER method, uint methodLength, nint methodData, uint dataLength, out uint bytesReturned)
    {
        bytesReturned = 0;
        return HRESULT.FromWin32(Constants.ERROR_SET_NOT_FOUND);
    }

    public HRESULT KsEvent(ref KSIDENTIFIER evt, uint eventLength, nint eventData, uint dataLength, out uint bytesReturned)
    {
        bytesReturned = 0;
        return HRESULT.FromWin32(Constants.ERROR_SET_NOT_FOUND);
    }

    public new void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        lock (_lock)
        {
            try { _allocator?.Object.UninitializeSampleAllocator(); } catch { }

            _descriptor?.Dispose();
            _descriptor = null;

            _queue?.Dispose();
            _queue = null;

            _allocator?.Dispose();
            _allocator = null;

            _state = _MF_STREAM_STATE.MF_STREAM_STATE_STOPPED;
        }

        base.Dispose();
        GC.SuppressFinalize(this);
    }
}

