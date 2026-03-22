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
    private const string NsContacts   = "Contacts:";
    private const string NsCalendar   = "Calendar:";
    private const string NsMove       = "Move:";
    private const string NsItemEst    = "GetItemEstimate:";
    private const string NsMeetResp   = "MeetingResponse:";
    private const string NsTasks      = "Tasks:";
    private const string NsResolve    = "ResolveRecipients:";
    private const string NsValidCert  = "ValidateCert:";
    private const string NsContacts2  = "Contacts2:";
    private const string NsPing       = "Ping:";
    private const string NsSearch     = "Search:";
    private const string NsGAL        = "GAL:";
    private const string NsEmail2     = "Email2:";
    private const string NsNotes      = "Notes:";
    private const string NsRights     = "RightsManagement:";
    private const string NsSettings   = "Settings:";
    private const string NsDocLib     = "DocumentLibrary:";
    private const string NsItemOps    = "ItemOperations:";
    private const string NsCompose    = "ComposeMail:";

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

            // ── Codepage 1: Contacts ────────────────────────────────────────
            (NsContacts, "Anniversary",       1, 0x05),
            (NsContacts, "AssistantName",     1, 0x06),
            (NsContacts, "AssistantTelephoneNumber", 1, 0x07),
            (NsContacts, "Birthday",          1, 0x08),
            (NsContacts, "Body",              1, 0x09),
            (NsContacts, "BodySize",          1, 0x0A),
            (NsContacts, "BodyTruncated",     1, 0x0B),
            (NsContacts, "Business2PhoneNumber",    1, 0x0C),
            (NsContacts, "BusinessCity",            1, 0x0D),
            (NsContacts, "BusinessCountry",         1, 0x0E),
            (NsContacts, "BusinessPostalCode",      1, 0x0F),
            (NsContacts, "BusinessState",           1, 0x10),
            (NsContacts, "BusinessStreet",          1, 0x11),
            (NsContacts, "BusinessFaxNumber",       1, 0x12),
            (NsContacts, "BusinessPhoneNumber",     1, 0x13),
            (NsContacts, "CarPhoneNumber",          1, 0x14),
            (NsContacts, "Categories",              1, 0x15),
            (NsContacts, "Category",                1, 0x16),
            (NsContacts, "Children",                1, 0x17),
            (NsContacts, "Child",                   1, 0x18),
            (NsContacts, "CompanyName",             1, 0x19),
            (NsContacts, "Department",              1, 0x1A),
            (NsContacts, "Email1Address",           1, 0x1B),
            (NsContacts, "Email2Address",           1, 0x1C),
            (NsContacts, "Email3Address",           1, 0x1D),
            (NsContacts, "FileAs",                  1, 0x1E),
            (NsContacts, "FirstName",               1, 0x1F),
            (NsContacts, "Home2PhoneNumber",        1, 0x20),
            (NsContacts, "HomeCity",                1, 0x21),
            (NsContacts, "HomeCountry",             1, 0x22),
            (NsContacts, "HomePostalCode",          1, 0x23),
            (NsContacts, "HomeState",               1, 0x24),
            (NsContacts, "HomeStreet",              1, 0x25),
            (NsContacts, "HomeFaxNumber",           1, 0x26),
            (NsContacts, "HomePhoneNumber",         1, 0x27),
            (NsContacts, "JobTitle",                1, 0x28),
            (NsContacts, "LastName",                1, 0x29),
            (NsContacts, "MiddleName",              1, 0x2A),
            (NsContacts, "MobilePhoneNumber",       1, 0x2B),
            (NsContacts, "OfficeLocation",          1, 0x2C),
            (NsContacts, "OtherCity",               1, 0x2D),
            (NsContacts, "OtherCountry",            1, 0x2E),
            (NsContacts, "OtherPostalCode",         1, 0x2F),
            (NsContacts, "OtherState",              1, 0x30),
            (NsContacts, "OtherStreet",             1, 0x31),
            (NsContacts, "PagerNumber",             1, 0x32),
            (NsContacts, "RadioPhoneNumber",        1, 0x33),
            (NsContacts, "Spouse",                  1, 0x34),
            (NsContacts, "Suffix",                  1, 0x35),
            (NsContacts, "Title",                   1, 0x36),
            (NsContacts, "WebPage",                 1, 0x37),
            (NsContacts, "YomiCompanyName",         1, 0x38),
            (NsContacts, "YomiFirstName",           1, 0x39),
            (NsContacts, "YomiLastName",            1, 0x3A),
            (NsContacts, "CompressedRTF",           1, 0x3B),
            (NsContacts, "Picture",                 1, 0x3C),
            (NsContacts, "Alias",                   1, 0x3D),
            (NsContacts, "WeightedRank",            1, 0x3E),

            // ── Codepage 2: Email ────────────────────────────────────────────
            (NsEmail, "Attachment",      2, 0x05),
            (NsEmail, "Attachments",     2, 0x06),  // deprecated 12.0+, use AirSyncBase
            (NsEmail, "AttName",         2, 0x07),
            (NsEmail, "AttSize",         2, 0x08),
            (NsEmail, "Att0Id",          2, 0x09),
            (NsEmail, "AttMethod",       2, 0x0A),
            // 0x0B not used
            (NsEmail, "Body",            2, 0x0C),  // deprecated 12.0+, use AirSyncBase
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
            (NsEmail, "Location",        2, 0x21),  // deprecated 16.0+, use AirSyncBase
            (NsEmail, "MeetingRequest",  2, 0x22),
            (NsEmail, "Organizer",       2, 0x23),
            (NsEmail, "RecurrenceId",    2, 0x24),
            (NsEmail, "Reminder",        2, 0x25),
            (NsEmail, "ResponseRequested", 2, 0x26),
            (NsEmail, "Recurrences",     2, 0x27),
            (NsEmail, "Recurrence",      2, 0x28),
            (NsEmail, "Type",            2, 0x29),
            (NsEmail, "Until",           2, 0x2A),
            (NsEmail, "Occurrences",     2, 0x2B),
            (NsEmail, "Interval",        2, 0x2C),
            (NsEmail, "DayOfWeek",       2, 0x2D),
            (NsEmail, "DayOfMonth",      2, 0x2E),
            (NsEmail, "WeekOfMonth",     2, 0x2F),
            (NsEmail, "MonthOfYear",     2, 0x30),
            (NsEmail, "StartTime",       2, 0x31),
            (NsEmail, "Sensitivity",     2, 0x32),
            (NsEmail, "TimeZone",        2, 0x33),
            (NsEmail, "GlobalObjId",     2, 0x34),
            (NsEmail, "ThreadTopic",     2, 0x35),
            (NsEmail, "MIMEData",        2, 0x36),
            (NsEmail, "MIMETruncated",   2, 0x37),
            (NsEmail, "MIMESize",        2, 0x38),
            (NsEmail, "InternetCPID",    2, 0x39),
            (NsEmail, "Flag",            2, 0x3A),
            (NsEmail, "Status",          2, 0x3B),
            (NsEmail, "ContentClass",    2, 0x3C),
            (NsEmail, "FlagType",        2, 0x3D),
            (NsEmail, "CompleteTime",    2, 0x3E),
            (NsEmail, "DisallowNewTimeProposal", 2, 0x3F),

            // ── Codepage 4: Calendar ────────────────────────────────────────
            (NsCalendar, "TimeZone",          4, 0x05),
            (NsCalendar, "AllDayEvent",       4, 0x06),
            (NsCalendar, "Attendees",         4, 0x07),
            (NsCalendar, "Attendee",          4, 0x08),
            (NsCalendar, "Email",             4, 0x09),
            (NsCalendar, "Name",              4, 0x0A),
            (NsCalendar, "Body",              4, 0x0B),
            (NsCalendar, "BodyTruncated",     4, 0x0C),
            (NsCalendar, "BusyStatus",        4, 0x0D),
            (NsCalendar, "Categories",        4, 0x0E),
            (NsCalendar, "Category",          4, 0x0F),
            (NsCalendar, "CompressedRTF",     4, 0x10),
            (NsCalendar, "DtStamp",           4, 0x11),
            (NsCalendar, "EndTime",           4, 0x12),
            (NsCalendar, "Exception",         4, 0x13),
            (NsCalendar, "Exceptions",        4, 0x14),
            (NsCalendar, "Deleted",           4, 0x15),
            (NsCalendar, "ExceptionStartTime",4, 0x16),
            (NsCalendar, "Location",          4, 0x17),
            (NsCalendar, "MeetingStatus",     4, 0x18),
            (NsCalendar, "OrganizerEmail",    4, 0x19),
            (NsCalendar, "OrganizerName",     4, 0x1A),
            (NsCalendar, "Recurrence",        4, 0x1B),
            (NsCalendar, "Type",              4, 0x1C),
            (NsCalendar, "Until",             4, 0x1D),
            (NsCalendar, "Occurrences",       4, 0x1E),
            (NsCalendar, "Interval",          4, 0x1F),
            (NsCalendar, "DayOfWeek",         4, 0x20),
            (NsCalendar, "DayOfMonth",        4, 0x21),
            (NsCalendar, "WeekOfMonth",       4, 0x22),
            (NsCalendar, "MonthOfYear",       4, 0x23),
            (NsCalendar, "Reminder",          4, 0x24),
            (NsCalendar, "Sensitivity",       4, 0x25),
            (NsCalendar, "Subject",           4, 0x26),
            (NsCalendar, "StartTime",         4, 0x27),
            (NsCalendar, "UID",               4, 0x28),
            (NsCalendar, "AttendeeStatus",    4, 0x29),
            (NsCalendar, "AttendeeType",      4, 0x2A),
            (NsCalendar, "DisallowNewTimeProposal", 4, 0x33),
            (NsCalendar, "ResponseRequested", 4, 0x34),
            (NsCalendar, "AppointmentReplyTime", 4, 0x35),
            (NsCalendar, "ResponseType",      4, 0x36),
            (NsCalendar, "CalendarType",      4, 0x37),
            (NsCalendar, "IsLeapMonth",       4, 0x38),
            (NsCalendar, "FirstDayOfWeek",    4, 0x39),
            (NsCalendar, "OnlineMeetingConfLink",   4, 0x3A),
            (NsCalendar, "OnlineMeetingExternalLink",4, 0x3B),

            // ── Codepage 5: Move ────────────────────────────────────────────
            (NsMove, "MoveItems",   5, 0x05),
            (NsMove, "Move",        5, 0x06),
            (NsMove, "SrcMsgId",    5, 0x07),
            (NsMove, "SrcFldId",    5, 0x08),
            (NsMove, "DstFldId",    5, 0x09),
            (NsMove, "Response",    5, 0x0A),
            (NsMove, "Status",      5, 0x0B),
            (NsMove, "DstMsgId",    5, 0x0C),

            // ── Codepage 6: GetItemEstimate ─────────────────────────────────
            (NsItemEst, "GetItemEstimate", 6, 0x05),
            (NsItemEst, "Version",         6, 0x06),
            (NsItemEst, "Collections",     6, 0x07),
            (NsItemEst, "Collection",      6, 0x08),
            (NsItemEst, "Class",           6, 0x09),
            (NsItemEst, "CollectionId",    6, 0x0A),
            (NsItemEst, "DateTime",        6, 0x0B),
            (NsItemEst, "Estimate",        6, 0x0C),
            (NsItemEst, "Response",        6, 0x0D),
            (NsItemEst, "Status",          6, 0x0E),

            // ── Codepage 7: FolderHierarchy ──────────────────────────────────────
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

            // ── Codepage 8: MeetingResponse ──────────────────────────────
            (NsMeetResp, "CalendarId",       8, 0x05),
            (NsMeetResp, "CollectionId",     8, 0x06),
            (NsMeetResp, "MeetingResponse",  8, 0x07),
            (NsMeetResp, "RequestId",        8, 0x08),
            (NsMeetResp, "Request",          8, 0x09),
            (NsMeetResp, "Result",           8, 0x0A),
            (NsMeetResp, "Status",           8, 0x0B),
            (NsMeetResp, "UserResponse",     8, 0x0C),
            (NsMeetResp, "InstanceId",       8, 0x0E),
            (NsMeetResp, "SendResponse",     8, 0x12),

            // ── Codepage 9: Tasks ───────────────────────────────────────────
            (NsTasks, "Body",              9, 0x05),
            (NsTasks, "BodySize",          9, 0x06),
            (NsTasks, "BodyTruncated",     9, 0x07),
            (NsTasks, "Categories",        9, 0x08),
            (NsTasks, "Category",          9, 0x09),
            (NsTasks, "Complete",          9, 0x0A),
            (NsTasks, "DateCompleted",     9, 0x0B),
            (NsTasks, "DueDate",           9, 0x0C),
            (NsTasks, "UtcDueDate",        9, 0x0D),
            (NsTasks, "Importance",        9, 0x0E),
            (NsTasks, "Recurrence",        9, 0x0F),
            (NsTasks, "Type",              9, 0x10),
            (NsTasks, "Start",             9, 0x11),
            (NsTasks, "UtcStartDate",      9, 0x12),
            (NsTasks, "Subject",           9, 0x13),
            (NsTasks, "Sensitivity",       9, 0x14),
            (NsTasks, "ReminderSet",       9, 0x15),
            (NsTasks, "ReminderTime",      9, 0x16),
            (NsTasks, "Recurrence_Type",          9, 0x17),  // alias in some docs
            (NsTasks, "Recurrence_Start",         9, 0x18),
            (NsTasks, "Recurrence_Until",         9, 0x19),
            (NsTasks, "Recurrence_Occurrences",   9, 0x1A),
            (NsTasks, "Recurrence_Interval",      9, 0x1B),
            (NsTasks, "Recurrence_DayOfMonth",    9, 0x1C),
            (NsTasks, "Recurrence_DayOfWeek",     9, 0x1D),
            (NsTasks, "Recurrence_WeekOfMonth",   9, 0x1E),
            (NsTasks, "Recurrence_MonthOfYear",   9, 0x1F),
            (NsTasks, "Recurrence_Regenerate",    9, 0x20),
            (NsTasks, "Recurrence_DeadOccur",     9, 0x21),
            (NsTasks, "OrdinalDate",              9, 0x22),
            (NsTasks, "SubOrdinalDate",           9, 0x23),
            (NsTasks, "CalendarType",             9, 0x24),
            (NsTasks, "IsLeapMonth",              9, 0x25),
            (NsTasks, "FirstDayOfWeek",           9, 0x26),

            // ── Codepage 10: ResolveRecipients ──────────────────────────────
            (NsResolve, "ResolveRecipients",  10, 0x05),
            (NsResolve, "Response",           10, 0x06),
            (NsResolve, "Status",             10, 0x07),
            (NsResolve, "Type",               10, 0x08),
            (NsResolve, "Recipient",          10, 0x09),
            (NsResolve, "DisplayName",        10, 0x0A),
            (NsResolve, "EmailAddress",       10, 0x0B),
            (NsResolve, "Certificates",       10, 0x0C),
            (NsResolve, "Certificate",        10, 0x0D),
            (NsResolve, "MiniCertificate",    10, 0x0E),
            (NsResolve, "Options",            10, 0x0F),
            (NsResolve, "To",                 10, 0x10),
            (NsResolve, "CertificateCount",   10, 0x11),
            (NsResolve, "RecipientCount",     10, 0x12),
            (NsResolve, "Availability",       10, 0x13),
            (NsResolve, "StartTime",          10, 0x14),
            (NsResolve, "EndTime",            10, 0x15),
            (NsResolve, "MergedFreeBusy",     10, 0x16),
            (NsResolve, "Picture",            10, 0x17),
            (NsResolve, "MaxSize",            10, 0x18),
            (NsResolve, "Data",               10, 0x19),
            (NsResolve, "MaxPictures",        10, 0x1A),

            // ── Codepage 11: ValidateCert ───────────────────────────────────
            (NsValidCert, "ValidateCert",       11, 0x05),
            (NsValidCert, "Certificates",       11, 0x06),
            (NsValidCert, "Certificate",        11, 0x07),
            (NsValidCert, "CertificateChain",   11, 0x08),
            (NsValidCert, "CheckCRL",           11, 0x09),
            (NsValidCert, "Status",             11, 0x0A),

            // ── Codepage 12: Contacts2 ──────────────────────────────────────
            (NsContacts2, "CustomerId",         12, 0x05),
            (NsContacts2, "GovernmentId",       12, 0x06),
            (NsContacts2, "IMAddress",          12, 0x07),
            (NsContacts2, "IMAddress2",         12, 0x08),
            (NsContacts2, "IMAddress3",         12, 0x09),
            (NsContacts2, "ManagerName",        12, 0x0A),
            (NsContacts2, "CompanyMainPhone",   12, 0x0B),
            (NsContacts2, "AccountName",        12, 0x0C),
            (NsContacts2, "NickName",           12, 0x0D),
            (NsContacts2, "MMS",                12, 0x0E),

            // ── Codepage 13: Ping ───────────────────────────────────────────
            (NsPing, "Ping",                13, 0x05),
            (NsPing, "AutdState",           13, 0x06),
            (NsPing, "Status",              13, 0x07),
            (NsPing, "HeartbeatInterval",   13, 0x08),
            (NsPing, "Folders",             13, 0x09),
            (NsPing, "Folder",              13, 0x0A),
            (NsPing, "Id",                  13, 0x0B),
            (NsPing, "Class",               13, 0x0C),
            (NsPing, "MaxFolders",          13, 0x0D),

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

            // ── Codepage 22: Email2 ─────────────────────────────────────────
            (NsEmail2, "UmCallerID",            22, 0x05),
            (NsEmail2, "UmUserNotes",           22, 0x06),
            (NsEmail2, "UmAttDuration",         22, 0x07),
            (NsEmail2, "UmAttOrder",            22, 0x08),
            (NsEmail2, "ConversationId",        22, 0x09),
            (NsEmail2, "ConversationIndex",     22, 0x0A),
            (NsEmail2, "LastVerbExecuted",      22, 0x0B),
            (NsEmail2, "LastVerbExecutionTime", 22, 0x0C),
            (NsEmail2, "ReceivedAsBcc",         22, 0x0D),
            (NsEmail2, "Sender",                22, 0x0E),
            (NsEmail2, "CalendarType",          22, 0x0F),
            (NsEmail2, "IsLeapMonth",           22, 0x10),
            (NsEmail2, "AccountId",             22, 0x11),
            (NsEmail2, "FirstDayOfWeek",        22, 0x12),
            (NsEmail2, "MeetingMessageType",    22, 0x13),
            // 0x14 not used
            (NsEmail2, "IsDraft",               22, 0x15),
            (NsEmail2, "Bcc",                   22, 0x16),
            (NsEmail2, "Send",                  22, 0x17),

            // ── Codepage 15: Search ─────────────────────────────────────────
            (NsSearch, "Search",            15, 0x05),
            (NsSearch, "Store",             15, 0x07),
            (NsSearch, "Name",              15, 0x08),
            (NsSearch, "Query",             15, 0x09),
            (NsSearch, "Options",           15, 0x0A),
            (NsSearch, "Range",             15, 0x0B),
            (NsSearch, "Status",            15, 0x0C),
            (NsSearch, "Response",          15, 0x0D),
            (NsSearch, "Result",            15, 0x0E),
            (NsSearch, "Properties",        15, 0x0F),
            (NsSearch, "Total",             15, 0x10),
            (NsSearch, "EqualTo",           15, 0x11),
            (NsSearch, "Value",             15, 0x12),
            (NsSearch, "And",               15, 0x13),
            (NsSearch, "Or",                15, 0x14),
            (NsSearch, "FreeText",          15, 0x15),
            (NsSearch, "DeepTraversal",     15, 0x17),
            (NsSearch, "LongId",            15, 0x18),
            (NsSearch, "RebuildResults",    15, 0x19),
            (NsSearch, "LessThan",          15, 0x1A),
            (NsSearch, "GreaterThan",       15, 0x1B),
            (NsSearch, "Schema",            15, 0x1C),
            (NsSearch, "Supported",         15, 0x1D),
            (NsSearch, "UserName",          15, 0x1E),
            (NsSearch, "Password",          15, 0x1F),
            (NsSearch, "ConversationId",    15, 0x20),
            (NsSearch, "Picture",           15, 0x21),
            (NsSearch, "MaxSize",           15, 0x22),
            (NsSearch, "MaxPictures",       15, 0x23),

            // ── Codepage 16: GAL ────────────────────────────────────────────
            (NsGAL, "DisplayName",    16, 0x05),
            (NsGAL, "Phone",          16, 0x06),
            (NsGAL, "Office",         16, 0x07),
            (NsGAL, "Title",          16, 0x08),
            (NsGAL, "Company",        16, 0x09),
            (NsGAL, "Alias",          16, 0x0A),
            (NsGAL, "FirstName",      16, 0x0B),
            (NsGAL, "LastName",       16, 0x0C),
            (NsGAL, "HomePhone",      16, 0x0D),
            (NsGAL, "MobilePhone",    16, 0x0E),
            (NsGAL, "EmailAddress",   16, 0x0F),
            (NsGAL, "Picture",        16, 0x10),
            (NsGAL, "Status",         16, 0x11),
            (NsGAL, "Data",           16, 0x12),

            // ── Codepage 18: Settings ───────────────────────────────────────
            (NsSettings, "Settings",          18, 0x05),
            (NsSettings, "Status",            18, 0x06),
            (NsSettings, "Get",               18, 0x07),
            (NsSettings, "Set",               18, 0x08),
            (NsSettings, "Oof",               18, 0x09),
            (NsSettings, "OofState",          18, 0x0A),
            (NsSettings, "StartTime",         18, 0x0B),
            (NsSettings, "EndTime",           18, 0x0C),
            (NsSettings, "OofMessage",        18, 0x0D),
            (NsSettings, "AppliesToInternal",          18, 0x0E),
            (NsSettings, "AppliesToExternalKnown",     18, 0x0F),
            (NsSettings, "AppliesToExternalUnknown",   18, 0x10),
            (NsSettings, "Enabled",           18, 0x11),
            (NsSettings, "ReplyMessage",      18, 0x12),
            (NsSettings, "BodyType",          18, 0x13),
            (NsSettings, "DevicePassword",    18, 0x14),
            (NsSettings, "Password",          18, 0x15),
            (NsSettings, "DeviceInformation", 18, 0x16),
            (NsSettings, "Model",             18, 0x17),
            (NsSettings, "IMEI",              18, 0x18),
            (NsSettings, "FriendlyName",      18, 0x19),
            (NsSettings, "OS",                18, 0x1A),
            (NsSettings, "OSLanguage",        18, 0x1B),
            (NsSettings, "PhoneNumber",       18, 0x1C),
            (NsSettings, "UserInformation",   18, 0x1D),
            (NsSettings, "EmailAddresses",    18, 0x1E),
            (NsSettings, "SmtpAddress",       18, 0x1F),
            (NsSettings, "UserAgent",         18, 0x20),
            (NsSettings, "EnableOutboundSMS", 18, 0x21),
            (NsSettings, "MobileOperator",    18, 0x22),
            (NsSettings, "PrimarySmtpAddress",18, 0x23),
            (NsSettings, "Accounts",          18, 0x24),
            (NsSettings, "Account",           18, 0x25),
            (NsSettings, "AccountId",         18, 0x26),
            (NsSettings, "AccountName",       18, 0x27),
            (NsSettings, "UserDisplayName",   18, 0x28),
            (NsSettings, "SendDisabled",      18, 0x29),
            (NsSettings, "RightsManagementInformation", 18, 0x2B),

            // ── Codepage 19: DocumentLibrary ────────────────────────────────
            (NsDocLib, "LinkId",           19, 0x05),
            (NsDocLib, "DisplayName",      19, 0x06),
            (NsDocLib, "IsFolder",         19, 0x07),
            (NsDocLib, "CreationDate",     19, 0x08),
            (NsDocLib, "LastModifiedDate", 19, 0x09),
            (NsDocLib, "IsHidden",         19, 0x0A),
            (NsDocLib, "ContentLength",    19, 0x0B),
            (NsDocLib, "ContentType",      19, 0x0C),

            // ── Codepage 20: ItemOperations ─────────────────────────────────
            (NsItemOps, "ItemOperations",  20, 0x05),
            (NsItemOps, "Fetch",           20, 0x06),
            (NsItemOps, "Store",           20, 0x07),
            (NsItemOps, "Options",         20, 0x08),
            (NsItemOps, "Range",           20, 0x09),
            (NsItemOps, "Total",           20, 0x0A),
            (NsItemOps, "Properties",      20, 0x0B),
            (NsItemOps, "Data",            20, 0x0C),
            (NsItemOps, "Status",          20, 0x0D),
            (NsItemOps, "Response",        20, 0x0E),
            (NsItemOps, "Version",         20, 0x0F),
            (NsItemOps, "Schema",          20, 0x10),
            (NsItemOps, "Part",            20, 0x11),
            (NsItemOps, "EmptyFolderContents", 20, 0x12),
            (NsItemOps, "DeleteSubFolders",    20, 0x13),
            (NsItemOps, "UserName",        20, 0x14),
            (NsItemOps, "Password",        20, 0x15),
            (NsItemOps, "Move",            20, 0x16),
            (NsItemOps, "DstFldId",        20, 0x17),
            (NsItemOps, "ConversationId",  20, 0x18),
            (NsItemOps, "MoveAlways",      20, 0x19),

            // ── Codepage 21: ComposeMail ────────────────────────────────────
            (NsCompose, "SendMail",        21, 0x05),
            (NsCompose, "SmartForward",    21, 0x06),
            (NsCompose, "SmartReply",      21, 0x07),
            (NsCompose, "SaveInSentItems", 21, 0x08),
            (NsCompose, "ReplaceMime",     21, 0x09),
            (NsCompose, "Type",            21, 0x0A),  // removed in 14.1
            (NsCompose, "Source",          21, 0x0B),
            (NsCompose, "FolderId",        21, 0x0C),
            (NsCompose, "ItemId",          21, 0x0D),
            (NsCompose, "LongId",          21, 0x0E),
            (NsCompose, "InstanceId",      21, 0x0F),
            (NsCompose, "Mime",            21, 0x10),
            (NsCompose, "ClientId",        21, 0x11),
            (NsCompose, "Status",          21, 0x12),
            (NsCompose, "AccountId",       21, 0x13),
            (NsCompose, "Forwardees",      21, 0x15),
            (NsCompose, "Forwardee",       21, 0x16),
            (NsCompose, "ForwardeeName",   21, 0x17),
            (NsCompose, "ForwardeeEmail",  21, 0x18),

            // ── Codepage 23: Notes ──────────────────────────────────────────
            (NsNotes, "Subject",          23, 0x05),
            (NsNotes, "MessageClass",     23, 0x06),
            (NsNotes, "LastModifiedDate", 23, 0x07),
            (NsNotes, "Categories",       23, 0x08),
            (NsNotes, "Category",         23, 0x09),

            // ── Codepage 24: RightsManagement ───────────────────────────────
            (NsRights, "RightsManagementSupport",      24, 0x05),
            (NsRights, "RightsManagementTemplates",    24, 0x06),
            (NsRights, "RightsManagementTemplate",     24, 0x07),
            (NsRights, "RightsManagementLicense",      24, 0x08),
            (NsRights, "EditAllowed",                  24, 0x09),
            (NsRights, "ReplyAllowed",                 24, 0x0A),
            (NsRights, "ReplyAllAllowed",              24, 0x0B),
            (NsRights, "ForwardAllowed",               24, 0x0C),
            (NsRights, "ModifyRecipientsAllowed",      24, 0x0D),
            (NsRights, "ExtractAllowed",               24, 0x0E),
            (NsRights, "PrintAllowed",                 24, 0x0F),
            (NsRights, "ExportAllowed",                24, 0x10),
            (NsRights, "ProgrammaticAccessAllowed",    24, 0x11),
            (NsRights, "Owner",                        24, 0x12),
            (NsRights, "ContentExpiryDate",            24, 0x13),
            (NsRights, "TemplateID",                   24, 0x14),
            (NsRights, "TemplateName",                 24, 0x15),
            (NsRights, "TemplateDescription",          24, 0x16),
            (NsRights, "ContentOwner",                 24, 0x17),
            (NsRights, "RemoveRightsManagementDistribution", 24, 0x18),
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
