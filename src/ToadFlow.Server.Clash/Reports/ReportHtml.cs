using System.Globalization;
using System.Text;
using ToadFlow.Server.Clash.Collisions;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>
///     Выгрузка пары разделов в один самодостаточный HTML: стили и скрипт фильтров внутри, картинки base64 у первых
///     <see cref="MaxImages" /> строк в порядке отчёта, остальные строки той же пары — таблицей. Строки сгруппированы по уровням,
///     наверху — оглавление уровней. Фильтры (статус, уровень, поиск) работают в файле без сервера. Печатается на A4: карточка
///     коллизии не рвётся между страницами, фильтры и оглавление не печатаются.
/// </summary>
public static class ReportHtml
{
    public const int MaxImages = 1000;
    public const string ContentType = "text/html; charset=utf-8";
    public const string NoLevel = "Без уровня";

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private const string Style = """
        :root { color-scheme: light; }
        [hidden] { display: none !important; }
        body { font: 14px/1.45 system-ui, -apple-system, "Segoe UI", Roboto, Arial, sans-serif; color: #212529; margin: 24px; background: #fff; }
        h1 { font-size: 22px; margin: 0 0 4px; }
        h2 { font-size: 17px; margin: 28px 0 10px; }
        h3.level { font-size: 15px; margin: 22px 0 8px; padding-bottom: 4px; border-bottom: 2px solid #dee2e6; }
        .muted, .count { color: #868e96; }
        .note { background: #fff3bf; border: 1px solid #ffe066; padding: 8px 12px; border-radius: 6px; margin: 12px 0; }
        .tiles { display: flex; flex-wrap: wrap; gap: 10px; margin: 14px 0; }
        .tile { border: 1px solid #dee2e6; border-radius: 8px; padding: 8px 14px; min-width: 110px; }
        .tile b { display: block; font-size: 20px; }
        .filters { position: sticky; top: 0; z-index: 1; background: #fff; display: flex; flex-wrap: wrap; gap: 8px 16px; align-items: center; padding: 10px 0; border-bottom: 1px solid #dee2e6; }
        .filters label { white-space: nowrap; }
        .filters input[type=search], .filters select { font: inherit; padding: 3px 6px; }
        .toc { columns: 3 220px; padding-left: 18px; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border-bottom: 1px solid #e9ecef; padding: 4px 8px; text-align: left; vertical-align: top; }
        th { background: #f1f3f5; font-weight: 600; }
        td.num, th.num { text-align: right; }
        tr.group td { background: #f8f9fa; font-weight: 600; }
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
            .filters, .toc, .tocTitle { display: none; }
            .card img, .card .noimg { width: 240px; height: 150px; }
            a { color: inherit; text-decoration: none; }
        }
        """;

    /// <summary>Фильтры файла: прячет карточки и строки, пересчитывает заголовки уровней и «Показано N из M».</summary>
    private const string Script = """
        (function () {
          var q = document.getElementById('f-q'), lv = document.getElementById('f-level'), st = document.getElementById('f-status'),
              shown = document.getElementById('shown');
          var items = [].slice.call(document.querySelectorAll('[data-q]')), heads = [].slice.call(document.querySelectorAll('[data-head]'));
          function apply() {
            var text = q.value.trim().toLowerCase(), level = lv.value, on = {}, counts = {}, n = 0;
            [].forEach.call(st.querySelectorAll('input'), function (c) { if (c.checked) on[c.value] = 1; });
            items.forEach(function (e) {
              var ok = on[e.dataset.status] && (level === '*' || e.dataset.level === level) && (!text || e.dataset.q.indexOf(text) >= 0);
              e.hidden = !ok;
              if (ok) { n++; counts[e.dataset.level] = (counts[e.dataset.level] || 0) + 1; }
            });
            heads.forEach(function (h) {
              var c = counts[h.dataset.level] || 0, s = h.querySelector('.count');
              h.hidden = c === 0;
              if (s) s.textContent = c;
            });
            shown.textContent = 'Показано ' + n + ' из ' + items.length;
          }
          q.addEventListener('input', apply);
          lv.addEventListener('change', apply);
          st.addEventListener('change', apply);
        })();
        """;

    /// <param name="pair">Пара файла: подпись и счётчики сводки.</param>
    /// <param name="levels">Уровни пары в порядке строк, с числами.</param>
    /// <param name="rows">Строки пары в порядке отчёта (уровень, потом №).</param>
    /// <param name="total">Сколько строк всего — для пометки о картинках и «Показано».</param>
    /// <param name="image">Картинка коллизии; null — картинки нет.</param>
    public static async Task WriteAsync(Stream output, ReportView report, ReportPairView pair, IReadOnlyList<ReportLevelView> levels,
        IAsyncEnumerable<ReportExportRow> rows, int total, Func<CollisionRow, CancellationToken, Task<byte[]?>> image, string webBase, TimeZoneInfo zone,
        CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        var check = report.Check.Object is { Length: > 0 } project ? $"{project} · {report.Check.Name}" : report.Check.Name;
        var at = TimeZoneInfo.ConvertTime(report.Run.FinishedAt, zone).ToString("dd.MM.yyyy HH:mm", Russian);
        var reportUrl = $"{webBase}/clash/reports/{report.Run.RunId}?pair={Uri.EscapeDataString(pair.Pair)}";
        await writer.WriteAsync($"""
            <!doctype html>
            <html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E($"Коллизии {pair.Label} — {check} {at}")}</title><style>{Style}</style></head><body>
            <h1>Коллизии {E(pair.Label)}</h1>
            <div>{E(check)}</div>
            <div class="muted">Версия {report.Run.Version} · прогон {at} · {E(report.Run.Trigger == "schedule" ? "по расписанию" : "вручную")}{(report.Run.StartedBy is { } by ? " (" + E(by) + ")" : "")}</div>
            <div class="muted">Отчёт в ToadFlow: <a href="{E(reportUrl)}">{E(reportUrl)}</a></div>
            """);

        await writer.WriteAsync("<h2>Модели</h2><table><tr><th>Модель</th><th>Раздел</th><th>Выгружена</th><th></th></tr>");
        foreach (var model in report.Models.Where(m => m.Discipline == pair.DisciplineA || m.Discipline == pair.DisciplineB))
            await writer.WriteAsync($"<tr><td>{E(model.Name)}</td><td>{E(model.Discipline)}</td><td>{Date(model.UploadedAt, zone)}</td>" +
                                    $"<td>{(model.Unchecked ? "<b style=\"color:#e03131\">не выгрузилась — не проверена</b>" : "")}</td></tr>");
        await writer.WriteAsync("</table>");

        await writer.WriteAsync("<div class=\"tiles\">");
        foreach (var (label, value) in new (string, int)[]
                 {
                     ("Открытых сейчас", pair.Open), ("Новых", pair.New), ("Вернулись", pair.Returned), ("Исправлено", pair.Fixed), ("Фиктивных", pair.Fake)
                 })
            await writer.WriteAsync($"<div class=\"tile\"><b>{N(value)}</b>{label}</div>");
        await writer.WriteAsync("</div>");

        var levelIndex = levels.Select((l, i) => (l.Level, i)).ToDictionary(x => x.Level, x => x.i);
        await writer.WriteAsync("<h2 class=\"tocTitle\">Уровни</h2><ul class=\"toc\">");
        foreach (var (level, i) in levels.Select((l, i) => (l, i)))
            await writer.WriteAsync($"<li><a href=\"#level-{i}\">{E(LevelLabel(level.Level))}</a> <span class=\"count\">{N(level.Count)}</span></li>");
        await writer.WriteAsync("</ul>");

        await writer.WriteAsync($"<h2>Коллизии ({N(total)})</h2>");
        if (total > MaxImages)
            await writer.WriteAsync($"<div class=\"note\">картинки — у первых {N(MaxImages)} из {N(total)}; остальные — таблицей ниже</div>");
        await writer.WriteAsync("<div class=\"filters\"><span id=\"f-status\">");
        foreach (var status in Enum.GetValues<CollisionStatus>())
            await writer.WriteAsync($"<label><input type=\"checkbox\" value=\"{status}\" checked> {E(ReportLabels.Status(status))}</label> ");
        await writer.WriteAsync("</span><select id=\"f-level\"><option value=\"*\">Все уровни</option>");
        foreach (var level in levels)
            await writer.WriteAsync($"<option value=\"{E(level.Level)}\">{E(LevelLabel(level.Level))} ({N(level.Count)})</option>");
        await writer.WriteAsync("</select><input type=\"search\" id=\"f-q\" placeholder=\"№, ID, UniqueId, семейство, тип\">" +
                                $"<span id=\"shown\" class=\"muted\">Показано {N(total)} из {N(total)}</span></div>");

        var index = 0;
        var tableOpen = false;
        string? currentLevel = null;
        await foreach (var row in rows.WithCancellation(ct))
        {
            var level = row.Row.Level;
            if (index < MaxImages)
            {
                if (level != currentLevel)
                    await writer.WriteAsync($"<h3 class=\"level\" id=\"level-{levelIndex.GetValueOrDefault(level, -1)}\" data-level=\"{E(level)}\" data-head>" +
                                            $"{E(LevelLabel(level))} <span class=\"count\">{N(levels.FirstOrDefault(l => l.Level == level)?.Count ?? 0)}</span></h3>");
                await CardAsync(writer, row, await image(row.Row.Collision, ct), webBase, report.Run.PartId);
            }
            else
            {
                if (!tableOpen)
                {
                    tableOpen = true;
                    currentLevel = null;
                    await writer.WriteAsync("<h2>Без картинок</h2><table><tr>" + string.Concat(ReportColumns.Headers.Select(h => $"<th>{E(h)}</th>")) + "</tr>");
                }

                if (level != currentLevel)
                    await writer.WriteAsync($"<tr class=\"group\" data-level=\"{E(level)}\" data-head><td colspan=\"{ReportColumns.Headers.Count}\">" +
                                            $"{E(LevelLabel(level))} <span class=\"count\"></span></td></tr>");
                var cells = ReportColumns.Cells(row, webBase, report.Run.PartId, zone);
                var link = ReportLabels.CollisionUrl(webBase, report.Run.PartId, row.Row.Collision.Id);
                await writer.WriteAsync($"<tr class=\"row\" {Data(row)}>" + string.Concat(cells.Select((c, i) => i == cells.Count - 1
                    ? $"<td><a href=\"{E(link)}\">открыть</a></td>"
                    : $"<td>{E(ReportColumns.Display(c))}</td>")) + "</tr>");
            }

            currentLevel = level;
            index++;
        }

        if (tableOpen) await writer.WriteAsync("</table>");
        await writer.WriteAsync($"<script>{Script}</script></body></html>");
    }

    public static string LevelLabel(string level) => level.Length == 0 ? NoLevel : level;

    /// <summary>Атрибуты фильтра строки: статус, уровень и текст поиска строчными.</summary>
    private static string Data(ReportExportRow row)
    {
        var c = row.Row.Collision;
        var q = string.Join(' ', new[]
        {
            c.Number.ToString(CultureInfo.InvariantCulture), c.ElementIdA.ToString(CultureInfo.InvariantCulture), c.ElementIdB.ToString(CultureInfo.InvariantCulture),
            c.UniqueIdA, c.UniqueIdB, c.FamilyA, c.TypeA, c.FamilyB, c.TypeB
        }.Where(s => !string.IsNullOrEmpty(s))).ToLower(Russian);
        return $"data-status=\"{c.Status}\" data-level=\"{E(row.Row.Level)}\" data-q=\"{E(q)}\"";
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
            <div class="card" {Data(row)}>{picture}<div class="body">
            <div><b>№{c.Number}</b> <span class="badge">{E(ReportLabels.Status(c.Status))}</span>{change}
            · глубина {N((int)Math.Round(c.DepthMm))} мм · {E(LevelLabel(row.Row.Level))} · правит {E(CollisionFeed.AssigneeText(c.Assignee))}
            · <a href="{E(ReportLabels.CollisionUrl(webBase, partId, c.Id))}">открыть</a></div>
            {Side("a", "A", c.ModelNameA, c.CategoryA, c.FamilyA, c.TypeA, c.ElementIdA)}
            {Side("b", "B", c.ModelNameB, c.CategoryB, c.FamilyB, c.TypeB, c.ElementIdB)}
            {note}</div></div>
            """);
    }

    private static string Side(string css, string label, string model, string category, string? family, string? type, long elementId) =>
        $"<div class=\"side\"><span class=\"dot {css}\"></span><b>{label}</b> {E(model)} · {E(CategoryNames.Label(category))} · {E(family ?? "")} · {E(type ?? "")} · ID {elementId}</div>";

    /// <summary>Экранирует только разметку: WebUtility.HtmlEncode превращает «×» и неразрывный пробел в числовые ссылки.</summary>
    private static string E(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    private static string N(int value) => value.ToString("N0", Russian);

    private static string Date(DateTimeOffset? value, TimeZoneInfo zone) =>
        value is { } at ? TimeZoneInfo.ConvertTime(at, zone).ToString("dd.MM.yyyy HH:mm", Russian) : "";
}
