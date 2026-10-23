using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Auth;

/// <summary>Создаёт первого администратора из настроек <c>Admin:Login</c>/<c>Admin:Password</c>, если пользователей нет.</summary>
public sealed class AdminSeeder(IServiceScopeFactory scopes, IOptions<AdminSeedOptions> options, TimeProvider time, ILogger<AdminSeeder> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        if (await db.Users.AnyAsync(ct)) return;

        var seed = options.Value;
        if (string.IsNullOrWhiteSpace(seed.Login) || string.IsNullOrWhiteSpace(seed.Password))
        {
            logger.LogWarning("Пользователей нет, а Admin:Login/Admin:Password не заданы — войти будет некому");
            return;
        }

        var admin = User.Create(seed.Login, "Администратор", UserRole.Admin, "", time.GetUtcNow());
        admin.PasswordHash = new PasswordHasher<User>().HashPassword(admin, seed.Password);
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Создан администратор {Login}", admin.Login);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
