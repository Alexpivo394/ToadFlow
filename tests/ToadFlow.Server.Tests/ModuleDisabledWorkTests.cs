using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Сервер с одной выгрузкой: расписания и задания LOI лежат в базе, но не работают.</summary>
public class ModuleDisabledWorkTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ModuleDisabledWorkTests()
    {
        Time = _time;
        Modules = "export";
    }

    private Task<Schedule> ScheduleAsync(string kind, string name, DateTimeOffset? nextRunAt = null) => DbAsync(async db =>
    {
        var schedule = Seed.Schedule(db, Seed.Source(db).Id);
        schedule.ModuleKind = kind;
        schedule.Name = name;
        schedule.NextRunAt = nextRunAt;
        await db.SaveChangesAsync();
        return schedule;
    });

    [Test]
    public async Task DisabledSchedule_NotListed()
    {
        await ScheduleAsync(ModuleKinds.Loi, "Проверка LOI");
        await ScheduleAsync(ModuleKinds.Export, "Выгрузка");

        var list = await (await AdminAsync()).GetJsonAsync<JsonElement[]>("/api/schedules");

        list.Select(s => s.GetProperty("name").GetString()).ShouldBe(["Выгрузка"]);
    }

    [Test]
    public async Task DisabledSchedule_DoesNotRun()
    {
        var loi = await ScheduleAsync(ModuleKinds.Loi, "Проверка LOI", _time.GetUtcNow().AddMinutes(-1));
        var export = await ScheduleAsync(ModuleKinds.Export, "Выгрузка", _time.GetUtcNow().AddMinutes(-1));

        _time.Advance(TimeSpan.FromSeconds(31));

        // Выгрузка запустилась — значит, проход по расписаниям был, а LOI он пропустил.
        await EventuallyAsync(async () => await DbAsync(db => db.Runs.AnyAsync(r => r.ScheduleId == export.Id)));
        (await DbAsync(db => db.Runs.AnyAsync(r => r.ScheduleId == loi.Id))).ShouldBeFalse();
        (await DbAsync(db => db.Schedules.SingleAsync(s => s.Id == loi.Id))).NextRunAt.ShouldNotBeNull().ShouldBe(loi.NextRunAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Test]
    public async Task CreateSchedule_OfDisabledModule_400()
    {
        var admin = await AdminAsync();
        var body = JsonSerializer.SerializeToNode(ScheduleApi.Body(await ScheduleApi.SourceAsync(admin), await ScheduleApi.DefaultPresetAsync(admin)),
            TestJson.Options)!;
        body["moduleKind"] = ModuleKinds.Loi;

        var response = await admin.PostJsonAsync("/api/schedules", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

public class ModuleWorkTests : ServerTest
{
    [Test]
    public async Task ScheduleFilter_ByModule()
    {
        await DbAsync(async db =>
        {
            var source = Seed.Source(db);
            var export = Seed.Schedule(db, source.Id);
            export.Name = "Выгрузка";
            var loi = Seed.Schedule(db, source.Id);
            loi.Name = "Проверка LOI";
            loi.ModuleKind = ModuleKinds.Loi;
            await db.SaveChangesAsync();
        });
        var admin = await AdminAsync();

        var onlyLoi = await admin.GetJsonAsync<JsonElement[]>("/api/schedules?module=loi");
        var all = await admin.GetJsonAsync<JsonElement[]>("/api/schedules");

        onlyLoi.Select(s => (s.GetProperty("name").GetString(), s.GetProperty("module").GetString())).ShouldBe([("Проверка LOI", "loi")]);
        all.Select(s => s.GetProperty("module").GetString()).Order().ShouldBe(["export", "loi"]);
    }

    [Test]
    public async Task QueuedJobs_CancelledOnStart()
    {
        var (runId, loiJobId) = await DbAsync(async db =>
        {
            var run = Seed.Run(db);
            var job = Seed.Job(db, run.Id);
            job.ModuleKind = ModuleKinds.Loi;
            await db.SaveChangesAsync();
            return (run.Id, job.Id);
        });

        await using var exportOnly = new ServerFactory(ConnectionString, modules: "export");
        _ = exportOnly.Server;

        await EventuallyAsync(async () => (await DbAsync(db => db.Jobs.SingleAsync(j => j.Id == loiJobId))).Status == JobStatus.Cancelled);
        (await DbAsync(db => db.Jobs.SingleAsync(j => j.Id == loiJobId))).Note.ShouldBe("Модуль выключен");
        await EventuallyAsync(async () => (await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId))).FinishedAt is not null);
    }

    [Test]
    public async Task QueuedJobs_OfEnabledModule_Untouched()
    {
        var jobId = await DbAsync(async db =>
        {
            var job = Seed.Job(db, Seed.Run(db).Id);
            await db.SaveChangesAsync();
            return job.Id;
        });

        await using var exportOnly = new ServerFactory(ConnectionString, modules: "export");
        _ = exportOnly.Server;
        await Task.Delay(500);

        (await DbAsync(db => db.Jobs.SingleAsync(j => j.Id == jobId))).Status.ShouldBe(JobStatus.Queued);
    }
}

public class ModuleDisabledRunTests : ServerTest
{
    public ModuleDisabledRunTests() => Modules = "export";

    [Test]
    public async Task RunNow_ScheduleOfDisabledModule_404()
    {
        var scheduleId = await DbAsync(async db =>
        {
            var schedule = Seed.Schedule(db, Seed.Source(db).Id);
            schedule.ModuleKind = ModuleKinds.Loi;
            await db.SaveChangesAsync();
            return schedule.Id;
        });

        var response = await (await AdminAsync()).PostAsync($"/api/schedules/{scheduleId}/run", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await DbAsync(db => db.Runs.AnyAsync(r => r.ScheduleId == scheduleId))).ShouldBeFalse();
    }
}

/// <summary>Задание выключенного модуля, попавшее в очередь после старта (например, «повторить упавшие» через API), агенту не выдаётся.</summary>
public class ModuleDisabledClaimTests : ServerTest
{
    public ModuleDisabledClaimTests() => Modules = "export";

    [Test]
    public async Task Claim_JobOfDisabledModule_NotHandedOut()
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync(), "FARM-01");
        await agent.ConnectAsync(new InstalledRevitDto(2022, true));
        var (loiJob, exportJob) = await DbAsync(async db =>
        {
            var run = Seed.Run(db);
            var loi = Seed.Job(db, run.Id, priority: 10);
            loi.ModuleKind = ModuleKinds.Loi;
            var export = Seed.Job(db, run.Id);
            await db.SaveChangesAsync();
            return (loi.Id, export.Id);
        });

        var assignment = await agent.RequestJobAsync(1, 2022);

        assignment.ShouldNotBeNull().JobId.ShouldBe(exportJob); // у LOI приоритет выше, но модуль выключен
        (await agent.RequestJobAsync(2, 2022)).ShouldBeNull();
        (await DbAsync(db => db.Jobs.SingleAsync(j => j.Id == loiJob))).Status.ShouldBe(JobStatus.Queued);
    }
}

public class ScheduleKindLockedTests : ServerTest
{
    [Test]
    public async Task UpdateSchedule_ChangingKind_400()
    {
        var admin = await AdminAsync();
        var created = await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(await ScheduleApi.SourceAsync(admin), await ScheduleApi.DefaultPresetAsync(admin)));
        var body = JsonSerializer.SerializeToNode(ScheduleApi.Body(created.SourceId, await ScheduleApi.DefaultPresetAsync(admin)), TestJson.Options)!;
        body["moduleKind"] = ModuleKinds.ClashExtract;
        body["moduleSettings"] = JsonNode.Parse("""{"viewName":"Navisworks","worksetKeyword":"","parts":[]}""");

        var response = await admin.PutJsonAsync($"/api/schedules/{created.Id}", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Вид задачи");
        (await DbAsync(db => db.Schedules.SingleAsync(s => s.Id == created.Id))).ModuleKind.ShouldBe(ModuleKinds.Export);
    }
}
