using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EasArchiver;

/// <summary>Authentication method for the EAS server.</summary>
public enum AuthMode
{
    Basic,
    Ntlm
}

/// <summary>Progress information reported during sync.</summary>
public class SyncProgress
{
    public string Phase       { get; init; } = "";
    public string FolderName  { get; init; } = "";
    public int    FoldersTotal    { get; init; }
    public int    FoldersActive   { get; init; }
    public int    EmailsNew       { get; init; }
    public int    RequestCount    { get; init; }
}

/// <summary>
/// Loaded from config.json (section "Eas").
/// Missing values are prompted interactively at startup.
/// </summary>
public class EasConfig
{
    public string ServerUrl       { get; set; } = "";
    public string Domain          { get; set; } = "";
    public string Username        { get; set; } = "";
    /// <summary>Runtime-only — never serialized to JSON. Always prompt interactively.</summary>
    [JsonIgnore]
    public string Password        { get; set; } = "";
    public string ArchiveDirectory{ get; set; } = "mail_archive";
    public int    WindowSize      { get; set; } = 50;
    /// <summary>0 = off  1 = URL+status  2 = +headers  3 = +body</summary>
    public int    Verbosity       { get; set; } = 0;
    public bool   DebugBlobs      { get; set; } = false;
    /// <summary>Fix raw UTF-8 in MIME headers with RFC 2047 Q-encoding (default true).</summary>
    public bool   FixHeaders     { get; set; } = true;
    /// <summary>Only sync folders matching these paths (empty = all). Matches subfolders too.</summary>
    public List<string> Include   { get; set; } = [];
    /// <summary>Skip folders matching these paths. Matches subfolders too. Applied after Include.</summary>
    public List<string> Exclude   { get; set; } = [];
    /// <summary>Authentication mode: Basic (default) or Ntlm.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthMode Auth          { get; set; } = AuthMode.Ntlm;
    /// <summary>Hard request cap per session. Throws if exceeded (prevents runaway loops). 0 = unlimited.</summary>
    public int      MaxRequests   { get; set; } = 50;
    /// <summary>Ask for confirmation every N requests. 0 = never ask.</summary>
    public int      ConfirmEvery  { get; set; } = 5;
}

/// <summary>Folder metadata from FolderSync (persisted for hierarchy resolution).</summary>
public class FolderInfo
{
    public string Name     { get; set; } = "";
    public string ParentId { get; set; } = "0";
    /// <summary>EAS folder type (1=User mail, 2=Inbox, 3=Drafts, etc.). 0 = unknown/non-email.</summary>
    public int    Type     { get; set; }

    public bool IsEmailFolder => Type is 1 or 2 or 3 or 4 or 5 or 6 or 12;
}

/// <summary>
/// Persisted to eas_sync_state.json – stores SyncKeys
/// so that each run only fetches new emails.
/// </summary>
public class SyncState
{
    public string?                         FolderSyncKey { get; set; }
    public Dictionary<string, string>      FolderKeys    { get; set; } = [];
    /// <summary>All folders (email + non-email) for hierarchy path resolution. Email folders have IsEmailFolder = true.</summary>
    public Dictionary<string, FolderInfo>  Folders       { get; set; } = [];
}
