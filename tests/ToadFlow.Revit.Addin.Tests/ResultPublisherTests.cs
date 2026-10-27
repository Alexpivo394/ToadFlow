using Alexpivo394.Revit.Export.Core;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Export;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin.Tests;

[NotInParallel(nameof(ResultPublisher))]
public class ResultPublisherTests
{
    [Test]
    public void PublishOutputs_ReplacesPreviousResults_AndReportsSizes()
    {
        using var dirs = new Dirs();
        dirs.Previous("АР.rvt", "old rvt");
        dirs.Previous("АР.nwc", "old nwc");
        dirs.Staged("АР.rvt", "new rvt");
        dirs.Staged("АР.nwc", "new nwc!");
        var model = new ModelResult { Rvt = StepResult.Done, Nwc = StepResult.Done };
        var log = new List<string>();

        ResultPublisher.PublishOutputs(model, dirs.Payload, dirs.Staging, log.Add);

        File.ReadAllText(dirs.Out("АР.rvt")).ShouldBe("new rvt");
        File.ReadAllText(dirs.Out("АР.nwc")).ShouldBe("new nwc!");
        Directory.GetFiles(dirs.Output).Length.ShouldBe(2);
        log.Count(l => l.Contains("опубликован")).ShouldBe(2);
        var (outcome, result) = PayloadMapping.ToResult(model, dirs.Payload);
        outcome.ShouldBe(JobOutcome.Succeeded);
        result.NwcSizeBytes.ShouldBe(8);
    }

    [Test]
    public void PublishOutputs_ExportProducedNoFile_StepFails_PreviousNwcStays()
    {
        using var dirs = new Dirs();
        dirs.Previous("АР.nwc", "last good");
        var model = new ModelResult { Nwc = StepResult.Done };

        ResultPublisher.PublishOutputs(model, dirs.Payload, dirs.Staging, _ => { });

        model.Nwc.ShouldBe(StepResult.Failed);
        model.Note.ShouldNotBeNull().ShouldContain("прежний файл сохранён");
        File.ReadAllText(dirs.Out("АР.nwc")).ShouldBe("last good");
        PayloadMapping.ToResult(model, dirs.Payload).Outcome.ShouldBe(JobOutcome.Failed);
    }

    [Test]
    public void PublishOutputs_NetworkFailureDuringReplace_PreviousNwcStays()
    {
        using var dirs = new Dirs();
        dirs.Previous("АР.nwc", "last good");
        dirs.Staged("АР.nwc", "new");
        ResultPublisher.ReplaceOverride = (temp, target, backup) =>
        {
            File.Move(target, backup);
            File.Delete(temp);
            throw new IOException("Сетевой путь не найден");
        };
        var model = new ModelResult { Nwc = StepResult.Done, Note = "вид пуст" };
        var log = new List<string>();

        ResultPublisher.PublishOutputs(model, dirs.Payload, dirs.Staging, log.Add);

        model.Nwc.ShouldBe(StepResult.Failed);
        model.Note.ShouldBe("вид пуст; NWC: не опубликован — Сетевой путь не найден; прежний файл сохранён");
        log.ShouldContain(l => l.Contains("System.IO.IOException")); // полный текст исключения — в журнал задания
        File.ReadAllText(dirs.Out("АР.nwc")).ShouldBe("last good");
        Directory.GetFiles(dirs.Output).ShouldBe([dirs.Out("АР.nwc")]);
    }

    [Test]
    public void PublishOutputs_TargetBusy_PreviousFileStays()
    {
        using var dirs = new Dirs();
        dirs.Previous("АР.nwc", "last good");
        dirs.Staged("АР.nwc", "new");
        var model = new ModelResult { Nwc = StepResult.Done };

        using (new FileStream(dirs.Out("АР.nwc"), FileMode.Open, FileAccess.Read, FileShare.Read))
            ResultPublisher.PublishOutputs(model, dirs.Payload, dirs.Staging, _ => { });

        model.Nwc.ShouldBe(StepResult.Failed);
        File.ReadAllText(dirs.Out("АР.nwc")).ShouldBe("last good");
    }

    [Test]
    public void PublishOutputs_SkippedOrCancelled_PublishesNothing()
    {
        using var dirs = new Dirs();
        dirs.Previous("АР.nwc", "last good");
        dirs.Staged("АР.nwc", "half-written");
        var cancelled = new ModelResult { Nwc = StepResult.Done, SkippedByUser = true };
        var failed = new ModelResult { Nwc = StepResult.Failed };

        ResultPublisher.PublishOutputs(cancelled, dirs.Payload, dirs.Staging, _ => { });
        ResultPublisher.PublishOutputs(failed, dirs.Payload, dirs.Staging, _ => { });

        File.ReadAllText(dirs.Out("АР.nwc")).ShouldBe("last good");
    }

    [Test]
    public void Publish_LeftoversAfterCrash_DoNotBlockNextPublish()
    {
        using var dirs = new Dirs();
        dirs.Previous("АР.nwc", "old");
        dirs.Staged("АР.nwc", "new");
        var stale = dirs.Out("АР.nwc") + ResultPublisher.Marker + "dead.tmp";
        File.WriteAllText(stale, "partial");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(3));

        ResultPublisher.Publish(Path.Combine(dirs.Staging, "АР.nwc"), dirs.Out("АР.nwc"));

        File.ReadAllText(dirs.Out("АР.nwc")).ShouldBe("new");
        File.Exists(stale).ShouldBeFalse();
    }

    [Test]
    public void CleanupLeftovers_OnlyCopyOfPreviousResult_IsRestoredNotDeleted()
    {
        using var dirs = new Dirs();
        var backup = dirs.Out("АР.nwc") + ResultPublisher.Marker + "x.bak";
        File.WriteAllText(backup, "last good");
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow - TimeSpan.FromHours(3));

        ResultPublisher.CleanupLeftovers(dirs.Out("АР.nwc"));

        File.ReadAllText(dirs.Out("АР.nwc")).ShouldBe("last good");
        File.Exists(backup).ShouldBeFalse();
    }

    private sealed class Dirs : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tf-pub-" + Guid.NewGuid().ToString("N"))).FullName;

        public Dirs()
        {
            Output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
            Staging = Directory.CreateDirectory(Path.Combine(_root, "staging")).FullName;
            Payload = new ExportJobPayload("АР.rvt", @"\\srv\models\АР.rvt", false, 2024, true, true, Output, new ExportSettingsDto());
        }

        public string Output { get; }
        public string Staging { get; }
        public ExportJobPayload Payload { get; }

        public string Out(string name) => Path.Combine(Output, name);

        public void Previous(string name, string content) => File.WriteAllText(Out(name), content);

        public void Staged(string name, string content) => File.WriteAllText(Path.Combine(Staging, name), content);

        public void Dispose()
        {
            ResultPublisher.ReplaceOverride = null;
            Directory.Delete(_root, true);
        }
    }
}
