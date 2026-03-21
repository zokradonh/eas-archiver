using System.Text;
using System.Xml.Linq;

namespace EasArchiver;

/// <summary>
/// Minimal WBXML encoder/decoder for Exchange ActiveSync (MS-ASWBXML).
/// Covers the codepages used by EasArchiver: AirSync, Email,
/// FolderHierarchy, Provision, AirSyncBase.
/// </summary>
internal static class EasWbxml
{
    // ── WBXML token constants ────────────────────────────────────────────────
    private const byte TokSwitchPage = 0x00;
    private const byte TokEnd        = 0x01;
    private const byte TokStrI       = 0x03;  // inline string (null-terminated UTF-8)
    private const byte TokOpaque     = 0xC3;  // opaque binary blob
    private const byte FlagContent   = 0x40;  // tag has child content

    // ── EAS namespace URIs ───────────────────────────────────────────────────
    private const string NsAirSync     = "AirSync:";
    private const string NsEmail       = "Email:";
    private const string NsFolderHier  = "FolderHierarchy:";
    private const string NsProvision   = "Provision:";
    private const string NsAirSyncBase = "AirSyncBase:";

    // (namespace, localName) → (codepage, token)
    private static readonly Dictionary<(string, string), (byte page, byte tok)> EncodeMap;
    // (codepage, token) → (namespace, localName)
    private static readonly Dictionary<(byte page, byte tok), (string ns, string name)> DecodeMap;

    static EasWbxml()
    {
        var defs = new (string ns, string name, byte page, byte tok)[]
        {
            // ── Codepage 0: AirSync ──────────────────────────────────────────
            (NsAirSync, "Sync",              0, 0x05),
            (NsAirSync, "Responses",         0, 0x06),
            (NsAirSync, "Add",               0, 0x07),
            (NsAirSync, "Change",            0, 0x08),
            (NsAirSync, "Delete",            0, 0x09),
            (NsAirSync, "Fetch",             0, 0x0A),
            (NsAirSync, "SyncKey",           0, 0x0B),
            (NsAirSync, "ClientId",          0, 0x0C),
            (NsAirSync, "ServerId",          0, 0x0D),
            (NsAirSync, "Status",            0, 0x0E),
            (NsAirSync, "Collection",        0, 0x0F),
            (NsAirSync, "Class",             0, 0x10),
            (NsAirSync, "Version",           0, 0x11),  // deprecated
            (NsAirSync, "CollectionId",      0, 0x12),
            (NsAirSync, "GetChanges",        0, 0x13),
            (NsAirSync, "MoreAvailable",     0, 0x14),
            (NsAirSync, "WindowSize",        0, 0x15),
            (NsAirSync, "Commands",          0, 0x16),
            (NsAirSync, "Options",           0, 0x17),
            (NsAirSync, "FilterType",        0, 0x18),
            (NsAirSync, "Truncation",        0, 0x19),  // deprecated
            (NsAirSync, "RtfTruncation",     0, 0x1A),  // deprecated
            (NsAirSync, "Conflict",          0, 0x1B),
            (NsAirSync, "Collections",       0, 0x1C),
            (NsAirSync, "ApplicationData",   0, 0x1D),
            (NsAirSync, "DeletesAsMoves",    0, 0x1E),
            (NsAirSync, "NotifyGUID",        0, 0x1F),
            (NsAirSync, "Supported",         0, 0x20),
            (NsAirSync, "SoftDelete",        0, 0x21),
            (NsAirSync, "MIMESupport",       0, 0x22),
            (NsAirSync, "MIMETruncation",    0, 0x23),
            (NsAirSync, "Wait",              0, 0x24),
            (NsAirSync, "Limit",             0, 0x25),
            (NsAirSync, "Partial",           0, 0x26),
            (NsAirSync, "ConversationMode",  0, 0x27),
            (NsAirSync, "MaxItems",          0, 0x28),
            (NsAirSync, "HeartbeatInterval", 0, 0x29),

            // ── Codepage 2: Email ────────────────────────────────────────────
            (NsEmail, "Attachment",      2, 0x05),
            (NsEmail, "Attachments",     2, 0x06),
            (NsEmail, "AttName",         2, 0x07),
            (NsEmail, "AttSize",         2, 0x08),
            (NsEmail, "Att0Id",          2, 0x09),
            (NsEmail, "AttMethod",       2, 0x0A),
            (NsEmail, "AttRemoved",      2, 0x0B),
            (NsEmail, "Body",            2, 0x0C),
            (NsEmail, "BodySize",        2, 0x0D),
            (NsEmail, "BodyTruncated",   2, 0x0E),
            (NsEmail, "DateReceived",    2, 0x0F),
            (NsEmail, "DisplayName",     2, 0x10),
            (NsEmail, "DisplayTo",       2, 0x11),
            (NsEmail, "Importance",      2, 0x12),
            (NsEmail, "MessageClass",    2, 0x13),
            (NsEmail, "Subject",         2, 0x14),
            (NsEmail, "Read",            2, 0x15),
            (NsEmail, "To",              2, 0x16),
            (NsEmail, "Cc",              2, 0x17),
            (NsEmail, "From",            2, 0x18),
            (NsEmail, "ReplyTo",         2, 0x19),
            (NsEmail, "AllDayEvent",     2, 0x1A),
            (NsEmail, "Categories",      2, 0x1B),
            (NsEmail, "Category",        2, 0x1C),
            (NsEmail, "DtStamp",         2, 0x1D),
            (NsEmail, "EndTime",         2, 0x1E),
            (NsEmail, "InstanceType",    2, 0x1F),
            (NsEmail, "BusyStatus",      2, 0x20),
            (NsEmail, "OrganizerName",   2, 0x21),
            (NsEmail, "OrganizerEmail",  2, 0x22),
            (NsEmail, "NativeBodyType",  2, 0x23),
            (NsEmail, "TimeZone",        2, 0x24),
            (NsEmail, "GlobalObjId",     2, 0x25),
            (NsEmail, "ThreadTopic",     2, 0x26),
            (NsEmail, "MIMEData",        2, 0x27),
            (NsEmail, "MIMESize",        2, 0x28),
            (NsEmail, "MIMETruncated",   2, 0x29),
            (NsEmail, "InternetCPID",    2, 0x2A),
            (NsEmail, "Flag",            2, 0x2B),
            (NsEmail, "Status",          2, 0x2C),
            (NsEmail, "ContentClass",    2, 0x2D),
            (NsEmail, "FlagType",        2, 0x2E),
            (NsEmail, "CompleteTime",    2, 0x2F),
            (NsEmail, "DisallowNewTimeProposal", 2, 0x30),

            // ── Codepage 7: FolderHierarchy ──────────────────────────────────
            (NsFolderHier, "Folders",      7, 0x05),
            (NsFolderHier, "Folder",       7, 0x06),
            (NsFolderHier, "DisplayName",  7, 0x07),
            (NsFolderHier, "ServerId",     7, 0x08),
            (NsFolderHier, "ParentId",     7, 0x09),
            (NsFolderHier, "Type",         7, 0x0A),
            (NsFolderHier, "Response",     7, 0x0B),
            (NsFolderHier, "Status",       7, 0x0C),
            (NsFolderHier, "ContentClass", 7, 0x0D),
            (NsFolderHier, "Changes",      7, 0x0E),
            (NsFolderHier, "Add",          7, 0x0F),
            (NsFolderHier, "Delete",       7, 0x10),
            (NsFolderHier, "Update",       7, 0x11),
            (NsFolderHier, "SyncKey",      7, 0x12),
            (NsFolderHier, "FolderCreate", 7, 0x13),
            (NsFolderHier, "FolderDelete", 7, 0x14),
            (NsFolderHier, "FolderUpdate", 7, 0x15),
            (NsFolderHier, "FolderSync",   7, 0x16),
            (NsFolderHier, "Count",        7, 0x17),

            // ── Codepage 14: Provision ───────────────────────────────────────
            (NsProvision, "Provision",          14, 0x05),
            (NsProvision, "Policies",           14, 0x06),
            (NsProvision, "Policy",             14, 0x07),
            (NsProvision, "PolicyType",         14, 0x08),
            (NsProvision, "PolicyKey",          14, 0x09),
            (NsProvision, "Data",               14, 0x0A),
            (NsProvision, "Status",             14, 0x0B),
            (NsProvision, "RemoteWipe",         14, 0x0C),
            (NsProvision, "EASProvisionDoc",    14, 0x0D),
            (NsProvision, "DevicePasswordEnabled",              14, 0x0E),
            (NsProvision, "AlphanumericDevicePasswordRequired", 14, 0x0F),
            (NsProvision, "RequireStorageCardEncryption",       14, 0x10),
            (NsProvision, "PasswordRecoveryEnabled",            14, 0x11),
            (NsProvision, "DocumentBrowseEnabled",              14, 0x12),
            (NsProvision, "AttachmentsEnabled",                 14, 0x13),
            (NsProvision, "MaxAttachmentSize",                  14, 0x14),
            (NsProvision, "MaxCalendarAgeFilter",               14, 0x15),
            (NsProvision, "AllowSimpleDevicePassword",          14, 0x16),
            (NsProvision, "DevicePasswordExpiration",           14, 0x17),
            (NsProvision, "DevicePasswordHistory",              14, 0x18),
            (NsProvision, "MaxEmailAgeFilter",                  14, 0x19),
            (NsProvision, "MaxEmailBodyTruncationSize",         14, 0x1A),
            (NsProvision, "MaxEmailHTMLBodyTruncationSize",     14, 0x1B),
            (NsProvision, "RequireSignedSMIMEMessages",         14, 0x1C),
            (NsProvision, "RequireEncryptedSMIMEMessages",      14, 0x1D),
            (NsProvision, "RequireSignedSMIMEAlgorithm",        14, 0x1E),
            (NsProvision, "RequireEncryptionSMIMEAlgorithm",    14, 0x1F),
            (NsProvision, "AllowSMIMEEncryptionAlgorithmNegotiation", 14, 0x20),
            (NsProvision, "AllowSMIMESoftCerts",         14, 0x21),
            (NsProvision, "AllowBrowser",                14, 0x22),
            (NsProvision, "AllowConsumerEmail",          14, 0x23),
            (NsProvision, "AllowDesktopSync",            14, 0x24),
            (NsProvision, "AllowHTMLEmail",              14, 0x25),
            (NsProvision, "AllowInternetSharing",        14, 0x26),
            (NsProvision, "AllowIrDA",                   14, 0x27),
            (NsProvision, "AllowPOPIMAPEmail",           14, 0x28),
            (NsProvision, "AllowRemoteDesktop",          14, 0x29),
            (NsProvision, "AllowStorageCard",            14, 0x2A),
            (NsProvision, "AllowTextMessaging",          14, 0x2B),
            (NsProvision, "AllowUnsignedApplications",   14, 0x2C),
            (NsProvision, "AllowUnsignedInstallationPackages", 14, 0x2D),
            (NsProvision, "AllowWifi",                   14, 0x2E),
            (NsProvision, "AllowBluetooth",              14, 0x2F),
            (NsProvision, "ApprovedApplicationList",     14, 0x30),
            (NsProvision, "Hash",                        14, 0x31),

            // ── Codepage 17: AirSyncBase ─────────────────────────────────────
            (NsAirSyncBase, "BodyPreference",    17, 0x05),
            (NsAirSyncBase, "Type",              17, 0x06),
            (NsAirSyncBase, "TruncationSize",    17, 0x07),
            (NsAirSyncBase, "AllOrNone",         17, 0x08),
            // 0x09 is reserved
            (NsAirSyncBase, "Body",              17, 0x0A),
            (NsAirSyncBase, "Data",              17, 0x0B),
            (NsAirSyncBase, "EstimatedDataSize", 17, 0x0C),
            (NsAirSyncBase, "Truncated",         17, 0x0D),
            (NsAirSyncBase, "Attachments",       17, 0x0E),
            (NsAirSyncBase, "Attachment",        17, 0x0F),
            (NsAirSyncBase, "DisplayName",       17, 0x10),
            (NsAirSyncBase, "FileReference",     17, 0x11),
            (NsAirSyncBase, "Method",            17, 0x12),
            (NsAirSyncBase, "ContentId",         17, 0x13),
            (NsAirSyncBase, "ContentLocation",   17, 0x14),
            (NsAirSyncBase, "IsInline",          17, 0x15),
            (NsAirSyncBase, "NativeBodyType",    17, 0x16),
            (NsAirSyncBase, "ContentType",       17, 0x17),
            (NsAirSyncBase, "Preview",           17, 0x18),
            (NsAirSyncBase, "BodyPartPreference",17, 0x19),
            (NsAirSyncBase, "BodyPart",          17, 0x1A),
            (NsAirSyncBase, "Status",            17, 0x1B),
        };

        EncodeMap = new(defs.Length);
        DecodeMap = new(defs.Length);
        foreach (var (ns, name, page, tok) in defs)
        {
            EncodeMap.TryAdd((ns, name), (page, tok));
            DecodeMap.TryAdd((page, tok), (ns, name));
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Encodes an XElement tree into an EAS WBXML byte array.</summary>
    public static byte[] Encode(XElement root)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x03); // WBXML 1.3
        ms.WriteByte(0x01); // public identifier: unknown
        ms.WriteByte(0x6A); // charset: UTF-8 (106)
        ms.WriteByte(0x00); // string-table length: 0
        byte page = 0xFF;
        WriteElement(ms, root, ref page);
        return ms.ToArray();
    }

    /// <summary>Decodes an EAS WBXML byte array into an XElement tree.</summary>
    public static XElement Decode(byte[] data)
        => new WbxmlReader(data).ReadDocument();

    // ── Encoder ───────────────────────────────────────────────────────────────

    private static void WriteElement(Stream s, XElement el, ref byte page)
    {
        var key = (el.Name.NamespaceName, el.Name.LocalName);
        if (!EncodeMap.TryGetValue(key, out var tag))
            throw new InvalidOperationException(
                $"WBXML: unknown EAS tag {{{el.Name.NamespaceName}}}{el.Name.LocalName}");

        if (tag.page != page)
        {
            s.WriteByte(TokSwitchPage);
            s.WriteByte(tag.page);
            page = tag.page;
        }

        bool hasContent = el.HasElements || el.Value.Length > 0;
        s.WriteByte((byte)(tag.tok | (hasContent ? FlagContent : 0)));

        if (hasContent)
        {
            if (el.HasElements)
                foreach (var child in el.Elements())
                    WriteElement(s, child, ref page);
            else
                WriteStrI(s, el.Value);

            s.WriteByte(TokEnd);
        }
    }

    private static void WriteStrI(Stream s, string text)
    {
        s.WriteByte(TokStrI);
        var bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
        s.WriteByte(0x00); // null terminator
    }

    // ── Decoder ───────────────────────────────────────────────────────────────

    private sealed class WbxmlReader
    {
        private readonly byte[] _data;
        private int  _pos;
        private byte _page;

        public WbxmlReader(byte[] data) { _data = data; }

        public XElement ReadDocument()
        {
            _pos = 1;                         // skip version byte
            SkipMbInt();                      // public identifier
            SkipMbInt();                      // charset
            var strtblLen = (int)ReadMbInt(); // string table length
            _pos += strtblLen;                // skip string table bytes
            return ReadElement() ?? throw new InvalidOperationException("Empty WBXML document");
        }

        private XElement? ReadElement()
        {
            while (_pos < _data.Length)
            {
                byte tok = _data[_pos++];

                if (tok == TokSwitchPage) { _page = _data[_pos++]; continue; }
                if (tok == TokEnd)        return null;
                if (tok == TokStrI)       { SkipStrI(); continue; }
                if (tok == TokOpaque)     { SkipOpaque(); continue; }

                byte tagCode    = (byte)(tok & 0x3F);
                bool hasContent = (tok & FlagContent) != 0;

                if (!DecodeMap.TryGetValue((_page, tagCode), out var info))
                    throw new InvalidOperationException(
                        $"WBXML: unknown tag page={_page} tok=0x{tagCode:X2}");

                var el = new XElement(XNamespace.Get(info.ns) + info.name);

                if (hasContent)
                {
                    while (_pos < _data.Length)
                    {
                        byte next = _data[_pos];

                        if (next == TokEnd)        { _pos++; break; }
                        if (next == TokSwitchPage) { _pos++; _page = _data[_pos++]; continue; }

                        if (next == TokStrI)
                        {
                            _pos++;
                            el.Add(ReadStrI());
                            continue;
                        }

                        if (next == TokOpaque)
                        {
                            _pos++;
                            el.Add(ReadOpaque());
                            continue;
                        }

                        var child = ReadElement();
                        if (child != null) el.Add(child);
                    }
                }

                return el;
            }
            return null;
        }

        // ── String helpers ────────────────────────────────────────────────────

        private string ReadStrI()
        {
            int start = _pos;
            while (_pos < _data.Length && _data[_pos] != 0x00) _pos++;
            var s = Encoding.UTF8.GetString(_data, start, _pos - start);
            if (_pos < _data.Length) _pos++; // consume null terminator
            return s;
        }

        private string ReadOpaque()
        {
            int len = (int)ReadMbInt();
            var s   = Encoding.UTF8.GetString(_data, _pos, len);
            _pos   += len;
            return s;
        }

        private void SkipStrI()
        {
            while (_pos < _data.Length && _data[_pos++] != 0x00) { }
        }

        private void SkipOpaque()
        {
            _pos += (int)ReadMbInt();
        }

        // ── Multi-byte integer (WBXML mbint) ──────────────────────────────────

        private uint ReadMbInt()
        {
            uint v = 0;
            byte b;
            do { b = _data[_pos++]; v = (v << 7) | (uint)(b & 0x7F); }
            while ((b & 0x80) != 0);
            return v;
        }

        private void SkipMbInt() => ReadMbInt();
    }
}
