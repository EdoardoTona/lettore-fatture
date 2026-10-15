using System.Text.Json;
using Fatture.Core;

namespace Fatture.Cli;

// Shared by the console CLI and the Windows app's headless worker mode.
public static class BridgeServer
{
    public static int Run() => Run(Console.In, Console.Out);

    public static int Run(TextReader input, TextWriter output)
    {
        void Write(BridgeResponse response) => output.WriteLine(JsonSerializer.Serialize(response, JsonContext.Default.BridgeResponse));
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            BridgeRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize(line, JsonContext.Default.BridgeRequest)
                    ?? throw new InvalidDataException("Richiesta vuota.");
                if (request.Operation == "ping") Write(new(request.Id, true, null, null));
                else if (request.Operation == "open" && request.Path != null && request.OutputDirectory != null)
                    Write(new(request.Id, true, new DocumentEngine().Open(request.Path, request.OutputDirectory), null));
                else if (request.Operation == "exportExcel" && request.Documents != null && request.ExportPath != null)
                    Write(new(request.Id, true, null, null, ExcelExporter.ExportDocuments(request.Documents, request.ExportPath)));
                else throw new InvalidDataException("Operazione non supportata.");
            }
            catch (Exception ex) { Write(new(request?.Id ?? "", false, null, ex.Message)); }
        }
        return 0;
    }
}

public sealed record BridgeRequest(string Id, string Operation, string? Path = null, string? OutputDirectory = null,
    RenderedDocument[]? Documents = null, string? ExportPath = null);
public sealed record BridgeResponse(string Id, bool Ok, OpenResult? Result, string? Error, ExcelExportResult? Export = null);
