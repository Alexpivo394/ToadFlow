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
    GroupSource,
    GroupRenamed,
    ModelAdded,
    ModelRemoved,
    ModelDiscipline
}

/// <param name="Text">Готовая строка для человека: «Клетка АР × КР: допуск 20 → 50 мм».</param>
public sealed record ConfigChange(ConfigChangeKind Kind, string Text);

/// <summary>
///     Отличия двух конфигураций части: группы и наборы — по Id, клетки — по паре групп, исключения — по паре наборов (порядок в паре
///     не важен). Порядок списка: группы, наборы, клетки, исключения.
/// </summary>
public static class ConfigurationDiff
{
    /// <param name="modelNames">Имена моделей по ключу (Id в форме N) — модели источника группы называются по имени; без имени — ключом.</param>
    public static IReadOnlyList<ConfigChange> Compare(ClashConfiguration before, ClashConfiguration after,
        IReadOnlyDictionary<string, string>? modelNames = null)
    {
        var changes = new List<ConfigChange>();
        var names = modelNames ?? new Dictionary<string, string>();
        Groups(before, after, names, changes);
        Sets(before, after, names, changes);
        Cells(before, after, changes);
        Exclusions(before, after, changes);
        return changes;
    }

    /// <summary>Отличия состава части: модели добавлены, убраны, у модели сменился раздел. Порядок — по имени модели.</summary>
    public static IReadOnlyList<ConfigChange> CompareModels(IReadOnlyList<VersionModel> before, IReadOnlyList<VersionModel> after)
    {
        var old = before.ToDictionary(m => m.ModelId);
        var current = after.Select(m => m.ModelId).ToHashSet();
        var changes = new List<(string Name, ConfigChange Change)>();
        foreach (var model in after)
        {
            if (!old.TryGetValue(model.ModelId, out var was))
                changes.Add((model.ModelName, new ConfigChange(ConfigChangeKind.ModelAdded, $"Модель «{model.ModelName}» добавлена")));
            else if (!string.Equals(was.Discipline, model.Discipline, StringComparison.OrdinalIgnoreCase))
                changes.Add((model.ModelName,
                    new ConfigChange(ConfigChangeKind.ModelDiscipline, $"Раздел модели «{model.ModelName}»: {was.Discipline} → {model.Discipline}")));
        }

        foreach (var model in before.Where(m => !current.Contains(m.ModelId)))
            changes.Add((model.ModelName, new ConfigChange(ConfigChangeKind.ModelRemoved, $"Модель «{model.ModelName}» убрана")));
        return changes.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => c.Change).ToList();
    }

    /// <summary>Ключи моделей из источников групп обеих конфигураций — чьи имена нужны <see cref="Compare" />.</summary>
    public static IReadOnlyList<string> ModelKeys(params ClashConfiguration[] configurations) =>
        configurations.SelectMany(c => c.Groups).SelectMany(g => g.Source.ModelKeys)
            .Concat(configurations.SelectMany(c => c.Sets).SelectMany(s => s.AnyOf).SelectMany(g => g.All).SelectMany(ConditionText.ModelKeys))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void Groups(ClashConfiguration before, ClashConfiguration after, IReadOnlyDictionary<string, string> names, List<ConfigChange> changes)
    {
        var old = before.Groups.ToDictionary(g => g.Id);
        foreach (var group in after.Groups)
        {
            if (!old.TryGetValue(group.Id, out var was))
            {
                changes.Add(new ConfigChange(ConfigChangeKind.GroupAdded, $"Группа «{group.Name}» добавлена: {SourceText(group.Source, names)}"));
                continue;
            }

            if (was.Name != group.Name)
                changes.Add(new ConfigChange(ConfigChangeKind.GroupRenamed, $"Группа «{was.Name}» переименована в «{group.Name}»"));
            if (SourceChange(was.Source, group.Source, names) is { } source)
                changes.Add(new ConfigChange(ConfigChangeKind.GroupSource, $"Группа «{group.Name}»: {source}"));
        }

        var current = after.Groups.Select(g => g.Id).ToHashSet();
        foreach (var group in before.Groups.Where(g => !current.Contains(g.Id)))
            changes.Add(new ConfigChange(ConfigChangeKind.GroupRemoved, $"Группа «{group.Name}» удалена"));
    }

    /// <summary>
    ///     Что поменялось в источнике группы: разделы (без учёта регистра и порядка, как их сравнивают правила) и состав моделей по ключу —
    ///     добавленные и убранные модели по имени.
    /// </summary>
    /// <returns>null — источник тот же.</returns>
    private static string? SourceChange(GroupSource was, GroupSource now, IReadOnlyDictionary<string, string> names)
    {
        var parts = new List<string>();
        if (!was.Disciplines.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(now.Disciplines))
            parts.Add($"разделы {DisciplinesText(was)} → {DisciplinesText(now)}");
        var (oldKeys, newKeys) = (was.ModelKeys.ToHashSet(StringComparer.Ordinal), now.ModelKeys.ToHashSet(StringComparer.Ordinal));
        if (now.ModelKeys.Where(k => !oldKeys.Contains(k)).Distinct().ToList() is { Count: > 0 } added)
            parts.Add("модели добавлены: " + ModelsText(added, names));
        if (was.ModelKeys.Where(k => !newKeys.Contains(k)).Distinct().ToList() is { Count: > 0 } removed)
            parts.Add("модели убраны: " + ModelsText(removed, names));
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string DisciplinesText(GroupSource source) => source.Disciplines.Count == 0 ? "нет" : string.Join(", ", source.Disciplines);

    private static string ModelsText(IEnumerable<string> keys, IReadOnlyDictionary<string, string> names) =>
        string.Join(", ", keys.Select(k => $"«{names.GetValueOrDefault(k, k)}»"));

    private static void Sets(ClashConfiguration before, ClashConfiguration after, IReadOnlyDictionary<string, string> names, List<ConfigChange> changes)
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
            var (from, to) = (ConditionText.Of(was.AnyOf, names), ConditionText.Of(set.AnyOf, names));
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

        var oldGroup = before.GroupExclusions.ToHashSet();
        var currentGroup = after.GroupExclusions.ToHashSet();
        foreach (var exclusion in after.GroupExclusions.Where(e => !oldGroup.Contains(e)).Distinct())
            changes.Add(new ConfigChange(ConfigChangeKind.ExclusionAdded,
                $"Исключение «{SetName(after, exclusion.SetId)}» × вся группа «{GroupName(after, exclusion.GroupId)}» добавлено"));
        foreach (var exclusion in before.GroupExclusions.Where(e => !currentGroup.Contains(e)).Distinct())
            changes.Add(new ConfigChange(ConfigChangeKind.ExclusionRemoved,
                $"Исключение «{SetName(before, exclusion.SetId)}» × вся группа «{GroupName(before, exclusion.GroupId)}» удалено"));
    }

    private static string GroupName(ClashConfiguration config, Guid id) => config.Groups.FirstOrDefault(g => g.Id == id)?.Name ?? "—";

    private static string SetName(ClashConfiguration config, Guid id) => config.Sets.FirstOrDefault(s => s.Id == id)?.Name ?? "—";

    private static string Mm(double value) => value.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));

    private static string SourceText(GroupSource source, IReadOnlyDictionary<string, string> names)
    {
        var parts = new List<string>();
        if (source.Disciplines.Count > 0) parts.Add("разделы " + string.Join(", ", source.Disciplines));
        if (source.ModelKeys.Count > 0) parts.Add("модели " + ModelsText(source.ModelKeys.Distinct(), names));
        return parts.Count == 0 ? "без источника" : string.Join("; ", parts);
    }
}

/// <summary>
///     Условие набора одной строкой: условия группы — через «и», группы — через «или». Модель в условии называется по имени из
///     <c>names</c> (ключ — Id модели в форме N), без имени — ключом.
/// </summary>
public static class ConditionText
{
    public static string Of(IReadOnlyList<ConditionGroup> anyOf, IReadOnlyDictionary<string, string>? names = null) =>
        anyOf.Count == 0
            ? "без условий"
            : string.Join(" или ", anyOf.Select(g => anyOf.Count > 1 && g.All.Count > 1 ? $"({Of(g, names)})" : Of(g, names)));

    public static string Of(ConditionGroup group, IReadOnlyDictionary<string, string>? names = null) =>
        group.All.Count == 0 ? "все элементы" : string.Join(" и ", group.All.Select(c => Of(c, names)));

    /// <summary>Ключи моделей, которые называет условие на модель; у других условий — пусто.</summary>
    public static IEnumerable<string> ModelKeys(Condition condition) =>
        condition.Property.Kind != PropertyKind.Model ? [] : (condition.Values ?? []).Append(condition.Value ?? "").Where(k => k.Length > 0);

    public static string Of(Condition condition, IReadOnlyDictionary<string, string>? names = null)
    {
        if (condition.Property.Kind == PropertyKind.Model && names is not null) condition = condition with
        {
            Value = condition.Value is { } key ? Name(key, names) : null,
            Values = condition.Values?.Select(k => Name(k, names)).ToList()
        };
        var property = condition.Property.Kind switch
        {
            PropertyKind.Model => "Модель",
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

    private static string Name(string key, IReadOnlyDictionary<string, string> names) =>
        names.TryGetValue(key, out var name) || names.TryGetValue(key.ToLowerInvariant(), out name) ? name : key;
}
