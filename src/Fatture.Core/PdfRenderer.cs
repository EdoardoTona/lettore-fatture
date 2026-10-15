using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Fatture.Core;

public static partial class PdfRenderer
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly Color Ink = Color.FromRgb(29, 43, 55);
    private static readonly Color Accent = Color.FromRgb(28, 101, 112);
    private static readonly Color Light = Color.FromRgb(240, 244, 246);
    private static readonly Lock FontLock = new();
    private static bool fontsReady;

    public static void Invoice(Invoice invoice, string path)
    {
        var doc = Create(invoice.Title);
        var section = doc.AddSection();
        SetupSection(section, "Copia di cortesia · Il documento originale è il file XML");
        var consumed = new HashSet<XElement>();
        string Take(XElement node, string name)
        {
            var child = node.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            if (child == null) return "";
            consumed.Add(child);
            return child.Value.Trim();
        }
        Label(section, "FATTURA ELETTRONICA");
        var heading = section.AddParagraph(invoice.Title, "Title");
        heading.Format.SpaceAfter = Unit.FromPoint(5);
        section.AddParagraph($"{Date(invoice.Date)}  ·  {invoice.Version}  ·  {invoice.Type}", "Subtitle");
        Take(invoice.General, "Numero"); Take(invoice.General, "Data"); Take(invoice.General, "TipoDocumento");
        Take(invoice.General, "Divisa");

        var parties = section.AddTable();
        parties.AddColumn(Unit.FromMillimeter(87)); parties.AddColumn(Unit.FromMillimeter(87));
        var partyRow = parties.AddRow();
        RenderParty(partyRow.Cells[0], invoice.Header.Child("CedentePrestatore"), "FORNITORE", consumed);
        RenderParty(partyRow.Cells[1], invoice.Header.Child("CessionarioCommittente"), "CLIENTE", consumed);
        Space(section);
        foreach (var reason in invoice.General.Children("Causale"))
        { section.AddParagraph(Text(reason.Value), "Body"); consumed.Add(reason); }

        var lines = invoice.Body.Child("DatiBeniServizi").Children("DettaglioLinee").ToArray();
        if (lines.Length > 10000) throw new InvalidDataException("La fattura supera il limite di 10.000 righe.");
        var table = Table(section, [9, 71, 15, 26, 15, 12, 26], ["N.", "Descrizione", "Q.tà", "Prezzo", "U.M.", "IVA", "Importo"]);
        foreach (var line in lines)
        {
            var description = Take(line, "Descrizione");
            var chunks = Chunks(Text(description), 650).ToArray();
            var number = Take(line, "NumeroLinea"); var qty = Number(Take(line, "Quantita"));
            var price = Number(Take(line, "PrezzoUnitario")); var unit = Take(line, "UnitaMisura");
            var vat = Number(Take(line, "AliquotaIVA")); var total = Number(Take(line, "PrezzoTotale"));
            var nature = Take(line, "Natura"); if (nature.Length > 0) vat += "\n" + nature;
            for (int i = 0; i < chunks.Length; i++)
                Row(table, i == 0 ? [number, chunks[i], qty, price, unit, vat, total] : ["", chunks[i], "", "", "", "", ""]);
        }
        Space(section);
        var taxes = Table(section, [22, 29, 43, 35, 45], ["IVA %", "Natura", "Imponibile", "Imposta", "Esigibilità"]);
        foreach (var tax in invoice.Body.Child("DatiBeniServizi").Children("DatiRiepilogo"))
        {
            Row(taxes, [Number(Take(tax, "AliquotaIVA")), Take(tax, "Natura"), Number(Take(tax, "ImponibileImporto")),
                Number(Take(tax, "Imposta")), Take(tax, "EsigibilitaIVA") switch { "I" => "Immediata", "D" => "Differita", "S" => "Split payment", var s => s }]);
            var legal = Take(tax, "RiferimentoNormativo");
            if (legal.Length > 0) section.AddParagraph(Text(legal), "Small");
        }
        var amount = Take(invoice.General, "ImportoTotaleDocumento");
        var totalP = section.AddParagraph(amount.Length > 0 ? $"Totale documento: {Number(amount)} {invoice.Currency}" : "Totale documento non indicato nel file XML", "Total");
        totalP.Format.SpaceBefore = Unit.FromPoint(14);
        var payments = invoice.Body.Children("DatiPagamento").ToArray();
        foreach (var payment in payments)
        {
            Heading(section, "Pagamento");
            Details(section, payment, consumed);
        }

        // Preserve optional data instead of silently losing CIG/CUP, discounts, withholding,
        // pension contributions, DDT, linked invoices, codes and future extension fields.
        var extras = invoice.Header.Descendants().Concat(invoice.Body.Descendants())
            .Where(e => !e.HasElements && !consumed.Contains(e) && !string.IsNullOrWhiteSpace(e.Value)
                && !e.AncestorsAndSelf().Any(a => a.Name.LocalName == "Allegati")
                && e.Name.NamespaceName != "http://www.w3.org/2000/09/xmldsig#").ToArray();
        if (extras.Length > 0)
        {
            Heading(section, "Dati aggiuntivi del documento");
            foreach (var group in extras.GroupBy(e => e.Parent))
            {
                var parent = group.Key!;
                var title = LabelName(parent.Name.LocalName);
                if (parent.Name.LocalName == "DettaglioLinee") title += " · riga " + parent.ValueOf("NumeroLinea");
                var p = section.AddParagraph(title, "Small"); p.Format.Font.Bold = true; p.Format.KeepWithNext = true;
                section.AddParagraph(string.Join("   ·   ", group.Select(e => $"{LabelName(e.Name.LocalName)}: {Text(e.Value.Trim())}")), "Small");
            }
        }
        var attachments = invoice.Body.Children("Allegati").ToArray();
        if (attachments.Length > 0)
        {
            Heading(section, "Allegati XML");
            foreach (var a in attachments) section.AddParagraph(Text(a.ValueOf("NomeAttachment")), "Small");
        }
        Save(doc, path);
    }

    public static void Email(string subject, IReadOnlyList<Metadata> headers, string body,
        IReadOnlyList<string> attachments, string path)
    {
        var doc = Create(subject);
        var section = doc.AddSection();
        SetupSection(section, "Email · Contenuto locale, immagini remote escluse");
        Label(section, "MESSAGGIO EMAIL");
        section.AddParagraph(Text(subject), "Title");
        foreach (var header in headers) section.AddParagraph($"{header.Label}: {Text(header.Value)}", "Small");
        if (attachments.Count > 0)
            section.AddParagraph("Allegati: " + Text(string.Join(" · ", attachments)), "Small");
        Space(section);
        var rule = section.AddParagraph(); rule.Format.Borders.Bottom.Width = Unit.FromPoint(0.5);
        rule.Format.Borders.Bottom.Color = Color.FromRgb(214, 222, 228);
        Space(section);
        if (body.Length > 1_000_000) throw new InvalidDataException("Il testo dell’email supera il limite di un milione di caratteri.");
        foreach (var line in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            foreach (var chunk in Chunks(Text(line), 2000))
            {
                var p = section.AddParagraph(chunk, "Body");
                p.Format.SpaceAfter = Unit.FromPoint(line.Length == 0 ? 5 : 2);
            }
        Save(doc, path);
    }

    public static string Number(string value) => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var n) ? n.ToString("#,0.00######", Italian) : value;
    public static string Date(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) ? date.ToString("dd/MM/yyyy", Italian) : value;

    private static Document Create(string title)
    {
        lock (FontLock)
        {
            if (!fontsReady) { GlobalFontSettings.FontResolver = new SystemFonts(); fontsReady = true; }
        }
        var doc = new Document();
        doc.Info.Title = title; doc.Info.Author = "Lettore Fatture";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "ViewerSans"; normal.Font.Size = 9; normal.Font.Color = Ink;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
        normal.ParagraphFormat.WidowControl = true;
        var titleStyle = doc.Styles.AddStyle("Title", StyleNames.Normal);
        titleStyle.Font.Size = 23; titleStyle.Font.Bold = true; titleStyle.ParagraphFormat.SpaceAfter = Unit.FromPoint(14);
        var subtitle = doc.Styles.AddStyle("Subtitle", StyleNames.Normal);
        subtitle.Font.Color = Accent; subtitle.ParagraphFormat.SpaceAfter = Unit.FromPoint(22);
        doc.Styles.AddStyle("Body", StyleNames.Normal).Font.Size = 9;
        doc.Styles.AddStyle("Small", StyleNames.Normal).Font.Size = 8;
        var total = doc.Styles.AddStyle("Total", StyleNames.Normal);
        total.Font.Size = 16; total.Font.Bold = true; total.Font.Color = Accent;
        total.ParagraphFormat.Alignment = ParagraphAlignment.Right;
        return doc;
    }

    private static void SetupSection(Section section, string footerText)
    {
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromMillimeter(18); section.PageSetup.BottomMargin = Unit.FromMillimeter(20);
        section.PageSetup.LeftMargin = Unit.FromMillimeter(18); section.PageSetup.RightMargin = Unit.FromMillimeter(18);
        section.PageSetup.FooterDistance = Unit.FromMillimeter(10);
        var footer = section.Footers.Primary.AddParagraph(footerText + "    ·    ");
        footer.Format.Font.Size = 7; footer.Format.Font.Color = Colors.Gray;
        footer.AddPageField(); footer.AddText(" / "); footer.AddNumPagesField();
    }

    private static void RenderParty(Cell cell, XElement party, string label, HashSet<XElement> consumed)
    {
        var tag = cell.AddParagraph(label); tag.Format.Font.Size = 8; tag.Format.Font.Color = Accent;
        var name = cell.AddParagraph(Text(Core.Invoice.PartyName(party))); name.Format.Font.Bold = true; name.Format.Font.Size = 11;
        var anagrafica = party.Child("DatiAnagrafici").Child("Anagrafica");
        foreach (var e in anagrafica.Elements()) consumed.Add(e);
        string Get(XElement parent, string key)
        {
            var child = parent.Elements().FirstOrDefault(e => e.Name.LocalName == key);
            if (child == null) return ""; consumed.Add(child); return child.Value.Trim();
        }
        void Line(string text) { if (!string.IsNullOrWhiteSpace(text)) cell.AddParagraph(Text(text)).Format.Font.Size = 8; }
        var data = party.Child("DatiAnagrafici"); var vat = data.Child("IdFiscaleIVA");
        var country = Get(vat, "IdPaese"); var code = Get(vat, "IdCodice");
        if (country.Length + code.Length > 0) Line("P. IVA " + country + code);
        var fiscal = Get(data, "CodiceFiscale"); if (fiscal.Length > 0) Line("C.F. " + fiscal);
        var address = party.Child("Sede");
        Line($"{Get(address, "Indirizzo")} {Get(address, "NumeroCivico")}".Trim());
        var zip = Get(address, "CAP"); var city = Get(address, "Comune"); var province = Get(address, "Provincia");
        Line($"{zip} {city}".Trim() + (province.Length > 0 ? $" ({province})" : ""));
        var nation = Get(address, "Nazione");
        if (nation.Length > 0 && !nation.Equals("IT", StringComparison.OrdinalIgnoreCase)) Line(nation);
        Details(cell, party, consumed);
    }

    private static void Details(object container, XElement parent, HashSet<XElement> consumed)
    {
        foreach (var e in parent.Descendants().Where(e => !e.HasElements && !consumed.Contains(e) && e.Value.Trim().Length > 0))
        {
            var value = e.Name.LocalName.Contains("Importo", StringComparison.Ordinal) ? Number(e.Value.Trim()) : e.Value.Trim();
            if (e.Name.LocalName.StartsWith("Data", StringComparison.Ordinal)) value = Date(value);
            var text = $"{LabelName(e.Name.LocalName)}: {Text(value)}";
            if (container is Cell cell) cell.AddParagraph(text).Format.Font.Size = 8;
            else ((Section)container).AddParagraph(text, "Small");
            consumed.Add(e);
        }
    }

    private static Table Table(Section section, double[] widths, string[] labels)
    {
        var table = section.AddTable();
        table.Format.Font.Size = 8;
        table.TopPadding = Unit.FromPoint(6); table.BottomPadding = Unit.FromPoint(6);
        table.LeftPadding = Unit.FromPoint(4); table.RightPadding = Unit.FromPoint(4);
        foreach (var w in widths) table.AddColumn(Unit.FromMillimeter(w));
        var header = table.AddRow(); header.HeadingFormat = true; header.Shading.Color = Light;
        header.Format.Font.Bold = true; header.Format.Font.Color = Accent;
        for (int i = 0; i < labels.Length; i++) header.Cells[i].AddParagraph(labels[i]);
        return table;
    }

    private static void Row(Table table, string[] values)
    {
        var row = table.AddRow();
        row.Borders.Bottom.Color = Light; row.Borders.Bottom.Width = Unit.FromPoint(0.5);
        for (int i = 0; i < values.Length; i++)
        {
            row.Cells[i].AddParagraph(Text(values[i]));
            row.Cells[i].Format.Alignment = ParagraphAlignment.Left;
        }
    }

    private static void Heading(Section section, string value)
    {
        var p = section.AddParagraph(value); p.Format.Font.Bold = true; p.Format.Font.Color = Accent;
        p.Format.SpaceBefore = Unit.FromPoint(16); p.Format.SpaceAfter = Unit.FromPoint(7); p.Format.KeepWithNext = true;
    }
    private static void Label(Section section, string value)
    {
        var p = section.AddParagraph(value); p.Format.Font.Color = Accent;
        p.Format.Font.Size = 8; p.Format.Font.Bold = true; p.Format.SpaceAfter = Unit.FromPoint(12);
    }
    private static void Space(Section section) => section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);
    private static void Save(Document doc, string path)
    {
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
        renderer.PdfDocument.Dispose();
    }
    private static string Text(string value) => LongToken().Replace(ControlChars().Replace(value, ""), "$0 ");
    [GeneratedRegex(@"[^\S\r\n]*[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex ControlChars();
    [GeneratedRegex(@"[^\s]{70}")]
    private static partial Regex LongToken();
    [GeneratedRegex(@"(?<=[a-zà-ù])(?=[A-Z])")]
    private static partial Regex CamelCase();
    private static string LabelName(string name) => name switch
    {
        "IdCodice" => "Partita IVA / identificativo", "IdPaese" => "Paese IVA", "CodiceFiscale" => "Codice fiscale",
        "ModalitaPagamento" => "Modalità (codice)", "CondizioniPagamento" => "Condizioni (codice)",
        "DataScadenzaPagamento" => "Scadenza", "ImportoPagamento" => "Importo", "NumeroCivico" => "Civico",
        _ => CamelCase().Replace(name, " ")
    };
    private static IEnumerable<string> Chunks(string text, int length)
    {
        if (text.Length == 0) { yield return ""; yield break; }
        for (int i = 0; i < text.Length; i += length) yield return text.Substring(i, Math.Min(length, text.Length - i));
    }

    private sealed class SystemFonts : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new(bold ? "bold" : "regular", false, italic);
        public byte[] GetFont(string faceName)
        {
            var filename = faceName == "bold" ? "Arial Bold.ttf" : "Arial.ttf";
            var mac = Path.Combine("/System/Library/Fonts/Supplemental", filename);
            var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), faceName == "bold" ? "arialbd.ttf" : "arial.ttf");
            var linux = faceName == "bold" ? "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf" : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
            foreach (var path in new[] { mac, win, linux }) if (File.Exists(path)) return File.ReadAllBytes(path);
            throw new FileNotFoundException("Font Arial non trovato nel sistema operativo.");
        }
    }
}
