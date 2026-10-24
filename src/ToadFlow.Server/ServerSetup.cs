using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Dashboard;
using ToadFlow.Server.Data;
using ToadFlow.Server.Maintenance;
using ToadFlow.Server.Events;
using ToadFlow.Server.Modules;
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
        builder.Services.AddAgents();
        builder.Services.AddQueue();
        builder.Services.AddSingleton<ServerModuleRegistry>();
        builder.Services.AddSchedules();
        builder.Services.AddHostedService<RetentionCleaner>();
        return builder;
    }

    public static void UseToadFlowServer(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();

        var api = app.MapGroup("/api").RequireAuthorization(Policies.Viewer);
        api.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        api.MapAuth();
        api.MapApiTokens();
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

        app.MapHub<UiHub>("/hubs/ui");
        app.MapHub<AgentHub>("/hubs/agent");
    }
}
