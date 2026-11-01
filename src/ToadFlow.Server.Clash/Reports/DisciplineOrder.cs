using System.Globalization;
using ToadFlow.Server.Naming;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>
///     Ранги разделов для порядка отчёта: порядок записей «Раздел» справочника именования (повтор значения берёт первое
///     место), разделы вне справочника — после всех известных, по алфавиту.
/// </summary>
public sealed record DisciplineRanks(IReadOnlyDictionary<string, int> Rank)
{
    private static readonly StringComparer Russian = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), false);

    /// <param name="present">Разделы, которые нужно упорядочить (неизвестные справочнику получают ранги после известных).</param>
    public static DisciplineRanks From(NamingConfig config, IEnumerable<string> present)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in config.Entries.Where(e => e.Field == NamingFields.Discipline))
        {
            var name = (string.IsNullOrWhiteSpace(entry.Value) ? entry.Tokens : entry.Value).Trim();
            if (name.Length > 0) rank.TryAdd(name, rank.Count);
        }

        var known = rank.Count;
        foreach (var (name, index) in present.Select(d => d.Trim()).Where(d => d.Length > 0 && !rank.ContainsKey(d)).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(Russian).Select((d, i) => (d, i)))
            rank[name] = known + index;
        return new DisciplineRanks(rank);
    }

    /// <summary>Ранг раздела; раздел, которого не было при построении, — в самом конце.</summary>
    public int Of(string discipline) => Rank.TryGetValue(discipline.Trim(), out var rank) ? rank : int.MaxValue;

    /// <summary>Разделы пары в порядке справочника; при равном ранге — как в ключе.</summary>
    public (string First, string Second) Ordered(string a, string b) => Of(b) < Of(a) ? (b, a) : (a, b);

    /// <summary>Подпись пары в порядке справочника: <c>ОВ × ВК</c>.</summary>
    public string Label(string a, string b)
    {
        var (first, second) = Ordered(a, b);
        return $"{first} × {second}";
    }

    public (int First, int Second) PairRank(string a, string b)
    {
        var (first, second) = Ordered(a, b);
        return (Of(first), Of(second));
    }

    /// <summary>Разделы пары по её ключу <c>ClashCollision.DisciplinePair</c> («A × B»).</summary>
    public static (string A, string B) Split(string pair)
    {
        var parts = pair.Split(" × ", 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (pair, pair);
    }
}

/// <summary>Порядок разделов из справочника именования в настройках.</summary>
public sealed class DisciplineOrder(NamingStore naming)
{
    public async Task<DisciplineRanks> LoadAsync(IEnumerable<string> present, CancellationToken ct) =>
        DisciplineRanks.From(await naming.GetAsync(ct), present);
}
