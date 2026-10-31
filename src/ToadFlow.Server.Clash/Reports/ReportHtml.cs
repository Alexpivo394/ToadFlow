using System.Globalization;
using System.Net;
using System.Text;
using ToadFlow.Server.Clash.Collisions;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>
///     Выгрузка отчёта в один самодостаточный HTML: стили внутри, картинки base64 у первых <see cref="MaxImages" /> строк по
///     текущей сортировке, остальные строки — таблицей. Печатается на A4: карточка коллизии не рвётся между страницами.
/// </summary>
public static class ReportHtml
{
    public const int MaxImages = 500;
    public const string ContentType = "text/html; charset=utf-8";

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private const string Style = """
        :root { color-scheme: light; }
        body { font: 14px/1.45 system-ui, -apple-system, "Segoe UI", Roboto, Arial, sans-serif; color: #212529; margin: 24px; background: #fff; }
        h1 { font-size: 22px; margin: 0 0 4px; }
        h2 { font-size: 17px; margin: 28px 0 10px; }
        .muted { color: #868e96; }
        .note { background: #fff3bf; border: 1px solid #ffe066; padding: 8px 12px; border-radius: 6px; margin: 12px 0; }
        .tiles { display: flex; flex-wrap: wrap; gap: 10px; margin: 14px 0; }
        .tile { border: 1px solid #dee2e6; border-radius: 8px; padding: 8px 14px; min-width: 110px; }
        .tile b { display: block; font-size: 20px; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border-bottom: 1px solid #e9ecef; padding: 4px 8px; text-align: left; vertical-align: top; }
        th { background: #f1f3f5; font-weight: 600; }
        td.num, th.num { text-align: right; }
        .card { display: flex; gap: 16px; border: 1px solid #dee2e6; border-radius: 8px; padding: 12px; margin: 0 0 12px; break-inside: avoid; page-break-inside: avoid; }
        .card img, .card .noimg { width: 320px; height: 200px; flex: none; border-radius: 4px; background: #f8f9fa; }
        .card .noimg { display: flex; align-items: center; justify-content: center; color: #adb5bd; }
        .card .body { flex: 1; min-width: 0; }
        .badge { display: inline-block; padding: 0 8px; border-radius: 10px; background: #e9ecef; font-size: 12px; }
        .side { margin-top: 6px; }
        .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 6px; }
        .a { background: #e03131; } .b { background: #1971c2; }
        a { color: #1971c2; }
        @media print {
            body { margin: 0; font-size: 11px; }
            @page { size: A4; margin: 12mm; }
            .card img, .card .noimg { width: 240px; height: 150px; }
            a { color: inherit; text-decoration: none; }
        }
        """;

    /// <param name="total">Сколько строк всего по фильтру — для пометки о картинках.</param>
    /// <param name="image">Картинка коллизии; null — картинки нет.</param>
    public static async Task WriteAsync(Stream output, ReportView report, IAsyncEnumerable<ReportExportRow> rows, int total,
        Func<Guid, CancellationToken, Task<byte[]?>> image, string webBase, TimeZoneInfo zone, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        var check = report.Check.Object is { Length: > 0 } project ? $"{project} · {report.Check.Name}" : report.Check.Name;
        var at = TimeZoneInfo.ConvertTime(report.Run.FinishedAt, zone).ToString("dd.MM.yyyy HH:mm", Russian);
        await writer.WriteAsync($"""
            <!doctype html>
            <html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E($"Коллизии {check} {at}")}</title><style>{Style}</style></head><body>
            <h1>Коллизии: {E(check)}</h1>
            <div class="muted">Версия {report.Run.Version} · прогон {at} · {E(report.Run.Trigger == "schedule" ? "по расписанию" : "вручную")}{(report.Run.StartedBy is { } by ? " (" + E(by) + ")" : "")}</div>
            <div class="muted">Отчёт в ToadFlow: <a href="{E($"{webBase}/clash/reports/{report.Run.RunId}")}">{E($"{webBase}/clash/reports/{report.Run.RunId}")}</a></div>
            """);

        await writer.WriteAsync("<h2>Модели</h2><table><tr><th>Модель</th><th>Раздел</th><th>Выгружена</th><th></th></tr>");
        foreach (var model in report.Models)
            await writer.WriteAsync($"<tr><td>{E(model.Name)}</td><td>{E(model.Discipline)}</td><td>{Date(model.UploadedAt, zone)}</td>" +
                                    $"<td>{(model.Unchecked ? "<b style=\"color:#e03131\">не выгрузилась — не проверена</b>" : "")}</td></tr>");
        await writer.WriteAsync("</table>");

        var s = report.Summary;
        await writer.WriteAsync("<div class=\"tiles\">");
        foreach (var (label, value) in new (string, int)[]
                 {
                     ("Новых", s.New), ("Вернулись", s.Returned), ("Активных", s.Active), ("Исправлено", s.Fixed), ("Ушли из-за правил", s.GoneByRules),
                     ("Не проверено", s.Unchecked), ("Фиктивных", s.Fake)
                 })
            await writer.WriteAsync($"<div class=\"tile\"><b>{N(value)}</b>{label}</div>");
        await writer.WriteAsync("</div>");

        await writer.WriteAsync("<h2>По парам разделов</h2><table><tr><th>Пара</th><th class=\"num\">Открытых сейчас</th><th class=\"num\">Новых</th>" +
                                "<th class=\"num\">Вернулись</th><th class=\"num\">Исправлено</th><th class=\"num\">Фиктивных</th></tr>");
        foreach (var pair in report.Pairs)
            await writer.WriteAsync($"<tr><td>{E(pair.Pair)}</td><td class=\"num\">{N(pair.Open)}</td><td class=\"num\">{N(pair.New)}</td>" +
                                    $"<td class=\"num\">{N(pair.Returned)}</td><td class=\"num\">{N(pair.Fixed)}</td><td class=\"num\">{N(pair.Fake)}</td></tr>");
        await writer.WriteAsync("</table>");

        await writer.WriteAsync($"<h2>Коллизии ({N(total)})</h2>");
        if (total > MaxImages)
            await writer.WriteAsync($"<div class=\"note\">картинки — у первых {N(MaxImages)} из {N(total)}, сузьте фильтр, чтобы увидеть остальные</div>");

        var index = 0;
        var tableOpen = false;
        await foreach (var row in rows.WithCancellation(ct))
        {
            if (index < MaxImages) await CardAsync(writer, row, await image(row.Row.Collision.Id, ct), webBase, report.Run.PartId);
            else
            {
                if (!tableOpen)
                {
                    tableOpen = true;
                    await writer.WriteAsync("<table><tr>" + string.Concat(ReportColumns.Headers.Select(h => $"<th>{E(h)}</th>")) + "</tr>");
                }

                var cells = ReportColumns.Cells(row, webBase, report.Run.PartId, zone);
                var link = ReportLabels.CollisionUrl(webBase, report.Run.PartId, row.Row.Collision.Id);
                await writer.WriteAsync("<tr class=\"row\">" + string.Concat(cells.Select((c, i) => i == cells.Count - 1
                    ? $"<td><a href=\"{E(link)}\">открыть</a></td>"
                    : $"<td>{E(ReportColumns.Display(c))}</td>")) + "</tr>");
            }

            index++;
        }

        if (tableOpen) await writer.WriteAsync("</table>");
        await writer.WriteAsync("</body></html>");
    }

    private static async Task CardAsync(StreamWriter writer, ReportExportRow row, byte[]? png, string webBase, Guid partId)
    {
        var c = row.Row.Collision;
        var picture = png is null
            ? "<div class=\"noimg\">нет картинки</div>"
            : $"<img alt=\"Коллизия №{c.Number}\" src=\"data:image/png;base64,{Convert.ToBase64String(png)}\">";
        var change = row.Row.Change is { } moved && moved != c.Status ? $" <span class=\"muted\">в прогоне: {E(ReportLabels.Status(moved))}</span>" : "";
        var note = row.Row.LastNote is { } text
            ? $"<div class=\"side muted\">Примечание{(row.Row.LastNoteBy is { } by ? " — " + E(by) : "")}: {E(text)}</div>"
            : "";
        await writer.WriteAsync($"""
            <div class="card">{picture}<div class="body">
            <div><b>№{c.Number}</b> <span class="badge">{E(ReportLabels.Status(c.Status))}</span>{change}
            · {E(ReportColumns.Pair(row))} · глубина {N((int)Math.Round(c.DepthMm))} мм · {E(ReportColumns.Level(row))} · правит {E(CollisionFeed.AssigneeText(c.Assignee))}
            · <a href="{E(ReportLabels.CollisionUrl(webBase, partId, c.Id))}">открыть</a></div>
            {Side("a", "A", c.ModelNameA, c.CategoryA, c.FamilyA, c.TypeA, c.ElementIdA)}
            {Side("b", "B", c.ModelNameB, c.CategoryB, c.FamilyB, c.TypeB, c.ElementIdB)}
            {note}</div></div>
            """);
    }

    private static string Side(string css, string label, string model, string category, string? family, string? type, long elementId) =>
        $"<div class=\"side\"><span class=\"dot {css}\"></span><b>{label}</b> {E(model)} · {E(CategoryNames.Label(category))} · {E(family ?? "")} · {E(type ?? "")} · ID {elementId}</div>";

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static string N(int value) => value.ToString("N0", Russian);

    private static string Date(DateTimeOffset? value, TimeZoneInfo zone) =>
        value is { } at ? TimeZoneInfo.ConvertTime(at, zone).ToString("dd.MM.yyyy HH:mm", Russian) : "";
}
