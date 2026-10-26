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

    private readonly object _sync = new();
    private readonly List<RevitInstance> _instances = [];
    private readonly Dictionary<int, DateTimeOffset> _failedStarts = [];

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

    /// <summary>Экземпляр Revit этого года под задание: свободный готовый или новый (ждём hello до 5 минут).</summary>
    /// <param name="job">Задание, под которое запускается Revit: пока он стартует, heartbeat держит аренду задания.</param>
    /// <exception cref="RevitStartException">Revit этого года нет, слотов нет или он не запустился.</exception>
    public async Task<RevitInstance> AcquireAsync(int year, CancellationToken ct, JobAssignmentDto? job = null)
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
            instance.Attach(launcher.Start(year, revit.RevitExe, new Dictionary<string, string> { [PipeNames.InstanceIdVariable] = instance.InstanceId.ToString() }), pipe);
        }
        catch (Exception exception) when (exception is not RevitStartException)
        {
            await pipe.DisposeAsync();
            Forget(instance, failedStart: true);
            throw new RevitStartException($"Не удалось запустить Revit {year}: {exception.Message}");
        }

        using var timeout = new CancellationTokenSource(StartTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var hello = instance.Hello.WaitAsync(linked.Token);
        await Task.WhenAny(hello, instance.Process.Exited);

        if (!hello.IsCompletedSuccessfully)
        {
            var reason = instance.Process.HasExited ? $"Revit {year} закрылся при запуске" : $"Revit {year} не запустился за {StartTimeout.TotalMinutes:0} мин";
            logger.LogWarning("{Reason}", reason);
            KillCore(instance, reason);
            lock (_sync) _failedStarts[year] = time.GetUtcNow();
            ct.ThrowIfCancellationRequested();
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
        if (instance.Process.HasExited) return;

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

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in Instances) KillCore(instance, "агент остановлен");
        await Task.CompletedTask;
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
