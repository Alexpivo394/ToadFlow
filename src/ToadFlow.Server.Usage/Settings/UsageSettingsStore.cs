using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Usage.Settings;

/// <param name="RetentionMonths">Сколько месяцев хранить данные. 13 — чтобы месяц сравнивался с тем же месяцем год назад.</param>
public sealed record UsageSettings(int RetentionMonths = 13)
{
    public const int MinMonths = 1;
    public const int MaxMonths = 120;

    public IReadOnlyList<string> Validate() =>
        RetentionMonths is < MinMonths or > MaxMonths ? [$"Срок хранения — от {MinMonths} до {MaxMonths} месяцев"] : [];
}

/// <summary>Настройки модуля «Аналитика» в общей таблице настроек (ключ <c>usage.settings</c>).</summary>
public sealed class UsageSettingsStore(ToadFlowDb db)
{
    public const string Key = "usage.settings";

    public async Task<UsageSettings> GetAsync(CancellationToken ct = default)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == Key, ct);
        return row is null ? new UsageSettings() : JsonSerializer.Deserialize<UsageSettings>(row.Value, ToadFlowDb.Json) ?? new UsageSettings();
    }

    public async Task SaveAsync(UsageSettings settings, CancellationToken ct = default)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null) db.Settings.Add(row = new Setting { Key = Key });
        row.Value = JsonSerializer.Serialize(settings, ToadFlowDb.Json);
        await db.SaveChangesAsync(ct);
    }
}
