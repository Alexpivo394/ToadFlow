using System.Threading.Channels;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Agent.Instances;

/// <summary>
///     Один экземпляр Revit в слоте агента. Сообщения из пайпа разбирает сам: hello и alive обновляют состояние,
///     шаги и результат текущего задания уходят исполнителю в <see cref="JobEvents" />.
/// </summary>
public sealed class RevitInstance
{
    private readonly TaskCompletionSource<HelloMessage> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private Channel<PipeMessage> _jobEvents = Channel.CreateUnbounded<PipeMessage>();

    internal RevitInstance(int slot, int year, TimeProvider time)
    {
        Slot = slot;
        Year = year;
        _time = time;
        LastActivityAt = time.GetUtcNow();
    }

    public Guid InstanceId { get; } = Guid.NewGuid();
    public int Slot { get; }
    public int Year { get; }
    public InstanceState State { get; internal set; } = InstanceState.Starting;
    public IRevitProcess Process { get; private set; } = null!;
    public bool HasNwcExporter { get; private set; }
    public Guid? JobId { get; private set; }
    public string? ModelName { get; private set; }
    public string? Stage { get; private set; }
    public DateTimeOffset? StageSince { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public int ModelsDone { get; internal set; }
    public DateTimeOffset IdleSince { get; internal set; }

    /// <summary>Почему агент убил экземпляр; null — не убивал (Revit закрылся сам).</summary>
    public string? KillReason { get; internal set; }

    internal PipeChannel Pipe { get; private set; } = null!;
    internal Task<HelloMessage> Hello => _hello.Task;

    /// <summary>Завершается, когда процесс закрылся или аддин отключился от пайпа.</summary>
    internal Task Closed { get; private set; } = Task.CompletedTask;

    internal ChannelReader<PipeMessage> JobEvents
    {
        get
        {
            lock (_sync) return _jobEvents.Reader;
        }
    }

    internal void Attach(IRevitProcess process, PipeChannel pipe)
    {
        Process = process;
        Pipe = pipe;
        Closed = Task.WhenAny(process.Exited, pipe.Completion);
        _ = Task.Run(PumpAsync);
    }

    /// <summary>
    ///     Задание ждёт, пока этот экземпляр запустится. Heartbeat сообщает его серверу — тот продлевает аренду; иначе
    ///     через две минуты запуска Revit задание считалось бы брошенным и уходило в очередь заново.
    /// </summary>
    internal void Reserve(JobAssignmentDto assignment)
    {
        lock (_sync)
        {
            JobId = assignment.JobId;
            ModelName = assignment.ModelName;
            SetStage("Запуск Revit");
        }
    }

    internal void BeginJob(JobAssignmentDto assignment)
    {
        lock (_sync)
        {
            _jobEvents = Channel.CreateUnbounded<PipeMessage>();
            JobId = assignment.JobId;
            ModelName = assignment.ModelName;
            State = InstanceState.Busy;
            SetStage("Начало работы");
            Touch();
        }
    }

    internal void EndJob()
    {
        lock (_sync)
        {
            JobId = null;
            ModelName = null;
            Stage = null;
            StageSince = null;
        }
    }

    internal void SetStage(string stage)
    {
        if (Stage == stage) return;
        Stage = stage;
        StageSince = _time.GetUtcNow();
    }

    internal void Touch() => LastActivityAt = _time.GetUtcNow();

    /// <summary>Для дашборда: «не отвечает» с момента последней активности.</summary>
    internal void MarkUnresponsive()
    {
        State = InstanceState.Unresponsive;
        StageSince = LastActivityAt;
    }

    internal void MarkResponsive()
    {
        if (State == InstanceState.Unresponsive) State = InstanceState.Busy;
    }

    public InstanceStatusDto ToStatus() =>
        new(Slot, Year, Process?.Pid, State, JobId, Stage, StageSince);

    private async Task PumpAsync()
    {
        await foreach (var message in Pipe.Incoming.ReadAllAsync())
        {
            switch (message)
            {
                case HelloMessage hello:
                    HasNwcExporter = hello.HasNwcExporter;
                    Touch();
                    _hello.TrySetResult(hello);
                    break;
                case AliveMessage alive:
                    // Часы аддина и агента одни (одна машина), но не позже «сейчас» агента.
                    var at = alive.LastActivityAt < _time.GetUtcNow() ? alive.LastActivityAt : _time.GetUtcNow();
                    if (at > LastActivityAt) LastActivityAt = at;
                    break;
                case ProgressMessage or ResultMessage:
                    lock (_sync) _jobEvents.Writer.TryWrite(message);
                    break;
            }
        }

        _hello.TrySetException(new IOException("Аддин отключился, не поздоровавшись"));
    }
}
