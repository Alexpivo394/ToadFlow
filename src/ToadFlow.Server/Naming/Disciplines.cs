using System.Globalization;

namespace ToadFlow.Server.Naming;

/// <summary>Разделы, которые модуль знает помимо разбора имён — например, заданные вручную у моделей части коллизий.</summary>
public interface IDisciplineSource
{
    Task<IReadOnlyList<string>> DisciplinesAsync(CancellationToken ct);
}

/// <summary>Список разделов для профиля пользователя: разбор имён моделей и источники модулей.</summary>
public static class Disciplines
{
    private static readonly StringComparer Russian = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), false);

    /// <summary>Без повторов, по алфавиту. Запись разбора имён без значения даёт раздел своими кусками.</summary>
    public static async Task<IReadOnlyList<string>> ListAsync(NamingStore naming, IEnumerable<IDisciplineSource> sources, CancellationToken ct)
    {
        var config = await naming.GetAsync(ct);
        var all = config.Entries.Where(e => e.Field == NamingFields.Discipline)
            .Select(e => string.IsNullOrWhiteSpace(e.Value) ? e.Tokens : e.Value)
            .ToList();
        foreach (var source in sources) all.AddRange(await source.DisciplinesAsync(ct));
        return all.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).Order(Russian).ToList();
    }

    /// <summary>
    ///     Разделы из запроса в написании списка (регистр не важен), без повторов. Неизвестные — в
    ///     <paramref name="unknown" />.
    /// </summary>
    public static string[] Canonical(IReadOnlyList<string> known, IEnumerable<string>? requested, out IReadOnlyList<string> unknown)
    {
        var result = new List<string>();
        var missing = new List<string>();
        foreach (var name in (requested ?? []).Select(d => d?.Trim() ?? "").Where(d => d.Length > 0))
        {
            var match = known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (match is null) missing.Add(name);
            else if (!result.Contains(match, StringComparer.Ordinal)) result.Add(match);
        }

        unknown = missing;
        return result.ToArray();
    }
}
