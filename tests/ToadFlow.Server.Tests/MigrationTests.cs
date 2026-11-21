using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using ToadFlow.Server.Data;
using ToadFlow.Server.Settings;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Обновление схемы с выпуска 1.7.1 (ветка main) одной миграцией 2.0 и соответствие модели миграциям.</summary>
public class MigrationTests
{
    /// <summary>Последняя миграция выпуска 1.7.1.</summary>
    private const string Main171 = "20261026083805_LoiProfileGroups";

    /// <summary>Миграция выпуска 2.0 — последняя до платформы модулей.</summary>
    private const string Clash20 = "20261104102438_Clash2_0";

    private const string RunModuleName = "20261120202222_RunModuleName";

    [ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
    public required PostgresFixture Postgres { get; init; }

    private static void NoHostedServices(IServiceCollection services)
    {
        foreach (var hosted in services.Where(s => s.ServiceType == typeof(IHostedService)).ToList()) services.Remove(hosted);
    }

    [Test]
    public async Task Migrations_FromMain171Schema_ApplyCleanly()
    {
        var connection = await Postgres.CreateEmptyDatabaseAsync();
        await using var server = new ServerFactory(connection + ";Pooling=false", services: NoHostedServices);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(Main171);
        var userId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO users (id, login, display_name, password_hash, role, is_disabled, created_at)
            VALUES ({userId}, 'ivanov', 'Иванов', 'hash', 'Operator', false, now())
            """);

        await migrator.MigrateAsync();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        // С 1.7.1 до 2.0 — одна миграция; после неё — миграции следующих выпусков.
        applied.Where(m => string.CompareOrdinal(m, Main171) > 0 && string.CompareOrdinal(m, Clash20) <= 0).ShouldBe([Clash20]);
        (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        (user.Login, user.Role.ToString(), user.IsPending, user.Disciplines.Length).ShouldBe(("ivanov", "Operator", false, 0));
        user.SecurityStamp.ShouldNotBe(Guid.Empty);
        Postgres.DropDatabaseLater(connection);
    }

    [Test]
    public async Task Migration_BackfillsModuleName()
    {
        var connection = await Postgres.CreateEmptyDatabaseAsync();
        await using var server = new ServerFactory(connection + ";Pooling=false", services: NoHostedServices);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Clash20);

        var (user, source, schedule) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        const string Selection = """{"models":[],"folders":[]}""", Empty = "{}";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO users (id, login, display_name, password_hash, role, is_disabled, created_at)
            VALUES ({user}, 'ivanov', 'Иванов', 'hash', 'Operator', false, now());
            INSERT INTO sources (id, name, kind, host, root_path) VALUES ({source}, 'RS', 'RevitServer', 'rs22', '');
            INSERT INTO schedules (id, name, module_kind, source_id, output_folder_template, selection, module_settings_json, recurrence_kind,
                days_of_week_mask, time_of_day, priority, model_timeout_minutes, max_retries, is_enabled, created_by_user_id, created_at,
                updated_by_user_id, updated_at)
            VALUES ({schedule}, 'LOI', 'loi', {source}, '', {Selection}::jsonb, {Empty}::jsonb, 'Weekly', 0, '20:00', 0, 30, 1, true, {user}, now(),
                {user}, now());
            """);
        string[] kinds = ["export", "", "loi-catalog", "clash-extract"];
        var runs = new List<Guid>();
        foreach (var kind in kinds)
        {
            var run = Guid.NewGuid();
            runs.Add(run);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO runs (id, name, state, created_at, cancel_requested) VALUES ({run}, 'Запуск', 'Completed', now(), false);
                INSERT INTO jobs (id, run_id, module_kind, model_name, model_path, payload_json, status, attempt, max_attempts, priority,
                    timeout_minutes, created_at, cancel_requested, requires_nwc)
                VALUES ({Guid.NewGuid()}, {run}, {kind}, 'АР.rvt', 'RSN://rs22/АР.rvt', {Empty}::jsonb, 'Succeeded', 1, 1, 0, 30, now(), false, false);
                """);
        }

        var scheduled = Guid.NewGuid();
        runs.Add(scheduled);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO runs (id, schedule_id, name, state, created_at, cancel_requested) VALUES ({scheduled}, {schedule}, 'LOI', 'Completed', now(), false);
            """);

        await migrator.MigrateAsync();

        var names = await db.Runs.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.ModuleName);
        runs.Select(id => names[id]).ShouldBe(["export", "export", "loi", "clash", "loi"]);
        Postgres.DropDatabaseLater(connection);
    }

    /// <summary>
    ///     После отката на прежнюю версию без восстановления копии она создаёт запуски без модуля; миграция при повторном
    ///     обновлении уже применена, поэтому модуль дозаполняет запуск сервера.
    /// </summary>
    [Test]
    public async Task Startup_FillsModuleOfRunsCreatedByPreviousVersion()
    {
        var connection = await Postgres.CreateEmptyDatabaseAsync();
        await using var server = new ServerFactory(connection + ";Pooling=false", services: NoHostedServices);
        var runner = new MigrationRunner(server.Services.GetRequiredService<IServiceScopeFactory>());
        await runner.StartAsync(CancellationToken.None);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();

        var (user, source, schedule) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        const string Selection = """{"models":[],"folders":[]}""", Empty = "{}";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO users (id, login, display_name, password_hash, role, is_disabled, created_at)
            VALUES ({user}, 'ivanov', 'Иванов', 'hash', 'Operator', false, now());
            INSERT INTO sources (id, name, kind, host, root_path) VALUES ({source}, 'RS', 'RevitServer', 'rs22', '');
            INSERT INTO schedules (id, name, module_kind, source_id, output_folder_template, selection, module_settings_json, recurrence_kind,
                days_of_week_mask, time_of_day, priority, model_timeout_minutes, max_retries, is_enabled, created_by_user_id, created_at,
                updated_by_user_id, updated_at)
            VALUES ({schedule}, 'Коллизии', 'clash-extract', {source}, '', {Selection}::jsonb, {Empty}::jsonb, 'Weekly', 0, '20:00', 0, 30, 1, true,
                {user}, now(), {user}, now());
            """);
        var (byJob, byEmptyKind, bySchedule, orphan, filled) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO runs (id, name, state, created_at, cancel_requested) VALUES ({byJob}, 'Запуск', 'Completed', now(), false);
            INSERT INTO runs (id, name, state, created_at, cancel_requested) VALUES ({byEmptyKind}, 'Запуск', 'Completed', now(), false);
            INSERT INTO runs (id, schedule_id, name, state, created_at, cancel_requested) VALUES ({bySchedule}, {schedule}, 'Коллизии', 'Completed', now(), false);
            INSERT INTO runs (id, name, state, created_at, cancel_requested) VALUES ({orphan}, 'Запуск', 'Completed', now(), false);
            INSERT INTO runs (id, name, state, created_at, cancel_requested, module_name) VALUES ({filled}, 'Запуск', 'Completed', now(), false, 'export');
            INSERT INTO jobs (id, run_id, module_kind, model_name, model_path, payload_json, status, attempt, max_attempts, priority,
                timeout_minutes, created_at, cancel_requested, requires_nwc)
            VALUES ({Guid.NewGuid()}, {byJob}, 'loi-catalog', 'АР.rvt', 'RSN://rs22/АР.rvt', {Empty}::jsonb, 'Succeeded', 1, 1, 0, 30, now(), false, false),
                   ({Guid.NewGuid()}, {byEmptyKind}, '', 'АР.rvt', 'RSN://rs22/АР.rvt', {Empty}::jsonb, 'Succeeded', 1, 1, 0, 30, now(), false, false),
                   ({Guid.NewGuid()}, {filled}, 'loi', 'АР.rvt', 'RSN://rs22/АР.rvt', {Empty}::jsonb, 'Succeeded', 1, 1, 0, 30, now(), false, false);
            """);

        await runner.StartAsync(CancellationToken.None);

        var names = await db.Runs.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.ModuleName);
        new[] { byJob, byEmptyKind, bySchedule, orphan, filled }.Select(id => names[id]).ShouldBe(["loi", "export", "clash", null, "export"]);
        Postgres.DropDatabaseLater(connection);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Migration_CopiesClashThreshold(bool generalSaved)
    {
        var connection = await Postgres.CreateEmptyDatabaseAsync();
        await using var server = new ServerFactory(connection + ";Pooling=false", services: NoHostedServices);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(RunModuleName);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO settings (key, value) VALUES ('clash.overview', '{{"staleDays":7,"agentsOfflineMinutes":25}}');
            """);
        if (generalSaved)
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO settings (key, value) VALUES ('general', '{{"timeZoneId":"Asia/Novosibirsk","defaultModelTimeoutMinutes":45}}');
                """);

        await migrator.MigrateAsync();

        var general = await scope.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync();
        general.AgentsOfflineMinutes.ShouldBe(25);
        if (generalSaved) (general.TimeZoneId, general.DefaultModelTimeoutMinutes).ShouldBe(("Asia/Novosibirsk", 45));
        Postgres.DropDatabaseLater(connection);
    }

    [Test]
    public async Task Migration_NoClashThreshold_GeneralStaysDefault()
    {
        var connection = await Postgres.CreateEmptyDatabaseAsync();
        await using var server = new ServerFactory(connection + ";Pooling=false", services: NoHostedServices);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        await db.GetService<IMigrator>().MigrateAsync();

        (await scope.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync()).AgentsOfflineMinutes.ShouldBe(10);
        (await db.Settings.CountAsync(s => s.Key == "general")).ShouldBe(0);
        Postgres.DropDatabaseLater(connection);
    }

    [Test]
    public async Task Model_HasNoPendingChanges()
    {
        var connection = await Postgres.CreateEmptyDatabaseAsync();
        await using var server = new ServerFactory(connection + ";Pooling=false", services: NoHostedServices);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();

        db.Database.HasPendingModelChanges().ShouldBeFalse();
        Postgres.DropDatabaseLater(connection);
    }

    /// <summary>Выпуск 3.0: заявки без направлений; «срочно» становится «блокирует выдачу».</summary>
    [Test]
    public async Task Migration_RequestUrgencyBecomesBlocksIssue()
    {
        var connection = await Postgres.CreateEmptyDatabaseAsync();
        await using var server = new ServerFactory(connection + ";Pooling=false", services: NoHostedServices);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261121150830_FamiliesPanelAndRequests");
        var (urgent, normal, author) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var (id, urgency) in new[] { (urgent, "Urgent"), (normal, "Normal") })
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO family_requests (id, title, text, section, urgency, project, state, author_user_id, created_at, updated_at, search_text)
                VALUES ({id}, 'Клапан', '', 'ОВ', {urgency}, '', 'New', {author}, now(), now(), 'клапан')
                """);

        await migrator.MigrateAsync();

        var requests = await db.Set<ToadFlow.Server.Families.Requests.FamilyRequest>().AsNoTracking().ToDictionaryAsync(r => r.Id);
        (requests[urgent].BlocksIssue, requests[normal].BlocksIssue).ShouldBe((true, false));
        requests[urgent].Kind.ShouldBe(ToadFlow.Server.Families.Requests.RequestKind.New);
        requests[urgent].DirectionId.ShouldBeNull();
        Postgres.DropDatabaseLater(connection);
    }
}
