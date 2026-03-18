using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace EasArchiver;

class Program
{
    private const string StateFile = "eas_sync_state.json";

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== EAS Email Archiver ===\n");

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

        // Prompt for missing required fields interactively
        easCfg = PromptMissingFields(easCfg);

        // ── Confirm before sending any request ───────────────────────────────
        Console.WriteLine($"Server:   {easCfg.ServerUrl}");
        Console.WriteLine($"User:     {easCfg.Username}");
        Console.WriteLine($"Device:   {EasArchiver.DeviceId}");
        Console.Write("\nProceed? [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLower();
        if (answer != "y" && answer != "yes")
        {
            Console.WriteLine("Aborted.");
            return 0;
        }
        Console.WriteLine();

        // ── Load sync state ──────────────────────────────────────────────────
        var state = LoadState(StateFile);

        // ── Start archiving ──────────────────────────────────────────────────
        var archiver = new EasArchiver(easCfg);
        try
        {
            await archiver.RunAsync(state);
            SaveState(StateFile, state);
            Console.WriteLine($"\nArchive: {Path.GetFullPath(easCfg.ArchiveDirectory)}");
            return 0;
        }
        catch (EasQuarantineException ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n⚠  Device not yet approved (HTTP 449 – Quarantine).");
            Console.WriteLine($"   Device ID for approval: {ex.DeviceId}");
            Console.WriteLine("   Please restart after the admin has approved the device.");
            Console.ResetColor();
            return 2;
        }
        catch (EasAuthException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n✗  Authentication failed (HTTP 401).");
            Console.WriteLine("   Please check username/password in appsettings.json.");
            Console.ResetColor();
            return 1;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n✗  Error: {ex.Message}");
            Console.ResetColor();
            return 1;
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
        File.WriteAllText(path,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
