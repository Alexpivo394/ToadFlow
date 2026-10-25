using Microsoft.AspNetCore.Authentication;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Agents;

public static class AgentSetup
{
    /// <summary>
    ///     Результат задания (итоги проверки LOI, каталог параметров) и пачка шагов приходят одним сообщением. При 32 КБ
    ///     по умолчанию сервер рвал соединение на каждой попытке, и агент слал результат по кругу.
    /// </summary>
    private const long MaxAgentMessageBytes = 64L * 1024 * 1024;

    public static void AddAgents(this IServiceCollection services)
    {
        services.AddSignalR().AddHubOptions<AgentHub>(options => options.MaximumReceiveMessageSize = MaxAgentMessageBytes);
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AgentAuthHandler>(AgentAuthHandler.SchemeName, null);
        services.AddSingleton<AgentRegistry>();
        services.AddHostedService<AgentPresenceMonitor>();
        services.AddScoped<IDomainEventHandler, AgentSettingsBroadcaster>();
    }
}
