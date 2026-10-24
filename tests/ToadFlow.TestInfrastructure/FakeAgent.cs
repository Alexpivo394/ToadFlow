using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using ToadFlow.Contracts;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Агент для тестов: регистрируется по коду, держит хаб и отвечает на вызовы сервера заданными функциями.</summary>
public sealed class FakeAgent : IAsyncDisposable
{
    private readonly ServerFactory _server;

    private FakeAgent(ServerFactory server, Guid id, string key, string machineName)
    {
        _server = server;
        Id = id;
        Key = key;
        MachineName = machineName;
        Hub = Connect(server, id, key);
        Hub.On<SourceDto, string, SourceEntryDto[]>(nameof(IAgentClient.ListChildren), (source, path) => Task.FromResult(ListChildren(source, path)));
        Hub.On<SourceDto, SelectionDto, ExpandedModelDto[]>(nameof(IAgentClient.ExpandSelection),
            (source, selection) => Task.FromResult(ExpandSelection(source, selection)));
        Hub.On<int>(nameof(IAgentClient.KillInstance), slot => Killed.Add(slot));
        Hub.On<Guid>(nameof(IAgentClient.CancelJob), jobId => Cancelled.Add(jobId));
        Hub.On<AgentSettingsDto>(nameof(IAgentClient.ApplySettings), settings => AppliedSettings.Add(settings));
    }

    public Guid Id { get; }
    public string Key { get; }
    public string MachineName { get; }
    public HubConnection Hub { get; }

    public Func<SourceDto, string, SourceEntryDto[]> ListChildren { get; set; } = (_, _) => [];
    public Func<SourceDto, SelectionDto, ExpandedModelDto[]> ExpandSelection { get; set; } = (_, _) => [];
    public ConcurrentBag<int> Killed { get; } = [];
    public ConcurrentBag<Guid> Cancelled { get; } = [];
    public ConcurrentBag<AgentSettingsDto> AppliedSettings { get; } = [];

    /// <summary>Регистрирует нового агента через одноразовый код, выданный администратором.</summary>
    public static async Task<FakeAgent> RegisterAsync(ServerFactory server, HttpClient admin, string machineName = "BIM-FARM-01")
    {
        var code = await (await admin.PostAsync("/api/agents/registration-codes", null)).EnsureSuccessStatusCode().ReadJsonAsync<CodeBody>();
        var registered = await (await server.CreateClient().PostJsonAsync("/api/agents/register", new { code = code.Code, machineName }))
            .EnsureSuccessStatusCode().ReadJsonAsync<RegisterBody>();
        return new FakeAgent(server, registered.AgentId, registered.Key, machineName);
    }

    /// <summary>Подключается и представляется серверу найденными версиями Revit.</summary>
    public Task<AgentSettingsDto> ConnectAsync(params InstalledRevitDto[] revits) => ConnectAsync("1.1.0", revits);

    /// <param name="version">Версия агента: 1.0.x — до заданий LOI, такие получают только выгрузку.</param>
    public async Task<AgentSettingsDto> ConnectAsync(string version, params InstalledRevitDto[] revits)
    {
        await Hub.StartAsync();
        return await Hub.InvokeAsync<AgentSettingsDto>(nameof(IAgentHub.Hello),
            new AgentHelloDto(MachineName, version, 65536, revits.Length > 0 ? revits : [new InstalledRevitDto(2022, true)]));
    }

    public Task HeartbeatAsync(params InstanceStatusDto[] instances) =>
        Hub.InvokeAsync(nameof(IAgentHub.Heartbeat), new AgentHeartbeatDto(42, 30000, instances));

    public Task<JobAssignmentDto?> RequestJobAsync(int slot, int year, bool hasNwcExporter = true) =>
        Hub.InvokeAsync<JobAssignmentDto?>(nameof(IAgentHub.RequestJob), new JobRequestDto(slot, year, hasNwcExporter));

    public Task ReportInstanceKilledAsync(int slot, string reason) =>
        Hub.InvokeAsync(nameof(IAgentHub.ReportInstanceKilled), slot, reason);

    public Task ReportProgressAsync(Guid jobId, int slot, params JobLogDto[] lines) =>
        Hub.InvokeAsync(nameof(IAgentHub.ReportProgress), new JobProgressDto(jobId, slot, lines));

    public Task CompleteJobAsync(Guid jobId, JobOutcome outcome, string? note = null, string? resultJson = null) =>
        Hub.InvokeAsync(nameof(IAgentHub.CompleteJob), new JobCompletionDto(jobId, outcome, note, resultJson));

    /// <summary>Второе соединение тем же ключом — например, чтобы проверить, что сервер его не пустит.</summary>
    public HubConnection NewConnection(string? key = null) => Connect(_server, Id, key ?? Key);

    public async ValueTask DisposeAsync() => await Hub.DisposeAsync();

    private static HubConnection Connect(ServerFactory server, Guid id, string key) => new HubConnectionBuilder()
        .WithUrl(new Uri(server.Server.BaseAddress, "/hubs/agent"), options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => server.Server.CreateHandler();
            options.Headers["X-Agent-Id"] = id.ToString();
            options.Headers["X-Agent-Key"] = key;
        })
        .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
        .Build();

    private sealed record CodeBody(string Code, DateTimeOffset ExpiresAt);

    private sealed record RegisterBody(Guid AgentId, string Key);
}
