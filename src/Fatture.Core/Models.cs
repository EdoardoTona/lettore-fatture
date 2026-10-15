using System.Xml.Linq;

namespace Fatture.Core;

public sealed record Metadata(string Label, string Value);
public sealed record SignatureInfo(string Subject, string Issuer, string? SignedAt,
    string ValidFrom, string ValidUntil, string Integrity, string Format);
public sealed record RenderedDocument(string Id, string Kind, string Title, string Subtitle,
    string SourceName, string PdfPath, string SuggestedFileName,
    IReadOnlyList<Metadata> Metadata, IReadOnlyList<SignatureInfo> Signatures, AccountingData? Invoice = null);
public sealed record OpenResult(IReadOnlyList<RenderedDocument> Documents, IReadOnlyList<string> Warnings);
public sealed record AccountingData(string Fingerprint, IReadOnlyDictionary<string, string> Values);
public sealed record ExcelExportResult(int InvoiceCount, int DuplicateCount, string Path);

public sealed record Invoice(XElement Header, XElement Body, string Version, int Index)
{
    public XElement General => Body.Child("DatiGenerali").Child("DatiGeneraliDocumento");
    public string Number => General.ValueOf("Numero");
    public string Date => General.ValueOf("Data");
    public string Currency => General.ValueOf("Divisa");
    public string Type => General.ValueOf("TipoDocumento");
    public string Total => General.ValueOf("ImportoTotaleDocumento");
    public string Seller => PartyName(Header.Child("CedentePrestatore"));
    public string Buyer => PartyName(Header.Child("CessionarioCommittente"));
    public string Title => $"{DocumentType(Type)} {Number}".Trim();
    public static string PartyName(XElement party)
    {
        var a = party.Child("DatiAnagrafici").Child("Anagrafica");
        return a.ValueOf("Denominazione") is { Length: > 0 } name
            ? name : $"{a.ValueOf("Nome")} {a.ValueOf("Cognome")}".Trim();
    }
    public static string DocumentType(string type) => type switch
    {
        "TD01" => "Fattura", "TD02" => "Acconto / anticipo", "TD03" => "Acconto su parcella",
        "TD04" => "Nota di credito", "TD05" => "Nota di debito", "TD06" => "Parcella",
        "TD16" => "Integrazione reverse charge interno", "TD17" => "Integrazione servizi estero",
        "TD18" => "Integrazione acquisti intracomunitari", "TD19" => "Integrazione beni estero",
        "TD20" => "Autofattura regolarizzazione", "TD21" => "Autofattura splafonamento",
        "TD22" => "Estrazione deposito IVA", "TD23" => "Estrazione deposito IVA con versamento",
        "TD24" => "Fattura differita", "TD25" => "Fattura differita art. 21, c. 4, lett. b",
        "TD26" => "Cessione beni ammortizzabili", "TD27" => "Autoconsumo / cessioni gratuite",
        "TD28" => "Acquisto da San Marino", _ => $"Documento {type}"
    };
}

internal static class XmlValues
{
    internal static XElement Child(this XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name) ?? new XElement(name);
    internal static IEnumerable<XElement> Children(this XElement element, string name) =>
        element.Elements().Where(e => e.Name.LocalName == name);
    internal static string ValueOf(this XElement element, string name) => element.Child(name).Value.Trim();
}
