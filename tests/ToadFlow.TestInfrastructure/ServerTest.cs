using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Основа интеграционного теста: свежая база и сервер на каждый тест.</summary>
public abstract class ServerTest
{
    public const string UserPassword = "user-pass-123";

    [ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
    public required PostgresFixture Postgres { get; init; }

    protected ServerFactory Server { get; private set; } = null!;

    /// <summary>Подменное время для теста; задаётся до старта сервера (в конструкторе наследника).</summary>
    protected TimeProvider? Time { get; init; }

    [Before(Test)]
    public async Task StartServerAsync()
    {
        _connectionString = await Postgres.CreateDatabaseAsync();
        Server = new ServerFactory(_connectionString, Time);
        _ = Server.Server; // старт хоста и миграции — до первого запроса
    }

    [After(Test)]
    public async Task StopServerAsync()
    {
        await Server.DisposeAsync();
        await Postgres.DropDatabaseAsync(_connectionString);
    }

    private string _connectionString = "";

    /// <summary>Работа с базой в отдельной области, как в обработчике запроса.</summary>
    protected async Task<T> DbAsync<T>(Func<ToadFlowDb, Task<T>> action)
    {
        await using var scope = Server.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ToadFlowDb>());
    }

    protected Task DbAsync(Func<ToadFlowDb, Task> action) => DbAsync(async db =>
    {
        await action(db);
        return true;
    });

    /// <summary>Создаёт пользователя с ролью и паролем <see cref="UserPassword" />, входит им и возвращает клиент с токеном.</summary>
    protected async Task<HttpClient> ClientAsync(UserRole role)
    {
        var login = $"{role}-{Guid.NewGuid():N}"[..20];
        await DbAsync(async db =>
        {
            var user = User.Create(login, $"Тест {role}", role, "", DateTimeOffset.UtcNow);
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, UserPassword);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });
        return await LoginAsync(login, UserPassword);
    }

    protected async Task<HttpClient> LoginAsync(string login, string password)
    {
        var client = Server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(login, password));
        return client;
    }

    protected async Task<string> TokenAsync(string login, string password)
    {
        var response = await Server.CreateClient().PostJsonAsync("/api/auth/login", new { login, password });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync<LoginBody>()).Token;
    }

    /// <summary>Ждёт, пока условие станет истинным: фоновые службы и события работают асинхронно.</summary>
    protected static async Task EventuallyAsync(Func<Task<bool>> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Условие не выполнилось за отведённое время");
            await Task.Delay(50);
        }
    }

    protected Task<HttpClient> AdminAsync() => LoginAsync(ServerFactory.AdminLogin, ServerFactory.AdminPassword);

    private sealed record LoginBody(string Token);
}
