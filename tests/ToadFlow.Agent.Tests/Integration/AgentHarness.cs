using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToadFlow.Agent.Config;
using ToadFlow.Agent.Revit;
using ToadFlow.Agent.Server;
using ToadFlow.Agent.Tests.Infrastructure;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Agent.Tests.Integration;

/// <summary>Настоящий агент (ядро) против сервера в памяти: Revit заменён <see cref="FakeLauncher" />.</summary>
public sealed class AgentHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly List<IHostedService> _hosted;

    private AgentHarness(ServiceProvider services, FakeLauncher launcher, AgentConfig config)
    {
        _services = services;
        Launcher = launcher;
        Config = config;
        _hosted = services.GetServices<IHostedService>().ToList();
    }

    public FakeLauncher Launcher { get; }
    public AgentConfig Config { get; }
    public ServerConnection Connection => _services.GetRequiredService<ServerConnection>();

    public static async Task<AgentHarness> StartAsync(ServerFactory server, HttpClient admin, FakeLauncher launcher, TimeSpan? silence = null,
        string machineName = "BIM-FARM-01")
    {
        var code = await (await admin.PostAsync("/api/agents/registration-codes", null)).ReadJsonAsync<CodeBody>();
        var config = await new Registration(server.Server.CreateHandler()).RegisterAsync("http://localhost", code.Code, machineName);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddToadFlowAgentCore(config, new AgentOptions
        {
            MachineName = machineName,
            HttpMessageHandlerFactory = server.Server.CreateHandler,
            Transport = HttpTransportType.LongPolling,
            HeartbeatInterval = TimeSpan.FromMilliseconds(500),
            PollInterval = TimeSpan.FromMilliseconds(500),
            SilenceTimeoutOverride = silence,
            Revits = [new RevitInstallation(2022, @"C:\Program Files\Autodesk\Revit 2022\Revit.exe", true)],
            Launcher = launcher
        });

        var harness = new AgentHarness(services.BuildServiceProvider(), launcher, config);
        foreach (var hosted in harness._hosted) await hosted.StartAsync(CancellationToken.None);
        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _hosted) await hosted.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
        await Launcher.DisposeAddinsAsync();
    }

    private sealed record CodeBody(string Code);
}
