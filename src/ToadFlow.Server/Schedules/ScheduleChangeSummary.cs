using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Schedules;

/// <summary>Что поменяли в расписании — человеческим языком, для «Истории изменений» и «Событий системы».</summary>
public static class ScheduleChangeSummary
{
    private static readonly string[] Days = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    public static string Describe(Schedule old, Schedule current, string? oldSource, string? newSource)
    {
        var parts = new List<string>();
        Add(parts, "название", old.Name, current.Name);
        Add(parts, "источник", oldSource, newSource);
        Add(parts, "папка результатов", old.OutputFolderTemplate, current.OutputFolderTemplate);
        Add(parts, "повтор", Kind(old), Kind(current));
        if (current.RecurrenceKind == RecurrenceKind.Weekly) Add(parts, "дни", DaysText(old.DaysOfWeekMask), DaysText(current.DaysOfWeekMask));
        if (current.RecurrenceKind == RecurrenceKind.EveryNHours) Add(parts, "интервал", $"{old.IntervalHours} ч", $"{current.IntervalHours} ч");
        if (current.RecurrenceKind == RecurrenceKind.Once) Add(parts, "запуск", old.OnceAt?.ToString("dd.MM.yyyy HH:mm"), current.OnceAt?.ToString("dd.MM.yyyy HH:mm"));
        if (current.RecurrenceKind != RecurrenceKind.Once) Add(parts, "время", old.TimeOfDay.ToString("HH:mm"), current.TimeOfDay.ToString("HH:mm"));
        Add(parts, "приоритет", Priority(old.Priority), Priority(current.Priority));
        Add(parts, "таймаут на модель", $"{old.ModelTimeoutMinutes} мин", $"{current.ModelTimeoutMinutes} мин");
        Add(parts, "повторов упавших", old.MaxRetries.ToString(), current.MaxRetries.ToString());
        Add(parts, "моделей и папок", Count(old.Selection), Count(current.Selection));
        if (old.ModuleSettingsJson != current.ModuleSettingsJson) parts.Add("изменены параметры задачи");
        if (old.IsEnabled != current.IsEnabled) parts.Add(current.IsEnabled ? "включено" : "выключено");

        return parts.Count == 0 ? "без изменений" : string.Join("; ", parts);
    }

    public static string DaysText(int mask)
    {
        var days = Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).ToList();
        if (days.Count == 0) return "—";
        if (days.Count == 7) return "ежедневно";
        if (days.SequenceEqual([0, 1, 2, 3, 4])) return "Пн–Пт";
        return string.Join(", ", days.Select(i => Days[i]));
    }

    private static string Kind(Schedule schedule) => schedule.RecurrenceKind switch
    {
        RecurrenceKind.Weekly => "по дням недели",
        RecurrenceKind.EveryNHours => "каждые N часов",
        _ => "один раз"
    };

    private static string Priority(int priority) => priority switch { > 0 => "высокий", < 0 => "низкий", _ => "обычный" };

    private static string Count(SelectionDto selection) => (selection.Models.Length + selection.Folders.Length).ToString();

    private static void Add(List<string> parts, string what, string? before, string? after)
    {
        if (before != after) parts.Add($"{what} {before ?? "—"} → {after ?? "—"}");
    }
}
