using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fatture.Core;

namespace Fatture.Preview;

public static class NativePreview
{
    private static readonly Lock RenderLock = new();

    [UnmanagedCallersOnly(EntryPoint = "fatture_preview")]
    public static int Render(nint inputPath, nint outputDirectory)
    {
        // Never let a managed exception cross the native ABI boundary.
        try
        {
            var input = Marshal.PtrToStringUTF8(inputPath) ?? throw new ArgumentException("Percorso mancante.");
            var output = Marshal.PtrToStringUTF8(outputDirectory) ?? throw new ArgumentException("Destinazione mancante.");
            lock (RenderLock)
            {
                var result = new DocumentEngine().Open(input, output);
                File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(result, PreviewJson.Default.OpenResult));
            }
            return 0;
        }
        catch (Exception error)
        {
            try
            {
                var output = Marshal.PtrToStringUTF8(outputDirectory);
                if (output != null)
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "error.txt"), error.Message);
                }
            }
            catch { /* Error reporting must never cross the ABI either. */ }
            return 1;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenResult))]
internal partial class PreviewJson : JsonSerializerContext;
