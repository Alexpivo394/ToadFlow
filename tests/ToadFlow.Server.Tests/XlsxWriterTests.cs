using System.IO.Compression;
using System.Xml.Linq;
using Shouldly;
using ToadFlow.Server.Files;

namespace ToadFlow.Server.Tests;

public class XlsxWriterTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Поток только на запись, без поиска и длины — как тело ответа.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    }

    private static async Task<byte[]> WriteAsync(Func<XlsxWriter, Task> write, bool forwardOnly = false)
    {
        using var memory = new MemoryStream();
        Stream target = forwardOnly ? new ForwardOnlyStream(memory) : memory;
        await using (var writer = XlsxWriter.Create(target)) await write(writer);
        return memory.ToArray();
    }

    private static Dictionary<string, XDocument> Parts(byte[] xlsx)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        return zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels"))
            .ToDictionary(e => e.FullName, e =>
            {
                using var stream = e.Open();
                return XDocument.Load(stream);
            });
    }

    private static List<List<XElement>> Rows(XDocument sheet) =>
        sheet.Root!.Element(Main + "sheetData")!.Elements(Main + "row").Select(r => r.Elements(Main + "c").ToList()).ToList();

    private static string? Text(XElement cell) => cell.Attribute("t")?.Value switch
    {
        "inlineStr" => cell.Element(Main + "is")!.Element(Main + "t")!.Value,
        _ => cell.Element(Main + "v")?.Value
    };

    [Test]
    public async Task XlsxWriter_TwoSheets_OpensAsValidPackage()
    {
        var when = new DateTimeOffset(2026, 10, 31, 12, 30, 0, TimeSpan.FromHours(3));
        var xlsx = await WriteAsync(async writer =>
        {
            var sheet = writer.AddSheet("Коллизии", [("№", 6), ("Текст", 30), ("Когда", 18), ("Ссылка", 12)]);
            await sheet.WriteRowAsync([XlsxCell.Number(1), XlsxCell.Text("Стена \"А\" & <труба>"), XlsxCell.Date(when), XlsxCell.Link("https://x/a?b=1&c=\"2\"", "открыть")]);
            await sheet.WriteRowAsync([XlsxCell.Number(2.5), XlsxCell.Text(""), XlsxCell.Text("x"), XlsxCell.Text("y")]);
            var summary = writer.AddSheet("Сводка", [("Показатель", 20), ("Значение", 10)]);
            await summary.WriteRowAsync([XlsxCell.Text("Новых"), XlsxCell.Number(5)]);
        });

        var parts = Parts(xlsx);
        parts.Keys.ShouldContain("[Content_Types].xml");
        parts.Keys.ShouldContain("xl/workbook.xml");
        parts.Keys.ShouldContain("xl/styles.xml");
        parts.Keys.ShouldContain("xl/_rels/workbook.xml.rels");
        parts.Keys.ShouldContain("_rels/.rels");
        parts["xl/workbook.xml"].Root!.Element(Main + "sheets")!.Elements(Main + "sheet").Select(s => s.Attribute("name")!.Value).ShouldBe(["Коллизии", "Сводка"]);

        var first = parts["xl/worksheets/sheet1.xml"];
        var rows = Rows(first);
        rows.Count.ShouldBe(3);
        rows[0].Select(Text).ShouldBe(["№", "Текст", "Когда", "Ссылка"]);
        rows[0].ShouldAllBe(c => c.Attribute("s")!.Value == "1"); // жирная шапка
        Text(rows[1][0]).ShouldBe("1");
        Text(rows[1][1]).ShouldBe("Стена \"А\" & <труба>");
        double.Parse(Text(rows[1][2])!, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(new DateTime(2026, 10, 31, 12, 30, 0).ToOADate(), 1e-6);
        rows[1][3].Element(Main + "f")!.Value.ShouldBe("HYPERLINK(\"https://x/a?b=1&c=\"\"2\"\"\",\"открыть\")");
        Text(rows[2][0]).ShouldBe("2.5");
        first.Root!.Element(Main + "autoFilter")!.Attribute("ref")!.Value.ShouldBe("A1:D1");
        var pane = first.Root.Element(Main + "sheetViews")!.Element(Main + "sheetView")!.Element(Main + "pane")!;
        (pane.Attribute("ySplit")!.Value, pane.Attribute("state")!.Value).ShouldBe(("1", "frozen"));
        first.Root.Element(Main + "cols")!.Elements(Main + "col").Count().ShouldBe(4);
        Rows(parts["xl/worksheets/sheet2.xml"]).Count.ShouldBe(2);
    }

    [Test]
    public async Task XlsxWriter_InvalidXmlChars_Stripped()
    {
        var xlsx = await WriteAsync(async writer =>
            await writer.AddSheet("Лист", [("A", 10)]).WriteRowAsync([XlsxCell.Text("a\u0001b\u0008c\td￾e\uD800f")]));

        Text(Rows(Parts(xlsx)["xl/worksheets/sheet1.xml"])[1][0]).ShouldBe("abc\tdef");
    }

    [Test]
    public async Task XlsxWriter_NonSeekableStream_Works()
    {
        var xlsx = await WriteAsync(async writer =>
        {
            var sheet = writer.AddSheet("Лист", [("A", 10)]);
            for (var i = 0; i < 1000; i++) await sheet.WriteRowAsync([XlsxCell.Number(i)]);
        }, forwardOnly: true);

        Rows(Parts(xlsx)["xl/worksheets/sheet1.xml"]).Count.ShouldBe(1001);
    }
}
