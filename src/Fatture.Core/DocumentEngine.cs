using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace Fatture.Core;

public sealed class DocumentEngine
{
    public const int MaxInputBytes = 64 * 1024 * 1024;
    public OpenResult Open(string inputPath, string outputDirectory)
    {
        var file = new FileInfo(inputPath);
        if (!file.Exists) throw new FileNotFoundException("Il documento non esiste.", inputPath);
        if (file.Length > MaxInputBytes) throw new InvalidDataException("Il file supera il limite di 64 MB.");
        Directory.CreateDirectory(outputDirectory);
        var context = new Context(Path.GetFullPath(outputDirectory), file.Name);
        ReadPayload(File.ReadAllBytes(file.FullName), file.Name, context, [], 0, strict: true);
        return new(context.Documents.OrderBy(d => d.Kind == "invoice" ? 0 : d.Kind == "email" ? 1 : 2).ToArray(), context.Warnings);
    }

    private static void ReadPayload(byte[] bytes, string name, Context context,
        IReadOnlyList<SignatureInfo> inherited, int depth, bool strict = false)
    {
        context.Visit(depth);
        context.Charge(bytes.Length);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension == ".p7m")
        {
            var (content, signers) = Signatures.Unwrap(bytes);
            var inner = name[..^4];
            if (Path.GetExtension(inner).Length == 0)
                inner += LooksLikeXml(content) ? ".xml" : ".eml";
            ReadPayload(content, inner, context, inherited.Concat(signers).ToArray(), depth + 1, strict);
        }
        else if (extension == ".xml")
        {
            var doc = InvoiceParser.Read(bytes);
            var invoices = InvoiceParser.Parse(doc);
            if (invoices.Count == 0)
            {
                if (strict) throw new InvalidDataException("Questo XML non è una fattura elettronica FPA12/FPR12.");
                return; // PEC receipts and SdI metadata are not invoices.
            }
            if (!context.Seen.Add("xml:" + Convert.ToHexString(SHA256.HashData(bytes)))) return;
            var signatures = inherited.Concat(Signatures.InspectXml(bytes)).ToArray();
            foreach (var invoice in invoices)
            {
                var path = context.NextPdf();
                PdfRenderer.Invoice(invoice, path);
                var metadata = new List<Metadata>
                {
                    new("Formato", invoice.Version), new("Tipo documento", invoice.Type), new("Numero", invoice.Number),
                    new("Data", PdfRenderer.Date(invoice.Date)), new("Fornitore", invoice.Seller), new("Cliente", invoice.Buyer),
                    new("Totale", invoice.Total.Length > 0 ? $"{PdfRenderer.Number(invoice.Total)} {invoice.Currency}" : "Non indicato"),
                    new("Corpo nel file", invoice.Index.ToString()),
                    new("Originale", "Il PDF è una copia di cortesia del documento XML.")
                };
                context.Add("invoice", invoice.Title, invoice.Seller, name, path, invoice.Title + ".pdf", metadata, signatures,
                    AccountingMapper.FromInvoice(invoice, name == context.SourceName ? name : $"{context.SourceName} › {name}"));
                foreach (var attachment in invoice.Body.Children("Allegati"))
                {
                    var attachmentName = attachment.ValueOf("NomeAttachment");
                    if (attachment.ValueOf("AlgoritmoCompressione").Length > 0)
                    { context.Warnings.Add($"{attachmentName}: allegato compresso non aperto."); continue; }
                    if (!Supported(attachmentName)) continue;
                    context.TryAttachment(attachmentName, () => ReadPayload(Convert.FromBase64String(attachment.ValueOf("Attachment")),
                        attachmentName, context, [], depth + 1));
                }
            }
        }
        else if (extension == ".eml")
        {
            using var stream = new MemoryStream(bytes, false);
            using var message = MimeMessage.Load(stream);
            ReadMessage(message, name, context, depth + 1, inherited);
        }
        else if (extension == ".pdf")
        {
            if (bytes.AsSpan(0, Math.Min(1024, bytes.Length)).IndexOf("%PDF-"u8) < 0)
                throw new InvalidDataException("L’allegato non contiene un PDF riconoscibile.");
            if (!context.Seen.Add("pdf:" + Convert.ToHexString(SHA256.HashData(bytes)))) return;
            var path = context.NextPdf(); File.WriteAllBytes(path, bytes);
            context.Add("pdf", Path.GetFileName(name), "Allegato PDF", name, path, Path.GetFileName(name),
                [new("Tipo", "PDF originale allegato"), new("Dimensione", $"{bytes.Length / 1024.0:N1} KB")], inherited);
        }
        else if (strict) throw new NotSupportedException("Apri un file XML, P7M, EML oppure PDF.");
    }

    private static void ReadMessage(MimeMessage message, string name, Context context, int depth, IReadOnlyList<SignatureInfo> signatures)
    {
        context.Visit(depth);
        var subject = string.IsNullOrWhiteSpace(message.Subject) ? "Email senza oggetto" : message.Subject;
        var headers = new List<Metadata>
        {
            new("Da", message.From.ToString()), new("A", message.To.ToString()),
            new("Data", message.Date == default ? "Non indicata" : message.Date.ToString("dd/MM/yyyy HH:mm zzz"))
        };
        if (message.Cc.Count > 0) headers.Add(new("Cc", message.Cc.ToString()));
        var body = message.TextBody;
        if (string.IsNullOrWhiteSpace(body) && message.HtmlBody is { } html)
            body = EmailHtml.ToText(html);
        body ??= "Il messaggio non contiene un corpo testuale.";
        var mailKey = "mail:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            subject + "\n" + string.Join("\n", headers.Select(h => h.Value)) + "\n" + body)));
        if (!context.Seen.Add(mailKey)) return;
        var attachmentNames = new List<string>();
        var firstDocument = context.Documents.Count;
        if (message.Body != null)
            Walk(message.Body, name, context, depth + 1, attachmentNames);
        // SdI's delivery text and its PEC/forward wrappers add no useful preview
        // once their invoice has been extracted. Keep receipts and normal email,
        // and retain the message if extraction failed or no invoice was found.
        if (IsSdiDelivery(subject) && context.Documents.Skip(firstDocument).Any(d => d.Kind == "invoice")) return;
        var path = context.NextPdf();
        PdfRenderer.Email(subject, headers, body, attachmentNames, path);
        var metadata = headers.Concat(new[] { new Metadata("Oggetto", subject), new Metadata("Allegati", string.Join("\n", attachmentNames)) }).ToArray();
        context.Add("email", subject, message.From.ToString(), name, path, subject + ".pdf", metadata, signatures);
    }

    private static bool IsSdiDelivery(string subject)
    {
        var normalized = subject.Trim();
        while (true)
        {
            const string forwarded = "Messaggio PEC inoltrato [";
            const string certified = "POSTA CERTIFICATA:";
            if (normalized.StartsWith(forwarded, StringComparison.OrdinalIgnoreCase) && normalized.EndsWith(']'))
                normalized = normalized[forwarded.Length..^1].Trim();
            else if (normalized.StartsWith(certified, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[certified.Length..].Trim();
            else break;
        }
        return Regex.IsMatch(normalized, @"^Invio File\s+\d+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static void Walk(MimeEntity entity, string name, Context context, int depth, List<string> attachmentNames)
    {
        context.Visit(depth);
        if (entity is Multipart multipart)
        {
            foreach (var child in multipart) Walk(child, name, context, depth + 1, attachmentNames);
        }
        else if (entity is MessagePart nested && nested.Message != null)
        {
            var nestedName = nested.ContentDisposition?.FileName ?? nested.ContentType.Name ?? "Messaggio allegato.eml";
            attachmentNames.Add(nestedName);
            context.TryAttachment(nestedName, () => ReadMessage(nested.Message, name + " › " + nestedName, context, depth + 1, []));
        }
        else if (entity is MimePart part && part.Content != null)
        {
            var fileName = part.FileName;
            if (fileName == null && part.ContentType.MimeType == "application/pdf") fileName = "Allegato.pdf";
            if (fileName == null && part.ContentType.MimeType is "application/xml" or "text/xml") fileName = "Allegato.xml";
            if (fileName == null) return;
            attachmentNames.Add(fileName);
            if (!Supported(fileName)) return;
            context.TryAttachment(fileName, () =>
            {
                using var stream = new LimitedMemoryStream();
                part.Content.DecodeTo(stream);
                ReadPayload(stream.ToArray(), fileName, context, [], depth + 1);
            });
        }
    }

    private static bool Supported(string name) => Path.GetExtension(name).ToLowerInvariant() is ".xml" or ".p7m" or ".eml" or ".pdf";
    private static bool LooksLikeXml(byte[] bytes) => Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith('<');
    private sealed class Context(string directory, string sourceName)
    {
        public string SourceName { get; } = sourceName;
        private long decodedBytes;
        private int nodes;
        public List<RenderedDocument> Documents { get; } = [];
        public List<string> Warnings { get; } = [];
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
        public void Visit(int depth)
        {
            if (depth > 30 || ++nodes > 4000) throw new InvalidDataException("Documento troppo annidato o con troppi allegati.");
        }
        public void Charge(int count)
        {
            if (count > MaxInputBytes || (decodedBytes += count) > 128L * 1024 * 1024)
                throw new InvalidDataException("Gli allegati superano il limite di lettura di 128 MB.");
        }
        public string NextPdf()
        {
            if (Documents.Count >= 256) throw new InvalidDataException("Raggiunto il limite di 256 documenti per file.");
            return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pdf");
        }
        public void Add(string kind, string title, string subtitle, string name, string path, string suggested,
            IReadOnlyList<Metadata> metadata, IReadOnlyList<SignatureInfo> signatures, AccountingData? invoice = null)
        {
            var safe = string.Concat(suggested.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c));
            safe = safe.Length > 180 ? safe[..176] + ".pdf" : safe;
            Documents.Add(new(Guid.NewGuid().ToString("N"), kind, title, subtitle, name, path, safe, metadata, signatures, invoice));
        }
        public void TryAttachment(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            { Warnings.Add($"{Path.GetFileName(name)}: {ex.Message}"); }
        }
    }
    private sealed class LimitedMemoryStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count); base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count)
        { if (Position + count > MaxInputBytes) throw new InvalidDataException("Allegato superiore a 64 MB."); }
    }
}
