using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ToadFlow.Server.Files;

/// <summary>Ячейка листа Excel.</summary>
public abstract record XlsxCell
{
    private XlsxCell()
    {
    }

    public static XlsxCell Text(string? value) => new TextCell(value ?? "");
    public static XlsxCell Number(double value) => new NumberCell(value);

    /// <summary>Дата и время: в ячейку идут часы и минуты <paramref name="value" /> как есть (часовой пояс — забота вызывающего).</summary>
    public static XlsxCell Date(DateTimeOffset value) => new DateCell(value);

    /// <summary>Ссылка формулой <c>HYPERLINK</c>.</summary>
    public static XlsxCell Link(string url, string text) => new LinkCell(url, text);

    public static XlsxCell Empty { get; } = new TextCell("");

    public sealed record TextCell(string Value) : XlsxCell;

    public sealed record NumberCell(double Value) : XlsxCell;

    public sealed record DateCell(DateTimeOffset Value) : XlsxCell;

    public sealed record LinkCell(string Url, string Value) : XlsxCell;
}

/// <summary>
///     Потоковый писатель .xlsx без сторонних библиотек: строки — inline strings, общей таблицы строк нет, поэтому листы на
///     сотни тысяч строк не собираются в памяти. Шапка жирная, первая строка закреплена, автофильтр по шапке. ZIP пишется в
///     буфер в памяти и сливается в выходной поток асинхронно — подходит телу ответа, где синхронная запись запрещена.
/// </summary>
public sealed class XlsxWriter : IAsyncDisposable
{
    private const int StyleHeader = 1;
    private const int StyleDate = 2;
    private const int StyleLink = 3;
    private const int DrainBytes = 256 * 1024;

    private readonly Stream _output;
    private readonly DrainBuffer _buffer = new();
    private readonly ZipArchive _zip;
    private readonly List<string> _sheets = [];
    private SheetWriter? _current;

    private XlsxWriter(Stream output)
    {
        _output = output;
        _zip = new ZipArchive(_buffer, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
    }

    public static XlsxWriter Create(Stream output) => new(output ?? throw new ArgumentNullException(nameof(output)));

    /// <summary>Новый лист с шапкой; прежний лист закрывается.</summary>
    /// <param name="columns">Заголовок и ширина колонки в символах.</param>
    public SheetWriter AddSheet(string name, IReadOnlyList<(string Header, double Width)> columns)
    {
        _current?.Close();
        _sheets.Add(name);
        _current = new SheetWriter(this, _zip.CreateEntry($"xl/worksheets/sheet{_sheets.Count}.xml", CompressionLevel.Fastest), columns);
        return _current;
    }

    internal async Task DrainAsync(bool force, CancellationToken ct = default)
    {
        if (!force && _buffer.Pending < DrainBytes) return;
        await _buffer.DrainToAsync(_output, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _current?.Close();
        _current = null;
        Entry("[Content_Types].xml", ContentTypes());
        Entry("_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Entry("xl/workbook.xml", Workbook());
        Entry("xl/_rels/workbook.xml.rels", WorkbookRels());
        Entry("xl/styles.xml", Styles);
        _zip.Dispose();
        await _buffer.DrainToAsync(_output, CancellationToken.None);
        await _output.FlushAsync();
    }

    private void Entry(string name, string xml)
    {
        using var stream = _zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        var bytes = Encoding.UTF8.GetBytes(xml);
        stream.Write(bytes);
    }

    private string ContentTypes()
    {
        var text = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var i = 1; i <= _sheets.Count; i++)
            text.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        return text.Append("</Types>").ToString();
    }

    private string Workbook()
    {
        var text = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
        for (var i = 0; i < _sheets.Count; i++)
            text.Append($"""<sheet name="{Attribute(SheetName(_sheets[i]))}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        text.Append("</sheets>");
        // Автофильтру Excel нужно скрытое имя _FilterDatabase на каждый лист.
        text.Append("<definedNames>");
        for (var i = 0; i < _sheets.Count; i++)
            text.Append($"""<definedName name="_xlnm._FilterDatabase" localSheetId="{i}" hidden="1">'{Attribute(SheetName(_sheets[i]).Replace("'", "''"))}'!$A$1:${ColumnName(Math.Max(1, _columnsBySheet.ElementAtOrDefault(i)))}$1</definedName>""");
        return text.Append("</definedNames></workbook>").ToString();
    }

    private readonly List<int> _columnsBySheet = [];

    internal void Columns(int count) => _columnsBySheet.Add(count);

    private string WorkbookRels()
    {
        var text = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (var i = 1; i <= _sheets.Count; i++)
            text.Append($"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""");
        text.Append($"""<Relationship Id="rId{_sheets.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        return text.Append("</Relationships>").ToString();
    }

    private const string Styles =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="dd.mm.yyyy hh:mm"/></numFmts><fonts count="3"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font><font><u/><sz val="11"/><color rgb="FF0563C1"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="4"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs></styleSheet>""";

    /// <summary>Имя листа Excel: до 31 символа, без <c>[]:*?/\</c>.</summary>
    private static string SheetName(string name)
    {
        var clean = new string(name.Select(c => "[]:*?/\\".Contains(c) ? '_' : c).ToArray());
        return clean.Length > 31 ? clean[..31] : clean.Length == 0 ? "Лист" : clean;
    }

    internal static string ColumnName(int index)
    {
        var name = "";
        for (var n = index; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    /// <summary>Текст для XML: символы, недопустимые в XML 1.0, вырезаются, служебные — экранируются.</summary>
    internal static string Escape(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                text.Append(c).Append(value[++i]);
                continue;
            }

            if (char.IsSurrogate(c) || c is '￾' or '￿' || (c < 0x20 && c is not ('\t' or '\n' or '\r'))) continue;
            text.Append(c switch
            {
                '<' => "&lt;",
                '>' => "&gt;",
                '&' => "&amp;",
                '"' => "&quot;",
                _ => c.ToString()
            });
        }

        return text.ToString();
    }

    private static string Attribute(string value) => SecurityElement.Escape(value) ?? "";

    /// <summary>Лист: строки пишутся по одной, автофильтр и конец листа — при закрытии.</summary>
    public sealed class SheetWriter
    {
        private readonly XlsxWriter _owner;
        private readonly Stream _entry;
        private readonly StreamWriter _writer;
        private readonly int _columns;
        private int _row;
        private bool _closed;

        internal SheetWriter(XlsxWriter owner, ZipArchiveEntry entry, IReadOnlyList<(string Header, double Width)> columns)
        {
            _owner = owner;
            _columns = Math.Max(1, columns.Count);
            owner.Columns(_columns);
            _entry = entry.Open();
            _writer = new StreamWriter(_entry, new UTF8Encoding(false), 64 * 1024);
            _writer.Write(
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
            _writer.Write("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
            _writer.Write("<cols>");
            for (var i = 0; i < columns.Count; i++)
                _writer.Write($"""<col min="{i + 1}" max="{i + 1}" width="{columns[i].Width.ToString(CultureInfo.InvariantCulture)}" customWidth="1"/>""");
            _writer.Write("</cols><sheetData>");
            WriteRow(columns.Select(c => XlsxCell.Text(c.Header)).ToList(), StyleHeader);
        }

        public async Task WriteRowAsync(IReadOnlyList<XlsxCell> cells, CancellationToken ct = default)
        {
            if (_closed) throw new InvalidOperationException("Лист уже закрыт");
            WriteRow(cells, null);
            if (_row % 200 == 0)
            {
                _writer.Flush();
                await _owner.DrainAsync(false, ct);
            }
        }

        private void WriteRow(IReadOnlyList<XlsxCell> cells, int? style)
        {
            _row++;
            _writer.Write($"<row r=\"{_row}\">");
            for (var i = 0; i < cells.Count; i++)
            {
                var reference = ColumnName(i + 1) + _row;
                var s = style is { } fixedStyle ? $" s=\"{fixedStyle}\"" : "";
                switch (cells[i])
                {
                    case XlsxCell.NumberCell number:
                        _writer.Write($"<c r=\"{reference}\"{s}><v>{number.Value.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    case XlsxCell.DateCell date:
                        _writer.Write(
                            $"<c r=\"{reference}\" s=\"{style ?? StyleDate}\"><v>{date.Value.DateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    case XlsxCell.LinkCell link:
                        var formula = $"HYPERLINK(\"{link.Url.Replace("\"", "\"\"")}\",\"{link.Value.Replace("\"", "\"\"")}\")";
                        _writer.Write($"<c r=\"{reference}\" t=\"str\" s=\"{style ?? StyleLink}\"><f>{Escape(formula)}</f><v>{Escape(link.Value)}</v></c>");
                        break;
                    case XlsxCell.TextCell { Value.Length: 0 }:
                        break;
                    case XlsxCell.TextCell text:
                        _writer.Write($"<c r=\"{reference}\" t=\"inlineStr\"{s}><is><t xml:space=\"preserve\">{Escape(text.Value)}</t></is></c>");
                        break;
                }
            }

            _writer.Write("</row>");
        }

        internal void Close()
        {
            if (_closed) return;
            _closed = true;
            _writer.Write($"</sheetData><autoFilter ref=\"A1:{ColumnName(_columns)}1\"/></worksheet>");
            _writer.Dispose();
        }
    }

    /// <summary>Поток без поиска, копящий байты ZIP до слива в выходной поток.</summary>
    private sealed class DrainBuffer : Stream
    {
        private MemoryStream _pending = new();

        public long Pending => _pending.Length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _pending.Write(buffer);

        public async Task DrainToAsync(Stream output, CancellationToken ct)
        {
            if (_pending.Length == 0) return;
            await output.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), ct);
            _pending = new MemoryStream();
        }
    }
}
