using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Schedules;

/// <param name="DaysOfWeekMask">Бит 0 — понедельник … бит 6 — воскресенье.</param>
public sealed record RecurrenceRule(RecurrenceKind Kind, int DaysOfWeekMask, TimeOnly TimeOfDay, int? IntervalHours, DateTimeOffset? OnceAt)
{
    public static RecurrenceRule Of(Schedule schedule) =>
        new(schedule.RecurrenceKind, schedule.DaysOfWeekMask, schedule.TimeOfDay, schedule.IntervalHours, schedule.OnceAt);
}

/// <summary>Когда запускать расписание. Время считается по местным часам пояса из «Общих», а не в UTC.</summary>
public static class Recurrence
{
    /// <summary>Ближайший запуск строго позже <paramref name="after" />; null — больше запусков не будет.</summary>
    public static DateTimeOffset? Next(RecurrenceRule rule, DateTimeOffset after, TimeZoneInfo timeZone) => rule.Kind switch
    {
        RecurrenceKind.Weekly => NextWeekly(rule, after, timeZone),
        RecurrenceKind.EveryNHours => NextEveryNHours(rule, after, timeZone),
        RecurrenceKind.Once => rule.OnceAt > after ? rule.OnceAt.Value.ToUniversalTime() : null,
        _ => null
    };

    private static DateTimeOffset? NextWeekly(RecurrenceRule rule, DateTimeOffset after, TimeZoneInfo timeZone)
    {
        if ((rule.DaysOfWeekMask & 0b1111111) == 0) return null;

        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, timeZone).DateTime);
        for (var day = 0; day <= 7; day++)
        {
            var date = localDate.AddDays(day);
            if ((rule.DaysOfWeekMask & (1 << MondayIndex(date.DayOfWeek))) == 0) continue;

            var candidate = ToInstant(date.ToDateTime(rule.TimeOfDay), timeZone);
            if (candidate > after) return candidate;
        }

        return null;
    }

    /// <summary>Каждые N часов по местным часам от <see cref="RecurrenceRule.TimeOfDay" />; цепочка начинается заново каждый день.</summary>
    private static DateTimeOffset? NextEveryNHours(RecurrenceRule rule, DateTimeOffset after, TimeZoneInfo timeZone)
    {
        if (rule.IntervalHours is not (> 0 and <= 24) || rule.IntervalHours is not { } hours) return null;

        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, timeZone).DateTime);
        for (var day = -1; day <= 1; day++)
        {
            var anchor = localDate.AddDays(day).ToDateTime(rule.TimeOfDay);
            for (var slot = anchor.AddHours(-24); slot < anchor.AddHours(24); slot = slot.AddHours(hours))
            {
                // Слоты, ушедшие за полночь в обе стороны, принадлежат цепочке соседнего дня — их она и сгенерирует.
                if (slot < anchor.Date || slot >= anchor.Date.AddDays(1)) continue;
                var candidate = ToInstant(slot, timeZone);
                if (candidate > after) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    ///     Местное время → момент. Несуществующее (часы переведены вперёд) — первая минута после разрыва;
    ///     повторяющееся (часы переведены назад) — первое вхождение.
    /// </summary>
    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo timeZone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local)) local = local.AddMinutes(1);

        var offset = timeZone.IsAmbiguousTime(local) ? timeZone.GetAmbiguousTimeOffsets(local).Max() : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static int MondayIndex(DayOfWeek day) => ((int)day + 6) % 7;
}
