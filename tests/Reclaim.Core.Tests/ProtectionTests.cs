using Reclaim.Core.Leftovers;
using Reclaim.Core.Models;

namespace Reclaim.Core.Tests;

public class ProtectionTests
{
    public static TheoryData<string> SystemPaths() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS", "x"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft"),
        "C:\\",
    ];

    [Theory]
    [MemberData(nameof(SystemPaths))]
    public void System_and_shared_locations_are_protected(string path) =>
        Assert.True(ProtectionGuard.IsProtectedPath(path), path);

    [Fact]
    public void A_program_folder_is_not_protected() =>
        Assert.False(ProtectionGuard.IsProtectedPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Contoso Widget Studio")));

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft")]
    [InlineData(@"HKEY_CURRENT_USER\Software")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKEY_CLASSES_ROOT\CLSID")]
    public void Core_registry_keys_are_protected(string key) => Assert.True(ProtectionGuard.IsProtectedKey(key), key);

    [Fact]
    public void A_vendor_key_is_not_protected() =>
        Assert.False(ProtectionGuard.IsProtectedKey(@"HKEY_CURRENT_USER\Software\Contoso Labs\Widget Studio"));

    [Fact]
    public void Another_installed_programs_folder_and_key_are_never_offered()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReclaimTestApps", "OtherApp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        try
        {
            var other = new InstalledApp
            {
                Id = "other", Name = "Other App", InstallLocation = dir,
                RegistryKeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\OtherApp",
            };
            var guard = new ProtectionGuard([other], []);
            Assert.NotNull(guard.WhyProtectedPath(dir));
            Assert.NotNull(guard.WhyProtectedPath(Path.Combine(dir, "data")));          // inside it
            Assert.NotNull(guard.WhyProtectedPath(Path.GetDirectoryName(dir)!));        // contains it
            Assert.NotNull(guard.WhyProtectedKey(other.RegistryKeyPath));
            Assert.Null(guard.WhyProtectedPath(dir + "-sibling"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Exclusion_rules_support_wildcards_and_children()
    {
        var rule = new ExclusionRule { Pattern = @"HKEY_CURRENT_USER\Software\MyTools*", IncludeChildren = true };
        Assert.True(rule.Matches(@"HKEY_CURRENT_USER\Software\MyToolsPro"));
        Assert.True(rule.Matches(@"HKEY_CURRENT_USER\Software\MyTools\Sub\Key"));
        Assert.False(rule.Matches(@"HKEY_CURRENT_USER\Software\OtherTools"));

        var exact = new ExclusionRule { Pattern = @"C:\Data", IncludeChildren = false };
        Assert.True(exact.Matches(@"C:\Data"));
        Assert.False(exact.Matches(@"C:\Data\x"));
        Assert.False(new ExclusionRule { Pattern = @"C:\Data", Enabled = false }.Matches(@"C:\Data"));
    }
}
