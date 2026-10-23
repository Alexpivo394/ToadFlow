using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Schedules;

namespace ToadFlow.Server.Tests;

public class RecurrenceTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private const int Weekdays = 0b0011111; // Пн–Пт
    private const int MonWedFri = 0b0010101;

    private static RecurrenceRule Weekly(int mask, int hour, int minute = 0) => new(RecurrenceKind.Weekly, mask, new TimeOnly(hour, minute), null, null);

    [Test]
    public void Recurrence_Weekly_NextDaySameTime() =>
        // Чт 22.10.2026 21:00 МСК → Пт 23.10 20:00 МСК
        Recurrence.Next(Weekly(Weekdays, 20), DateTimeOffset.Parse("2026-10-22T18:00:00Z"), Moscow)
            .ShouldBe(DateTimeOffset.Parse("2026-10-23T17:00:00Z"));

    [Test]
    public void Recurrence_Weekly_SameDayLater() =>
        Recurrence.Next(Weekly(Weekdays, 20), DateTimeOffset.Parse("2026-10-23T10:00:00Z"), Moscow)
            .ShouldBe(DateTimeOffset.Parse("2026-10-23T17:00:00Z"));

    [Test]
    public void Recurrence_Weekly_SkipsUncheckedDays() =>
        // Пт 23.10 после 20:00 → следующий отмеченный день — Пн 26.10 (Сб, Вс не отмечены)
        Recurrence.Next(Weekly(MonWedFri, 20), DateTimeOffset.Parse("2026-10-23T18:00:00Z"), Moscow)
            .ShouldBe(DateTimeOffset.Parse("2026-10-26T17:00:00Z"));

    [Test]
    public void Recurrence_Weekly_LocalTimeNotUtc() =>
        Recurrence.Next(Weekly(0b1111111, 20), DateTimeOffset.Parse("2026-10-23T16:59:00Z"), Moscow)
            .ShouldBe(DateTimeOffset.Parse("2026-10-23T17:00:00Z"));

    [Test]
    public void Recurrence_Weekly_StrictlyAfter() =>
        Recurrence.Next(Weekly(0b1111111, 20), DateTimeOffset.Parse("2026-10-23T17:00:00Z"), Moscow)
            .ShouldBe(DateTimeOffset.Parse("2026-10-24T17:00:00Z"));

    [Test]
    public void Recurrence_SpringForwardGap() =>
        // 08.03.2026 в Нью-Йорке 02:00–03:00 не существует: 02:30 → 03:00 EDT = 07:00Z
        Recurrence.Next(Weekly(0b1111111, 2, 30), DateTimeOffset.Parse("2026-03-08T05:00:00Z"), NewYork)
            .ShouldBe(DateTimeOffset.Parse("2026-03-08T07:00:00Z"));

    [Test]
    public void Recurrence_FallBackOverlap_FirstOccurrence() =>
        // 01.11.2026 01:30 в Нью-Йорке бывает дважды; первое — по EDT (−4) = 05:30Z
        Recurrence.Next(Weekly(0b1111111, 1, 30), DateTimeOffset.Parse("2026-11-01T03:00:00Z"), NewYork)
            .ShouldBe(DateTimeOffset.Parse("2026-11-01T05:30:00Z"));

    [Test]
    public void Recurrence_EveryNHours() =>
        // Каждые 4 ч от 06:00: после 13:10 МСК → 14:00 МСК
        Recurrence.Next(new RecurrenceRule(RecurrenceKind.EveryNHours, 0, new TimeOnly(6, 0), 4, null),
                DateTimeOffset.Parse("2026-10-23T10:10:00Z"), Moscow)
            .ShouldBe(DateTimeOffset.Parse("2026-10-23T11:00:00Z"));

    [Test]
    public void Recurrence_EveryNHours_CrossesMidnight() =>
        // Каждые 6 ч от 06:00 → слоты 00, 06, 12, 18: после 23:00 МСК → 00:00 следующего дня
        Recurrence.Next(new RecurrenceRule(RecurrenceKind.EveryNHours, 0, new TimeOnly(6, 0), 6, null),
                DateTimeOffset.Parse("2026-10-23T20:00:00Z"), Moscow)
            .ShouldBe(DateTimeOffset.Parse("2026-10-23T21:00:00Z"));

    [Test]
    public void Recurrence_Once_Future() =>
        Recurrence.Next(new RecurrenceRule(RecurrenceKind.Once, 0, default, null, DateTimeOffset.Parse("2026-10-24T03:00:00Z")),
                DateTimeOffset.Parse("2026-10-23T10:00:00Z"), Moscow)
            .ShouldBe(DateTimeOffset.Parse("2026-10-24T03:00:00Z"));

    [Test]
    public void Recurrence_Once_PastIsNull() =>
        Recurrence.Next(new RecurrenceRule(RecurrenceKind.Once, 0, default, null, DateTimeOffset.Parse("2026-10-20T03:00:00Z")),
                DateTimeOffset.Parse("2026-10-23T10:00:00Z"), Moscow)
            .ShouldBeNull();

    [Test]
    public void Recurrence_Weekly_NoDays_Null() =>
        Recurrence.Next(Weekly(0, 20), DateTimeOffset.Parse("2026-10-23T10:00:00Z"), Moscow).ShouldBeNull();
}
