using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Fatture.Windows;

internal sealed partial class MainWindow
{
    private async Task SmokeTestAsync()
    {
        var report = Environment.GetEnvironmentVariable("FATTURE_SMOKE_RESULT")
            ?? Path.Combine(Path.GetTempPath(), "fatture-windows-check.json");
        try
        {
            using var timeout = new System.Windows.Forms.Timer { Interval = 30_000 };
            timeout.Tick += (_, _) => { ExitCode = 1; Close(); };
            timeout.Start();
            var input = Path.Combine(sessionDirectory, "prova.xml");
            var xml = Encoding.UTF8.GetBytes("""
                <FatturaElettronica versione="FPR12"><FatturaElettronicaHeader>
                  <CedentePrestatore><DatiAnagrafici><IdFiscaleIVA><IdPaese>IT</IdPaese><IdCodice>00123456789</IdCodice></IdFiscaleIVA><Anagrafica><Denominazione>Fornitore di prova</Denominazione></Anagrafica></DatiAnagrafici></CedentePrestatore>
                  <CessionarioCommittente><DatiAnagrafici><Anagrafica><Denominazione>Cliente di prova</Denominazione></Anagrafica></DatiAnagrafici></CessionarioCommittente>
                </FatturaElettronicaHeader><FatturaElettronicaBody>
                  <DatiGenerali><DatiGeneraliDocumento><TipoDocumento>TD01</TipoDocumento><Divisa>EUR</Divisa><Data>2026-10-15</Data><Numero>000042</Numero><Causale>Verifica Windows</Causale><ImportoTotaleDocumento>122.00</ImportoTotaleDocumento></DatiGeneraliDocumento></DatiGenerali>
                  <DatiBeniServizi><DettaglioLinee><NumeroLinea>1</NumeroLinea><Descrizione>Consulenza</Descrizione><Quantita>1.00</Quantita><PrezzoUnitario>100.00</PrezzoUnitario><PrezzoTotale>100.00</PrezzoTotale><AliquotaIVA>22.00</AliquotaIVA></DettaglioLinee>
                    <DatiRiepilogo><AliquotaIVA>22.00</AliquotaIVA><ImponibileImporto>100.00</ImponibileImporto><Imposta>22.00</Imposta></DatiRiepilogo></DatiBeniServizi>
                </FatturaElettronicaBody></FatturaElettronica>
                """);
            File.WriteAllBytes(input, xml);
            if (Environment.GetEnvironmentVariable("FATTURE_SMOKE_WINDOW_CLOSE") == "loading")
            {
                _ = OpenFilesAsync([input]);
                await Task.Delay(10);
                File.WriteAllText(report, JsonSerializer.Serialize(new { ok = true, mode = "loading", workerId = bridge.WorkerId, sessionDirectory }));
                Close(); return;
            }
            using var key = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=Verifica locale Windows", key, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
            var cms = new SignedCms(new ContentInfo(xml), detached: false);
            cms.ComputeSignature(new CmsSigner(certificate), silent: true);
            var signed = input + ".p7m"; File.WriteAllBytes(signed, cms.Encode());
            var email = Path.Combine(sessionDirectory, "prova.eml");
            File.WriteAllText(email, "From: test@example.invalid\r\nTo: client@example.invalid\r\nSubject: Email di prova\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nMessaggio senza fattura.");
            await OpenFilesAsync([input, signed, email]);
            Check(documents.Items.Count == 3, "Apertura XML/P7M/email");
            Check(details.Panel2Collapsed, "Inspector chiuso all’avvio");
            documents.SelectedIndex = 0; await previewTask;
            for (int i = 0; i < 100 && !pdf.HasRenderedPage; i++) await Task.Delay(50);
            Check(pdf.PageCount == 1 && pdf.HasRenderedPage, "Rendering PDF nativo");
            pdf.ZoomBy(1.15); pdf.Fit();
            info.PerformClick(); Check(!details.Panel2Collapsed, "Apertura inspector");
            info.PerformClick(); Check(details.Panel2Collapsed, "Chiusura inspector");
            var output = Path.ChangeExtension(report, ".xlsx");
            var export = await bridge.ExportAsync(Entries.Select(entry => entry.Document).ToArray(), output);
            Check(export.InvoiceCount == 1 && export.DuplicateCount == 1 && File.Exists(output), "Excel e deduplicazione");
            Check(Entries[1].Document.Signatures.Any(signature => signature.Integrity == "Integrità verificata"), "Firma P7M");
            documents.ClearSelected(); documents.SetSelected(0, true); documents.SetSelected(1, true);
            Check(documents.SelectedItems.Count == 2, "Selezione multipla");
            CloseSelected(); await previewTask;
            Check(documents.Items.Count == 1 && Entries[0].Document.Kind == "email", "Chiusura multipla");
            File.WriteAllText(report, JsonSerializer.Serialize(new { ok = true, mode = "ready", workerId = bridge.WorkerId,
                sessionDirectory, invoices = export.InvoiceCount, duplicates = export.DuplicateCount, nativePdf = true }));
            Close();
        }
        catch (Exception error)
        {
            ExitCode = 1;
            File.WriteAllText(report, JsonSerializer.Serialize(new { ok = false, error = error.ToString() }));
            Close();
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Verifica fallita: " + message);
    }
}
