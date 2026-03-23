using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Serilog;

namespace EasArchiver;

// ── Exceptions ───────────────────────────────────────────────────────────────

public class EasQuarantineException(string deviceId)
    : Exception($"HTTP 449 – device '{deviceId}' is in quarantine")
{
    public string DeviceId { get; } = deviceId;
}

public class EasAuthException()
    : Exception("HTTP 401 – authentication failed");

// ── Archiver ─────────────────────────────────────────────────────────────────

public class EasArchiver
{
    // App version (from csproj <Version>)
    public static readonly string AppVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    // EAS protocol version
    private const string EasVersion = "14.1";
    private const string DeviceType = "WindowsPC";
    private const int    MaxHexLogBytes = 2000;

    // Platform-aware app data directory
    public static readonly string AppDataDir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasArchiver")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".eas-archiver");

    // Stable device ID – generated once and persisted locally
    public static readonly string DeviceId = LoadOrCreateDeviceId();

    private static string LoadOrCreateDeviceId()
    {
        var file = Path.Combine(AppDataDir, "device-id");
        if (File.Exists(file))
        {
            var stored = File.ReadAllText(file).Trim();
            if (!string.IsNullOrEmpty(stored)) return stored;
        }
        Directory.CreateDirectory(AppDataDir);
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(file, id);
        return id;
    }

    // Windows version for User-Agent (e.g. "Windows 11 (10.0.26100)")
    private static readonly string OsVersion = GetWindowsVersion();

    // ── EAS XML namespaces ────────────────────────────────────────────────────
    private static readonly XNamespace NsAirSync     = "AirSync:";
    private static readonly XNamespace NsFolderHier  = "FolderHierarchy:";
    private static readonly XNamespace NsEmail       = "Email:";
    private static readonly XNamespace NsAirSyncBase = "AirSyncBase:";
    private static readonly XNamespace NsSettings    = "Settings:";

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly EasConfig  _cfg;
    private readonly HttpClient _http;
    private readonly HttpClientHandler _handler;
    private readonly int        _v; // verbosity 0-3
    private int _requestCount = 0;

    public EasArchiver(EasConfig cfg)
    {
        _cfg = cfg;
        _v   = cfg.Verbosity;

        _handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() };
        _http = new HttpClient(_handler);
        _http.DefaultRequestHeaders.Add("MS-ASProtocolVersion", EasVersion);
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            $"EasArchiver/{AppVersion} ({OsVersion}; DeviceType={DeviceType})");

        var creds = BuildBasicAuth(cfg);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", creds);
    }

    // ── Main flow ─────────────────────────────────────────────────────────────

    public async Task RunAsync(SyncState state)
    {
        // First sync: register device information with the server
        if (state.FolderSyncKey is null or "0")
        {
            Log.Information("     Sending device information …");
            await SendDeviceInfoAsync();
        }

        Log.Information("1/2  Fetching folder structure …");
        var folders = await FolderSyncAsync(state);
        Log.Information("     {Count} folders found.\n", folders.Count);

        Log.Information("2/2  Archiving emails …");
        int totalNew = await SyncAllFoldersAsync(folders, state);
        Log.Information("\n     Total: {TotalNew} new email(s) archived.", totalNew);
    }

    // ── Device info (Settings command) ────────────────────────────────────────

    private async Task SendDeviceInfoAsync()
    {
        var osName = RuntimeInformation.OSDescription.Trim();
        var osLang = CultureInfo.CurrentCulture.Name; // e.g. "de-DE"

        var deviceInfo = Xml(NsSettings + "Settings",
            Xml(NsSettings + "DeviceInformation",
                Xml(NsSettings + "Set",
                    Xml(NsSettings + "OS", osName),
                    Xml(NsSettings + "OSLanguage", osLang),
                    Xml(NsSettings + "UserAgent",
                        $"EasArchiver/{AppVersion} ({osName}; DeviceType={DeviceType})"))));

        var root = await PostAsync("Settings", deviceInfo);
        var status = root?.Descendants(NsSettings + "Status").FirstOrDefault()?.Value;
        if (status is not null && status != "1")
            Log.Warning("  Settings/DeviceInformation Status={Status}", status);
    }

    // ── Step 2: FolderSync ───────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> FolderSyncAsync(SyncState state)
    {
        var syncKey = state.FolderSyncKey ?? "0";
        // Start from persisted folder list (subsequent syncs only return changes)
        var folders = new Dictionary<string, string>(state.KnownFolders);
        bool retried = false;

        while (true)
        {
            var request = Xml(NsFolderHier + "FolderSync",
                Xml(NsFolderHier + "SyncKey", syncKey));

            var root = await PostAsync("FolderSync", request);
            if (root is null) break;

            var status = root.Descendants(NsFolderHier + "Status").FirstOrDefault()?.Value;

            // Status 9/165 = invalid SyncKey → reset and retry once
            if (status is "9" or "165" && !retried)
            {
                syncKey = "0";
                state.FolderSyncKey = null;
                folders.Clear();
                retried = true;
                continue;
            }
            if (status is not null && status != "1")
                throw new InvalidOperationException($"FolderSync failed – Status={status}");

            var newKey = root.Descendants(NsFolderHier + "SyncKey").FirstOrDefault()?.Value;
            if (newKey is not null)
            {
                syncKey = newKey;
                state.FolderSyncKey = newKey;
            }

            foreach (var add in root.Descendants(NsFolderHier + "Add"))
            {
                var id   = add.Element(NsFolderHier + "ServerId")?.Value;
                var name = add.Element(NsFolderHier + "DisplayName")?.Value;
                var type = add.Element(NsFolderHier + "Type")?.Value;
                // Only include email folder types:
                // 1=User mail, 2=Inbox, 3=Drafts, 4=Deleted, 5=Sent, 6=Outbox, 12=User mail
                if (id is not null && name is not null
                    && type is "1" or "2" or "3" or "4" or "5" or "6" or "12")
                    folders[id] = name;
            }

            // Handle folder deletions
            foreach (var del in root.Descendants(NsFolderHier + "Delete"))
            {
                var id = del.Element(NsFolderHier + "ServerId")?.Value;
                if (id is not null) folders.Remove(id);
            }

            // Handle folder updates (rename etc.)
            foreach (var upd in root.Descendants(NsFolderHier + "Update"))
            {
                var id   = upd.Element(NsFolderHier + "ServerId")?.Value;
                var name = upd.Element(NsFolderHier + "DisplayName")?.Value;
                var type = upd.Element(NsFolderHier + "Type")?.Value;
                if (id is not null && name is not null
                    && type is "1" or "2" or "3" or "4" or "5" or "6" or "12")
                    folders[id] = name;
                else if (id is not null)
                    folders.Remove(id); // type changed to non-email → remove
            }

            // MoreAvailable → another page of folders; also loop once after SyncKey=0
            if (root.Descendants(NsFolderHier + "MoreAvailable").FirstOrDefault() is null
                && syncKey != "0")
                break;
        }

        // Persist known folders for next run
        state.KnownFolders = new Dictionary<string, string>(folders);
        return folders;
    }

    // ── Step 3: Sync all folders (batched) ───────────────────────────────────

    private async Task<int> SyncAllFoldersAsync(
        Dictionary<string, string> folders, SyncState state)
    {
        // Prepare folder paths
        var folderPaths = new Dictionary<string, string>();
        foreach (var (id, name) in folders)
        {
            var path = Path.Combine(_cfg.ArchiveDirectory, Sanitize(name));
            Directory.CreateDirectory(path);
            folderPaths[id] = path;
        }

        // Track per-folder counts and retry state
        var counts  = new Dictionary<string, int>();
        var retried = new HashSet<string>();
        // Active set: folders that still need syncing
        var active  = new HashSet<string>(folders.Keys);
        int totalNew = 0;

        while (active.Count > 0)
        {
            // Remember which folders had SyncKey=0 before the request
            var wasZero = new HashSet<string>(
                active.Where(id => state.FolderKeys.GetValueOrDefault(id, "0") == "0"));

            var request = BuildBatchSyncRequest(active, state);
            var root    = await PostAsync("Sync", request);
            if (root is null) break;

            // Check top-level status (applies to entire request)
            var topStatus = root.Element(NsAirSync + "Status")?.Value;
            if (topStatus is not null && topStatus != "1")
            {
                Log.Warning("  Sync batch Status={Status}", topStatus);
                break;
            }

            // Server only returns collections that have something to report.
            // Folders not in the response have no changes → done.
            var responded = new HashSet<string>();
            var needMore  = new HashSet<string>();

            foreach (var coll in root.Descendants(NsAirSync + "Collection"))
            {
                var collId = coll.Element(NsAirSync + "CollectionId")?.Value;
                if (collId is null || !active.Contains(collId)) continue;
                responded.Add(collId);

                var status = coll.Element(NsAirSync + "Status")?.Value;

                // Invalid SyncKey → reset and retry once
                if (status is "3" or "4" or "165" && !retried.Contains(collId))
                {
                    state.FolderKeys.Remove(collId);
                    retried.Add(collId);
                    needMore.Add(collId);
                    continue;
                }
                if (status is not null && status != "1") continue;

                var newKey = coll.Element(NsAirSync + "SyncKey")?.Value;
                if (newKey is not null)
                    state.FolderKeys[collId] = newKey;

                var commands = coll.Element(NsAirSync + "Commands");
                if (commands is not null)
                {
                    foreach (var add in commands.Elements(NsAirSync + "Add"))
                    {
                        if (await SaveEmailAsync(add, folderPaths[collId]))
                        {
                            counts[collId] = counts.GetValueOrDefault(collId) + 1;
                            totalNew++;
                        }
                    }
                }

                // Was SyncKey=0 → just got initialized, need to re-request with real key
                if (wasZero.Contains(collId) && newKey is not null)
                    needMore.Add(collId);
                // MoreAvailable → server has more items for this folder
                else if (coll.Element(NsAirSync + "MoreAvailable") is not null)
                    needMore.Add(collId);
            }

            // Folders that were SyncKey=0 but not in the response → still need init
            foreach (var id in wasZero)
                if (!responded.Contains(id))
                    needMore.Add(id);

            active = needMore;
        }

        // Log results per folder
        foreach (var (id, name) in folders)
        {
            var count = counts.GetValueOrDefault(id);
            Log.Information("     {FolderName,-45}{Result}", name,
                count > 0 ? $"{count,4} new mail(s)" : "   –");
        }

        return totalNew;
    }

    private XElement BuildBatchSyncRequest(
        HashSet<string> folderIds, SyncState state)
    {
        var collections = new XElement(NsAirSync + "Collections");

        foreach (var folderId in folderIds)
        {
            var syncKey = state.FolderKeys.GetValueOrDefault(folderId, "0");

            var collection = new XElement(NsAirSync + "Collection",
                Xml(NsAirSync + "SyncKey",      syncKey),
                Xml(NsAirSync + "CollectionId", folderId));

            // SyncKey=0 is init — only SyncKey + CollectionId allowed
            if (syncKey != "0")
            {
                collection.Add(
                    Xml(NsAirSync + "DeletesAsMoves", "0"),
                    Xml(NsAirSync + "GetChanges",     "1"),
                    Xml(NsAirSync + "WindowSize",     _cfg.WindowSize.ToString()),
                    new XElement(NsAirSync + "Options",
                        Xml(NsAirSync + "MIMESupport",    "2"),
                        Xml(NsAirSync + "MIMETruncation", "8"),
                        new XElement(NsAirSyncBase + "BodyPreference",
                            Xml(NsAirSyncBase + "Type",     "4"),
                            Xml(NsAirSyncBase + "AllOrNone", "1"))));
            }

            collections.Add(collection);
        }

        return new XElement(NsAirSync + "Sync",
            new XAttribute(XNamespace.Xmlns + "Email",       NsEmail.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "AirSyncBase", NsAirSyncBase.NamespaceName),
            collections);
    }
    // ── Save email as .eml ───────────────────────────────────────────────────

    private async Task<bool> SaveEmailAsync(XElement addEl, string folderPath)
    {
        var serverId = addEl.Element(NsAirSync + "ServerId")?.Value ?? "";
        var appData  = addEl.Element(NsAirSync + "ApplicationData");
        if (appData is null) return false;

        var subject = appData.Element(NsEmail + "Subject")?.Value ?? "no_subject";
        var dateStr = appData.Element(NsEmail + "DateReceived")?.Value ?? "";

        // Prefer MIME (Type=4), fall back to text (Type=1 or 2)
        string? content = null;

        foreach (var body in appData.Descendants(NsAirSyncBase + "Body"))
        {
            var type = body.Element(NsAirSyncBase + "Type")?.Value;
            var data = body.Element(NsAirSyncBase + "Data")?.Value;
            if (string.IsNullOrEmpty(data)) continue;

            if (type == "4")                    // MIME – ideal, use directly
            { content = data; break; }

            if (content is null)               // Fallback: build minimal .eml
            {
                var from = appData.Element(NsEmail + "From")?.Value ?? "";
                var to   = appData.Element(NsEmail + "To")?.Value   ?? "";
                content =
                    $"From: {from}\r\nTo: {to}\r\nSubject: {subject}\r\n" +
                    $"Date: {dateStr}\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" +
                    data;
            }
        }

        if (content is null) return false;

        var path = BuildEmlPath(folderPath, serverId, subject, dateStr);
        if (File.Exists(path)) return false;

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    // ── HTTP helpers ─────────────────────────────────────────────────────────

    private async Task<XElement?> PostAsync(string cmd, XElement body)
    {
        var url =
            $"{_cfg.ServerUrl.TrimEnd('/')}/" +
            $"?Cmd={cmd}&User={Uri.EscapeDataString(_cfg.Username)}" +
            $"&DeviceId={DeviceId}&DeviceType={DeviceType}";

        // ── Rate limit: 200 ms between requests ──────────────────────────────
        if (_requestCount > 0)
            await Task.Delay(200);

        // ── Confirm every 5 requests ─────────────────────────────────────────
        if (_requestCount > 0 && _requestCount % 5 == 0)
        {
            Console.Write($"\n[{_requestCount} requests sent] Continue? [Y/n] ");
            var ans = Console.ReadLine()?.Trim().ToLower();
            if (ans == "n" || ans == "no")
                throw new OperationCanceledException("Aborted by user after rate-limit prompt.");
            Console.WriteLine();
        }

        _requestCount++;

        var wbxmlBytes = EasWbxml.Encode(body);
        var content    = new ByteArrayContent(wbxmlBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.ms-sync.wbxml");

        // ── Verbosity: Request ────────────────────────────────────────────────
        if (_v >= 1) Log.Debug("→ POST {Url}", url);
        if (_v >= 2)
        {
            foreach (var h in _http.DefaultRequestHeaders)
                Log.Debug("  {Key}: {Value}", h.Key, string.Join(", ", h.Value));
            Log.Debug("  Content-Type: {ContentType}", content.Headers.ContentType?.ToString());
            Log.Debug("  Content-Length: {Length}", wbxmlBytes.Length);
            var cookies = _handler.CookieContainer.GetCookieHeader(new Uri(url));
            if (!string.IsNullOrEmpty(cookies))
                Log.Debug("  Cookie: {Cookie}", cookies);
        }
        if (_v >= 3) Log.Debug("\n  req-hex: {Hex}\n{Body}\n", Convert.ToHexString(wbxmlBytes), body);

        var resp = await _http.PostAsync(url, content);

        // ── Verbosity: Response ───────────────────────────────────────────────
        if (_v >= 1) Log.Debug("← {StatusCode} {Reason}", (int)resp.StatusCode, resp.ReasonPhrase);
        if (_v >= 2)
        {
            foreach (var h in resp.Headers)
                Log.Debug("  {Key}: {Value}", h.Key, string.Join(", ", h.Value));
            foreach (var h in resp.Content.Headers)
                Log.Debug("  {Key}: {Value}", h.Key, string.Join(", ", h.Value));
        }

        if ((int)resp.StatusCode == 449) throw new EasQuarantineException(DeviceId);
        if ((int)resp.StatusCode == 401) throw new EasAuthException();

        var responseBytes = await resp.Content.ReadAsByteArrayAsync();

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"HTTP {(int)resp.StatusCode} for Cmd={cmd}: " +
                Encoding.UTF8.GetString(responseBytes)[..Math.Min(300, responseBytes.Length)]);

        if (responseBytes.Length == 0) return null;

        // Save Sync response blobs as hex files for debugging
        if (cmd == "Sync" && responseBytes.Length > MaxHexLogBytes)
        {
            var blobDir = Path.Combine(AppDataDir, "debug", "syncblobs");
            Directory.CreateDirectory(blobDir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var blobPath = Path.Combine(blobDir, $"{timestamp}_{_requestCount}.hex");
            await File.WriteAllTextAsync(blobPath, Convert.ToHexString(responseBytes));
            Log.Debug("  Sync blob saved: {Path}", blobPath);
        }

        if (_v >= 3 && responseBytes.Length <= MaxHexLogBytes)
            Log.Debug("\n  resp-hex: {Hex}\n", Convert.ToHexString(responseBytes));

        var decoded = EasWbxml.Decode(responseBytes);
        if (_v >= 3) Log.Debug("{Decoded}\n", decoded);
        return decoded;
    }

    // ── Helper methods ───────────────────────────────────────────────────────

    private static string BuildBasicAuth(EasConfig cfg)
    {
        var user = string.IsNullOrEmpty(cfg.Domain)
            ? cfg.Username
            : $"{cfg.Domain}\\{cfg.Username}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{cfg.Password}"));
    }

    private static string Sanitize(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name.Trim();
    }

    private static string BuildEmlPath(string folder, string serverId, string subject, string dateStr)
    {
        var safeDate = DateTime.TryParse(dateStr, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToString("yyyy-MM-dd_HHmmss")
            : Sanitize(dateStr);
        var safeSubject = Sanitize(subject);
        safeSubject = safeSubject[..Math.Min(60, safeSubject.Length)];
        var hash        = Convert.ToHexString(
                              MD5.HashData(Encoding.UTF8.GetBytes(serverId))
                          )[..8].ToLower();
        return Path.Combine(folder, $"{safeDate}_{safeSubject}_{hash}.eml");
    }

    private static string GetWindowsVersion()
    {
        try
        {
            var os = RuntimeInformation.OSDescription.Trim(); // e.g. "Microsoft Windows 10.0.26100"
            return os;
        }
        catch
        {
            return "Windows";
        }
    }

    // Compact XML builder
    private static XElement Xml(XName name, params object[] content) => new(name, content);
}
