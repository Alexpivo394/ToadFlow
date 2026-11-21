using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Families.Data;

namespace ToadFlow.Server.Families.Scan;

public enum ScanStart
{
    Started,

    /// <summary>Корень библиотеки не задан.</summary>
    NotConfigured,

    /// <summary>Обход уже идёт: отчёт ещё не пришёл, а с начала не прошло <see cref="LibraryScanner.LostAfter" />.</summary>
    Running,

    /// <summary>Нет агента в сети, который умеет обходить библиотеку.</summary>
    NoAgent,

    /// <summary>Агент ответил «прошлый обход ещё идёт».</summary>
    AgentBusy,

    /// <summary>Агент не ответил или ответил ошибкой.</summary>
    Failed
}

public sealed record ScanStartResult(ScanStart Outcome, Guid? ScanId = null, string? Error = null);

/// <summary>Агент библиотеки: назначенный в параметрах или первый в сети, кто умеет обход.</summary>
public sealed record LibraryAgent(Guid Id, string MachineName, bool Online);

/// <summary>Один обход за раз на весь сервер: «Обойти сейчас» и обход по интервалу не должны начать два обхода.</summary>
public sealed class LibraryScanGate
{
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

/// <summary>
///     Просит агента библиотеки обойти её. Обход идёт у агента в фоне, отчёт приходит отдельным запросом; обход без отчёта дольше
///     <see cref="LostAfter" /> считается потерянным — его отчёт, если всё же придёт, не применяется.
/// </summary>
public sealed class LibraryScanner(ToadFlowDb db, FamiliesSettingsStore settingsStore, AgentRegistry registry, LibraryScanGate gate, TimeProvider time,
    ILogger<LibraryScanner> logger)
{
    public static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Обход по интервалу: если библиотека задана и с начала прошлого обхода прошёл интервал.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        if (!settings.Configured) return;
        var last = await db.Set<FamilyScan>().MaxAsync(s => (DateTimeOffset?)s.StartedAt, ct);
        if (last is { } at && time.GetUtcNow() - at < TimeSpan.FromMinutes(settings.ScanIntervalMinutes)) return;
        var result = await StartAsync(ct);
        if (result.Outcome is ScanStart.Failed) logger.LogWarning("Обход библиотеки не начат: {Error}", result.Error);
    }

    public async Task<ScanStartResult> StartAsync(CancellationToken ct)
    {
        await gate.Lock.WaitAsync(ct);
        try
        {
            return await StartLockedAsync(ct);
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    /// <summary>Агенты, которые умеют обходить библиотеку, — из них администратор выбирает агента библиотеки.</summary>
    public async Task<List<LibraryAgent>> AgentsAsync(CancellationToken ct) =>
        (await db.Agents.AsNoTracking().Where(a => a.State != AgentState.Disabled).Select(a => new { a.Id, a.MachineName, a.Modules }).ToListAsync(ct))
        .Where(a => a.Modules?.Contains(ModuleKinds.FamiliesScan) == true)
        .OrderBy(a => a.MachineName)
        .Select(a => new LibraryAgent(a.Id, a.MachineName, registry.IsConnected(a.Id)))
        .ToList();

    /// <summary>Агент библиотеки сейчас; null — назначенного нет и в сети нет никого, кто умеет обход.</summary>
    public async Task<LibraryAgent?> AgentAsync(FamiliesSettings settings, CancellationToken ct)
    {
        var agents = (await db.Agents.AsNoTracking().Where(a => a.State != AgentState.Disabled).Select(a => new { a.Id, a.MachineName, a.Modules }).ToListAsync(ct))
            .Where(a => a.Modules?.Contains(ModuleKinds.FamiliesScan) == true)
            .ToList();
        if (settings.LibraryAgentId is { } chosen)
            return agents.FirstOrDefault(a => a.Id == chosen) is { } agent ? new LibraryAgent(agent.Id, agent.MachineName, registry.IsConnected(agent.Id)) : null;
        return agents.Where(a => registry.IsConnected(a.Id)).OrderBy(a => a.MachineName).Select(a => new LibraryAgent(a.Id, a.MachineName, true)).FirstOrDefault();
    }

    private async Task<ScanStartResult> StartLockedAsync(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        if (!settings.Configured) return new ScanStartResult(ScanStart.NotConfigured);

        var now = time.GetUtcNow();
        var unfinished = await db.Set<FamilyScan>().Where(s => s.FinishedAt == null).ToListAsync(ct);
        if (unfinished.Any(s => now - s.StartedAt < LostAfter)) return new ScanStartResult(ScanStart.Running);
        foreach (var lost in unfinished)
        {
            lost.FinishedAt = now;
            lost.Errors = ["агент не прислал отчёт обхода"];
        }

        if (await AgentAsync(settings, ct) is not { Online: true } agent || registry.Client(agent.Id) is not { } client)
        {
            await db.SaveChangesAsync(ct);
            return new ScanStartResult(ScanStart.NoAgent);
        }

        var scan = new FamilyScan { Id = Guid.CreateVersion7(), AgentId = agent.Id, StartedAt = now };
        db.Set<FamilyScan>().Add(scan);
        await db.SaveChangesAsync(ct);

        try
        {
            var started = await client.StartFamilyScan(new FamilyScanRequestDto(scan.Id, settings.Root, settings.ReviewFolder)).WaitAsync(CallTimeout, ct);
            if (started) return new ScanStartResult(ScanStart.Started, scan.Id);
            db.Set<FamilyScan>().Remove(scan);
            await db.SaveChangesAsync(ct);
            return new ScanStartResult(ScanStart.AgentBusy);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            scan.FinishedAt = time.GetUtcNow();
            scan.Errors = [$"агент {agent.MachineName} не начал обход: {exception.Message}"];
            await db.SaveChangesAsync(CancellationToken.None);
            return new ScanStartResult(ScanStart.Failed, scan.Id, exception.Message);
        }
    }
}

/// <summary>Раз в минуту проверяет, не пора ли обойти библиотеку.</summary>
public sealed class LibraryScanLoop(IServiceScopeFactory scopes, TimeProvider time, ILogger<LibraryScanLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, time, ct);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LibraryScanner>().TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Проверка обхода библиотеки семейств не удалась");
            }
        }
    }
}
