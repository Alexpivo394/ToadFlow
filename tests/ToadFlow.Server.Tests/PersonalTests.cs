using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>«Для вас»: что ждёт именно этого человека — по всем включённым модулям.</summary>
public class PersonalTests : ServerTest
{
    public PersonalTests() => Services = FamiliesFixtures.WithoutScanLoop;

    private sealed record Created(Guid Id, int Number);

    private static Task<PersonalView> TodayAsync(HttpClient client) => client.GetJsonAsync<PersonalView>("/api/me/today");

    private static async Task<Created> CreateAsync(HttpClient client, string title = "Клапан")
    {
        var response = await client.PostJsonAsync("/api/families/requests", new NewRequest(title, null, null, null, null));
        response.EnsureSuccessStatusCode();
        return await response.ReadJsonAsync<Created>();
    }

    [Test]
    public async Task Designer_SeesOwnRequestsWaiting()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(designer);
        (await TodayAsync(designer)).Items.ShouldBeEmpty();

        (await coordinator.PostJsonAsync($"/api/families/requests/{created.Id}/ask", new RequestMessageRequest("Какой DN?"))).EnsureSuccessStatusCode();

        var item = (await TodayAsync(designer)).Items.ShouldHaveSingleItem();
        (item.Module, item.Count, item.Tone, item.Link).ShouldBe(("families", 1, PersonalTone.Action, "/families/requests?scope=mine"));
        item.Text.ShouldContain("ответ");
    }

    [Test]
    public async Task Coordinator_SeesUnassignedAndOverdue()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        await CreateAsync(designer, "первая");
        await CreateAsync(designer, "вторая");
        var overdue = await (await designer.PostJsonAsync("/api/families/requests",
            new NewRequest("просроченная", null, null, null, null, NeededBy: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2)))).ReadJsonAsync<Created>();
        (await coordinator.PostAsync($"/api/families/requests/{overdue.Id}/take", null)).EnsureSuccessStatusCode();

        var items = (await TodayAsync(coordinator)).Items;

        items.Single(i => i.Link == "/families/requests?scope=direction").Count.ShouldBe(2);
        items.Single(i => i.Link == "/families/requests?scope=assigned").Count.ShouldBe(1);
    }

    [Test]
    public async Task FollowedScheduleFailed()
    {
        var admin = await AdminAsync();
        var creator = await ClientAsync(UserRole.Operator);
        var schedule = await ScheduleApi.CreateAsync(creator, ScheduleApi.Body(await ScheduleApi.SourceAsync(admin), await ScheduleApi.DefaultPresetAsync(admin)));
        await DbAsync(async db =>
        {
            var run = new Run { Id = Guid.NewGuid(), ScheduleId = schedule.Id, ModuleName = "export", Name = "ЖК Север", State = RunState.Completed,
                CreatedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow };
            db.Runs.Add(run);
            db.Jobs.Add(new Job { Id = Guid.NewGuid(), RunId = run.Id, Status = JobStatus.Failed, ModuleKind = "export", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        var item = (await TodayAsync(creator)).Items.Single(i => i.Module == null);
        (item.Count, item.Link, item.ResolveKey).ShouldBe((1, "/export/history", "export"));
    }

    /// <summary>Запуск расписания с моделью <paramref name="path" /> в статусе <paramref name="status" />.</summary>
    private Task FollowedRunAsync(Guid scheduleId, string path, JobStatus status, DateTimeOffset finished) => DbAsync(async db =>
    {
        var run = new Run { Id = Guid.NewGuid(), ScheduleId = scheduleId, ModuleName = "export", Name = "ЖК Север", State = RunState.Completed,
            CreatedAt = finished, FinishedAt = finished };
        db.Runs.Add(run);
        db.Jobs.Add(new Job { Id = Guid.NewGuid(), RunId = run.Id, Status = status, ModuleKind = "export", ModelPath = path, CreatedAt = finished, FinishedAt = finished });
        await db.SaveChangesAsync();
    });

    private async Task<(HttpClient Admin, HttpClient Creator, Guid ScheduleId)> FollowedScheduleAsync()
    {
        var admin = await AdminAsync();
        var creator = await ClientAsync(UserRole.Operator);
        var schedule = await ScheduleApi.CreateAsync(creator, ScheduleApi.Body(await ScheduleApi.SourceAsync(admin), await ScheduleApi.DefaultPresetAsync(admin)));
        return (admin, creator, schedule.Id);
    }

    [Test]
    public async Task FollowedScheduleFailed_ModelLaterSucceeded_NotListed()
    {
        var (_, creator, scheduleId) = await FollowedScheduleAsync();
        await FollowedRunAsync(scheduleId, "RSN://rs22/А.rvt", JobStatus.Failed, DateTimeOffset.UtcNow.AddHours(-2));
        await FollowedRunAsync(scheduleId, "RSN://rs22/А.rvt", JobStatus.Succeeded, DateTimeOffset.UtcNow.AddHours(-1));

        (await TodayAsync(creator)).Items.ShouldNotContain(i => i.Module == null);
    }

    [Test]
    public async Task FollowedScheduleFailed_Resolved_Hidden_NewFailureReturns()
    {
        var (_, creator, scheduleId) = await FollowedScheduleAsync();
        await FollowedRunAsync(scheduleId, "RSN://rs22/А.rvt", JobStatus.Failed, DateTimeOffset.UtcNow.AddMinutes(-30));

        (await creator.PostJsonAsync("/api/overview/attention/export/resolve", new ResolveRequest("Источник недоступен, починили"))).EnsureSuccessStatusCode();
        (await TodayAsync(creator)).Items.ShouldNotContain(i => i.Module == null);

        await FollowedRunAsync(scheduleId, "RSN://rs22/Б.rvt", JobStatus.Failed, DateTimeOffset.UtcNow.AddSeconds(1));
        (await TodayAsync(creator)).Items.Single(i => i.Module == null).Count.ShouldBe(1);
    }
}

/// <summary>Семейства выключены: «Для вас» не спрашивает выключенный модуль.</summary>
public class PersonalDisabledModuleTests : ServerTest
{
    public PersonalDisabledModuleTests() => Modules = "export";

    [Test]
    public async Task DisabledModule_NoLines()
    {
        var view = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<PersonalView>("/api/me/today");

        view.Items.ShouldBeEmpty();
    }
}
