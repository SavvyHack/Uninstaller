using System.Diagnostics;
using System.Text;

namespace Reclaim.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public static class ProcessRunner
{
    /// <summary>Runs a hidden console process and captures its output.</summary>
    public static ProcessResult Run(string file, string args, TimeSpan timeout, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir ?? Environment.SystemDirectory,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), true);
            }
            p.WaitForExit();
            return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message, false);
        }
    }

    public static Task<ProcessResult> RunAsync(string file, string args, TimeSpan timeout, string? workingDir = null) =>
        Task.Run(() => Run(file, args, timeout, workingDir));

    public static void OpenShell(string target, string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open '{target}': {ex.Message}");
        }
    }

    public static void RevealInExplorer(string path)
    {
        if (Directory.Exists(path)) OpenShell("explorer.exe", $"\"{path}\"");
        else if (File.Exists(path)) OpenShell("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) OpenShell("explorer.exe", $"\"{dir}\"");
    }

    public static void SearchOnline(string query) =>
        OpenShell("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
}
