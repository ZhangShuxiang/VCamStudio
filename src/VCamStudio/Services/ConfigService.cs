using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using VCamStudio.Models;
using VCamStudio.Shared;

namespace VCamStudio.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string DefaultPath
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable("VCAM_CONFIG");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try
                {
                    return Path.GetFullPath(custom);
                }
                catch (Exception ex)
                {
                    AppLog.Write("WARN", $"VCAM_CONFIG 路径无效，已忽略: {custom} ({ex.Message})");
                }
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dotted = Path.Combine(home, ".vcam");
            return Directory.Exists(dotted) ? Path.Combine(dotted, "config.json") : dotted;
        }
    }

    public string DefaultDirectory => Path.GetDirectoryName(DefaultPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string PortablePath => Path.Combine(AppContext.BaseDirectory, "config.vcam");

    public string FallbackPath
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "VCamStudio", "config.vcam");
        }
    }

    public AppConfig CreateDefault()
    {
        return new AppConfig
        {
            Tabs =
            [
                new CameraConfig
                {
                    Title = "摄像头 1",
                    Slot = 0,
                    Width = 1280,
                    Height = 720,
                    Fps = 30
                }
            ]
        };
    }

    public AppConfig? Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
                return null;

            string json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config == null)
            {
                error = "配置文件内容为空或格式不正确。";
                return null;
            }

            Normalize(config);
            return config;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Write("ERROR", $"加载配置失败 {path}: {ex}");
            return null;
        }
    }

    public bool Save(string path, AppConfig config, out string? error)
    {
        error = null;
        try
        {
            Normalize(config);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Write("ERROR", $"保存配置失败 {path}: {ex}");
            return false;
        }
    }

    public static void Normalize(AppConfig config)
    {
        config.Version = 1;
        if (config.Tabs == null || config.Tabs.Count == 0)
        {
            config.Tabs =
            [
                new CameraConfig { Title = "摄像头 1", Slot = 0 }
            ];
        }

        if (config.Tabs.Count > CameraSlots.Count)
            config.Tabs = config.Tabs.Take(CameraSlots.Count).ToList();

        var used = new HashSet<int>();
        for (int i = 0; i < config.Tabs.Count; i++)
        {
            var tab = config.Tabs[i];
            if (string.IsNullOrWhiteSpace(tab.Title))
                tab.Title = $"摄像头 {i + 1}";

            if (tab.Slot < 0 || tab.Slot >= CameraSlots.Count || !used.Add(tab.Slot))
            {
                int free = Enumerable.Range(0, CameraSlots.Count).FirstOrDefault(slot => !used.Contains(slot), -1);
                if (free < 0)
                {
                    config.Tabs = config.Tabs.Take(i).ToList();
                    break;
                }

                tab.Slot = free;
                used.Add(free);
            }

            tab.Width = Math.Clamp(MakeEven(tab.Width), 160, 1920);
            tab.Height = Math.Clamp(MakeEven(tab.Height), 120, 1080);
            tab.Fps = tab.Fps is 15 or 24 or 25 or 30 ? tab.Fps : 30;
            if (!Enum.IsDefined(tab.FitMode))
                tab.FitMode = FitMode.Contain;
            if (tab.MediaKind == MediaKind.TestPattern)
                tab.MediaKind = MediaKind.None;
        }

        if (config.SelectedIndex < 0 || config.SelectedIndex >= config.Tabs.Count)
            config.SelectedIndex = 0;
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
}
