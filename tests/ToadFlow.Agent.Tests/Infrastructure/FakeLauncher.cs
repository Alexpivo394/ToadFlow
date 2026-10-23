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

    public Task Exited => _exited.Task;

    public void Kill()
    {
        Killed = true;
        Exit();
    }

    /// <summary>Процесс завершился сам — «упал».</summary>
    public void Exit() => _exited.TrySetResult();
}

/// <summary>Вместо Revit.exe поднимает <see cref="FakeAddin" /> с поведением из очереди (последнее повторяется).</summary>
public sealed class FakeLauncher : IProcessLauncher
{
    private readonly ConcurrentQueue<(FakeJobBehavior Behavior, bool Hello)> _scripts = new();
    private (FakeJobBehavior Behavior, bool Hello) _last = (FakeBehaviors.Ok(), true);
    private int _pid = 1000;

    public ConcurrentBag<FakeAddin> Addins { get; } = [];
    public ConcurrentBag<(string Exe, FakeRevitProcess Process)> Started { get; } = [];

    /// <summary>Следующий запуск Revit: как он себя поведёт.</summary>
    public FakeLauncher Then(FakeJobBehavior behavior, bool hello = true)
    {
        _scripts.Enqueue((behavior, hello));
        return this;
    }

    public IRevitProcess Start(int year, string exe, IReadOnlyDictionary<string, string> environment)
    {
        if (_scripts.TryDequeue(out var script)) _last = script;
        var process = new FakeRevitProcess(Interlocked.Increment(ref _pid), environment);
        Started.Add((exe, process));
        Addins.Add(new FakeAddin(process, year, _last.Behavior, _last.Hello));
        return process;
    }

    public async Task DisposeAddinsAsync()
    {
        foreach (var addin in Addins) await addin.DisposeAsync();
    }
}
