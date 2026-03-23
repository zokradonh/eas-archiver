using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace EasArchiver;

class Program
{
    private static readonly string StateFile = Path.Combine(
        EasArchiver.AppDataDir, "eas_sync_state.json");

    private static readonly string LogFile = Path.Combine(
        EasArchiver.AppDataDir, "eas-archiver.log");

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Contains("--version"))
        {
            Console.WriteLine($"EasArchiver {EasArchiver.AppVersion}");
            return 0;
        }

        Directory.CreateDirectory(EasArchiver.AppDataDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "{Message:lj}{NewLine}{Exception}")
            .WriteTo.File(LogFile,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        Log.Information("=== EAS Email Archiver ===\n");

        // ── Test mode ────────────────────────────────────────────────────────
        if (args.Contains("--test"))
            return WbxmlTests.RunAll();

        // ── Decode hex file → XML ───────────────────────────────────────────
        if (args.Length == 2 && args[0] == "--decode")
        {
            var hex   = File.ReadAllText(args[1]).Trim();
            var xml   = EasWbxml.Decode(Convert.FromHexString(hex));
            var outPath = Path.ChangeExtension(args[1], ".xml");
            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                CheckCharacters = false,
                Encoding = new UTF8Encoding(false),
            };
            using var stream = File.Create(outPath);
            using var writer = System.Xml.XmlWriter.Create(stream, settings);
            xml.WriteTo(writer);
            writer.Flush();
            Console.WriteLine($"Written to {outPath}");
            return 0;
        }

        // ── Normalize -v / -vv / -vvv → --Eas:Verbosity=N ───────────────────
        args = NormalizeVerbosityArgs(args);

        // ── Load configuration ───────────────────────────────────────────────
        // Order (later overrides earlier):
        //   1. appsettings.json
        //   2. Environment variables  (EAS__Username etc.)
        //   3. Command line arguments  (--Eas:Username=...)
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var easCfg = config.GetSection("Eas").Get<EasConfig>() ?? new EasConfig();

        // ── Parse --include / --exclude from CLI (supports multiple) ─────────
        foreach (var arg in args)
        {
            if (arg.StartsWith("--include=", StringComparison.OrdinalIgnoreCase))
                easCfg.Include.Add(arg["--include=".Length..]);
            else if (arg.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase))
                easCfg.Exclude.Add(arg["--exclude=".Length..]);
        }

        // Prompt for missing required fields interactively
        easCfg = PromptMissingFields(easCfg);

        // ── Validate archive drive ───────────────────────────────────────────
        if (Path.IsPathRooted(easCfg.ArchiveDirectory))
        {
            var root = Path.GetPathRoot(easCfg.ArchiveDirectory)!;
            if (!Directory.Exists(root))
            {
                Log.Error("Archive drive not available: {Root}", root);
                Log.Error("   ArchiveDirectory is set to: {Dir}", easCfg.ArchiveDirectory);
                return 1;
            }
        }

        // ── Confirm before sending any request ───────────────────────────────
        Log.Information("Server:   {ServerUrl}", easCfg.ServerUrl);
        Log.Information("User:     {Username}", easCfg.Username);
        Log.Information("Device:   {DeviceId}", EasArchiver.DeviceId);
        Console.Write("\nProceed? [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLower();
        if (answer != "y" && answer != "yes")
        {
            Log.Information("Aborted.");
            return 0;
        }
        Log.Information("");

        // ── Load sync state ──────────────────────────────────────────────────
        var state = LoadState(StateFile);

        // ── Start archiving ──────────────────────────────────────────────────
        var archiver = new EasArchiver(easCfg);
        try
        {
            await archiver.RunAsync(state);
            SaveState(StateFile, state);
            Log.Information("\nArchive: {ArchiveDir}", Path.GetFullPath(easCfg.ArchiveDirectory));
            return 0;
        }
        catch (OperationCanceledException ex)
        {
            Log.Information("\n{Message}", ex.Message);
            return 0;
        }
        catch (EasQuarantineException ex)
        {
            Log.Warning("\nDevice not yet approved (HTTP 449 – Quarantine).");
            Log.Warning("   Device ID for approval: {DeviceId}", ex.DeviceId);
            Log.Warning("   Please restart after the admin has approved the device.");
            return 2;
        }
        catch (EasAuthException)
        {
            Log.Error("\nAuthentication failed (HTTP 401).");
            Log.Error("   Please check username/password in appsettings.json.");
            return 1;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unhandled error");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    // ── Helper methods ───────────────────────────────────────────────────────

    private static EasConfig PromptMissingFields(EasConfig cfg)
    {
        bool prompted = false;

        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) ||
            cfg.ServerUrl.Contains("example.com"))
        {
            Console.Write("EAS-Server-URL: ");
            cfg.ServerUrl = Console.ReadLine()?.Trim() ?? "";
            prompted = true;
        }

        if (string.IsNullOrWhiteSpace(cfg.Username))
        {
            Console.Write("Username:       ");
            cfg.Username = Console.ReadLine()?.Trim() ?? "";
            prompted = true;
        }

        if (string.IsNullOrWhiteSpace(cfg.Password))
        {
            Console.Write("Password:       ");
            cfg.Password = ReadPassword();
            prompted = true;
        }

        if (prompted) Console.WriteLine();
        return cfg;
    }

    private static string[] NormalizeVerbosityArgs(string[] args)
    {
        var result = new List<string>();
        foreach (var arg in args)
        {
            result.Add(arg switch
            {
                "-vvv" or "--verbose=3" => "--Eas:Verbosity=3",
                "-vv"  or "--verbose=2" => "--Eas:Verbosity=2",
                "-v"   or "--verbose"
                       or "--verbose=1" => "--Eas:Verbosity=1",
                "--debug-blobs"         => "--Eas:DebugBlobs=true",
                _                       => arg,
            });
        }
        return result.ToArray();
    }

    private static string ReadPassword()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
                sb.Remove(sb.Length - 1, 1);
            else if (key.KeyChar != '\0')
                sb.Append(key.KeyChar);
        }
        return sb.ToString();
    }

    private static SyncState LoadState(string path)
    {
        if (!File.Exists(path)) return new SyncState();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SyncState>(json) ?? new SyncState();
        }
        catch { return new SyncState(); }
    }

    private static void SaveState(string path, SyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
