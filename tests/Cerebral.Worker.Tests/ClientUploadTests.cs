using System.Net;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cerebral.Worker.Tests;

public static class ClientUploadTests
{
    public static async Task RunAsync()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        var directory = Path.Combine(Path.GetTempPath(), "cerebral-client-upload-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "user-owned-model.bin");
        var bytes = Enumerable.Range(0, 100_000).Select(x => (byte)(x % 256)).ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = directory });
        builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        byte[]? received = null; string? key = null; string? contentType = null; JsonElement metadata = default; var redirectFollowed = false;
        app.MapPost("/api/models/upload", async (HttpRequest request) =>
        {
            using var buffer = new MemoryStream(); await request.Body.CopyToAsync(buffer); received = buffer.ToArray();
            key = request.Headers["X-Cerebral-Key"].ToString(); contentType = request.ContentType;
            metadata = JsonSerializer.Deserialize<JsonElement>(request.Query["metadata"].ToString());
            return CommandResult.Ok(new ModelRecord { Id = metadata.GetProperty("id").GetString()!, Name = metadata.GetProperty("name").GetString()!, Path = "/service/models/fixture.gguf", SizeBytes = received.Length });
        });
        app.MapPost("/redirect/api/models/upload", (HttpContext context) =>
        {
            context.Response.StatusCode = 307; context.Response.Headers.Location = "/capture"; return Task.CompletedTask;
        });
        app.MapPost("/capture", () => { redirectFollowed = true; return Results.Ok(); });
        try
        {
            await app.StartAsync(); var url = app.Urls.Single();
            using var client = new CerebralClient(url, "fixture-installation-key");
            var result = await client.UploadModelAsync(source, "Model & fixture / α", "https://example.invalid/terms?a=1&b=2");
            Check(received is not null && received.SequenceEqual(bytes), "client model upload streams exact binary bytes from caller-owned file (HTTP fixture)");
            Check(key == "fixture-installation-key" && contentType == "application/octet-stream", "model upload uses installation header and binary content type");
            Check(metadata.GetProperty("name").GetString() == "Model & fixture / α" && metadata.GetProperty("license").GetString() == "https://example.invalid/terms?a=1&b=2", "upload metadata survives URL encoding");
            Check(!metadata.TryGetProperty("path", out _) && result.Path == "/service/models/fixture.gguf" && result.SizeBytes == bytes.Length,
                "upload sends bytes without requiring source-path access on service");
            using var redirected = new CerebralClient(url + "/redirect", "fixture-installation-key"); var denied = false;
            try { await redirected.UploadModelAsync(source, "redirect fixture"); }
            catch (IOException ex) { denied = ex.Message.Contains("Refresh the model list", StringComparison.Ordinal); }
            Check(denied && !redirectFollowed, "upload redirect does not forward key or model and reports unconfirmed outcome");
        }
        finally
        {
            await app.StopAsync();
            File.Delete(source); Directory.Delete(directory);
        }
    }
}
