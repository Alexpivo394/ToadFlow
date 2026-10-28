using System.Collections.Concurrent;
using ToadFlow.Agent.Instances;

namespace ToadFlow.Agent.Tests.Infrastructure;

/// <summary>Процесс «Revit» без Revit: живёт, пока его не убьют или он не «упадёт».</summary>
public sealed class FakeRevitProcess(int pid, IReadOnlyDictionary<string, string> environment) : IRevitProcess
{
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Pid { get; } = pid;
    public IReadOnlyDictionary<string, string> Environment { get; } = environment;
    public bool HasExited => _exited.Task.IsCompleted;
    public long WorkingSetMb { get; set; } = 2000;
    public bool Killed { get; private set; }

    /// <summary>Счётчики процесса для сторожа зависаний; null — счётчиков нет, как у процесса без доступа.</summary>
    public Func<ProcessSample?>? Sampler { get; set; }

    public Task Exited => _exited.Task;

    public FakeWindows Dialogs { get; } = new();

    public IRevitWindows Windows => Dialogs;

    public ProcessSample? Sample() => HasExited ? null : Sampler?.Invoke();

    public void Kill()
    {
        Killed = true;
        Exit();
    }

    /// <summary>Процесс завершился сам — «упал».</summary>
    public void Exit() => _exited.TrySetResult();
}

/// <summary>Как «Revit» запускается: до hello он может показать окна, после hello — прислать или не прислать ready.</summary>
/// <param name="Hello">false — аддин так и не подключился.</param>
/// <param name="Ready">null — аддин до 1.7 (готов сразу после hello); false — Revit не закончил запуск.</param>
/// <param name="BeforeHello">Что «Revit» делает до загрузки аддина — например, ждёт ответа в окне.</param>
public sealed record FakeStartup(bool Hello = true, bool? Ready = null, Func<FakeRevitProcess, Task>? BeforeHello = null);

/// <summary>Вместо Revit.exe поднимает <see cref="FakeAddin" /> с поведением из очереди (последнее повторяется).</summary>
public sealed class FakeLauncher : IProcessLauncher
{
    private readonly ConcurrentQueue<(FakeJobBehavior Behavior, FakeStartup Startup)> _scripts = new();
    private (FakeJobBehavior Behavior, FakeStartup Startup) _last = (FakeBehaviors.Ok(), new FakeStartup());
    private int _pid = 1000;

    public ConcurrentBag<FakeAddin> Addins { get; } = [];
    public ConcurrentBag<(string Exe, FakeRevitProcess Process)> Started { get; } = [];

    /// <summary>Следующий запуск Revit: как он себя поведёт.</summary>
    public FakeLauncher Then(FakeJobBehavior behavior, bool hello = true) => Then(behavior, new FakeStartup(hello));

    public FakeLauncher Then(FakeJobBehavior behavior, FakeStartup startup)
    {
        _scripts.Enqueue((behavior, startup));
        return this;
    }

    public IRevitProcess Start(int year, string exe, IReadOnlyDictionary<string, string> environment)
    {
        if (_scripts.TryDequeue(out var script)) _last = script;
        var process = new FakeRevitProcess(Interlocked.Increment(ref _pid), environment);
        Started.Add((exe, process));
        Addins.Add(new FakeAddin(process, year, _last.Behavior, _last.Startup));
        return process;
    }

    public async Task DisposeAddinsAsync()
    {
        foreach (var addin in Addins) await addin.DisposeAsync();
    }
}
