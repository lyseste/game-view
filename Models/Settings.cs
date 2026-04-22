using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace VCDV.Models;

public class Settings
{
    public string VideoDevice { get; set; } = "";
    public int AudioInput { get; set; } = -1;
    public int AudioOutput { get; set; } = -1;
    public string Resolution { get; set; } = "1080p";
    public string FpsMode { get; set; } = "60";
    public int CustomFps { get; set; } = 60;
    public double Volume { get; set; } = 0.5;
    public bool ShowOverlay    { get; set; } = true;
    public bool ShowRenderInfo { get; set; } = false;

    [JsonIgnore]
    public (int Width, int Height) ResolutionSize => Resolution switch
    {
        "720p"  => (1280, 720),
        "1080p" => (1920, 1080),
        "1440p" => (2560, 1440),
        "4K"    => (3840, 2160),
        _       => (1920, 1080),
    };

    [JsonIgnore]
    public int TargetFps => FpsMode switch
    {
        "30"     => 30,
        "60"     => 60,
        "120"    => 120,
        "custom" => Math.Clamp(CustomFps, 30, 240),
        _        => 60,
    };

    private static string SettingsPath()
    {
        var dir = AppContext.BaseDirectory;
        return Path.Combine(dir, "game-view_settings.json");
    }

    public static Settings Load()
    {
        var path = SettingsPath();
        if (!File.Exists(path))
            return new Settings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to load settings from {Path}: {Error}", path, ex.Message);
            return new Settings();
        }
    }

    public void Save()
    {
        var path = SettingsPath();
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to save settings to {Path}: {Error}", path, ex.Message);
        }
    }

    public Settings Clone() => new()
    {
        VideoDevice    = VideoDevice,
        AudioInput     = AudioInput,
        AudioOutput    = AudioOutput,
        Resolution     = Resolution,
        FpsMode        = FpsMode,
        CustomFps      = CustomFps,
        Volume         = Volume,
        ShowOverlay    = ShowOverlay,
        ShowRenderInfo = ShowRenderInfo,
    };
}
