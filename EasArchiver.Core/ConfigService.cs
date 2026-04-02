using System.IO;
using System.Text.Json;
using Serilog;

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
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load config from {Path} — using defaults", ConfigPath);
            return new EasConfig();
        }
    }

    public static void SaveConfig(EasConfig cfg)
    {
        Directory.CreateDirectory(EasArchiver.AppDataDir);
        var wrapper = new ConfigWrapper { Eas = cfg };
        WriteAtomically(ConfigPath, JsonSerializer.Serialize(wrapper, JsonOpts));
    }

    public static SyncState LoadState()
    {
        if (!File.Exists(StatePath)) return new SyncState();
        try
        {
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<SyncState>(json) ?? new SyncState();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load sync state from {Path} — starting fresh", StatePath);
            return new SyncState();
        }
    }

    public static void SaveState(SyncState state)
    {
        Directory.CreateDirectory(EasArchiver.AppDataDir);
        WriteAtomically(StatePath, JsonSerializer.Serialize(state, JsonOpts));
    }

    /// <summary>
    /// Writes content to a temp file next to the target, then atomically replaces it.
    /// Prevents corruption if the process is killed mid-write.
    /// </summary>
    private static void WriteAtomically(string path, string content)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save {Path}", path);
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Wraps EasConfig so the JSON has { "Eas": { ... } } matching appsettings.json format.</summary>
    private class ConfigWrapper
    {
        public EasConfig Eas { get; set; } = new();
    }
}
