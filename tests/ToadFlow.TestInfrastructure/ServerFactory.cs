using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Сервер ToadFlow в памяти поверх отдельной базы встроенного Postgres.</summary>
/// <param name="time">Подменное время для фоновых служб; null — настоящее.</param>
/// <param name="services">Последняя правка служб сервера для теста; null — без правок.</param>
/// <param name="modules">Настройка <c>Modules</c> (какие модули включены); null — не задана, включены все.</param>
public sealed class ServerFactory(string connectionString, TimeProvider? time = null, Action<IServiceCollection>? services = null, string? modules = null)
    : WebApplicationFactory<Program>
{
    public const string AdminLogin = "admin";
    public const string AdminPassword = "admin-pass";

    /// <summary>Ключ работника движка коллизий (<c>Clash:EngineKey</c>).</summary>
    public const string EngineKey = "test-engine-key-0123456789-abcdefghij";

    /// <summary>Своё хранилище слепков коллизий у каждого сервера тестов.</summary>
    public string ClashStorageRoot { get; } = Path.Combine(Path.GetTempPath(), "toadflow-clash-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Embedded", "false");
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        builder.UseSetting("Auth:JwtKey", "test-jwt-key-0123456789abcdef0123456789abcdef");
        // Тесты входят часто и с одного адреса: лимиты частоты проверяет свой тест (RateLimitTests).
        builder.UseSetting("Auth:Limits:LoginPerMinute", "100000");
        builder.UseSetting("Auth:Limits:RegisterPer10Minutes", "100000");
        builder.UseSetting("Admin:Login", AdminLogin);
        builder.UseSetting("Admin:Password", AdminPassword);
        builder.UseSetting("Clash:StorageRoot", ClashStorageRoot);
        builder.UseSetting("Clash:EngineKey", EngineKey);
        // Тесты входят часто и с одного адреса: лимиты частоты проверяет свой тест (RateLimitTests).
        builder.UseSetting("Auth:Limits:LoginPerMinute", "100000");
        builder.UseSetting("Auth:Limits:RegisterPer10Minutes", "100000");
        if (modules is not null) builder.UseSetting("Modules", modules);
        if (time is not null) builder.ConfigureServices(collection => collection.Replace(ServiceDescriptor.Singleton(time)));
        if (services is not null) builder.ConfigureServices(services);
    }

    private IHost? _host;

    protected override IHost CreateHost(IHostBuilder builder) => _host = base.CreateHost(builder);

    /// <summary>Останавливает сервер и гасит его ограничители частоты: сами они не освобождаются и держат сервер в памяти.</summary>
    public override async ValueTask DisposeAsync()
    {
        IReadOnlyList<System.Threading.RateLimiting.PartitionedRateLimiter<Microsoft.AspNetCore.Http.HttpContext>> limiters = [];
        try
        {
            if (_host is not null) limiters = RateLimiterCleanup.Find(_host.Services.GetRequiredService<IServer>());
        }
        catch (ObjectDisposedException)
        {
            // Сервер остановился сам (упала фоновая служба) и уже освободил свои службы.
        }

        _host = null;
        await base.DisposeAsync();
        foreach (var limiter in limiters) await limiter.DisposeAsync();
        try
        {
            if (Directory.Exists(ClashStorageRoot)) Directory.Delete(ClashStorageRoot, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Файл ещё занят — папку уберёт очистка при следующем прогоне.
        }
    }
}
