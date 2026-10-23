using Alexpivo394.Revit.Export.Core;
using Shouldly;
using ToadFlow.Agent.Instances;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Export;
using ToadFlow.Contracts.Pipe;
using ToadFlow.Revit.Addin.Logic;
using NwcParametersDto = ToadFlow.Contracts.Export.NwcParameters;

namespace ToadFlow.Revit.Addin.Tests;

public class AddinPipeJsonTests
{
    private static string Sample(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", file));

    [Test]
    public void ReadsAgentSamples()
    {
        AddinPipeJson.Deserialize(Sample("job.json")).ShouldBeOfType<JobMessage>().TimeoutMinutes.ShouldBe(30);
        AddinPipeJson.Deserialize(Sample("cancel.json")).ShouldBeOfType<CancelMessage>().JobId.ShouldBe(Guid.Parse("0f4f21fd-5ab8-45fd-b322-fd9b4af4c8f2"));
        AddinPipeJson.Deserialize(Sample("result.json")).ShouldBeOfType<ResultMessage>().Outcome.ShouldBe(JobOutcome.Warning);
        AddinPipeJson.Deserialize(Sample("hello.json")).ShouldBeOfType<HelloMessage>().RevitYear.ShouldBe(2022);
        AddinPipeJson.Deserialize(Sample("job.json")).ShouldBeOfType<JobMessage>().PayloadJson.ShouldContain("АР.rvt");
    }

    [Test]
    public void WritesAgentReadableJson()
    {
        var jobId = Guid.NewGuid();
        PipeMessage[] messages =
        [
            new HelloMessage { InstanceId = Guid.NewGuid(), RevitYear = 2022, Pid = 7, HasNwcExporter = false, AddinVersion = "1" },
            new ProgressMessage { JobId = jobId, Stage = "Экспорт NWC", Text = "экспорт", At = DateTimeOffset.UtcNow },
            new AliveMessage { LastActivityAt = DateTimeOffset.UtcNow },
            new ResultMessage { JobId = jobId, Outcome = JobOutcome.InfrastructureFailure, Note = "нет", ResultJson = """{"rvt":"Failed"}""" }
        ];

        foreach (var message in messages)
        {
            var line = AddinPipeJson.Serialize(message);
            line.ShouldNotContain("\n");
            PipeJson.Serialize(PipeJson.Deserialize(line)).ShouldBe(PipeJson.Serialize(message)); // агент читает то же, что аддин писал
        }
    }
}

public class PayloadMappingTests
{
    [Test]
    public void AllSettingsFieldsMapped_UpgradeForbidden()
    {
        var settings = new ExportSettingsDto
        {
            SkipLinks = false, Audit = true, WorksetKeyword = "#Связи", DeleteRevitLinks = false, DeleteCad = false, DeletePointClouds = true,
            Purge = false, Compact = false, NwcViewName = "NW", NwcSharedCoordinates = false, NwcConvertElementProperties = true,
            NwcParameters = NwcParametersDto.Elements, NwcDivideIntoLevels = false, NwcExportLinks = true, NwcConvertLinkedCad = false, NwcExportParts = true,
            NwcRoomGeometry = false, NwcRoomAsAttribute = false, NwcConvertLights = true, NwcExportElementIds = false, NwcExportUrls = false,
            NwcFindMissingMaterials = false, NwcFacetingFactor = 2.5
        };

        var options = PayloadMapping.ToOptions(settings);

        options.AllowUpgrade.ShouldBeFalse();
        foreach (var property in typeof(ExportSettingsDto).GetProperties())
        {
            var target = typeof(ExportOptions).GetProperty(property.Name).ShouldNotBeNull($"нет поля {property.Name} в ExportOptions");
            target.GetValue(options)!.ToString().ShouldBe(property.GetValue(settings)!.ToString(), property.Name);
        }
    }

    [Test]
    public void YearMismatch_Error() =>
        PayloadMapping.CheckYear(Payload(year: 2024), 2022).ShouldNotBeNull().ShouldContain("2024");

    [Test]
    public void YearMatch_NoError() => PayloadMapping.CheckYear(Payload(year: 2022), 2022).ShouldBeNull();

    [Test]
    public void ParsePayload_FromServerJson()
    {
        const string json = """
            {"modelName":"АР.rvt","modelPath":"\\\\nas\\ЖК\\АР.rvt","isServer":false,"revitYear":2022,"rvt":true,"nwc":false,
             "outputFolder":"\\\\nas\\out","settings":{"skipLinks":true,"nwcParameters":"Elements","nwcFacetingFactor":1.5}}
            """;

        var payload = PayloadMapping.ParsePayload(json);

        payload.ModelPath.ShouldBe(@"\\nas\ЖК\АР.rvt");
        payload.Settings.NwcParameters.ShouldBe(NwcParametersDto.Elements);
        payload.Settings.NwcFacetingFactor.ShouldBe(1.5);
        payload.Settings.NwcViewName.ShouldBe("Navisworks"); // не пришло — значение по умолчанию
    }

    [Test]
    public void ToResult_StatusesSizesAndOutcome()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "toadflow-out-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "АР.rvt"), new byte[4096]);
            var model = new ModelResult { Name = "АР.rvt", Rvt = StepResult.Done, Nwc = StepResult.Skipped, Note = "вид «Navisworks» не найден" };

            var (outcome, result) = PayloadMapping.ToResult(model, Payload(nwc: true) with { OutputFolder = folder });

            outcome.ShouldBe(JobOutcome.Warning);
            result.Rvt.ShouldBe(StepStatus.Done);
            result.Nwc.ShouldBe(StepStatus.Skipped);
            result.RvtSizeBytes.ShouldBe(4096);
            result.RvtPath.ShouldBe(Path.Combine(folder, "АР.rvt"));
            result.NwcPath.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Test]
    public void ToResult_SkippedByUser_Cancelled() =>
        PayloadMapping.ToResult(new ModelResult { Name = "АР.rvt", SkippedByUser = true }, Payload()).Outcome.ShouldBe(JobOutcome.Cancelled);

    private static ExportJobPayload Payload(int year = 2022, bool nwc = false) =>
        new("АР.rvt", @"\\nas\ЖК\АР.rvt", false, year, true, nwc, @"\\nas\out", new ExportSettingsDto());
}

public class AddinBootstrapTests
{
    [Test]
    public void NoInstanceId_DoesNothing() => AddinBootstrap.InstanceId(new Dictionary<string, string?>()).ShouldBeNull();

    [Test]
    public void GarbageInstanceId_DoesNothing() =>
        AddinBootstrap.InstanceId(new Dictionary<string, string?> { [PipeNames.InstanceIdVariable] = "not-a-guid" }).ShouldBeNull();

    [Test]
    public void InstanceId_Parsed()
    {
        var id = Guid.NewGuid();

        AddinBootstrap.InstanceId(new Dictionary<string, string?> { [PipeNames.InstanceIdVariable] = id.ToString() }).ShouldBe(id);
    }
}
