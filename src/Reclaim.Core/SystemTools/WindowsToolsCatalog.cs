using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.SystemTools;

public sealed record WindowsTool(string Name, string Description, string Glyph, string Command, string? Arguments = null, bool Console = false)
{
    public void Launch()
    {
        if (Console) ProcessRunner.OpenShell("cmd.exe", $"/k {Command} {Arguments}");
        else ProcessRunner.OpenShell(Command, Arguments);
    }
}

/// <summary>One-click access to useful built-in Windows tools.</summary>
public static class WindowsToolsCatalog
{
    public static IReadOnlyList<WindowsTool> All { get; } =
    [
        new("System Restore", "Roll the PC back to an earlier restore point", "History24", "rstrui.exe"),
        new("System Properties", "Computer name, performance and protection settings", "Desktop24", "SystemPropertiesAdvanced.exe"),
        new("System Information", "Hardware, drivers and software summary", "Info24", "msinfo32.exe"),
        new("Services", "Start, stop and configure background services", "Settings24", "services.msc"),
        new("Task Scheduler", "Programs that run on a schedule", "CalendarClock24", "taskschd.msc"),
        new("Event Viewer", "System and application logs", "DocumentText24", "eventvwr.msc"),
        new("Device Manager", "Hardware and drivers", "DeveloperBoard24", "devmgmt.msc"),
        new("Disk Management", "Partitions and volumes", "HardDrive24", "diskmgmt.msc"),
        new("Disk Cleanup", "Windows' own space cleaner", "Broom24", "cleanmgr.exe"),
        new("Optimize Drives", "Defragment and trim drives", "DataUsage24", "dfrgui.exe"),
        new("Windows Security", "Firewall, antivirus and protection status", "Shield24", "windowsdefender:"),
        new("Windows Features", "Turn optional Windows components on or off", "AppsAddIn24", "optionalfeatures.exe"),
        new("Malicious Software Removal Tool", "Microsoft's on-demand malware scan", "ShieldError24", "mrt.exe"),
        new("Group Policy", "Local policy editor (Pro editions)", "BookToolbox24", "gpedit.msc"),
        new("Shared Folders", "Shares, sessions and open files", "FolderPeople24", "fsmgmt.msc"),
        new("Backup and Restore", "Windows 7-style backups and system images", "ArrowSync24", "sdclt.exe"),
        new("Network Information", "Full IP configuration", "Globe24", "ipconfig", "/all", Console: true),
        new("Network Connections (netstat)", "Programs with open network connections", "PlugConnected24", "netstat", "-b -n -o", Console: true),
        new("Check Disk", "Check the system drive's file system for errors", "HardDrive24", "chkdsk", "", Console: true),
        new("System File Checker", "Find and repair corrupt Windows files", "WrenchScrewdriver24", "sfc", "/scannow", Console: true),
        new("DISM Health Restore", "Repair the Windows component store", "Wrench24", "DISM", "/Online /Cleanup-Image /RestoreHealth", Console: true),
        new("On-Screen Keyboard", "Type without a physical keyboard", "Keyboard24", "osk.exe"),
        new("Registry Editor", "Edit the Windows registry", "Database24", "regedit.exe"),
        new("Resource Monitor", "Live CPU, disk, network and memory use", "Pulse24", "resmon.exe"),
    ];
}
