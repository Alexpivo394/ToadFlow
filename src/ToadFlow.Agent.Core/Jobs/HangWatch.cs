using ToadFlow.Agent.Instances;

namespace ToadFlow.Agent.Jobs;

/// <summary>Что сторож думает о Revit в эту секунду.</summary>
public enum HangVerdict
{
    /// <summary>Аддин недавно сообщал о ходе задания.</summary>
    Active,

    /// <summary>Аддин молчит, но процесс работает: долгий синхронный вызов Revit API (экспорт NWC, сохранение, очистка).</summary>
    WorkingSilently,

    /// <summary>Аддин молчит и процесс простаивает, но лимит молчания ещё не вышел.</summary>
    Quiet,

    /// <summary>Аддин молчит и процесс простаивает дольше лимита — Revit завис.</summary>
    Hung
}

/// <summary>
///     Сторож зависаний одного задания. Различает:
///     <list type="bullet">
///         <item>отсутствие сообщений о ходе — аддин не может их слать, пока поток Revit API занят синхронным вызовом вроде
///         <c>Document.Export(…, NavisworksExportOptions)</c>;</item>
///         <item>долгую операцию — сообщений нет, но процесс тратит процессор или читает и пишет файлы;</item>
///         <item>зависание — нет ни сообщений, ни работы процесса дольше лимита молчания;</item>
///         <item>общий таймаут задания — его проверяет <see cref="JobRunner" />, и он ограничивает даже «работающий» Revit,
///         например застрявший в бесконечном цикле.</item>
///     </list>
///     Работу процесса агент видит сам по счётчикам ОС (<see cref="ProcessSample" />), а не по словам аддина: фоновый поток
///     аддина жив и у зависшего Revit, поэтому подтверждение «жив» от него ничего не доказывает.
/// </summary>
public sealed class HangWatch(TimeSpan silenceLimit)
{
    /// <summary>Как часто снимать счётчики процесса.</summary>
    public static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(5);

    /// <summary>За какое окно усредняется работа процесса: одиночный всплеск фоновых потоков работой не считается.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Работа — не меньше этой доли одного ядра в среднем за окно. Простаивающий или ждущий диалога Revit — около нуля.</summary>
    public const double BusyCpuShare = 0.05;

    /// <summary>Или не меньше стольких байт ввода-вывода в секунду в среднем за окно.</summary>
    public const double BusyIoBytesPerSecond = 256 * 1024;

    private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(10);

    private readonly Queue<(DateTimeOffset At, ProcessSample Sample)> _samples = new();
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;

    public TimeSpan SilenceLimit { get; } = silenceLimit;

    /// <summary>Когда процесс в последний раз работал по счётчикам; null — не видели или счётчиков нет.</summary>
    public DateTimeOffset? LastWorkAt { get; private set; }

    /// <summary>Доля ядра за последнее окно.</summary>
    public double CpuShare { get; private set; }

    /// <summary>Ввод-вывод за последнее окно, байт в секунду.</summary>
    public double IoBytesPerSecond { get; private set; }

    /// <summary>Наибольшая занятая память процесса за задание.</summary>
    public long PeakWorkingSetMb { get; private set; }

    public long PeakPrivateMb { get; private set; }

    public bool SampleDue(DateTimeOffset now) => now - _lastSampleAt >= SampleEvery;

    public void Observe(DateTimeOffset now, ProcessSample? sample)
    {
        _lastSampleAt = now;
        if (sample is not { } current) return;

        PeakWorkingSetMb = Math.Max(PeakWorkingSetMb, current.WorkingSetMb);
        PeakPrivateMb = Math.Max(PeakPrivateMb, current.PrivateMb);
        _samples.Enqueue((now, current));
        while (_samples.Count > 2 && now - _samples.Peek().At > Window) _samples.Dequeue();

        var (firstAt, first) = _samples.Peek();
        var span = now - firstAt;
        if (span < MinSpan) return;

        CpuShare = (current.CpuTime - first.CpuTime) / span;
        IoBytesPerSecond = current.IoBytes >= first.IoBytes ? (current.IoBytes - first.IoBytes) / span.TotalSeconds : 0;
        if (CpuShare >= BusyCpuShare || IoBytesPerSecond >= BusyIoBytesPerSecond) LastWorkAt = now;
    }

    /// <param name="lastActivityAt">Последнее сообщение о ходе задания или отметка «Revit жив» от потока Revit API.</param>
    /// <param name="quietAfter">С какого молчания показывать «не отвечает».</param>
    public HangVerdict Judge(DateTimeOffset now, DateTimeOffset lastActivityAt, TimeSpan quietAfter)
    {
        var silence = now - lastActivityAt;
        if (silence < quietAfter && silence <= SilenceLimit) return HangVerdict.Active;

        // Работа процесса, замеченная после последнего сообщения, сдвигает отсчёт молчания — но только пока она продолжается.
        var lastSign = LastWorkAt is { } work && work > lastActivityAt ? work : lastActivityAt;
        if (now - lastSign > SilenceLimit) return HangVerdict.Hung;
        return LastWorkAt is { } recent && now - recent <= SampleEvery + SampleEvery ? HangVerdict.WorkingSilently : HangVerdict.Quiet;
    }

    /// <summary>Для журнала: чем был занят процесс за последнее окно.</summary>
    public string DescribeActivity() =>
        _samples.Count < 2 ? "счётчики процесса недоступны" : $"процессор {CpuShare * 100:0.#}% ядра, ввод-вывод {IoBytesPerSecond / 1024:0} КБ/с";
}
