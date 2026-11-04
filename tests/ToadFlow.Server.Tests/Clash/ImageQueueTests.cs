using Shouldly;
using ToadFlow.Server.Clash.Images;

namespace ToadFlow.Server.Tests;

/// <summary>Очередь отрисовок: предел ожидающих, отмена брошенных, выгрузки не вытесняют людей.</summary>
public class ImageQueueTests
{
    /// <summary>Отрисовка, которая идёт, пока тест её не отпустит.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public async Task<int> Work(CancellationToken ct)
        {
            Token = ct;
            Started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return 1;
        }

        public void Release() => _release.TrySetResult();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("условие не выполнилось");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task Queue_Full_429RetryAfter()
    {
        var queue = new RenderQueue<int, int>(threads: 1, archiveWindow: 1, interactiveCapacity: 2);
        var running = new Gate();
        var first = queue.RunAsync(0, ImagePriority.Interactive, running.Work, CancellationToken.None);
        await running.Started.Task;
        var waiting = new[] { 1, 2 }.Select(key => queue.RunAsync(key, ImagePriority.Interactive, new Gate().Work, CancellationToken.None)).ToList();

        await Should.ThrowAsync<RenderQueueFullException>(queue.RunAsync(3, ImagePriority.Interactive, new Gate().Work, CancellationToken.None));
        // Та же картинка, что уже ждёт, — не новая отрисовка: к ней можно присоединиться.
        var joined = queue.RunAsync(1, ImagePriority.Interactive, new Gate().Work, CancellationToken.None);
        joined.IsFaulted.ShouldBeFalse();

        running.Release();
        (await first).ShouldBe(1);
        _ = waiting;
    }

    [Test]
    public async Task AllWaitersGone_RenderCancelled()
    {
        var queue = new RenderQueue<int, int>(threads: 1, archiveWindow: 1, interactiveCapacity: 10);
        var running = new Gate();
        using var leaveRunning = new CancellationTokenSource();
        var first = queue.RunAsync(0, ImagePriority.Interactive, running.Work, leaveRunning.Token);
        await running.Started.Task;
        var queued = new Gate();
        using var leaveQueued = new CancellationTokenSource();
        var second = queue.RunAsync(1, ImagePriority.Interactive, queued.Work, leaveQueued.Token);

        // Ушли все, кто ждал вторую картинку: она снимается с очереди и не начнётся.
        await leaveQueued.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(second);
        // Ушли все, кто ждал начатую: её отрисовка отменяется, поток освобождается.
        await leaveRunning.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(first);

        running.Token.IsCancellationRequested.ShouldBeTrue();
        await Until(() => queue.Running == 0);
        queued.Started.Task.IsCompleted.ShouldBeFalse();
    }

    [Test]
    public async Task OneWaiterGone_OthersStillGetImage()
    {
        var queue = new RenderQueue<int, int>(threads: 1, archiveWindow: 1, interactiveCapacity: 10);
        var gate = new Gate();
        using var leaving = new CancellationTokenSource();
        var a = queue.RunAsync(0, ImagePriority.Interactive, gate.Work, leaving.Token);
        var b = queue.RunAsync(0, ImagePriority.Interactive, gate.Work, CancellationToken.None);
        await gate.Started.Task;

        await leaving.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(a);
        gate.Token.IsCancellationRequested.ShouldBeFalse();
        gate.Release();

        (await b).ShouldBe(1);
    }

    /// <summary>Архив занял своё окно потоков: картинка, которую ждёт человек, начинается сразу, не за архивом.</summary>
    [Test]
    public async Task Archive_DoesNotStarveInteractive()
    {
        var queue = new RenderQueue<int, int>(threads: 2, archiveWindow: 1, interactiveCapacity: 10);
        var archive = Enumerable.Range(0, 5).Select(_ => new Gate()).ToList();
        var archiveTasks = archive.Select((gate, i) => queue.RunAsync(100 + i, ImagePriority.Archive, gate.Work, CancellationToken.None)).ToList();
        await archive[0].Started.Task;
        await Task.Delay(50);
        archive.Skip(1).ShouldAllBe(gate => !gate.Started.Task.IsCompleted); // архиву — одно окно из двух

        var person = new Gate();
        var image = queue.RunAsync(1, ImagePriority.Interactive, person.Work, CancellationToken.None);

        await person.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        person.Release();
        (await image).ShouldBe(1);
        foreach (var gate in archive) gate.Release();
        (await Task.WhenAll(archiveTasks)).ShouldAllBe(r => r == 1);
    }

    /// <summary>Картинку, которую рисует архив, попросил человек: она уходит вперёд очереди архива.</summary>
    [Test]
    public async Task ArchiveImageRequestedByPerson_JumpsAhead()
    {
        var queue = new RenderQueue<int, int>(threads: 1, archiveWindow: 1, interactiveCapacity: 10);
        var busy = new Gate();
        var first = queue.RunAsync(0, ImagePriority.Archive, busy.Work, CancellationToken.None);
        await busy.Started.Task;
        var later = new Gate();
        var wanted = new Gate();
        var laterTask = queue.RunAsync(1, ImagePriority.Archive, later.Work, CancellationToken.None);
        var wantedTask = queue.RunAsync(2, ImagePriority.Archive, wanted.Work, CancellationToken.None);

        var byPerson = queue.RunAsync(2, ImagePriority.Interactive, wanted.Work, CancellationToken.None);
        busy.Release();

        await wanted.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        later.Started.Task.IsCompleted.ShouldBeFalse();
        wanted.Release();
        later.Release();
        await Task.WhenAll(first, laterTask, wantedTask, byPerson);
    }

    [Test]
    public async Task Abandoned_StillStopping_NewRequestStartsFresh()
    {
        var queue = new RenderQueue<int, int>(threads: 2, archiveWindow: 1, interactiveCapacity: 10);
        // Брошенная отрисовка замечает отмену не сразу: пока она не остановилась, ключ не должен достаться новому запросу.
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var first = queue.RunAsync(1, ImagePriority.Interactive, async ct =>
        {
            started.TrySetResult();
            await stopping.Task;
            ct.ThrowIfCancellationRequested();
            return 1;
        }, cancel.Token);
        await started.Task;

        cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => first);
        var second = await queue.RunAsync(1, ImagePriority.Interactive, _ => Task.FromResult(2), CancellationToken.None);

        second.ShouldBe(2);
        stopping.SetResult();
        await Until(() => queue.Running == 0);
    }
}
