using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    // EAS protocol version
    private const string EasVersion = "14.1";
    private const string DeviceType = "WindowsPC";

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
    private static readonly XNamespace NsProvision   = "Provision:";

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly EasConfig  _cfg;
    private readonly HttpClient _http;
    private readonly int        _v; // verbosity 0-3
    private int _requestCount = 0;

    public EasArchiver(EasConfig cfg)
    {
        _cfg = cfg;
        _v   = cfg.Verbosity;

        var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("MS-ASProtocolVersion", EasVersion);
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            $"EasArchiver/1.0 ({OsVersion}; DeviceType={DeviceType})");

        var creds = BuildBasicAuth(cfg);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", creds);
    }

    // ── Main flow ─────────────────────────────────────────────────────────────

    public async Task RunAsync(SyncState state)
    {
        Log.Information("1/3  Provisioning …");
        await ProvisionAsync();

        Log.Information("2/3  Fetching folder structure …");
        var folders = await FolderSyncAsync(state);
        Log.Information("     {Count} folders found.\n", folders.Count);

        Log.Information("3/3  Synchronizing emails …");
        int totalNew = 0;

        foreach (var (id, name) in folders)
        {
            int count = await SyncFolderAsync(id, name, state);
            Log.Information("     {FolderName,-45}{Result}", name,
                count > 0 ? $"{count,4} new mail(s)" : "   –");
            totalNew += count;
        }

        Log.Information("\n     Total: {TotalNew} new email(s) archived.", totalNew);
    }

    // ── Step 1: Provisioning ─────────────────────────────────────────────────

    private async Task ProvisionAsync()
    {
        var request = Xml(NsProvision + "Provision",
            Xml(NsProvision + "Policies",
                Xml(NsProvision + "Policy",
                    Xml(NsProvision + "PolicyType", "MS-EAS-Provisioning-WBXML"))));

        var root = await PostAsync("Provision", request);
        if (root is null) return;

        var policyKey = root.Descendants(NsProvision + "PolicyKey")
                            .FirstOrDefault()?.Value;
        if (policyKey is null) return;

        // Acknowledge – confirm to server as "compliant"
        var ack = Xml(NsProvision + "Provision",
            Xml(NsProvision + "Policies",
                Xml(NsProvision + "Policy",
                    Xml(NsProvision + "PolicyType",  "MS-EAS-Provisioning-WBXML"),
                    Xml(NsProvision + "PolicyKey",   policyKey),
                    Xml(NsProvision + "Status",      "1"))));

        var ackRoot   = await PostAsync("Provision", ack);

        // The server returns a final policy key in the acknowledgement response.
        // All subsequent requests MUST carry X-MS-PolicyKey with this value,
        // otherwise FolderSync (and Sync) silently returns empty results.
        var finalKey  = ackRoot?.Descendants(NsProvision + "PolicyKey")
                                .FirstOrDefault()?.Value ?? policyKey;

        _http.DefaultRequestHeaders.Remove("X-MS-PolicyKey");
        _http.DefaultRequestHeaders.Add("X-MS-PolicyKey", finalKey);

        Log.Information("     Policy-Key: {PolicyKey}", finalKey);
    }

    // ── Step 2: FolderSync ───────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> FolderSyncAsync(SyncState state)
    {
        var syncKey = state.FolderSyncKey ?? "0";
        var folders = new Dictionary<string, string>(); // id → display name
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

            // MoreAvailable → another page of folders; also loop once after SyncKey=0
            if (root.Descendants(NsFolderHier + "MoreAvailable").FirstOrDefault() is null
                && syncKey != "0")
                break;
        }

        return folders;
    }

    // ── Step 3: Sync folders ─────────────────────────────────────────────────

    private async Task<int> SyncFolderAsync(string folderId, string folderName, SyncState state)
    {
        var folderPath = Path.Combine(_cfg.ArchiveDirectory, Sanitize(folderName));
        Directory.CreateDirectory(folderPath);

        var syncKey = state.FolderKeys.GetValueOrDefault(folderId, "0");
        int totalNew = 0;
        bool retried = false;

        while (true)
        {
            var request = BuildSyncRequest(folderId, syncKey);
            var root    = await PostAsync("Sync", request);
            if (root is null) break;

            var status = root.Descendants(NsAirSync + "Status").FirstOrDefault()?.Value;

            // Status 4 = protocol error, 3/165 = invalid SyncKey → reset and retry once
            if (status is "3" or "4" or "165" && !retried)
            {
                syncKey = "0";
                state.FolderKeys.Remove(folderId);
                retried = true;
                continue;
            }
            if (status is not null && status != "1") break;

            var newKey = root.Descendants(NsAirSync + "SyncKey").FirstOrDefault()?.Value;
            if (newKey is not null)
            {
                syncKey = newKey;
                state.FolderKeys[folderId] = newKey;
            }

            var commands = root.Descendants(NsAirSync + "Commands").FirstOrDefault();
            if (commands is null)
            {
                // SyncKey=0 response only provides a key, no data — continue to fetch
                if (syncKey != "0") continue;
                break;
            }

            foreach (var add in commands.Elements(NsAirSync + "Add"))
                if (await SaveEmailAsync(add, folderPath))
                    totalNew++;

            // MoreAvailable → another sync cycle needed
            if (root.Descendants(NsAirSync + "MoreAvailable").FirstOrDefault() is null)
                break;
        }

        return totalNew;
    }

    private XElement BuildSyncRequest(string collectionId, string syncKey)
    {
        // SyncKey=0 is the initialisation request – only SyncKey + CollectionId allowed.
        // GetChanges, Options etc. must NOT be sent until the server has issued a real key.
        var collection = new XElement(NsAirSync + "Collection",
            Xml(NsAirSync + "SyncKey",      syncKey),
            Xml(NsAirSync + "CollectionId", collectionId));

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

        return new XElement(NsAirSync + "Sync",
            new XAttribute(XNamespace.Xmlns + "Email",       NsEmail.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "AirSyncBase", NsAirSyncBase.NamespaceName),
            new XElement(NsAirSync + "Collections", collection));
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

        // ── Confirm every 2 requests ─────────────────────────────────────────
        if (_requestCount > 0 && _requestCount % 2 == 0)
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
            Log.Debug("  Content-Type: {ContentType}", content.Headers.ContentType);
            Log.Debug("  Content-Length: {Length}", wbxmlBytes.Length);
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

        if (_v >= 3) Log.Debug("\n  resp-hex: {Hex}\n", Convert.ToHexString(responseBytes));

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
        var safeDate    = Sanitize(dateStr.Length >= 10 ? dateStr[..10] : dateStr);
        var safeSubject = Sanitize(subject)[..Math.Min(60, subject.Length)];
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
