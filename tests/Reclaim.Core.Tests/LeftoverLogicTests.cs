using Reclaim.Core.Leftovers;
using Reclaim.Core.Models;

namespace Reclaim.Core.Tests;

public class LeftoverLogicTests
{
    private static AppFingerprint Fingerprint(string name, string? publisher = null, string? installDir = null)
    {
        var app = new InstalledApp { Id = "t", Name = name, Publisher = publisher, InstallLocation = installDir };
        return AppFingerprint.FromApp(app);
    }

    [Theory]
    [InlineData("Contoso Widget Studio 3.2.1 (x64)", "Contoso Widget Studio")]
    [InlineData("Foo Bar v2.0.11", "Foo Bar")]
    [InlineData("Mega App™ 2024 64-bit", "Mega App")]
    public void CleanName_drops_versions_years_and_architectures(string raw, string expected) =>
        Assert.Equal(expected, AppFingerprint.CleanName(raw));

    [Fact]
    public void PackGuid_produces_the_Windows_Installer_compressed_form() =>
        Assert.Equal("87654321DCBA10FE32547698BADCFE10", AppFingerprint.PackGuid("{12345678-ABCD-EF01-2345-6789ABCDEF01}"));

    [Fact]
    public void Exact_program_name_is_certain()
    {
        var fp = Fingerprint("Contoso Widget Studio", "Contoso Labs");
        var score = ConfidenceScorer.ScoreName("ContosoWidgetStudio", fp);
        Assert.True(score.Value >= 80, score.ToString());
    }

    [Fact]
    public void Product_name_without_publisher_prefix_still_matches()
    {
        var fp = Fingerprint("Contoso Widget Studio", "Contoso");
        Assert.True(ConfidenceScorer.ScoreName("Widget Studio", fp).Value >= 80);
    }

    [Theory]
    [InlineData("Common")]
    [InlineData("Update")]
    [InlineData("Microsoft")]
    [InlineData("Temp")]
    [InlineData("ab")]
    public void Generic_or_tiny_names_never_match(string candidate)
    {
        var fp = Fingerprint("Update Common Microsoft Temp Tool");
        Assert.Equal(0, ConfidenceScorer.ScoreName(candidate, fp).Value);
    }

    [Fact]
    public void Publisher_folder_alone_is_only_possible()
    {
        var fp = Fingerprint("Contoso Widget Studio", "Contoso Labs Inc.");
        var score = ConfidenceScorer.ScoreName("Contoso Labs", fp);
        Assert.InRange(score.Value, 1, 54);
    }

    [Fact]
    public void Unrelated_names_do_not_match()
    {
        var fp = Fingerprint("Contoso Widget Studio", "Contoso Labs");
        Assert.Equal(0, ConfidenceScorer.ScoreName("Fabrikam Notes", fp).Value);
    }

    [Fact]
    public void Install_dir_references_respect_folder_boundaries()
    {
        // Not under %TEMP%: install folders in Temp are deliberately ignored (they are installers, not programs).
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReclaimTestApps", "FpTest");
        Directory.CreateDirectory(dir);
        try
        {
            var fp = Fingerprint("Fp Test", installDir: dir);
            Assert.True(ConfidenceScorer.ReferencesInstallDir($"\"{dir}\\app.exe\" --tray", fp));
            Assert.True(ConfidenceScorer.ReferencesInstallDir(dir, fp));
            Assert.False(ConfidenceScorer.ReferencesInstallDir(dir + "Other\\app.exe", fp));
            Assert.False(ConfidenceScorer.ReferencesInstallDir(null, fp));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Theory]
    [InlineData(95, ConfidenceBucket.Certain)]
    [InlineData(80, ConfidenceBucket.Certain)]
    [InlineData(79, ConfidenceBucket.Likely)]
    [InlineData(55, ConfidenceBucket.Likely)]
    [InlineData(40, ConfidenceBucket.Possible)]
    public void Confidence_maps_to_buckets(int confidence, ConfidenceBucket bucket)
    {
        var item = new LeftoverItem { Kind = LeftoverKind.File, Path = "x", Category = "c", Confidence = confidence };
        Assert.Equal(bucket, item.Bucket);
    }
}
