using Alexpivo394.Revit.Clash.Rules;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Можно ли прогнать конфигурацию части: модели с разделами и выгрузками, конфигурация без ошибок, есть что проверять.</summary>
public static class DraftRunChecks
{
    /// <summary>Причины, по которым прогон невозможен; пусто — можно.</summary>
    public static async Task<IReadOnlyList<string>> ProblemsAsync(Guid partId, ClashConfiguration config, PartModels models, CancellationToken ct)
    {
        var partModels = await models.OfPartAsync(partId, ct);
        var errors = new List<string>();
        if (partModels.Count == 0) errors.Add("В части нет моделей");
        errors.AddRange(partModels.Where(m => m.Discipline is null).Select(m => $"{m.Name}: раздел не определён — задайте его вручную"));
        errors.AddRange(partModels.Where(m => m.Latest is null).Select(m => $"{m.Name}: модель ещё не выгружалась"));
        errors.AddRange(await DraftEndpoints.ProblemsAsync(partId, config, models, ct));
        if (config.Sets.Count == 0) errors.Add("В черновике нет наборов");
        if (!config.Matrix.Any(c => c.Enabled)) errors.Add("В матрице нет включённых проверок");
        return errors;
    }
}
