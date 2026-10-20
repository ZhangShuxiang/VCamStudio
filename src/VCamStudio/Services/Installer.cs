using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;
using VCamStudio.Shared;

namespace VCamStudio.Services;

internal static class Installer
{
    private const string PayloadResourcePrefix = "VCamStudio.Payload.";

    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VCamStudio");

    public static string SourceDirectory => Path.Combine(InstallRoot, "Source");
    public static string ComHostPath => Path.Combine(SourceDirectory, "VCamSource.comhost.dll");
    public static string SlotsDirectory => Path.Combine(InstallRoot, "slots");
    public static string LogsDirectory => Path.Combine(InstallRoot, "logs");
    public static string ManifestPath => Path.Combine(InstallRoot, "install.json");

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsRegistered()
    {
        for (int i = 0; i < CameraSlots.Count; i++)
        {
            if (!IsClsidRegistered(CameraSlots.GetClsid(i)))
                return false;
        }

        return File.Exists(ComHostPath);
    }

    public static bool IsClsidRegistered(Guid clsid)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid:D}}}\InprocServer32");
            string? value = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string registered = value.Trim().Trim('"');
            return string.Equals(
                Path.GetFullPath(registered),
                Path.GetFullPath(ComHostPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string? GetInstalledVersion()
    {
        try
        {
            if (!File.Exists(ManifestPath))
                return null;

            foreach (string line in File.ReadAllLines(ManifestPath))
            {
                if (line.StartsWith("version=", StringComparison.OrdinalIgnoreCase))
                    return line[8..].Trim();
            }
        }
        catch
        {
            // Ignore malformed manifests; the caller will offer a repair.
        }

        return null;
    }

    public static int RunInstall()
    {
        if (!IsAdministrator)
        {
            AppLog.Write("ERROR", "安装组件需要管理员权限。");
            return 5;
        }

        try
        {
            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(SlotsDirectory);
            Directory.CreateDirectory(LogsDirectory);

            string[] requiredFiles =
            [
                "VCamSource.comhost.dll",
                "VCamSource.dll",
                "VCamSource.deps.json",
                "VCamSource.runtimeconfig.json",
                "VCamStudio.Shared.dll",
                "DirectNCore.dll"
            ];

            foreach (string file in requiredFiles)
            {
                string destination = Path.Combine(SourceDirectory, file);
                if (TryExtractPayload(file, destination))
                    continue;

                // Development builds can still ship the payload next to the exe;
                // the published single-file build always embeds it.
                string source = Path.Combine(AppContext.BaseDirectory, file);
                if (!File.Exists(source))
                    throw new FileNotFoundException($"安装包缺少文件: {file}", source);

                File.Copy(source, destination, true);
            }

            GrantDirectoryAccess(SourceDirectory, "RX");
            GrantDirectoryAccess(SlotsDirectory, "M");
            GrantDirectoryAccess(LogsDirectory, "M");

            int registerExit = RunProcess(
                Path.Combine(Environment.SystemDirectory, "regsvr32.exe"),
                $"/s \"{ComHostPath}\"");

            if (registerExit != 0)
                throw new InvalidOperationException($"regsvr32 返回错误代码 {registerExit}。");

            for (int i = 0; i < CameraSlots.Count; i++)
            {
                if (!IsClsidRegistered(CameraSlots.GetClsid(i)))
                    throw new InvalidOperationException($"COM 类注册失败: {CameraSlots.GetClsid(i):B}");
            }

            File.WriteAllText(
                ManifestPath,
                $"version={AppVersion}{Environment.NewLine}installed={DateTime.Now:O}{Environment.NewLine}path={SourceDirectory}{Environment.NewLine}");

            AppLog.Write("INFO", $"虚拟摄像头组件安装成功: {SourceDirectory}");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "安装虚拟摄像头组件失败: " + ex);
            return 1;
        }
    }

    public static int RunUninstall()
    {
        if (!IsAdministrator)
            return 5;

        try
        {
            if (File.Exists(ComHostPath))
            {
                RunProcess(
                    Path.Combine(Environment.SystemDirectory, "regsvr32.exe"),
                    $"/s /u \"{ComHostPath}\"");
            }

            if (Directory.Exists(SourceDirectory))
                Directory.Delete(SourceDirectory, true);

            if (File.Exists(ManifestPath))
                File.Delete(ManifestPath);

            AppLog.Write("INFO", "虚拟摄像头组件已卸载。");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "卸载虚拟摄像头组件失败: " + ex);
            return 1;
        }
    }

    private static void GrantDirectoryAccess(string directory, string rights)
    {
        // Use the well-known Users SID so this works on non-English Windows too.
        int exit = RunProcess(
            Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            $"\"{directory}\" /grant *S-1-5-32-545:(OI)(CI){rights} /T /C /Q");

        if (exit != 0)
            throw new InvalidOperationException($"设置目录权限失败 ({rights}): {directory}");
    }

    private static bool TryExtractPayload(string file, string destination)
    {
        using Stream? stream = typeof(Installer).Assembly.GetManifestResourceStream(PayloadResourcePrefix + file);
        if (stream == null)
            return false;

        string temporary = destination + ".tmp";
        try
        {
            using (var output = File.Create(temporary))
                stream.CopyTo(output);

            File.Move(temporary, destination, true);
            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // A locked temp file is harmless; the next install retries.
            }
        }
    }

    private static int RunProcess(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            return -1;

        process.WaitForExit(30_000);
        if (!process.HasExited)
        {
            try { process.Kill(true); } catch { }
            return -2;
        }

        return process.ExitCode;
    }
}
