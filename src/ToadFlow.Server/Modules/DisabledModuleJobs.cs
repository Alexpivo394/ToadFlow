using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Queue;

namespace ToadFlow.Server.Modules;

/// <summary>
///     При старте сервера отменяет задания выключенных модулей, оставшиеся в очереди: выполнить их некому, а запуски иначе
///     висели бы незавершёнными. Выполняющиеся задания не трогаются — их итог пришлёт агент.
/// </summary>
internal sealed class DisabledModuleJobs(IServiceScopeFactory scopes, ModuleSet modules, TimeProvider time, ILogger<DisabledModuleJobs> logger)
    : IHostedService
{
    public const string Note = "Модуль выключен";

    public async Task StartAsync(CancellationToken ct)
    {
        var disabled = modules.Known.Except(modules.Enabled).SelectMany(m => m.Kinds).ToList();
        if (disabled.Count == 0) return;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var queued = db.Jobs.Where(j => j.Status == JobStatus.Queued && disabled.Contains(j.ModuleKind));
        var runIds = await queued.Select(j => j.RunId).Distinct().ToListAsync(ct);
        if (runIds.Count == 0) return;

        var now = time.GetUtcNow();
        var cancelled = await queued.ExecuteUpdateAsync(j => j
            .SetProperty(x => x.Status, JobStatus.Cancelled)
            .SetProperty(x => x.FinishedAt, now)
            .SetProperty(x => x.Note, Note), ct);
        var progress = scope.ServiceProvider.GetRequiredService<RunProgress>();
        foreach (var runId in runIds) await progress.UpdateAsync(runId);
        logger.LogInformation("Отменено заданий выключенных модулей: {Count}", cancelled);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
