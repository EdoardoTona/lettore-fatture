using System.Text.Json;
using System.Text.Json.Serialization;
using Fatture.Core;

namespace Fatture.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["bridge"])
        {
            string? line;
            while ((line = Console.ReadLine()) != null)
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
        if (args is ["open", var path, "--output", var output])
        {
            try
            {
                Console.WriteLine(JsonSerializer.Serialize(new DocumentEngine().Open(path, output), JsonContext.Default.OpenResult));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        Console.Error.WriteLine("Uso: fatture open documento.xml|p7m|eml|pdf --output cartella\n     fatture bridge");
        return 2;
    }
    private static void Write(BridgeResponse response) => Console.WriteLine(JsonSerializer.Serialize(response, JsonContext.Default.BridgeResponse));
}
internal sealed record BridgeRequest(string Id, string Operation, string? Path, string? OutputDirectory,
    RenderedDocument[]? Documents = null, string? ExportPath = null);
internal sealed record BridgeResponse(string Id, bool Ok, OpenResult? Result, string? Error, ExcelExportResult? Export = null);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(BridgeResponse))]
[JsonSerializable(typeof(OpenResult))]
internal partial class JsonContext : JsonSerializerContext;
