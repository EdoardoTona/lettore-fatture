using System.Text.Json;
using System.Text.Json.Serialization;
using Fatture.Core;

namespace Fatture.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["bridge"]) return BridgeServer.Run();
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
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(BridgeResponse))]
[JsonSerializable(typeof(OpenResult))]
internal partial class JsonContext : JsonSerializerContext;
