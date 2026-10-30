namespace ToadFlow.Server.Clash.Analytics;

/// <summary>
///     Снимок коллизий части по разделу после утверждённого прогона — ряды сводки по времени. Пишет сверка в своей транзакции;
///     задним числом не восстанавливается.
/// </summary>
public sealed class ClashCollisionStat
{
    /// <summary>Итог части без разбивки: каждая коллизия один раз.</summary>
    public const string Total = "";

    public Guid RunId { get; set; }
    public Guid PartId { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>Раздел; коллизия пары разделов считается за каждый раздел пары, коллизия внутри раздела — один раз. <see cref="Total" /> — итог.</summary>
    public string Discipline { get; set; } = "";

    /// <summary>Открытые у части после прогона.</summary>
    public int Open { get; set; }

    /// <summary>Фиктивные у части после прогона.</summary>
    public int Fake { get; set; }

    /// <summary>Переходы этого прогона: найдены впервые, исправлены, вернулись, ушли из-за правил.</summary>
    public int New { get; set; }

    public int Fixed { get; set; }
    public int Returned { get; set; }
    public int GoneByRules { get; set; }
}
