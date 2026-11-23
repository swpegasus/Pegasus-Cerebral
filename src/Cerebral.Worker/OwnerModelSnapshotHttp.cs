using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

internal static class OwnerModelSnapshotHttp
{
    internal static void UseOwnerModelSnapshotRoute(IApplicationBuilder app, CommandDispatcher commands) =>
        app.Use(async (context, next) =>
        {
            var route = OwnerModelSnapshotService.ClassifyHttpPath(context.Request.Path.Value);
            if (route == OwnerSnapshotHttpRoute.None) { await next(context); return; }
            // All recognized snapshot paths share the output bound, including pure refusals.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(OwnerModelSnapshotService.Deadline);
            if (route == OwnerSnapshotHttpRoute.Rejected)
            {
                context.Response.StatusCode = 400;
                await WriteResultAsync(context, CommandResult.Fail("snapshot_route_noncanonical"), deadline.Token);
                return;
            }
            if (context.Request.Method != "POST") { context.Response.StatusCode = 405; return; }
            using var inspection = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            // Reserve a bounded final interval for a classified timeout reply; the outer
            // deadline also cancels a successful response stalled by client backpressure.
            inspection.CancelAfter(OwnerModelSnapshotService.Deadline - TimeSpan.FromMilliseconds(250));
            CommandResult result;
            try
            {
                if (context.Request.ContentLength is > OwnerModelSnapshotService.MaximumRequestBytes)
                    throw new OwnerSnapshotUnavailableException("request_byte_bound");
                using var body = new MemoryStream();
                var buffer = new byte[OwnerModelSnapshotService.MaximumRequestBytes + 1];
                while (true)
                {
                    var remaining = buffer.Length - (int)body.Length;
                    if (remaining == 0) throw new OwnerSnapshotUnavailableException("request_byte_bound");
                    var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, remaining), inspection.Token);
                    if (read == 0) break;
                    body.Write(buffer, 0, read);
                    if (body.Length > OwnerModelSnapshotService.MaximumRequestBytes)
                        throw new OwnerSnapshotUnavailableException("request_byte_bound");
                }
                var request = OwnerModelSnapshotService.ParseEnvelope(body.ToArray());
                result = await commands.ExecuteOwnerSnapshotHttpAsync(context, request, inspection.Token);
            }
            catch (UnauthorizedAccessException error)
            {
                context.Response.StatusCode = error.Message == "owner_authentication_required" ? 401 : 403;
                result = CommandResult.Fail(error.Message);
            }
            catch (OwnerSnapshotUnavailableException error)
            {
                context.Response.StatusCode = error.Code == "request_byte_bound" ? 413 :
                    error.Code.StartsWith("owner_", StringComparison.Ordinal) ||
                    error.Code.StartsWith("configuration_", StringComparison.Ordinal) ||
                    error.Code.StartsWith("retained_", StringComparison.Ordinal) ||
                    error.Code.StartsWith("runtime_", StringComparison.Ordinal) ? 503 : 400;
                result = CommandResult.Fail(error.Code);
            }
            catch (OwnerRuntimeSnapshotUnavailableException)
            { context.Response.StatusCode = 503; result = CommandResult.Fail("runtime_snapshot_unavailable"); }
            catch (OperationCanceledException)
            {
                if (context.RequestAborted.IsCancellationRequested) return;
                context.Response.StatusCode = 503; result = CommandResult.Fail("snapshot_deadline");
            }
            await WriteResultAsync(context, result, deadline.Token);
        });

    private static async Task WriteResultAsync(HttpContext context, CommandResult result, CancellationToken token)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        try
        {
            await context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(result, CerebralJson.Options), token);
            await context.Response.Body.FlushAsync(token);
        }
        catch (OperationCanceledException) { context.Abort(); }
    }
}
