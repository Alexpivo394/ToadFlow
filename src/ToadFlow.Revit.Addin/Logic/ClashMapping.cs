using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Alexpivo394.Revit.Clash.Revit;
using Alexpivo394.Revit.Export.Core;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>Задание выгрузки для коллизий ↔ типы библиотек выгрузки. Без Revit API — проверяется тестами.</summary>
public static class ClashMapping
{
    private static readonly NumberFormatInfo Thousands = new() { NumberGroupSeparator = " ", NumberGroupSizes = [3] };

    public static ClashExtractPayload ParsePayload(string json) => AddinPipeJson.DeserializeObject<ClashExtractPayload>(json);

    /// <summary>Ошибка, если задание для другой версии Revit; null — версия та.</summary>
    public static string? CheckYear(int payloadYear, int revitYear) =>
        payloadYear == revitYear ? null : $"Задание для Revit {payloadYear}, а этот экземпляр — Revit {revitYear}";

    /// <summary>
    ///     Как открыть модель: с отсоединением, без связей и без обновления — как для выгрузки NWC. Остальные шаги экспорта
    ///     (очистка, сжатие, удаление связей) к открытию не относятся.
    /// </summary>
    public static ExportOptions ToOpenOptions(ClashExtractPayload payload) => new()
    {
        SkipLinks = true,
        Audit = false,
        WorksetKeyword = payload.WorksetKeyword ?? "",
        AllowUpgrade = false
    };

    public static ModelJob ToModelJob(ClashExtractPayload payload) => new(payload.ModelName, payload.ModelPath, payload.IsServer, false, false);

    /// <summary>Строка журнала о ходе выгрузки.</summary>
    public static string Describe(ExtractProgress progress) => progress.Stage switch
    {
        ExtractStage.Collecting => "сбор видимых элементов",
        ExtractStage.Elements => $"элементы: {Number(progress.Done)} из {Number(progress.Total)}",
        _ => $"опись модели: {Number(progress.Done)} из {Number(progress.Total)}"
    };

    /// <summary>
    ///     Исключение выгрузки → итог задания. Нет вида — ошибка модели, повтор не поможет. Внутренняя ошибка Revit, ввод-вывод
    ///     (диск полон, файл держит антивирус, сеть при копировании модели) и нехватка памяти — сбой инфраструктуры: сервис повторит
    ///     модель в новом Revit. Прочие ошибки аргументов — ошибка модели с исходным текстом.
    /// </summary>
    /// <param name="revitFailure">Исключение — внутренняя ошибка Revit (<c>Autodesk.Revit.Exceptions.InternalException</c>).</param>
    public static (JobOutcome Outcome, string Note) MapFailure(Exception exception, bool revitFailure, string viewName) => exception switch
    {
        OperationCanceledException => (JobOutcome.Cancelled, "отменено пользователем"),
        _ when revitFailure => (JobOutcome.InfrastructureFailure, exception.Message),
        System.IO.IOException or UnauthorizedAccessException => (JobOutcome.InfrastructureFailure, $"сбой записи на машине агента: {exception.Message}"),
        OutOfMemoryException => (JobOutcome.InfrastructureFailure, "не хватило памяти Revit на выгрузку геометрии"),
        // Так экстрактор говорит, что вида нет (GeometryExtractor, параметр viewName).
        ArgumentException { ParamName: "viewName" } => (JobOutcome.Failed, $"в модели нет 3D-вида «{viewName}»"),
        _ => (JobOutcome.Failed, exception.Message)
    };

    /// <summary>Строка журнала о неудачной выгрузке: отмена — без стека, остальное — с подробностями для разбора.</summary>
    public static string FailureLog(Exception exception) =>
        exception is OperationCanceledException ? "выгрузка отменена" : $"выгрузка не удалась: {exception}";

    public static ClashStagedSnapshot ToStaged(string folder, int elements, long triangles, int inventory) => new(folder, elements, triangles, inventory);

    /// <summary>
    ///     Строки журнала об отчёте выгрузки: пропущенные из-за ошибок Revit элементы (первые 20) и элементы далеко от
    ///     внутреннего начала. Выгрузка без них строк не добавляет.
    /// </summary>
    public static IReadOnlyList<string> DescribeReport(ExtractResult result)
    {
        var lines = new List<string>();
        if (result.Skipped.Count > 0)
        {
            lines.Add($"Пропущено элементов: {Number(result.Skipped.Count)}");
            lines.AddRange(result.Skipped.Take(ListedSkipped).Select(s => $"{s.UniqueId} (ID {s.ElementId}), {s.Stage}: {s.Message}"));
        }

        if (result.FarElements > 0)
            lines.Add($"{Number(result.FarElements)} элементов дальше 1 км от внутреннего начала — точность геометрии снижена. " +
                      "Перенесите модель ближе к внутреннему началу");
        return lines;
    }

    private const int ListedSkipped = 20;

    private static string Number(long value) => value.ToString("#,0", Thousands);
}
