# EAS Email Archiver

Downloads emails from an Exchange ActiveSync (EAS) server as local `.eml` files.
Incremental – only fetches new emails on each run.

Two frontends are available:
- **CLI** (`EasArchiver.exe`) – headless, ideal for scheduled tasks and scripting
- **GUI** (`EasArchiver.Gui.exe`) – Avalonia desktop app with a visual config editor, folder browser, live log output, and built-in auto-update support

## Prerequisites

- Windows 10 / 11 (should also run on Linux / macOS)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (build only)

## Configuration

Settings are loaded from this file:

`config.json` – user settings (`%LOCALAPPDATA%\EasArchiver\` on Windows, `~/.eas-archiver/` on Linux/macOS)

The GUI provides a built-in editor for all settings — no manual editing of `config.json` required.

Example `config.json`:

```json
{
  "Eas": {
    "ServerUrl":        "https://eas.example.com/Microsoft-Server-ActiveSync",
    "Domain":           "",
    "Username":         "john.doe",
    "ArchiveDirectory": "mail_archive",
    "WindowSize":       50,
    "FixHeaders":       true,
    "Include":          [],
    "Exclude":          []
  }
}
```

- **ArchiveDirectory** – relative (to the working directory) or absolute path. An absolute path like `D:\MailArchive` is useful for storing emails on a separate drive.
- **Domain** – leave empty if the server expects just `username` (without `domain\`)
- **WindowSize** – number of emails per sync request (50 is a good default)
- **FixHeaders** – EAS sometimes delivers raw UTF-8 in MIME headers (Subject, From, To, etc.) instead of RFC 2047 encoded values. When enabled (default), these are automatically fixed with `=?UTF-8?Q?...?=` encoding before saving. Set to `false` to save headers as-is.
- **Include** – only sync folders matching these names (empty = all). Also matches subfolders.
- **Exclude** – skip folders matching these names. Applied after Include. Also matches subfolders.
- Missing fields are prompted interactively at startup

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

CLI:
```
dotnet publish EasArchiver.Cli -c Release -r win-x64      # Windows
dotnet publish EasArchiver.Cli -c Release -r osx-arm64    # macOS (Apple Silicon)
dotnet publish EasArchiver.Cli -c Release -r linux-x64    # Linux
```
Output: `EasArchiver.Cli/bin/Release/net10.0/<rid>/publish/EasArchiver.exe`

GUI (Windows only):
```
dotnet publish EasArchiver.Gui -c Release -r win-x64
```
Output: `EasArchiver.Gui/bin/Release/net10.0/win-x64/publish/EasArchiver.Gui.exe`

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

## Updates

The GUI checks for updates automatically on startup using [Velopack](https://velopack.io). When a new version is available, a notification appears and the update can be applied with one click.

The CLI supports the `--auto-update` flag to check and apply updates non-interactively.

## Credentials

On Windows, the password can be saved to disk via the GUI or by entering it interactively on first run. It is stored in:

`%LOCALAPPDATA%\EasArchiver\credential.dat`

The file is encrypted with the [Windows Data Protection API (DPAPI)](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection) in `CurrentUser` scope. This means:

- The ciphertext is tied to your Windows user account and the local machine. No other user account — not even an administrator — can decrypt it without knowing your Windows login password.
- The file is useless on another machine or after a user profile migration; you will be prompted for the password again.
- DPAPI does **not** protect against malware running as your own user, or against memory-scraping attacks while the application is running.

On Linux and macOS the file is never created. Store the password in plaintext in `config.json` instead.

To clear the saved password, delete `credential.dat` or use the GUI settings screen.

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
mail_archive/
  Inbox/
    2024-03-15_143045_Subject_a1b2c3d4.eml
    Projects/                   ← subfolders follow server hierarchy
      2024-06-01_091500_Update_b2c3d4e5.eml
  Sent Items/
    ...
<localappdata>/config.json                ← configuration (password here or via env var)
<localappdata>/credential.dat             ← password encrypted with DPAPI (Windows only)
<localappdata>/eas_sync_state.json        ← sync state (created automatically)
```

## Format

`.eml` (RFC 2822) – opens with a double-click in Outlook or Thunderbird.

## AI

Thanks to AI for saving me hundreds of hours.
