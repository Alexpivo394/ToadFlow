using Microsoft.Extensions.Options;

namespace ToadFlow.Server.Families.Read;

/// <summary>Картинки семейств: <c>{Families:StorageRoot}/previews/{хеш версии}.png</c>. Одна картинка на содержимое, не перезаписывается.</summary>
public sealed class PreviewStore(IOptions<FamiliesStorageOptions> options)
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public string PathOf(string hash) => Path.Combine(options.Value.Root, "previews", hash + ".png");

    public bool Exists(string hash) => File.Exists(PathOf(hash));

    /// <returns>false — это не PNG, картинка не сохранена.</returns>
    public async Task<bool> SaveAsync(string hash, byte[] png, CancellationToken ct)
    {
        if (png.Length < PngSignature.Length || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)) return false;
        var path = PathOf(hash);
        if (File.Exists(path)) return true;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, png, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return true;
    }
}
