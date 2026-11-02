using System.Globalization;
using Alexpivo394.Revit.Clash.Rules;

namespace ToadFlow.Server.Clash.Parts;

public enum ConfigChangeKind
{
    SetAdded,
    SetRemoved,
    SetRenamed,
    SetConditions,
    CellEnabled,
    CellDisabled,
    CellTolerance,
    ExclusionAdded,
    ExclusionRemoved,
    GroupAdded,
    GroupRemoved,
    GroupSource
}

/// <param name="Text">Готовая строка для человека: «Клетка АР × КР: допуск 20 → 50 мм».</param>
public sealed record ConfigChange(ConfigChangeKind Kind, string Text);

/// <summary>
///     Отличия двух конфигураций части: группы и наборы — по Id, клетки — по паре групп, исключения — по паре наборов (порядок в паре
///     не важен). Порядок списка: группы, наборы, клетки, исключения.
/// </summary>
public static class ConfigurationDiff
{
    public static IReadOnlyList<ConfigChange> Compare(ClashConfiguration before, ClashConfiguration after)
    {
        var changes = new List<ConfigChange>();
        Groups(before, after, changes);
        Sets(before, after, changes);
        Cells(before, after, changes);
        Exclusions(before, after, changes);
        return changes;
    }

    private static void Groups(ClashConfiguration before, ClashConfiguration after, List<ConfigChange> changes)
    {
        var old = before.Groups.ToDictionary(g => g.Id);
        foreach (var group in after.Groups)
        {
            if (!old.TryGetValue(group.Id, out var was))
                changes.Add(new ConfigChange(ConfigChangeKind.GroupAdded, $"Группа «{group.Name}» добавлена: {SourceText(group.Source)}"));
            else if (SourceText(was.Source) != SourceText(group.Source))
                changes.Add(new ConfigChange(ConfigChangeKind.GroupSource, $"Группа «{group.Name}»: {SourceText(was.Source)} → {SourceText(group.Source)}"));
        }

        var current = after.Groups.Select(g => g.Id).ToHashSet();
        foreach (var group in before.Groups.Where(g => !current.Contains(g.Id)))
            changes.Add(new ConfigChange(ConfigChangeKind.GroupRemoved, $"Группа «{group.Name}» удалена"));
    }

    private static void Sets(ClashConfiguration before, ClashConfiguration after, List<ConfigChange> changes)
    {
        var old = before.Sets.ToDictionary(s => s.Id);
        foreach (var set in after.Sets)
        {
            var group = GroupName(after, set.GroupId);
            if (!old.TryGetValue(set.Id, out var was))
            {
                changes.Add(new ConfigChange(ConfigChangeKind.SetAdded, $"Набор «{set.Name}» ({group}) добавлен"));
                continue;
            }

            if (was.Name != set.Name)
                changes.Add(new ConfigChange(ConfigChangeKind.SetRenamed, $"Набор «{was.Name}» ({group}) переименован в «{set.Name}»"));
            var (from, to) = (ConditionText.Of(was.AnyOf), ConditionText.Of(set.AnyOf));
            if (from != to) changes.Add(new ConfigChange(ConfigChangeKind.SetConditions, $"Набор «{set.Name}» ({group}): {from} → {to}"));
        }

        var current = after.Sets.Select(s => s.Id).ToHashSet();
        foreach (var set in before.Sets.Where(s => !current.Contains(s.Id)))
            changes.Add(new ConfigChange(ConfigChangeKind.SetRemoved, $"Набор «{set.Name}» ({GroupName(before, set.GroupId)}) удалён"));
    }

    private static void Cells(ClashConfiguration before, ClashConfiguration after, List<ConfigChange> changes)
    {
        foreach (var cell in after.Matrix)
        {
            var name = $"Клетка {GroupName(after, cell.GroupA)} × {GroupName(after, cell.GroupB)}";
            var was = before.CellFor(cell.GroupA, cell.GroupB);
            if (cell.Enabled && was is not { Enabled: true })
                changes.Add(new ConfigChange(ConfigChangeKind.CellEnabled, $"{name} включена, допуск {Mm(cell.ToleranceMm)} мм"));
            else if (!cell.Enabled && was is { Enabled: true })
                changes.Add(new ConfigChange(ConfigChangeKind.CellDisabled, $"{name} выключена"));
            else if (cell.Enabled && was is not null && Math.Abs(was.ToleranceMm - cell.ToleranceMm) > 1e-9)
                changes.Add(new ConfigChange(ConfigChangeKind.CellTolerance, $"{name}: допуск {Mm(was.ToleranceMm)} → {Mm(cell.ToleranceMm)} мм"));
        }

        foreach (var cell in before.Matrix.Where(c => c.Enabled && after.CellFor(c.GroupA, c.GroupB) is null))
            changes.Add(new ConfigChange(ConfigChangeKind.CellDisabled,
                $"Клетка {GroupName(before, cell.GroupA)} × {GroupName(before, cell.GroupB)} выключена"));
    }

    private static void Exclusions(ClashConfiguration before, ClashConfiguration after, List<ConfigChange> changes)
    {
        static (Guid, Guid) Key(SetPairExclusion e) => e.SetA.CompareTo(e.SetB) <= 0 ? (e.SetA, e.SetB) : (e.SetB, e.SetA);
        var old = before.Exclusions.Select(Key).ToHashSet();
        var current = after.Exclusions.Select(Key).ToHashSet();
        foreach (var exclusion in after.Exclusions.Where(e => !old.Contains(Key(e))).DistinctBy(Key))
            changes.Add(new ConfigChange(ConfigChangeKind.ExclusionAdded,
                $"Исключение «{SetName(after, exclusion.SetA)}» × «{SetName(after, exclusion.SetB)}» добавлено"));
        foreach (var exclusion in before.Exclusions.Where(e => !current.Contains(Key(e))).DistinctBy(Key))
            changes.Add(new ConfigChange(ConfigChangeKind.ExclusionRemoved,
                $"Исключение «{SetName(before, exclusion.SetA)}» × «{SetName(before, exclusion.SetB)}» удалено"));
    }

    private static string GroupName(ClashConfiguration config, Guid id) => config.Groups.FirstOrDefault(g => g.Id == id)?.Name ?? "—";

    private static string SetName(ClashConfiguration config, Guid id) => config.Sets.FirstOrDefault(s => s.Id == id)?.Name ?? "—";

    private static string Mm(double value) => value.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));

    private static string SourceText(GroupSource source)
    {
        var parts = new List<string>();
        if (source.Disciplines.Count > 0) parts.Add("разделы " + string.Join(", ", source.Disciplines));
        if (source.ModelKeys.Count > 0) parts.Add("моделей: " + source.ModelKeys.Count);
        return parts.Count == 0 ? "без источника" : string.Join("; ", parts);
    }
}

/// <summary>Условие набора одной строкой: условия группы — через «и», группы — через «или».</summary>
public static class ConditionText
{
    public static string Of(IReadOnlyList<ConditionGroup> anyOf) =>
        anyOf.Count == 0 ? "без условий" : string.Join(" или ", anyOf.Select(g => anyOf.Count > 1 && g.All.Count > 1 ? $"({Of(g)})" : Of(g)));

    public static string Of(ConditionGroup group) => group.All.Count == 0 ? "все элементы" : string.Join(" и ", group.All.Select(Of));

    public static string Of(Condition condition)
    {
        var property = condition.Property.Kind switch
        {
            PropertyKind.Category => "Категория",
            PropertyKind.Family => "Семейство",
            PropertyKind.Type => "Тип",
            PropertyKind.Level => "Уровень",
            PropertyKind.Workset => "Рабочий набор",
            _ => condition.Property.ParameterName ?? condition.Property.SharedGuid?.ToString() ?? "Параметр"
        };
        var value = condition.Value ?? "";
        return condition.Operator switch
        {
            ConditionOperator.Equals => $"{property} = {value}",
            ConditionOperator.NotEquals => $"{property} ≠ {value}",
            ConditionOperator.Contains => $"{property} содержит «{value}»",
            ConditionOperator.NotContains => $"{property} не содержит «{value}»",
            ConditionOperator.Greater => $"{property} > {value}",
            ConditionOperator.Less => $"{property} < {value}",
            ConditionOperator.IsEmpty => $"{property} пусто",
            ConditionOperator.IsNotEmpty => $"{property} не пусто",
            ConditionOperator.OneOf => $"{property} — одно из: {string.Join(", ", condition.Values ?? [])}",
            _ => property
        };
    }
}
