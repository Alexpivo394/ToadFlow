using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ToadFlow.Agent.Jobs;
using ToadFlow.Agent.Server;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;

namespace ToadFlow.Agent.Tests.Jobs;

/// <summary>Задания агент ведёт по (задание, попытка) и после переподключения сверяет их со списком сервера.</summary>
public class AttemptTrackingTests
{
    private static readonly Guid Job = Guid.NewGuid();

    [Test]
    public void TwoAttemptsSameJob_DoNotShareRunningCancelOrStaging()
    {
        var runner = new JobRunner(null!, TimeProvider.System, NullLogger<JobRunner>.Instance);
        var (first, second) = (new JobAttemptKey(Job, 1), new JobAttemptKey(Job, 2));
        runner.Track(first);
        runner.Track(second);

        runner.Cancel(first);
        runner.IsCancelled(first).ShouldBeTrue();
        runner.IsCancelled(second).ShouldBeFalse();

        // Первая попытка закончилась: отмена второй (от сервера — по заданию) не теряется.
        runner.Forget(first);
        runner.Cancel(Job);
        runner.IsCancelled(second).ShouldBeTrue();
        runner.Active.ShouldBe([second]);

        ClashStaging.Folder(Job, 1).ShouldNotBe(ClashStaging.Folder(Job, 2));
        ClashStaging.IsInside(ClashStaging.Folder(Job, 2)).ShouldBeTrue();
    }

    [Test]
    public void Reconnected_ServerNoLongerHasJob_LocalAttemptCancelled()
    {
        var other = Guid.NewGuid();
        var local = new[] { new JobAttemptKey(Job, 1), new JobAttemptKey(other, 3) };

        var plan = JobReconciliation.Plan(local, [new ActiveJobDto(other, 3), new ActiveJobDto(Job, 2)]);

        // Попытку 1 сервер уже отдал другой попытке: её надо бросить; попытку 2 агент не получал — о ней ниже.
        plan.Cancel.ShouldBe([new JobAttemptKey(Job, 1)]);
    }

    [Test]
    public void RequestJobResponseLost_UnknownActiveJobReportedFailed()
    {
        var plan = JobReconciliation.Plan([], [new ActiveJobDto(Job, 2)]);

        plan.Cancel.ShouldBeEmpty();
        plan.Unknown.ShouldBe([new JobAttemptKey(Job, 2)]);
        var completion = JobReconciliation.LostAssignment(plan.Unknown[0]);
        (completion.JobId, completion.Attempt, completion.Outcome).ShouldBe((Job, (int?)2, JobOutcome.InfrastructureFailure));
    }

    [Test]
    public void ServerWithoutActiveJobs_NothingToDo()
    {
        var plan = JobReconciliation.Plan([new JobAttemptKey(Job, 1)], null);

        plan.Cancel.ShouldBeEmpty();
        plan.Unknown.ShouldBeEmpty();
    }
}
