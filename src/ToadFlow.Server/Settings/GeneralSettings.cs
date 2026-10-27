using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Settings;

/// <summary>Раздел «Общие». Значения по умолчанию — из спеки.</summary>
public sealed record GeneralSettings(
    string TimeZoneId = "Europe/Moscow",
    int DefaultModelTimeoutMinutes = 30,
    int SilenceTimeoutMinutes = 10,
    int LogRetentionDays = 90,
    int RecycleAfterModels = 20,
    int RecycleMemoryMb = 12000,
    int RamReserveMb = AgentSettingsDto.DefaultRamReserveMb,
    int RamPerJobMb = AgentSettingsDto.DefaultRamPerJobMb)
{
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId ?? "", out _)) errors.Add($"Неизвестный часовой пояс «{TimeZoneId}»");
        if (DefaultModelTimeoutMinutes < 1) errors.Add("Таймаут на модель — не меньше 1 минуты");
        if (SilenceTimeoutMinutes < 1) errors.Add("Молчание Revit до перезапуска — не меньше 1 минуты");
        if (LogRetentionDays < 1) errors.Add("Хранить логи — не меньше 1 дня");
        if (RecycleAfterModels < 1) errors.Add("Перезапуск Revit — не реже чем после 1 модели");
        if (RecycleMemoryMb < 1000) errors.Add("Порог памяти для перезапуска Revit — не меньше 1000 МБ");
        if (RamReserveMb < 512) errors.Add("Резерв памяти для Windows — не меньше 512 МБ");
        if (RamPerJobMb < 512) errors.Add("Память на задание — не меньше 512 МБ");
        return errors;
    }

    /// <summary>Что поменялось — для «Событий системы».</summary>
    public string DescribeChanges(GeneralSettings old)
    {
        var parts = new List<string>();
        if (old.TimeZoneId != TimeZoneId) parts.Add($"часовой пояс {old.TimeZoneId} → {TimeZoneId}");
        if (old.DefaultModelTimeoutMinutes != DefaultModelTimeoutMinutes)
            parts.Add($"таймаут на модель {old.DefaultModelTimeoutMinutes} → {DefaultModelTimeoutMinutes} мин");
        if (old.SilenceTimeoutMinutes != SilenceTimeoutMinutes) parts.Add($"молчание Revit {old.SilenceTimeoutMinutes} → {SilenceTimeoutMinutes} мин");
        if (old.LogRetentionDays != LogRetentionDays) parts.Add($"хранение логов {old.LogRetentionDays} → {LogRetentionDays} дн.");
        if (old.RecycleAfterModels != RecycleAfterModels) parts.Add($"перезапуск Revit после {old.RecycleAfterModels} → {RecycleAfterModels} моделей");
        if (old.RecycleMemoryMb != RecycleMemoryMb) parts.Add($"порог памяти Revit {old.RecycleMemoryMb} → {RecycleMemoryMb} МБ");
        if (old.RamReserveMb != RamReserveMb) parts.Add($"резерв памяти {old.RamReserveMb} → {RamReserveMb} МБ");
        if (old.RamPerJobMb != RamPerJobMb) parts.Add($"память на задание {old.RamPerJobMb} → {RamPerJobMb} МБ");
        return parts.Count == 0 ? "без изменений" : string.Join("; ", parts);
    }
}

public interface ISettingsStore
{
    Task<GeneralSettings> GetAsync(CancellationToken ct = default);

    Task SaveAsync(GeneralSettings settings, Actor actor, CancellationToken ct = default);
}

/// <summary>Хранит раздел «Общие» одной JSON-строкой в таблице настроек.</summary>
internal sealed class SettingsStore(ToadFlowDb db, IDomainEvents events) : ISettingsStore
{
    private const string Key = "general";

    public async Task<GeneralSettings> GetAsync(CancellationToken ct = default)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == Key, ct);
        return row is null ? new GeneralSettings() : JsonSerializer.Deserialize<GeneralSettings>(row.Value, ToadFlowDb.Json) ?? new GeneralSettings();
    }

    public async Task SaveAsync(GeneralSettings settings, Actor actor, CancellationToken ct = default)
    {
        var old = await GetAsync(ct);
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null) db.Settings.Add(row = new Setting { Key = Key });
        row.Value = JsonSerializer.Serialize(settings, ToadFlowDb.Json);
        await db.SaveChangesAsync(ct);
        events.Publish(new SettingsChanged(actor, settings.DescribeChanges(old)));
    }
}

public static class SettingsEndpoints
{
    public static void MapSettings(this RouteGroupBuilder api)
    {
        api.MapGet("/settings", (ISettingsStore store, CancellationToken ct) => store.GetAsync(ct));
        api.MapPut("/settings", async (GeneralSettings settings, ISettingsStore store, HttpContext http, CancellationToken ct) =>
        {
            var errors = settings.Validate();
            if (errors.Count > 0) return Results.BadRequest(new { error = string.Join("; ", errors) });

            await store.SaveAsync(settings, http.User.Actor(), ct);
            return Results.Ok(settings);
        }).RequireAuthorization(Policies.Admin);
    }
}
