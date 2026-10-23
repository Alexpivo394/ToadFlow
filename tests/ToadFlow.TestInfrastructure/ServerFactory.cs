using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Сервер ToadFlow в памяти поверх отдельной базы встроенного Postgres.</summary>
/// <param name="time">Подменное время для фоновых служб; null — настоящее.</param>
public sealed class ServerFactory(string connectionString, TimeProvider? time = null) : WebApplicationFactory<Program>
{
    public const string AdminLogin = "admin";
    public const string AdminPassword = "admin-pass";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Embedded", "false");
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        builder.UseSetting("Auth:JwtKey", "test-jwt-key-0123456789abcdef0123456789abcdef");
        builder.UseSetting("Admin:Login", AdminLogin);
        builder.UseSetting("Admin:Password", AdminPassword);
        if (time is not null) builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton(time)));
    }
}
