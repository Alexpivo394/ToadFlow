using ToadFlow.Contracts.Clash;

namespace ToadFlow.Server.Clash.Engine;

public enum EngineRunState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
///     Прогон черновика части в движке. Снимок конфигурации и слепков берётся при «Прогнать»: правка черновика во время
///     прогона на него не влияет. Работник держит прогон арендой; без хода аренда истекает, и прогон возвращается в очередь.
/// </summary>
public sealed class EngineRun
{
    public Guid Id { get; set; }
    public Guid PartId { get; set; }
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

public sealed record DraftRunSummary(
    Guid RunId,
    EngineRunState State,
    string? Stage,
    int? HitCount,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    EngineRunStats? Stats)
{
    public static DraftRunSummary Of(EngineRun run) => new(run.Id, run.State, run.Stage, run.HitCount, run.Error, run.CreatedAt, run.FinishedAt, run.Stats);
}
