using Alexpivo394.Revit.ParamCheck.Profiles;
using Alexpivo394.Revit.ParamCheck.Summaries;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Loi.Naming;

namespace ToadFlow.Server.Loi.Analytics;

/// <summary>Откуда пришла проверка: задание расписания или ручная проверка пользователя.</summary>
public sealed record CheckOrigin(LoiCheckSource Source, Guid? RunId = null, Guid? ScheduleId = null, Guid? UserId = null);

/// <summary>Классификация модели: заказчик, объект, раздел, часть и свои поля.</summary>
public sealed record ModelPlacement(string? Customer, string? Object, string? Discipline, string? Part, Dictionary<string, string> Fields);

/// <summary>Складывает итоги проверок в таблицы аналитики и раскладывает модели по справочнику именования.</summary>
public sealed class CheckRecorder(ToadFlowDb db, LoiSettingsStore settings)
{
    /// <summary>Путь модели для сравнения: <c>RSN:\\…</c> и <c>RSN://…</c> — одна модель, регистр не важен.</summary>
    public static string ModelKey(string modelPath, string modelName) =>
        ModelNames.Key(modelPath.Trim().Length > 0 ? modelPath : modelName).ToLowerInvariant();

    /// <summary>Ключ поправки «по имени»: она действует на все модели с этим именем, где бы они ни лежали.</summary>
    public static string NameKey(string modelNameOrPath) => ModelKey("", ModelNames.FromPath(modelNameOrPath));

    /// <summary>Похоже ли на путь к модели, а не на одно имя.</summary>
    public static bool IsPath(string text) => text.IndexOfAny(['\\', '/', ':']) >= 0;

    /// <returns>Записана ли проверка: false — аналитика выключена или проверка с этим id уже есть.</returns>
    public async Task<bool> RecordAsync(Guid id, CheckSummary summary, CheckOrigin origin, CancellationToken ct)
    {
        if (!(await settings.AnalyticsAsync(ct)).Enabled) return false;
        if (await db.Set<LoiCheck>().AnyAsync(c => c.Id == id, ct)) return false;

        var key = ModelKey(summary.ModelPath, summary.ModelName);
        var placement = await PlaceAsync(new ModelNameParser(await settings.NamingAsync(ct)), key, summary.ModelPath, summary.ModelName, ct);
        db.Set<LoiCheck>().Add(new LoiCheck
        {
            Id = id,
            CheckedAt = summary.CheckedAt.ToUniversalTime(),
            Source = origin.Source,
            RunId = origin.RunId,
            ScheduleId = origin.ScheduleId,
            UserId = origin.UserId,
            ModelKey = key,
            ModelName = summary.ModelName,
            ModelPath = summary.ModelPath,
            Customer = placement.Customer,
            Object = placement.Object,
            Discipline = placement.Discipline,
            Part = placement.Part,
            Fields = placement.Fields,
            ProfileName = summary.ProfileName,
            ProfileFingerprint = summary.ProfileFingerprint,
            ViewName = summary.ViewName,
            DurationSeconds = summary.DurationSeconds,
            Elements = summary.Elements,
            CompleteElements = summary.CompleteElements,
            Values = summary.Values,
            Filled = summary.Filled,
            Empty = summary.Empty,
            Missing = summary.Missing,
            Rules = summary.Rules.Select((rule, position) => new LoiCheckRule
            {
                CheckId = id,
                Position = position,
                Title = rule.Title,
                Elements = rule.Elements,
                CompleteElements = rule.CompleteElements,
                Values = rule.Values,
                Filled = rule.Filled,
                Empty = rule.Empty,
                Missing = rule.Missing,
                Parameters = rule.Parameters.Select(p => new LoiCheckParameter { CheckId = id, Name = p.Name, Filled = p.Filled, Empty = p.Empty, Missing = p.Missing })
                    .ToList()
            }).ToList()
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    ///     Заново раскладывает уже записанные проверки — после правки справочника или ручной поправки.
    /// </summary>
    /// <param name="modelKey">
    ///     Только эта модель — или, если это ключ поправки по имени (<see cref="NameKey" />), все модели с этим именем;
    ///     null — все модели.
    /// </param>
    /// <returns>Сколько моделей пересчитано.</returns>
    public async Task<int> ReclassifyAsync(string? modelKey, CancellationToken ct)
    {
        var parser = new ModelNameParser(await settings.NamingAsync(ct));
        var models = (await db.Set<LoiCheck>().AsNoTracking().GroupBy(c => c.ModelKey)
                .Select(g => g.OrderByDescending(c => c.CheckedAt).Select(c => new { c.ModelKey, c.ModelPath, c.ModelName }).First())
                .ToListAsync(ct))
            .Where(m => modelKey is null || m.ModelKey == modelKey || NameKey(m.ModelName) == modelKey)
            .ToList();

        foreach (var model in models)
        {
            var placement = await PlaceAsync(parser, model.ModelKey, model.ModelPath, model.ModelName, ct);
            await db.Set<LoiCheck>().Where(c => c.ModelKey == model.ModelKey).ExecuteUpdateAsync(c => c
                .SetProperty(x => x.Customer, placement.Customer)
                .SetProperty(x => x.Object, placement.Object)
                .SetProperty(x => x.Discipline, placement.Discipline)
                .SetProperty(x => x.Part, placement.Part)
                .SetProperty(x => x.Fields, placement.Fields), ct);
        }

        return models.Count;
    }

    /// <summary>Классификация модели: справочник именования, поверх — ручная поправка (по пути главнее, чем по имени).</summary>
    public async Task<ModelPlacement> PlaceAsync(ModelNameParser parser, string modelKey, string modelPath, string modelName, CancellationToken ct)
    {
        var parsed = parser.Parse(modelPath.Trim().Length > 0 ? modelPath : modelName);
        var nameKey = NameKey(modelName.Trim().Length > 0 ? modelName : modelPath);
        var manual = (await db.Set<LoiModelOverride>().AsNoTracking().Where(o => o.ModelKey == modelKey || o.ModelKey == nameKey).ToListAsync(ct))
            .OrderBy(o => o.ModelKey == modelKey ? 0 : 1).FirstOrDefault();
        return new ModelPlacement(
            Blank(manual?.Customer) ?? parsed.Customer,
            Blank(manual?.Object) ?? parsed.Object,
            Blank(manual?.Discipline) ?? parsed.Discipline,
            Blank(manual?.Part) ?? parsed.Part,
            parsed.Fields.ToDictionary());
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
