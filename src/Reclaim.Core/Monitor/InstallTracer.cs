using System.Diagnostics;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Inventory;
using Reclaim.Core.SystemTools;

namespace Reclaim.Core.Monitor;

/// <summary>
/// "Install &amp; trace": records the system before an installer runs, watches files in real time,
/// and diffs the registry, services, tasks and firewall afterwards.
/// Flow: <see cref="StartAsync"/> → user installs → <see cref="FinishAsync"/>.
/// </summary>
public sealed class InstallTracer : IDisposable
{
    private RegistrySnapshot? _regBefore;
    private FileSnapshot? _filesBefore;
    private HashSet<string> _tasksBefore = [];
    private HashSet<string> _rulesBefore = [];
    private readonly EtwFileTracer _etw = new();
    private Process? _installer;
    private DateTime _started;
    private string _installerPath = "";

    public bool UsingRealTime => _etw.IsRunning;
    public bool InstallerRunning => _installer is { HasExited: false };
    public event EventHandler? InstallerExited;

    public async Task StartAsync(string installerPath, string? arguments, IProgress<string>? progress, CancellationToken ct)
    {
        _installerPath = installerPath;
        await Task.Run(() =>
        {
            progress?.Report("Recording the registry before installation…");
            _regBefore = RegistrySnapshot.Capture(progress: progress, ct: ct);
            _tasksBefore = ScheduledTasks.List().Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _rulesBefore = FirewallRules.List().Select(r => r.Name + "|" + r.ApplicationPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!_etw.TryStart(out var error))
            {
                progress?.Report($"Real-time file monitoring unavailable ({error}); recording files instead…");
                _filesBefore = FileSnapshot.Capture(progress: progress, ct: ct);
            }
        }, ct);

        _started = DateTime.Now;
        progress?.Report("Starting the installer…");
        var isMsi = installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        var psi = isMsi
            ? new ProcessStartInfo("msiexec.exe", $"/i \"{installerPath}\" {arguments}") { UseShellExecute = false }
            : new ProcessStartInfo(installerPath, arguments ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installerPath) };
        _installer = Process.Start(psi) ?? throw new InvalidOperationException("Installer did not start.");
        _etw.Track(_installer.Id);
        _installer.EnableRaisingEvents = true;
        _installer.Exited += (_, _) => InstallerExited?.Invoke(this, EventArgs.Empty);
        Log.Info($"Tracing install of {installerPath} (real-time: {UsingRealTime})");
    }

    public async Task<TraceLog> FinishAsync(IProgress<string>? progress, CancellationToken ct)
    {
        // Give file system and registry flushes a moment to land.
        await Task.Delay(1500, ct);
        _etw.Stop();

        return await Task.Run(() =>
        {
            var log = new TraceLog
            {
                Installer = _installerPath,
                Started = _started,
                Finished = DateTime.Now,
                Method = UsingRealTime ? "Real-time (ETW) + registry snapshot" : "Snapshot comparison",
            };

            progress?.Report("Comparing the registry…");
            var regAfter = RegistrySnapshot.Capture(progress: progress, ct: ct);
            var diff = regAfter.DiffFrom(_regBefore!);
            log.RegistryKeys = diff.CreatedKeys.Where(k => !IsNoise(k)).ToList();
            log.RegistryValues = diff.CreatedValues.Where(v => !IsNoise(v.Key)).Select(v => new TraceValue { Key = v.Key, Name = v.Value }).ToList();
            log.ChangedValues = diff.ChangedValues.Where(v => !IsNoise(v.Key)).Select(v => new TraceValue { Key = v.Key, Name = v.Value }).ToList();
            log.Services = log.RegistryKeys
                .Where(k => k.StartsWith(Services.ServicesKey + "\\", StringComparison.OrdinalIgnoreCase))
                .Select(k => k[(Services.ServicesKey.Length + 1)..]).Where(s => !s.Contains('\\')).ToList();

            progress?.Report("Comparing files…");
            if (_filesBefore is not null)
            {
                var after = FileSnapshot.Capture(progress: progress, ct: ct);
                foreach (var p in after.CreatedSince(_filesBefore))
                    (Directory.Exists(p) ? log.Directories : log.Files).Add(p);
            }
            else
            {
                CollapseEtwPaths(_etw.TouchedPaths, _started, log);
            }

            log.ScheduledTasks = ScheduledTasks.List().Select(t => t.Path).Where(p => !_tasksBefore.Contains(p)).ToList();
            log.FirewallRules = FirewallRules.List().Select(r => r.Name + "|" + r.ApplicationPath).Where(r => !_rulesBefore.Contains(r)).ToList();

            // Name the trace after the program the installer registered.
            foreach (var key in log.RegistryKeys.Where(k => k.Contains(@"\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase)))
            {
                using var k = RegistryUtil.Open(key);
                if (k is null) continue;
                var app = RegistryUninstallProvider.FromKey(k, key, key[(key.LastIndexOf('\\') + 1)..], Models.Bitness.Unknown, false);
                if (app is null) continue;
                log.LinkedUninstallKeys.Add(key);
                if (string.IsNullOrEmpty(log.Name))
                {
                    log.Name = app.Name;
                    log.Publisher = app.Publisher;
                    log.Version = app.Version;
                }
            }
            if (string.IsNullOrEmpty(log.Name)) log.Name = Path.GetFileNameWithoutExtension(_installerPath);

            TraceStore.Save(log);
            Log.Info($"Trace saved for {log.Name}: {log.TotalChanges} changes");
            return log;
        }, ct);
    }

    /// <summary>Keeps files created after tracing started; reduces them to top-most new folders.</summary>
    private static void CollapseEtwPaths(IEnumerable<string> touched, DateTime started, TraceLog log)
    {
        var threshold = started.AddSeconds(-2);
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in touched)
        {
            if (File.Exists(p) && File.GetCreationTime(p) >= threshold) created.Add(p);
        }
        // Promote to the highest ancestor folder that was itself created during the install.
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var looseFiles = new List<string>();
        foreach (var f in created)
        {
            string? top = null;
            var dir = Path.GetDirectoryName(f);
            while (dir is not null && dir.Length > 3 && Directory.Exists(dir) && Directory.GetCreationTime(dir) >= threshold)
            {
                top = dir;
                dir = Path.GetDirectoryName(dir);
            }
            if (top is not null) dirs.Add(top);
            else looseFiles.Add(f);
        }
        log.Directories = dirs.Where(d => !dirs.Any(o => !PathUtil.SamePath(o, d) && PathUtil.IsUnder(d, o))).OrderBy(d => d).ToList();
        log.Files = looseFiles.OrderBy(f => f).ToList();
    }

    private static readonly string[] NoiseFragments =
    [
        @"\Microsoft\Windows\CurrentVersion\Explorer\", @"\Microsoft\Windows\CurrentVersion\Diagnostics", @"\Microsoft\Windows NT\CurrentVersion\Notifications",
        @"\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", @"\Microsoft\Windows\CurrentVersion\CapabilityAccessManager",
        @"\Microsoft\Windows\CurrentVersion\DeviceAccess", @"\Microsoft\Windows\Windows Error Reporting", @"\Microsoft\WindowsUpdate",
        @"\Microsoft\Windows\CurrentVersion\WindowsUpdate", @"\Services\bam\", @"\Microsoft\Windows\CurrentVersion\Group Policy",
        @"\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant", @"\Microsoft\Windows\Shell\", @"\Microsoft\Windows\CurrentVersion\Search",
        @"\Microsoft\Windows\CurrentVersion\Holographic", @"\Microsoft\Windows\CurrentVersion\Privacy", @"\Microsoft\Windows\CurrentVersion\Store",
        @"\Microsoft\Windows\CurrentVersion\PushNotifications", @"\Microsoft\Windows\CurrentVersion\UFH", @"\Microsoft\Input\", @"\Microsoft\Office\",
        @"\Microsoft\Windows Defender", @"\Microsoft\Edge\", @"\Google\Chrome\", @"\Microsoft\OneDrive", @"\Microsoft\Windows\CurrentVersion\Authentication",
        @"\Microsoft\IdentityCRL", @"\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", @"\Microsoft\Windows\CurrentVersion\Lock Screen",
        @"\Services\SharedAccess\Parameters\FirewallPolicy",
    ];

    private static bool IsNoise(string key) => NoiseFragments.Any(n => key.Contains(n, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        _etw.Dispose();
        _installer?.Dispose();
    }
}
