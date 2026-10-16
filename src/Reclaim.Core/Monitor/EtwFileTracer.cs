using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Monitor;

/// <summary>
/// Real-time file capture through a kernel ETW session, limited to the installer's process tree
/// (plus msiexec, whose service process is not a child of the installer). No driver needed.
/// </summary>
public sealed class EtwFileTracer : IDisposable
{
    private TraceEventSession? _session;
    private Thread? _thread;
    private readonly ConcurrentDictionary<int, byte> _tracked = new();
    private readonly ConcurrentDictionary<string, byte> _paths = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning => _session is not null;

    public IReadOnlyCollection<string> TouchedPaths => (IReadOnlyCollection<string>)_paths.Keys;

    public int EventCount { get; private set; }

    public bool TryStart(out string? error)
    {
        error = null;
        if (TraceEventSession.IsElevated() != true)
        {
            error = "Real-time monitoring needs administrator rights.";
            return false;
        }
        try
        {
            _session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Process |
                KernelTraceEventParser.Keywords.FileIOInit |
                KernelTraceEventParser.Keywords.FileIO);

            var kernel = _session.Source.Kernel;
            kernel.ProcessStart += d =>
            {
                var image = d.ImageFileName ?? "";
                if (_tracked.ContainsKey(d.ParentID) || image.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                    _tracked[d.ProcessID] = 0;
            };
            kernel.FileIOCreate += d => Record(d.ProcessID, d.FileName);
            kernel.FileIOWrite += d => Record(d.ProcessID, d.FileName);
            kernel.FileIORename += d => Record(d.ProcessID, d.FileName);

            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Log.Warn($"ETW processing stopped: {ex.Message}"); }
            }) { IsBackground = true, Name = "Reclaim ETW" };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Dispose();
            return false;
        }
    }

    public void Track(int pid) => _tracked[pid] = 0;

    private void Record(int pid, string? file)
    {
        EventCount++;
        if (string.IsNullOrEmpty(file) || !_tracked.ContainsKey(pid)) return;
        if (file.Length < 4 || file[1] != ':') return;
        if (file.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
        if (file.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase) || file.StartsWith(@"C:\$", StringComparison.Ordinal)) return;
        _paths[file] = 0;
    }

    public void Stop()
    {
        try { _session?.Stop(); } catch { }
        _thread?.Join(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        Stop();
        _session?.Dispose();
        _session = null;
    }
}
