using System;
using System.IO;

namespace ToadFlow.Contracts.Clash;

/// <summary>Задание агенту: выгрузить геометрию и параметры одной модели для поиска коллизий.</summary>
/// <param name="ModelKey">Ключ модели в слепке (<c>ClashModel.Id</c> в форме <c>N</c>): с ним сервер сверяет загруженный файл.</param>
/// <param name="ViewName">3D-вид — фильтр: выгружаются только видимые на нём элементы.</param>
/// <param name="WorksetKeyword">Рабочие наборы с этим словом в имени не открываются; пусто — открываются все.</param>
public sealed record ClashExtractPayload(
    string ModelName,
    string ModelPath,
    bool IsServer,
    int RevitYear,
    string ModelKey,
    string ViewName,
    string WorksetKeyword);

/// <summary>Аддин → агент: слепок записан в локальную папку, его осталось загрузить на сервер.</summary>
/// <param name="Folder">Папка с <c>model.tcg</c> и <c>model.tcp</c> внутри <see cref="ClashStaging.Root" />.</param>
public sealed record ClashStagedSnapshot(string Folder, int Elements, long Triangles, int Inventory);

/// <summary>Агент → сервер: итог задания <see cref="ModuleKinds.ClashExtract" />, слепок уже опубликован.</summary>
public sealed record ClashExtractResult(Guid SnapshotId, int Elements, long Triangles, int Inventory, long GeometryBytes, long ParametersBytes);

/// <summary>Начать загрузку слепка задания: размеры и SHA-256 (hex) обоих файлов.</summary>
public sealed record ClashUploadRequest(Guid JobId, long GeometryBytes, string GeometrySha256, long ParametersBytes, string ParametersSha256);

/// <summary>Сколько байт каждого файла сервер уже принял: с этого места продолжается загрузка.</summary>
public sealed record ClashUploadState(Guid UploadId, long GeometryReceived, long ParametersReceived);

/// <summary>Где аддин оставляет слепок для агента. Revit и агент работают под одним пользователем Windows.</summary>
public static class ClashStaging
{
    /// <summary><c>%LocalAppData%\ToadFlow\clash-staging</c>.</summary>
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToadFlow", "clash-staging");

    public static string Folder(Guid jobId) => Path.Combine(Root, jobId.ToString("N"));

    /// <summary>Путь лежит внутри <see cref="Root" /> (не сам корень), без выхода наружу через <c>..</c>.</summary>
    public static bool IsInside(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>Правила загрузки слепка на сервер.</summary>
public static class ClashUploads
{
    /// <summary>Наибольшая часть после распаковки: меньше лимита nginx на тело запроса (10 МБ).</summary>
    public const int MaxChunkBytes = 8 * 1024 * 1024;

    public const string Geometry = "geometry";
    public const string Parameters = "parameters";

    /// <summary>Имя файла слепка по имени части в адресе; null — такой части нет.</summary>
    public static string? FileName(string file) => file switch
    {
        Geometry => "model.tcg",
        Parameters => "model.tcp",
        _ => null
    };
}
