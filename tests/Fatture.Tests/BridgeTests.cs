using System.Text.Json;
using Fatture.Cli;
using Fatture.Core;
using Xunit;

namespace Fatture.Tests;

public sealed class BridgeTests
{
    [Fact]
    public void MalformedRequestsDoNotInterruptLaterRequests()
    {
        using var input = new StringReader("{broken\n{\"id\":\"bad\",\"operation\":\"unknown\"}\n{\"id\":\"ping\",\"operation\":\"ping\"}\n");
        using var output = new StringWriter();
        Assert.Equal(0, BridgeServer.Run(input, output));
        var responses = Read(output);
        Assert.Equal(3, responses.Length);
        Assert.False(responses[0].Ok); Assert.False(responses[1].Ok);
        Assert.Equal("bad", responses[1].Id);
        Assert.True(responses[2].Ok); Assert.Equal("ping", responses[2].Id);
    }

    [Fact]
    public void JsonRoundTripExportsInvoicesAndIgnoresEmail()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fatture-bridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "fatture.xlsx");
            var invoice = new RenderedDocument("1", "invoice", "Fattura 42", "Fornitore è", "test.xml", "unused.pdf", "test.pdf", [], [],
                new AccountingData("same", new Dictionary<string, string> { ["supplier"] = "Società di prova", ["total"] = "122.00" }));
            var request = new BridgeRequest("excel", "exportExcel", Documents: [invoice, invoice with { Id = "2" }, invoice with { Kind = "email" }], ExportPath: path);
            using var input = new StringReader(JsonSerializer.Serialize(request, Json) + "\n");
            using var output = new StringWriter();
            BridgeServer.Run(input, output);
            var response = Assert.Single(Read(output));
            Assert.True(response.Ok, response.Error);
            Assert.Equal(1, response.Export!.InvoiceCount);
            Assert.Equal(1, response.Export.DuplicateCount);
            Assert.True(File.Exists(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static BridgeResponse[] Read(StringWriter output) => output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonSerializer.Deserialize<BridgeResponse>(line, Json)!).ToArray();
}
