using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Maintenance;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public sealed record RunCounts(int Succeeded, int Warning, int Failed, int Queued, int Running, int Cancelled, int Total);

public sealed record RunRow(Guid Id, string Name, DateTimeOffset CreatedAt, RunState State, string? TriggeredBy, RunCounts Counts, double? DurationSeconds,
    string? Warning);

public sealed record Page<T>(T[] Items, int Total);

public sealed record JobRow(Guid Id, Guid RunId, string RunName, string ModelName, JobStatus Status, int? RevitYear, string? AgentName, int? Slot,
    int Attempt, int MaxAttempts, double? DurationSeconds, string? Note, JsonElement? Result, int? WaitingForRevit);

public sealed record RunDetails(RunRow Run, JobRow[] Jobs);

public sealed record LogLine(DateTimeOffset At, string Stage, string Text);

public class HistoryTests : ServerTest
{
    private Task SeedAsync(Action<Data.ToadFlowDb, Run> fill, string name = "ЖК Север", DateTimeOffset? createdAt = null) => DbAsync(async db =>
    {
        var run = Seed.Run(db, RunState.Running);
        run.Name = name;
        run.CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        fill(db, run);
        await db.SaveChangesAsync();
    });

    private static Job Finished(Data.ToadFlowDb db, Guid runId, string name, JobStatus status, string? result = null)
    {
        var job = Seed.Job(db, runId, name: name);
        job.Status = status;
        job.Attempt = 1;
        job.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        job.FinishedAt = DateTimeOffset.UtcNow;
        job.ResultJson = result;
        return job;
    }

    [Test]
    public async Task Runs_FilterByStatusAndSearch()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        await SeedAsync((db, run) => Finished(db, run.Id, "ЖК-Юг_ОВ.rvt", JobStatus.Failed), "ЖК Юг");
        await SeedAsync((db, run) => Finished(db, run.Id, "Школа_АР.rvt", JobStatus.Succeeded), "Школа");

        var failed = await viewer.GetJsonAsync<Page<RunRow>>("/api/runs?status=Failed");
        var school = await viewer.GetJsonAsync<Page<RunRow>>("/api/runs?search=школа");

        failed.Items.ShouldHaveSingleItem().Name.ShouldBe("ЖК Юг");
        failed.Items[0].Counts.Failed.ShouldBe(1);
        school.Items.ShouldHaveSingleItem().Name.ShouldBe("Школа");
        (await viewer.GetJsonAsync<Page<RunRow>>("/api/runs")).Total.ShouldBe(2);
    }

    [Test]
    public async Task RunDetails_IncludesExportResultAndWaitingForRevit()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        Guid runId = default;
        await SeedAsync((db, run) =>
        {
            runId = run.Id;
            Finished(db, run.Id, "АР.rvt", JobStatus.Succeeded,
                """{"rvt":"Done","nwc":"Done","rvtSizeBytes":432013312,"nwcSizeBytes":100663296,"rvtPath":"\\\\nas\\out\\АР.rvt","nwcPath":null}""");
            Seed.Job(db, run.Id, year: 2024, name: "КР.rvt");
        });

        var details = await viewer.GetJsonAsync<RunDetails>($"/api/runs/{runId}");

        var done = details.Jobs.Single(j => j.ModelName == "АР.rvt");
        done.Result.ShouldNotBeNull().GetProperty("rvtSizeBytes").GetInt64().ShouldBe(432013312);
        done.DurationSeconds.ShouldNotBeNull().ShouldBeGreaterThan(200);
        details.Jobs.Single(j => j.ModelName == "КР.rvt").WaitingForRevit.ShouldBe(2024);
        details.Run.Counts.Total.ShouldBe(2);
    }

    [Test]
    public async Task Jobs_AllModelsTab_FilterByStatus()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        await SeedAsync((db, run) =>
        {
            Finished(db, run.Id, "А.rvt", JobStatus.Failed);
            Finished(db, run.Id, "Б.rvt", JobStatus.Succeeded);
        });

        var jobs = await viewer.GetJsonAsync<Page<JobRow>>("/api/jobs?status=Failed");

        jobs.Items.ShouldHaveSingleItem().ModelName.ShouldBe("А.rvt");
        jobs.Items[0].RunName.ShouldBe("ЖК Север");
    }

    [Test]
    public async Task LogTxt_FormatsLocalTime()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        Guid jobId = default;
        await SeedAsync((db, run) =>
        {
            var job = Finished(db, run.Id, "ОВ.rvt", JobStatus.Failed);
            jobId = job.Id;
            db.JobLogLines.Add(new JobLogLine { JobId = job.Id, At = DateTimeOffset.Parse("2026-10-23T12:48:02Z"), Stage = "Открытие", Text = "открытие" });
        });

        var json = await viewer.GetJsonAsync<LogLine[]>($"/api/jobs/{jobId}/log");
        var text = await (await viewer.GetAsync($"/api/jobs/{jobId}/log.txt")).Content.ReadAsStringAsync();

        json.ShouldHaveSingleItem().Text.ShouldBe("открытие");
        text.ShouldContain("[15:48:02] Открытие: открытие");
    }
}

public sealed record Kpi(int Exported24h, int Failed24h, double FailedPercent, int Queued, int Running, double? AvgDurationSeconds24h, int AgentsOnline,
    int AgentsTotal);

public sealed record Daily(DateOnly Date, int Succeeded, int Failed);

public sealed record Upcoming(Guid ScheduleId, string Name, DateTimeOffset At, int ModelCount);

public sealed record RecentError(DateTimeOffset At, string ModelName, string? Note, string? AgentName, int? RevitYear, Guid JobId);

public sealed record Dashboard(Kpi Kpi, Daily[] Daily, Upcoming[] Upcoming, RecentError[] RecentErrors);

public class DashboardTests : ServerTest
{
    [Test]
    public async Task Dashboard_KpiCounts()
    {
        var admin = await AdminAsync();
        var now = DateTimeOffset.UtcNow;
        await DbAsync(async db =>
        {
            var run = Seed.Run(db, RunState.Running);
            void Done(JobStatus status, int minutes, DateTimeOffset finished)
            {
                var job = Seed.Job(db, run.Id);
                job.Status = status;
                job.StartedAt = finished.AddMinutes(-minutes);
                job.FinishedAt = finished;
            }

            Done(JobStatus.Succeeded, 4, now.AddHours(-1));
            Done(JobStatus.Warning, 6, now.AddHours(-2));
            Done(JobStatus.Failed, 30, now.AddHours(-3));
            Done(JobStatus.Succeeded, 4, now.AddDays(-2)); // за пределами 24 ч
            Seed.Job(db, run.Id); // в очереди
            var running = Seed.Job(db, run.Id);
            running.Status = JobStatus.Running;
            await db.SaveChangesAsync();
        });

        var dashboard = await admin.GetJsonAsync<Dashboard>("/api/dashboard");

        dashboard.Kpi.Exported24h.ShouldBe(2);
        dashboard.Kpi.Failed24h.ShouldBe(1);
        dashboard.Kpi.FailedPercent.ShouldBe(33.3, 0.1);
        dashboard.Kpi.Queued.ShouldBe(1);
        dashboard.Kpi.Running.ShouldBe(1);
        dashboard.Kpi.AvgDurationSeconds24h.ShouldNotBeNull().ShouldBe(300, 1);
        dashboard.Daily.Length.ShouldBe(14);
        dashboard.RecentErrors.ShouldHaveSingleItem();
    }

    [Test]
    public async Task Dashboard_DailyUsesLocalDays()
    {
        var admin = await AdminAsync();
        var moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, moscow).DateTime);
        // 21:30Z вчерашнего местного дня — это уже 00:30 сегодняшнего по Москве.
        var lateUtc = new DateTimeOffset(todayLocal.AddDays(-1).ToDateTime(new TimeOnly(21, 30)), TimeSpan.Zero);
        await DbAsync(async db =>
        {
            var run = Seed.Run(db, RunState.Running);
            var job = Seed.Job(db, run.Id);
            job.Status = JobStatus.Succeeded;
            job.FinishedAt = lateUtc;
            await db.SaveChangesAsync();
        });

        var dashboard = await admin.GetJsonAsync<Dashboard>("/api/dashboard");

        dashboard.Daily.Last().Date.ShouldBe(todayLocal);
        dashboard.Daily.Last().Succeeded.ShouldBe(1);
    }

    [Test]
    public async Task Dashboard_UpcomingFromSchedules()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin)));

        var dashboard = await admin.GetJsonAsync<Dashboard>("/api/dashboard");

        dashboard.Upcoming.ShouldHaveSingleItem().ModelCount.ShouldBe(1);
    }
}

public class RetentionTests : ServerTest
{
    [Test]
    public async Task Retention_RemovesOldFinishedRunsOnly()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        var (oldRun, oldUnfinished, fresh) = await DbAsync(async db =>
        {
            var a = Seed.Run(db, RunState.Completed);
            a.FinishedAt = old;
            Seed.Job(db, a.Id);
            var b = Seed.Run(db, RunState.Running);
            b.CreatedAt = old;
            var c = Seed.Run(db, RunState.Completed);
            c.FinishedAt = DateTimeOffset.UtcNow.AddDays(-1);
            db.SystemEvents.Add(new SystemEvent { At = old, ActorKind = ActorKind.System, ActorName = "Система", Kind = "x", Text = "старое" });
            await db.SaveChangesAsync();
            return (a.Id, b.Id, c.Id);
        });

        await Server.Services.GetServices<IHostedService>().OfType<RetentionCleaner>().Single().CleanAsync(CancellationToken.None);

        var runs = await DbAsync(db => db.Runs.Select(r => r.Id).ToListAsync());
        runs.ShouldNotContain(oldRun);
        runs.ShouldContain(oldUnfinished);
        runs.ShouldContain(fresh);
        (await DbAsync(db => db.Jobs.CountAsync(j => j.RunId == oldRun))).ShouldBe(0);
        (await DbAsync(db => db.SystemEvents.AnyAsync(e => e.Text == "старое"))).ShouldBeFalse();
    }
}
