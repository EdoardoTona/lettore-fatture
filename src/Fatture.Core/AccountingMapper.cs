using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Fatture.Core;

public static class AccountingMapper
{
    public static AccountingData FromInvoice(Invoice invoice, string sourceName)
    {
        var supplier = invoice.Header.Child("CedentePrestatore").Child("DatiAnagrafici");
        var customer = invoice.Header.Child("CessionarioCommittente").Child("DatiAnagrafici");
        var taxes = invoice.Body.Child("DatiBeniServizi").Children("DatiRiepilogo").ToArray();
        var payments = invoice.Body.Children("DatiPagamento").SelectMany(p => p.Children("DettaglioPagamento")).ToArray();
        var reasons = invoice.General.Children("Causale").Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToArray();
        var description = reasons.Length > 0 ? string.Join("\n", reasons) : Distinct(invoice.Body.Child("DatiBeniServizi")
            .Children("DettaglioLinee").Select(l => l.ValueOf("Descrizione")), "\n");
        var dueDates = payments.Select(p => p.ValueOf("DataScadenzaPagamento")).Where(s => s.Length > 0).Distinct().Order().ToArray();
        var general = invoice.General;
        var values = new Dictionary<string, string>
        {
            ["date"] = invoice.Date, ["number"] = invoice.Number, ["type"] = invoice.Type,
            ["supplierVatCountry"] = supplier.Child("IdFiscaleIVA").ValueOf("IdPaese"),
            ["supplierVat"] = supplier.Child("IdFiscaleIVA").ValueOf("IdCodice"),
            ["supplierTaxCode"] = supplier.ValueOf("CodiceFiscale"), ["supplier"] = invoice.Seller,
            ["description"] = description, ["currency"] = invoice.Currency,
            ["taxable"] = Sum(taxes.Select(t => t.ValueOf("ImponibileImporto"))),
            ["vat"] = Sum(taxes.Select(t => t.ValueOf("Imposta"))), ["total"] = invoice.Total,
            ["paymentTotal"] = Sum(payments.Select(p => p.ValueOf("ImportoPagamento"))),
            ["vatRates"] = Distinct(taxes.Select(t => t.ValueOf("AliquotaIVA"))),
            ["vatNatures"] = Distinct(taxes.Select(t => t.ValueOf("Natura"))),
            ["vatCollection"] = Distinct(taxes.Select(t => Collection(t.ValueOf("EsigibilitaIVA")))),
            ["vatBreakdown"] = string.Join("\n", taxes.Select(t => $"{t.ValueOf("AliquotaIVA")}%" +
                (t.ValueOf("Natura") is { Length: > 0 } nature ? $" ({nature})" : "") +
                $" · Imponibile: {t.ValueOf("ImponibileImporto")} · IVA: {t.ValueOf("Imposta")}" +
                (t.ValueOf("EsigibilitaIVA") is { Length: > 0 } collection ? $" · {Collection(collection)}" : ""))),
            ["withholding"] = Sum(general.Children("DatiRitenuta").Select(r => r.ValueOf("ImportoRitenuta"))),
            ["stampDuty"] = general.Child("DatiBollo").ValueOf("ImportoBollo"),
            ["pension"] = Sum(general.Children("DatiCassaPrevidenziale").Select(c => c.ValueOf("ImportoContributoCassa"))),
            ["firstDueDate"] = dueDates.FirstOrDefault() ?? "", ["dueDates"] = string.Join("; ", dueDates),
            ["paymentMethods"] = Distinct(payments.Select(p => p.ValueOf("ModalitaPagamento"))),
            ["iban"] = Distinct(payments.Select(p => p.ValueOf("IBAN"))),
            ["customer"] = invoice.Buyer, ["customerVatCountry"] = customer.Child("IdFiscaleIVA").ValueOf("IdPaese"),
            ["customerVat"] = customer.Child("IdFiscaleIVA").ValueOf("IdCodice"),
            ["customerTaxCode"] = customer.ValueOf("CodiceFiscale"), ["source"] = sourceName
        };
        // Only identical business content is deduplicated, across XML/P7M/EML.
        // Different versions with the same invoice number remain distinct.
        var normalized = Normalize(invoice.Header).ToString(SaveOptions.DisableFormatting) + Normalize(invoice.Body).ToString(SaveOptions.DisableFormatting);
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))), values);
    }

    private static XElement Normalize(XElement element) => new(element.Name.LocalName,
        element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => new XAttribute(a.Name.LocalName, a.Value)),
        element.HasElements ? element.Elements().Select(Normalize) : element.Value.Trim());
    private static string Distinct(IEnumerable<string> values, string separator = "; ") => string.Join(separator, values.Where(s => s.Length > 0).Distinct());
    private static string Collection(string value) => value switch { "I" => "Immediata", "D" => "Differita", "S" => "Split payment", _ => value };
    internal static bool TryNumber(string value, out decimal number) => decimal.TryParse(value,
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    private static string Sum(IEnumerable<string> values)
    {
        var parts = values.ToArray();
        if (parts.Length == 0 || parts.Any(v => v.Length == 0)) return "";
        decimal sum = 0;
        foreach (var part in parts)
        {
            if (!TryNumber(part, out var number)) return string.Join(" + ", parts);
            try { sum += number; } catch (OverflowException) { return string.Join(" + ", parts); }
        }
        return sum.ToString(CultureInfo.InvariantCulture);
    }
}
