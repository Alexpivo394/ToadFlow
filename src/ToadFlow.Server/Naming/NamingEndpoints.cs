using Alexpivo394.Revit.ParamCheck.Profiles;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Naming;

/// <param name="Config">Настройки для предпросмотра — ещё не сохранённые; null — сохранённые.</param>
public sealed record NamingPreviewRequest(NamingConfig? Config, string[] Names);

public sealed record NamingPreviewRow(string Name, ModelClassification Result);

public sealed record KnownModel(string Name, string Path);

/// <summary>Модели, которые знает модуль (например, LOI — по своим проверкам), — для предпросмотра справочника на настоящих именах.</summary>
public interface INamingModelSource
{
    Task<IReadOnlyList<KnownModel>> ModelsAsync(CancellationToken ct);
}

/// <summary>
///     Справочник именования моделей — общий для модулей (LOI, коллизии, разделы пользователей), поэтому правится через ядро:
///     чтение — от наблюдателя, запись — администратор.
/// </summary>
public static class NamingEndpoints
{
    public const string Entity = "naming";

    /// <summary>Ключ модели для сравнения: <c>RSN:\\…</c> и <c>RSN://…</c> — одна модель, регистр не важен.</summary>
    public static string ModelKey(string modelPath, string modelName) =>
        ModelNames.Key(modelPath.Trim().Length > 0 ? modelPath : modelName).ToLowerInvariant();

    public static void MapNaming(this RouteGroupBuilder api)
    {
        var naming = api.MapGroup("/naming");
        naming.MapGet("/", (NamingStore store, CancellationToken ct) => store.GetAsync(ct));
        naming.MapGet("/defaults", () => NamingDefaults.Create());
        naming.MapPost("/preview", PreviewAsync);
        naming.MapGet("/models", KnownModelsAsync);
        naming.MapPut("/", async (NamingConfig config, NamingStore store, HttpContext http, IDomainEvents events, CancellationToken ct) =>
        {
            if (ModelNameParser.Validate(config) is { Count: > 0 } errors) return Results.BadRequest(new { error = string.Join("; ", errors), errors });
            await store.SaveAsync(config, ct);
            events.Publish(new CatalogChanged(http.User.Actor(), Entity, Guid.Empty, $"Изменён справочник именования моделей: записей {config.Entries.Count}"));
            return Results.Ok(config);
        }).RequireAuthorization(Policies.Admin);
    }

    private static async Task<IResult> PreviewAsync(NamingPreviewRequest request, NamingStore store, CancellationToken ct)
    {
        var config = request.Config ?? await store.GetAsync(ct);
        if (ModelNameParser.Validate(config) is { Count: > 0 } errors) return Results.BadRequest(new { error = string.Join("; ", errors), errors });

        var parser = new ModelNameParser(config);
        return Results.Ok((request.Names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Take(2000)
            .Select(name => new NamingPreviewRow(name.Trim(), parser.Parse(name.Trim()))).ToList());
    }

    /// <summary>Модели, которые сервис уже знает: от модулей и из расписаний — для предпросмотра справочника на настоящих именах.</summary>
    private static async Task<List<KnownModel>> KnownModelsAsync(ToadFlowDb db, IEnumerable<INamingModelSource> sources, CancellationToken ct)
    {
        var models = new Dictionary<string, KnownModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        foreach (var model in await source.ModelsAsync(ct))
            models.TryAdd(ModelKey(model.Path, model.Name), model);

        foreach (var schedule in await db.Schedules.AsNoTracking().ToListAsync(ct))
        {
            foreach (var model in schedule.LastKnownModels ?? [])
                models.TryAdd(ModelKey(model.ModelPath, model.Name), new KnownModel(Path.GetFileNameWithoutExtension(model.Name), model.ModelPath));
            foreach (var model in schedule.Selection.Models)
                models.TryAdd(ModelKey(model.ModelPath, model.Name), new KnownModel(Path.GetFileNameWithoutExtension(model.Name), model.ModelPath));
        }

        return models.Values.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
