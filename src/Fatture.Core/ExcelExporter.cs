using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Fatture.Core;

public static class ExcelExporter
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private sealed record Column(string Key, string Title, double Width, string Kind = "text");
    private static readonly Column[] Columns =
    [
        new("date", "Data documento", 16, "date"), new("number", "Numero documento", 21), new("type", "Tipo documento", 16),
        new("supplierVatCountry", "Paese IVA fornitore", 12), new("supplierVat", "Partita IVA fornitore", 22),
        new("supplierTaxCode", "Codice fiscale fornitore", 23), new("supplier", "Fornitore", 35), new("description", "Descrizione / causale", 60),
        new("currency", "Valuta", 10), new("taxable", "Imponibile totale", 20, "money"), new("vat", "IVA totale", 18, "money"),
        new("total", "Totale documento", 20, "money"), new("paymentTotal", "Importo pagamenti", 20, "money"),
        new("vatRates", "Aliquote IVA (%)", 19), new("vatNatures", "Natura IVA", 16), new("vatCollection", "Esigibilità IVA", 22),
        new("vatBreakdown", "Dettaglio IVA per aliquota", 65), new("withholding", "Ritenute", 18, "money"),
        new("stampDuty", "Bollo", 16, "money"), new("pension", "Contributi previdenziali", 23, "money"),
        new("firstDueDate", "Prima scadenza", 16, "date"), new("dueDates", "Scadenze (ISO)", 30),
        new("paymentMethods", "Modalità pagamento", 22), new("iban", "IBAN", 34),
        new("customer", "Cliente", 35), new("customerVatCountry", "Paese IVA cliente", 12), new("customerVat", "Partita IVA cliente", 22),
        new("customerTaxCode", "Codice fiscale cliente", 23), new("source", "File sorgente", 40), new("warnings", "Avvisi export", 55)
    ];

    public static ExcelExportResult ExportDocuments(IEnumerable<RenderedDocument> documents, string path)
    {
        var invoices = documents.Where(d => d.Kind == "invoice" && d.Invoice != null).Select(d => d.Invoice!).ToArray();
        var unique = invoices.GroupBy(i => i.Fingerprint, StringComparer.Ordinal).ToArray();
        if (unique.Length == 0) throw new InvalidDataException("Non ci sono fatture aperte da esportare.");
        if (unique.Length > 1_048_575) throw new InvalidDataException("Troppe fatture per un singolo foglio Excel.");
        var rows = new List<XElement> { new(Sheet + "row", new XAttribute("r", 1), new XAttribute("ht", 32), new XAttribute("customHeight", 1),
            Columns.Select((c, index) => TextCell(Address(index, 1), c.Title, 1))) };
        for (int i = 0; i < unique.Length; i++)
        {
            var group = unique[i];
            var values = new Dictionary<string, string>(group.First().Values);
            values["source"] = string.Join("; ", group.Select(v => v.Values.GetValueOrDefault("source", "")).Where(v => v.Length > 0).Distinct());
            var row = new XElement(Sheet + "row", new XAttribute("r", i + 2));
            var warnings = new List<string>();
            foreach (var (column, index) in Columns.Select((column, index) => (column, index)))
            {
                var value = values.GetValueOrDefault(column.Key, "");
                if (column.Key == "warnings") value = string.Join("; ", warnings);
                var address = Address(index, i + 2);
                if (value.Length == 0) continue; // Missing amounts stay empty, never become zero.
                if (column.Kind == "money" && AccountingMapper.TryNumber(value, out var number) && SignificantDigits(number) <= 15)
                    row.Add(NumberCell(address, number.ToString(CultureInfo.InvariantCulture), 2));
                else if (column.Kind == "date" && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                         && date.Year >= 1900)
                    row.Add(NumberCell(address, date.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(CultureInfo.InvariantCulture), 3));
                else
                {
                    if (column.Kind != "text") warnings.Add($"{column.Title}: valore conservato come testo");
                    if (value.Length > 32767) warnings.Add($"{column.Title}: testo abbreviato al limite Excel di 32767 caratteri");
                    row.Add(TextCell(address, Limit(value), 0));
                }
            }
            rows.Add(row);
        }
        var end = Address(Columns.Length - 1, rows.Count);
        var worksheet = new XDocument(new XElement(Sheet + "worksheet",
            new XElement(Sheet + "dimension", new XAttribute("ref", "A1:" + end)),
            new XElement(Sheet + "sheetViews", new XElement(Sheet + "sheetView", new XAttribute("workbookViewId", 0),
                new XElement(Sheet + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"),
                    new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
            new XElement(Sheet + "sheetFormatPr", new XAttribute("defaultRowHeight", 30)),
            new XElement(Sheet + "cols", Columns.Select((c, i) => new XElement(Sheet + "col", new XAttribute("min", i + 1),
                new XAttribute("max", i + 1), new XAttribute("width", c.Width), new XAttribute("customWidth", 1)))),
            new XElement(Sheet + "sheetData", rows), new XElement(Sheet + "autoFilter", new XAttribute("ref", "A1:" + end))));
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".fatture-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml", ContentTypes());
                Add(zip, "_rels/.rels", Rels(("rId1", "officeDocument", "xl/workbook.xml")));
                Add(zip, "xl/workbook.xml", new XDocument(new XElement(Sheet + "workbook", new XAttribute(XNamespace.Xmlns + "r", Relationships),
                    new XElement(Sheet + "sheets", new XElement(Sheet + "sheet", new XAttribute("name", "Fatture"), new XAttribute("sheetId", 1), new XAttribute(Relationships + "id", "rId1"))))));
                Add(zip, "xl/_rels/workbook.xml.rels", Rels(("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "styles", "styles.xml")));
                Add(zip, "xl/styles.xml", Styles());
                Add(zip, "xl/worksheets/sheet1.xml", worksheet);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(unique.Length, invoices.Length - unique.Length, fullPath);
    }

    private static string Address(int column, int row)
    {
        var letters = "";
        for (int value = column + 1; value > 0; value = (value - 1) / 26) letters = (char)('A' + (value - 1) % 26) + letters;
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }
    private static XElement TextCell(string address, string value, int style) => new(Sheet + "c", new XAttribute("r", address),
        new XAttribute("s", style), new XAttribute("t", "inlineStr"), new XElement(Sheet + "is", new XElement(Sheet + "t",
            new XAttribute(XNamespace.Xml + "space", "preserve"), Clean(value))));
    private static XElement NumberCell(string address, string value, int style) => new(Sheet + "c", new XAttribute("r", address),
        new XAttribute("s", style), new XElement(Sheet + "v", value));
    private static int SignificantDigits(decimal number) => number == 0 ? 1 :
        number.ToString("G29", CultureInfo.InvariantCulture).Replace("-", "").Replace(".", "").TrimStart('0').Length;
    private static string Limit(string value)
    {
        if (value.Length <= 32767) return value;
        var end = char.IsHighSurrogate(value[32765]) ? 32765 : 32766;
        return value[..end] + "…";
    }
    private static string Clean(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
            if (rune.Value is 9 or 10 or 13 || rune.Value is >= 0x20 and <= 0xD7FF or >= 0xE000 and <= 0xFFFD or >= 0x10000 and <= 0x10FFFF)
                text.Append(rune.ToString());
        return text.ToString();
    }
    private static void Add(ZipArchive zip, string name, XDocument document)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        document.Save(writer);
    }
    private static XDocument ContentTypes()
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        return new(new XElement(ns + "Types",
            new XElement(ns + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ns + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new[] { ("/xl/workbook.xml", "sheet.main"), ("/xl/worksheets/sheet1.xml", "worksheet"), ("/xl/styles.xml", "styles") }
                .Select(p => new XElement(ns + "Override", new XAttribute("PartName", p.Item1),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml." + p.Item2 + "+xml")))));
    }
    private static XDocument Rels(params (string Id, string Type, string Target)[] items)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        return new(new XElement(ns + "Relationships", items.Select(i => new XElement(ns + "Relationship", new XAttribute("Id", i.Id),
            new XAttribute("Type", Relationships.NamespaceName + "/" + i.Type), new XAttribute("Target", i.Target)))));
    }
    private static XDocument Styles() => XDocument.Parse("""
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="2"><numFmt numFmtId="164" formatCode="#,##0.00######;[Red]-#,##0.00######"/><numFmt numFmtId="165" formatCode="dd/mm/yyyy"/></numFmts>
          <fonts count="2"><font><sz val="11"/><name val="Arial"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Arial"/></font></fonts>
          <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1C6570"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="4">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment vertical="top"/></xf>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment vertical="top"/></xf>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """);
}
