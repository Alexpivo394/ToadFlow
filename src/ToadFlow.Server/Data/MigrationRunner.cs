using Microsoft.EntityFrameworkCore;

namespace ToadFlow.Server.Data;

/// <summary>
///     Накатывает миграции при старте. Зарегистрирован первым из фоновых служб: хост запускает их по порядку,
///     поэтому все остальные службы видят уже готовую схему.
/// </summary>
public sealed class MigrationRunner(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ToadFlowDb>().Database.MigrateAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
