using System.Text.Json;
using System.Text.Json.Serialization;
using Alexpivo394.Revit.ParamCheck.Core;
using Alexpivo394.Revit.ParamCheck.Services;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Loi;
using ToadFlow.Contracts.Pipe;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin.Tests;

public class LoiMappingTests
{
    /// <summary>Сервер пишет payload через System.Text.Json (camelCase, перечисления строками), аддин читает Newtonsoft.</summary>
    private static readonly JsonSerializerOptions Server = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static ModelReport Report()
    {
        var report = new ModelReport { ProfileName = "ОВ", ModelName = "АР", SourcePath = "RSN://rs/АР.rvt", CheckedAt = DateTime.Now };
        var rule = new RuleReport("Стены", ["Марка"]);
        rule.Count([new ParamValue(true, "С1")]);
        rule.Count([new ParamValue(true, "")]);
        report.Rules.Add(rule);
        return report;
    }

    private static ModelOutcome Checked() =>
        new("ОВ", "АР", "RSN://rs/АР.rvt", ModelStatus.Checked, TimeSpan.FromMinutes(2), Report(), ReportPath: @"\\nas\loi\АР_LOIReport.xlsx");

    [Test]
    public void Payload_FromServer_ReadByAddin()
    {
        var sent = new LoiJobPayload("АР.rvt", "RSN://rs22/ЖК/АР.rvt", true, 2022, @"\\nas\loi\2026-10-24", DateTimeOffset.Parse("2026-10-24T20:00:00+03:00"),
            """{"Name":"ОВ","Rules":[]}""", new LoiCheckSettingsDto { SkipLinks = false, ViewName = "3D", WorksetKeyword = "#", ProblemsOnly = true, HistoryFile = "h.xlsx" });

        var read = LoiMapping.ParsePayload(JsonSerializer.Serialize(sent, Server));

        read.ShouldBe(sent with { Settings = read.Settings });
        read.Settings.ShouldBe(sent.Settings);
        read.RunStartedAt.Offset.ShouldBe(TimeSpan.FromHours(3)); // время запуска в базе — по часам из «Общих», а не машины агента
        LoiMapping.ParseCatalogPayload(JsonSerializer.Serialize(new LoiCatalogPayload(2024), Server)).RevitYear.ShouldBe(2024);
    }

    [Test]
    public void Options_UpgradeForbidden()
    {
        var options = LoiMapping.ToOptions(new LoiCheckSettingsDto { SkipLinks = false, ViewName = "3D", WorksetKeyword = "#Связи", ProblemsOnly = true });

        options.ShouldBe(new CheckOptions { SkipLinks = false, ViewName = "3D", WorksetKeyword = "#Связи", ProblemsOnly = true, AllowUpgrade = false });
    }

    [Test]
    public void ReportPath_InOutputFolder()
    {
        var payload = new LoiJobPayload("АР_К1.rvt", "RSN://rs/АР_К1.rvt", true, 2022, @"\\nas\loi", DateTimeOffset.Now, "{}", new LoiCheckSettingsDto());

        LoiMapping.ReportPath(payload).ShouldBe(@"\\nas\loi\АР_К1_LOIReport.xlsx");
    }

    [Test]
    public void Checked_Succeeded_WithFillStats()
    {
        var (outcome, note, result) = LoiMapping.ToResult(Checked(), "abc", null);

        outcome.ShouldBe(JobOutcome.Succeeded);
        note.ShouldBeNull();
        result.ShouldNotBeNull();
        result.Elements.ShouldBe(2);
        result.CompleteElements.ShouldBe(1);
        result.FillRate.ShouldBe(0.5);
        result.ReportPath.ShouldBe(@"\\nas\loi\АР_LOIReport.xlsx");
        result.ProfileFingerprint.ShouldBe("abc");
        result.Rules.ShouldHaveSingleItem().Parameters.ShouldHaveSingleItem().Filled.ShouldBe(1); // статистика по параметрам — для аналитики
    }

    [Test]
    public void HistoryNotWritten_Warning()
    {
        var (outcome, note, result) = LoiMapping.ToResult(Checked(), "abc", "база занята");

        outcome.ShouldBe(JobOutcome.Warning);
        note.ShouldNotBeNull().ShouldContain("база занята");
        result.ShouldNotBeNull();
    }

    [Test]
    public void Failures_MapToJobOutcomes()
    {
        var model = new ModelOutcome("ОВ", "АР", "p", ModelStatus.Failed, TimeSpan.Zero, Error: "нет вида «3D»");
        var revit = model with { Error = "A managed exception was thrown by Revit", RevitFailure = true };
        var skipped = model with { Status = ModelStatus.Skipped, Error = null };

        LoiMapping.ToResult(model, "abc", null).ShouldBe((JobOutcome.Failed, "нет вида «3D»", null));
        LoiMapping.ToResult(revit, "abc", null).Outcome.ShouldBe(JobOutcome.InfrastructureFailure); // сервис повторит модель в новом Revit
        LoiMapping.ToResult(skipped, "abc", null).Outcome.ShouldBe(JobOutcome.Cancelled);

        LoiMapping.GoesToHistory(Checked()).ShouldBeTrue();
        LoiMapping.GoesToHistory(model).ShouldBeTrue();
        LoiMapping.GoesToHistory(revit).ShouldBeFalse();
        LoiMapping.GoesToHistory(skipped).ShouldBeFalse();
    }

    [Test]
    public void Catalog_ToContract()
    {
        var snapshot = new CatalogSnapshot([new CategoryInfo("OST_Walls", "Стены", true)], ["ADSK_Марка"], true);

        var result = LoiMapping.ToCatalogResult(snapshot);

        result.Categories.ShouldHaveSingleItem().ShouldBe(new LoiCategoryDto("OST_Walls", "Стены", true));
        result.Parameters.ShouldBe(["ADSK_Марка"]);
        result.HasSharedParameterFile.ShouldBeTrue();
    }

    [Test]
    public void Year_Mismatch() => LoiMapping.CheckYear(2024, 2022).ShouldNotBeNull().ShouldContain("2024");

    [Test]
    public void JobFromOldAgent_IsExport()
    {
        var job = AddinPipeJson.Deserialize("""{"type":"job","jobId":"0f4f21fd-5ab8-45fd-b322-fd9b4af4c8f2","payloadJson":"{}","timeoutMinutes":30}""");

        job.ShouldBeOfType<JobMessage>().ModuleKind.ShouldBe("");
    }
}
