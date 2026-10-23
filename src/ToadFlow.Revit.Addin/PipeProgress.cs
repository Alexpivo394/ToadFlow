using Alexpivo394.Revit.Export.Core;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Revit.Addin;

/// <summary>
///     Ход выгрузки — агенту. Каждый вызов из потока Revit API отмечает «Revit жив» (<see cref="LastActivityAt" />):
///     по этому времени агент отличает долгую операцию от зависания. В пайп уходит только смена шага и строки журнала.
/// </summary>
internal sealed class PipeProgress : IExportProgress, IExportLog
{
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
        _jobId = jobId;
        _stage = null;
        _abort = false;
        Touch();
    }

    public void RequestAbort(Guid jobId)
    {
        if (jobId == _jobId) _abort = true;
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

    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);

    private void Post(string stage, string text) =>
        Send?.Invoke(new ProgressMessage { JobId = _jobId, Stage = stage, Text = text, At = DateTimeOffset.UtcNow });
}
