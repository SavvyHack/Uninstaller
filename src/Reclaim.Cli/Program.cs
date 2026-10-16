using System.Security.Principal;
using System.Text.Json;
using Reclaim.Core.Backup;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Inventory;
using Reclaim.Core.Leftovers;
using Reclaim.Core.Models;
using Reclaim.Core.Monitor;
using Reclaim.Core.SystemTools;
using Reclaim.Core.Uninstall;

namespace Reclaim.Cli;

internal static class Program
{
    private const string Usage = """
        Reclaim command line

          reclaim list [--search TEXT] [--source win32|store|extension|orphan|...] [--json]
          reclaim info NAME
          reclaim uninstall NAME... [--quiet] [--scan safe|moderate|advanced] [--clean certain|likely|none]
                                    [--no-restore-point] [--yes]
          reclaim scan NAME|PATH [--scan safe|moderate|advanced] [--json] [--clean certain|likely|none] [--yes]
          reclaim export html|csv|json|text FILE
          reclaim script ps1|bat FILE NAME...
          reclaim trace INSTALLER [-- ARGS]
          reclaim backups
          reclaim restore BACKUP_FOLDER

        NAME matches a program's exact display name, or a unique part of it.
        Exit codes: 0 success, 1 failure, 2 usage error, 3 not found or ambiguous.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list" => await ListAsync(args),
                "info" => await InfoAsync(args),
                "uninstall" => await UninstallAsync(args),
                "scan" => await ScanAsync(args),
                "export" => await ExportAsync(args),
                "script" => await ScriptAsync(args),
                "trace" => await TraceAsync(args),
                "backups" => Backups(),
                "restore" => Restore(args),
                _ => Fail($"Unknown command '{args[0]}'.", 2),
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, 1);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string? Opt(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Flag(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Positional arguments after the command, skipping options and their values.</summary>
    private static List<string> Positional(string[] args)
    {
        var withValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--search", "--source", "--scan", "--clean" };
        var list = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--") break;
            if (args[i].StartsWith("--"))
            {
                if (withValue.Contains(args[i])) i++;
                continue;
            }
            list.Add(args[i]);
        }
        return list;
    }

    private static int Fail(string message, int code)
    {
        Console.Error.WriteLine(message);
        return code;
    }

    private static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static Task<List<InstalledApp>> LoadAsync() => new AppCatalog().LoadAsync(new CatalogOptions
    {
        IncludeSystemComponents = false,
        IncludeWindowsUpdates = false,
    });

    private static InstalledApp? Resolve(List<InstalledApp> apps, string name, out string? error)
    {
        error = null;
        var exact = apps.Where(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || a.Id.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var partial = exact.Count > 1 ? exact : apps.Where(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];
        error = partial.Count == 0
            ? $"No program matches '{name}'."
            : $"'{name}' matches {partial.Count} programs:\n  " + string.Join("\n  ", partial.Take(15).Select(a => $"{a.Name} ({a.Source})"));
        return null;
    }

    private static ScanMode ParseMode(string[] args) =>
        Enum.TryParse<ScanMode>(Opt(args, "--scan") ?? "moderate", true, out var m) ? m : ScanMode.Moderate;

    private static ConfidenceBucket? ParseClean(string[] args) => (Opt(args, "--clean") ?? "none").ToLowerInvariant() switch
    {
        "certain" => ConfidenceBucket.Certain,
        "likely" => ConfidenceBucket.Likely,
        _ => null,
    };

    // ------------------------------------------------------------------ commands

    private static async Task<int> ListAsync(string[] args)
    {
        var apps = await LoadAsync();
        var search = Opt(args, "--search");
        var source = Opt(args, "--source");
        var filtered = apps
            .Where(a => search is null || a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || (a.Publisher?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(a => source is null || a.Source.ToString().StartsWith(source, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (Flag(args, "--json"))
        {
            Console.WriteLine(ExportService.ExportApps(filtered, ExportFormat.Json));
            return 0;
        }
        foreach (var a in filtered)
            Console.WriteLine($"{Trunc(a.Name, 48),-48}  {Trunc(a.Version ?? "", 16),-16}  {a.Source,-16}  {Trunc(a.Publisher ?? "", 30)}");
        Console.WriteLine($"\n{filtered.Count} item(s)");
        return 0;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static async Task<int> InfoAsync(string[] args)
    {
        var names = Positional(args);
        if (names.Count != 1) return Fail("Usage: reclaim info NAME", 2);
        var apps = await LoadAsync();
        var app = Resolve(apps, names[0], out var err);
        if (app is null) return Fail(err!, 3);
        AppEnricher.Enrich(app, CancellationToken.None);
        var type = UninstallerTypeDetector.Detect(app);
        Console.WriteLine($"""
            Name:          {app.Name}
            Publisher:     {app.Publisher}
            Version:       {app.Version}
            Source:        {app.Source}
            Installed:     {app.InstallDate:yyyy-MM-dd}
            Size:          {app.SizeText}
            Location:      {app.InstallLocation}
            Registry key:  {app.RegistryKeyPath}
            Uninstaller:   {type}
            Uninstall:     {app.UninstallString}
            Quiet command: {SilentArgs.Build(app) ?? "(none known)"}
            Signature:     {app.Signature?.Status} {app.Signature?.Signer}
            Flags:         {app.Flags}
            """);
        return 0;
    }

    private static async Task<int> UninstallAsync(string[] args)
    {
        var names = Positional(args);
        if (names.Count == 0) return Fail("Usage: reclaim uninstall NAME... [options]", 2);
        if (!IsElevated) Console.Error.WriteLine("Note: not running as administrator. Machine-wide programs may fail to uninstall.");
        var apps = await LoadAsync();
        var targets = new List<InstalledApp>();
        foreach (var n in names)
        {
            var a = Resolve(apps, n, out var err);
            if (a is null) return Fail(err!, 3);
            targets.Add(a);
        }

        var quiet = Flag(args, "--quiet");
        var mode = ParseMode(args);
        var clean = ParseClean(args);
        var fingerprints = targets.ToDictionary(t => t.Id, AppFingerprint.FromApp);

        if (!Flag(args, "--no-restore-point") && SettingsStore.Current.CreateRestorePoint && IsElevated)
        {
            var (_, msg) = await BackupManager.CreateRestorePointAsync("Reclaim CLI uninstall");
            Console.WriteLine(msg);
        }

        var runner = new UninstallRunner();
        var exit = 0;
        foreach (var app in targets)
        {
            Console.WriteLine($"\n== {app.Name} ==");
            var outcome = await runner.RunAsync(new UninstallRequest
            {
                App = app,
                Quiet = quiet,
                Timeout = TimeSpan.FromMinutes(SettingsStore.Current.UninstallTimeoutMinutes),
            }, new Progress<string>(Console.WriteLine), CancellationToken.None);
            Console.WriteLine($"Result: {outcome.Status}: {outcome.Message}");
            if (!outcome.CanScanLeftovers)
            {
                exit = 1;
                continue;
            }
            var others = apps.Where(a => targets.All(t => t.Id != a.Id));
            var scanner = new LeftoverScanner(fingerprints[app.Id], new ProtectionGuard(others));
            var leftovers = await scanner.ScanAsync(mode, null, CancellationToken.None);
            exit |= await ReportAndClean(app.Name, leftovers, clean, Flag(args, "--yes"), Flag(args, "--json"));
            if (!outcome.IsSuccess) exit = 1;
        }
        return exit;
    }

    private static async Task<int> ScanAsync(string[] args)
    {
        var names = Positional(args);
        if (names.Count != 1) return Fail("Usage: reclaim scan NAME|PATH [--scan MODE] [--json] [--clean certain|likely] [--yes]", 2);
        var apps = await LoadAsync();
        var target = File.Exists(names[0]) || Directory.Exists(names[0])
            ? ForcedUninstall.Create(names[0], apps)
            : Resolve(apps, names[0], out _) ?? ForcedUninstall.Create(names[0], apps);
        var scanner = new LeftoverScanner(AppFingerprint.FromApp(target), new ProtectionGuard(apps.Where(a => a.Id != target.Id)));
        var leftovers = await scanner.ScanAsync(Flag(args, "--scan") ? ParseMode(args) : ScanMode.Advanced, null, CancellationToken.None);
        return await ReportAndClean(target.Name, leftovers, ParseClean(args), Flag(args, "--yes"), Flag(args, "--json"));
    }

    private static async Task<int> ReportAndClean(string name, List<LeftoverItem> leftovers, ConfidenceBucket? clean, bool yes, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(leftovers.Select(l => new
            {
                l.Confidence, Bucket = l.Bucket.ToString(), Kind = l.Kind.ToString(), l.Path, l.ValueName, l.Category, l.Reason, l.SizeBytes,
            }), new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"\nLeftovers of {name}: {leftovers.Count}");
            foreach (var l in leftovers.OrderBy(l => l.Bucket).ThenByDescending(l => l.Confidence))
                Console.WriteLine($"  [{l.Confidence,3} {l.Bucket,-8}] {l.Kind,-14} {l.Display}{(l.SizeBytes > 0 ? "  (" + l.SizeText + ")" : "")}\n{"",28}{l.Reason}");
        }
        if (clean is null || leftovers.Count == 0) return 0;

        var selected = leftovers.Where(l => l.Bucket <= clean.Value).ToList();
        if (selected.Count == 0) return 0;
        if (!yes)
        {
            Console.Write($"\nRemove {selected.Count} item(s) ({clean} and above)? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return 0;
        }
        var result = await LeftoverCleaner.CleanAsync(selected, $"CLI cleanup {name}", null, CancellationToken.None);
        Console.WriteLine($"Removed {result.Removed}, failed {result.Failed}, freed {Format.Bytes(result.BytesFreed)}. Backup: {result.BackupFolder}");
        foreach (var e in result.Errors) Console.WriteLine("  ! " + e);
        return result.Failed == 0 ? 0 : 1;
    }

    private static async Task<int> ExportAsync(string[] args)
    {
        var p = Positional(args);
        if (p.Count != 2 || !Enum.TryParse<ExportFormat>(p[0], true, out var fmt)) return Fail("Usage: reclaim export html|csv|json|text FILE", 2);
        var apps = await LoadAsync();
        await File.WriteAllTextAsync(p[1], ExportService.ExportApps(apps.Where(a => a.Source != AppSource.Orphan), fmt));
        Console.WriteLine($"Wrote {p[1]}");
        return 0;
    }

    private static async Task<int> ScriptAsync(string[] args)
    {
        var p = Positional(args);
        if (p.Count < 3) return Fail("Usage: reclaim script ps1|bat FILE NAME...", 2);
        var apps = await LoadAsync();
        var targets = new List<InstalledApp>();
        foreach (var n in p.Skip(2))
        {
            var a = Resolve(apps, n, out var err);
            if (a is null) return Fail(err!, 3);
            targets.Add(a);
        }
        var kind = p[0].Equals("bat", StringComparison.OrdinalIgnoreCase) ? ScriptKind.Batch : ScriptKind.PowerShell;
        await File.WriteAllTextAsync(p[1], ScriptExporter.Generate(targets, kind));
        Console.WriteLine($"Wrote {p[1]}");
        return 0;
    }

    private static async Task<int> TraceAsync(string[] args)
    {
        var p = Positional(args);
        if (p.Count < 1) return Fail("Usage: reclaim trace INSTALLER [-- ARGS]", 2);
        var dashes = Array.IndexOf(args, "--");
        var installerArgs = dashes >= 0 ? string.Join(" ", args.Skip(dashes + 1)) : null;
        using var tracer = new InstallTracer();
        var done = new TaskCompletionSource();
        tracer.InstallerExited += (_, _) => done.TrySetResult();
        await tracer.StartAsync(Path.GetFullPath(p[0]), installerArgs, new Progress<string>(Console.WriteLine), CancellationToken.None);
        Console.WriteLine(tracer.UsingRealTime ? "Real-time monitoring active." : "Using snapshot comparison.");
        Console.WriteLine("Waiting for the installer to exit…");
        await done.Task;
        var log = await tracer.FinishAsync(new Progress<string>(Console.WriteLine), CancellationToken.None);
        Console.WriteLine($"""

            Trace saved: {log.Name}
              folders: {log.Directories.Count}, files: {log.Files.Count}
              registry keys: {log.RegistryKeys.Count}, values: {log.RegistryValues.Count}
              services: {log.Services.Count}, tasks: {log.ScheduledTasks.Count}, firewall rules: {log.FirewallRules.Count}
            """);
        return 0;
    }

    private static int Backups()
    {
        foreach (var b in BackupManager.List())
            Console.WriteLine($"{b.Created:yyyy-MM-dd HH:mm}  {b.Items,4} items  {Format.Bytes(b.SizeBytes),9}  {b.Title}{(b.Restored ? "  (restored)" : "")}\n{"",18}{b.Folder}");
        return 0;
    }

    private static int Restore(string[] args)
    {
        var p = Positional(args);
        if (p.Count != 1) return Fail("Usage: reclaim restore BACKUP_FOLDER", 2);
        var session = BackupSession.Open(p[0]) ?? throw new DirectoryNotFoundException("Not a Reclaim backup folder.");
        var (ok, failed) = session.Restore(new Progress<string>(Console.WriteLine));
        Console.WriteLine($"Restored {ok}, failed {failed}.");
        return failed == 0 ? 0 : 1;
    }
}
