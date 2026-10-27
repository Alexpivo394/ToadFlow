using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Alexpivo394.Revit.Export.Core;
using ToadFlow.Contracts.Export;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>
///     Публикация готовых файлов в папку результатов так, чтобы прошлая успешная выгрузка не терялась.
///     <para>
///         Revit пишет RVT и NWC в локальную папку задания. Отсюда файл копируется во временный файл рядом с целевым
///         (та же папка — тот же том, в том числе на SMB-ресурсе) и только потом подменяет старый одной операцией
///         <see cref="File.Replace(string, string, string, bool)" />. Копирование по сети не атомарно — поэтому оно идёт
///         во временный файл; атомарна только подмена внутри одной папки.
///     </para>
///     <para>
///         Если подмена не удалась, старый файл остаётся (или возвращается из резервной копии подмены). Обрывки от упавших
///         публикаций — <c>имя.toadflow-*.tmp</c> и <c>.bak</c> — убираются при следующей публикации того же файла.
///     </para>
/// </summary>
public static class ResultPublisher
{
    /// <summary>Метка временных файлов публикации: <c>АР.nwc.toadflow-{guid}.tmp</c>.</summary>
    public const string Marker = ".toadflow-";

    private const string TempExtension = ".tmp";
    private const string BackupExtension = ".bak";
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>Обрывки моложе этого не трогаем: их может прямо сейчас писать другой экземпляр.</summary>
    internal static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(1);

    /// <summary>Только для тестов: вместо <see cref="File.Replace(string, string, string, bool)" />.</summary>
    internal static Action<string, string, string>? ReplaceOverride { get; set; }

    /// <summary>
    ///     Публикует выгруженные форматы из <paramref name="stagingFolder" /> в папку результатов задания. Формат, который не
    ///     удалось опубликовать, становится <see cref="StepResult.Failed" />, причина дописывается в <see cref="ModelResult.Note" />.
    /// </summary>
    /// <param name="log">Строка для журнала задания.</param>
    public static void PublishOutputs(ModelResult model, ExportJobPayload payload, string stagingFolder, Action<string> log)
    {
        if (model.SkippedByUser) return;

        var notes = new List<string>();
        if (model.Rvt == StepResult.Done && !TryPublish("RVT", payload.ModelName)) model.Rvt = StepResult.Failed;
        if (model.Nwc == StepResult.Done && !TryPublish("NWC", Path.GetFileNameWithoutExtension(payload.ModelName) + ".nwc")) model.Nwc = StepResult.Failed;

        if (notes.Count > 0) model.Note = string.IsNullOrEmpty(model.Note) ? string.Join("; ", notes) : model.Note + "; " + string.Join("; ", notes);
        return;

        bool TryPublish(string format, string fileName)
        {
            var stopwatch = Stopwatch.StartNew();
            var target = Path.Combine(payload.OutputFolder, fileName);
            try
            {
                var bytes = Publish(Path.Combine(stagingFolder, fileName), target);
                log($"{format} опубликован: {target}, {bytes / (1024.0 * 1024.0):0.#} МБ за {stopwatch.Elapsed.TotalSeconds:0.#} с");
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                var kept = File.Exists(target) ? "; прежний файл сохранён" : "";
                notes.Add($"{format}: не опубликован — {exception.Message}{kept}");
                log($"{format} не опубликован за {stopwatch.Elapsed.TotalSeconds:0.#} с: {exception}");
                return false;
            }
        }
    }

    /// <summary>Проверяет готовый файл и подменяет им <paramref name="target" />. Возвращает размер опубликованного файла.</summary>
    /// <exception cref="InvalidOperationException">Готовый файл пуст, не читается или копия не совпала по размеру.</exception>
    /// <exception cref="IOException">Не удалось скопировать или подменить; прежний <paramref name="target" /> не тронут.</exception>
    public static long Publish(string staged, string target)
    {
        var size = Verify(staged);
        var folder = Path.GetDirectoryName(Path.GetFullPath(target))!;
        Directory.CreateDirectory(folder);
        CleanupLeftovers(target);

        var token = Guid.NewGuid().ToString("N");
        var temp = target + Marker + token + TempExtension;
        var backup = target + Marker + token + BackupExtension;
        try
        {
            CopyVerified(staged, temp, size);
            if (File.Exists(target)) Replace(temp, target, backup);
            else File.Move(temp, target);
        }
        finally
        {
            TryDelete(temp);
            // Без целевого файла резервная копия — единственная копия прошлой выгрузки: её не трогаем.
            if (File.Exists(target)) TryDelete(backup);
        }

        return size;
    }

    /// <summary>
    ///     Убирает обрывки прошлых публикаций <paramref name="target" />. Резервная копия без целевого файла — единственная
    ///     копия прошлой выгрузки (подмена упала посередине): её возвращаем на место, а не удаляем.
    /// </summary>
    public static void CleanupLeftovers(string target)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(target))!;
        if (!Directory.Exists(folder)) return;

        var prefix = Path.GetFileName(target) + Marker;
        foreach (var file in Directory.GetFiles(folder, prefix + "*"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < LeftoverAge) continue;
                if (file.EndsWith(BackupExtension, StringComparison.OrdinalIgnoreCase) && !File.Exists(target)) File.Move(file, target);
                else File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Файл занят — значит, его ещё пишут; уберём в следующий раз.
            }
        }
    }

    /// <summary>NWC и RVT не проверить без Navisworks и Revit — проверяем, что файл есть, не пуст и читается; целиком его прочитает копирование.</summary>
    private static long Verify(string staged)
    {
        var info = new FileInfo(staged);
        if (!info.Exists) throw new InvalidOperationException($"готовый файл {info.Name} не найден");
        if (info.Length == 0) throw new InvalidOperationException($"готовый файл {info.Name} пуст");

        using var stream = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[Math.Min(64 * 1024, info.Length)];
        if (stream.Read(buffer, 0, buffer.Length) == 0) throw new InvalidOperationException($"готовый файл {info.Name} не читается");
        return info.Length;
    }

    private static void CopyVerified(string source, string temp, long size)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
        {
            input.CopyTo(output, 1024 * 1024);
            output.Flush(true);
        }

        var copied = new FileInfo(temp).Length;
        if (copied != size) throw new IOException($"копия в папке результатов не совпала по размеру ({copied} из {size} байт)");
    }

    /// <summary>
    ///     Подмена одной операцией файловой системы. ReplaceFile при сбое оставляет старый файл под именем
    ///     <paramref name="backup" /> — тогда возвращаем его. Если сервер SMB не умеет ReplaceFile, меняем файлы двумя
    ///     переименованиями внутри папки; занятый целевой файл так не обойти — это ошибка, а старый файл остаётся.
    /// </summary>
    private static void Replace(string temp, string target, string backup)
    {
        try
        {
            if (ReplaceOverride is { } replace) replace(temp, target, backup);
            else File.Replace(temp, target, backup, true);
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Restore(target, backup);
            if (exception.HResult is SharingViolation or LockViolation) throw new IOException($"файл {Path.GetFileName(target)} занят другой программой", exception);
            if (!File.Exists(temp)) throw;
        }

        File.Move(target, backup);
        try
        {
            File.Move(temp, target);
        }
        catch
        {
            Restore(target, backup);
            throw;
        }
    }

    private static void Restore(string target, string backup)
    {
        if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target);
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Удалим при следующей публикации этого файла (CleanupLeftovers).
        }
    }
}
