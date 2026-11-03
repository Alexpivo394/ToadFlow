using Alexpivo394.Revit.Clash.Rules;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Можно ли прогнать конфигурацию части: модели с разделами и выгрузками, конфигурация без ошибок, есть что проверять.</summary>
public static class DraftRunChecks
{
    /// <summary>Причины, по которым прогон с нынешним составом части невозможен; пусто — можно.</summary>
    public static async Task<IReadOnlyList<string>> ProblemsAsync(Guid partId, ClashConfiguration config, PartModels models, CancellationToken ct) =>
        Problems(await models.OfPartAsync(partId, ct), config);

    /// <summary>Причины, по которым прогон конфигурации на этих моделях невозможен; пусто — можно.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<PartModelView> partModels, ClashConfiguration config)
    {
        var errors = new List<string>();
        if (partModels.Count == 0) errors.Add("В части нет моделей");
        errors.AddRange(partModels.Where(m => m.Discipline is null).Select(m => $"{m.Name}: раздел не определён — задайте его вручную"));
        errors.AddRange(partModels.Where(m => m.Latest is null).Select(m => $"{m.Name}: модель ещё не выгружалась"));
        var disciplines = partModels.Select(m => m.Discipline).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        errors.AddRange(ConfigurationValidator.Problems(config, disciplines, partModels.Select(m => m.ModelId.ToString("N")).ToList()));
        if (config.Sets.Count == 0) errors.Add("В черновике нет наборов");
        if (!config.Matrix.Any(c => c.Enabled)) errors.Add("В матрице нет включённых проверок");
        return errors;
    }
}
