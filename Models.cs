using System.Collections.Generic;

namespace EasArchiver;

/// <summary>
/// Loaded from appsettings.json (section "Eas").
/// Missing values are prompted interactively at startup.
/// </summary>
public class EasConfig
{
    public string ServerUrl       { get; set; } = "";
    public string Domain          { get; set; } = "";
    public string Username        { get; set; } = "";
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
}

/// <summary>Folder metadata from FolderSync (persisted for hierarchy resolution).</summary>
public class FolderInfo
{
    public string Name     { get; set; } = "";
    public string ParentId { get; set; } = "0";
}

/// <summary>
/// Persisted to eas_sync_state.json – stores SyncKeys
/// so that each run only fetches new emails.
/// </summary>
public class SyncState
{
    public string?                         FolderSyncKey { get; set; }
    public Dictionary<string, string>      FolderKeys    { get; set; } = [];
    /// <summary>Email folder id → info, persisted across runs.</summary>
    public Dictionary<string, FolderInfo>  KnownFolders  { get; set; } = [];
    /// <summary>All folder id → info (including non-email), for hierarchy path resolution.</summary>
    public Dictionary<string, FolderInfo>  FolderTree    { get; set; } = [];
}
