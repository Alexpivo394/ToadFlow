using System.Globalization;
using System.Text;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>Оглавление архива выгрузки: шапка прогона, сводка и раздел → пары со ссылками на файлы пар.</summary>
internal static class ReportArchiveIndex
{
    /// <param name="File">Имя файла пары в архиве.</param>
    /// <param name="Rows">Строк в файле.</param>
    /// <param name="Images">Сколько из них с картинками.</param>
    public sealed record Entry(string File, ReportPairView Pair, int Rows, int Images);

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private const string Style = """
        :root { color-scheme: light; }
        body { font: 14px/1.45 system-ui, -apple-system, "Segoe UI", Roboto, Arial, sans-serif; color: #212529; margin: 24px; background: #fff; }
        h1 { font-size: 22px; margin: 0 0 4px; }
        h2 { font-size: 17px; margin: 24px 0 8px; }
        .muted { color: #868e96; }
        .tiles { display: flex; flex-wrap: wrap; gap: 10px; margin: 14px 0; }
        .tile { border: 1px solid #dee2e6; border-radius: 8px; padding: 8px 14px; min-width: 110px; }
        .tile b { display: block; font-size: 20px; }
        table { border-collapse: collapse; width: 100%; max-width: 900px; }
        th, td { border-bottom: 1px solid #e9ecef; padding: 4px 8px; text-align: left; }
        th { background: #f1f3f5; font-weight: 600; }
        td.num, th.num { text-align: right; }
        a { color: #1971c2; }
        """;

    /// <param name="discipline">Раздел архива; null — все пары.</param>
    public static async Task WriteAsync(Stream output, ReportView report, string? discipline, IReadOnlyList<Entry> files, DisciplineRanks ranks, string webBase,
        TimeZoneInfo zone, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        var check = report.Check.Object is { Length: > 0 } project ? $"{project} · {report.Check.Name}" : report.Check.Name;
        var at = TimeZoneInfo.ConvertTime(report.Run.FinishedAt, zone).ToString("dd.MM.yyyy HH:mm", Russian);
        var url = $"{webBase}/clash/reports/{report.Run.RunId}";
        var scope = discipline is null ? "все пары" : $"раздел {discipline}";
        await writer.WriteAsync($"""
            <!doctype html>
            <html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E($"Коллизии {check} {at} — {scope}")}</title><style>{Style}</style></head><body>
            <h1>Коллизии: {E(check)}</h1>
            <div class="muted">Версия {report.Run.Version} · прогон {at} · {E(scope)} · {N(files.Count)} пар, {N(files.Sum(f => f.Rows))} коллизий</div>
            <div class="muted">Отчёт в ToadFlow: <a href="{E(url)}">{E(url)}</a></div>
            """);

        var s = report.Summary;
        await writer.WriteAsync("<div class=\"tiles\">");
        foreach (var (label, value) in new (string, int)[]
                 {
                     ("Новых", s.New), ("Вернулись", s.Returned), ("Активных", s.Active), ("Исправлено", s.Fixed), ("Ушли из-за правил", s.GoneByRules),
                     ("Не проверено", s.Unchecked), ("Фиктивных", s.Fake)
                 })
            await writer.WriteAsync($"<div class=\"tile\"><b>{N(value)}</b>{label}</div>");
        await writer.WriteAsync("</div>");

        var disciplines = files.SelectMany(f => new[] { f.Pair.DisciplineA, f.Pair.DisciplineB }).Distinct()
            .Where(d => discipline is null || d == discipline).OrderBy(ranks.Of);
        foreach (var name in disciplines)
        {
            await writer.WriteAsync($"<h2>{E(name)}</h2><table><tr><th>Пара</th><th>Файл</th><th class=\"num\">Коллизий</th><th class=\"num\">С картинками</th></tr>");
            foreach (var file in files.Where(f => f.Pair.DisciplineA == name || f.Pair.DisciplineB == name))
                await writer.WriteAsync($"<tr><td>{E(file.Pair.Label)}</td><td><a href=\"{E(file.File)}\">{E(file.File)}</a></td>" +
                                        $"<td class=\"num\">{N(file.Rows)}</td><td class=\"num\">{N(file.Images)}</td></tr>");
            await writer.WriteAsync("</table>");
        }

        await writer.WriteAsync("</body></html>");
    }

    private static string E(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    private static string N(int value) => value.ToString("N0", Russian);
}
