using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Лента «Что происходит»: готовые задания и рабочие события, без пользователей и настроек.</summary>
public class OverviewFeedTests : ServerTest
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);

    public OverviewFeedTests()
    {
        Time = new FakeTimeProvider(Now);
        Services = services =>
        {
            OverviewFakes.Quiet(services);
            OverviewFakes.TokensOnTestClock(services);
        };
    }

    private async Task<FeedItem[]> FeedAsync(UserRole role = UserRole.Viewer) =>
        await (await ClientAsync(role)).GetJsonAsync<FeedItem[]>("/api/overview/feed");

    private static void Job(ToadFlowDb db, string model, JobStatus status, DateTimeOffset at, Guid? agentId = null)
    {
        var job = Seed.Job(db, Seed.Run(db, RunState.Completed).Id, name: model);
        job.Status = status;
        job.FinishedAt = at;
        job.AgentId = agentId;
    }

    private static void Event(ToadFlowDb db, string kind, string text, DateTimeOffset at) =>
        db.SystemEvents.Add(new SystemEvent { At = at, ActorKind = ActorKind.System, ActorName = "система", Kind = kind, Text = text });

    [Test]
    public async Task Feed_MergesJobsAndEventsNewestFirst()
    {
        await DbAsync(async db =>
        {
            var agent = OverviewFakes.Agent(db, "PC1", AgentState.Online, Now);
            Job(db, "А.rvt", JobStatus.Succeeded, Now.AddMinutes(-30), agent.Id);
            Event(db, "RunFinished", "Запуск «ЖК Север» завершён", Now.AddMinutes(-25));
            Job(db, "Б.rvt", JobStatus.Failed, Now.AddMinutes(-90));
            Job(db, "В.rvt", JobStatus.Running, Now.AddMinutes(-5));
            await db.SaveChangesAsync();
        });

        var feed = await FeedAsync();

        feed.Select(f => f.Text).ShouldBe(["Запуск «ЖК Север» завершён", "А.rvt — готово · PC1", "Б.rvt — ошибка"]);
        (feed[1].Module, feed[1].Tone).ShouldBe(("export", "ok"));
        (feed[0].Module, feed[0].Tone).ShouldBe((null, "info"));
        feed[2].Tone.ShouldBe("fail");
    }

    [Test]
    public async Task Feed_HidesUserSettingsAndConfigEvents()
    {
        await DbAsync(async db =>
        {
            Event(db, "UserChanged", "Пользователь ivanov стал администратором", Now.AddMinutes(-1));
            Event(db, "SettingsChanged", "Семейства: корень библиотеки «X»", Now.AddMinutes(-2));
            Event(db, "CatalogChanged", "Изменён источник «RS22»", Now.AddMinutes(-3));
            Event(db, "AgentChanged", "Лимиты агента PC1: R2022: 2", Now.AddMinutes(-4));
            Event(db, "CatalogChanged", "Семейства: опубликовано «Дверь»", Now.AddMinutes(-5));
            Event(db, "ClashPartApproved", "Утверждена версия 2 части Корпус 1", Now.AddMinutes(-6));
            await db.SaveChangesAsync();
        });

        var feed = await FeedAsync();

        feed.Select(f => (f.Text, f.Module)).ShouldBe([("Семейства: опубликовано «Дверь»", "families"), ("Утверждена версия 2 части Корпус 1", "clash")]);
    }

    [Test]
    public async Task Feed_Takes15()
    {
        await DbAsync(async db =>
        {
            for (var i = 0; i < 20; i++) Job(db, $"{i}.rvt", JobStatus.Succeeded, Now.AddMinutes(-i));
            await db.SaveChangesAsync();
        });

        var feed = await FeedAsync();

        feed.Length.ShouldBe(15);
        feed[0].Text.ShouldStartWith("0.rvt");
    }

    [Test]
    public async Task Feed_OfflineAgentIsFail()
    {
        await DbAsync(async db =>
        {
            Event(db, "AgentWentOffline", "Агент PC3 потерял связь", Now.AddMinutes(-1));
            Event(db, "AgentWentOnline", "Агент PC3 подключился", Now.AddMinutes(-2));
            await db.SaveChangesAsync();
        });

        (await FeedAsync()).Select(f => f.Tone).ShouldBe(["fail", "ok"]);
    }

    [Test]
    public async Task Feed_ShowsWhoResolvedAndWhy_WithModuleIcon()
    {
        const string text = "Выгрузка: ошибки отмечены решёнными — Борисов: модель удалили из источника";
        await DbAsync(async db =>
        {
            Event(db, "AttentionResolved", text, Now.AddMinutes(-1));
            await db.SaveChangesAsync();
        });

        var item = (await FeedAsync()).ShouldHaveSingleItem();

        (item.Text, item.Module, item.Tone).ShouldBe((text, "export", "ok"));
    }
}
