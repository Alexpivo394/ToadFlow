using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Сервер ToadFlow в памяти поверх отдельной базы встроенного Postgres.</summary>
/// <param name="time">Подменное время для фоновых служб; null — настоящее.</param>
/// <param name="services">Последняя правка служб сервера для теста; null — без правок.</param>
public sealed class ServerFactory(string connectionString, TimeProvider? time = null, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
{
    public const string AdminLogin = "admin";
    public const string AdminPassword = "admin-pass";

    /// <summary>Ключ работника движка коллизий (<c>Clash:EngineKey</c>).</summary>
    public const string EngineKey = "test-engine-key-0123456789";

    /// <summary>Своё хранилище слепков коллизий у каждого сервера тестов.</summary>
    public string ClashStorageRoot { get; } = Path.Combine(Path.GetTempPath(), "toadflow-clash-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Embedded", "false");
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        builder.UseSetting("Auth:JwtKey", "test-jwt-key-0123456789abcdef0123456789abcdef");
        builder.UseSetting("Admin:Login", AdminLogin);
        builder.UseSetting("Admin:Password", AdminPassword);
        builder.UseSetting("Clash:StorageRoot", ClashStorageRoot);
        builder.UseSetting("Clash:EngineKey", EngineKey);
        if (time is not null) builder.ConfigureServices(collection => collection.Replace(ServiceDescriptor.Singleton(time)));
        if (services is not null) builder.ConfigureServices(services);
    }
}
