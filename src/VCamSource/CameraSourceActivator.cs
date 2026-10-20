using System.Runtime.InteropServices;
using DirectN;
using VCamSource.Utilities;
using VCamStudio.Shared;

namespace VCamSource;

/// <summary>
/// COM class factory object used by the Windows virtual camera pipeline. One
/// concrete class exists per camera slot, because the virtual camera source id is
/// the CLSID of this object.
/// </summary>
[ComVisible(false)]
public abstract class CameraSourceActivatorBase : MFAttributes, IMFActivateImpl, ICustomQueryInterface
{
    protected CameraSourceActivatorBase(int slot)
    {
        Slot = slot;
        SetDefaultAttributes(this);
    }

    public int Slot { get; }

    private void SetDefaultAttributes(IMFAttributes attributes)
    {
        attributes.SetUINT32(MFConstants.MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1).ThrowOnError();
        attributes.SetGUID(MFConstants.MFT_TRANSFORM_CLSID_Attribute, GetType().GUID).ThrowOnError();
    }

    public HRESULT ActivateObject(Guid riid, out nint ppv)
    {
        CameraMediaSource? source = null;
        try
        {
            if (riid == typeof(IMFMediaSourceEx).GUID || riid == typeof(IMFMediaSource).GUID)
            {
                source = new CameraMediaSource(Slot);
                SetDefaultAttributes(source);
                ppv = ComObject.QueryObjectInterface(source, riid, false);
                if (ppv != IntPtr.Zero)
                {
                    // The returned COM reference owns the object now.
                    source = null;
                    return HRESULTS.S_OK;
                }

                source.Dispose();
                source = null;
            }

            ppv = 0;
            return HRESULTS.E_NOINTERFACE;
        }
        catch (Exception ex)
        {
            try { source?.Dispose(); } catch { }
            SourceLog.Write("ERROR", $"ActivateObject(slot {Slot}) failed: {ex}");
            ppv = 0;
            return HRESULTS.E_FAIL;
        }
    }

    public HRESULT ShutdownObject() => HRESULTS.S_OK;

    public HRESULT DetachObject() => HRESULTS.S_OK;

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
    {
        ppv = 0;
        return CustomQueryInterfaceResult.NotHandled;
    }
}

[ComVisible(true)]
[Guid("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f601")]
public sealed class CameraSource1 : CameraSourceActivatorBase
{
    public CameraSource1() : base(0) { }
}

[ComVisible(true)]
[Guid("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f602")]
public sealed class CameraSource2 : CameraSourceActivatorBase
{
    public CameraSource2() : base(1) { }
}

[ComVisible(true)]
[Guid("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f603")]
public sealed class CameraSource3 : CameraSourceActivatorBase
{
    public CameraSource3() : base(2) { }
}

[ComVisible(true)]
[Guid("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f604")]
public sealed class CameraSource4 : CameraSourceActivatorBase
{
    public CameraSource4() : base(3) { }
}

[ComVisible(true)]
[Guid("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f605")]
public sealed class CameraSource5 : CameraSourceActivatorBase
{
    public CameraSource5() : base(4) { }
}

[ComVisible(true)]
[Guid("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f606")]
public sealed class CameraSource6 : CameraSourceActivatorBase
{
    public CameraSource6() : base(5) { }
}

[ComVisible(true)]
[Guid("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f607")]
public sealed class CameraSource7 : CameraSourceActivatorBase
{
    public CameraSource7() : base(6) { }
}

[ComVisible(true)]
[Guid("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f608")]
public sealed class CameraSource8 : CameraSourceActivatorBase
{
    public CameraSource8() : base(7) { }
}
