# Reclaim

A modern, all-in-one uninstaller for Windows 10/11. It removes programs, then finds and removes what they left behind,
with every removal backed up so it can be undone.

It brings together the best ideas from Revo Uninstaller Pro, Uninstalr, Geek Uninstaller, Bulk Crap Uninstaller and
IObit Uninstaller in a clean, Windows 11-style (Fluent / Mica) interface. See
[docs/research/feature-reference.md](docs/research/feature-reference.md) for where each feature comes from. Reclaim is
a clean-room implementation: no other product's code or binaries were used or reverse engineered.

## Features

- **Apps**: desktop programs, Windows/Store apps, browser extensions (Chrome, Edge, Brave, Vivaldi, Opera, Firefox,
  plus policy-forced ones), Scoop, Chocolatey, Steam and Windows updates in one searchable list. It has filter chips,
  regex search, badges (update available, bundleware, unsigned, broken, traced, moved) and a detail pane.
- **Uninstall wizard**: restore point → the program's own uninstaller (or quiet mode) → leftover scan
  (Safe / Moderate / Advanced) → review grouped by confidence (Certain / Likely / Possible) → done, with **Undo**.
- **Batch and quiet uninstall** with a single-MSI-at-a-time queue and resume after reboot. Also forced MSI uninstall
  and uninstall-script export (PowerShell/batch).
- **Deep leftover detection**: program and data folders, settings keys, App Paths, file associations, COM classes,
  type libraries, shell extensions, services, drivers, scheduled tasks, firewall rules, startup entries, PATH entries,
  usage traces and a registry-wide reference sweep. A protection layer never offers Windows, shared locations, or any
  other installed program's files and keys.
- **Force uninstall** for broken or already-removed programs, and detection of orphaned program folders.
- **Hunter mode**: drag a target onto any window, desktop icon or tray icon to uninstall it, stop it auto-starting,
  close it or open its folder. You can also drop a program file or shortcut onto the window.
- **Install monitor**: trace an installer in real time (kernel ETW plus registry snapshot diff), then remove exactly what
  it created. Traces can be exported and imported. Reclaim also notices programs uninstalled elsewhere and offers a
  leftover scan.
- **Cleanup**: junk files (patterns and exclusions), Windows history/temp/caches, browser caches and history, Office
  recent files, free-space wiping (Evidence Remover) and a file shredder.
- **Startup manager** that stays in sync with Task Manager. **Software Relocator** moves programs to another drive
  behind a junction and is reversible. **Updater** uses winget. **Windows tools** launcher.
- **Backups**: undo history, System Restore points and full registry backup.
- **Portable**: a single exe. Put a `portable.flag` file next to it to keep settings and backups beside the exe.
  It also has a command line (`reclaim-cli`) and an optional Explorer "Uninstall with Reclaim" menu entry.

## Build and run

Requires the .NET 10 SDK on Windows.

```bash
dotnet build Reclaim.slnx
dotnet test tests/Reclaim.Core.Tests
```

Reclaim asks for administrator rights, which uninstalling machine-wide software needs. To browse and work on the UI
without elevation, build with the preview manifest:

```bash
dotnet build src/Reclaim.App -p:Preview=true
```

`--page Apps` opens a page directly. `--uninstall-target <path>` opens the uninstall wizard for a file or folder.

### Portable single-file build

```bash
dotnet publish src/Reclaim.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeAllContentForSelfExtract=true -o publish/portable
```

## Command line

```text
reclaim-cli list [--search TEXT] [--source win32|store|extension|orphan] [--json]
reclaim-cli info NAME
reclaim-cli uninstall NAME... [--quiet] [--scan safe|moderate|advanced] [--clean certain|likely|none] [--yes]
reclaim-cli scan NAME|PATH [--scan MODE] [--json] [--clean certain|likely] [--yes]
reclaim-cli export html|csv|json|text FILE
reclaim-cli script ps1|bat FILE NAME...
reclaim-cli trace INSTALLER [-- ARGS]
reclaim-cli backups
reclaim-cli restore BACKUP_FOLDER
```

## End-to-end test

`tests/fixtures` plants a realistic per-user program (no admin needed). Its "sloppy" uninstaller leaves eleven kinds of
leftovers behind, next to a decoy app from the same publisher that must never be touched:

```bash
powershell -ExecutionPolicy Bypass -File tests/fixtures/Install-FakeApp.ps1
reclaim-cli uninstall "Contoso Widget Studio" --quiet --scan advanced --clean certain --yes
powershell -ExecutionPolicy Bypass -File tests/fixtures/Test-FakeAppState.ps1 -Expect Removed
reclaim-cli restore <backup folder printed above>
powershell -ExecutionPolicy Bypass -File tests/fixtures/Test-FakeAppState.ps1 -Expect Present
powershell -ExecutionPolicy Bypass -File tests/fixtures/Remove-FakeApp.ps1
```

## Layout

| Path | What |
|---|---|
| `src/Reclaim.Core` | All logic: inventory, uninstall engine, leftover scanner, backups, monitor, cleaners, system tools |
| `src/Reclaim.App` | WPF + WPF-UI app (MVVM with CommunityToolkit.Mvvm) |
| `src/Reclaim.Cli` | Command-line front end |
| `tests/Reclaim.Core.Tests` | xUnit tests (logic + HKCU/file-system integration) |
| `tests/fixtures` | End-to-end fake app scripts |
