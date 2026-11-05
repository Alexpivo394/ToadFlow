using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Revit.UI;
using ToadFlow.Contracts.Pipe;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin;

/// <summary>
///     Клиент пайпа агента (фоновый поток): hello, ready после запуска Revit, приём заданий и отмен, «Revit жив» каждые
///     10 секунд. Задание выполняется в потоке Revit API через внешнее событие.
/// </summary>
internal sealed class AgentPipeClient(Guid instanceId, int revitYear, bool hasNwcExporter, string? journalPath, JobExecutor executor, ExternalEvent externalEvent,
    PipeProgress progress) : IDisposable
{
    private const int ConnectTimeoutMs = 60_000;
    private const int WmNull = 0x0000;
    private static readonly TimeSpan AliveEvery = TimeSpan.FromSeconds(10);

    private readonly object _write = new();
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Timer? _alive;
    private bool _ready;

    /// <summary>
    ///     Revit закончил запуск. ApplicationInitialized может прийти раньше, чем фоновый поток подключится к пайпу, —
    ///     тогда ready уходит сразу после hello.
    /// </summary>
    public void MarkReady()
    {
        lock (_write)
        {
            _ready = true;
            if (_writer is not null) Write(new ReadyMessage());
        }
    }

    public void Start()
    {
        executor.Send = Send;
        progress.Send = Send;
        new Thread(Run) { IsBackground = true, Name = "ToadFlow pipe" }.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _alive?.Dispose();
        _pipe?.Dispose();
    }

    /// <summary>
    ///     Сеансы связи с агентом. Пайп оборвался, а Revit не закрывается: пробуем подключиться снова, и если агент не вернулся за
    ///     <see cref="PipeLoss.Grace" />, Revit закрывается сам — упавший агент его уже не закроет.
    /// </summary>
    private void Run()
    {
        var loss = new PipeLoss();
        while (!_stop.IsCancellationRequested)
        {
            if (Session()) loss.Restored();
            if (_stop.IsCancellationRequested) return;

            loss.Lost(DateTimeOffset.UtcNow);
            if (loss.ShouldExit(DateTimeOffset.UtcNow))
            {
                Process.GetCurrentProcess().Kill();
                return;
            }

            _stop.Token.WaitHandle.WaitOne(AliveEvery);
        }
    }

    /// <returns>Подключились к агенту (сеанс был, пусть и оборвался).</returns>
    private bool Session()
    {
        var connected = false;
        try
        {
            _alive?.Dispose();
            _pipe?.Dispose();
            _pipe = new NamedPipeClientStream(".", PipeNames.ForInstance(instanceId), PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe.Connect(ConnectTimeoutMs);
            connected = true;
            var encoding = new UTF8Encoding(false);
            lock (_write)
            {
                _writer = new StreamWriter(_pipe, encoding) { AutoFlush = true };
                Write(new HelloMessage
                {
                    InstanceId = instanceId,
                    RevitYear = revitYear,
                    Pid = Process.GetCurrentProcess().Id,
                    HasNwcExporter = hasNwcExporter,
                    AddinVersion = typeof(AgentPipeClient).Assembly.GetName().Version?.ToString() ?? "0",
                    JournalPath = journalPath,
                    SendsReady = true
                });
                if (_ready) Write(new ReadyMessage());
            }

            _alive = new Timer(_ => Send(new AliveMessage { LastActivityAt = progress.LastActivityAt }), null, AliveEvery, AliveEvery);

            using var reader = new StreamReader(_pipe, encoding);
            while (!_stop.IsCancellationRequested && reader.ReadLine() is { } line)
            {
                switch (Deserialize(line))
                {
                    case JobMessage job:
                        executor.Enqueue(job);
                        externalEvent.Raise();
                        Nudge();
                        break;
                    case CancelMessage cancel:
                        progress.RequestAbort(cancel.JobId);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
            // Агент недоступен или закрыл пайп: Run попробует снова, а если агент не вернётся — закроет Revit.
        }
        finally
        {
            lock (_write) _writer = null;
        }

        return connected;
    }

    private void Send(PipeMessage message)
    {
        lock (_write) Write(message);
    }

    /// <summary>Под <see cref="_write" />.</summary>
    private void Write(PipeMessage message)
    {
        try
        {
            _writer?.WriteLine(AddinPipeJson.Serialize(message));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // пайп закрыт
        }
    }

    private static PipeMessage? Deserialize(string line)
    {
        try
        {
            return AddinPipeJson.Deserialize(line);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Простаивающий Revit обрабатывает внешние события, только когда в его цикле сообщений что-то происходит.
    ///     Пустое сообщение главному окну будит цикл — иначе задание ждало бы движения мыши.
    /// </summary>
    internal static void Nudge()
    {
        var window = Process.GetCurrentProcess().MainWindowHandle;
        if (window != IntPtr.Zero) PostMessage(window, WmNull, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
