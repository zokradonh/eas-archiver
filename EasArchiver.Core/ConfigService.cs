using System.IO;
using System.Text.Json;

namespace EasArchiver;

/// <summary>Loads/saves EasConfig and SyncState from/to the app data directory.</summary>
public static class ConfigService
{
    private static readonly string ConfigPath =
        Path.Combine(EasArchiver.AppDataDir, "config.json");

    private static readonly string StatePath =
        Path.Combine(EasArchiver.AppDataDir, "eas_sync_state.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static EasConfig LoadConfig()
    {
        if (!File.Exists(ConfigPath)) return new EasConfig();
        try
        {
            var json = File.ReadAllText(ConfigPath);
            var wrapper = JsonSerializer.Deserialize<ConfigWrapper>(json);
            return wrapper?.Eas ?? new EasConfig();
        }
        catch { return new EasConfig(); }
    }

    public static void SaveConfig(EasConfig cfg)
    {
        Directory.CreateDirectory(EasArchiver.AppDataDir);
        var wrapper = new ConfigWrapper { Eas = cfg };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(wrapper, JsonOpts));
    }

    public static SyncState LoadState()
    {
        if (!File.Exists(StatePath)) return new SyncState();
        try
        {
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<SyncState>(json) ?? new SyncState();
        }
        catch { return new SyncState(); }
    }

    public static void SaveState(SyncState state)
    {
        Directory.CreateDirectory(EasArchiver.AppDataDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOpts));
    }

    /// <summary>Wraps EasConfig so the JSON has { "Eas": { ... } } matching appsettings.json format.</summary>
    private class ConfigWrapper
    {
        public EasConfig Eas { get; set; } = new();
    }
}
