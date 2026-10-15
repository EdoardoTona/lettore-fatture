using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Diagnostics.CodeAnalysis;

namespace Fatture.Core;

public static class Signatures
{
    public static (byte[] Content, IReadOnlyList<SignatureInfo> Signers) Unwrap(byte[] bytes)
    {
        // Some senders store a base64/PEM CMS instead of DER.
        if (bytes.Length > 0 && (bytes[0] == '-' || bytes[0] == 'M'))
        {
            try
            {
                var text = System.Text.Encoding.ASCII.GetString(bytes);
                text = string.Join("\n", text.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal)));
                bytes = Convert.FromBase64String(text);
            }
            catch (FormatException) { /* Try as DER below. */ }
        }
        var cms = new SignedCms();
        cms.Decode(bytes);
        if (cms.Detached || cms.ContentInfo.Content.Length == 0)
            throw new InvalidDataException("La firma è separata: il P7M non contiene un documento.");
        if (cms.SignerInfos.Count == 0) throw new InvalidDataException("Il P7M non contiene firmatari.");
        var infos = new List<SignatureInfo>();
        foreach (SignerInfo signer in cms.SignerInfos)
        {
            var integrity = "Integrità verificata";
            try { signer.CheckSignature(true); }
            catch (CryptographicException) { integrity = "Firma non valida o non verificabile"; }
            string? signedAt = null;
            foreach (CryptographicAttributeObject attr in signer.SignedAttributes)
                if (attr.Oid.Value == "1.2.840.113549.1.9.5" && attr.Values.Count > 0)
                    try { signedAt = new Pkcs9SigningTime(attr.Values[0].RawData).SigningTime.ToUniversalTime().ToString("O"); }
                    catch (CryptographicException) { }
            infos.Add(Info(signer.Certificate, signedAt, integrity, "CAdES / CMS"));
        }
        return (cms.ContentInfo.Content, infos);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The CLI roots System.Security.Cryptography and System.Security.Cryptography.Xml for SignedXml algorithm lookup. Other hosts must preserve them when trimming.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "XSLT transforms are explicitly rejected before constructing SignedXml; supported signature transforms do not compile XSLT.")]
    public static IReadOnlyList<SignatureInfo> InspectXml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = DocumentEngine.MaxInputBytes });
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        doc.Load(reader);
        var nodes = doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        var result = new List<SignatureInfo>();
        foreach (XmlElement element in nodes)
        {
            X509Certificate2? cert = null;
            var time = element.GetElementsByTagName("SigningTime", "http://uri.etsi.org/01903/v1.3.2#").OfType<XmlElement>().FirstOrDefault()?.InnerText;
            try
            {
                var certNode = element.GetElementsByTagName("X509Certificate", SignedXml.XmlDsigNamespaceUrl).OfType<XmlElement>().FirstOrDefault();
                if (certNode != null) cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certNode.InnerText));
                // Keep certificate metadata available even if .NET cannot verify the algorithm.
                var methods = element.GetElementsByTagName("CanonicalizationMethod", SignedXml.XmlDsigNamespaceUrl).OfType<XmlElement>()
                    .Concat(element.GetElementsByTagName("Transform", SignedXml.XmlDsigNamespaceUrl).OfType<XmlElement>()).ToArray();
                if (methods.Any(m => m.GetAttribute("Algorithm") == "http://www.w3.org/TR/1999/REC-xslt-19991116"))
                {
                    result.Add(Info(cert, time, "Verifica non supportata (XSLT)", "XAdES / XMLDSig"));
                    continue;
                }
                if (methods.Any(m => m.GetAttribute("Algorithm").StartsWith("http://www.w3.org/2006/12/xml-c14n11", StringComparison.Ordinal)))
                {
                    result.Add(Info(cert, time, "Verifica non supportata (C14N 1.1)", "XAdES / XMLDSig"));
                    continue;
                }
                var signature = new SignedXml(doc) { Resolver = null! };
                signature.LoadXml(element);
                // Require an enveloped signature over the whole invoice and forbid external references.
                var references = signature.SignedInfo!.References.Cast<Reference>().ToArray();
                bool safe = references.Any(r => r.Uri == "") && references.All(r => string.IsNullOrEmpty(r.Uri) || r.Uri.StartsWith('#'));
                bool valid = safe && cert != null && signature.CheckSignature(cert, true);
                result.Add(Info(cert, time, valid ? "Integrità verificata" : "Firma non valida o non verificabile", "XAdES / XMLDSig"));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            { result.Add(Info(cert, time, "Firma non verificabile", "XAdES / XMLDSig")); }
            finally { cert?.Dispose(); }
        }
        return result;
    }

    private static SignatureInfo Info(X509Certificate2? cert, string? time, string integrity, string format) =>
        new(cert?.GetNameInfo(X509NameType.SimpleName, false) ?? "Certificato non disponibile",
            cert?.Issuer ?? "", time, cert?.NotBefore.ToUniversalTime().ToString("O") ?? "",
            cert?.NotAfter.ToUniversalTime().ToString("O") ?? "", integrity, format);
}
