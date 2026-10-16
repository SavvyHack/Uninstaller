using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Tests;

public class PathAndCommandTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\uninst.exe\" /S", "C:\\Program Files\\App\\uninst.exe", "/S")]
    [InlineData("C:\\Tools\\remove.exe --all", "C:\\Tools\\remove.exe", "--all")]
    [InlineData("MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", "MsiExec.exe", "/X{11111111-2222-3333-4444-555555555555}")]
    [InlineData("C:\\Program Files\\Some App\\unins000.exe", "C:\\Program Files\\Some App\\unins000.exe", "")]
    public void Split_separates_executable_and_arguments(string command, string exe, string args)
    {
        var (e, a) = CommandLine.Split(command);
        Assert.Equal(exe, e);
        Assert.Equal(args, a);
    }

    [Fact]
    public void Clean_strips_quotes_icon_index_and_expands_variables()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(Path.Combine(windir, "notepad.exe"), PathUtil.Clean("\"%windir%\\notepad.exe\",0"));
        Assert.Null(PathUtil.Clean("relative\\path.exe"));
        Assert.Null(PathUtil.Clean(""));
    }

    [Theory]
    [InlineData("C:\\Apps\\Foo\\bin\\x.dll", "C:\\Apps\\Foo", true)]
    [InlineData("C:\\Apps\\Foo", "C:\\Apps\\Foo\\", true)]
    [InlineData("C:\\Apps\\FooBar\\x.dll", "C:\\Apps\\Foo", false)]
    [InlineData("HKEY_CURRENT_USER\\Software\\A\\B", "HKEY_CURRENT_USER\\Software\\A", true)]
    public void IsUnder_respects_folder_boundaries(string path, string root, bool expected) =>
        Assert.Equal(expected, PathUtil.IsUnder(path, root));

    [Theory]
    [InlineData("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes", "HKEY_LOCAL_MACHINE\\SOFT????\\Classes", true)]
    [InlineData("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes", "HKEY_LOCAL_MACHINE\\*\\Classes", true)]
    [InlineData("readme.tmp", "*.tmp", true)]
    [InlineData("readme.txt", "*.tmp", false)]
    public void Wildcard_matches_like_the_exclusion_list(string input, string pattern, bool expected) =>
        Assert.Equal(expected, Wildcard.IsMatch(input, pattern));

    [Fact]
    public void Format_bytes_is_human_readable()
    {
        Assert.Equal("0 B", Format.Bytes(0));
        Assert.Equal("512 B", Format.Bytes(512));
        Assert.Equal("1.5 KB", Format.Bytes(1536));
        Assert.Equal("2 GB", Format.Bytes(2L * 1024 * 1024 * 1024));
    }
}
