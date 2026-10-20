using System.Text.Json;
using System.Text.Json.Serialization;

namespace VCamStudio.Shared;

/// <summary>
/// Per-slot output format. The COM media source runs in the Frame Server service
/// and cannot ask the UI for its current settings, so the UI writes this small
/// file before starting / restarting a camera.
/// </summary>
public sealed class SlotOptions
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SlotOptions Clone() => new() { Width = Width, Height = Height, Fps = Fps };

    public void Normalize()
    {
        Width = Math.Clamp(MakeEven(Width), 160, FrameProtocol.MaxWidth);
        Height = Math.Clamp(MakeEven(Height), 120, FrameProtocol.MaxHeight);
        Fps = Math.Clamp(Fps, 1, 60);
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VCamStudio", "slots");

    public static string GetPath(int slot) => Path.Combine(DirectoryPath, $"slot{slot + 1}.json");

    public static SlotOptions Load(int slot)
    {
        try
        {
            string path = GetPath(slot);
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var options = JsonSerializer.Deserialize<SlotOptions>(json, JsonOptions);
                if (options != null)
                {
                    options.Normalize();
                    return options;
                }
            }
        }
        catch
        {
            // The media source must still be able to start with a sane default.
        }

        var defaults = new SlotOptions();
        defaults.Normalize();
        return defaults;
    }

    public static bool TrySave(int slot, SlotOptions options, out string? error)
    {
        error = null;
        try
        {
            options.Normalize();
            Directory.CreateDirectory(DirectoryPath);

            string path = GetPath(slot);
            // The installer grants Users modify on this directory. Writing the
            // small file in place also works on locked-down profiles that deny
            // creating new temporary files.
            File.WriteAllText(path, JsonSerializer.Serialize(options, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            // If the file already contains exactly these options, the media
            // source can still start correctly even when this process is not
            // allowed to rewrite ProgramData (for example a restricted token).
            try
            {
                var existing = Load(slot);
                options.Normalize();
                if (existing.Width == options.Width &&
                    existing.Height == options.Height &&
                    existing.Fps == options.Fps)
                {
                    error = null;
                    return true;
                }
            }
            catch
            {
                // Fall through to the original error.
            }

            error = ex.Message;
            return false;
        }
    }
}
