using Shouldly;
using ToadFlow.Agent.Revit;

namespace ToadFlow.Agent.Tests.Revit;

public class RevitLocatorTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string ProgramData = @"C:\ProgramData";

    private sealed class FakeRegistry(params UninstallEntry[] entries) : IRegistryReader
    {
        public IEnumerable<UninstallEntry> UninstallEntries() => entries;
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Files.ContainsKey(path);

        public IEnumerable<string> GetFiles(string directory, string pattern) =>
            Files.Keys.Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase)
                                     && path.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase));

        public string ReadAllText(string path) => Files[path];
    }

    private static RevitLocator Locator(FakeRegistry registry, FakeFileSystem files) => new(registry, files, ProgramFiles, ProgramData);

    [Test]
    public void Find_UninstallEntry_UsesInstallLocation()
    {
        var files = new FakeFileSystem { Files = { [@"D:\Apps\Revit 2022\Revit.exe"] = "" } };

        var found = Locator(new FakeRegistry(new UninstallEntry("Revit 2022", @"D:\Apps\Revit 2022\")), files).Find();

        found.ShouldHaveSingleItem().ShouldBe(new RevitInstallation(2022, @"D:\Apps\Revit 2022\Revit.exe", false));
    }

    [Test]
    public void Find_IgnoresNonRevitProducts()
    {
        var files = new FakeFileSystem { Files = { [@"C:\Program Files\Autodesk\Batch Print for Revit 2022\Revit.exe"] = "" } };
        var registry = new FakeRegistry(
            new UninstallEntry("Batch Print for Autodesk Revit 2022", @"C:\Program Files\Autodesk\Batch Print for Revit 2022\"),
            new UninstallEntry("Autodesk Revit 2022", @"C:\Program Files\Autodesk"),
            new UninstallEntry("Autodesk Russian Content for Revit 2022", null));

        Locator(registry, files).Find().ShouldBeEmpty();
    }

    [Test]
    public void Find_FallbackToProgramFiles()
    {
        var files = new FakeFileSystem { Files = { [@"C:\Program Files\Autodesk\Revit 2024\Revit.exe"] = "" } };

        Locator(new FakeRegistry(), files).Find().ShouldHaveSingleItem().Year.ShouldBe(2024);
    }

    [Test]
    public void Find_YearsOutside2020To2027_Ignored()
    {
        var files = new FakeFileSystem
        {
            Files = { [@"C:\Program Files\Autodesk\Revit 2019\Revit.exe"] = "", [@"C:\Program Files\Autodesk\Revit 2028\Revit.exe"] = "" }
        };
        var registry = new FakeRegistry(new UninstallEntry("Revit 2019", @"C:\Program Files\Autodesk\Revit 2019\"));

        Locator(registry, files).Find().ShouldBeEmpty();
    }

    [Test]
    public void Find_NwcExporterByAddinManifest()
    {
        var files = new FakeFileSystem
        {
            Files =
            {
                [@"C:\Program Files\Autodesk\Revit 2024\Revit.exe"] = "",
                [@"C:\Program Files\Autodesk\Revit 2025\Revit.exe"] = "",
                [@"C:\ProgramData\Autodesk\Revit\Addins\2024\Autodesk.Navisworks.Exporter.addin"] = "<RevitAddIns/>",
                [@"C:\ProgramData\Autodesk\Revit\Addins\2025\NWExport.addin"] = "<Name>Navisworks NWC Export</Name>"
            }
        };

        var found = Locator(new FakeRegistry(), files).Find();

        found.Single(r => r.Year == 2024).HasNwcExporter.ShouldBeTrue();
        found.Single(r => r.Year == 2025).HasNwcExporter.ShouldBeTrue();
    }

    [Test]
    public void Find_ExeMissing_Ignored() =>
        Locator(new FakeRegistry(new UninstallEntry("Revit 2023", @"C:\Program Files\Autodesk\Revit 2023\")), new FakeFileSystem()).Find().ShouldBeEmpty();

    [Test]
    public void Find_DuplicateYears_Once()
    {
        var files = new FakeFileSystem { Files = { [@"C:\Program Files\Autodesk\Revit 2022\Revit.exe"] = "" } };
        var registry = new FakeRegistry(new UninstallEntry("Revit 2022", @"C:\Program Files\Autodesk\Revit 2022\"));

        Locator(registry, files).Find().ShouldHaveSingleItem();
    }
}
