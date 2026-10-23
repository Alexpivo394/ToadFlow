using System.Threading.Channels;

namespace ToadFlow.Server.Events;

public interface IDomainEvents
{
    /// <summary>Не ждёт обработчиков: событие уходит в очередь, обработчики вызываются в фоне.</summary>
    void Publish(DomainEvent domainEvent);
}

public interface IDomainEventHandler
{
    Task HandleAsync(DomainEvent domainEvent, CancellationToken ct);
}

internal sealed class DomainEventQueue(TimeProvider time) : IDomainEvents
{
    private readonly Channel<DomainEvent> _channel = Channel.CreateUnbounded<DomainEvent>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<DomainEvent> Reader => _channel.Reader;

    public void Publish(DomainEvent domainEvent) => _channel.Writer.TryWrite(domainEvent with { At = time.GetUtcNow() });
}

/// <summary>Раздаёт события обработчикам; ошибка одного обработчика не мешает остальным.</summary>
internal sealed class DomainEventDispatcher(DomainEventQueue queue, IServiceScopeFactory scopes, ILogger<DomainEventDispatcher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var domainEvent in queue.Reader.ReadAllAsync(ct))
        {
            await using var scope = scopes.CreateAsyncScope();
            foreach (var handler in scope.ServiceProvider.GetServices<IDomainEventHandler>())
            {
                try
                {
                    await handler.HandleAsync(domainEvent, ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Обработчик {Handler} не справился с {Event}", handler.GetType().Name, domainEvent.GetType().Name);
                }
            }
        }
    }
}
