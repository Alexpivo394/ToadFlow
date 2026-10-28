using ToadFlow.Contracts.Clash;

namespace ToadFlow.Clash.Worker;

/// <summary>
///     Слепки на диске работника по Id: повторный прогон той же части геометрию не скачивает. Слепок неизменен, поэтому
///     скачанный целиком и проверенный по размеру файл верен всегда. Сверх лимита удаляются давно не нужные слепки.
/// </summary>
public sealed class SnapshotCache(EngineClient client, WorkerOptions options)
{
    public const string Geometry = "model.tcg";
    public const string Parameters = "model.tcp";

    /// <summary>Папка слепка с обоими файлами; недостающее скачивается.</summary>
    public async Task<string> GetAsync(EngineRunModel model, CancellationToken ct)
    {
        var root = options.ResolvedCacheRoot;
        var folder = Path.Combine(root, model.SnapshotId.ToString("N"));
        if (!IsComplete(folder, model))
        {
            var staging = folder + ".part";
            Directory.CreateDirectory(staging);
            await FetchAsync(model.SnapshotId, ClashUploads.Geometry, Path.Combine(staging, Geometry), model.GeometryBytes, ct);
            await FetchAsync(model.SnapshotId, ClashUploads.Parameters, Path.Combine(staging, Parameters), model.ParametersBytes, ct);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.Move(staging, folder);
        }

        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow); // отметка использования для вытеснения
        return folder;
    }

    /// <summary>Удаляет давно не нужные слепки, пока кеш больше лимита. Слепки текущего прогона не трогаются.</summary>
    public void Trim(IReadOnlyCollection<Guid> keep)
    {
        var root = options.ResolvedCacheRoot;
        if (!Directory.Exists(root)) return;

        var folders = new DirectoryInfo(root).GetDirectories()
            .Where(d => !d.Name.EndsWith(".part", StringComparison.Ordinal))
            .Select(d => (Dir: d, Bytes: d.GetFiles().Sum(f => f.Length)))
            .OrderBy(d => d.Dir.LastWriteTimeUtc)
            .ToList();
        var total = folders.Sum(f => f.Bytes);
        var protectedNames = keep.Select(id => id.ToString("N")).ToHashSet();
        foreach (var (dir, bytes) in folders)
        {
            if (total <= options.CacheLimitBytes) break;
            if (protectedNames.Contains(dir.Name)) continue;
            dir.Delete(recursive: true);
            total -= bytes;
        }
    }

    private async Task FetchAsync(Guid snapshotId, string file, string path, long size, CancellationToken ct)
    {
        if (File.Exists(path) && new FileInfo(path).Length == size) return;
        if (File.Exists(path) && new FileInfo(path).Length > size) File.Delete(path);

        await client.DownloadAsync(snapshotId, file, path, ct);
        var received = new FileInfo(path).Length;
        if (received != size) throw new IOException($"Слепок {snapshotId:N}: {file} скачан не полностью ({received} из {size} байт)");
    }

    private static bool IsComplete(string folder, EngineRunModel model) =>
        File.Exists(Path.Combine(folder, Geometry)) && new FileInfo(Path.Combine(folder, Geometry)).Length == model.GeometryBytes
                                                    && File.Exists(Path.Combine(folder, Parameters))
                                                    && new FileInfo(Path.Combine(folder, Parameters)).Length == model.ParametersBytes;
}
