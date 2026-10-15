using System.IO.Compression;
using System.Xml.Linq;
using Fatture.Core;
using Xunit;

namespace Fatture.Tests;

public sealed class ExcelExporterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fatture-excel-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static Invoice Invoice(string number = "000042", string causale = "Consulenza generale") => Assert.Single(InvoiceParser.Parse(XDocument.Parse($$"""
        <FatturaElettronica versione="FPR12"><FatturaElettronicaHeader>
        <CedentePrestatore><DatiAnagrafici><IdFiscaleIVA><IdPaese>IT</IdPaese><IdCodice>00123456789</IdCodice></IdFiscaleIVA><CodiceFiscale>ABC123</CodiceFiscale><Anagrafica><Denominazione>=1+1</Denominazione></Anagrafica></DatiAnagrafici></CedentePrestatore>
        <CessionarioCommittente><DatiAnagrafici><Anagrafica><Denominazione>Cliente prova</Denominazione></Anagrafica></DatiAnagrafici></CessionarioCommittente>
        </FatturaElettronicaHeader><FatturaElettronicaBody><DatiGenerali><DatiGeneraliDocumento>
        <TipoDocumento>TD04</TipoDocumento><Divisa>EUR</Divisa><Data>2026-10-15</Data><Numero>{{number}}</Numero><Causale>{{causale}}</Causale>
        <ImportoTotaleDocumento>162.00</ImportoTotaleDocumento><DatiRitenuta><ImportoRitenuta>10.00</ImportoRitenuta></DatiRitenuta><DatiBollo><ImportoBollo>2.00</ImportoBollo></DatiBollo>
        </DatiGeneraliDocumento></DatiGenerali><DatiBeniServizi><DettaglioLinee><Descrizione>Prima prestazione</Descrizione></DettaglioLinee>
        <DettaglioLinee><Descrizione>Seconda prestazione</Descrizione></DettaglioLinee>
        <DatiRiepilogo><AliquotaIVA>22.00</AliquotaIVA><ImponibileImporto>100.00</ImponibileImporto><Imposta>22.00</Imposta><EsigibilitaIVA>S</EsigibilitaIVA></DatiRiepilogo>
        <DatiRiepilogo><AliquotaIVA>0.00</AliquotaIVA><Natura>N2.2</Natura><ImponibileImporto>40.00</ImponibileImporto><Imposta>0.00</Imposta></DatiRiepilogo></DatiBeniServizi>
        <DatiPagamento><DettaglioPagamento><ImportoPagamento>80.00</ImportoPagamento><DataScadenzaPagamento>2026-12-01</DataScadenzaPagamento><ModalitaPagamento>MP05</ModalitaPagamento></DettaglioPagamento>
        <DettaglioPagamento><ImportoPagamento>72.00</ImportoPagamento><DataScadenzaPagamento>2026-11-01</DataScadenzaPagamento></DettaglioPagamento></DatiPagamento>
        </FatturaElettronicaBody></FatturaElettronica>
        """)));
    private static RenderedDocument Document(Invoice invoice, string source = "invoice.xml") => new("id-" + source, "invoice", invoice.Title, "", source, "unused.pdf", "invoice.pdf", [], [], AccountingMapper.FromInvoice(invoice, source));
    private string Output => Path.Combine(directory, "fatture.xlsx");
    private List<Dictionary<string, XElement>> ReadRows()
    {
        using var zip = ZipFile.OpenRead(Output);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var rows = XDocument.Load(stream).Descendants(Ns + "row").ToArray();
        var headers = rows[0].Elements(Ns + "c").ToDictionary(c => new string(c.Attribute("r")!.Value.TakeWhile(char.IsLetter).ToArray()), c => c.Value);
        return rows.Skip(1).Select(r => r.Elements(Ns + "c").ToDictionary(
            c => headers[new string(c.Attribute("r")!.Value.TakeWhile(char.IsLetter).ToArray())], c => c)).ToList();
    }

    [Fact] public void ExportsOneInvoicePerRowWithTypedAmountsAndTextIdentifiers()
    {
        var result = ExcelExporter.ExportDocuments([Document(Invoice())], Output);
        Assert.Equal(1, result.InvoiceCount);
        var row = Assert.Single(ReadRows());
        Assert.Equal("00123456789", row["Partita IVA fornitore"].Value);
        Assert.Equal("inlineStr", row["Partita IVA fornitore"].Attribute("t")!.Value);
        Assert.Equal("000042", row["Numero documento"].Value);
        Assert.Equal("=1+1", row["Fornitore"].Value);
        Assert.Equal("inlineStr", row["Fornitore"].Attribute("t")!.Value);
        Assert.Empty(row.Values.SelectMany(c => c.Descendants(Ns + "f")));
        Assert.Equal(140m, decimal.Parse(row["Imponibile totale"].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(22m, decimal.Parse(row["IVA totale"].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("TD04", row["Tipo documento"].Value);
        Assert.Equal("Consulenza generale", row["Descrizione / causale"].Value);
        Assert.Contains("N2.2", row["Dettaglio IVA per aliquota"].Value);
        Assert.Contains("Split payment", row["Esigibilità IVA"].Value);
        Assert.Equal("152.00", row["Importo pagamenti"].Value);
        Assert.Equal("3", row["Prima scadenza"].Attribute("s")!.Value);
        Assert.Null(row["IVA totale"].Attribute("t"));
    }

    [Fact] public void IgnoresOtherDocumentsAndDeduplicatesOnlyIdenticalContent()
    {
        var first = Document(Invoice(), "invoice.xml");
        var signed = Document(Invoice(), "invoice.xml.p7m");
        var email = first with { Kind = "email" }; // Ignore even if unrelated data is present.
        var pdf = first with { Kind = "pdf", Invoice = null };
        var changed = Document(Invoice(causale: "Descrizione aggiornata"), "updated.xml");
        var result = ExcelExporter.ExportDocuments([first, signed, email, pdf, changed], Output);
        Assert.Equal(2, result.InvoiceCount); Assert.Equal(1, result.DuplicateCount);
        var rows = ReadRows(); Assert.Equal(2, rows.Count);
        Assert.Equal("invoice.xml; invoice.xml.p7m", rows[0]["File sorgente"].Value);
    }

    [Fact] public void MissingTotalStaysEmptyAndDescriptionFallsBackToLines()
    {
        var invoice = Invoice(causale: "");
        invoice.General.Elements().Single(e => e.Name.LocalName == "ImportoTotaleDocumento").Remove();
        ExcelExporter.ExportDocuments([Document(invoice)], Output);
        var row = Assert.Single(ReadRows());
        Assert.False(row.ContainsKey("Totale documento"));
        Assert.Equal("Prima prestazione\nSeconda prestazione", row["Descrizione / causale"].Value);
        Assert.Throws<InvalidDataException>(() => ExcelExporter.ExportDocuments([], Output));
    }

    [Fact] public void PreservesLongPrecisionAsTextAndWarnsAboutExcelLimits()
    {
        var document = Document(Invoice());
        var values = new Dictionary<string, string>(document.Invoice!.Values)
        { ["total"] = "123456789012345.67", ["description"] = new string('x', 33000) };
        ExcelExporter.ExportDocuments([document with { Invoice = new("different", values) }], Output);
        var row = Assert.Single(ReadRows());
        Assert.Equal("inlineStr", row["Totale documento"].Attribute("t")!.Value);
        Assert.Equal("123456789012345.67", row["Totale documento"].Value);
        Assert.True(row["Descrizione / causale"].Value.Length <= 32767);
        Assert.Contains("testo abbreviato", row["Avvisi export"].Value);
        Assert.Contains("conservato come testo", row["Avvisi export"].Value);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
