# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test

```bash
dotnet build                    # build all projects
dotnet publish EasArchiver.Cli -c Release -r win-x64   # single-file CLI executable
dotnet publish EasArchiver.Gui -c Release -r win-x64   # GUI executable
dotnet run --project EasArchiver.Cli -- --test          # run built-in WBXML codec tests
```

There is no separate test project. WBXML tests are built into the CLI (`--test` flag) and defined in `EasArchiver.Core/WbxmlTests.cs`.

## Architecture

Three projects, one solution:

- **EasArchiver.Core** — shared library: EAS protocol client, WBXML codec, config/credential/update services, models
- **EasArchiver.Cli** — headless CLI entry point with interactive prompts
- **EasArchiver.Gui** — Avalonia desktop app (MVVM via CommunityToolkit.Mvvm)

All target `net10.0` with C# 14, nullable enabled.

### Core flow

`EasArchiver.ArchiveAsync()` drives the sync pipeline:
1. `SendDeviceInfoAsync()` — Settings command (device registration)
2. `FolderSyncAsync()` — fetch/update folder hierarchy, persist to `SyncState`
3. `SyncAllFoldersAsync()` — batch Sync across folders using SyncKeys for incremental fetches
4. `SaveEmailAsync()` — extract MIME body, apply header fixes, write `.eml`

### WBXML codec (`Wbxml.cs`)

Encodes/decodes Microsoft's binary XML format (MS-ASWBXML) used by EAS. 16 codepages mapping `(namespace, localName)` ↔ `(codepage, token)`. All EAS HTTP communication goes through `PostAsync()` which WBXML-encodes the request `XElement` and decodes the response.

### MIME header fixes (`EasArchiver.cs`)

Exchange EAS sometimes delivers raw UTF-8 in MIME headers instead of proper encoding. When `FixHeaders` is enabled (default):
- `FixMimeHeaders()` — RFC 2047 Q-encodes top-level headers (Subject, From, To, Cc, Bcc)
- `FixMimePartHeaders()` — RFC 2231 encodes attachment `name=`/`filename=` parameters; RFC 2047 encodes `Content-Description`; normalizes NFD→NFC

### State persistence

All persisted files live in `%LOCALAPPDATA%\EasArchiver\` (Windows) or `~/.eas-archiver/` (Linux/macOS):
- `config.json` — user settings (loaded/saved by `ConfigService`)
- `eas_sync_state.json` — folder tree + per-folder SyncKeys for incremental sync
- `credential.dat` — DPAPI-encrypted password (Windows only, via `CredentialService`)
- `device-id` — random GUID identifying this device to Exchange

### GUI specifics

Avalonia with Semi.Avalonia theme. `MainViewModel` exposes config properties, folder list, sync commands, log capture (via `DelegateSink` Serilog sink), and Velopack update state. Dialogs for password entry and rate-limit confirmation are callback-driven (`Func<Task<T>>`).

## Conventions

- Version is `0.0.0-local` in `Directory.Build.props`; CI overrides via `-p:Version=` from git tags
- EAS protocol version: 16.1
- HTTP rate limit: 200ms between requests
- Email files: `<ArchiveDir>/<FolderPath>/<Date>_<Subject>.eml`
- License: CC0 1.0 (public domain)
