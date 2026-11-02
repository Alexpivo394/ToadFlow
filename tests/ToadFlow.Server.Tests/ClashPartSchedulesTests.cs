using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Регулярный прогон части: какие расписания выгрузки для коллизий её прогоняют, выбор из части и из утверждения.</summary>
public class ClashPartSchedulesTests : ServerTest
{
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId)> PartAsync(bool approve = true)
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await ClashDraftFixtures.PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        if (approve) (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = false })).EnsureSuccessStatusCode();
        return (admin, agent, partId);
    }

    /// <summary>Расписание выгрузки для коллизий с частями <paramref name="parts" />; <paramref name="kind" /> — для расписания другого модуля.</summary>
    private Task<Guid> ScheduleAsync(string name, Guid[] parts, string view = "Navisworks", string kind = ModuleKinds.ClashExtract) => DbAsync(async db =>
    {
        var schedule = Seed.Schedule(db, Seed.Source(db).Id);
        schedule.Name = name;
        schedule.ModuleKind = kind;
        schedule.ModuleSettingsJson = kind == ModuleKinds.ClashExtract
            ? JsonSerializer.Serialize(new ClashScheduleSettings(view, "", parts), ToadFlowDb.Json)
            : "{}";
        await db.SaveChangesAsync();
        return schedule.Id;
    });

    private Task<ClashScheduleSettings> SettingsAsync(Guid scheduleId) => DbAsync(async db =>
        ClashScheduleSettings.TryParse(JsonDocument.Parse((await db.Schedules.SingleAsync(s => s.Id == scheduleId)).ModuleSettingsJson).RootElement)!);

    [Test]
    public async Task Schedules_ListMarksSelected()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        var night = await ScheduleAsync("Ночная выгрузка", [partId]);
        var other = await ScheduleAsync("Дневная выгрузка", []);
        await ScheduleAsync("Экспорт", [], kind: ModuleKinds.Export);

        var list = await admin.GetJsonAsync<PartScheduleView[]>($"/api/clash/parts/{partId}/schedules");

        list.Select(s => (s.Id, s.Selected)).OrderBy(s => s.Selected).ShouldBe([(other, false), (night, true)]);
        list.Single(s => s.Id == night).Name.ShouldBe("Ночная выгрузка");
        (await admin.GetJsonAsync<ClashPartSummary[]>("/api/clash/parts")).Single(p => p.Id == partId).ScheduleNames.ShouldBe(["Ночная выгрузка"]);
    }

    [Test]
    public async Task Schedules_Put_AddsAndRemoves_KeepsViewName()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        var keepOther = Guid.NewGuid();
        var night = await ScheduleAsync("Ночная", [partId, keepOther], view: "Мой вид");
        var day = await ScheduleAsync("Дневная", []);

        var response = await admin.PutJsonAsync($"/api/clash/parts/{partId}/schedules", new { scheduleIds = new[] { day } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var nightSettings = await SettingsAsync(night);
        nightSettings.ViewName.ShouldBe("Мой вид");
        nightSettings.Parts.ShouldBe([keepOther]);
        (await SettingsAsync(day)).Parts.ShouldBe([partId]);
    }

    [Test]
    public async Task Schedules_Put_NotApproved_400()
    {
        var (admin, agent, partId) = await PartAsync(approve: false);
        await using var _ = agent;
        var day = await ScheduleAsync("Дневная", []);

        var response = await admin.PutJsonAsync($"/api/clash/parts/{partId}/schedules", new { scheduleIds = new[] { day } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Сначала утвердите часть");
        (await SettingsAsync(day)).Parts.ShouldBeEmpty();
    }

    [Test]
    public async Task Approve_WithSchedules_AppliedInSameTransaction()
    {
        var (admin, agent, partId) = await PartAsync(approve: false);
        await using var _ = agent;
        var night = await ScheduleAsync("Ночная", []);

        var broken = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = false, schedules = new[] { night, Guid.NewGuid() } });
        broken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DbAsync(db => db.Set<ClashPart>().SingleAsync(p => p.Id == partId))).ApprovedVersion.ShouldBeNull();

        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = false, schedules = new[] { night } }))
            .EnsureSuccessStatusCode();

        (await SettingsAsync(night)).Parts.ShouldBe([partId]);
        (await DbAsync(db => db.Set<ClashPart>().SingleAsync(p => p.Id == partId))).ApprovedVersion.ShouldBe(1);
    }

    [Test]
    public async Task Schedules_Put_WritesChangeLog()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        var night = await ScheduleAsync("Ночная", [partId]);
        var day = await ScheduleAsync("Дневная", []);
        var name = (await DbAsync(db => db.Set<ClashPart>().SingleAsync(p => p.Id == partId))).Name;

        (await admin.PutJsonAsync($"/api/clash/parts/{partId}/schedules", new { scheduleIds = new[] { day } })).EnsureSuccessStatusCode();

        var changes = await DbAsync(db => db.ScheduleChanges.Where(c => c.ScheduleId == night || c.ScheduleId == day).ToListAsync());
        changes.Single(c => c.ScheduleId == day).Summary.ShouldBe($"Часть «{name}»: добавлена в регулярный прогон");
        changes.Single(c => c.ScheduleId == night).Summary.ShouldBe($"Часть «{name}»: убрана из регулярного прогона");
    }
}
