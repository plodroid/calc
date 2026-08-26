using System.Text.Json;

namespace DualShockStudio.Models;

public sealed class AppSettings
{
    public FeatureSettings Features { get; set; } = new();
    public TuningSettings Tuning { get; set; } = new();
    public List<KeyBinding> Bindings { get; set; } = DefaultBindings();
    public string ProfileName { get; set; } = "Default";

    public static List<KeyBinding> DefaultBindings() => new()
    {
        new("Cross", "Key", "Space"), new("Circle", "Key", "Escape"),
        new("Square", "Key", "E"), new("Triangle", "Key", "Tab"),
        new("TouchSwipeUp", "Media", "VolumeUp"), new("TouchSwipeDown", "Media", "VolumeDown"),
        new("TouchSwipeLeft", "Media", "PreviousTrack"), new("TouchSwipeRight", "Media", "NextTrack"),
        new("TouchTap", "Mouse", "Left")
    };
}

public sealed class FeatureSettings
{
    public bool MediaRemote { get; set; }
    public bool GyroMouse { get; set; }
    public bool PcControl { get; set; }
    public bool NotificationRumble { get; set; }
    public bool MusicBeatRumble { get; set; }
    public bool MicReactiveLightbar { get; set; }
    public bool GyroSteering { get; set; }
    public bool TouchpadSwipe { get; set; } = true;
    public bool TouchpadMouse { get; set; }
    public bool TouchpadScroll { get; set; } = true;
    public bool BatteryWarnings { get; set; } = true;
    public bool BatteryLightbar { get; set; }
    public bool HapticDrive { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
}

public sealed class TuningSettings
{
    public double GyroSensitivity { get; set; } = 6.0;
    public double GyroSmoothing { get; set; } = 0.32;
    public double GyroDeadzoneDps { get; set; } = 0.65;
    public double SteeringAngle { get; set; } = 9.0;
    public double SteeringReleaseAngle { get; set; } = 5.0;
    public double TouchpadSensitivity { get; set; } = 1.35;
    public int StickDeadzone { get; set; } = 12;
    public int RumbleStrength { get; set; } = 70;
    public int BeatSensitivity { get; set; } = 70;
    public int BeatMinThreshold { get; set; } = 3;
    public int SwipeThreshold { get; set; } = 150;
    public int MicNoiseGate { get; set; } = 3;
    public int MicLoudThreshold { get; set; } = 68;
    public int HapticDriveStrength { get; set; } = 75;
}

public sealed class KeyBinding
{
    public string Input { get; set; } = "Cross";
    public string ActionType { get; set; } = "Key";
    public string ActionValue { get; set; } = "Space";
    public bool Enabled { get; set; } = true;
    public KeyBinding() { }
    public KeyBinding(string input, string actionType, string actionValue) { Input = input; ActionType = actionType; ActionValue = actionValue; }
}

public static class SettingsStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualShockStudio");
    public static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
