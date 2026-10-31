using System.Globalization;
using System.Text;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>
///     Сообщение в чат об утверждённом прогоне (спек §13). Без HTTP: тот же текст пойдёт в Telegram или Bitrix, когда появится
///     отправка.
/// </summary>
public static class ReportChatMessage
{
    private const int TopPairs = 5;
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <param name="zone">Часовой пояс «Общих» — время прогона в нём.</param>
    public static string Compose(ReportView report, string reportUrl, TimeZoneInfo zone)
    {
        var summary = report.Summary;
        var at = TimeZoneInfo.ConvertTime(report.Run.FinishedAt, zone);
        var title = report.Check.Object is { Length: > 0 } project ? $"{project} · {report.Check.Name}" : report.Check.Name;
        var text = new StringBuilder();
        text.Append($"Коллизии: {title} (v{report.Run.Version}), прогон {at.ToString("dd.MM.yyyy HH:mm", Russian)}\n");
        text.Append($"Новых {N(summary.New)}, вернулись {N(summary.Returned)}, активных {N(summary.Active)}, исправлено {N(summary.Fixed)}\n");

        var pairs = report.Pairs.Where(p => p.New > 0).OrderByDescending(p => p.New).ThenBy(p => p.Pair, StringComparer.CurrentCultureIgnoreCase)
            .Take(TopPairs).ToList();
        if (pairs.Count > 0) text.Append($"Новые по разделам: {string.Join(", ", pairs.Select(p => $"{p.Pair} — {N(p.New)}"))}\n");

        if (summary.Unchecked > 0)
        {
            var models = report.Models.Where(m => m.Unchecked).Select(m => m.Name).ToList();
            text.Append($"Не проверено {N(summary.Unchecked)}");
            if (models.Count > 0) text.Append($": не выгрузились {string.Join(", ", models)}");
            text.Append('\n');
        }

        text.Append($"Отчёт: {reportUrl}");
        return text.ToString();
    }

    private static string N(int value) => value.ToString("N0", Russian);
}
