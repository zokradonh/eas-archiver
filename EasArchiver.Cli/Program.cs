using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace EasArchiver;

class Program
{
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

        // ── Load configuration ───────────────────────────────────────────────
        var easCfg = ConfigService.LoadConfig();

        // ── Apply CLI overrides ──────────────────────────────────────────────
        foreach (var arg in args)
        {
            if (arg.StartsWith("--include=", StringComparison.OrdinalIgnoreCase))
                easCfg.Include.Add(arg["--include=".Length..]);
            else if (arg.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase))
                easCfg.Exclude.Add(arg["--exclude=".Length..]);
            else if (arg is "-v" or "--verbose" or "--verbose=1")
                easCfg.Verbosity = 1;
            else if (arg is "-vv" or "--verbose=2")
                easCfg.Verbosity = 2;
            else if (arg is "-vvv" or "--verbose=3")
                easCfg.Verbosity = 3;
            else if (arg is "--debug-blobs")
                easCfg.DebugBlobs = true;
        }

        // Try loading stored password (DPAPI) before prompting
        if (string.IsNullOrWhiteSpace(easCfg.Password))
        {
            var stored = CredentialService.Load();
            if (stored is not null)
                easCfg.Password = stored;
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
        var state = ConfigService.LoadState();

        if (args.Contains("--reset"))
        {
            state = new SyncState();
            ConfigService.SaveState(state);
            Log.Information("Sync state has been reset.\n");
        }

        // ── Start archiving ──────────────────────────────────────────────────
        var archiver = new EasArchiver(easCfg);
        archiver.ConfirmContinue = count =>
        {
            Console.Write($"\n[{count} requests sent] Continue? [Y/n] ");
            var ans = Console.ReadLine()?.Trim().ToLower();
            Console.WriteLine();
            return Task.FromResult(ans is not "n" and not "no");
        };
        try
        {
            await archiver.RunAsync(state);
            ConfigService.SaveState(state);
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
}
