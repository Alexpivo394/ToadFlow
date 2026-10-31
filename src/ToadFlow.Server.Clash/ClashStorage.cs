using Alexpivo394.Revit.ParamCheck.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash;

/// <summary>Корень хранилища слепков (том Docker); <c>Clash:StorageRoot</c> в конфигурации.</summary>
public sealed class ClashStorageOptions
{
    public string Root { get; set; } = "";
}

/// <summary>Раскладка хранилища: <c>{Root}/{модель}/{слепок}/model.tcg|tcp</c> и <c>{Root}/uploads/{загрузка}</c>.</summary>
public sealed class ClashStorage(ToadFlowDb db, IOptions<ClashStorageOptions> options, TimeProvider time)
{
    public string Root => options.Value.Root;

    public string ModelFolder(Guid modelId) => Path.Combine(Root, modelId.ToString("N"));

    public string SnapshotFolder(Guid modelId, Guid snapshotId) => Path.Combine(ModelFolder(modelId), snapshotId.ToString("N"));

    public string UploadFolder(Guid uploadId) => Path.Combine(Root, "uploads", uploadId.ToString("N"));

    /// <summary>Картинки коллизий части: <c>{Root}/images/{часть}/{коллизия}-{версия}.png</c>.</summary>
    public string ImageFolder(Guid partId) => Path.Combine(Root, "images", partId.ToString("N"));

    /// <summary>Картинка версии <paramref name="version" />: <c>{root}/images/{часть}/{коллизия}-{версия}.png</c>.</summary>
    public static string ImagePath(string root, Guid partId, Guid collisionId, string version) =>
        Path.Combine(root, "images", partId.ToString("N"), $"{collisionId:N}-{version}.png");

    /// <summary>Пишет картинку версии атомарно (временный файл той же папки, затем перенос) и удаляет другие версии коллизии.</summary>
    public Task WriteImageAsync(Guid partId, Guid collisionId, string version, Stream content, CancellationToken ct) =>
        WriteImageAsync(Root, partId, collisionId, version, content, ct);

    /// <summary>Все версии картинки коллизии.</summary>
    public void DeleteImages(Guid partId, Guid collisionId) => DeleteImages(Root, partId, collisionId, keep: null);

    internal static async Task WriteImageAsync(string root, Guid partId, Guid collisionId, string version, Stream content, CancellationToken ct)
    {
        var path = ImagePath(root, partId, collisionId, version);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        DeleteImages(root, partId, collisionId, keep: path);
    }

    /// <summary>
    ///     Удаляет картинки коллизии, кроме <paramref name="keep" />: версии <c>{коллизия}-*.png</c> и прежнюю без версии. Файл, который
    ///     система не даёт удалить (его сейчас отдают), остаётся: устаревшую версию никто не попросит, её удалит следующая запись.
    /// </summary>
    private static void DeleteImages(string root, Guid partId, Guid collisionId, string? keep)
    {
        var folder = Path.Combine(root, "images", partId.ToString("N"));
        if (!Directory.Exists(folder)) return;
        var name = collisionId.ToString("N");
        foreach (var file in Directory.EnumerateFiles(folder, name + "*"))
        {
            var fileName = Path.GetFileName(file);
            if (!fileName.EndsWith(".png", StringComparison.Ordinal)) continue;
            if (fileName != name + ".png" && !fileName.StartsWith(name + "-", StringComparison.Ordinal)) continue;
            if (keep is not null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void DeleteImageFolder(Guid partId)
    {
        var folder = ImageFolder(partId);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>Ключ модели по пути (или имени, если пути нет), как в аналитике LOI.</summary>
    public static string KeyOf(string modelPath, string modelName) =>
        ModelNames.Key(modelPath.Trim().Length > 0 ? modelPath : modelName).ToLowerInvariant();

    /// <summary>Модель по пути; новая — создаётся, у известной обновляются имя и путь.</summary>
    public async Task<ClashModel> EnsureModelAsync(string name, string modelPath, CancellationToken ct)
    {
        var key = KeyOf(modelPath, name);
        var model = await db.Set<ClashModel>().SingleOrDefaultAsync(m => m.Key == key, ct);
        if (model is not null)
        {
            if (model.Name != name || model.ModelPath != modelPath)
            {
                model.Name = name;
                model.ModelPath = modelPath;
                await db.SaveChangesAsync(ct);
            }

            return model;
        }

        model = new ClashModel { Id = Guid.CreateVersion7(), Key = key, Name = name, ModelPath = modelPath, CreatedAt = time.GetUtcNow() };
        db.Set<ClashModel>().Add(model);
        await db.SaveChangesAsync(ct);
        return model;
    }
}
