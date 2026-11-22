using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Overview;
using OverviewDto = ToadFlow.Server.Overview.OverviewView;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Подставки для «Обзора»: том с заданным местом, рабочий процесс, источник плитки.</summary>
internal static class OverviewFakes
{
    /// <summary>
    ///     Без настоящих томов и рабочих процессов: на машине разработчика может быть мало места, а движок коллизий в тестах не
    ///     запущен — их строки внимания мешали бы проверять остальные.
    /// </summary>
    public static void Quiet(IServiceCollection services)
    {
        services.RemoveAll<IDataVolume>();
        services.RemoveAll<IWorkerProbe>();
    }

    /// <summary>
    ///     Для тестов с часами на фиксированной дате: срок токена проверяется по тем же подменённым часам, по которым он выдан. Иначе
    ///     через сутки после даты теста настоящие часы считают токен истёкшим и тесты падают с 401.
    /// </summary>
    public static void TokensOnTestClock(IServiceCollection services) =>
        services.AddOptions<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure<TimeProvider>((options, time) => options.TokenValidationParameters.LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return (notBefore is not { } from || from <= now + parameters.ClockSkew) && (expires is not { } to || to >= now - parameters.ClockSkew);
            });

    public sealed class Volume(string name, long free, long total) : IDataVolume
    {
        public string Name => name;
        public VolumeStat? Read() => new(name, free, total);
    }

    public sealed class Worker(string module, string title, DateTimeOffset? lastSeen, DateTimeOffset started) : IWorkerProbe
    {
        public string Module => module;
        public string Title => title;
        public DateTimeOffset? LastSeenAt => lastSeen;
        public DateTimeOffset StartedAt => started;
    }

    public sealed class Tile(string module, Func<OverviewTile?> tile) : IOverviewSource
    {
        public string Module => module;
        public Task<OverviewTile?> TileAsync(IServiceProvider services, CancellationToken ct) => Task.FromResult(tile());
    }

    public static Agent Agent(ToadFlowDb db, string machine, AgentState state, DateTimeOffset? lastSeen)
    {
        var agent = new Agent { Id = Guid.CreateVersion7(), MachineName = machine, State = state, LastSeenAt = lastSeen, RegisteredAt = DateTimeOffset.UtcNow };
        db.Agents.Add(agent);
        return agent;
    }

    public static void FailedJob(ToadFlowDb db, string kind, DateTimeOffset finished)
    {
        var run = Seed.Run(db, RunState.Completed);
        var job = Seed.Job(db, run.Id);
        job.ModuleKind = kind;
        job.Status = JobStatus.Failed;
        job.FinishedAt = finished;
    }

    /// <summary>Законченное задание модели по пути <paramref name="path" />.</summary>
    public static void FinishedJob(ToadFlowDb db, string kind, string path, JobStatus status, DateTimeOffset finished)
    {
        var job = Seed.Job(db, Seed.Run(db, RunState.Completed).Id);
        job.ModuleKind = kind;
        job.ModelPath = path;
        job.Status = status;
        job.FinishedAt = finished;
    }
}

public class OverviewTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public OverviewTests()
    {
        Time = _time;
        Services = OverviewFakes.Quiet;
    }

    [Test]
    public async Task Overview_AllQuiet_NoAttention()
    {
        var overview = await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview");

        overview.Attention.ShouldBeEmpty();
    }

    [Test]
    public async Task AgentOffline_AfterThreshold_Listed()
    {
        var admin = await AdminAsync();
        await DbAsync(async db =>
        {
            OverviewFakes.Agent(db, "BIM-PC-03", AgentState.Offline, _time.GetUtcNow());
            OverviewFakes.Agent(db, "OLD-PC", AgentState.Disabled, _time.GetUtcNow().AddDays(-30)); // отключённые не в счёт
            await db.SaveChangesAsync();
        });

        _time.Advance(TimeSpan.FromMinutes(5));
        (await admin.GetJsonAsync<OverviewDto>("/api/overview")).Attention.ShouldBeEmpty(); // меньше порога 10 мин

        _time.Advance(TimeSpan.FromMinutes(20));
        var item = (await admin.GetJsonAsync<OverviewDto>("/api/overview")).Attention.ShouldHaveSingleItem();
        (item.Level, item.Text, item.Link).ShouldBe((AttentionLevel.Problem, "Агент BIM-PC-03 не в сети 25 мин", "/farm/agents"));
    }

    [Test]
    public async Task PendingRegistration_VisibleToOperator_NotViewer()
    {
        await DbAsync(async db =>
        {
            Seed.User(db, UserRole.Designer).IsPending = true;
            Seed.User(db, UserRole.Designer).IsPending = true;
            await db.SaveChangesAsync();
        });

        var forOperator = await (await ClientAsync(UserRole.Operator)).GetJsonAsync<OverviewDto>("/api/overview");
        var forViewer = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<OverviewDto>("/api/overview");

        var item = forOperator.Attention.ShouldHaveSingleItem();
        (item.Level, item.Text, item.Link).ShouldBe((AttentionLevel.Todo, "2 заявки на регистрацию", "/admin/users"));
        forViewer.Attention.ShouldBeEmpty();
    }

    [Test]
    public async Task FailedJobs_GroupedByModule_ExportBelowAlarmIsQuiet()
    {
        await DbAsync(async db =>
        {
            OverviewFakes.FailedJob(db, ModuleKinds.Export, _time.GetUtcNow().AddHours(-1));
            OverviewFakes.FailedJob(db, "", _time.GetUtcNow().AddHours(-2));
            OverviewFakes.FailedJob(db, ModuleKinds.LoiCatalog, _time.GetUtcNow().AddHours(-3));
            OverviewFakes.FailedJob(db, ModuleKinds.ClashExtract, _time.GetUtcNow().AddHours(-4));
            OverviewFakes.FailedJob(db, ModuleKinds.Export, _time.GetUtcNow().AddDays(-2)); // раньше суток
            await db.SaveChangesAsync();
        });

        var overview = await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview");

        overview.Attention.Select(a => (a.Text, a.Link, a.Module)).ShouldBe([
            ("Проверка LOI: 1 модель с ошибкой за сутки", "/loi/history?status=Failed", "loi"),
            ("Коллизии: 1 модель с ошибкой за сутки", "/clash/setup/history?status=Failed", "clash")
        ]);
    }

    [Test]
    public async Task FailedExports_AboveAlarm_Listed()
    {
        await DbAsync(async db =>
        {
            for (var i = 0; i < 15; i++) OverviewFakes.FailedJob(db, ModuleKinds.Export, _time.GetUtcNow().AddHours(-1));
            await db.SaveChangesAsync();
        });
        var admin = await AdminAsync();
        (await admin.GetJsonAsync<OverviewDto>("/api/overview")).Attention.ShouldBeEmpty(); // 15 — ещё норма: пустые модели падают

        await DbAsync(async db =>
        {
            OverviewFakes.FailedJob(db, ModuleKinds.Export, _time.GetUtcNow().AddHours(-1));
            await db.SaveChangesAsync();
        });
        var item = (await admin.GetJsonAsync<OverviewDto>("/api/overview")).Attention.ShouldHaveSingleItem();
        (item.Level, item.Text).ShouldBe((AttentionLevel.Problem, "Выгрузка: 16 моделей с ошибкой за сутки"));
    }

    [Test]
    public async Task FailedJobs_LaterSuccessOfSameModel_NotCounted()
    {
        var now = _time.GetUtcNow();
        await DbAsync(async db =>
        {
            OverviewFakes.FinishedJob(db, ModuleKinds.Loi, "RSN://rs22/A.rvt", JobStatus.Failed, now.AddHours(-3));
            OverviewFakes.FinishedJob(db, ModuleKinds.Loi, "RSN://rs22/A.rvt", JobStatus.Succeeded, now.AddHours(-1));
            OverviewFakes.FinishedJob(db, ModuleKinds.Loi, "RSN://rs22/B.rvt", JobStatus.Failed, now.AddHours(-2));
            OverviewFakes.FinishedJob(db, ModuleKinds.Loi, "RSN://rs22/B.rvt", JobStatus.Warning, now.AddHours(-1));
            await db.SaveChangesAsync();
        });

        (await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview")).Attention.ShouldBeEmpty();
    }

    [Test]
    public async Task FailedJobs_SuccessBeforeFailure_StillCounted()
    {
        var now = _time.GetUtcNow();
        await DbAsync(async db =>
        {
            OverviewFakes.FinishedJob(db, ModuleKinds.Loi, "RSN://rs22/A.rvt", JobStatus.Succeeded, now.AddHours(-3));
            OverviewFakes.FinishedJob(db, ModuleKinds.Loi, "RSN://rs22/A.rvt", JobStatus.Failed, now.AddHours(-1));
            await db.SaveChangesAsync();
        });

        var item = (await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview")).Attention.ShouldHaveSingleItem();
        item.Text.ShouldBe("Проверка LOI: 1 модель с ошибкой за сутки");
    }

    [Test]
    public async Task FailedJobs_SuccessInOtherKindOrEmptyPath_StillCounted()
    {
        var now = _time.GetUtcNow();
        await DbAsync(async db =>
        {
            OverviewFakes.FinishedJob(db, ModuleKinds.Loi, "RSN://rs22/A.rvt", JobStatus.Failed, now.AddHours(-3));
            OverviewFakes.FinishedJob(db, ModuleKinds.Export, "RSN://rs22/A.rvt", JobStatus.Succeeded, now.AddHours(-1));
            OverviewFakes.FinishedJob(db, ModuleKinds.Loi, "", JobStatus.Failed, now.AddHours(-3));
            OverviewFakes.FinishedJob(db, ModuleKinds.Loi, "", JobStatus.Succeeded, now.AddHours(-1));
            await db.SaveChangesAsync();
        });

        var item = (await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview")).Attention.ShouldHaveSingleItem();
        item.Text.ShouldBe("Проверка LOI: 2 модели с ошибкой за сутки");
    }

    [Test]
    public async Task LowDisk_Listed()
    {
        const long gb = 1024L * 1024 * 1024;
        await using var server = Server.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IDataVolume>(new OverviewFakes.Volume("Данные", 4 * gb, 1000 * gb)); // меньше 5 ГБ
            s.AddSingleton<IDataVolume>(new OverviewFakes.Volume("Слепки", 50 * gb, 1000 * gb)); // меньше 10 %
            s.AddSingleton<IDataVolume>(new OverviewFakes.Volume("Логи", 200 * gb, 1000 * gb));
        }));
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Authorization = (await AdminAsync()).DefaultRequestHeaders.Authorization;

        var overview = await admin.GetJsonAsync<OverviewDto>("/api/overview");

        overview.Attention.Select(a => (a.Text, a.Link)).ShouldBe([
            ("Мало места: Данные, свободно 4 ГБ", "/admin/server"),
            ("Мало места: Слепки, свободно 50 ГБ", "/admin/server")
        ]);
    }

    [Test]
    public async Task SilentWorker_Listed()
    {
        var started = _time.GetUtcNow();
        await using var server = Server.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IWorkerProbe>(new OverviewFakes.Worker("clash", "Движок коллизий", null, started));
            s.AddSingleton<IWorkerProbe>(new OverviewFakes.Worker("export", "Свежий", started.AddMinutes(9), started));
        }));
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Authorization = (await AdminAsync()).DefaultRequestHeaders.Authorization;
        _time.Advance(TimeSpan.FromMinutes(10));

        var overview = await admin.GetJsonAsync<OverviewDto>("/api/overview");

        var item = overview.Attention.ShouldHaveSingleItem();
        (item.Text, item.Link, item.Module).ShouldBe(("Движок коллизий не на связи 10 мин", "/admin/server", "clash"));
        overview.Server.Workers.Select(w => (w.Title, w.Online)).ShouldBe([("Движок коллизий", false), ("Свежий", true)]);
    }

    [Test]
    public async Task ServerStats_Filled()
    {
        var stats = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<ServerStats>("/api/server");

        stats.Version.ShouldNotBeNullOrWhiteSpace();
        stats.DatabaseBytes.ShouldBeGreaterThan(0);
        stats.ProcessMemoryBytes.ShouldBeGreaterThan(0);
        stats.StartedAt.ShouldBeLessThanOrEqualTo(_time.GetUtcNow());
    }
}

/// <summary>Настоящие тома сервера: хотя бы один, с местом.</summary>
public class OverviewVolumesTests : ServerTest
{
    [Test]
    public async Task ServerStats_HasVolume()
    {
        var stats = await (await AdminAsync()).GetJsonAsync<ServerStats>("/api/server");

        stats.Volumes.ShouldNotBeEmpty();
        stats.Volumes.ShouldAllBe(v => v.TotalBytes > 0);
    }
}

public class OverviewThrowingTileTests : ServerTest
{
    public OverviewThrowingTileTests() => Services = s =>
    {
        OverviewFakes.Quiet(s);
        s.RemoveAll<IOverviewSource>();
        s.AddSingleton<IOverviewSource>(new OverviewFakes.Tile("export", () => throw new InvalidOperationException("сломался")));
        s.AddSingleton<IOverviewSource>(new OverviewFakes.Tile("loi", () => new OverviewTile("loi", "87 %", "заполнено", "▲ 2", ["объектов: 12"], "/loi/analytics")));
        s.AddSingleton<IOverviewSource>(new OverviewFakes.Tile("clash", () => new OverviewTile("clash", "318", "открыто", null, [], "/clash/summary")));
    };

    [Test]
    public async Task ThrowingSource_GivesNoDataTile_RestIntact()
    {
        var overview = await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview");

        overview.Tiles.Select(t => (t.Module, t.Value, t.Label)).ShouldBe([("export", "—", "нет данных"), ("loi", "87 %", "заполнено"), ("clash", "318", "открыто")]);
        overview.Tiles[0].Link.ShouldBe("/export");
        overview.Server.Version.ShouldNotBeNullOrWhiteSpace();
    }
}

public class OverviewDisabledModuleTests : ServerTest
{
    public OverviewDisabledModuleTests()
    {
        Modules = "export";
        Services = s =>
        {
            OverviewFakes.Quiet(s);
            s.RemoveAll<IOverviewSource>();
            s.AddSingleton<IOverviewSource>(new OverviewFakes.Tile("export", () => new OverviewTile("export", "142", "за сутки", null, [], "/export/schedules")));
            s.AddSingleton<IOverviewSource>(new OverviewFakes.Tile("loi", () => new OverviewTile("loi", "87 %", "заполнено", null, [], "/loi/analytics")));
        };
    }

    [Test]
    public async Task Tiles_OnlyForEnabledModules()
    {
        var overview = await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview");

        overview.Tiles.Select(t => t.Module).ShouldBe(["export"]);
    }

    [Test]
    public async Task FailedJobs_OfDisabledModule_NotListed()
    {
        await DbAsync(async db =>
        {
            OverviewFakes.FailedJob(db, ModuleKinds.Loi, DateTimeOffset.UtcNow.AddHours(-1));
            await db.SaveChangesAsync();
        });

        var overview = await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview");

        overview.Attention.ShouldBeEmpty();
    }
}
