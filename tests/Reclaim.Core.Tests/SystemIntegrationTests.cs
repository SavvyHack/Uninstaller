using Microsoft.Win32;
using Reclaim.Core.Backup;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Leftovers;
using Reclaim.Core.Monitor;
using Reclaim.Core.SystemTools;

namespace Reclaim.Core.Tests;

/// <summary>Tests that touch the real file system and HKCU (no admin rights needed); everything is cleaned up.</summary>
public class SystemIntegrationTests : IDisposable
{
    private readonly string _key = @"Software\ReclaimTests\" + Guid.NewGuid().ToString("N");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReclaimIT-" + Guid.NewGuid().ToString("N"));
    private string FullKey => @"HKEY_CURRENT_USER\" + _key;

    public SystemIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_key, false);
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Registry_snapshot_diff_reports_new_keys_and_values()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("Existing", "1");
        var before = RegistrySnapshot.Capture([FullKey]);

        using (var k = Registry.CurrentUser.CreateSubKey(_key))
        {
            k.SetValue("Added", "x");
            k.SetValue("Existing", "2");
            using var sub = k.CreateSubKey(@"NewVendor\NewProduct");
            sub.SetValue("v", 1);
        }
        var after = RegistrySnapshot.Capture([FullKey]);
        var diff = after.DiffFrom(before);

        Assert.Equal([FullKey + @"\NewVendor"], diff.CreatedKeys); // top-most new key only
        Assert.Contains((FullKey, "Added"), diff.CreatedValues);
        Assert.Contains((FullKey, "Existing"), diff.ChangedValues);
    }

    [Fact]
    public void Relocation_moves_files_behind_a_junction_and_back()
    {
        var source = Path.Combine(_dir, "App");
        var target = Path.Combine(_dir, "Moved", "App");
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        File.WriteAllText(Path.Combine(source, "bin", "app.dll"), new string('a', 4096));
        File.WriteAllText(Path.Combine(source, "config.ini"), "x=1");
        var size = FileOps.DirectorySize(source);

        SoftwareRelocator.MoveWithJunction(source, target, size, null, CancellationToken.None);

        Assert.True(new DirectoryInfo(source).Attributes.HasFlag(FileAttributes.ReparsePoint), "old path should be a junction");
        Assert.Equal("x=1", File.ReadAllText(Path.Combine(source, "config.ini"))); // still reachable through the link
        Assert.True(File.Exists(Path.Combine(target, "bin", "app.dll")));
        Assert.False(Directory.Exists(source + ".reclaim-moving"));

        SoftwareRelocator.MoveBack(source, target, CancellationToken.None);

        Assert.False(new DirectoryInfo(source).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("x=1", File.ReadAllText(Path.Combine(source, "config.ini")));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Move_back_refuses_to_touch_a_real_folder()
    {
        var source = Path.Combine(_dir, "Real");
        Directory.CreateDirectory(source);
        Assert.Throws<IOException>(() => SoftwareRelocator.MoveBack(source, Path.Combine(_dir, "Nope"), CancellationToken.None));
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public void Removed_leftovers_can_be_restored_from_the_backup()
    {
        var folder = Path.Combine(_dir, "Leftover");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "data.txt"), "keep me");
        using (var k = Registry.CurrentUser.CreateSubKey(_key + @"\Vendor"))
        {
            k.SetValue("Setting", "on");
            k.SetValue("Number", 42, RegistryValueKind.DWord);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("Loose", "value");

        var items = new List<LeftoverItem>
        {
            new() { Kind = LeftoverKind.Directory, Path = folder, Category = "t" },
            new() { Kind = LeftoverKind.RegistryKey, Path = FullKey + @"\Vendor", Category = "t" },
            new() { Kind = LeftoverKind.RegistryValue, Path = FullKey, ValueName = "Loose", Category = "t" },
        };
        SettingsStore.Current.DeleteMode = DeleteMode.Quarantine;
        var result = LeftoverCleaner.Clean(items, "test", null, CancellationToken.None);

        Assert.Equal(3, result.Removed);
        Assert.False(Directory.Exists(folder));
        Assert.False(RegistryUtil.KeyExists(FullKey + @"\Vendor"));
        Assert.False(RegistryUtil.ValueExists(FullKey, "Loose"));

        var (restored, failed) = BackupSession.Open(result.BackupFolder)!.Restore();

        Assert.Equal(3, restored);
        Assert.Equal(0, failed);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(folder, "data.txt")));
        using var vendor = Registry.CurrentUser.OpenSubKey(_key + @"\Vendor")!;
        Assert.Equal("on", vendor.GetValue("Setting"));
        Assert.Equal(42, vendor.GetValue("Number"));
        using var root = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal("value", root.GetValue("Loose"));
    }

    [Fact]
    public void Cleaner_refuses_protected_locations_even_if_selected()
    {
        var items = new List<LeftoverItem>
        {
            new() { Kind = LeftoverKind.Directory, Path = Environment.GetFolderPath(Environment.SpecialFolder.Windows), Category = "t" },
            new() { Kind = LeftoverKind.RegistryKey, Path = @"HKEY_CURRENT_USER\Software", Category = "t" },
        };
        var result = LeftoverCleaner.Clean(items, "guard test", null, CancellationToken.None);
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, result.Failed);
        Assert.True(Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
    }

    [Fact]
    public void Shredder_overwrites_and_deletes_files()
    {
        var file = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(file, "top secret");
        Assert.True(Cleaners.SecureErase.ShredFile(file, Cleaners.WipeMethod.DoD3, CancellationToken.None));
        Assert.False(File.Exists(file));
        Assert.Empty(Directory.GetFiles(_dir)); // the renamed copy is gone too
    }
}
