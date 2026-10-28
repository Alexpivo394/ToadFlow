using System.Collections.Concurrent;
using ToadFlow.Agent.Instances;

namespace ToadFlow.Agent.Tests.Infrastructure;

/// <summary>Окно-диалог «Revit» без Revit.</summary>
/// <param name="Closes">Закрывается ли окно, когда агент на него отвечает.</param>
public sealed record FakeWindow(WindowSnapshot Snapshot, bool Closes)
{
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Окна-диалоги процесса <see cref="FakeRevitProcess" />: тест открывает их, агент читает и отвечает.</summary>
public sealed class FakeWindows : IRevitWindows
{
    private readonly ConcurrentDictionary<nint, FakeWindow> _open = new();
    private int _handle = 0x1000;

    /// <summary>Как агент отвечал: заголовок окна и действие.</summary>
    public ConcurrentQueue<(string Title, DialogAction Action)> Answers { get; } = new();

    /// <summary>Если задано — перечисление окон бросает это исключение, как UI Automation, пока Revit занят.</summary>
    public Func<Exception?>? Failure { get; set; }

    public FakeWindow Show(string title, string[] texts, string[] buttons, bool closes = true, bool hasProgress = false)
    {
        var handle = (nint)Interlocked.Increment(ref _handle);
        var window = new FakeWindow(new WindowSnapshot(handle, title, texts, buttons, hasProgress), closes);
        _open[handle] = window;
        return window;
    }

    /// <summary>Окно закрыл кто-то другой — Revit или аддин.</summary>
    public void Remove(FakeWindow window)
    {
        if (_open.TryRemove(window.Snapshot.Handle, out _)) window.Closed.TrySetResult();
    }

    public IReadOnlyList<nint> Dialogs()
    {
        if (Failure?.Invoke() is { } failure) throw failure;
        return _open.Keys.Order().ToList();
    }

    public WindowSnapshot Read(nint window) => _open.TryGetValue(window, out var open) ? open.Snapshot : new WindowSnapshot(window, "", [], [], false, false);

    public bool Close(WindowSnapshot window, DialogAction action)
    {
        Answers.Enqueue((window.Title, action));
        if (!_open.TryGetValue(window.Handle, out var open)) return true;
        if (!open.Closes) return false;
        Remove(open);
        return true;
    }
}
