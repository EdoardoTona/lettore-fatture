using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Fatture.Core;
using MimeKit;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Fatture.Tests;

public sealed class DocumentTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fatture-tests-" + Guid.NewGuid().ToString("N"));
    private static string Body(string number = "TEST-42", string description = "Consulenza &amp; assistenza") => $$"""
        <FatturaElettronicaBody><DatiGenerali><DatiGeneraliDocumento>
        <TipoDocumento>TD01</TipoDocumento><Divisa>EUR</Divisa><Data>2026-10-15</Data><Numero>{{number}}</Numero>
        <ImportoTotaleDocumento>122.00</ImportoTotaleDocumento><DatiRitenuta><TipoRitenuta>RT01</TipoRitenuta><ImportoRitenuta>20.00</ImportoRitenuta></DatiRitenuta>
        </DatiGeneraliDocumento></DatiGenerali><DatiBeniServizi><DettaglioLinee>
        <NumeroLinea>1</NumeroLinea><Descrizione>{{description}}</Descrizione><Quantita>1.00</Quantita>
        <PrezzoUnitario>100.00</PrezzoUnitario><PrezzoTotale>100.00</PrezzoTotale><AliquotaIVA>22.00</AliquotaIVA>
        </DettaglioLinee><DatiRiepilogo><AliquotaIVA>22.00</AliquotaIVA><ImponibileImporto>100.00</ImponibileImporto><Imposta>22.00</Imposta></DatiRiepilogo>
        </DatiBeniServizi></FatturaElettronicaBody>
        """;
    private static string InvoiceXml(string? bodies = null) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <p:FatturaElettronica xmlns:p="http://ivaservizi.agenziaentrate.gov.it/docs/xsd/fatture/v1.2" versione="FPR12">
        <FatturaElettronicaHeader>
        <CedentePrestatore><DatiAnagrafici><IdFiscaleIVA><IdPaese>IT</IdPaese><IdCodice>00000000000</IdCodice></IdFiscaleIVA><Anagrafica><Denominazione>Fornitore di prova</Denominazione></Anagrafica></DatiAnagrafici></CedentePrestatore>
        <CessionarioCommittente><DatiAnagrafici><Anagrafica><Nome>Mario</Nome><Cognome>Rossi</Cognome></Anagrafica></DatiAnagrafici></CessionarioCommittente>
        </FatturaElettronicaHeader>{{bodies ?? Body()}}</p:FatturaElettronica>
        """;
    private string Write(string name, byte[] bytes)
    { Directory.CreateDirectory(directory); var path = Path.Combine(directory, name); File.WriteAllBytes(path, bytes); return path; }
    private string Write(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));
    private OpenResult Open(string path) => new DocumentEngine().Open(path, Path.Combine(directory, "out"));

    [Fact] public void MultipleBodiesProduceSeparatePdfsAndPreserveAmounts()
    {
        var result = Open(Write("invoice.xml", InvoiceXml(Body() + Body("TEST-43"))));
        Assert.Equal(2, result.Documents.Count); Assert.Empty(result.Warnings);
        Assert.All(result.Documents, doc => {
            using var pdf = PdfReader.Open(doc.PdfPath, PdfDocumentOpenMode.Import);
            Assert.True(pdf.PageCount > 0);
            Assert.Contains(doc.Metadata, m => m.Label == "Totale" && m.Value == "122,00 EUR");
            Assert.Contains(doc.Metadata, m => m.Label == "Cliente" && m.Value == "Mario Rossi");
        });
    }

    [Fact] public void CmsExtractsInvoiceAndChecksIntegrityWithoutTrustStore()
    {
        using var key = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=Firmatario di prova", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var cms = new SignedCms(new ContentInfo(Encoding.UTF8.GetBytes(InvoiceXml())));
        cms.ComputeSignature(new CmsSigner(cert));
        var bytes = cms.Encode();
        var result = Open(Write("invoice.xml.p7m", bytes));
        var signature = Assert.Single(Assert.Single(result.Documents).Signatures);
        Assert.Equal("Integrità verificata", signature.Integrity);
        var needle = Encoding.UTF8.GetBytes("TEST-42");
        var index = bytes.AsSpan().IndexOf(needle); Assert.True(index > 0); bytes[index] = (byte)'X';
        var tampered = Open(Write("tampered.xml.p7m", bytes));
        Assert.Equal("Firma non valida o non verificabile", Assert.Single(Assert.Single(tampered.Documents).Signatures).Integrity);
    }

    [Fact] public void XmlSignatureCoversInvoiceAndDetectsAlteration()
    {
        using var key = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=Test XML", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var doc = new XmlDocument { PreserveWhitespace = true }; doc.LoadXml(InvoiceXml());
        var signature = new SignedXml(doc) { SigningKey = key };
        var reference = new Reference(""); reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        signature.AddReference(reference); signature.KeyInfo = new KeyInfo(); signature.KeyInfo.AddClause(new KeyInfoX509Data(cert));
        signature.ComputeSignature(); doc.DocumentElement!.AppendChild(doc.ImportNode(signature.GetXml(), true));
        var bytes = Encoding.UTF8.GetBytes(doc.OuterXml);
        Assert.Equal("Integrità verificata", Assert.Single(Signatures.InspectXml(bytes)).Integrity);
        Assert.Equal("Firma non valida o non verificabile", Assert.Single(Signatures.InspectXml(
            Encoding.UTF8.GetBytes(doc.OuterXml.Replace("TEST-42", "CHANGED", StringComparison.Ordinal)))).Integrity);
        var unsupported = Assert.Single(Signatures.InspectXml(Encoding.UTF8.GetBytes(doc.OuterXml.Replace(
            "http://www.w3.org/TR/2001/REC-xml-c14n-20010315", "http://www.w3.org/2006/12/xml-c14n11", StringComparison.Ordinal))));
        Assert.Equal("Test XML", unsupported.Subject);
        Assert.Equal("Verifica non supportata (C14N 1.1)", unsupported.Integrity);
        var xslt = Assert.Single(Signatures.InspectXml(Encoding.UTF8.GetBytes(doc.OuterXml.Replace(
            "http://www.w3.org/TR/2001/REC-xml-c14n-20010315", "http://www.w3.org/TR/1999/REC-xslt-19991116", StringComparison.Ordinal))));
        Assert.Equal("Verifica non supportata (XSLT)", xslt.Integrity);
    }

    [Fact] public void NestedPecFindsInvoiceIgnoresReceiptsAndDeduplicatesInvoice()
    {
        using var original = new MimeMessage(); original.Subject = "Consegna fattura";
        var builder = new BodyBuilder { TextBody = "In allegato la fattura." };
        builder.Attachments.Add("invoice.xml", Encoding.UTF8.GetBytes(InvoiceXml()));
        builder.Attachments.Add("invoice-duplicate.xml", Encoding.UTF8.GetBytes(InvoiceXml()));
        builder.Attachments.Add("daticert.xml", Encoding.UTF8.GetBytes("<postacert><tipo>consegna</tipo></postacert>"));
        original.Body = builder.ToMessageBody();
        using var outer = new MimeMessage(); outer.Subject = "POSTA CERTIFICATA";
        outer.Body = new Multipart("mixed") { new TextPart("plain") { Text = "Busta PEC" }, new MessagePart { Message = original } };
        var path = Path.Combine(directory, "mail.eml"); Directory.CreateDirectory(directory); outer.WriteTo(path);
        var result = Open(path);
        Assert.Single(result.Documents, d => d.Kind == "invoice");
        Assert.Equal(2, result.Documents.Count(d => d.Kind == "email")); Assert.Empty(result.Warnings);
    }

    [Fact] public void HtmlOnlyEmailWithEncodedSubjectProducesEmailPdf()
    {
        var result = Open(Write("normal.eml", "From: sender@example.org\r\nTo: reader@example.org\r\nSubject: =?utf-8?B?Q2l0dMOg?=\r\nMIME-Version: 1.0\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<html><body><h1>Buongiorno</h1><p>Un messaggio normale.</p><img src='https://invalid.example/tracker.png'></body></html>"));
        var doc = Assert.Single(result.Documents); Assert.Equal("email", doc.Kind); Assert.Equal("Città", doc.Title);
        using var pdf = PdfReader.Open(doc.PdfPath, PdfDocumentOpenMode.Import); Assert.True(pdf.PageCount > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SdiDeliveryWrappersAreHiddenOnlyAfterExtractingAnInvoice(bool containsInvoice)
    {
        using var delivery = new MimeMessage(); delivery.Subject = "Invio File 12345";
        var builder = new BodyBuilder { TextBody = "Trasmissione automatica del Sistema di Interscambio." };
        builder.Attachments.Add("invoice.xml", Encoding.UTF8.GetBytes(containsInvoice ? InvoiceXml() : "<ricevuta/>"));
        delivery.Body = builder.ToMessageBody();
        using var certified = new MimeMessage(); certified.Subject = "POSTA CERTIFICATA: Invio File 12345";
        certified.Body = new Multipart("mixed") { new TextPart("plain") { Text = "Busta PEC" }, new MessagePart { Message = delivery } };
        using var forwarded = new MimeMessage(); forwarded.Subject = "Messaggio PEC inoltrato [POSTA CERTIFICATA: Invio File 12345]";
        forwarded.Body = new Multipart("mixed") { new TextPart("plain") { Text = "Inoltro PEC" }, new MessagePart { Message = certified } };
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sdi.eml"); forwarded.WriteTo(path);
        var result = Open(path);
        Assert.Empty(result.Warnings);
        if (containsInvoice) Assert.Equal("invoice", Assert.Single(result.Documents).Kind);
        else Assert.Equal(3, result.Documents.Count(d => d.Kind == "email"));
    }

    [Fact] public void SdiDeliveryDoesNotHideOrdinaryEmailAttachedAlongsideInvoice()
    {
        using var note = new MimeMessage { Subject = "Nota da conservare", Body = new TextPart("plain") { Text = "Dettagli utili." } };
        var builder = new BodyBuilder { TextBody = "Invio automatico." };
        builder.Attachments.Add("invoice.xml", Encoding.UTF8.GetBytes(InvoiceXml()));
        using var delivery = new MimeMessage { Subject = "Invio File 12345" };
        delivery.Body = new Multipart("mixed") { builder.ToMessageBody(), new MessagePart { Message = note } };
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sdi-with-note.eml"); delivery.WriteTo(path);
        var result = Open(path);
        Assert.Single(result.Documents, d => d.Kind == "invoice");
        Assert.Equal("Nota da conservare", Assert.Single(result.Documents, d => d.Kind == "email").Title);
    }

    [Fact] public void LongInvoiceAndLongDescriptionsPaginate()
    {
        var body = Body(description: new string('A', 4000));
        var marker = "</DettaglioLinee>";
        var line = body[(body.IndexOf("<DettaglioLinee>", StringComparison.Ordinal))..(body.IndexOf(marker, StringComparison.Ordinal) + marker.Length)];
        body = body.Replace(line, string.Concat(Enumerable.Repeat(line, 45)), StringComparison.Ordinal);
        var result = Open(Write("long.xml", InvoiceXml(body)));
        using var pdf = PdfReader.Open(Assert.Single(result.Documents).PdfPath, PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount > 2);
    }

    [Fact] public void DtdAndNonInvoicesAreRejected()
    {
        Assert.Throws<XmlException>(() => InvoiceParser.Read(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///etc/passwd'>]><x>&a;</x>")));
        Assert.Throws<InvalidDataException>(() => Open(Write("receipt.xml", "<postacert/>")));
        Assert.Throws<NotSupportedException>(() => Open(Write("simple.xml", "<FatturaElettronicaSemplificata versione='FSM10'/>")));
        Assert.Throws<CryptographicException>(() => Open(Write("bad.p7m", "Not a CMS")));
    }

    [Fact] public void LocalExamplesAllOpenWhenAvailable()
    {
        var examples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../esempi"));
        if (!Directory.Exists(examples)) return;
        var files = Directory.GetFiles(examples).Where(p => new[] { ".xml", ".p7m", ".eml" }.Contains(Path.GetExtension(p))).ToArray();
        foreach (var file in files)
        {
            var result = Open(file); Assert.NotEmpty(result.Documents); Assert.Empty(result.Warnings);
            foreach (var doc in result.Documents)
            { using var pdf = PdfReader.Open(doc.PdfPath, PdfDocumentOpenMode.Import); Assert.True(pdf.PageCount > 0); }
        }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
