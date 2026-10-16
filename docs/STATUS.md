# Build status

## Done
- **Core engine** (`src/Reclaim.Core`): every planned feature (see README).
- **App UI** (`src/Reclaim.App`): every page plus the uninstall sheet, Hunter window, drag-and-drop, light and dark
  themes, and the external-uninstall notification.
- **CLI** (`src/Reclaim.Cli`, `reclaim-cli.exe`).
- **Tests**: 80 xUnit tests passing (scoring, protection, installer detection and silent switches, script export,
  winget parsing, registry snapshot diff, relocation with a junction and back, cleanup + restore, shredder).
- **End-to-end** with `tests/fixtures`, run both through the CLI and through the real UI (driven by UI Automation):
  - every planted leftover was found and removed
  - the decoy from the same publisher was untouched
  - Undo/restore brought everything back
- **Portable single-file publish** verified to start.

## Verified only without administrator rights
- The UI was exercised with the preview (asInvoker) build. Per-user programs, HKCU and the user's files were tested
  for real.
- Not yet exercised on this machine: machine-wide (HKLM / Program Files) uninstalls, MSI uninstalls, restore-point
  creation, kernel ETW install tracing, services and firewall-rule removal, and the Relocator across two physical
  drives. All of these need an elevated run.

## Known limitations
- Signature check uses embedded Authenticode only, so catalog-signed Windows files can show as "Unsigned".
- Store app removal for all users goes through PowerShell's Appx module.
- Shredding and free-space wiping can't guarantee erasure on SSDs (the UI warns about this).
