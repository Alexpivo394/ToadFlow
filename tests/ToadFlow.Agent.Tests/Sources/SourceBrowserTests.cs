using System.Text;
using OpenMcdf;
using Shouldly;
using ToadFlow.Agent.Sources;
using ToadFlow.Contracts;

namespace ToadFlow.Agent.Tests.Sources;

public class SourceBrowserTests
{
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "toadflow-src-" + Guid.NewGuid().ToString("N"))).FullName;

        /// <summary>Модель .rvt — составной файл с потоком BasicFileInfo, как у Revit; year = null — файл без версии.</summary>
        public string Model(string relative, int? year)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var root = RootStorage.Create(path);
            if (year is { } y)
            {
                using var stream = root.CreateStream("BasicFileInfo");
                var data = Encoding.Unicode.GetBytes($"Worksharing: Not enabled\r\nFormat: {y}\r\n");
                stream.Write(data, 0, data.Length);
            }

            return path;
        }

        public SourceDto Source => new(Guid.NewGuid(), SourceKind.Folder, Root, null, "");

        public void Dispose() => Directory.Delete(Root, true);
    }

    private readonly SourceBrowser _browser = new();

    [Test]
    public async Task ListChildren_Folder_RootIsHost()
    {
        using var tree = new TempTree();
        tree.Model(@"ЖК\АР.rvt", 2022);
        tree.Model("Школа.rvt", 2024);

        var entries = await _browser.ListChildrenAsync(tree.Source, "");

        entries.Select(e => (e.Name, e.IsFolder)).ShouldBe([("ЖК", true), ("Школа.rvt", false)]);
        var inside = await _browser.ListChildrenAsync(tree.Source, entries[0].FolderPath);
        inside.ShouldHaveSingleItem().ModelPath.ShouldBe(Path.Combine(tree.Root, "ЖК", "АР.rvt"));
    }

    [Test]
    public async Task Expand_FolderRecursive_WithYears()
    {
        using var tree = new TempTree();
        var ar = tree.Model(@"ЖК\Корпус1\АР.rvt", 2022);
        var kr = tree.Model(@"ЖК\Корпус2\Секция\КР.rvt", 2024);
        tree.Model(@"Школа\ОВ.rvt", 2024);

        var models = await _browser.ExpandAsync(tree.Source, new SelectionDto([], [new FolderSelectionDto(Path.Combine(tree.Root, "ЖК"), true, false)]));

        models.Select(m => (m.ModelPath, m.RevitYear, m.Rvt, m.Nwc, m.IsServer)).OrderBy(m => m.ModelPath)
            .ShouldBe([(ar, 2022, true, false, false), (kr, 2024, true, false, false)]);
    }

    [Test]
    public async Task Expand_WholeSourceFolderPathEmpty()
    {
        using var tree = new TempTree();
        tree.Model("А.rvt", 2022);
        tree.Model(@"Б\Б.rvt", 2022);

        (await _browser.ExpandAsync(tree.Source, new SelectionDto([], [new FolderSelectionDto("", true, true)]))).Length.ShouldBe(2);
    }

    [Test]
    public async Task Expand_ExplicitModelOverridesFolderFormats()
    {
        using var tree = new TempTree();
        var ar = tree.Model(@"ЖК\АР.rvt", 2022);
        tree.Model(@"ЖК\КР.rvt", 2022);

        var models = await _browser.ExpandAsync(tree.Source, new SelectionDto(
            [new ModelSelectionDto("АР.rvt", ar, false, true)], [new FolderSelectionDto(Path.Combine(tree.Root, "ЖК"), true, true)]));

        models.Length.ShouldBe(2);
        var explicitModel = models.Single(m => m.ModelPath == ar);
        explicitModel.Rvt.ShouldBeFalse();
        explicitModel.Nwc.ShouldBeTrue();
    }

    [Test]
    public async Task Expand_Deduplicates_PathCaseInsensitive()
    {
        using var tree = new TempTree();
        var ar = tree.Model(@"ЖК\АР.rvt", 2022);

        var models = await _browser.ExpandAsync(tree.Source, new SelectionDto(
            [new ModelSelectionDto("АР.rvt", ar.ToUpperInvariant(), true, true)], [new FolderSelectionDto(Path.Combine(tree.Root, "ЖК"), true, true)]));

        models.ShouldHaveSingleItem();
    }

    [Test]
    public async Task Expand_UnreadableFile_YearNull()
    {
        using var tree = new TempTree();
        var broken = Path.Combine(tree.Root, "битая.rvt");
        await File.WriteAllTextAsync(broken, "not a revit file");

        var models = await _browser.ExpandAsync(tree.Source, new SelectionDto([new ModelSelectionDto("битая.rvt", broken, true, false)], []));

        models.ShouldHaveSingleItem().RevitYear.ShouldBeNull();
    }

    [Test]
    public async Task Expand_ExplicitModelMissing_Skipped()
    {
        using var tree = new TempTree();

        var models = await _browser.ExpandAsync(tree.Source,
            new SelectionDto([new ModelSelectionDto("пропала.rvt", Path.Combine(tree.Root, "пропала.rvt"), true, false)], []));

        models.ShouldBeEmpty();
    }

    [Test]
    public async Task Expand_RevitServer_ExplicitModels_YearNull()
    {
        var source = new SourceDto(Guid.NewGuid(), SourceKind.RevitServer, "rs22", 2022, "");

        var models = await _browser.ExpandAsync(source, new SelectionDto([new ModelSelectionDto("АР.rvt", "RSN://rs22/ЖК/АР.rvt", true, true)], []));

        var model = models.ShouldHaveSingleItem();
        model.IsServer.ShouldBeTrue();
        model.RevitYear.ShouldBeNull();
    }

    [Test]
    public async Task Expand_SkipsBackupFiles()
    {
        using var tree = new TempTree();
        tree.Model(@"ЖК\АР.rvt", 2022);
        tree.Model(@"ЖК\АР.0001.rvt", 2022);
        tree.Model(@"ЖК\АР_backup\АР.rvt", 2022);

        (await _browser.ExpandAsync(tree.Source, new SelectionDto([], [new FolderSelectionDto(Path.Combine(tree.Root, "ЖК"), true, true)])))
            .ShouldHaveSingleItem().Name.ShouldBe("АР.rvt");
    }
}
