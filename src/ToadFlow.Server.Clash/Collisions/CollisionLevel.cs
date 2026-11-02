using System.Linq.Expressions;

namespace ToadFlow.Server.Clash.Collisions;

/// <summary>
///     Уровень коллизии для группировки отчёта: один уровень, если у сторон он совпадает или есть только у одной; «A / B»,
///     если разный; пустая строка — уровня нет. То же правило, что колонка «Уровень» выгрузок.
/// </summary>
public static class CollisionLevel
{
    /// <summary>Значение фильтра уровня «без уровня».</summary>
    public const string None = "-";

    public static readonly Expression<Func<ClashCollision, string>> Key = c =>
        c.LevelA == c.LevelB || c.LevelB == null ? c.LevelA ?? "" : c.LevelA == null ? c.LevelB : c.LevelA + " / " + c.LevelB;

    /// <summary>Место строки в порядке уровней: индекс «пара␟уровень» в <paramref name="keys" />.</summary>
    public static Expression<Func<ClashCollision, int>> Position(string[] keys) => c =>
        Array.IndexOf(keys, c.DisciplinePair + Separator
                            + (c.LevelA == c.LevelB || c.LevelB == null ? c.LevelA ?? "" : c.LevelA == null ? c.LevelB : c.LevelA + " / " + c.LevelB));

    /// <summary>Место строки в порядке уровней без пары: индекс <see cref="Key" /> в <paramref name="levels" />.</summary>
    public static Expression<Func<ClashCollision, int>> LevelPosition(string[] levels) => c =>
        Array.IndexOf(levels, c.LevelA == c.LevelB || c.LevelB == null ? c.LevelA ?? "" : c.LevelA == null ? c.LevelB : c.LevelA + " / " + c.LevelB);

    /// <summary>Место пары в порядке пар.</summary>
    public static Expression<Func<ClashCollision, int>> PairPosition(string[] pairs) => c => Array.IndexOf(pairs, c.DisciplinePair);

    public const string Separator = "\u001f";

    public static string Composite(string pair, string level) => pair + Separator + level;
}
