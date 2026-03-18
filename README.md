# EAS Email Archiver

Lädt E-Mails von einem Exchange ActiveSync (EAS) Server lokal als `.eml`-Dateien herunter.
Inkrementell – holt bei jedem Lauf nur neue E-Mails.

## Voraussetzungen

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (nur zum Kompilieren)

## Konfiguration

Alle Einstellungen in `appsettings.json`:

```json
{
  "Eas": {
    "ServerUrl":        "https://eas.example.com/Microsoft-Server-ActiveSync",
    "Domain":           "",
    "Username":         "vorname.nachname",
    "Password":         "meinPasswort",
    "ArchiveDirectory": "email_archiv",
    "WindowSize":       50
  }
}
```

- **Domain** kann leer bleiben, wenn der Server nur `username` (ohne `domain\`) erwartet
- **WindowSize**: Anzahl E-Mails pro Sync-Request (50 ist ein guter Wert)
- Felder können auch leer bleiben → werden beim Start interaktiv abgefragt

Alternativ per Umgebungsvariable (überschreibt appsettings.json):
```
EAS__ServerUrl=https://...
EAS__Username=vorname.nachname
EAS__Password=geheim
```

Oder per Kommandozeile:
```
EasArchiver.exe --Eas:Username=vorname.nachname --Eas:Password=geheim
```

## Kompilieren

```cmd
dotnet build
```

Als **einzelne .exe** (empfohlen – keine .NET-Installation auf Zielmaschine nötig):
```cmd
dotnet publish -c Release
```

Ausgabe: `bin\Release\net10.0\win-x64\publish\EasArchiver.exe`

## Starten

```cmd
EasArchiver.exe
```

## Quarantäne (HTTP 449)

Beim ersten Start auf einem neuen Server muss das Gerät freigeschaltet werden.
Das Programm zeigt dann:

```
⚠  Gerät noch nicht freigegeben (HTTP 449 – Quarantäne).
   Geräte-ID zur Freischaltung: a1b2c3d4e5f6...
   Nach der Freischaltung durch den Admin bitte erneut starten.
```

Die angezeigte Geräte-ID dem Admin mitteilen – er findet das Gerät in der
Exchange-Verwaltungskonsole unter **Mobile Geräte** und kann es dort freischalten.

Die Geräte-ID ist stabil und basiert auf dem Computernamen.

## Automatisch per Task Scheduler

1. `Win+S` → "Aufgabenplanung"
2. "Einfache Aufgabe erstellen"
3. Trigger: Täglich, z.B. 08:00 Uhr
4. Aktion → Programm starten:
   - Programm/Skript: `C:\Tools\EasArchiver\EasArchiver.exe`
   - Starten in:      `C:\Tools\EasArchiver\`

## Dateistruktur

```
EasArchiver.exe
appsettings.json        ← Konfiguration (Passwort hier oder per Env-Var)
eas_sync_state.json     ← Sync-Status (automatisch erstellt)
email_archiv\
  Posteingang\
    2024-03-15_Betreff_a1b2c3d4.eml
  Gesendete Elemente\
    ...
```

## Format

`.eml` (RFC 2822) öffnet sich per Doppelklick in Outlook.
