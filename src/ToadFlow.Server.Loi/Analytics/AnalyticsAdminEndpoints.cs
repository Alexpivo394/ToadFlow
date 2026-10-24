using Alexpivo394.Revit.ParamCheck.Summaries;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Loi.Naming;

namespace ToadFlow.Server.Loi.Analytics;

/// <param name="Id">Id проверки, который выдал плагин: при повторной отправке (сеть пропадала) проверка не задвоится.</param>
public sealed record ManualCheckRequest(Guid Id, CheckSummary Summary);

/// <param name="Reason">Почему не записана: аналитика выключена или проверка уже есть. Плагину повторять отправку не нужно.</param>
public sealed record ManualCheckResult(bool Recorded, string? Reason);

/// <param name="Config">Настройки для предпросмотра — ещё не сохранённые; null — сохранённые.</param>
public sealed record NamingPreviewRequest(NamingConfig? Config, string[] Names);

public sealed record NamingPreviewRow(string Name, ModelClassification Result);

public sealed record KnownModel(string Name, string Path);

public sealed record ModelOverrideRequest(string ModelPath, string ModelName, string? Customer, string? Object, string? Discipline, string? Part);

public sealed record ModelOverrideView(string ModelKey, string ModelName, string? Customer, string? Object, string? Discipline, string? Part, DateTimeOffset UpdatedAt);

/// <summary>Приём ручных проверок и настройки аналитики: справочник именования, поправки моделей, срок хранения.</summary>
internal static class AnalyticsAdminEndpoints
{
    public const string Entity = "loi-analytics";

    public static void MapLoiAnalyticsAdmin(this RouteGroupBuilder loi)
    {
        // Ручная проверка из плагина ParamChecker — любой пользователь со своим токеном, проверка записывается на него.
        loi.MapPost("/checks", async (ManualCheckRequest request, CheckRecorder recorder, HttpContext http, IDomainEvents events, CancellationToken ct) =>
        {
            if (request.Id == Guid.Empty || request.Summary is not { } summary) return Results.BadRequest(new { error = "Нет id проверки или её итогов" });
            if (string.IsNullOrWhiteSpace(summary.ModelName)) return Results.BadRequest(new { error = "Не указано имя модели" });
            if (summary.Elements < 0 || summary.Values < 0) return Results.BadRequest(new { error = "Счётчики не могут быть отрицательными" });

            var recorded = await recorder.RecordAsync(request.Id, summary, new CheckOrigin(LoiCheckSource.Manual, UserId: http.User.UserId()), ct);
            if (recorded) events.Publish(new LoiCheckRecorded(http.User.Actor(), request.Id));
            return Results.Ok(new ManualCheckResult(recorded, recorded ? null : "Аналитика на сервере выключена или проверка уже записана"));
        });

        loi.MapGet("/analytics/settings", (LoiSettingsStore store, CancellationToken ct) => store.AnalyticsAsync(ct));
        loi.MapGet("/naming", (LoiSettingsStore store, CancellationToken ct) => store.NamingAsync(ct));
        loi.MapPost("/naming/preview", PreviewAsync);
        loi.MapGet("/naming/models", KnownModelsAsync);
        loi.MapGet("/models/overrides", async (ToadFlowDb db) => await db.Set<LoiModelOverride>().AsNoTracking().OrderBy(o => o.ModelName)
            .Select(o => new ModelOverrideView(o.ModelKey, o.ModelName, o.Customer, o.Object, o.Discipline, o.Part, o.UpdatedAt)).ToListAsync());

        var admin = loi.MapGroup("").RequireAuthorization(Policies.Admin);
        admin.MapPut("/analytics/settings", async (AnalyticsSettings settings, LoiSettingsStore store, HttpContext http, IDomainEvents events, CancellationToken ct) =>
        {
            if (settings.Validate() is { Count: > 0 } errors) return Results.BadRequest(new { error = string.Join("; ", errors) });
            await store.SaveAnalyticsAsync(settings, ct);
            events.Publish(new CatalogChanged(http.User.Actor(), Entity, Guid.Empty,
                $"Аналитика LOI: {(settings.Enabled ? "включена" : "выключена")}, хранить {(settings.RetentionYears == 0 ? "всегда" : $"{settings.RetentionYears} лет")}"));
            return Results.Ok(settings);
        });

        admin.MapPut("/naming", async (NamingConfig config, LoiSettingsStore store, HttpContext http, IDomainEvents events, CancellationToken ct) =>
        {
            if (ModelNameParser.Validate(config) is { Count: > 0 } errors) return Results.BadRequest(new { error = string.Join("; ", errors), errors });
            await store.SaveNamingAsync(config, ct);
            events.Publish(new CatalogChanged(http.User.Actor(), Entity, Guid.Empty, $"Изменён справочник именования моделей: записей {config.Entries.Count}"));
            return Results.Ok(config);
        });

        admin.MapPost("/analytics/reclassify", async (CheckRecorder recorder, HttpContext http, IDomainEvents events, CancellationToken ct) =>
        {
            var models = await recorder.ReclassifyAsync(null, ct);
            events.Publish(new CatalogChanged(http.User.Actor(), Entity, Guid.Empty, $"Аналитика LOI пересчитана по справочнику именования: моделей {models}"));
            return Results.Ok(new { models });
        });

        admin.MapPut("/models/overrides", SaveOverrideAsync);
        admin.MapDelete("/models/overrides", async (string key, ToadFlowDb db, CheckRecorder recorder, CancellationToken ct) =>
        {
            await db.Set<LoiModelOverride>().Where(o => o.ModelKey == key).ExecuteDeleteAsync(ct);
            await recorder.ReclassifyAsync(key, ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> PreviewAsync(NamingPreviewRequest request, LoiSettingsStore store, CancellationToken ct)
    {
        var config = request.Config ?? await store.NamingAsync(ct);
        if (ModelNameParser.Validate(config) is { Count: > 0 } errors) return Results.BadRequest(new { error = string.Join("; ", errors), errors });

        var parser = new ModelNameParser(config);
        return Results.Ok((request.Names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Take(2000)
            .Select(name => new NamingPreviewRow(name.Trim(), parser.Parse(name.Trim()))).ToList());
    }

    /// <summary>Модели, которые сервис уже знает, — для предпросмотра справочника на настоящих именах.</summary>
    private static async Task<List<KnownModel>> KnownModelsAsync(ToadFlowDb db, CancellationToken ct)
    {
        var models = new Dictionary<string, KnownModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in await db.Set<LoiCheck>().AsNoTracking().Select(c => new { c.ModelKey, c.ModelName, c.ModelPath }).Distinct().ToListAsync(ct))
            models.TryAdd(check.ModelKey, new KnownModel(check.ModelName, check.ModelPath));

        foreach (var schedule in await db.Schedules.AsNoTracking().ToListAsync(ct))
        {
            foreach (var model in schedule.LastKnownModels ?? [])
                models.TryAdd(CheckRecorder.ModelKey(model.ModelPath, model.Name), new KnownModel(Path.GetFileNameWithoutExtension(model.Name), model.ModelPath));
            foreach (var model in schedule.Selection.Models)
                models.TryAdd(CheckRecorder.ModelKey(model.ModelPath, model.Name), new KnownModel(Path.GetFileNameWithoutExtension(model.Name), model.ModelPath));
        }

        return models.Values.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static async Task<IResult> SaveOverrideAsync(ModelOverrideRequest request, ToadFlowDb db, CheckRecorder recorder, HttpContext http,
        TimeProvider time, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModelPath) && string.IsNullOrWhiteSpace(request.ModelName))
            return Results.BadRequest(new { error = "Укажите модель" });

        var key = CheckRecorder.ModelKey(request.ModelPath ?? "", request.ModelName ?? "");
        var manual = await db.Set<LoiModelOverride>().FindAsync([key], ct);
        if (manual is null) db.Set<LoiModelOverride>().Add(manual = new LoiModelOverride { ModelKey = key });
        manual.ModelName = (request.ModelName ?? "").Trim();
        manual.Customer = Blank(request.Customer);
        manual.Object = Blank(request.Object);
        manual.Discipline = Blank(request.Discipline);
        manual.Part = Blank(request.Part);
        manual.UpdatedByUserId = http.User.UserId();
        manual.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await recorder.ReclassifyAsync(key, ct);
        return Results.Ok(new ModelOverrideView(key, manual.ModelName, manual.Customer, manual.Object, manual.Discipline, manual.Part, manual.UpdatedAt));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Пришла новая проверка — странице аналитики пора обновиться. В «События системы» не пишется: проверок много.</summary>
public sealed record LoiCheckRecorded(Actor Actor, Guid CheckId) : DomainEvent(Actor)
{
    public override string UiEntity => AnalyticsAdminEndpoints.Entity;
    public override string UiId => CheckId.ToString();
}
