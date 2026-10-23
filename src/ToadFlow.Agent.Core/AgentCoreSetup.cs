using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ToadFlow.Agent.Config;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Jobs;
using ToadFlow.Agent.Revit;
using ToadFlow.Agent.Server;
using ToadFlow.Agent.Sources;

namespace ToadFlow.Agent;

public static class AgentCoreSetup
{
    /// <summary>Все службы агента и рабочий цикл. Хост (трей, служба, тест) только запускает их.</summary>
    public static IServiceCollection AddToadFlowAgentCore(this IServiceCollection services, AgentConfig config, AgentOptions? options = null)
    {
        options ??= new AgentOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(config);
        services.AddSingleton(options);
        services.AddSingleton(options.Launcher ?? new ProcessLauncher(config.TrustAddin ? new AddinTrust() : null));
        services.AddSingleton(provider => new InstanceManager(provider.GetRequiredService<IProcessLauncher>(), options.Revits ?? new RevitLocator().Find(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InstanceManager>>()));
        services.AddSingleton<JobRunner>();
        services.AddSingleton<SourceBrowser>();
        services.AddSingleton<ServerConnection>();
        services.AddHostedService<AgentWorker>();
        return services;
    }
}
