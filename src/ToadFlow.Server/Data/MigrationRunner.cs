using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Data;

/// <summary>
///     Накатывает миграции при старте. Зарегистрирован первым из фоновых служб: хост запускает их по порядку,
///     поэтому все остальные службы видят уже готовую схему.
/// </summary>
public sealed class MigrationRunner(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        await db.Database.MigrateAsync(ct);
        await FillRunModulesAsync(db, scope.ServiceProvider.GetRequiredService<ModuleSet>(), ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    ///     Модуль запусков, которые его не получили: их создала прежняя версия, поднятая на уже обновлённой базе (откат без
    ///     восстановления копии). Правило то же, что у миграции <c>RunModuleName</c>: по самому раннему заданию, без заданий — по
    ///     расписанию. Запусков без модуля обычно нет, и тогда это один пустой запрос.
    /// </summary>
    private static async Task FillRunModulesAsync(ToadFlowDb db, ModuleSet modules, CancellationToken ct)
    {
        var runs = await db.Runs.Where(r => r.ModuleName == null)
            .Select(r => new
            {
                r.Id,
                JobKind = db.Jobs.Where(j => j.RunId == r.Id).OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).Select(j => j.ModuleKind).FirstOrDefault(),
                ScheduleKind = db.Schedules.Where(s => s.Id == r.ScheduleId).Select(s => s.ModuleKind).FirstOrDefault()
            })
            .ToListAsync(ct);
        foreach (var group in runs
                     .Select(r => (r.Id, Module: modules.NameOfKind(r.JobKind ?? r.ScheduleKind ?? "\0")))
                     .Where(r => r.Module is not null)
                     .GroupBy(r => r.Module!))
        {
            var ids = group.Select(r => r.Id).ToList();
            await db.Runs.Where(r => ids.Contains(r.Id) && r.ModuleName == null).ExecuteUpdateAsync(r => r.SetProperty(x => x.ModuleName, group.Key), ct);
        }
    }
}
