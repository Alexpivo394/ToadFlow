using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class DataModelTests : ServerTest
{
    [Test]
    public async Task Migrations_ApplyOnEmptyDatabase()
    {
        await using var scope = Server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();

        (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        (await db.Jobs.CountAsync()).ShouldBe(0);
        (await db.Schedules.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task Schedule_RoundTripsJsonColumns()
    {
        var selection = new SelectionDto(
            [new ModelSelectionDto("АР_К1.rvt", "RSN://rs22/ЖК/АР_К1.rvt", true, false)],
            [new FolderSelectionDto("ЖК/Корпус2", true, true)]);
        Guid id;

        await using (var scope = Server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
            var source = Seed.Source(db);
            var schedule = Seed.Schedule(db, source.Id);
            schedule.Selection = selection;
            schedule.ModuleSettingsJson = """{"presetId":"7d1d0c0e-2b0b-4c55-9f36-1f0c8b9b3c11"}""";
            schedule.LastKnownModels = [new ExpandedModelDto("АР_К1.rvt", "RSN://rs22/ЖК/АР_К1.rvt", true, 2022, true, false)];
            await db.SaveChangesAsync();
            id = schedule.Id;
        }

        await using (var scope = Server.Services.CreateAsyncScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<ToadFlowDb>().Schedules.SingleAsync(s => s.Id == id);

            loaded.Selection.Models.ShouldHaveSingleItem().ModelPath.ShouldBe("RSN://rs22/ЖК/АР_К1.rvt");
            loaded.Selection.Folders.ShouldHaveSingleItem().FolderPath.ShouldBe("ЖК/Корпус2");
            loaded.ModuleSettingsJson.ShouldContain("7d1d0c0e-2b0b-4c55-9f36-1f0c8b9b3c11");
            loaded.LastKnownModels.ShouldNotBeNull().ShouldHaveSingleItem().RevitYear.ShouldBe(2022);
        }
    }

    [Test]
    public async Task User_LoginIsUnique()
    {
        await using var scope = Server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        db.Users.Add(User.Create("Petrov", "Петров", UserRole.Operator, "hash", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        db.Users.Add(User.Create("PETROV", "Ещё Петров", UserRole.Viewer, "hash", DateTimeOffset.UtcNow));

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
