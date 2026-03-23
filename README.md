# EAS Email Archiver

Downloads emails from an Exchange ActiveSync (EAS) server as local `.eml` files.
Incremental – only fetches new emails on each run.

## Prerequisites

- Windows 10 / 11 (should also run on Linux / macOS)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (build only)

## Configuration

All settings in `appsettings.json`:

```json
{
  "Eas": {
    "ServerUrl":        "https://eas.example.com/Microsoft-Server-ActiveSync",
    "Domain":           "",
    "Username":         "john.doe",
    "Password":         "secret",
    "ArchiveDirectory": "mail_archive",
    "WindowSize":       50,
    "Include":          [],
    "Exclude":          []
  }
}
```

- **Domain** – leave empty if the server expects just `username` (without `domain\`)
- **WindowSize** – number of emails per sync request (50 is a good default)
- **Include** – only sync folders matching these names (empty = all). Also matches subfolders.
- **Exclude** – skip folders matching these names. Applied after Include. Also matches subfolders.
- Missing fields are prompted interactively at startup

Override via environment variables:
```
EAS__ServerUrl=https://...
EAS__Username=john.doe
EAS__Password=secret
```

Or via command line:
```
EasArchiver --Eas:Username=john.doe --Eas:Password=secret
```

### Folder filtering

Include only specific folders (whitelist):
```json
{
  "Eas": {
    "Include": ["Inbox", "Sent Items"]
  }
}
```

Exclude specific folders (blacklist):
```json
{
  "Eas": {
    "Exclude": ["Inbox/Spam", "Deleted Items"]
  }
}
```

Via CLI:
```
EasArchiver --include=Inbox --include="Sent Items" --exclude=Inbox/Spam
```

Rules:
- `Include` empty → all email folders are synced
- `Include` set → only matching folders and their subfolders
- `Exclude` → matching folders and their subfolders are skipped
- Exclude is applied after Include
- Matching is case-insensitive
- Subfolders are specified with `/`, e.g. `Inbox/Projects/2024`

## Building

```
dotnet build
```

As a **single-file executable** (recommended – no .NET installation required on target machine):
```
dotnet publish -c Release
```

Output: `bin/Release/net10.0/win-x64/publish/EasArchiver`

## Usage

```
EasArchiver                     # run archiver
EasArchiver --version           # print version and exit
EasArchiver --reset             # clear sync state and do a full re-sync
EasArchiver -v                  # verbosity level 1 (up to -vvv)
EasArchiver --debug-blobs       # save sync responses as hex files
EasArchiver --test              # run built-in WBXML codec tests
EasArchiver --decode file.hex   # decode a WBXML hex dump to XML (writes file.xml)
```

## Device ID

A random device ID is generated on first run and persisted in the app data directory
(`%LOCALAPPDATA%\EasArchiver\device-id` on Windows, `~/.eas-archiver/device-id` on Linux/macOS).
This ID identifies the device to the Exchange server. Delete the file to generate a new one.

## Scheduled runs (Task Scheduler)

1. `Win+S` → "Task Scheduler"
2. "Create Basic Task"
3. Trigger: Daily, e.g. 08:00
4. Action → Start a program:
   - Program/script: `C:\Tools\EasArchiver\EasArchiver.exe`
   - Start in:       `C:\Tools\EasArchiver\`

## File structure

```
EasArchiver
appsettings.json                ← configuration (password here or via env var)
eas_sync_state.json             ← sync state (created automatically)
mail_archive/
  Inbox/
    2024-03-15_143045_Subject_a1b2c3d4.eml
    Projects/                   ← subfolders follow server hierarchy
      2024-06-01_091500_Update_b2c3d4e5.eml
  Sent Items/
    ...
```

## Format

`.eml` (RFC 2822) – opens with a double-click in Outlook or Thunderbird.
