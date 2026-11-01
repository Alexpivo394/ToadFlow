using System.Globalization;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Files;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>Строка выгрузки: строка отчёта, когда коллизия найдена впервые, и подпись её пары в порядке справочника.</summary>
public sealed record ReportExportRow(ReportRow Row, DateTimeOffset FirstFoundAt, string PairLabel);

/// <summary>Русские подписи статусов и сторон — как в вебе.</summary>
public static class ReportLabels
{
    public static string Status(CollisionStatus status) => status switch
    {
        CollisionStatus.New => "новая",
        CollisionStatus.Active => "активная",
        CollisionStatus.Returned => "вернулась",
        CollisionStatus.Fake => "фиктивная",
        CollisionStatus.FixedPending => "исправлена (ждёт)",
        CollisionStatus.Fixed => "исправлена",
        CollisionStatus.GoneByRules => "ушла из-за правил",
        CollisionStatus.Unchecked => "не проверена",
        _ => status.ToString()
    };

    public static string Change(CollisionStatus? change) => change is { } status ? Status(status) : "";

    /// <summary>Ссылка на коллизию в вебе — страница проверки с открытой коллизией.</summary>
    public static string CollisionUrl(string webBase, Guid partId, Guid collisionId) => $"{webBase}/clash/checks/{partId}?collision={collisionId}";
}

/// <summary>Колонки выгрузок отчёта — одни на Excel и HTML-таблицу.</summary>
public static class ReportColumns
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private sealed record Column(string Header, double Width, Func<ReportExportRow, Context, XlsxCell> Value);

    private sealed record Context(string WebBase, Guid PartId, TimeZoneInfo Zone);

    private static XlsxCell Text(string? value) => XlsxCell.Text(value);

    private static readonly Column[] All =
    [
        new("№", 7, (r, _) => XlsxCell.Number(r.Row.Collision.Number)),
        new("Статус", 16, (r, _) => Text(ReportLabels.Status(r.Row.Collision.Status))),
        new("Переход в прогоне", 16, (r, _) => Text(ReportLabels.Change(r.Row.Change))),
        new("Кто правит", 13, (r, _) => Text(CollisionFeed.AssigneeText(r.Row.Collision.Assignee, r.Row.Collision.DisciplineA, r.Row.Collision.DisciplineB))),
        new("Пара разделов", 14, (r, _) => Text(Pair(r))),
        new("Глубина, мм", 10, (r, _) => XlsxCell.Number(Math.Round(r.Row.Collision.DepthMm))),
        new("Уровень", 14, (r, _) => Text(Level(r))),
        new("Модель A", 28, (r, _) => Text(r.Row.Collision.ModelNameA)),
        new("Раздел A", 8, (r, _) => Text(r.Row.Collision.DisciplineA)),
        new("Категория A", 22, (r, _) => Text(CategoryNames.Label(r.Row.Collision.CategoryA))),
        new("Семейство A", 24, (r, _) => Text(r.Row.Collision.FamilyA)),
        new("Тип A", 24, (r, _) => Text(r.Row.Collision.TypeA)),
        new("ElementId A", 11, (r, _) => XlsxCell.Number(r.Row.Collision.ElementIdA)),
        new("UniqueId A", 20, (r, _) => Text(r.Row.Collision.UniqueIdA)),
        new("Модель B", 28, (r, _) => Text(r.Row.Collision.ModelNameB)),
        new("Раздел B", 8, (r, _) => Text(r.Row.Collision.DisciplineB)),
        new("Категория B", 22, (r, _) => Text(CategoryNames.Label(r.Row.Collision.CategoryB))),
        new("Семейство B", 24, (r, _) => Text(r.Row.Collision.FamilyB)),
        new("Тип B", 24, (r, _) => Text(r.Row.Collision.TypeB)),
        new("ElementId B", 11, (r, _) => XlsxCell.Number(r.Row.Collision.ElementIdB)),
        new("UniqueId B", 20, (r, _) => Text(r.Row.Collision.UniqueIdB)),
        new("X, мм", 11, (r, _) => XlsxCell.Number(Math.Round(r.Row.Collision.X))),
        new("Y, мм", 11, (r, _) => XlsxCell.Number(Math.Round(r.Row.Collision.Y))),
        new("Z, мм", 11, (r, _) => XlsxCell.Number(Math.Round(r.Row.Collision.Z))),
        new("Найдена впервые: версия", 10, (r, _) => XlsxCell.Number(r.Row.Collision.FirstVersion)),
        new("Найдена впервые: дата", 16, (r, c) => XlsxCell.Date(TimeZoneInfo.ConvertTime(r.FirstFoundAt, c.Zone))),
        new("Последнее примечание", 40, (r, _) => Text(r.Row.LastNote)),
        new("Автор примечания", 18, (r, _) => Text(r.Row.LastNoteBy)),
        new("Дата примечания", 16, (r, c) => r.Row.LastNoteAt is { } at ? XlsxCell.Date(TimeZoneInfo.ConvertTime(at, c.Zone)) : XlsxCell.Empty),
        new("Ссылка", 10, (r, c) => XlsxCell.Link(ReportLabels.CollisionUrl(c.WebBase, c.PartId, r.Row.Collision.Id), "открыть"))
    ];

    public static IReadOnlyList<string> Headers { get; } = All.Select(c => c.Header).ToList();

    public static IReadOnlyList<(string Header, double Width)> Layout { get; } = All.Select(c => (c.Header, c.Width)).ToList();

    public static IReadOnlyList<XlsxCell> Cells(ReportExportRow row, string webBase, Guid partId, TimeZoneInfo zone)
    {
        var context = new Context(webBase, partId, zone);
        return All.Select(c => c.Value(row, context)).ToList();
    }

    /// <summary>Значение ячейки текстом — для HTML-таблицы.</summary>
    public static string Display(XlsxCell cell) => cell switch
    {
        XlsxCell.TextCell t => t.Value,
        XlsxCell.NumberCell n => n.Value.ToString("0.##", Russian),
        XlsxCell.DateCell d => d.Value.ToString("dd.MM.yyyy HH:mm", Russian),
        XlsxCell.LinkCell l => l.Value,
        _ => ""
    };

    public static string Pair(ReportExportRow row) => row.PairLabel;

    public static string Level(ReportExportRow row) =>
        row.Row.Collision.LevelA == row.Row.Collision.LevelB || row.Row.Collision.LevelB is null
            ? row.Row.Collision.LevelA ?? ""
            : row.Row.Collision.LevelA is null
                ? row.Row.Collision.LevelB
                : $"{row.Row.Collision.LevelA} / {row.Row.Collision.LevelB}";
}
