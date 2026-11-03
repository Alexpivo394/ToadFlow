using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Images;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>
///     Картинки HTML-выгрузки первых <see cref="ReportHtml.MaxImages" /> строк. Строка, попав в окно перед записью, начинает
///     рисоваться: впереди пишущего не больше 2 × <see cref="CollisionImages.ArchiveWindow" /> картинок. Выгрузка рисует в своём окне
///     потоков, уступает картинкам, которые ждут люди, и пишется по мере готовности. В памяти — пути к файлам; байты читаются, когда
///     строка пишется.
/// </summary>
internal sealed class ReportImages
{
    private readonly CollisionImages _images;
    private readonly IReadOnlyDictionary<Guid, ImageTarget> _targets;
    private readonly Dictionary<Guid, Task<string?>> _pending = [];
    private readonly CancellationToken _ct;

    private ReportImages(CollisionImages images, IReadOnlyDictionary<Guid, ImageTarget> targets, CancellationToken ct)
    {
        _images = images;
        _targets = targets;
        _ct = ct;
    }

    private int Window => 2 * _images.ArchiveWindow;

    /// <summary>Цели картинок первых строк выборки — до записи, пока соединение с базой не занято потоком строк.</summary>
    public static async Task<ReportImages> LoadAsync(IQueryable<ReportRowSource> rows, ToadFlowDb db, CollisionImages images, CancellationToken ct)
    {
        var ids = await rows.Select(r => r.Collision.Id).Take(ReportHtml.MaxImages).ToListAsync(ct);
        return new ReportImages(images, await ImageTargets.LoadAsync(db, ids, ct), ct);
    }

    /// <summary>Те же строки в том же порядке; картинка строки начинает рисоваться, когда строка входит в окно перед записью.</summary>
    public async IAsyncEnumerable<ReportExportRow> AheadAsync(IAsyncEnumerable<ReportExportRow> rows, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var window = new Queue<ReportExportRow>();
        var index = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            var id = row.Row.Collision.Id;
            if (index++ < ReportHtml.MaxImages && _targets.TryGetValue(id, out var target)) _pending.TryAdd(id, PathAsync(target));
            window.Enqueue(row);
            if (window.Count >= Window) yield return window.Dequeue();
        }

        while (window.Count > 0) yield return window.Dequeue();
    }

    /// <summary>Картинка строки; null — картинки нет (элемента нет в выгрузке, выгрузка удалена, отрисовка не удалась).</summary>
    public async Task<byte[]?> ImageAsync(CollisionRow row, CancellationToken ct)
    {
        if (!_pending.Remove(row.Id, out var render) || await render is not { } path) return null;
        try
        {
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Файл версии удалила запись более новой версии.
            return null;
        }
    }

    private async Task<string?> PathAsync(ImageTarget target)
    {
        try
        {
            var result = await _images.GetAsync(target, _ct, ImagePriority.Archive);
            return result.Status == StatusCodes.Status200OK ? result.Path : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
