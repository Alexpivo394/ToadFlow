using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using ToadFlow.Agent.Instances;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Agent.Tests.Infrastructure;

/// <summary>Что имитация аддина делает с заданием.</summary>
public delegate Task FakeJobBehavior(FakeAddin addin, JobMessage job, CancellationToken ct);

/// <summary>
///     Аддин без Revit: подключается к настоящему пайпу агента по TOADFLOW_INSTANCE_ID, отвечает hello и
///     выполняет задания заданным поведением.
/// </summary>
public sealed class FakeAddin : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;

    public FakeAddin(FakeRevitProcess process, int year, FakeJobBehavior behavior, FakeStartup startup)
    {
        Process = process;
        Year = year;
        Behavior = behavior;
        Run = Task.Run(() => RunAsync(startup));
    }

    public FakeRevitProcess Process { get; }
    public int Year { get; }
    public FakeJobBehavior Behavior { get; }
    public Task Run { get; }
    public ConcurrentQueue<PipeMessage> Received { get; } = new();

    /// <summary>Пришла отмена текущего задания.</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task SendAsync(PipeMessage message) => SendCoreAsync(message);

    public Task ProgressAsync(JobMessage job, string stage, string text) =>
        SendAsync(new ProgressMessage { JobId = job.JobId, Stage = stage, Text = text, At = DateTimeOffset.UtcNow });

    public Task ResultAsync(JobMessage job, JobOutcome outcome, string? note = null, string? resultJson = null) =>
        SendAsync(new ResultMessage { JobId = job.JobId, Outcome = outcome, Note = note, ResultJson = resultJson });

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_pipe is not null) await _pipe.DisposeAsync();
        try
        {
            await Run;
        }
        catch
        {
            // имитация остановлена
        }
    }

    private async Task RunAsync(FakeStartup startup)
    {
        if (!startup.Hello) return; // «Revit не стартовал»: к пайпу никто не подключается
        if (startup.BeforeHello is not null)
        {
            // Агент убил «Revit», так и не дождавшийся ответа в окне.
            await Task.WhenAny(startup.BeforeHello(Process), Process.Exited, Task.Delay(Timeout.Infinite, _stop.Token));
            if (Process.HasExited || _stop.IsCancellationRequested) return;
        }

        var instanceId = Guid.Parse(Process.Environment[PipeNames.InstanceIdVariable]);
        _pipe = new NamedPipeClientStream(".", PipeNames.ForInstance(instanceId), PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(10_000, _stop.Token);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
        var reader = new StreamReader(_pipe, new UTF8Encoding(false));

        await SendAsync(new HelloMessage
        {
            InstanceId = instanceId, RevitYear = Year, Pid = Process.Pid, HasNwcExporter = true, AddinVersion = "test", SendsReady = startup.Ready is not null
        });
        if (startup.Ready == true) await SendAsync(new ReadyMessage());

        while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token) is { } line)
        {
            var message = PipeJson.Deserialize(line);
            Received.Enqueue(message);
            switch (message)
            {
                case JobMessage job:
                    _ = Task.Run(() => Behavior(this, job, _stop.Token));
                    break;
                case CancelMessage:
                    Cancelled.TrySetResult();
                    break;
            }
        }
    }

    private async Task SendCoreAsync(PipeMessage message)
    {
        if (_writer is null || Process.HasExited) return;
        await _write.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(PipeJson.Serialize(message));
        }
        catch (IOException)
        {
            // агент закрыл пайп
        }
        finally
        {
            _write.Release();
        }
    }
}

public static class FakeBehaviors
{
    /// <summary>Шаг «Открытие», затем успех.</summary>
    public static FakeJobBehavior Ok(JobOutcome outcome = JobOutcome.Succeeded, string? resultJson = null, TimeSpan? delay = null) =>
        async (addin, job, ct) =>
        {
            await addin.ProgressAsync(job, "Открытие", "открытие модели");
            if (delay is { } d) await Task.Delay(d, ct);
            await addin.ResultAsync(job, outcome, null, resultJson ?? """{"rvt":"Done","nwc":"NotRequested","rvtSizeBytes":1024}""");
        };

    /// <summary>Шаг «Открытие» — и тишина: Revit завис.</summary>
    public static FakeJobBehavior Hang() => async (addin, job, _) => await addin.ProgressAsync(job, "Открытие", "открытие модели");

    /// <summary>Шаг «Открытие» — и процесс Revit падает.</summary>
    public static FakeJobBehavior Crash() => async (addin, job, _) =>
    {
        await addin.ProgressAsync(job, "Открытие", "открытие модели");
        addin.Process.Exit();
    };

    /// <summary>Работает, пока не попросят отменить, тогда отвечает «отменено».</summary>
    public static FakeJobBehavior UntilCancelled() => async (addin, job, _) =>
    {
        await addin.ProgressAsync(job, "Открытие", "открытие модели");
        await addin.Cancelled.Task;
        await addin.ResultAsync(job, JobOutcome.Cancelled, "прервано");
    };
}
