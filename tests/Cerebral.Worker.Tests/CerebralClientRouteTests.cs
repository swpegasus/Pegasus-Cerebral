using Cerebral.Client;
using Cerebral.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cerebral.Worker.Tests;

public static class CerebralClientRouteTests
{
    public static async Task RunAsync()
    {
        var assertions = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            assertions++; Console.WriteLine("PASS: " + name);
        }

        const string ownerKey = "fixture-owner-route-key";
        const string peerToken = "fixture-peer-route-token";
        var ownerRequests = 0;
        var peerRequests = 0;
        var ownerCredentialWasScoped = false;
        var peerCredentialWasScoped = false;
        const int oversizedOwnerResponseBytes = (8 * 1024 * 1024) + (64 * 1024);
        var responseChunk = Enumerable.Repeat((byte)' ', 64 * 1024).ToArray();

        static async Task<bool> RejectsOversizedResponseAsync(Func<CancellationToken, Task> send)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await send(timeout.Token); return false; }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested) { return true; }
        }

        async Task WriteChunkedOverflowAsync(HttpContext context)
        {
            context.Response.ContentType = "application/json";
            try
            {
                var remaining = oversizedOwnerResponseBytes;
                while (remaining > 0)
                {
                    var count = Math.Min(responseChunk.Length, remaining);
                    await context.Response.Body.WriteAsync(responseChunk.AsMemory(0, count), context.RequestAborted);
                    remaining -= count;
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                // The bounded owner client deliberately closes this oversized fixture response.
            }
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/api/command", async (HttpContext context) =>
        {
            ownerRequests++;
            ownerCredentialWasScoped = context.Request.Headers["X-Cerebral-Key"].FirstOrDefault() == ownerKey &&
                !context.Request.Headers.ContainsKey("X-Cerebral-Peer");
            var command = await context.Request.ReadFromJsonAsync<CommandRequest>(CerebralJson.Options,
                context.RequestAborted) ?? throw new InvalidDataException("Fixture command is missing.");
            if (command.Operation == "route.oversized-content-length")
            {
                context.Response.ContentType = "application/json";
                context.Response.ContentLength = oversizedOwnerResponseBytes;
                await context.Response.StartAsync(context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
                catch (OperationCanceledException) { /* Expected after the owner client rejects the header. */ }
                return;
            }
            if (command.Operation == "route.oversized-chunked")
            {
                await WriteChunkedOverflowAsync(context);
                return;
            }
            await Results.Json(CommandResult.Ok("owner"), CerebralJson.Options).ExecuteAsync(context);
        });
        app.MapPost("/api/peer/command", (HttpContext context) =>
        {
            peerRequests++;
            peerCredentialWasScoped = context.Request.Headers["X-Cerebral-Peer"].FirstOrDefault() == peerToken &&
                !context.Request.Headers.ContainsKey("X-Cerebral-Key");
            return Results.Json(CommandResult.Ok("peer"), CerebralJson.Options);
        });

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var dualCredential = new WorkerRegistration
            {
                Id = "loop-dual", Name = "Loopback dual credential", Url = address,
                AccessKey = ownerKey, PeerToken = peerToken
            };

            using var owner = CerebralClient.ForOwner(dualCredential);
            using var peer = CerebralClient.ForPeer(dualCredential);
            Check(await owner.GetAsync<string>("route.probe") == "owner" && ownerRequests == 1 && peerRequests == 0 &&
                ownerCredentialWasScoped,
                "ForOwner forces the owner command route and sends only the owner credential");
            Check(await peer.GetAsync<string>("route.probe") == "peer" && ownerRequests == 1 && peerRequests == 1 &&
                peerCredentialWasScoped,
                "ForPeer forces the peer command route and sends only the peer credential");

            using var compatible = new CerebralClient(dualCredential);
            Check(await compatible.GetAsync<string>("route.probe") == "peer" && peerRequests == 2,
                "the compatibility constructor continues to prefer the peer route when both credentials exist");

            static bool RejectsMissingCredential(Func<CerebralClient> create, string expected)
            {
                try { using var unused = create(); return false; }
                catch (InvalidOperationException error) { return error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase); }
            }

            var requestsBeforeRejection = ownerRequests + peerRequests;
            Check(RejectsMissingCredential(() => CerebralClient.ForOwner(new WorkerRegistration
                    { Id = "peer-only", Url = address, PeerToken = peerToken }), "owner access key") &&
                RejectsMissingCredential(() => CerebralClient.ForPeer(new WorkerRegistration
                    { Id = "owner-only", Url = address, AccessKey = ownerKey }), "peer token") &&
                ownerRequests + peerRequests == requestsBeforeRejection,
                "explicit clients reject a missing route credential before sending a request");

            Check(await RejectsOversizedResponseAsync(token => owner.CommandAsync(
                    "route.oversized-content-length", cancellationToken: token)),
                "ForOwner rejects an oversized declared response before JSON deserialization");
            Check(await RejectsOversizedResponseAsync(token => owner.CommandAsync(
                    "route.oversized-chunked", cancellationToken: token)),
                "ForOwner bounds a chunked response before JSON deserialization");

            Console.WriteLine($"RESULT: {assertions} CerebralClient route assertions passed using a deterministic loopback HTTP fixture.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
