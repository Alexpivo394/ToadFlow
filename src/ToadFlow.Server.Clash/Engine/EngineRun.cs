using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Engine;

public enum EngineRunState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum EngineRunKind
{
    /// <summary>Прогон черновика: результат — временный список, статусов нет.</summary>
    Draft,

    /// <summary>Прогон утверждённой версии: коллизии сверяются с базой, статусы, лента.</summary>
    Approved
}

/// <summary>
///     Прогон части в движке — черновика или утверждённой версии. Снимок конфигурации и слепков берётся при постановке в
///     очередь: правка черновика во время прогона на него не влияет. Работник держит прогон арендой; без хода аренда истекает,
///     и прогон возвращается в очередь.
/// </summary>
public sealed class EngineRun
{
    public Guid Id { get; set; }
    public Guid PartId { get; set; }
    public EngineRunKind Kind { get; set; }

    /// <summary>Утверждённая версия, которую прогоняет прогон; null — черновик.</summary>
    public int? Version { get; set; }

    /// <summary>Запуск расписания, после выгрузок которого поставлен прогон; null — вручную.</summary>
    public Guid? TriggerRunId { get; set; }

    /// <summary>Модели, которые в этом цикле не выгрузились: их пары, которых не нашли, — «не проверена».</summary>
    public Guid[] UncheckedModels { get; set; } = [];

    /// <summary>
    ///     Прошлый удачный утверждённый прогон части — основа инкрементальности и сверки. Ставится при постановке и уточняется
    ///     при выдаче работнику: пока прогон ждал, мог закончиться другой.
    /// </summary>
    public Guid? PreviousRunId { get; set; }

    /// <summary>Итог сверки утверждённого прогона.</summary>
    public ApprovedRunSummary? Summary { get; set; }

    public EngineRunState State { get; set; }
    public string ConfigurationJson { get; set; } = "";
    public List<EngineRunModel> Models { get; set; } = [];
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 2;
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? Stage { get; set; }
    public string? Error { get; set; }
    public EngineRunStats? Stats { get; set; }
    public int? HitCount { get; set; }
    public Guid CreatedByUserId { get; set; }

    /// <summary>Кто поставил прогон вручную — имя пользователя; null — система или расписание.</summary>
    public string? StartedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Аренда прогона: столько работник может молчать.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
}

/// <summary>Коллизия черновика: строка на коллизию последнего удачного прогона части (<c>ClashPart.ResultRunId</c>).</summary>
public sealed class ClashDraftHit
{
    public Guid RunId { get; set; }
    public int Number { get; set; }
    public Guid PartId { get; set; }

    /// <summary>Разделы сторон по алфавиту через « × » — для сводки и фильтра.</summary>
    public string DisciplinePair { get; set; } = "";

    /// <summary>Ключи проверок (клеток матрицы) коллизии; пусто — строка прогона до появления групп.</summary>
    public string[] Checks { get; set; } = [];

    public string ModelA { get; set; } = "";
    public string UniqueIdA { get; set; } = "";
    public long ElementIdA { get; set; }
    public string DisciplineA { get; set; } = "";
    public Guid[] SetsA { get; set; } = [];
    public string CategoryA { get; set; } = "";
    public string? FamilyA { get; set; }
    public string? TypeA { get; set; }
    public string? LevelA { get; set; }
    public string ModelB { get; set; } = "";
    public string UniqueIdB { get; set; } = "";
    public long ElementIdB { get; set; }
    public string DisciplineB { get; set; } = "";
    public Guid[] SetsB { get; set; } = [];
    public string CategoryB { get; set; } = "";
    public string? FamilyB { get; set; }
    public string? TypeB { get; set; }
    public string? LevelB { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public float DepthMm { get; set; }

    public static string PairOf(string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? $"{a} × {b}" : $"{b} × {a}";
}

/// <param name="QueuePosition">Место в очереди движка, с 1 — только у прогона в очереди.</param>
/// <param name="TypicalSeconds">
///     Обычная длительность: среднее последних 5 удачных прогонов части того же вида — у прогона в очереди или идущего; без истории —
///     <see langword="null" />.
/// </param>
public sealed record DraftRunSummary(
    Guid RunId,
    EngineRunState State,
    string? Stage,
    int? HitCount,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    EngineRunStats? Stats,
    int? QueuePosition = null,
    double? TypicalSeconds = null)
{
    private const int TypicalOf = 5;

    public static DraftRunSummary Of(EngineRun run) => new(run.Id, run.State, run.Stage, run.HitCount, run.Error, run.CreatedAt, run.FinishedAt, run.Stats);

    /// <summary>Как <see cref="Of" />, а у прогона в очереди или идущего — ещё место в очереди и обычная длительность.</summary>
    public static async Task<DraftRunSummary> DescribeAsync(EngineRun run, ToadFlowDb db, CancellationToken ct)
    {
        var summary = Of(run);
        if (run.State is not (EngineRunState.Queued or EngineRunState.Running)) return summary;

        // Очередь движка — по времени постановки, как выдача работнику (ORDER BY created_at).
        int? position = run.State == EngineRunState.Queued
            ? await db.Set<EngineRun>().CountAsync(r => r.State == EngineRunState.Queued && r.CreatedAt < run.CreatedAt, ct) + 1
            : null;
        var durations = await db.Set<EngineRun>().AsNoTracking()
            .Where(r => r.PartId == run.PartId && r.Kind == run.Kind && r.State == EngineRunState.Succeeded && r.StartedAt != null && r.FinishedAt != null)
            .OrderByDescending(r => r.FinishedAt).Take(TypicalOf)
            .Select(r => new { r.StartedAt, r.FinishedAt }).ToListAsync(ct);
        double? typical = durations.Count == 0 ? null : Math.Round(durations.Average(d => (d.FinishedAt!.Value - d.StartedAt!.Value).TotalSeconds));
        return summary with { QueuePosition = position, TypicalSeconds = typical };
    }
}

/// <summary>Состояние или ход прогона черновика части изменились — странице части пора обновиться. В журнал не пишется.</summary>
public sealed record ClashDraftChanged(ToadFlow.Server.Events.Actor Actor, Guid PartId) : ToadFlow.Server.Events.DomainEvent(Actor)
{
    public override string UiEntity => "clash-parts";
    public override string UiId => PartId.ToString();
}

/// <summary>
///     Ход прогона части (стадия, аренда) — странице нужно перечитать только состояние прогона, не списки. Сигнал раз в несколько
///     секунд на каждый идущий прогон. В журнал не пишется.
/// </summary>
public sealed record ClashRunProgress(ToadFlow.Server.Events.Actor Actor, Guid PartId) : ToadFlow.Server.Events.DomainEvent(Actor)
{
    public override string UiEntity => "clash-run-progress";
    public override string UiId => PartId.ToString();
}
