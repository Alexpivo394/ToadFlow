using ToadFlow.Contracts;

namespace ToadFlow.Server.Overview;

/// <summary>Сбой — что-то сломалось и требует починки; дело — что-то ждёт человека (например, заявка на регистрацию).</summary>
public enum AttentionLevel
{
    Problem,
    Todo
}

/// <summary>Строка блока «Требует внимания».</summary>
/// <param name="Link">Адрес страницы веба, где это делается.</param>
/// <param name="Module">Модуль-источник; null — ядро.</param>
/// <param name="MinRole">С какой роли строка видна: Наблюдатель не видит то, с чем ничего не может сделать.</param>
/// <param name="ResolveKey">Строку можно отметить решённой (<c>POST /api/overview/attention/{ResolveKey}/resolve</c>); null — нельзя, она пропадёт сама.</param>
public sealed record AttentionItem(AttentionLevel Level, string Text, string Link, string? Module, UserRole MinRole = UserRole.Viewer,
    string? ResolveKey = null);

/// <summary>Плитка модуля на «Обзоре»: главное число с подписью, изменение за период, до трёх строк пояснений.</summary>
/// <param name="Series">Ряд для графика: <see cref="OverviewDays.Count" /> значений по дням пояса из настроек, последний — сегодня; null — графика нет.</param>
/// <param name="Highlight">Фраза для шапки о последних сутках («выгружено 48 моделей»), без заглавной и точки; null — сказать нечего.</param>
public sealed record OverviewTile(string Module, string Value, string Label, string? Trend, IReadOnlyList<string> Lines, string Link,
    IReadOnlyList<double>? Series = null, string? Highlight = null);

/// <summary>Что модуль показывает на «Обзоре». Регистрируется в <c>AddServices</c> модуля — у выключенного модуля его нет.</summary>
public interface IOverviewSource
{
    /// <summary>Имя модуля (<c>IServerModule.Name</c>).</summary>
    string Module { get; }

    /// <summary>Плитка модуля; null — плитки нет. Исключение — плитка «нет данных», остальной «Обзор» цел.</summary>
    Task<OverviewTile?> TileAsync(IServiceProvider services, CancellationToken ct);

    /// <summary>Свои строки «Требует внимания».</summary>
    Task<IReadOnlyList<AttentionItem>> AttentionAsync(IServiceProvider services, CancellationToken ct) => Task.FromResult<IReadOnlyList<AttentionItem>>([]);

    /// <summary>Строки «Для вас» — что ждёт именно этого человека.</summary>
    Task<IReadOnlyList<PersonalItem>> PersonalAsync(IServiceProvider services, Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PersonalItem>>([]);
}

/// <summary>Рабочий процесс модуля вне сервера (например, движок коллизий): когда последний раз выходил на связь.</summary>
public interface IWorkerProbe
{
    string Module { get; }

    string Title { get; }

    /// <summary>Последнее обращение; null — с запуска сервера не обращался.</summary>
    DateTimeOffset? LastSeenAt { get; }

    /// <summary>Когда сервер запустился: молчание без единого обращения считается от этого времени.</summary>
    DateTimeOffset StartedAt { get; }
}

/// <summary>Том с данными сервера, место на котором стоит показывать: ядро — папка приложения, модули — свои хранилища.</summary>
public interface IDataVolume
{
    string Name { get; }

    /// <summary>Место на томе; null — прочитать не удалось.</summary>
    VolumeStat? Read();
}

public sealed record VolumeStat(string Name, long FreeBytes, long TotalBytes);

public sealed record WorkerStat(string Module, string Title, DateTimeOffset? LastSeenAt, bool Online);

public sealed record ServerStats(
    string Version,
    DateTimeOffset StartedAt,
    long DatabaseBytes,
    IReadOnlyList<VolumeStat> Volumes,
    long ProcessMemoryBytes,
    IReadOnlyList<WorkerStat> Workers);

public sealed record OverviewView(IReadOnlyList<AttentionItem> Attention, IReadOnlyList<OverviewTile> Tiles, ServerStats Server, OverviewPulse Pulse);

/// <summary>Дни графиков плиток: последние <see cref="Count" /> дней в поясе из настроек, последний — сегодня.</summary>
public static class OverviewDays
{
    public const int Count = 14;

    public static DateOnly DayOf(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>Первый день ряда и момент конца каждого дня (UTC) — для «на конец дня».</summary>
    public static (DateOnly First, DateTimeOffset[] Ends) Of(DateTimeOffset now, TimeZoneInfo zone)
    {
        var first = DayOf(now, zone).AddDays(-(Count - 1));
        return (first, Enumerable.Range(1, Count).Select(i => Start(first.AddDays(i), zone)).ToArray());
    }

    /// <summary>Начало дня в поясе — момент UTC.</summary>
    public static DateTimeOffset Start(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Число по дням ряда: сколько моментов пришлось на каждый день.</summary>
    public static IReadOnlyList<double> CountByDay(IEnumerable<DateTimeOffset> moments, DateTimeOffset now, TimeZoneInfo zone)
    {
        var first = Of(now, zone).First;
        var counts = new double[Count];
        foreach (var at in moments)
        {
            var index = DayOf(at, zone).DayNumber - first.DayNumber;
            if (index is >= 0 and < Count) counts[index]++;
        }

        return counts;
    }
}

/// <summary>Действие — ход за человеком (ответить, принять, взять); сведение — полезно знать.</summary>
public enum PersonalTone
{
    Action,
    Info
}

/// <summary>Строка «Для вас».</summary>
/// <param name="Module">Модуль-источник; null — ядро.</param>
/// <param name="Count">Сколько — крупно рядом с текстом.</param>
/// <param name="ResolveKey">Ошибки модуля можно отметить решёнными тем же «Решено», что в «Требует внимания»; null — нельзя.</param>
public sealed record PersonalItem(string? Module, string Text, string Link, PersonalTone Tone, int Count, string? ResolveKey = null);

/// <summary>Тело «Решено»: почему ошибки больше не проблема.</summary>
public sealed record ResolveRequest(string? Note)
{
    public const int MaxNote = 500;
}

public sealed record PersonalView(IReadOnlyList<PersonalItem> Items);
