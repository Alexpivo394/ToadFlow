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

    /// <summary>Картинки коллизий части: <c>{Root}/images/{часть}/{коллизия}.png</c>.</summary>
    public string ImageFolder(Guid partId) => Path.Combine(Root, "images", partId.ToString("N"));

    public string ImagePath(Guid partId, Guid collisionId) => Path.Combine(ImageFolder(partId), collisionId.ToString("N") + ".png");

    /// <summary>Пишет картинку во временный файл той же папки и переносит на место: читатель не увидит половину файла.</summary>
    public async Task WriteImageAsync(Guid partId, Guid collisionId, Stream content, CancellationToken ct)
    {
        var path = ImagePath(partId, collisionId);
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
    }

    public void DeleteImage(Guid partId, Guid collisionId)
    {
        var path = ImagePath(partId, collisionId);
        if (File.Exists(path)) File.Delete(path);
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
