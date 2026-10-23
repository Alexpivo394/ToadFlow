using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Export;

namespace ToadFlow.Server.Tests;

public class ExportOutcomeTests
{
    private static ExportJobResult Result(StepStatus rvt, StepStatus nwc) => new(rvt, nwc, null, null, null, null);

    [Test]
    public void AllDone_Succeeded() => ExportOutcome.From(Result(StepStatus.Done, StepStatus.Done), false).ShouldBe(JobOutcome.Succeeded);

    [Test]
    public void NwcSkipped_Warning() => ExportOutcome.From(Result(StepStatus.Done, StepStatus.Skipped), false).ShouldBe(JobOutcome.Warning);

    [Test]
    public void RvtFailed_Failed() => ExportOutcome.From(Result(StepStatus.Failed, StepStatus.Skipped), false).ShouldBe(JobOutcome.Failed);

    [Test]
    public void SkippedByUser_Cancelled() => ExportOutcome.From(Result(StepStatus.Done, StepStatus.NotRequested), true).ShouldBe(JobOutcome.Cancelled);

    [Test]
    public void NotRequestedIgnored() => ExportOutcome.From(Result(StepStatus.Done, StepStatus.NotRequested), false).ShouldBe(JobOutcome.Succeeded);
}
