using Alexpivo394.Revit.Export.Sources;
using ToadFlow.Contracts;

namespace ToadFlow.Agent.Sources;

/// <summary>
///     Дерево источника и разворачивание отметок расписания в список моделей. Это делает агент: сервер на Linux
///     сетевые папки Windows не видит. Версию файла из папки агент читает сам; для Revit Server версию подставит сервер.
/// </summary>
public sealed class SourceBrowser
{
    private const int MaxDepth = 50;

    public async Task<SourceEntryDto[]> ListChildrenAsync(SourceDto source, string folderPath, CancellationToken ct = default)
    {
        var tree = Tree(source);
        var entries = await tree.GetChildrenAsync(string.IsNullOrEmpty(folderPath) ? RootOf(source) : folderPath, ct);
        return entries.Select(e => new SourceEntryDto(e.Name, e.FolderPath, e.ModelPath, e.IsFolder)).ToArray();
    }

    /// <summary>Явно отмеченные модели плюс все модели отмеченных папок (рекурсивно). Явная модель задаёт свои форматы.</summary>
    public async Task<ExpandedModelDto[]> ExpandAsync(SourceDto source, SelectionDto selection, CancellationToken ct = default)
    {
        var tree = Tree(source);
        var isServer = source.Kind == SourceKind.RevitServer;
        var models = new Dictionary<string, (string Name, bool Rvt, bool Nwc)>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in selection.Folders)
        {
            var start = string.IsNullOrEmpty(folder.FolderPath) ? RootOf(source) : folder.FolderPath;
            await foreach (var model in WalkAsync(tree, start, 0, ct))
                models[model.ModelPath] = (model.Name, folder.Rvt, folder.Nwc);
        }

        foreach (var model in selection.Models)
        {
            if (!isServer && !File.Exists(model.ModelPath)) continue; // модель удалили или переименовали
            models[model.ModelPath] = (model.Name, model.Rvt, model.Nwc);
        }

        return models
            .Where(pair => pair.Value.Rvt || pair.Value.Nwc)
            .Select(pair => new ExpandedModelDto(pair.Value.Name, pair.Key, isServer, isServer ? null : RvtFileInfo.TryReadYear(pair.Key),
                pair.Value.Rvt, pair.Value.Nwc))
            .OrderBy(model => model.ModelPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async IAsyncEnumerable<SourceEntry> WalkAsync(ITreeSource tree, string folderPath, int depth,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (depth > MaxDepth) yield break;

        foreach (var entry in await tree.GetChildrenAsync(folderPath, ct))
        {
            if (!entry.IsFolder)
            {
                yield return entry;
                continue;
            }

            await foreach (var nested in WalkAsync(tree, entry.FolderPath, depth + 1, ct)) yield return nested;
        }
    }

    private static ITreeSource Tree(SourceDto source) => source.Kind == SourceKind.RevitServer
        ? new RevitServerSource(source.Host, (source.RevitYear ?? 0).ToString())
        : new FolderSource(source.Host);

    private static string RootOf(SourceDto source) => source.Kind == SourceKind.RevitServer ? source.RootPath : source.Host;
}
