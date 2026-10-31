using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Images;

/// <summary>
///     Задание картинок части: нарисовать коллизии, найденные прогоном <see cref="RunId" />, у которых картинки нет или она по
///     устаревшим отпечаткам. Одна строка на часть: сверка утверждённого прогона вставляет её или переключает на свой прогон.
///     Работник держит задание арендой; после <see cref="MaxAttempts" /> неудачных попыток задание снимается.
/// </summary>
public sealed class ClashImageJob
{
    /// <summary>Аренда задания: столько работник может молчать между пачками.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public const int MaxAttempts = 3;

    public Guid PartId { get; set; }
    public Guid RunId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempt { get; set; }

    /// <summary>Последний сбой, о котором сообщил работник.</summary>
    public string? Error { get; set; }
}

/// <summary>Картинки коллизий части не нарисованы за все попытки — в «События системы». Следующий прогон поставит задание снова.</summary>
public sealed record ClashImagesFailed(Actor Actor, Guid PartId, string PartName, string Error) : DomainEvent(Actor)
{
    public override string Describe() =>
        $"Часть {PartName}: картинки коллизий не нарисованы за {ClashImageJob.MaxAttempts} попытки ({Error}). Следующий прогон поставит их снова";

    public override string UiEntity => "clash-parts";
    public override string UiId => PartId.ToString();
}
