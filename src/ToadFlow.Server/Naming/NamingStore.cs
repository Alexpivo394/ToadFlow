using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Naming;

/// <summary>
///     Справочник разбора имён моделей — один на сервис: им пользуются проверка LOI (профиль по разделу, аналитика)
///     и коллизии (разделы моделей части). Ключ настроек остался от LOI, где справочник появился первым.
/// </summary>
public sealed class NamingStore(ToadFlowDb db)
{
    private const string Key = "loi.naming";

    public async Task<NamingConfig> GetAsync(CancellationToken ct = default)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == Key, ct);
        return (row is null ? null : JsonSerializer.Deserialize<NamingConfig>(row.Value, ToadFlowDb.Json)) ?? NamingDefaults.Create();
    }

    public async Task SaveAsync(NamingConfig config, CancellationToken ct = default)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null) db.Settings.Add(row = new Setting { Key = Key });
        row.Value = JsonSerializer.Serialize(config, ToadFlowDb.Json);
        await db.SaveChangesAsync(ct);
    }
}
