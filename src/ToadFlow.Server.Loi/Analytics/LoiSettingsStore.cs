using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Loi.Naming;

namespace ToadFlow.Server.Loi.Analytics;

/// <param name="Enabled">Сохранять итоги проверок в базу и показывать страницу аналитики. Выключено — в базу ничего не пишется.</param>
/// <param name="RetentionYears">Сколько лет хранить проверки; 0 — всегда. Отдельно от срока логов: смысл аналитики — история.</param>
public sealed record AnalyticsSettings(bool Enabled = true, int RetentionYears = 0)
{
    public IReadOnlyList<string> Validate() => RetentionYears is < 0 or > 100 ? ["Срок хранения аналитики — от 0 (всегда) до 100 лет"] : [];
}

/// <summary>Настройки модуля LOI в общей таблице настроек: справочник именования и аналитика.</summary>
public sealed class LoiSettingsStore(ToadFlowDb db)
{
    private const string NamingKey = "loi.naming";
    private const string AnalyticsKey = "loi.analytics";

    public async Task<NamingConfig> NamingAsync(CancellationToken ct = default) => await ReadAsync<NamingConfig>(NamingKey, ct) ?? NamingDefaults.Create();

    public Task SaveNamingAsync(NamingConfig config, CancellationToken ct = default) => WriteAsync(NamingKey, config, ct);

    public async Task<AnalyticsSettings> AnalyticsAsync(CancellationToken ct = default) => await ReadAsync<AnalyticsSettings>(AnalyticsKey, ct) ?? new AnalyticsSettings();

    public Task SaveAnalyticsAsync(AnalyticsSettings settings, CancellationToken ct = default) => WriteAsync(AnalyticsKey, settings, ct);

    private async Task<T?> ReadAsync<T>(string key, CancellationToken ct) where T : class
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct);
        return row is null ? null : JsonSerializer.Deserialize<T>(row.Value, ToadFlowDb.Json);
    }

    private async Task WriteAsync<T>(string key, T value, CancellationToken ct)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(row = new Setting { Key = key });
        row.Value = JsonSerializer.Serialize(value, ToadFlowDb.Json);
        await db.SaveChangesAsync(ct);
    }
}
