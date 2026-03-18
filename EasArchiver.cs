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

    // Stable device ID – generated once and persisted locally
    public static readonly string DeviceId = LoadOrCreateDeviceId();

    private static string LoadOrCreateDeviceId()
    {
        var dir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasArchiver")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".eas-archiver");
        var file = Path.Combine(dir, "device-id");
        if (File.Exists(file))
        {
            var stored = File.ReadAllText(file).Trim();
            if (!string.IsNullOrEmpty(stored)) return stored;
        }
        Directory.CreateDirectory(dir);
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

    public EasArchiver(EasConfig cfg)
    {
        _cfg = cfg;
        _v   = cfg.Verbosity;

        _http = new HttpClient();
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
        Console.WriteLine("1/3  Provisioning …");
        await ProvisionAsync();

        Console.WriteLine("2/3  Fetching folder structure …");
        var folders = await FolderSyncAsync(state);
        Console.WriteLine($"     {folders.Count} folders found.\n");

        Console.WriteLine("3/3  Synchronizing emails …");
        int totalNew = 0;

        foreach (var (id, name) in folders)
        {
            Console.Write($"     {name,-45}");
            int count = await SyncFolderAsync(id, name, state);
            Console.WriteLine(count > 0 ? $"{count,4} new mail(s)" : "   –");
            totalNew += count;
        }

        Console.WriteLine($"\n     Total: {totalNew} new email(s) archived.");
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

        Console.WriteLine($"     Policy-Key: {finalKey}");
    }

    // ── Step 2: FolderSync ───────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> FolderSyncAsync(SyncState state)
    {
        var syncKey = state.FolderSyncKey ?? "0";

        var request = Xml(NsFolderHier + "FolderSync",
            Xml(NsFolderHier + "SyncKey", syncKey));

        var root = await PostAsync("FolderSync", request);
        var folders = new Dictionary<string, string>(); // id → display name
        if (root is null) return folders;

        var status = root.Descendants(NsFolderHier + "Status").FirstOrDefault()?.Value;
        if (status is not null && status != "1")
            throw new InvalidOperationException($"FolderSync failed – Status={status}");

        var newKey = root.Descendants(NsFolderHier + "SyncKey").FirstOrDefault()?.Value;
        if (newKey is not null) state.FolderSyncKey = newKey;

        foreach (var add in root.Descendants(NsFolderHier + "Add"))
        {
            var id   = add.Element(NsFolderHier + "ServerId")?.Value;
            var name = add.Element(NsFolderHier + "DisplayName")?.Value;
            if (id is not null && name is not null)
                folders[id] = name;
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

        while (true)
        {
            var request = BuildSyncRequest(folderId, syncKey);
            var root    = await PostAsync("Sync", request);
            if (root is null) break;

            var newKey = root.Descendants(NsAirSync + "SyncKey").FirstOrDefault()?.Value;
            if (newKey is not null)
            {
                syncKey = newKey;
                state.FolderKeys[folderId] = newKey;
            }

            var status = root.Descendants(NsAirSync + "Status").FirstOrDefault()?.Value;
            if (status is not null && status != "1") break;

            var commands = root.Descendants(NsAirSync + "Commands").FirstOrDefault();
            if (commands is null) break;

            foreach (var add in commands.Elements(NsAirSync + "Add"))
                if (await SaveEmailAsync(add, folderPath))
                    totalNew++;

            // MoreAvailable → another sync cycle needed
            if (root.Descendants(NsAirSync + "MoreAvailable").FirstOrDefault() is null)
                break;
        }

        return totalNew;
    }

    private XElement BuildSyncRequest(string collectionId, string syncKey) =>
        new XElement(NsAirSync + "Sync",
            new XAttribute(XNamespace.Xmlns + "Email",        NsEmail.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "AirSyncBase",  NsAirSyncBase.NamespaceName),
            new XElement(NsAirSync + "Collections",
                new XElement(NsAirSync + "Collection",
                    Xml(NsAirSync + "SyncKey",       syncKey),
                    Xml(NsAirSync + "CollectionId",  collectionId),
                    Xml(NsAirSync + "DeletesAsMoves","0"),
                    Xml(NsAirSync + "GetChanges",    "1"),
                    Xml(NsAirSync + "WindowSize",    _cfg.WindowSize.ToString()),
                    new XElement(NsAirSync + "Options",
                        Xml(NsAirSync + "MIMESupport",     "2"),  // 2 = MIME preferred
                        Xml(NsAirSync + "MIMETruncation",  "8"),  // 8 = no truncation
                        new XElement(NsAirSyncBase + "BodyPreference",
                            Xml(NsAirSyncBase + "Type",     "4"), // 4 = MIME (preferred)
                            Xml(NsAirSyncBase + "AllOrNone","1"))))));
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

        var wbxmlBytes = EasWbxml.Encode(body);
        var content    = new ByteArrayContent(wbxmlBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.ms-sync.wbxml");

        // ── Verbosity: Request ────────────────────────────────────────────────
        if (_v >= 1) Log($"→ POST {url}");
        if (_v >= 2)
        {
            foreach (var h in _http.DefaultRequestHeaders)
                Log($"  {h.Key}: {string.Join(", ", h.Value)}");
            Log($"  Content-Type: {content.Headers.ContentType}");
            Log($"  Content-Length: {wbxmlBytes.Length}");
        }
        if (_v >= 3) Log($"\n  req-hex: {Convert.ToHexString(wbxmlBytes)}\n{body}\n");

        var resp = await _http.PostAsync(url, content);

        // ── Verbosity: Response ───────────────────────────────────────────────
        if (_v >= 1) Log($"← {(int)resp.StatusCode} {resp.ReasonPhrase}");
        if (_v >= 2)
        {
            foreach (var h in resp.Headers)
                Log($"  {h.Key}: {string.Join(", ", h.Value)}");
            foreach (var h in resp.Content.Headers)
                Log($"  {h.Key}: {string.Join(", ", h.Value)}");
        }

        if ((int)resp.StatusCode == 449) throw new EasQuarantineException(DeviceId);
        if ((int)resp.StatusCode == 401) throw new EasAuthException();

        var responseBytes = await resp.Content.ReadAsByteArrayAsync();

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"HTTP {(int)resp.StatusCode} for Cmd={cmd}: " +
                Encoding.UTF8.GetString(responseBytes)[..Math.Min(300, responseBytes.Length)]);

        if (responseBytes.Length == 0) return null;

        if (_v >= 3) Log($"\n  resp-hex: {Convert.ToHexString(responseBytes)}\n");

        var decoded = EasWbxml.Decode(responseBytes);
        if (_v >= 3) Log($"{decoded}\n");
        return decoded;
    }

    private static void Log(string msg)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(msg);
        Console.ForegroundColor = prev;
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
