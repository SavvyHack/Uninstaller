# Feature reference

Reclaim is a clean-room uninstaller. Nothing here comes from decompiling or disassembling any other product.
Revo Uninstaller's license forbids reverse engineering, so its binaries were **not** analyzed with REA or anything else.
Feature behavior was taken from public sources only: Revo's published user guide (shipped as a PDF with the free
edition), vendor feature pages for Revo Pro, Geek Uninstaller, IObit Uninstaller and Uninstalr, and the open-source
Bulk Crap Uninstaller project (Apache-2.0).

| Feature | Seen in | Reclaim implementation |
|---|---|---|
| Uninstall wizard: restore point → built-in uninstaller → leftover scan | Revo | `UninstallRunner`, wizard UI |
| Safe / Moderate / Advanced scan modes | Revo | `LeftoverScanner` (`ScanMode`) |
| Leftover confidence levels | BCU | `ConfidenceScorer`, Certain/Likely/Possible buckets |
| Aggressive reference tracing of leftovers | Uninstalr | `LeftoverScanner.ReferenceSweep`, COM/TypeLib/shell-extension scan |
| Exclusion list with `*`/`?` wildcards | Revo | `ExclusionRule` |
| "Mark as created" / manual include of a leftover | Revo | Leftover review lets any listed item be checked |
| Delete leftovers to Recycle Bin, plus restorable backups | Revo Pro | `BackupSession` (quarantine / recycle / permanent) |
| Full registry backup before uninstall | Revo Pro | `BackupManager.FullRegistryBackupAsync` |
| Forced uninstall for broken or already-removed programs | Revo Pro, Geek | `ForcedUninstall` |
| Forced MSI uninstall (/X instead of /I) | Revo | `SilentArgs.ForcedMsi` |
| Quick / multiple (batch) uninstall | Revo Pro, BCU, IObit | `BatchUninstallQueue` |
| Silent uninstall with no prompts | Uninstalr, BCU | `SilentArgs` per installer type |
| Resume a batch after reboot | Revo Pro | `BatchUninstallQueue` + RunOnce |
| Windows / Store apps, including all-users removal | Revo, Geek | `StoreAppProvider`, `StoreUninstaller` |
| Browser extension removal (+ policy-forced ones) | Revo Pro, IObit | `BrowserExtensionProvider`, `BrowserExtensionRemover` |
| Real-time installation monitoring / traced programs | Revo Pro | `InstallTracer` (kernel ETW + registry snapshot) |
| Shareable install logs | Revo Pro (logs database) | `TraceStore` import/export |
| Helper that catches uninstalls done elsewhere | Revo | `UninstallWatcher` |
| Hunter mode | Revo | `HunterService` |
| AutoRun manager | Revo | `AutorunManager` (Task Manager compatible) |
| Junk files cleaner with patterns + exclusions | Revo | `JunkFilesCleaner` |
| Windows / Browsers / MS Office history cleaners | Revo | `CleanerCatalog`, `TraceCleaner` |
| Evidence remover (free space wipe) | Revo | `SecureErase.WipeFreeSpaceAsync` |
| Unrecoverable delete (shredder) | Revo, IObit | `SecureErase.ShredAsync` |
| Windows tools launcher | Revo | `WindowsToolsCatalog` |
| Export list to HTML/TXT (+CSV/JSON) | Revo, Geek | `ExportService` |
| Open registry key / install folder / search online | Revo, Geek | detail pane actions |
| Portable, single exe | Geek, Uninstalr | `portable.flag`, single-file publish |
| Orphaned entries and leftover program folders | BCU, Geek | `OrphanDetector` |
| Signature (certificate) check | BCU | `SignatureVerifier` |
| Advanced filtering (regex, type chips) | BCU | Apps page filters |
| Scoop / Chocolatey / Steam apps | BCU | providers |
| Uninstall script export | BCU, Uninstalr | `ScriptExporter` |
| Software Relocator (move to another drive) | IObit | `SoftwareRelocator` (junction-based, reversible) |
| Software updater | IObit | `SoftwareUpdater` (winget) |
| Bundleware detection | IObit | `AppEnricher.FlagBundleware` |
| Windows updates (KB) removal | IObit | `WindowsUpdateProvider` |
| Drag-and-drop to uninstall, Explorer menu entry | IObit | Apps page drop target, `ShellIntegration` |
| Command line | BCU, Revo Pro | `reclaim` CLI |
