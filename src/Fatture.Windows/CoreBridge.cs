using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fatture.Cli;
using Fatture.Core;

namespace Fatture.Windows;

internal sealed class CoreBridge : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim queue = new(1);
    private readonly object lifecycle = new();
    private readonly CancellationTokenSource stopping = new();
    private Process? worker;
    private bool stopped;

    public int? WorkerId { get { lock (lifecycle) return worker?.Id; } }

    public async Task<OpenResult> OpenAsync(string path, string outputDirectory)
    {
        var response = await SendAsync(new(Guid.NewGuid().ToString("N"), "open", path, outputDirectory));
        return response.Result ?? throw new InvalidDataException("Il motore non ha restituito i documenti.");
    }

    public async Task<ExcelExportResult> ExportAsync(RenderedDocument[] documents, string path)
    {
        var response = await SendAsync(new(Guid.NewGuid().ToString("N"), "exportExcel", Documents: documents, ExportPath: path));
        return response.Export ?? throw new InvalidDataException("Il motore non ha restituito l’export.");
    }

    private async Task<BridgeResponse> SendAsync(BridgeRequest request)
    {
        await queue.WaitAsync(stopping.Token).ConfigureAwait(false);
        try
        {
            var process = Start();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), stopping.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(stopping.Token).ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync(stopping.Token).ConfigureAwait(false)
                ?? throw new IOException("Il motore di lettura si è interrotto. Riprova ad aprire il documento.");
            var response = JsonSerializer.Deserialize<BridgeResponse>(line, Json)
                ?? throw new InvalidDataException("Risposta vuota dal motore.");
            if (response.Id != request.Id) throw new InvalidDataException("Risposta inattesa dal motore.");
            if (!response.Ok) throw new InvalidDataException(response.Error ?? "Impossibile leggere il documento.");
            return response;
        }
        finally { queue.Release(); }
    }

    private Process Start()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (worker is { HasExited: false }) return worker;
            worker?.Dispose();
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("--core-bridge");
            start.Environment["DOTNET_EnableDiagnostics"] = "0";
            worker = Process.Start(start) ?? throw new IOException("Impossibile avviare il motore di lettura.");
            return worker;
        }
    }

    public void Dispose()
    {
        lock (lifecycle)
        {
            if (stopped) return;
            stopped = true;
            stopping.Cancel();
            if (worker == null) return;
            // Closing must also interrupt an import; do not wait for its request.
            try
            {
                if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                worker.WaitForExit();
            }
            catch (InvalidOperationException) { }
            // In-flight async readers still own the Process object. They complete
            // after cancellation, so don't dispose its streams underneath them.
        }
    }
}
