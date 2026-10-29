using System.Security.Cryptography;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Uploads;

/// <summary>Загрузку нельзя опубликовать: файлы не те, что объявлены, или выгрузка неполная. Сообщение — для журнала задания.</summary>
public sealed class SnapshotRejectedException(string message) : Exception(message);

/// <summary>
///     Проверяет загруженные файлы и публикует их слепком: размеры и SHA-256, оглавление TCG (выгрузка завершена, модель та),
///     читаемость TCP. Каталог загрузки переносится в хранилище одним <see cref="Directory.Move(string, string)" />,
///     поэтому неполного слепка в хранилище не бывает. Лишние старые слепки модели удаляются.
/// </summary>
public sealed class SnapshotPublisher(ToadFlowDb db, ClashStorage storage, ClashSettingsStore settings, TimeProvider time)
{
    /// <exception cref="SnapshotRejectedException">Проверка не прошла; каталог и запись загрузки удалены.</exception>
    public async Task<ClashExtractResult> PublishAsync(ClashUpload upload, CancellationToken ct)
    {
        var folder = storage.UploadFolder(upload.Id);
        var geometryPath = Path.Combine(folder, ClashUploads.FileName(ClashUploads.Geometry)!);
        var parametersPath = Path.Combine(folder, ClashUploads.FileName(ClashUploads.Parameters)!);

        ModelIndex index;
        ModelCatalog catalog;
        try
        {
            await VerifyAsync(geometryPath, upload.GeometryBytes, upload.GeometrySha256, "геометрии", ct);
            await VerifyAsync(parametersPath, upload.ParametersBytes, upload.ParametersSha256, "параметров", ct);
            index = ReadIndex(geometryPath);
            if (!index.Header.Complete) throw new SnapshotRejectedException("выгрузка модели не завершена");
            if (!string.Equals(index.Header.ModelKey, upload.ModelId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                throw new SnapshotRejectedException("слепок другой модели: ключ в файле не совпадает с заданием");
            catalog = ModelCatalog.Build(index, ReadParameters(parametersPath));
        }
        catch (SnapshotRejectedException)
        {
            await DiscardAsync(upload, ct);
            throw;
        }

        var snapshotId = Guid.CreateVersion7();
        var target = storage.SnapshotFolder(upload.ModelId, snapshotId);
        Directory.CreateDirectory(storage.ModelFolder(upload.ModelId));
        Directory.Move(folder, target);

        var snapshot = new ClashSnapshot
        {
            Id = snapshotId,
            ModelId = upload.ModelId,
            JobId = upload.JobId,
            ExtractedAt = index.Header.ExtractedAt,
            RevitYear = index.Header.RevitYear,
            ViewName = index.Header.ViewName,
            Elements = index.Elements.Count,
            Triangles = index.TriangleCount,
            Inventory = index.Inventory.Count,
            GeometryBytes = upload.GeometryBytes,
            ParametersBytes = upload.ParametersBytes,
            Catalog = catalog,
            CreatedAt = time.GetUtcNow()
        };
        db.Set<ClashSnapshot>().Add(snapshot);
        if (db.Entry(upload).State == EntityState.Detached) db.Attach(upload);
        upload.SnapshotId = snapshotId;
        await db.SaveChangesAsync(ct);

        await PruneAsync(upload.ModelId, (await settings.StorageAsync(ct)).KeepSnapshots, ct);
        return ResultOf(snapshot);
    }

    public static ClashExtractResult ResultOf(ClashSnapshot snapshot) =>
        new(snapshot.Id, snapshot.Elements, snapshot.Triangles, snapshot.Inventory, snapshot.GeometryBytes, snapshot.ParametersBytes);

    /// <summary>
    ///     Оставляет у модели <paramref name="keep" /> последних по времени выгрузки слепков: сначала файлы, потом записи.
    ///     Слепки прогонов движка в очереди и в работе не удаляются, пока прогон не закончится, а слепки прогона — видимого
    ///     результата части — пока у части не появится новый результат: на них открывается просмотрщик коллизий.
    /// </summary>
    public async Task PruneAsync(Guid modelId, int keep, CancellationToken ct)
    {
        var active = (await db.Set<EngineRun>().AsNoTracking()
                .Where(r => r.State == EngineRunState.Queued || r.State == EngineRunState.Running
                            || db.Set<ClashPart>().Any(p => p.ResultRunId == r.Id || p.ApprovedRunId == r.Id)
                            || db.Set<EngineRun>().Any(next => next.PreviousRunId == r.Id
                                                              && (next.State == EngineRunState.Queued || next.State == EngineRunState.Running)))
                .Select(r => r.Models).ToListAsync(ct))
            .SelectMany(m => m).Select(m => m.SnapshotId).ToHashSet();
        var stale = (await db.Set<ClashSnapshot>().Where(s => s.ModelId == modelId)
                .OrderByDescending(s => s.ExtractedAt).ThenByDescending(s => s.CreatedAt)
                .Skip(keep).Select(s => s.Id).ToListAsync(ct))
            .Where(id => !active.Contains(id)).ToList();
        if (stale.Count == 0) return;

        foreach (var id in stale)
        {
            var folder = storage.SnapshotFolder(modelId, id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }

        await db.Set<ClashSnapshot>().Where(s => stale.Contains(s.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Убирает брошенную или отклонённую загрузку: каталог и запись.</summary>
    public async Task DiscardAsync(ClashUpload upload, CancellationToken ct)
    {
        var folder = storage.UploadFolder(upload.Id);
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Ту же брошенную загрузку одновременно убрал фоновый уборщик.
        }

        await db.Set<ClashUpload>().Where(u => u.Id == upload.Id).ExecuteDeleteAsync(ct);
    }

    private static async Task VerifyAsync(string path, long size, string sha256, string what, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var received = info.Exists ? info.Length : 0;
        if (received != size) throw new SnapshotRejectedException($"файл {what} загружен не полностью: {received} из {size} байт");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, read);
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new SnapshotRejectedException($"контрольная сумма файла {what} не совпала: файл повреждён при передаче");
    }

    private static ModelIndex ReadIndex(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ModelGeometryFile.ReadIndex(stream);
        }
        catch (InvalidDataException exception)
        {
            throw new SnapshotRejectedException($"файл геометрии не читается: {exception.Message}");
        }
    }

    private static ModelParameters ReadParameters(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ModelParametersFile.Read(stream);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or EndOfStreamException)
        {
            throw new SnapshotRejectedException($"файл параметров не читается: {exception.Message}");
        }
    }
}
