using System.Globalization;

namespace VCamStudio.Shared;

/// <summary>
/// The virtual camera slots exposed by this application.
/// Each slot has its own COM class / CLSID. Windows keys a virtual camera off the
/// source CLSID, therefore one registered CLSID is required per concurrent camera.
/// </summary>
public static class CameraSlots
{
    public const int Count = 8;

    // Stable CLSIDs. Never change these after shipping: Windows stores virtual
    // camera registrations against the source id.
    private static readonly Guid[] Clsids =
    [
        new("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f601"),
        new("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f602"),
        new("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f603"),
        new("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f604"),
        new("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f605"),
        new("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f606"),
        new("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f607"),
        new("a3f1c2d4-5b6e-4f70-8a91-b2c3d4e5f608"),
    ];

    public static Guid GetClsid(int slot)
    {
        ValidateSlot(slot);
        return Clsids[slot];
    }

    /// <summary>CLSID in the "{...}" form required by MFCreateVirtualCamera.</summary>
    public static string GetSourceId(int slot) => "{" + GetClsid(slot).ToString("D").ToUpper(CultureInfo.InvariantCulture) + "}";

    public static string GetMappingName(int slot) => $"Global\\VCamStudio.Frame.{GetClsid(slot):N}";

    public static string GetLocalMappingName(int slot) => $"Local\\VCamStudio.Frame.{GetClsid(slot):N}";

    private static void ValidateSlot(int slot)
    {
        if (slot < 0 || slot >= Count)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot must be between 0 and {Count - 1}.");
    }
}
