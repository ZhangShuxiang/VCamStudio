using System.Runtime.InteropServices;
using DirectN;
using VCamSource.Utilities;
using VCamStudio.Shared;
using Constants = VCamSource.Utilities.Constants;

namespace VCamSource;

/// <summary>
/// Media Foundation media source that serves composed frames from the shared
/// section. It is instantiated by the Windows Frame Server.
/// </summary>
public sealed class CameraMediaSource : MFAttributes, IMFMediaSourceEx, IMFSampleAllocatorControl, IMFGetService, IKsControl, ICustomQueryInterface, IDisposable
{
    private readonly object _lock = new();
    private readonly int _slot;
    private readonly SlotOptions _options;
    private readonly CameraMediaStream[] _streams;
    private readonly SharedFrameReader _reader;
    private IComObject<IMFMediaEventQueue>? _queue;
    private IComObject<IMFPresentationDescriptor>? _presentationDescriptor;

    private bool _shutdown;
    private bool _disposed;

    public CameraMediaSource(int slot)
    {
        _slot = slot;
        _options = SlotOptions.Load(slot);
        _options.Normalize();

        SourceLog.Write("INFO", $"Creating media source for slot {slot + 1} ({_options.Width}x{_options.Height}@{_options.Fps}).");

        _streams = new CameraMediaStream[1];
        _streams[0] = new CameraMediaStream(this, 0, _options);
        _reader = new SharedFrameReader(slot, _options.Width, _options.Height, _options.Fps);

        try
        {
            uint streamId = 0;
            Functions.MFCreateSensorProfile(KSMedia.KSCAMERAPROFILE_Legacy, 0, null, out var legacy).ThrowOnError();
            legacy.AddProfileFilter(streamId, "((RES==;FRT<=30,1;SUT==))").ThrowOnError();

            Functions.MFCreateSensorProfile(KSMedia.KSCAMERAPROFILE_HighFrameRate, 0, null, out var high).ThrowOnError();
            high.AddProfileFilter(streamId, "((RES==;FRT>=60,1;SUT==))").ThrowOnError();

            Functions.MFCreateSensorProfileCollection(out var collection).ThrowOnError();
            collection.AddProfile(legacy).ThrowOnError();
            collection.AddProfile(high).ThrowOnError();
            SetUnknown(MFConstants.MF_DEVICEMFT_SENSORPROFILE_COLLECTION, collection).ThrowOnError();
        }
        catch (Exception ex)
        {
            SourceLog.Write("WARN", "Sensor profile setup skipped: " + ex.Message);
        }

        var descriptors = new IMFStreamDescriptor[_streams.Length];
        for (int i = 0; i < descriptors.Length; i++)
        {
            _streams[i].GetStreamDescriptor(out descriptors[i]).ThrowOnError();
        }

        Functions.MFCreatePresentationDescriptor(descriptors.Length, descriptors, out var descriptor).ThrowOnError();
        _presentationDescriptor = new ComObject<IMFPresentationDescriptor>(descriptor);

        Functions.MFCreateEventQueue(out var queue).ThrowOnError();
        _queue = new ComObject<IMFMediaEventQueue>(queue);
    }

    internal SharedFrameReader Reader => _reader;
    internal SlotOptions Options => _options;
    internal int Slot => _slot;
    internal IMFMediaEventQueue? EventQueue => _queue?.Object;

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
    {
        if (iid != typeof(IKsControl).GUID && iid != typeof(IMFAttributes).GUID &&
            iid != typeof(IMFGetService).GUID && iid != typeof(IMFMediaSourceEx).GUID &&
            iid != Constants.IID_IMFDeviceSourceInternal && iid != Constants.IID_IMFDeviceSourceStatus &&
            iid != Constants.IID_IMFDeviceController && iid != Constants.IID_IMFDeviceController2)
        {
            // Unknown interfaces are deliberately allowed to fall through to the CLR.
        }

        ppv = 0;
        return CustomQueryInterfaceResult.NotHandled;
    }

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

    public HRESULT GetCharacteristics(out uint characteristics)
    {
        characteristics = (uint)_MFMEDIASOURCE_CHARACTERISTICS.MFMEDIASOURCE_IS_LIVE;
        return HRESULTS.S_OK;
    }

    public HRESULT CreatePresentationDescriptor(out IMFPresentationDescriptor presentationDescriptor)
    {
        lock (_lock)
        {
            if (_presentationDescriptor == null)
            {
                presentationDescriptor = null!;
                return HRESULTS.MF_E_SHUTDOWN;
            }

            return _presentationDescriptor.Object.Clone(out presentationDescriptor);
        }
    }

    public HRESULT Start(IMFPresentationDescriptor presentationDescriptor, nint guidTimeFormat, PROPVARIANT startPosition)
    {
        try
        {
            if (guidTimeFormat != IntPtr.Zero && Marshal.PtrToStructure<Guid>(guidTimeFormat) != Guid.Empty)
                return HRESULTS.E_INVALIDARG;

            lock (_lock)
            {
                var queue = _queue;
                var current = _presentationDescriptor;
                if (queue == null || current == null || _shutdown)
                    return HRESULTS.MF_E_SHUTDOWN;

                presentationDescriptor.GetStreamDescriptorCount(out uint count).ThrowOnError();
                if (count != _streams.Length)
                    return HRESULTS.E_INVALIDARG;

                for (uint i = 0; i < count; i++)
                {
                    presentationDescriptor.GetStreamDescriptorByIndex(i, out bool selected, out var descriptor).ThrowOnError();
                    descriptor.GetStreamIdentifier(out uint id).ThrowOnError();

                    if (id >= _streams.Length)
                        return HRESULTS.E_INVALIDARG;

                    if (selected)
                    {
                        current.Object.SelectStream(i).ThrowOnError();
                        queue.Object.QueueEventParamUnk(
                            (uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MENewStream,
                            Guid.Empty,
                            HRESULTS.S_OK,
                            _streams[id]).ThrowOnError();

                        descriptor.GetMediaTypeHandler(out var handler).ThrowOnError();
                        handler.GetCurrentMediaType(out var mediaType).ThrowOnError();
                        _streams[id].Start(mediaType).ThrowOnError();
                    }
                    else if (_streams[id].IsRunning)
                    {
                        current.Object.DeselectStream(i).ThrowOnError();
                        _streams[id].Stop().ThrowOnError();
                    }
                }

                using var value = new PropVariant(Functions.MFGetSystemTime());
                queue.Object.QueueEventParamVar(
                    (uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MESourceStarted,
                    Guid.Empty,
                    HRESULTS.S_OK,
                    value.Detached).ThrowOnError();


                return HRESULTS.S_OK;
            }
        }
        catch (Exception ex)
        {
            SourceLog.Write("ERROR", "MediaSource.Start failed: " + ex);
            return HRESULTS.E_FAIL;
        }
    }

    public HRESULT Stop()
    {
        try
        {
            lock (_lock)
            {
                var queue = _queue;
                var presentationDescriptor = _presentationDescriptor;
                if (queue == null || presentationDescriptor == null)
                    return HRESULTS.MF_E_SHUTDOWN;

                for (uint i = 0; i < _streams.Length; i++)
                {
                    if (_streams[i].IsRunning)
                        _streams[i].Stop().ThrowOnError();
                    presentationDescriptor.Object.DeselectStream(i).ThrowOnError();
                }

                using var value = new PropVariant(Functions.MFGetSystemTime());
                queue.Object.QueueEventParamVar(
                    (uint)__MIDL___MIDL_itf_mfobjects_0000_0013_0001.MESourceStopped,
                    Guid.Empty,
                    HRESULTS.S_OK,
                    value.Detached).ThrowOnError();


                return HRESULTS.S_OK;
            }
        }
        catch (Exception ex)
        {
            SourceLog.Write("ERROR", "MediaSource.Stop failed: " + ex);
            return HRESULTS.E_FAIL;
        }
    }

    public HRESULT Pause() => HRESULTS.MF_E_INVALID_STATE_TRANSITION;

    public HRESULT Shutdown()
    {
        lock (_lock)
        {
            if (_shutdown)
                return HRESULTS.S_OK;

            _shutdown = true;
            SourceLog.Write("INFO", $"Shutting down media source for slot {_slot + 1}.");

            var queue = _queue;
            if (queue != null)
            {
                try { queue.Object.Shutdown(); } catch { }
            }

            try { Attributes.DeleteAllItems(); } catch { }

            _queue?.Dispose();
            _queue = null;

            _presentationDescriptor?.Dispose();
            _presentationDescriptor = null;

            foreach (var stream in _streams)
                stream.Dispose();

            _reader.Dispose();
            return HRESULTS.S_OK;
        }
    }

    public HRESULT GetSourceAttributes(out IMFAttributes attributes)
    {
        attributes = this;
        return HRESULTS.S_OK;
    }

    public HRESULT GetStreamAttributes(uint streamIdentifier, out IMFAttributes attributes)
    {
        if (streamIdentifier >= _streams.Length)
        {
            attributes = null!;
            return HRESULTS.E_FAIL;
        }

        attributes = _streams[streamIdentifier];
        return HRESULTS.S_OK;
    }

    public HRESULT SetD3DManager(object manager)
    {
        // CPU frames are used, so a D3D manager is not required.
        return HRESULTS.S_OK;
    }

    public HRESULT SetDefaultAllocator(uint outputStreamID, object allocator)
    {
        if (outputStreamID >= _streams.Length)
            return HRESULTS.E_FAIL;

        return _streams[outputStreamID].SetAllocator(allocator);
    }

    public HRESULT GetAllocatorUsage(uint outputStreamID, out uint inputStreamID, out MFSampleAllocatorUsage usage)
    {
        if (outputStreamID >= _streams.Length)
        {
            inputStreamID = 0;
            usage = 0;
            return HRESULTS.E_FAIL;
        }

        inputStreamID = outputStreamID;
        usage = MFSampleAllocatorUsage.MFSampleAllocatorUsage_UsesCustomAllocator;
        return HRESULTS.S_OK;
    }

    public HRESULT GetService(Guid guidService, Guid riid, out object ppv)
    {
        ppv = null!;
        return HRESULTS.E_NOINTERFACE;
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
        Shutdown();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}



