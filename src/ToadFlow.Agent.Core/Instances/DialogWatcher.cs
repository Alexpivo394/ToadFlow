using Microsoft.Extensions.Logging;

namespace ToadFlow.Agent.Instances;

/// <summary>Что случилось с окном Revit за шаг наблюдения.</summary>
public enum DialogOutcome
{
    /// <summary>Агент ответил, и окно закрылось.</summary>
    Closed,

    /// <summary>Агент ответил, но окно осталось.</summary>
    NotClosed,

    /// <summary>Окно ждёт человека: агент на него не отвечает.</summary>
    NeedsPerson,

    /// <summary>Окно означает сбой экземпляра.</summary>
    Fatal
}

/// <param name="Window">Окно для журналов: <see cref="WindowSnapshot.Describe" />.</param>
public sealed record DialogEvent(DialogOutcome Outcome, DialogKind Kind, string Window);

/// <summary>
///     Окна-диалоги одного экземпляра Revit — от запуска процесса до его выхода: при запуске их проверяет
///     <see cref="InstanceManager" />, во время задания — <see cref="Jobs.JobRunner" />. Каждое новое окно читается один раз,
///     распознаётся по <see cref="DialogRules" />, и агент отвечает только на известные окна, проверяя, что окно закрылось.
///     Шаг не бросает исключений: сбой UI Automation или Win32 пишется в журнал агента, и следующий шаг смотрит заново.
/// </summary>
public sealed class DialogWatcher(IRevitWindows windows, int year, int pid, TimeProvider time, ILogger logger)
{
    /// <summary>Сколько окно может ждать ответа, которого агент не даст, прежде чем экземпляр считается остановившимся.</summary>
    public static readonly TimeSpan AnswerGrace = TimeSpan.FromMinutes(1);

    /// <summary>Сколько раз отвечать окну, которое не закрывается.</summary>
    public const int MaxAttempts = 3;

    private readonly object _sync = new();
    private readonly Dictionary<nint, Tracked> _open = [];
    private string? _lastError;

    /// <summary>Окна, открытые на последнем шаге, — для причины сбоя.</summary>
    public IReadOnlyList<string> Open
    {
        get
        {
            lock (_sync) return _open.Values.Select(tracked => tracked.Snapshot.Describe()).ToList();
        }
    }

    /// <summary>Окно, которое ждёт ответа дольше <see cref="AnswerGrace" />, а агент ответить не может; null — такого нет.</summary>
    public WindowSnapshot? Blocking
    {
        get
        {
            var now = time.GetUtcNow();
            lock (_sync)
                return _open.Values.Where(tracked => tracked.WaitsForPerson && now - tracked.Since >= AnswerGrace).MinBy(tracked => tracked.Since)?.Snapshot;
        }
    }

    /// <summary>Один шаг: найти окна, ответить на известные. Синхронный и медленный (UI Automation) — вызывать не в потоке цикла.</summary>
    public IReadOnlyList<DialogEvent> Check()
    {
        lock (_sync)
        {
            var events = new List<DialogEvent>();
            try
            {
                var present = windows.Dialogs();
                foreach (var gone in _open.Keys.Except(present).ToList())
                {
                    if (_open[gone].WaitsForPerson) logger.LogInformation("Revit {Year} (PID {Pid}): окно {Window} закрылось", year, pid, _open[gone].Snapshot.Describe());
                    _open.Remove(gone);
                }

                foreach (var window in present) Step(window, events);
                if (_lastError is not null) logger.LogInformation("Revit {Year} (PID {Pid}): окна снова читаются", year, pid);
                _lastError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // UI Automation и Win32 бросают разное, пока окно создаётся, закрывается или Revit занят; шаг повторится.
                var error = $"{exception.GetType().Name}: {exception.Message}";
                if (error != _lastError) logger.LogWarning("Revit {Year} (PID {Pid}): окна не читаются — {Error}", year, pid, error);
                _lastError = error;
            }

            return events;
        }
    }

    private void Step(nint window, List<DialogEvent> events)
    {
        if (!_open.TryGetValue(window, out var tracked))
        {
            tracked = new Tracked(Read(window), time.GetUtcNow());
            _open[window] = tracked;
            logger.LogInformation("Revit {Year} (PID {Pid}): окно {Window} — {Kind}", year, pid, tracked.Snapshot.Describe(), DialogRules.Describe(tracked.Kind));
        }
        else if (!tracked.Snapshot.Readable)
        {
            tracked.Reread(Read(window));
            if (tracked.Snapshot.Readable)
                logger.LogInformation("Revit {Year} (PID {Pid}): окно {Window} — {Kind}", year, pid, tracked.Snapshot.Describe(), DialogRules.Describe(tracked.Kind));
        }

        var description = tracked.Snapshot.Describe();
        var (response, action) = DialogRules.Respond(tracked.Kind);
        switch (response)
        {
            case DialogResponse.Answer when tracked.Attempts < MaxAttempts:
                tracked.Attempts++;
                if (Close(tracked.Snapshot, action))
                {
                    logger.LogInformation("Revit {Year} (PID {Pid}): агент ответил на окно {Window} ({Action}), окно закрылось", year, pid, description, Describe(action));
                    events.Add(new DialogEvent(DialogOutcome.Closed, tracked.Kind, description));
                    _open.Remove(window);
                    return;
                }

                logger.LogWarning("Revit {Year} (PID {Pid}): агент ответил на окно {Window} ({Action}), но окно осталось — попытка {Attempt} из {Max}", year, pid,
                    description, Describe(action), tracked.Attempts, MaxAttempts);
                events.Add(new DialogEvent(DialogOutcome.NotClosed, tracked.Kind, description));
                if (tracked.Attempts == MaxAttempts) WaitForPerson(tracked, events);
                break;
            case DialogResponse.AskPerson:
                WaitForPerson(tracked, events);
                break;
            case DialogResponse.Fail when !tracked.Reported:
                tracked.Reported = true;
                logger.LogWarning("Revit {Year} (PID {Pid}): окно {Window} — {Kind}", year, pid, description, DialogRules.Describe(tracked.Kind));
                events.Add(new DialogEvent(DialogOutcome.Fatal, tracked.Kind, description));
                break;
        }
    }

    private void WaitForPerson(Tracked tracked, List<DialogEvent> events)
    {
        if (tracked.WaitsForPerson) return;
        tracked.WaitsForPerson = true;
        tracked.Since = time.GetUtcNow();
        events.Add(new DialogEvent(DialogOutcome.NeedsPerson, tracked.Kind, tracked.Snapshot.Describe()));
    }

    private WindowSnapshot Read(nint window)
    {
        try
        {
            return windows.Read(window);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogInformation("Revit {Year} (PID {Pid}): окно {Handle} пока не читается — {Error}", year, pid, window, exception.Message);
            return new WindowSnapshot(window, "", [], [], false, false);
        }
    }

    private bool Close(WindowSnapshot window, DialogAction action)
    {
        try
        {
            return windows.Close(window, action);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Revit {Year} (PID {Pid}): ответ на окно {Window} не удался — {Error}", year, pid, window.Describe(), exception.Message);
            return false;
        }
    }

    private static string Describe(DialogAction action) => action switch
    {
        DialogAction.AlwaysLoad => "«Всегда загружать»",
        _ => "закрыть"
    };

    private sealed class Tracked(WindowSnapshot snapshot, DateTimeOffset since)
    {
        public WindowSnapshot Snapshot { get; private set; } = snapshot;
        public DialogKind Kind { get; private set; } = DialogRules.Classify(snapshot);
        public DateTimeOffset Since { get; set; } = since;
        public int Attempts { get; set; }
        public bool WaitsForPerson { get; set; }
        public bool Reported { get; set; }

        public void Reread(WindowSnapshot snapshot)
        {
            Snapshot = snapshot;
            Kind = DialogRules.Classify(snapshot);
            if (snapshot.Readable) WaitsForPerson = false;
        }
    }
}
