namespace ToadFlow.Server.Clash.Collisions;

/// <summary>
///     Итог сверки утверждённого прогона. <see cref="New" />, <see cref="Returned" />, <see cref="Fixed" />,
///     <see cref="GoneByRules" />, <see cref="Unchecked" /> — сколько коллизий перешло в этот статус в этом прогоне;
///     <see cref="Active" /> и <see cref="Fake" /> — сколько их у части после прогона.
/// </summary>
/// <param name="Found">Сколько пар нашёл прогон.</param>
/// <param name="ReusedPairs">Пары, взятые из прошлого прогона без точной проверки.</param>
public sealed record ApprovedRunSummary(int Found, int New, int Active, int Returned, int Fixed, int GoneByRules, int Unchecked, int Fake, long ReusedPairs);

/// <summary>
///     Коллизия утверждённого прогона, пока прогон сдаёт результат частями. После последней части строки сверяются с базой
///     коллизий (<see cref="CollisionReconciler" />) и удаляются.
/// </summary>
public sealed class ClashApprovedHit
{
    public Guid RunId { get; set; }
    public int Number { get; set; }
    public Guid PartId { get; set; }
    public string DisciplinePair { get; set; } = "";
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
    public long FingerprintA { get; set; }
    public long FingerprintB { get; set; }
}
