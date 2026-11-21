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
public sealed record AttentionItem(AttentionLevel Level, string Text, string Link, string? Module, UserRole MinRole = UserRole.Viewer);

/// <summary>Плитка модуля на «Обзоре»: главное число с подписью, изменение за период, до трёх строк пояснений.</summary>
public sealed record OverviewTile(string Module, string Value, string Label, string? Trend, IReadOnlyList<string> Lines, string Link);

/// <summary>Что модуль показывает на «Обзоре». Регистрируется в <c>AddServices</c> модуля — у выключенного модуля его нет.</summary>
public interface IOverviewSource
{
    /// <summary>Имя модуля (<c>IServerModule.Name</c>).</summary>
    string Module { get; }

    /// <summary>Плитка модуля; null — плитки нет. Исключение — плитка «нет данных», остальной «Обзор» цел.</summary>
    Task<OverviewTile?> TileAsync(IServiceProvider services, CancellationToken ct);

    /// <summary>Свои строки «Требует внимания».</summary>
    Task<IReadOnlyList<AttentionItem>> AttentionAsync(IServiceProvider services, CancellationToken ct) => Task.FromResult<IReadOnlyList<AttentionItem>>([]);
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
