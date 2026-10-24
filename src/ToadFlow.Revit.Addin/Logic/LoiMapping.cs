using System.IO;
using System.Linq;
using Alexpivo394.Revit.ParamCheck.Core;
using Alexpivo394.Revit.ParamCheck.Services;
using Alexpivo394.Revit.ParamCheck.Summaries;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Loi;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>Задание LOI ↔ типы библиотеки проверки. Без Revit API — проверяется тестами.</summary>
public static class LoiMapping
{
    public static LoiJobPayload ParsePayload(string json) => AddinPipeJson.DeserializeObject<LoiJobPayload>(json);

    public static LoiCatalogPayload ParseCatalogPayload(string json) => AddinPipeJson.DeserializeObject<LoiCatalogPayload>(json);

    /// <summary>Ошибка, если задание для другой версии Revit; null — версия та.</summary>
    public static string? CheckYear(int payloadYear, int revitYear) =>
        payloadYear == revitYear ? null : $"Задание для Revit {payloadYear}, а этот экземпляр — Revit {revitYear}";

    /// <summary>Сервис никогда не обновляет модели: <c>AllowUpgrade = false</c>.</summary>
    public static CheckOptions ToOptions(LoiCheckSettingsDto s) => new()
    {
        SkipLinks = s.SkipLinks,
        WorksetKeyword = s.WorksetKeyword ?? "",
        ViewName = s.ViewName ?? "",
        ProblemsOnly = s.ProblemsOnly,
        AllowUpgrade = false
    };

    public static string ReportPath(LoiJobPayload payload) => Path.Combine(payload.OutputFolder, ReportNames.ForModel(payload.ModelName));

    /// <summary>
    ///     Дописывать ли модель в базу для дашбордов: проверенную и упавшую по вине модели — да; брошенную пользователем
    ///     и упавшую из-за Revit — нет, её повторят.
    /// </summary>
    public static bool GoesToHistory(ModelOutcome outcome) =>
        outcome.Status == ModelStatus.Checked || outcome is { Status: ModelStatus.Failed, RevitFailure: false };

    /// <summary>Итог проверки → итог задания, примечание и результат для истории и аналитики.</summary>
    /// <param name="profileFingerprint">Отпечаток правил профиля (<c>ProfileSerializer.Fingerprint</c>).</param>
    /// <param name="historyError">База для дашбордов не дописалась; null — дописалась или не ведётся.</param>
    public static (JobOutcome Outcome, string? Note, CheckSummary? Result) ToResult(ModelOutcome outcome, string profileFingerprint, string? historyError)
    {
        switch (outcome.Status)
        {
            case ModelStatus.Skipped:
                return (JobOutcome.Cancelled, "отменено пользователем", null);
            case ModelStatus.Failed:
                // Внутренняя ошибка Revit — не вина модели: сбой инфраструктуры, сервис повторит модель в новом Revit.
                return (outcome.RevitFailure ? JobOutcome.InfrastructureFailure : JobOutcome.Failed, outcome.Error, null);
        }

        var result = CheckSummaries.From(outcome, profileFingerprint);
        return historyError is null
            ? (JobOutcome.Succeeded, null, result)
            : (JobOutcome.Warning, $"база для дашбордов не дописана: {historyError}", result);
    }

    public static LoiCatalogResult ToCatalogResult(CatalogSnapshot snapshot) => new(
        snapshot.Categories.Select(c => new LoiCategoryDto(c.Key, c.Name, c.IsModel)).ToArray(),
        snapshot.Parameters.ToArray(),
        snapshot.HasSharedParameterFile);
}
