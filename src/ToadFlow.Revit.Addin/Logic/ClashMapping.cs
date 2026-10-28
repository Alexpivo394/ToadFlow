using System;
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
    ///     Исключение выгрузки → итог задания. Нет вида — ошибка модели, повтор не поможет. Внутренняя ошибка Revit — сбой
    ///     инфраструктуры: сервис повторит модель в новом Revit.
    /// </summary>
    /// <param name="revitFailure">Исключение — внутренняя ошибка Revit (<c>Autodesk.Revit.Exceptions.InternalException</c>).</param>
    public static (JobOutcome Outcome, string Note) MapFailure(Exception exception, bool revitFailure, string viewName) => exception switch
    {
        OperationCanceledException => (JobOutcome.Cancelled, "отменено пользователем"),
        _ when revitFailure => (JobOutcome.InfrastructureFailure, exception.Message),
        ArgumentException => (JobOutcome.Failed, $"в модели нет 3D-вида «{viewName}»"),
        _ => (JobOutcome.Failed, exception.Message)
    };

    /// <summary>Строка журнала о неудачной выгрузке: отмена — без стека, остальное — с подробностями для разбора.</summary>
    public static string FailureLog(Exception exception) =>
        exception is OperationCanceledException ? "выгрузка отменена" : $"выгрузка не удалась: {exception}";

    public static ClashStagedSnapshot ToStaged(string folder, int elements, long triangles, int inventory) => new(folder, elements, triangles, inventory);

    private static string Number(long value) => value.ToString("#,0", Thousands);
}
