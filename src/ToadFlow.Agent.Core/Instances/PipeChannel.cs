using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Agent.Instances;

/// <summary>Серверная сторона пайпа одного экземпляра Revit: ждёт аддин, читает строки-сообщения, отправляет свои.</summary>
public sealed class PipeChannel : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Channel<PipeMessage> _incoming = Channel.CreateUnbounded<PipeMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StreamWriter? _writer;

    private PipeChannel(Guid instanceId)
    {
        _pipe = new NamedPipeServerStream(PipeNames.ForInstance(instanceId), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Completion = Task.Run(RunAsync);
    }

    public static PipeChannel Listen(Guid instanceId) => new(instanceId);

    public ChannelReader<PipeMessage> Incoming => _incoming.Reader;

    /// <summary>Завершается, когда аддин отключился или канал закрыт.</summary>
    public Task Completion { get; }

    public async Task SendAsync(PipeMessage message)
    {
        await _connected.Task;
        await _write.WaitAsync();
        try
        {
            await _writer!.WriteLineAsync(PipeJson.Serialize(message));
        }
        finally
        {
            _write.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _pipe.DisposeAsync();
        try
        {
            await Completion;
        }
        catch
        {
            // канал уже закрыт
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stop.Token);
            var encoding = new UTF8Encoding(false);
            _writer = new StreamWriter(_pipe, encoding) { AutoFlush = true };
            _connected.TrySetResult();
            using var reader = new StreamReader(_pipe, encoding);
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                PipeMessage message;
                try
                {
                    message = PipeJson.Deserialize(line);
                }
                catch (InvalidDataException)
                {
                    continue; // повреждённую строку пропускаем — следующая придёт целой
                }

                await _incoming.Writer.WriteAsync(message, _stop.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // аддин отключился или канал закрыли
        }
        finally
        {
            _connected.TrySetException(new IOException("Аддин не подключился"));
            _incoming.Writer.TryComplete();
        }
    }
}
