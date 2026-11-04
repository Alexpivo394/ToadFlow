using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using ToadFlow.Server.Data;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Обновление схемы с выпуска 1.7.1 (ветка main) одной миграцией 2.0 и соответствие модели миграциям.</summary>
public class MigrationTests
{
    /// <summary>Последняя миграция выпуска 1.7.1.</summary>
    private const string Main171 = "20261026083805_LoiProfileGroups";

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
        applied[^1].ShouldEndWith("_Clash2_0");
        applied.Count(m => string.CompareOrdinal(m, Main171) > 0).ShouldBe(1);
        (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        (user.Login, user.Role.ToString(), user.IsPending, user.Disciplines.Length).ShouldBe(("ivanov", "Operator", false, 0));
        user.SecurityStamp.ShouldNotBe(Guid.Empty);
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
}
