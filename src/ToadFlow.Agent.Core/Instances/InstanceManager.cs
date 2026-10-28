using Microsoft.Extensions.Logging;
using ToadFlow.Agent.Revit;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Agent.Instances;

public sealed class RevitStartException(string message) : Exception(message);

/// <summary>
///     Слоты экземпляров Revit (сквозь все версии; сколько их — задают лимиты агента). Экземпляр запускается, когда для него есть
///     задание, живёт между заданиями и закрывается после простоя или планового перезапуска — чтобы не держать лицензию
///     и не копить утечки памяти.
/// </summary>
public sealed class InstanceManager(IProcessLauncher launcher, IReadOnlyList<RevitInstallation> revits, TimeProvider time, ILogger<InstanceManager> logger)
    : IAsyncDisposable
{
    public const int DefaultMaxSlots = 3;
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StartBackoff = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Как часто при запуске Revit проверять его окна-диалоги.</summary>
    private static readonly TimeSpan DialogPoll = TimeSpan.FromSeconds(1);

    private readonly object _sync = new();
    private readonly List<RevitInstance> _instances = [];
    private readonly Dictionary<int, DateTimeOffset> _failedStarts = [];
    private readonly Queue<long> _recentPeaks = new();

    /// <summary>По скольким последним заданиям оценивать память на следующее.</summary>
    public const int RecentPeaks = 5;

    public IReadOnlyList<RevitInstallation> Revits => revits;

    /// <summary>Сколько экземпляров Revit агент держит одновременно — сумма лимитов по версиям из настроек сервера.</summary>
    public int MaxSlots { get; set; } = DefaultMaxSlots;

    public IReadOnlyList<RevitInstance> Instances
    {
        get
        {
            lock (_sync) return _instances.ToList();
        }
    }

    public IReadOnlyList<InstanceStatusDto> Statuses() => Instances.Select(i => i.ToStatus()).ToList();

    /// <summary>Сколько экземпляров этого года сейчас заняты (или запускаются под задание).</summary>
    public int BusyCount(int year) => Instances.Count(i => i.Year == year && i.State != InstanceState.Idle);

    public bool HasFreeSlot(int year)
    {
        lock (_sync) return _instances.Any(i => i.Year == year && i.State == InstanceState.Idle) || _instances.Count < MaxSlots;
    }

    /// <summary>
    ///     Новый Revit этого года сейчас не запустится: недавний запуск не удался. Задания этого года, для которых нет
    ///     свободного экземпляра, брать не надо — иначе каждое сразу упадёт и потеряет попытку.
    /// </summary>
    public bool StartDeferred(int year)
    {
        lock (_sync) return _failedStarts.TryGetValue(year, out var failedAt) && time.GetUtcNow() - failedAt < StartBackoff;
    }

    /// <summary>
    ///     Экземпляр Revit этого года под задание: свободный готовый или новый. Новый готов, когда аддин сообщил, что Revit
    ///     закончил запуск (до 5 минут). Пока Revit запускается, его окна-диалоги проверяет <see cref="DialogWatcher" />:
    ///     известные окна агент закрывает, а окно, которое ждёт человека дольше <see cref="DialogWatcher.AnswerGrace" />,
    ///     и окно ошибки — сразу сбой запуска с текстом окна.
    /// </summary>
    /// <param name="job">Задание, под которое запускается Revit: пока он стартует, heartbeat держит аренду задания.</param>
    /// <param name="abandoned">Задание отменили, пока Revit запускается: запуск прерывается.</param>
    /// <exception cref="RevitStartException">Revit этого года нет, слотов нет, он не запустился или задание отменили.</exception>
    public async Task<RevitInstance> AcquireAsync(int year, CancellationToken ct, JobAssignmentDto? job = null, Func<bool>? abandoned = null)
    {
        RevitInstance instance;
        RevitInstallation revit;
        lock (_sync)
        {
            var idle = _instances.FirstOrDefault(i => i.Year == year && i.State == InstanceState.Idle);
            if (idle is not null)
            {
                idle.State = InstanceState.Busy;
                return idle;
            }

            revit = revits.FirstOrDefault(r => r.Year == year) ?? throw new RevitStartException($"На машине нет Revit {year}");
            if (_failedStarts.TryGetValue(year, out var failedAt) && time.GetUtcNow() - failedAt < StartBackoff)
                throw new RevitStartException($"Revit {year} недавно не запустился — следующая попытка через минуту");

            var slot = Enumerable.Range(1, MaxSlots).Except(_instances.Select(i => i.Slot)).FirstOrDefault();
            if (slot == 0) throw new RevitStartException($"Уже запущено экземпляров Revit: {_instances.Count} — свободного места нет");

            instance = new RevitInstance(slot, year, time);
            if (job is not null) instance.Reserve(job);
            _instances.Add(instance);
        }

        var pipe = PipeChannel.Listen(instance.InstanceId);
        try
        {
            instance.Attach(launcher.Start(year, revit.RevitExe, new Dictionary<string, string> { [PipeNames.InstanceIdVariable] = instance.InstanceId.ToString() }), pipe,
                logger);
        }
        catch (Exception exception) when (exception is not RevitStartException)
        {
            await pipe.DisposeAsync();
            Forget(instance, failedStart: true);
            throw new RevitStartException($"Не удалось запустить Revit {year}: {exception.Message}");
        }

        logger.LogInformation("Revit {Year} запускается в слоте {Slot}, PID {Pid}", year, instance.Slot, instance.Process.Pid);
        using var timeout = new CancellationTokenSource(StartTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var failure = await WaitReadyAsync(instance, abandoned, linked.Token);
        if (failure is not null)
        {
            if (ct.IsCancellationRequested)
            {
                KillCore(instance, "агент остановлен");
                ct.ThrowIfCancellationRequested();
            }

            if (abandoned?.Invoke() == true)
            {
                logger.LogInformation("Revit {Year} в слоте {Slot}, PID {Pid}: задание отменено во время запуска — Revit закрыт", year, instance.Slot, instance.Process.Pid);
                KillCore(instance, "задание отменено во время запуска");
                throw new RevitStartException($"задание отменено, пока запускался Revit {year}");
            }

            var reason = instance.Process.Describe() is { } details ? $"{failure}; {details}" : failure;
            logger.LogWarning("Revit {Year} в слоте {Slot}, PID {Pid}: {Reason}", year, instance.Slot, instance.Process.Pid, reason);
            KillCore(instance, failure);
            lock (_sync) _failedStarts[year] = time.GetUtcNow();
            throw new RevitStartException(reason);
        }

        lock (_sync) _failedStarts.Remove(year);
        instance.State = InstanceState.Busy;
        _ = instance.Closed.ContinueWith(_ => Forget(instance, failedStart: false), TaskScheduler.Default);
        logger.LogInformation("Revit {Year} запущен в слоте {Slot}, PID {Pid}", year, instance.Slot, instance.Process.Pid);
        return instance;
    }

    /// <summary>Задание закончено: экземпляр свободен — или плановый перезапуск после N моделей или по памяти.</summary>
    public void Release(RevitInstance instance, AgentSettingsDto settings)
    {
        instance.ModelsDone++;
        instance.EndJob();
        RecordPeak(instance.PeakWorkingSetMb);
        if (instance.Process.HasExited) return;

        // Аддин не уверен в состоянии Revit (модель не закрылась, неожиданная ошибка) — новых заданий этот процесс не получит.
        if (instance.RecycleReason is { } recycle)
        {
            logger.LogWarning("Перезапуск Revit {Year} в слоте {Slot}, PID {Pid}: {Reason}", instance.Year, instance.Slot, instance.Process.Pid, recycle);
            KillCore(instance, recycle);
            return;
        }

        logger.LogInformation("Revit {Year} в слоте {Slot}, PID {Pid}: после моделей {Models} занимает {Memory} МБ, пик за задание {Peak} МБ",
            instance.Year, instance.Slot, instance.Process.Pid, instance.ModelsDone, instance.Process.WorkingSetMb, instance.PeakWorkingSetMb);
        if (instance.ModelsDone >= settings.RecycleAfterModels || instance.Process.WorkingSetMb > settings.RecycleMemoryMb)
        {
            logger.LogInformation("Плановый перезапуск Revit {Year} в слоте {Slot}: моделей {Models}, памяти {Memory} МБ", instance.Year, instance.Slot,
                instance.ModelsDone, instance.Process.WorkingSetMb);
            KillCore(instance, "плановый перезапуск");
            return;
        }

        instance.State = InstanceState.Idle;
        instance.IdleSince = time.GetUtcNow();
    }

    /// <summary>Убить экземпляр в слоте; null — слот пуст.</summary>
    public RevitInstance? Kill(int slot, string reason)
    {
        var instance = Instances.FirstOrDefault(i => i.Slot == slot);
        if (instance is not null) KillCore(instance, reason);
        return instance;
    }

    /// <summary>Закрыть экземпляры, простаивающие дольше <see cref="IdleTimeout" />, — чтобы не держать лицензии.</summary>
    public void CloseIdle()
    {
        var now = time.GetUtcNow();
        foreach (var instance in Instances.Where(i => i.State == InstanceState.Idle && now - i.IdleSince >= IdleTimeout))
            KillCore(instance, "простаивал");
    }

    /// <summary>
    ///     Памяти машины меньше резерва: простаивающие Revit держат память впустую — закрываем их сразу. Занятые не трогаем:
    ///     много памяти у работающего Revit — норма тяжёлой модели, а убитое задание пришлось бы делать заново.
    /// </summary>
    public int CloseIdleForMemory()
    {
        var idle = Instances.Where(i => i.State == InstanceState.Idle).ToList();
        foreach (var instance in idle) KillCore(instance, "мало свободной памяти");
        return idle.Count;
    }

    /// <summary>Сколько памяти закладывать на задание: настройка или больший пик последних заданий этой машины.</summary>
    public long ExpectedJobMb(int configuredMb)
    {
        lock (_sync) return Math.Max(configuredMb, _recentPeaks.Count == 0 ? 0 : _recentPeaks.Max());
    }

    /// <summary>Пик памяти законченного задания — для оценки следующих. Учитываются последние <see cref="RecentPeaks" />.</summary>
    public void RecordPeak(long peakMb)
    {
        if (peakMb <= 0) return;
        lock (_sync)
        {
            _recentPeaks.Enqueue(peakMb);
            while (_recentPeaks.Count > RecentPeaks) _recentPeaks.Dequeue();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in Instances) KillCore(instance, "агент остановлен");
        await Task.CompletedTask;
    }

    /// <returns>null — Revit готов; иначе почему запуск не удался (для отмены и остановки агента текст не важен).</returns>
    private async Task<string?> WaitReadyAsync(RevitInstance instance, Func<bool>? abandoned, CancellationToken ct)
    {
        var year = instance.Year;
        var loaded = false;
        while (true)
        {
            if (instance.Ready.IsCompletedSuccessfully) return null;
            if (instance.Process.HasExited) return $"Revit {year} закрылся при запуске";
            if (instance.Ready.IsFaulted) return $"аддин ToadFlow в Revit {year} отключился от агента при запуске";
            if (abandoned?.Invoke() == true) return "";
            if (ct.IsCancellationRequested) return NotStarted(instance);

            foreach (var dialog in await Task.Run(instance.Dialogs.Check, CancellationToken.None))
            {
                switch (dialog.Outcome)
                {
                    case DialogOutcome.Fatal:
                        return $"Revit {year} при запуске показал окно {dialog.Window} — {DialogRules.Describe(dialog.Kind)}";
                    case DialogOutcome.NeedsPerson:
                        instance.SetStage($"Запуск Revit: ждёт ответа в окне {dialog.Window}");
                        break;
                }
            }

            if (instance.Dialogs.Blocking is { } blocked)
                return $"Revit {year} при запуске ждёт ответа в окне {blocked.Describe()} — {DialogRules.Describe(DialogRules.Classify(blocked))}";

            if (!loaded && instance.Hello.IsCompletedSuccessfully)
            {
                loaded = true;
                logger.LogInformation("Revit {Year} (PID {Pid}): аддин ToadFlow загружен, ждём конца запуска Revit", year, instance.Process.Pid);
                instance.SetStage("Запуск Revit: аддин загружен");
            }

            await Task.WhenAny(instance.Ready, instance.Process.Exited, Task.Delay(DialogPoll, time, ct));
        }
    }

    private static string NotStarted(RevitInstance instance)
    {
        var stage = instance.Hello.IsCompletedSuccessfully ? "аддин ToadFlow загружен, но Revit не закончил запуск" : "аддин ToadFlow не загрузился";
        var open = instance.Dialogs.Open;
        return $"Revit {instance.Year} не запустился за {StartTimeout.TotalMinutes:0} мин: {stage}" + (open.Count == 0 ? "" : $"; открыто окно {string.Join("; ", open)}");
    }

    private void KillCore(RevitInstance instance, string reason)
    {
        instance.KillReason ??= reason;
        instance.State = InstanceState.Restarting;
        Forget(instance, failedStart: false);
        try
        {
            instance.Process?.Kill();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Не удалось убить Revit в слоте {Slot}", instance.Slot);
        }

        _ = instance.Pipe?.DisposeAsync().AsTask();
    }

    private void Forget(RevitInstance instance, bool failedStart)
    {
        lock (_sync)
        {
            _instances.Remove(instance);
            if (failedStart) _failedStarts[instance.Year] = time.GetUtcNow();
        }
    }
}
