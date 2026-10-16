# Build status

## Done (committed on `feature/reclaim`)
- Solution scaffold: `Reclaim.slnx` with Core, App (WPF + WPF-UI 4.3), Cli and Tests projects. All target .NET 10 and win-x64.
- **Reclaim.Core** builds with 0 errors. It covers:
  - inventory (Win32/registry, Store, browser extensions, Scoop, Chocolatey, Steam, Windows updates, orphans, bundleware, signatures)
  - uninstall engine (type detection, silent switches, job-object runner, batch queue with reboot resume, forced and Store uninstall, script export)
  - leftover scanner (Safe/Moderate/Advanced, confidence scoring, protection guard, COM, shell and driver scans, deep reference sweep)
  - backup sessions, restore points and full registry backup
  - install tracer (ETW + registry snapshot), trace store and external-uninstall watcher
  - cleaners (junk, Windows, browsers, Office), shredder and free-space wipe
  - autorun manager, Software Relocator, winget updater, Windows tools, export, Explorer menu entry, Hunter service
- `docs/research/feature-reference.md` maps each feature to the tool it comes from (clean-room, built from public docs only).

## Remaining
1. **App UI** (`src/Reclaim.App`): app.manifest (requireAdministrator), icon, App.xaml with WPF-UI dictionaries and DI host, then:
   - FluentWindow with Mica and a NavigationView: Home, Apps, Hunter, Force uninstall, Install monitor, Cleanup, Startup, Tools, Backups, Settings
   - the uninstall side sheet, a 5-step stepper with the leftover TreeView grouped by confidence
   - the Hunter floating target window
   - Apps page: virtualized list, filter chips, detail pane, floating batch bar
2. **CLI** (`src/Reclaim.Cli`): `list`, `uninstall`, `export`, `trace`.
3. **Tests**: ConfidenceScorer, ProtectionGuard, SilentArgs/type detection, RegistrySnapshot diff, ScriptExporter, and Relocator in a temp dir.
4. **Fixture**: `tests/fixtures/Install-FakeApp.ps1` for an end-to-end leftover test.
5. **Publish**: single-file portable exe (`IncludeNativeLibrariesForSelfExtract` for TraceEvent).

None of Core has been run against the real system yet. It has only been compiled.
