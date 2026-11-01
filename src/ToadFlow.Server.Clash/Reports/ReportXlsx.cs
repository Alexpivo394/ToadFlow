using ToadFlow.Server.Files;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>Выгрузка отчёта в Excel: лист «Коллизии» со всеми строками по фильтру (потоком) и лист «Сводка» с итогом и парами.</summary>
public static class ReportXlsx
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static async Task WriteAsync(Stream output, ReportView report, IAsyncEnumerable<ReportExportRow> rows, string webBase, TimeZoneInfo zone,
        CancellationToken ct)
    {
        await using var writer = XlsxWriter.Create(output);
        var sheet = writer.AddSheet("Коллизии", ReportColumns.Layout);
        await foreach (var row in rows.WithCancellation(ct))
            await sheet.WriteRowAsync(ReportColumns.Cells(row, webBase, report.Run.PartId, zone), ct);

        var summary = writer.AddSheet("Сводка", [("Показатель", 34), ("Значение", 14), ("", 10), ("", 10), ("", 12), ("", 10)]);
        var s = report.Summary;
        var at = TimeZoneInfo.ConvertTime(report.Run.FinishedAt, zone);
        await summary.WriteRowAsync([XlsxCell.Text("Проверка"), XlsxCell.Text(report.Check.Object is { Length: > 0 } o ? $"{o} · {report.Check.Name}" : report.Check.Name)], ct);
        await summary.WriteRowAsync([XlsxCell.Text("Версия"), XlsxCell.Number(report.Run.Version)], ct);
        await summary.WriteRowAsync([XlsxCell.Text("Прогон"), XlsxCell.Date(at)], ct);
        foreach (var (label, value) in new (string, int)[]
                 {
                     ("Найдено", s.Found), ("Новых", s.New), ("Вернулись", s.Returned), ("Активных", s.Active), ("Исправлено", s.Fixed),
                     ("Ушли из-за правил", s.GoneByRules), ("Не проверено", s.Unchecked), ("Фиктивных", s.Fake)
                 })
            await summary.WriteRowAsync([XlsxCell.Text(label), XlsxCell.Number(value)], ct);

        await summary.WriteRowAsync([], ct);
        await summary.WriteRowAsync([XlsxCell.Text("Пара разделов"), XlsxCell.Text("Открытых сейчас"), XlsxCell.Text("Новых"), XlsxCell.Text("Вернулись"),
            XlsxCell.Text("Исправлено"), XlsxCell.Text("Фиктивных")], ct);
        foreach (var pair in report.Pairs)
            await summary.WriteRowAsync([XlsxCell.Text(pair.Label), XlsxCell.Number(pair.Open), XlsxCell.Number(pair.New), XlsxCell.Number(pair.Returned),
                XlsxCell.Number(pair.Fixed), XlsxCell.Number(pair.Fake)], ct);
    }
}
