using System.Text.Json;

namespace ToadFlow.Server.Usage.Ingest;

/// <summary>
///     Пачка записей от ToadTools: <c>{ "version": 1, "items": [ { "type": "...", ... } ] }</c>. Версия формата нужна сборщику
///     на будущее; сервер принимает любую, разбирая записи, которые знает, и пропуская остальные.
/// </summary>
public sealed record IngestBatch(int Version, IReadOnlyList<JsonElement> Items)
{
    public const int MaxItems = 200;
}

/// <param name="Accepted">Сколько записей сохранено (или обновило нарастающий итог).</param>
/// <param name="Skipped">Сколько пропущено: неизвестный вид, неверные поля, уже сохранённая операция.</param>
public sealed record IngestResult(int Accepted, int Skipped);

/// <summary>Допустимые значения строковых полей записей.</summary>
internal static class UsageValues
{
    public static readonly string[] SessionEnds = ["Running", "Normal", "Abnormal"];
    public static readonly string[] OperationKinds = ["Open", "Link", "Sync", "Save", "Reload"];
    public static readonly string[] Outcomes = ["Succeeded", "Failed", "Cancelled"];
    public static readonly string[] Storages = ["File", "RevitServer", "Cloud"];
    public static readonly string[] CommandSources = ["ToadTools", "Revit", "Addin"];
    public static readonly string[] DiskKinds = ["Ssd", "Hdd", "Unknown"];

    /// <summary>Ключ машины — имя компьютера в нижнем регистре.</summary>
    public static string MachineKey(string name) => name.Trim().ToLowerInvariant();

    /// <summary>Путь модели для сравнения: нижний регистр и <c>/</c> вместо <c>\</c>.</summary>
    public static string ModelKey(string path) => path.Trim().Replace('\\', '/').ToLowerInvariant();
}
