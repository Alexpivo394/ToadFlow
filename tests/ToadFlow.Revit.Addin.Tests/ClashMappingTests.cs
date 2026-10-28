using System.Text.Json;
using System.Text.Json.Serialization;
using Alexpivo394.Revit.Clash.Revit;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin.Tests;

public class ClashMappingTests
{
    private static readonly JsonSerializerOptions Server = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly ClashExtractPayload Payload = new("АР.rvt", "RSN://rs22/ЖК/АР.rvt", true, 2022, "0123456789abcdef0123456789abcdef", "Navisworks",
        "#Связи");

    [Test]
    public void ParsePayload_ReadsServerJson() =>
        ClashMapping.ParsePayload(JsonSerializer.Serialize(Payload, Server)).ShouldBe(Payload);

    [Test]
    public void MapFailure_MissingView_FailsWithoutRetry()
    {
        var (outcome, note) = ClashMapping.MapFailure(new ArgumentException("В документе нет 3D-вида", "options"), revitFailure: false, Payload.ViewName);

        outcome.ShouldBe(JobOutcome.Failed);
        note.ShouldBe("в модели нет 3D-вида «Navisworks»");
    }

    [Test]
    public void MapFailure_Cancelled() =>
        ClashMapping.MapFailure(new OperationCanceledException(), revitFailure: false, Payload.ViewName).ShouldBe((JobOutcome.Cancelled, "отменено пользователем"));

    [Test]
    public void MapFailure_RevitFailure_IsInfrastructure()
    {
        var (outcome, note) = ClashMapping.MapFailure(new InvalidOperationException("A managed exception was thrown by Revit"), revitFailure: true,
            Payload.ViewName);

        outcome.ShouldBe(JobOutcome.InfrastructureFailure);
        note.ShouldContain("A managed exception was thrown by Revit");
    }

    [Test]
    public void MapFailure_OtherError_FailsWithMessage() =>
        ClashMapping.MapFailure(new IOException("диск переполнен"), revitFailure: false, Payload.ViewName).ShouldBe((JobOutcome.Failed, "диск переполнен"));

    [Test]
    public void ToOpenOptions_NeverUpgradesAndSkipsLinks()
    {
        var options = ClashMapping.ToOpenOptions(Payload);

        options.AllowUpgrade.ShouldBeFalse();
        options.SkipLinks.ShouldBeTrue();
        options.Audit.ShouldBeFalse();
        options.WorksetKeyword.ShouldBe("#Связи");
    }

    [Test]
    public void CheckYear_Mismatch()
    {
        ClashMapping.CheckYear(2022, 2022).ShouldBeNull();
        ClashMapping.CheckYear(2022, 2024).ShouldBe("Задание для Revit 2022, а этот экземпляр — Revit 2024");
    }

    [Test]
    public void Describe_FormatsElements()
    {
        ClashMapping.Describe(new ExtractProgress(ExtractStage.Elements, 1500, 32419)).ShouldBe("элементы: 1 500 из 32 419");
        ClashMapping.Describe(new ExtractProgress(ExtractStage.Inventory, 10, 20)).ShouldBe("опись модели: 10 из 20");
        ClashMapping.Describe(new ExtractProgress(ExtractStage.Collecting, 0, 0)).ShouldBe("сбор видимых элементов");
    }

    [Test]
    public void ToResult_StagedSnapshot()
    {
        var staged = ClashMapping.ToStaged(@"C:\stage\job", 3, 120, 7);

        JsonSerializer.Deserialize<ClashStagedSnapshot>(AddinPipeJson.SerializeObject(staged), Server).ShouldBe(staged);
    }
}

public class ClashMappingLogTests
{
    [Test]
    public void FailureLog_Cancelled_IsShort() => ClashMapping.FailureLog(new OperationCanceledException()).ShouldBe("выгрузка отменена");

    [Test]
    public void FailureLog_Error_KeepsDetails() => ClashMapping.FailureLog(new IOException("диск переполнен")).ShouldStartWith("выгрузка не удалась: System.IO.IOException: диск переполнен");
}
