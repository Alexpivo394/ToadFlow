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
