using System.Text.Json.Nodes;
using Shouldly;
using ToadFlow.Agent.Instances;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Agent.Tests.Pipe;

public class PipeJsonTests
{
    private static readonly Guid JobId = Guid.Parse("0f4f21fd-5ab8-45fd-b322-fd9b4af4c8f2");

    public static IEnumerable<Func<PipeMessage>> Messages() =>
    [
        () => new HelloMessage { InstanceId = Guid.NewGuid(), RevitYear = 2024, Pid = 42, HasNwcExporter = true, AddinVersion = "1.0.0" },
        () => new JobMessage { JobId = JobId, PayloadJson = """{"modelName":"АР.rvt"}""", TimeoutMinutes = 30 },
        () => new ProgressMessage { JobId = JobId, Stage = "Открытие", Text = "открытие модели", At = DateTimeOffset.Parse("2026-10-23T12:00:00Z") },
        () => new AliveMessage { LastActivityAt = DateTimeOffset.Parse("2026-10-23T12:00:05Z") },
        () => new ResultMessage { JobId = JobId, Outcome = JobOutcome.Failed, Note = "RVT: нет доступа", ResultJson = """{"rvt":"Failed"}""" },
        () => new CancelMessage { JobId = JobId }
    ];

    [Test]
    [MethodDataSource(nameof(Messages))]
    public void PipeJson_RoundTrip_EveryMessageType(PipeMessage message)
    {
        var line = PipeJson.Serialize(message);

        line.ShouldNotContain("\n");
        var back = PipeJson.Deserialize(line);
        back.ShouldBeOfType(message.GetType());
        PipeJson.Serialize(back).ShouldBe(line);
    }

    [Test]
    [Arguments("hello.json", typeof(HelloMessage))]
    [Arguments("job.json", typeof(JobMessage))]
    [Arguments("progress.json", typeof(ProgressMessage))]
    [Arguments("alive.json", typeof(AliveMessage))]
    [Arguments("result.json", typeof(ResultMessage))]
    [Arguments("cancel.json", typeof(CancelMessage))]
    public void PipeJson_Samples_MatchFormat(string file, Type expected)
    {
        var sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pipe", "samples", file));

        var message = PipeJson.Deserialize(sample);

        message.ShouldBeOfType(expected);
        JsonNode.DeepEquals(JsonNode.Parse(PipeJson.Serialize(message)), JsonNode.Parse(sample)).ShouldBeTrue();
    }

    [Test]
    public void PipeJson_Result_OutcomeAsString() =>
        PipeJson.Serialize(new ResultMessage { JobId = JobId, Outcome = JobOutcome.InfrastructureFailure }).ShouldContain("\"outcome\":\"InfrastructureFailure\"");

    [Test]
    public void PipeJson_UnknownType_Throws() =>
        Should.Throw<InvalidDataException>(() => PipeJson.Deserialize("""{"type":"what"}"""));
}
