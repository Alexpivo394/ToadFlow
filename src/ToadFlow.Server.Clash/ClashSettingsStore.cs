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

/// <summary>Пороги обзора координатора.</summary>
/// <param name="StaleDays">Выгрузка старше — «не выгрузилось».</param>
/// <param name="NoRunDays">Утверждённую проверку не прогоняли дольше — «давно не прогонялось».</param>
/// <param name="SpikeShare">Всплеск: новых больше этой доли от активных…</param>
/// <param name="SpikeMin">…и больше этого числа.</param>
/// <param name="StuckDays">Открытая коллизия без записей в ленте дольше — «висит».</param>
/// <param name="AgentsOfflineMinutes">Ни одного агента в сети дольше — «агенты не в сети»: выгрузки для коллизий не идут.</param>
public sealed record ClashOverviewSettings(int StaleDays = 7, int NoRunDays = 7, double SpikeShare = 0.10, int SpikeMin = 50, int StuckDays = 30,
    int AgentsOfflineMinutes = 10)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (StaleDays is < 1 or > 365) errors.Add("Выгрузка устарела — от 1 до 365 дней");
        if (NoRunDays is < 1 or > 365) errors.Add("Давно не прогонялось — от 1 до 365 дней");
        if (SpikeShare is <= 0 or > 1 || double.IsNaN(SpikeShare)) errors.Add("Доля всплеска — больше 0 % и не больше 100 %");
        if (SpikeMin is < 0 or > 1_000_000) errors.Add("Всплеск — от 0 до 1 000 000 новых");
        if (StuckDays is < 1 or > 3650) errors.Add("Висят — от 1 до 3650 дней");
        if (AgentsOfflineMinutes is < 1 or > 10_080) errors.Add("Агенты не в сети — от 1 до 10 080 минут");
        return errors;
    }
}

/// <summary>Настройки модуля «Коллизии» для страницы настроек: хранение и пороги обзора.</summary>
public sealed record ClashSettingsView(ClashStorageSettings Storage, ClashOverviewSettings Overview);

/// <summary>Настройки модуля «Коллизии» в общей таблице настроек.</summary>
public sealed class ClashSettingsStore(ToadFlowDb db)
{
    private const string StorageKey = "clash.storage";
    private const string OverviewKey = "clash.overview";

    public Task<ClashStorageSettings> StorageAsync(CancellationToken ct = default) => GetAsync(StorageKey, () => new ClashStorageSettings(), ct);

    public Task SaveStorageAsync(ClashStorageSettings settings, CancellationToken ct = default) => SaveAsync(StorageKey, settings, ct);

    public Task<ClashOverviewSettings> OverviewAsync(CancellationToken ct = default) => GetAsync(OverviewKey, () => new ClashOverviewSettings(), ct);

    public Task SaveOverviewAsync(ClashOverviewSettings settings, CancellationToken ct = default) => SaveAsync(OverviewKey, settings, ct);

    private async Task<T> GetAsync<T>(string key, Func<T> fallback, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct);
        return row is null ? fallback() : JsonSerializer.Deserialize<T>(row.Value, ToadFlowDb.Json) ?? fallback();
    }

    private async Task SaveAsync<T>(string key, T settings, CancellationToken ct)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(row = new Setting { Key = key });
        row.Value = JsonSerializer.Serialize(settings, ToadFlowDb.Json);
        await db.SaveChangesAsync(ct);
    }
}
