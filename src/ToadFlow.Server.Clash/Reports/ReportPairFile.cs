using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Images;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>Общее у выгрузок: строки выгрузки потоком, HTML одной пары (файлом и в архиве), имя файла.</summary>
internal static class ReportPairFile
{
    /// <summary>Пара из отчёта; пара без строк в сводке — с нулями и подписью по справочнику.</summary>
    public static ReportPairView Pair(ReportView view, DisciplineRanks ranks, string pair)
    {
        if (view.Pairs.FirstOrDefault(p => p.Pair == pair) is { } known) return known;
        var (a, b) = DisciplineRanks.Split(pair);
        var (first, second) = ranks.Ordered(a, b);
        return new ReportPairView(pair, $"{first} × {second}", first, second, 0, 0, 0, 0, 0);
    }

    /// <summary>Строки выборки потоком из базы, в порядке отчёта, с подписью пары.</summary>
    public static async IAsyncEnumerable<ReportExportRow> Rows(ReportSelection selection, ReportContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var none = new Dictionary<Guid, int>();
        var labels = new Dictionary<string, string>();
        await foreach (var source in selection.Rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            var key = source.Collision.DisciplinePair;
            if (!labels.TryGetValue(key, out var label))
            {
                var (a, b) = DisciplineRanks.Split(key);
                labels[key] = label = selection.Ranks.Label(a, b);
            }

            yield return new ReportExportRow(RunReports.Row(source, context.ModelNames, none), source.Collision.CreatedAt, label);
        }
    }

    /// <summary>HTML одной пары: <paramref name="selection" /> уже отфильтрована по паре.</summary>
    /// <returns>Сколько строк в файле.</returns>
    public static async Task<int> WriteAsync(Stream body, ReportView view, ReportContext context, ReportSelection selection, string pair, ToadFlowDb db,
        CollisionImages images, string web, TimeZoneInfo zone, CancellationToken ct)
    {
        var total = await selection.Rows.CountAsync(ct);
        var pictures = await ReportImages.LoadAsync(selection.Rows, db, images, ct);
        var levels = selection.Order.LevelViews.Where(l => l.Pair == pair).ToList();
        await ReportHtml.WriteAsync(body, view, Pair(view, selection.Ranks, pair), levels, pictures.AheadAsync(Rows(selection, context, ct), ct), total,
            pictures.ImageAsync, web, zone, ct);
        return total;
    }

    /// <summary>
    ///     Имя файла из непустых частей через пробел; символы, недопустимые в имени файла Windows, заменены на «_».
    ///     Набор — Windows на любой ОС сервера: файлы и архивы открывают на Windows, а сервер работает в Linux.
    /// </summary>
    public static string FileName(IEnumerable<string?> parts)
    {
        var title = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.Concat(title.Select(c => c < ' ' || WindowsInvalid.Contains(c) ? '_' : c));
    }

    private const string WindowsInvalid = "<>:\"/\\|?*";
}
