using Alexpivo394.Revit.Families.Files;
using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ToadFlow.Agent.Families;
using ToadFlow.Contracts.Families;

namespace ToadFlow.Agent.Tests.Families;

/// <summary>Действия с файлами библиотеки: хеши проверяются до изменения, всё убранное и перезаписанное остаётся в истории.</summary>
public class FamilyFileActionsTests
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "toadflow-act-" + Guid.NewGuid().ToString("N"))).FullName;

    [After(Test)]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // временная папка
        }
    }

    private string Put(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return FamilyHash.OfFile(path);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    private bool Exists(string relative) => File.Exists(Path.Combine(_root, relative));

    private string? History(string hash, string extension = ".rfa") =>
        File.Exists(HistoryStore.PathOf(_root, hash, extension)) ? File.ReadAllText(HistoryStore.PathOf(_root, hash, extension)) : null;

    private Task<FamilyFileActionResult> RunAsync(FamilyFileActionKind kind, string path, string? target, string hash, string? catalog = null, string? replace = null) =>
        new FamilyFileActions(NullLogger<FamilyFileActions>.Instance).RunAsync(new FamilyFileActionDto(kind, _root, path, target, hash, catalog, replace));

    [Test]
    public async Task Move_TakesCatalogAlong_KeepsHistory()
    {
        var hash = Put(@"_На проверке\Клапан.rfa", "v1");
        var catalog = Put(@"_На проверке\Клапан.txt", "types");

        var result = await RunAsync(FamilyFileActionKind.Move, @"_На проверке\Клапан.rfa", @"ОВ\Клапаны\Клапан.rfa", hash);

        result.Error.ShouldBeNull();
        (Exists(@"_На проверке\Клапан.rfa"), Exists(@"_На проверке\Клапан.txt")).ShouldBe((false, false));
        (Read(@"ОВ\Клапаны\Клапан.rfa"), Read(@"ОВ\Клапаны\Клапан.txt")).ShouldBe(("v1", "types"));
        (History(hash), History(catalog, ".txt")).ShouldBe(("v1", "types"));
    }

    [Test]
    public async Task Move_OverCurrent_ReplacesKnownFile_OldContentStaysInHistory()
    {
        var current = Put(@"ОВ\Клапан.rfa", "v1");
        var oldCatalog = Put(@"ОВ\Клапан.txt", "old types");
        var candidate = Put(@"_На проверке\Клапан.rfa", "v2");

        var result = await RunAsync(FamilyFileActionKind.Move, @"_На проверке\Клапан.rfa", @"ОВ\Клапан.rfa", candidate, replace: current);

        result.Error.ShouldBeNull();
        Read(@"ОВ\Клапан.rfa").ShouldBe("v2");
        Exists(@"ОВ\Клапан.txt").ShouldBeFalse(); // у кандидата каталога нет — прежний не остаётся рядом с новым файлом
        (History(current), History(oldCatalog, ".txt"), History(candidate)).ShouldBe(("v1", "old types", "v2"));
    }

    [Test]
    public async Task Move_TargetTakenByUnknownFile_Refused()
    {
        Put(@"ОВ\Клапан.rfa", "чужой");
        var candidate = Put(@"_На проверке\Клапан.rfa", "v2");

        var withoutReplace = await RunAsync(FamilyFileActionKind.Move, @"_На проверке\Клапан.rfa", @"ОВ\Клапан.rfa", candidate);
        var wrongReplace = await RunAsync(FamilyFileActionKind.Move, @"_На проверке\Клапан.rfa", @"ОВ\Клапан.rfa", candidate, replace: FamilyHash.OfStream(new MemoryStream([1])));

        withoutReplace.Error.ShouldNotBeNull();
        wrongReplace.Error!.ShouldContain("изменился");
        (Read(@"ОВ\Клапан.rfa"), Read(@"_На проверке\Клапан.rfa")).ShouldBe(("чужой", "v2"));
    }

    [Test]
    public async Task Move_SourceChangedSinceScan_Refused()
    {
        var seen = Put(@"_На проверке\Клапан.rfa", "v1");
        Put(@"_На проверке\Клапан.rfa", "v1 правка");

        var result = await RunAsync(FamilyFileActionKind.Move, @"_На проверке\Клапан.rfa", @"ОВ\Клапан.rfa", seen);

        result.Error!.ShouldContain("изменился");
        Exists(@"ОВ\Клапан.rfa").ShouldBeFalse();
    }

    [Test]
    public async Task Move_OnlyLetterCase_Renames()
    {
        var hash = Put(@"ОВ\клапан.rfa", "v1");
        Put(@"ОВ\клапан.txt", "t");

        var result = await RunAsync(FamilyFileActionKind.Move, @"ОВ\клапан.rfa", @"ОВ\Клапан.rfa", hash);

        result.Error.ShouldBeNull();
        Directory.GetFiles(Path.Combine(_root, "ОВ")).Select(Path.GetFileName).ShouldBe(["Клапан.rfa", "Клапан.txt"], ignoreOrder: true);
    }

    [Test]
    public async Task Delete_RemovesFileAndCatalog_KeepsBothInHistory()
    {
        var hash = Put(@"ОВ\Клапан.rfa", "v1");
        var catalog = Put(@"ОВ\Клапан.txt", "types");

        var result = await RunAsync(FamilyFileActionKind.Delete, @"ОВ\Клапан.rfa", null, hash);

        result.Error.ShouldBeNull();
        (Exists(@"ОВ\Клапан.rfa"), Exists(@"ОВ\Клапан.txt")).ShouldBe((false, false));
        (History(hash), History(catalog, ".txt")).ShouldBe(("v1", "types"));
    }

    [Test]
    public async Task Delete_ChangedFile_Refused()
    {
        Put(@"ОВ\Клапан.rfa", "v2");

        var result = await RunAsync(FamilyFileActionKind.Delete, @"ОВ\Клапан.rfa", null, FamilyHash.OfStream(new MemoryStream("v1"u8.ToArray())));

        result.Error.ShouldNotBeNull();
        Read(@"ОВ\Клапан.rfa").ShouldBe("v2");
    }

    [Test]
    public async Task Restore_FromHistory_WithCatalog()
    {
        var hash = Put(@"ОВ\Клапан.rfa", "v1");
        var catalog = Put(@"ОВ\Клапан.txt", "types");
        (await RunAsync(FamilyFileActionKind.Delete, @"ОВ\Клапан.rfa", null, hash)).Error.ShouldBeNull();
        File.SetLastWriteTimeUtc(HistoryStore.PathOf(_root, hash, ".rfa"), DateTime.UtcNow.AddDays(-3));

        var result = await RunAsync(FamilyFileActionKind.Restore, @"ОВ\Клапан.rfa", null, hash, catalog);

        result.Error.ShouldBeNull();
        (Read(@"ОВ\Клапан.rfa"), Read(@"ОВ\Клапан.txt")).ShouldBe(("v1", "types"));
        // Время записи — копии из истории: обход возьмёт файл сразу, не дожидаясь, пока он «уляжется».
        File.GetLastWriteTimeUtc(Path.Combine(_root, @"ОВ\Клапан.rfa")).ShouldBe(File.GetLastWriteTimeUtc(HistoryStore.PathOf(_root, hash, ".rfa")));
    }

    [Test]
    public async Task Restore_OldVersionOverCurrent_CurrentKeptInHistory()
    {
        var old = Put(@"ОВ\Клапан.rfa", "v1");
        HistoryStore.Keep(_root, new ScannedFile(@"ОВ\Клапан.rfa", 2, DateTime.UtcNow, old, null, null));
        var current = Put(@"ОВ\Клапан.rfa", "v2");
        Put(@"ОВ\Клапан.txt", "types v2");

        var refused = await RunAsync(FamilyFileActionKind.Restore, @"ОВ\Клапан.rfa", null, old);
        var result = await RunAsync(FamilyFileActionKind.Restore, @"ОВ\Клапан.rfa", null, old, replace: current);

        refused.Error.ShouldNotBeNull();
        result.Error.ShouldBeNull();
        Read(@"ОВ\Клапан.rfa").ShouldBe("v1");
        Exists(@"ОВ\Клапан.txt").ShouldBeFalse();
        History(current).ShouldBe("v2");
    }

    [Test]
    public async Task Restore_NoCopyInHistory_Refused()
    {
        var result = await RunAsync(FamilyFileActionKind.Restore, @"ОВ\Клапан.rfa", null, FamilyHash.OfStream(new MemoryStream("x"u8.ToArray())));

        result.Error!.ShouldContain("history");
    }

    [Test]
    [Arguments(@"..\Клапан.rfa")]
    [Arguments(@"ОВ\..\..\Клапан.rfa")]
    [Arguments(@".toadflow\history\Клапан.rfa")]
    [Arguments(@"C:\Клапан.rfa")]
    [Arguments(@"ОВ\Клапан.txt")]
    public async Task PathsOutsideLibrary_Refused(string path)
    {
        var hash = Put(@"ОВ\Клапан.rfa", "v1");

        var asSource = await RunAsync(FamilyFileActionKind.Delete, path, null, hash);
        var asTarget = await RunAsync(FamilyFileActionKind.Move, @"ОВ\Клапан.rfa", path, hash);

        asSource.Error.ShouldNotBeNull();
        asTarget.Error.ShouldNotBeNull();
        Read(@"ОВ\Клапан.rfa").ShouldBe("v1");
    }

    [Test]
    public async Task LibraryUnreachable_Refused()
    {
        var result = await new FamilyFileActions(NullLogger<FamilyFileActions>.Instance).RunAsync(
            new FamilyFileActionDto(FamilyFileActionKind.Delete, Path.Combine(_root, "нет"), @"ОВ\Клапан.rfa", null, new string('a', 64), null, null));

        result.Error!.ShouldContain("не видит");
    }
}
