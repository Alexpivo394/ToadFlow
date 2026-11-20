using Alexpivo394.Revit.ParamCheck.Profiles;
using Alexpivo394.Revit.ParamCheck.Summaries;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Naming;

namespace ToadFlow.Server.Loi.Analytics;

/// <param name="Id">Id проверки, который выдал плагин: при повторной отправке (сеть пропадала) проверка не задвоится.</param>
public sealed record ManualCheckRequest(Guid Id, CheckSummary Summary);

/// <param name="Reason">Почему не записана: аналитика выключена или проверка уже есть. Плагину повторять отправку не нужно.</param>
public sealed record ManualCheckResult(bool Recorded, string? Reason);


public sealed record ModelOverrideRequest(string ModelPath, string ModelName, string? Customer, string? Object, string? Discipline, string? Part);

/// <param name="ByName">Поправка по имени — для всех моделей с этим именем; иначе <paramref name="ModelKey" /> — путь одной модели.</param>
public sealed record ModelOverrideView(string ModelKey, string ModelName, bool ByName, string? Customer, string? Object, string? Discipline, string? Part,
    DateTimeOffset UpdatedAt)
{
    public static ModelOverrideView Of(LoiModelOverride o) =>
        new(o.ModelKey, o.ModelName, !CheckRecorder.IsPath(o.ModelKey), o.Customer, o.Object, o.Discipline, o.Part, o.UpdatedAt);
}

/// <summary>Приём ручных проверок и настройки аналитики: поправки моделей, срок хранения. Справочник именования — в ядре (<c>/api/naming</c>).</summary>
internal static class AnalyticsAdminEndpoints
{
    public const string Entity = "loi-analytics";

    /// <summary>
    ///     Ручная проверка из плагина ParamChecker — статистику LOI отправляют только координаторы и администраторы, проверка
    ///     записывается на отправившего. Проектировщику и наблюдателю — 403: ParamChecker показывает «недостаточно прав».
    /// </summary>
    public static void MapLoiChecks(this RouteGroupBuilder members) =>
        members.MapPost("/loi/checks", async (ManualCheckRequest request, CheckRecorder recorder, HttpContext http, IDomainEvents events, CancellationToken ct) =>
        {
            if (request.Id == Guid.Empty || request.Summary is not { } summary) return Results.BadRequest(new { error = "Нет id проверки или её итогов" });
            if (string.IsNullOrWhiteSpace(summary.ModelName)) return Results.BadRequest(new { error = "Не указано имя модели" });
            if (summary.Elements < 0 || summary.Values < 0) return Results.BadRequest(new { error = "Счётчики не могут быть отрицательными" });

            var recorded = await recorder.RecordAsync(request.Id, summary, new CheckOrigin(LoiCheckSource.Manual, UserId: http.User.UserId()), ct);
            if (recorded) events.Publish(new LoiCheckRecorded(http.User.Actor(), request.Id));
            return Results.Ok(new ManualCheckResult(recorded, recorded ? null : "Аналитика на сервере выключена или проверка уже записана"));
        }).RequireAuthorization(Policies.Operator);

    public static void MapLoiAnalyticsAdmin(this RouteGroupBuilder loi)
    {

        loi.MapGet("/analytics/settings", (LoiSettingsStore store, CancellationToken ct) => store.AnalyticsAsync(ct));
        loi.MapGet("/models/overrides", async (ToadFlowDb db) => (await db.Set<LoiModelOverride>().AsNoTracking().OrderBy(o => o.ModelName).ToListAsync())
            .Select(ModelOverrideView.Of).ToList());

        var admin = loi.MapGroup("").RequireAuthorization(Policies.Admin);
        admin.MapPut("/analytics/settings", async (AnalyticsSettings settings, LoiSettingsStore store, HttpContext http, IDomainEvents events, CancellationToken ct) =>
        {
            if (settings.Validate() is { Count: > 0 } errors) return Results.BadRequest(new { error = string.Join("; ", errors) });
            await store.SaveAnalyticsAsync(settings, ct);
            events.Publish(new CatalogChanged(http.User.Actor(), Entity, Guid.Empty,
                $"Аналитика LOI: {(settings.Enabled ? "включена" : "выключена")}, хранить {(settings.RetentionYears == 0 ? "всегда" : $"{settings.RetentionYears} лет")}"));
            return Results.Ok(settings);
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

    private static async Task<IResult> SaveOverrideAsync(ModelOverrideRequest request, ToadFlowDb db, CheckRecorder recorder, HttpContext http,
        TimeProvider time, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModelPath) && string.IsNullOrWhiteSpace(request.ModelName))
            return Results.BadRequest(new { error = "Укажите модель" });

        // Путь — поправка одной модели; одно имя (без папок) — всех моделей с этим именем.
        var model = (string.IsNullOrWhiteSpace(request.ModelPath) ? request.ModelName : request.ModelPath)!.Trim();
        var byName = !CheckRecorder.IsPath(model);
        var key = byName ? CheckRecorder.NameKey(model) : CheckRecorder.ModelKey(model, "");
        var manual = await db.Set<LoiModelOverride>().FindAsync([key], ct);
        if (manual is null) db.Set<LoiModelOverride>().Add(manual = new LoiModelOverride { ModelKey = key });
        manual.ModelName = ModelNames.FromPath(model);
        manual.Customer = Blank(request.Customer);
        manual.Object = Blank(request.Object);
        manual.Discipline = Blank(request.Discipline);
        manual.Part = Blank(request.Part);
        manual.UpdatedByUserId = http.User.UserId();
        manual.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await recorder.ReclassifyAsync(key, ct);
        return Results.Ok(ModelOverrideView.Of(manual));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Пришла новая проверка — странице аналитики пора обновиться. В «События системы» не пишется: проверок много.</summary>
public sealed record LoiCheckRecorded(Actor Actor, Guid CheckId) : DomainEvent(Actor)
{
    public override string UiEntity => AnalyticsAdminEndpoints.Entity;
    public override string UiId => CheckId.ToString();
}

/// <summary>Модели из проверок LOI — для предпросмотра справочника именования.</summary>
internal sealed class LoiNamingModels(ToadFlowDb db) : INamingModelSource
{
    public async Task<IReadOnlyList<KnownModel>> ModelsAsync(CancellationToken ct) =>
        (await db.Set<LoiCheck>().AsNoTracking().Select(c => new { c.ModelName, c.ModelPath }).Distinct().ToListAsync(ct))
        .Select(c => new KnownModel(c.ModelName, c.ModelPath)).ToList();
}
