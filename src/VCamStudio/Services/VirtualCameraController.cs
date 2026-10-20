using System.Runtime.InteropServices;
using DirectN;
using VCamStudio.Shared;

namespace VCamStudio.Services;

public sealed class VirtualCameraException : Exception
{
    public VirtualCameraException(string message, int hresult = 0, Exception? inner = null)
        : base(message, inner)
    {
        HResult = hresult;
    }
}

/// <summary>
/// Owns one IMFVirtualCamera instance. The object is created directly with
/// MFCreateVirtualCamera and registered for the current user only, so shutting
/// down the app removes the camera from the system.
/// </summary>
public sealed class VirtualCameraController : IDisposable
{
    private static readonly object StartupGate = new();
    private static bool _mfStarted;

    private IComObject<IMFVirtualCamera>? _camera;
    private bool _started;
    private bool _disposed;

    public bool IsRunning => _started;

    public string FriendlyName { get; private set; } = string.Empty;
    public int Slot { get; private set; }

    public void Start(int slot, string friendlyName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
            return;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            throw new VirtualCameraException("虚拟摄像头 API 仅支持 Windows 11 (Build 22000+)。");

        // A controller normally stays on one slot for its lifetime. If it is
        // reused for a different slot/name, release the old registration first
        // instead of overwriting the only reference to it.
        if (_camera != null &&
            (Slot != slot || !string.Equals(FriendlyName, friendlyName, StringComparison.Ordinal)))
        {
            CleanupCamera();
        }

        EnsureMediaFoundation();

        if (!IsTypeSupported())
            throw new VirtualCameraException("当前系统不支持 Media Foundation 软件虚拟摄像头。");

        Slot = slot;
        FriendlyName = friendlyName;

        if (_camera != null)
        {
            // Stopping a camera uses Stop() and keeps the registration alive so
            // other virtual cameras do not receive a device-removal notification.
            // Re-start the same object when this tab is switched on again.
            HRESULT restart = _camera.Object.Start(null);
            if (restart.IsSuccess)
            {
                _started = true;
                AppLog.Write("INFO", $"虚拟摄像头已重新启动: {friendlyName} ({CameraSlots.GetSourceId(slot)})");
                return;
            }

            CleanupCamera();
        }

        HRESULT hr = Functions.MFCreateVirtualCamera(
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0001.MFVirtualCameraType_SoftwareCameraSource,
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_Session,
            __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser,
            friendlyName,
            CameraSlots.GetSourceId(slot),
            null,
            0,
            out var camera);

        if (hr.IsError)
            throw CreateException("创建虚拟摄像头失败", hr);

        try
        {
            _camera = new ComObject<IMFVirtualCamera>(camera);
            hr = _camera.Object.Start(null);
            if (hr.IsError)
            {
                CleanupCamera();
                throw CreateException("启动虚拟摄像头失败", hr);
            }

            _started = true;
            AppLog.Write("INFO", $"虚拟摄像头已启动: {friendlyName} ({CameraSlots.GetSourceId(slot)})");
        }
        catch
        {
            _camera?.Dispose();
            _camera = null;
            _started = false;
            throw;
        }
    }

    /// <summary>
    /// Disables this camera without removing its registration. Keeping the
    /// registration avoids a system-wide device-removal notification that can
    /// disturb other virtual cameras.
    /// </summary>
    public void Disable()
    {
        if (_disposed || !_started || _camera == null)
            return;

        try
        {
            _camera.Object.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "停止虚拟摄像头时出错: " + ex.Message);
        }

        _started = false;
        AppLog.Write("INFO", $"虚拟摄像头已停止: {FriendlyName} ({CameraSlots.GetSourceId(Slot)})");
    }

    /// <summary>Fully removes the virtual camera. Used when the tab/app closes.</summary>
    public void Stop()
    {
        if (_disposed)
            return;

        CleanupCamera();
    }

    private void CleanupCamera()
    {
        var camera = _camera;
        _camera = null;
        _started = false;

        if (camera == null)
            return;

        AppLog.Write("INFO", $"虚拟摄像头已停止: {FriendlyName} ({CameraSlots.GetSourceId(Slot)})");

        try
        {
            if (camera.Object != null)
                camera.Object.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "停止虚拟摄像头时出错: " + ex.Message);
        }

        try
        {
            if (camera.Object != null)
                camera.Object.Remove();
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "移除虚拟摄像头时出错: " + ex.Message);
        }

        try
        {
            if (camera.Object != null)
                camera.Object.Shutdown();
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "关闭虚拟摄像头时出错: " + ex.Message);
        }

        camera.Dispose();
    }

    private static bool IsTypeSupported()
    {
        try
        {
            HRESULT hr = Functions.MFIsVirtualCameraTypeSupported(
                __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0001.MFVirtualCameraType_SoftwareCameraSource,
                out bool supported);

            return hr.IsSuccess && supported;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureMediaFoundation()
    {
        lock (StartupGate)
        {
            if (_mfStarted)
                return;

            try
            {
                MFFunctions.MFStartup();
                _mfStarted = true;
            }
            catch (Exception ex)
            {
                AppLog.Write("ERROR", "MFStartup 失败: " + ex);
                throw new VirtualCameraException("Media Foundation 初始化失败。", ex.HResult, ex);
            }
        }
    }

    private static VirtualCameraException CreateException(string prefix, HRESULT hr)
    {
        int code = hr.Value;
        string detail = code switch
        {
            unchecked((int)0x80070005) => "访问被拒绝。请在“设置 → 隐私和安全性 → 相机”中允许桌面应用访问相机。",
            unchecked((int)0x80040154) => "找不到虚拟摄像头 COM 组件，请先安装/修复组件。",
            unchecked((int)0x80070002) => "找不到虚拟摄像头组件文件，请重新安装组件。",
            _ => $"HRESULT 0x{code:X8}"
        };

        return new VirtualCameraException($"{prefix}：{detail}", code);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
