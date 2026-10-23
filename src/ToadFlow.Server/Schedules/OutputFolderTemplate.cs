using System.Text.RegularExpressions;

namespace ToadFlow.Server.Schedules;

/// <summary>Папка результатов расписания с подстановками <c>{дата}</c>, <c>{время}</c>, <c>{расписание}</c>.</summary>
public static partial class OutputFolderTemplate
{
    private static readonly string[] Known = ["{дата}", "{время}", "{расписание}"];

    // Недопустимые в имени папки Windows символы — без разделителей пути: шаблон задаёт путь целиком.
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <param name="runStart">Начало запуска; дата и время берутся по часовому поясу настроек.</param>
    public static string Render(string template, DateTimeOffset runStart, TimeZoneInfo timeZone, string scheduleName)
    {
        var local = TimeZoneInfo.ConvertTime(runStart, timeZone);
        return template
            .Replace("{дата}", local.ToString("yyyy-MM-dd"))
            .Replace("{время}", local.ToString("HH-mm"))
            .Replace("{расписание}", Sanitize(scheduleName));
    }

    /// <summary>Ошибка шаблона для пользователя; null — шаблон в порядке.</summary>
    public static string? Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "Укажите папку результатов";

        var unknown = Token().Matches(template).Select(m => m.Value).Where(token => !Known.Contains(token)).ToList();
        return unknown.Count == 0
            ? null
            : $"Неизвестная подстановка {string.Join(", ", unknown)} — доступны {string.Join(", ", Known)}";
    }

    private static string Sanitize(string name) =>
        new string(name.Select(c => Invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Token();
}
