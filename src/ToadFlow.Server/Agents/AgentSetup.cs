using Microsoft.AspNetCore.Authentication;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Agents;

public static class AgentSetup
{
    public static void AddAgents(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AgentAuthHandler>(AgentAuthHandler.SchemeName, null);
        services.AddSingleton<AgentRegistry>();
        services.AddHostedService<AgentPresenceMonitor>();
        services.AddScoped<IDomainEventHandler, AgentSettingsBroadcaster>();
    }
}
