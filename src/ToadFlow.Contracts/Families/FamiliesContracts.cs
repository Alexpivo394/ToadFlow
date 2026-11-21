using System;
using System.Collections.Generic;
using Alexpivo394.Revit.Families.Files;

namespace ToadFlow.Contracts.Families;

/// <summary>Сервер просит агента библиотеки обойти её.</summary>
/// <param name="ScanId">Номер обхода: с ним агент присылает отчёт.</param>
/// <param name="Root">Корень библиотеки — путь UNC или с буквой диска.</param>
/// <param name="ReviewFolder">Имя папки проверки в корне.</param>
public sealed record FamilyScanRequestDto(Guid ScanId, string Root, string ReviewFolder);

/// <summary>Файл семейства, который видел обход.</summary>
/// <param name="RelativePath">Путь от корня библиотеки с разделителем <c>\</c>, как он сейчас на диске.</param>
/// <param name="Hash">SHA-256 файла <c>.rfa</c>, 64 шестнадцатеричных символа в нижнем регистре.</param>
/// <param name="CatalogHash">SHA-256 каталога типоразмеров <c>.txt</c> рядом; null — каталога нет.</param>
/// <param name="RevitYear">Версия Revit, в которой файл сохранён; null — прочитать не удалось.</param>
public sealed record FamilyScanFileDto(string RelativePath, long Size, DateTime ModifiedUtc, string Hash, string? CatalogHash, int? RevitYear);

/// <summary>Отчёт обхода. Агент отправляет его <c>POST /api/agent/families/scans/{ScanId}</c>, тело сжато gzip.</summary>
/// <param name="Complete">Прочитаны все папки; false — какие-то нет (см. <paramref name="Errors" />), и исчезнувшие файлы в архив не уходят.</param>
/// <param name="Errors">Папки, которые не прочитались, с причиной.</param>
/// <param name="Unsettled">
///     Файлы, которые обход видел, но ещё не взял: дописываются, копируются, открыты программой или копия версии не удалась.
///     Файл, которого нет ни в <paramref name="Files" />, ни здесь, сервер считает исчезнувшим.
/// </param>
public sealed record FamilyScanReportDto(
    Guid ScanId,
    bool Complete,
    IReadOnlyList<FamilyScanFileDto> Files,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Unsettled);

/// <summary>Задание ферме <see cref="ModuleKinds.FamiliesRead" />: прочитать содержимое одной версии семейства.</summary>
/// <param name="Path">Полный путь к файлу в библиотеке.</param>
/// <param name="Hash">Хеш, который видел обход: файл с другим хешем уже изменился, и читать его под этой версией нельзя.</param>
/// <param name="CatalogPath">Полный путь к каталогу типоразмеров; null — каталога нет.</param>
public sealed record FamilyReadPayload(Guid VersionId, string Path, string Hash, string? CatalogPath);

/// <summary>Итог задания чтения.</summary>
/// <param name="PreviewPngBase64">Картинка семейства в PNG; null — снять не удалось.</param>
public sealed record FamilyReadResult(FamilyContent Content, string? PreviewPngBase64);

/// <summary>Что сделать с файлом библиотеки по кнопке в вебе.</summary>
public enum FamilyFileActionKind
{
    /// <summary>Переложить <c>RelativePath</c> в <c>TargetPath</c> вместе с каталогом типоразмеров.</summary>
    Move,

    /// <summary>Убрать файл и его каталог из библиотеки; содержимое остаётся в <c>.toadflow\history</c>.</summary>
    Delete,

    /// <summary>Положить содержимое из <c>.toadflow\history</c> по хешу на <c>RelativePath</c>.</summary>
    Restore
}

/// <summary>
///     Действие с файлом библиотеки. Агент библиотеки проверяет хеши до любого изменения: файл, который успели поменять, не трогается.
///     Всё, что убирается или перезаписывается, сначала сохраняется в <c>.toadflow\history</c>.
/// </summary>
/// <param name="Root">Корень библиотеки.</param>
/// <param name="RelativePath">Файл, с которым действие; для <see cref="FamilyFileActionKind.Restore" /> — куда положить.</param>
/// <param name="TargetPath">Для <see cref="FamilyFileActionKind.Move" /> — куда переложить.</param>
/// <param name="Hash">
///     Хеш <c>.rfa</c>: для <see cref="FamilyFileActionKind.Move" /> и <see cref="FamilyFileActionKind.Delete" /> — какой файл ожидается на
///     месте; для <see cref="FamilyFileActionKind.Restore" /> — какое содержимое взять из истории.
/// </param>
/// <param name="CatalogHash">Каталог типоразмеров этого содержимого; null — каталога нет.</param>
/// <param name="ReplaceHash">Цель уже занята файлом с этим хешем, и его можно заменить; null — цель должна быть свободна.</param>
public sealed record FamilyFileActionDto(
    FamilyFileActionKind Kind,
    string Root,
    string RelativePath,
    string? TargetPath,
    string Hash,
    string? CatalogHash,
    string? ReplaceHash);

/// <param name="Error">Почему не сделано; null — сделано.</param>
public sealed record FamilyFileActionResult(string? Error);
