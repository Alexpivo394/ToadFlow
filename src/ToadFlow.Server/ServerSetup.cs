using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Dashboard;
using ToadFlow.Server.Data;
using ToadFlow.Server.Maintenance;
using ToadFlow.Server.Events;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Naming;
using ToadFlow.Server.Queue;
using ToadFlow.Server.Runs;
using ToadFlow.Server.Schedules;
using ToadFlow.Server.Settings;
using ToadFlow.Server.Sources;
using ToadFlow.Server.Users;
#if DEBUG
using ToadFlow.Server.Dev;
#endif

namespace ToadFlow.Server;

/// <summary>Сборка сервера: каждая часть ядра и модули регистрируются здесь.</summary>
public static class ServerSetup
{
    public static WebApplicationBuilder AddToadFlowServer(this WebApplicationBuilder builder)
    {
#if DEBUG
        EmbeddedPostgres.UseIfConfigured(builder);
#endif
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContext<ToadFlowDb>(options => options
            .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
            .UseSnakeCaseNamingConvention());
        builder.Services.AddHostedService<MigrationRunner>(); // первым: остальные службы стартуют после миграций
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.AddToadFlowAuth();
        builder.Services.AddDomainEvents();
        builder.Services.AddScoped<ISettingsStore, SettingsStore>();
        builder.Services.AddScoped<NamingStore>();
        builder.Services.AddAgents();
        builder.Services.AddQueue();
        builder.Services.AddSingleton<ServerModuleRegistry>();
        builder.Services.AddSchedules();
        builder.Services.AddHostedService<RetentionCleaner>();
        builder.Services.AddRequestDecompression(); // агенты сжимают части загрузок (Content-Encoding: gzip)
        return builder;
    }

    public static void UseToadFlowServer(this WebApplication app)
    {
        app.UseForwardedHeaders(); // адрес клиента за nginx — для лимитов входа и регистрации
        app.UseRequestDecompression();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // Участники — любая роль, в том числе проектировщик; всё остальное ядра и модулей — от наблюдателя и старше.
        var members = app.MapGroup("/api").RequireAuthorization(Policies.Member);
        members.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        members.MapAuth();
        members.MapApiTokens();
        members.MapRegistration();
        members.MapProfile();
        members.MapModuleMemberEndpoints(app.Services);

        var api = members.MapGroup("").RequireAuthorization(Policies.Viewer);
        api.MapUsers();
        api.MapEvents();
        api.MapSettings();
        api.MapSources();
        api.MapAgents();
        api.MapSchedules();
        api.MapRunCommands();
        api.MapHistory();
        api.MapDashboard();
        api.MapInstances();
        api.MapModules(app.Services);

        var agentApi = app.MapGroup("/api/agent").RequireAuthorization(Policies.Agent);
        agentApi.MapModuleAgentEndpoints(app.Services);
        app.MapModuleExternalEndpoints(app.Services);

        // Хаб рассылает события всех модулей: проектировщику их видеть не положено.
        app.MapHub<UiHub>("/hubs/ui").RequireAuthorization(Policies.Viewer);
        app.MapHub<AgentHub>("/hubs/agent");
    }
}
