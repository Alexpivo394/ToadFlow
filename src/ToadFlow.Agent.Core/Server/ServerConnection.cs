using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ToadFlow.Agent.Config;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Jobs;
using ToadFlow.Agent.Sources;
using ToadFlow.Contracts;

namespace ToadFlow.Agent.Server;

/// <summary>
///     Одно исходящее соединение SignalR с сервером: через него агент представляется, шлёт heartbeat, берёт и сдаёт задания,
///     а сервер просит построить дерево источника, убить Revit, отменить задание, применить настройки. После обрыва
///     соединение восстанавливается само, и агент представляется заново.
/// </summary>
public sealed class ServerConnection : IJobReporter, IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly HubConnection _hub;
    private readonly InstanceManager _instances;
    private readonly AgentOptions _options;
    private readonly ILogger<ServerConnection> _logger;

    public ServerConnection(AgentConfig config, AgentOptions options, SourceBrowser browser, InstanceManager instances, JobRunner runner,
        ILogger<ServerConnection> logger)
    {
        _instances = instances;
        _options = options;
        _logger = logger;
        _hub = new HubConnectionBuilder()
            .WithUrl($"{config.ServerUrl.TrimEnd('/')}/hubs/agent", http =>
            {
                http.Headers["X-Agent-Id"] = config.AgentId.ToString();
                http.Headers["X-Agent-Key"] = config.Key;
                if (options.Transport is { } transport) http.Transports = transport;
                if (options.HttpMessageHandlerFactory is { } factory) http.HttpMessageHandlerFactory = _ => factory();
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        _hub.On<SourceDto, string, SourceEntryDto[]>(nameof(IAgentClient.ListChildren), (source, path) => browser.ListChildrenAsync(source, path));
        _hub.On<SourceDto, SelectionDto, ExpandedModelDto[]>(nameof(IAgentClient.ExpandSelection), (source, selection) => browser.ExpandAsync(source, selection));
        _hub.On<int>(nameof(IAgentClient.KillInstance), slot =>
        {
            _logger.LogWarning("Сервер попросил убить Revit в слоте {Slot}", slot);
            instances.Kill(slot, "убит вручную");
        });
        _hub.On<Guid>(nameof(IAgentClient.CancelJob), runner.Cancel);
        _hub.On<AgentSettingsDto>(nameof(IAgentClient.ApplySettings), settings => Settings = settings);
        _hub.Reconnected += async _ => await HelloAsync(CancellationToken.None);
    }

    /// <summary>Последние настройки от сервера: лимиты экземпляров, таймауты, «не брать новые». null — ещё не представились.</summary>
    public AgentSettingsDto? Settings { get; private set; }

    public bool IsConnected => _hub.State == HubConnectionState.Connected && Settings is not null;

    /// <summary>Подключиться и представиться; повторяет, пока сервер не ответит.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (_hub.State == HubConnectionState.Disconnected) await _hub.StartAsync(ct);
                await HelloAsync(ct);
                _logger.LogInformation("Подключён к серверу");
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var delay = Delay(attempt);
                _logger.LogWarning("Нет связи с сервером ({Error}) — повтор через {Delay} с", exception.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>Соединение закрылось насовсем (например, сервер отказал) — переподключиться с нуля.</summary>
    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_hub.State == HubConnectionState.Disconnected) await StartAsync(ct);
    }

    public async Task HeartbeatAsync(AgentHeartbeatDto beat)
    {
        if (_hub.State != HubConnectionState.Connected) return;
        try
        {
            await _hub.InvokeAsync(nameof(IAgentHub.Heartbeat), beat);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Heartbeat не дошёл");
        }
    }

    public async Task<JobAssignmentDto?> RequestJobAsync(JobRequestDto request) =>
        IsConnected ? await _hub.InvokeAsync<JobAssignmentDto?>(nameof(IAgentHub.RequestJob), request) : null;

    /// <summary>Сдать результат; при обрыве — повторять, пока не дойдёт. Если сервер уже вернул задание в очередь, он ответ проигнорирует.</summary>
    public async Task CompleteJobAsync(JobCompletionDto completion, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (_hub.State == HubConnectionState.Connected)
                {
                    await _hub.InvokeAsync(nameof(IAgentHub.CompleteJob), completion, ct);
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Результат задания {Job} не дошёл ({Error}) — повторю", completion.JobId, exception.Message);
            }

            await Task.Delay(Delay(attempt + 1), ct);
        }
    }

    public async Task ProgressAsync(JobProgressDto progress)
    {
        if (_hub.State == HubConnectionState.Connected) await _hub.InvokeAsync(nameof(IAgentHub.ReportProgress), progress);
    }

    public async Task InstanceKilledAsync(int slot, string reason)
    {
        if (_hub.State == HubConnectionState.Connected) await _hub.InvokeAsync(nameof(IAgentHub.ReportInstanceKilled), slot, reason);
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();

    private async Task HelloAsync(CancellationToken ct)
    {
        var revits = _instances.Revits.Select(r => new InstalledRevitDto(r.Year, HasNwcExporter(r.Year, r.HasNwcExporter))).ToArray();
        var totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        Settings = await _hub.InvokeAsync<AgentSettingsDto>(nameof(IAgentHub.Hello), new AgentHelloDto(_options.MachineName, _options.AgentVersion, totalMb, revits), ct);
    }

    /// <summary>Аддин знает точно (сказал в hello); пока Revit этого года не запускался — догадка по манифестам.</summary>
    private bool HasNwcExporter(int year, bool guess) =>
        _instances.Instances.FirstOrDefault(i => i.Year == year && i.State != InstanceState.Starting) is { } running ? running.HasNwcExporter : guess;

    private static TimeSpan Delay(int attempt) => attempt < RetryDelays.Length ? RetryDelays[attempt] : MaxRetryDelay;

    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) => Delay((int)Math.Min(context.PreviousRetryCount + 1, int.MaxValue));
    }
}
