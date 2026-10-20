using System.ComponentModel;
using System.Diagnostics;
using VCamStudio.Models;

namespace VCamStudio.Services;

public sealed class CameraComponentService
{
    public InstallState GetState(out string message)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            message = "仅支持 Windows 11（内部版本 22000 或更高）。";
            return InstallState.NotInstalled;
        }

        if (!Installer.IsRegistered())
        {
            message = "虚拟摄像头 COM 组件尚未安装。";
            return InstallState.NotInstalled;
        }

        string? installed = Installer.GetInstalledVersion();
        // The 1.0.2 multi-camera fix lives in the WPF application. The 1.0.1
        // source already contains the custom-allocator fix, so it is compatible.
        System.Version minimum = new(1, 0, 3);
        if (!string.IsNullOrWhiteSpace(installed) &&
            System.Version.TryParse(installed, out var installedVersion) &&
            installedVersion < minimum)
        {
            message = $"组件版本 {installed} 过旧，需要修复到 1.0.3 或更高。";
            return InstallState.NeedsUpdate;
        }

        message = "虚拟摄像头组件已安装，可以开启摄像头。";
        return InstallState.Ready;
    }

    public async Task<(bool Success, string Message)> InstallAsync()
    {
        if (Installer.IsAdministrator)
        {
            int exit = await Task.Run(Installer.RunInstall);
            return exit == 0
                ? (true, "虚拟摄像头组件已安装。")
                : (false, $"安装失败（退出代码 {exit}）。详细信息见日志：{AppLog.FilePath}");
        }

        try
        {
            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                return (false, "无法定位应用程序可执行文件。");

            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                Arguments = "--install",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return (false, "无法启动安装程序。");

            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
                return (true, "虚拟摄像头组件已安装。");

            return (false, $"安装未完成（退出代码 {process.ExitCode}）。详细信息见日志：{AppLog.FilePath}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "已取消管理员授权，虚拟摄像头组件未安装。");
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "启动安装程序失败: " + ex);
            return (false, "安装失败：" + ex.Message);
        }
    }
}


