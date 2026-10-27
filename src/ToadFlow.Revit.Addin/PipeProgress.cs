using Alexpivo394.Revit.Export.Core;
using Alexpivo394.Revit.ParamCheck.Core;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Revit.Addin;

/// <summary>
///     Ход задания — агенту. Каждый вызов из потока Revit API отмечает «Revit жив» (<see cref="LastActivityAt" />). Во время
///     синхронного вызова API (например, экспорта NWC) отметок нет — тогда агент судит о работе Revit по счётчикам процесса
///     (сторож зависаний агента), а не по этому времени. В пайп уходит только смена шага и строки журнала.
/// </summary>
internal sealed class PipeProgress : IExportProgress, IExportLog, ICheckProgress, ICheckLog
{
    private readonly object _sync = new();
    private readonly HashSet<Guid> _cancelled = [];
    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    private Guid _jobId;
    private string? _stage;
    private volatile bool _abort;

    /// <summary>Куда отправлять сообщения; задаёт клиент пайпа.</summary>
    public Action<PipeMessage>? Send { get; set; }

    public DateTimeOffset LastActivityAt => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public bool ShouldAbort => _abort;

    public void Begin(Guid jobId)
    {
        lock (_sync)
        {
            _jobId = jobId;
            _stage = null;
            // Отмена могла прийти, пока задание ждало потока Revit: тогда оно бросается сразу, не начавшись.
            _abort = _cancelled.Remove(jobId);
        }

        Touch();
    }

    /// <summary>Попросить бросить задание — текущее или ещё ждущее своей очереди в потоке Revit.</summary>
    public void RequestAbort(Guid jobId)
    {
        lock (_sync)
        {
            if (jobId == _jobId) _abort = true;
            else _cancelled.Add(jobId);
        }
    }

    public void Status(string? status)
    {
        Touch();
        if (status is null || status == _stage) return;
        _stage = status;
        Post(status, status);
    }

    public void Report(int current, int total, string? status) => Touch();

    public void Step(string model, string text)
    {
        Touch();
        Post(_stage ?? "Журнал", text);
    }

    public void Error(string model, string text, Exception exception)
    {
        Touch();
        Post(_stage ?? "Журнал", $"{text}: {exception}");
    }

    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);

    private void Post(string stage, string text) =>
        Send?.Invoke(new ProgressMessage { JobId = _jobId, Stage = stage, Text = text, At = DateTimeOffset.UtcNow });
}
