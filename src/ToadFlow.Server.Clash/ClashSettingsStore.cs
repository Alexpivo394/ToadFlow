using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Clash;

/// <param name="KeepSnapshots">Сколько последних слепков хранить у каждой модели.</param>
public sealed record ClashStorageSettings(int KeepSnapshots = 3)
{
    public IReadOnlyList<string> Validate() => KeepSnapshots is < 1 or > 20 ? ["Хранить выгрузок — от 1 до 20"] : [];
}

/// <summary>Настройки модуля «Коллизии» в общей таблице настроек.</summary>
public sealed class ClashSettingsStore(ToadFlowDb db)
{
    private const string StorageKey = "clash.storage";

    public async Task<ClashStorageSettings> StorageAsync(CancellationToken ct = default)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == StorageKey, ct);
        return row is null ? new ClashStorageSettings() : JsonSerializer.Deserialize<ClashStorageSettings>(row.Value, ToadFlowDb.Json) ?? new ClashStorageSettings();
    }

    public async Task SaveStorageAsync(ClashStorageSettings settings, CancellationToken ct = default)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == StorageKey, ct);
        if (row is null) db.Settings.Add(row = new Setting { Key = StorageKey });
        row.Value = JsonSerializer.Serialize(settings, ToadFlowDb.Json);
        await db.SaveChangesAsync(ct);
    }
}
