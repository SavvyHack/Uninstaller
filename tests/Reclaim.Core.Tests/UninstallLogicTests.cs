using Reclaim.Core.Models;
using Reclaim.Core.Uninstall;
using Reclaim.Core.Updates;

namespace Reclaim.Core.Tests;

public class UninstallLogicTests
{
    private const string Code = "{11111111-2222-3333-4444-555555555555}";

    [Theory]
    [InlineData("MsiExec.exe /I" + Code, UninstallerType.Msi)]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\"", UninstallerType.InnoSetup)]
    [InlineData("\"C:\\ProgramData\\Package Cache\\{x}\\setup.exe\" /uninstall", UninstallerType.WixBurn)]
    [InlineData("\"C:\\Users\\u\\AppData\\Local\\Slack\\Update.exe\" --uninstall", UninstallerType.Squirrel)]
    [InlineData("\"C:\\Program Files (x86)\\InstallShield Installation Information\\{x}\\setup.exe\" -runfromtemp", UninstallerType.InstallShield)]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/440", UninstallerType.Steam)]
    [InlineData("", UninstallerType.None)]
    public void Detects_installer_frameworks(string uninstallString, UninstallerType expected) =>
        Assert.Equal(expected, UninstallerTypeDetector.Detect(uninstallString));

    [Fact]
    public void Msi_quiet_removal_uses_product_code() =>
        Assert.Equal($"MsiExec.exe /X{Code} /qn /norestart REBOOT=ReallySuppress", SilentArgs.Build(UninstallerType.Msi, "MsiExec.exe /I" + Code, null));

    [Theory]
    [InlineData(UninstallerType.Nsis, "\"C:\\Program Files\\App\\uninst.exe\"", "\"C:\\Program Files\\App\\uninst.exe\" /S")]
    [InlineData(UninstallerType.InnoSetup, "\"C:\\Apps\\unins000.exe\"", "C:\\Apps\\unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData(UninstallerType.WixBurn, "\"C:\\ProgramData\\Package Cache\\b\\setup.exe\" /uninstall", "\"C:\\ProgramData\\Package Cache\\b\\setup.exe\" /uninstall /quiet /norestart")]
    public void Builds_silent_switches(UninstallerType type, string uninstall, string expected) =>
        Assert.Equal(expected, SilentArgs.Build(type, uninstall, null));

    [Fact]
    public void Does_not_duplicate_a_switch_already_present() =>
        Assert.Equal("C:\\Apps\\uninst.exe /S", SilentArgs.Build(UninstallerType.Nsis, "C:\\Apps\\uninst.exe /S", null));

    [Fact]
    public void Unknown_uninstallers_get_no_guessed_switch() =>
        Assert.Null(SilentArgs.Build(UninstallerType.Unknown, "C:\\Apps\\remove.exe", null));

    [Fact]
    public void Registered_quiet_command_wins()
    {
        var app = new InstalledApp { Id = "a", Name = "A", UninstallString = "C:\\a\\u.exe", QuietUninstallString = "C:\\a\\u.exe /quiet" };
        Assert.Equal("C:\\a\\u.exe /quiet", SilentArgs.Build(app));
    }

    [Fact]
    public void Forced_msi_turns_modify_into_remove()
    {
        var app = new InstalledApp { Id = "a", Name = "A", UninstallString = "MsiExec.exe /I" + Code, Flags = AppFlags.Msi };
        Assert.Equal("MsiExec.exe /X" + Code, SilentArgs.ForcedMsi(app));
    }

    [Fact]
    public void Script_export_writes_silent_commands_and_notes()
    {
        var msi = new InstalledApp { Id = "1", Name = "Msi App", UninstallString = "MsiExec.exe /I" + Code, Flags = AppFlags.Msi, ProductCode = Code };
        var custom = new InstalledApp { Id = "2", Name = "Custom App", UninstallString = "C:\\c\\remove.exe" };
        var orphan = new InstalledApp { Id = "3", Name = "Leftover", Source = AppSource.Orphan };

        var ps = ScriptExporter.Generate([msi, custom, orphan], ScriptKind.PowerShell);
        Assert.Contains("#Requires -RunAsAdministrator", ps);
        Assert.Contains($"Start-Process -FilePath 'MsiExec.exe' -ArgumentList '/X{Code} /qn /norestart REBOOT=ReallySuppress' -Wait", ps);
        Assert.Contains("No silent switch known", ps);
        Assert.Contains("Not scriptable", ps);

        var bat = ScriptExporter.Generate([msi], ScriptKind.Batch);
        Assert.Contains($"start \"\" /wait MsiExec.exe /X{Code}", bat);
    }

    [Fact]
    public void Winget_table_is_parsed_by_column()
    {
        const string output = """
            Name                     Id                       Version     Available   Source
            ------------------------------------------------------------------------------------
            Mozilla Firefox (x64 en) Mozilla.Firefox          128.0       129.0.1     winget
            7-Zip 23.01 (x64)        7zip.7zip                23.01       24.08       winget
            2 upgrades available.
            """;
        var updates = SoftwareUpdater.Parse(output);
        Assert.Equal(2, updates.Count);
        Assert.Equal("Mozilla.Firefox", updates[0].Id);
        Assert.Equal("129.0.1", updates[0].AvailableVersion);
        Assert.Equal("23.01", updates[1].InstalledVersion);
        Assert.Equal("winget", updates[1].Source);
    }
}
