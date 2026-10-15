using System.Xml;
using System.Xml.Linq;

namespace Fatture.Core;

public static class InvoiceParser
{
    public static XDocument Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = DocumentEngine.MaxInputBytes, MaxCharactersFromEntities = 0
        });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (doc.Descendants().Any(e => e.Ancestors().Take(65).Count() > 64))
            throw new InvalidDataException("XML con troppi livelli di annidamento.");
        return doc;
    }

    public static IReadOnlyList<Invoice> Parse(XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("XML vuoto.");
        if (root.Name.LocalName == "FatturaElettronicaSemplificata")
            throw new NotSupportedException("Le fatture semplificate FSM10 non sono ancora supportate.");
        if (root.Name.LocalName != "FatturaElettronica") return [];
        var version = root.Attribute("versione")?.Value ?? "";
        if (version is not ("FPR12" or "FPA12"))
            throw new NotSupportedException($"Formato fattura non supportato: {version}.");
        var header = root.Child("FatturaElettronicaHeader");
        var bodies = root.Children("FatturaElettronicaBody").ToArray();
        if (!header.HasElements || bodies.Length == 0)
            throw new InvalidDataException("Fattura incompleta: mancano intestazione o corpo.");
        return bodies.Select((body, i) => new Invoice(header, body, version, i + 1)).ToArray();
    }
}
