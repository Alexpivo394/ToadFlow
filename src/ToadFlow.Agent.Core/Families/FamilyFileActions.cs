using Alexpivo394.Revit.Families.Files;
using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.Extensions.Logging;
using ToadFlow.Contracts.Families;

namespace ToadFlow.Agent.Families;

/// <summary>
///     Действия с файлами библиотеки по просьбе сервера. До любого изменения проверяется, что на месте ровно тот файл, который видел
///     сервер, а всё, что убирается или перезаписывается, сохраняется в <c>.toadflow\history</c>. Пути — только внутри библиотеки и вне
///     служебной папки. Действия идут по одному.
/// </summary>
public sealed class FamilyFileActions(ILogger<FamilyFileActions> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<FamilyFileActionResult> RunAsync(FamilyFileActionDto action)
    {
        await _gate.WaitAsync();
        try
        {
            var error = await Task.Run(() => Run(action));
            if (error is null) logger.LogInformation("Библиотека семейств: {Kind} «{Path}» → «{Target}» сделано", action.Kind, action.RelativePath, action.TargetPath);
            else logger.LogWarning("Библиотека семейств: {Kind} «{Path}» не сделано: {Error}", action.Kind, action.RelativePath, error);
            return new FamilyFileActionResult(error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Библиотека семейств: {Kind} «{Path}» не удалось", action.Kind, action.RelativePath);
            return new FamilyFileActionResult($"не удалось: {exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>Почему не сделано; null — сделано.</returns>
    internal static string? Run(FamilyFileActionDto action)
    {
        if (!Directory.Exists(action.Root)) return $"агент не видит папку библиотеки «{action.Root}»";
        if (!IsHash(action.Hash) || action.CatalogHash is { } c && !IsHash(c) || action.ReplaceHash is { } r && !IsHash(r)) return "неверный хеш";
        if (Check(action.RelativePath) is { } bad) return bad;
        if (action.Kind == FamilyFileActionKind.Move && (action.TargetPath is null || Check(action.TargetPath) is not null))
            return action.TargetPath is null ? "не указано, куда переложить" : Check(action.TargetPath);

        return action.Kind switch
        {
            FamilyFileActionKind.Move => Move(action),
            FamilyFileActionKind.Delete => Delete(action),
            FamilyFileActionKind.Restore => Restore(action),
            _ => "неизвестное действие"
        };
    }

    private static string? Move(FamilyFileActionDto action)
    {
        var source = Full(action.Root, action.RelativePath);
        var target = Full(action.Root, action.TargetPath!);
        if (Expect(source, action.Hash, action.RelativePath) is { } changed) return changed;
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && source != target)
        {
            // Только регистр букв: Windows переименует через промежуточное имя.
            var step = source + "." + Guid.NewGuid().ToString("N")[..8];
            File.Move(source, step);
            File.Move(step, target);
            MoveCatalog(action.Root, action.RelativePath, action.TargetPath!);
            return null;
        }

        if (Free(target, action.ReplaceHash, action.TargetPath!) is { } busy) return busy;

        Keep(action.Root, action.RelativePath, action.Hash);
        if (File.Exists(target)) Keep(action.Root, action.TargetPath!, action.ReplaceHash!);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        if (FamilyHash.OfFile(target) != action.Hash) return "файл изменился во время переноса — проверьте его в проводнике";
        File.Delete(source);

        var targetCatalog = Path.ChangeExtension(target, LibraryPaths.CatalogExtension);
        var sourceCatalog = Path.ChangeExtension(source, LibraryPaths.CatalogExtension);
        if (File.Exists(targetCatalog)) File.Delete(targetCatalog); // прежний каталог цели сохранён в истории вместе с её файлом
        if (File.Exists(sourceCatalog)) File.Move(sourceCatalog, targetCatalog);
        return null;
    }

    private static string? Delete(FamilyFileActionDto action)
    {
        var path = Full(action.Root, action.RelativePath);
        if (Expect(path, action.Hash, action.RelativePath) is { } changed) return changed;

        Keep(action.Root, action.RelativePath, action.Hash);
        File.Delete(path);
        var catalog = Path.ChangeExtension(path, LibraryPaths.CatalogExtension);
        if (File.Exists(catalog)) File.Delete(catalog);
        return null;
    }

    private static string? Restore(FamilyFileActionDto action)
    {
        var copy = HistoryStore.PathOf(action.Root, action.Hash, LibraryPaths.FamilyExtension);
        if (!File.Exists(copy)) return "в .toadflow\\history нет копии этой версии";
        var catalogCopy = action.CatalogHash is { } catalogHash ? HistoryStore.PathOf(action.Root, catalogHash, LibraryPaths.CatalogExtension) : null;
        if (catalogCopy is not null && !File.Exists(catalogCopy)) return "в .toadflow\\history нет копии каталога типоразмеров этой версии";

        var target = Full(action.Root, action.RelativePath);
        if (Free(target, action.ReplaceHash, action.RelativePath) is { } busy) return busy;
        if (File.Exists(target)) Keep(action.Root, action.RelativePath, action.ReplaceHash!);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(copy, target, overwrite: true);
        if (FamilyHash.OfFile(target) != action.Hash) return "копия в истории повреждена — файл положен, но его содержимое другое; проверьте его в проводнике";
        // Файл дописан агентом и проверен: время записи копии (давнее) — чтобы обход, начатый сразу, взял его, а не ждал, пока он «уляжется».
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(copy));
        var catalog = Path.ChangeExtension(target, LibraryPaths.CatalogExtension);
        if (catalogCopy is not null)
        {
            File.Copy(catalogCopy, catalog, overwrite: true);
            File.SetLastWriteTimeUtc(catalog, File.GetLastWriteTimeUtc(catalogCopy));
        }
        else if (File.Exists(catalog)) File.Delete(catalog);

        return null;
    }

    /// <summary>Файл на месте и с ожидаемым содержимым.</summary>
    private static string? Expect(string path, string hash, string relativePath)
    {
        if (!File.Exists(path)) return $"файла «{relativePath}» уже нет";
        return FamilyHash.OfFile(path) == hash ? null : $"файл «{relativePath}» изменился с последнего обхода — дождитесь обхода и повторите";
    }

    /// <summary>Цель свободна или занята ровно тем файлом, который разрешено заменить.</summary>
    private static string? Free(string target, string? replaceHash, string relativePath)
    {
        if (!File.Exists(target)) return null;
        if (replaceHash is null) return $"«{relativePath}» уже есть";
        return FamilyHash.OfFile(target) == replaceHash ? null : $"«{relativePath}» изменился с последнего обхода — дождитесь обхода и повторите";
    }

    /// <summary>Копия содержимого файла (и его каталога) в истории — до того, как файл уберут или перезапишут.</summary>
    private static void Keep(string root, string relativePath, string hash)
    {
        var path = Full(root, relativePath);
        var catalog = Path.ChangeExtension(path, LibraryPaths.CatalogExtension);
        var info = new FileInfo(path);
        HistoryStore.Keep(root, new ScannedFile(relativePath, info.Length, info.LastWriteTimeUtc, hash, File.Exists(catalog) ? FamilyHash.OfFile(catalog) : null, null));
    }

    private static void MoveCatalog(string root, string from, string to)
    {
        var source = Path.ChangeExtension(Full(root, from), LibraryPaths.CatalogExtension);
        if (!File.Exists(source)) return;
        var step = source + "." + Guid.NewGuid().ToString("N")[..8];
        File.Move(source, step);
        File.Move(step, Path.ChangeExtension(Full(root, to), LibraryPaths.CatalogExtension));
    }

    /// <summary>Путь от корня: файл семейства, без переходов наверх, вне служебной папки.</summary>
    private static string? Check(string relativePath)
    {
        var parts = relativePath.Replace('/', '\\').Split('\\');
        if (Path.IsPathRooted(relativePath) || parts.Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return $"путь «{relativePath}» вне библиотеки";
        if (parts[0].Equals(LibraryPaths.ServiceFolder, StringComparison.OrdinalIgnoreCase)) return "служебную папку трогать нельзя";
        return LibraryPaths.IsFamily(relativePath) ? null : $"«{relativePath}» — не файл семейства";
    }

    private static string Full(string root, string relativePath) => Path.Combine(root, relativePath.Replace('/', '\\'));

    private static bool IsHash(string hash) => hash.Length == 64 && hash.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}
