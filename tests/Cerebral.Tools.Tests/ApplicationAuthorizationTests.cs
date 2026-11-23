using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;

internal static class ApplicationAuthorizationTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        check(typeof(CortexApplicationAuthorization).GetMethod(nameof(CortexApplicationAuthorization.ExecuteUnifiedAsync),
            [typeof(JsonElement), typeof(string), typeof(CancellationToken)]) is not null,
            "the original three-argument application execution method remains available to compiled callers");
        var unsupportedState = Path.Combine(root, "unsupported-application-authorization");
        await using (var production = new CortexApplicationAuthorization(unsupportedState))
        {
            if (production.UnavailableReason is { } unavailable)
            {
                var inactive = production.Snapshot();
                check(!inactive.Active && !production.Configured && inactive.Message == unavailable &&
                    unavailable.Contains("installed PegasusCerebral", StringComparison.Ordinal),
                    "a user-session Worker reports actionable Full Access unavailability");
                foreach (var action in new Func<Task<FullAccessSnapshot>>[]
                {
                    () => production.EnableAsync(default), () => production.RotateAsync(default), () => production.DisableAsync(default)
                })
                {
                    var rejected = false;
                    try { await action(); }
                    catch (UnauthorizedAccessException error) { rejected = error.Message == unavailable; }
                    check(rejected, "unsupported production authorization action fails before opening the system pipe");
                }
                check(!Directory.Exists(unsupportedState), "unsupported production authorization actions create no credential storage");
                await using var dispatcher = new ToolDispatcher(() => new HostSettings(), stateDirectory: unsupportedState);
                check(dispatcher.FullAccessUnavailableReason == unavailable && !dispatcher.FullAccessActive &&
                    dispatcher.FullAccessStatus().Message == unavailable,
                    "the owner status exposes the same local eligibility reason without a Core contract change");
            }
        }

        var state = Path.Combine(root, "application-authorization");
        Directory.CreateDirectory(state);
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Convert.ToBase64String(secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        CryptographicOperations.ZeroMemory(secretBytes);
        var handlers = new ConcurrentBag<Task>();
        JsonElement operation = default;
        var connection = 0;
        var denyIssue = false;

        async Task<Stream> Connect(CancellationToken cancellationToken)
        {
            var name = "cerebral-application-test-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var accepting = server.WaitForConnectionAsync(cancellationToken);
            await client.ConnectAsync(cancellationToken);
            await accepting;
            var current = Interlocked.Increment(ref connection);
            handlers.Add(Task.Run(async () =>
            {
                await using (server)
                {
                    var initial = await ReadAsync<JsonElement>(server, cancellationToken);
                    var action = initial.GetProperty("action").GetString();
                    check(initial.GetProperty("applicationId").GetString() == CortexApplicationAuthorization.ApplicationId,
                        "application authorization identifies Cerebral");
                    if (denyIssue)
                    {
                        await WriteAsync(server, new { ok = false, state = "denied", err = "auth" }, cancellationToken);
                        return;
                    }
                    if (current == 1)
                    {
                        var hasCredential = initial.TryGetProperty("credential", out var firstCredential) && firstCredential.ValueKind != JsonValueKind.Null;
                        check(action == "issue" && !hasCredential,
                            "application issue request carries no credential");
                        await WriteAsync(server, new { ok = true, state = "issued", applicationId = CortexApplicationAuthorization.ApplicationId,
                            label = "Pegasus Cerebral", credentialId = "cac_test", generation = 1, credential = secret }, cancellationToken);
                        return;
                    }

                    check(action == "connect" && initial.GetProperty("credential").GetString() == secret,
                        "protected application credential is used only for the private connect frame");
                    await WriteAsync(server, new { ok = true, state = "ready", applicationId = CortexApplicationAuthorization.ApplicationId,
                        label = "Pegasus Cerebral", credentialId = "cac_test", generation = 1, sessionId = "cas_test" }, cancellationToken);
                    operation = await ReadAsync<JsonElement>(server, cancellationToken);
                    await WriteAsync(server, new { ok = true, jid = "job", rid = "request", state = "done", op = "dotnet_build",
                        data = new { exit = 0 }, partial = false }, cancellationToken);
                }
            }, CancellationToken.None));
            return client;
        }

        await using (var authorization = new CortexApplicationAuthorization(state, Connect))
        {
            check(authorization.UnavailableReason is null, "an explicitly injected transport retains the fixture authorization boundary");
            var status = await authorization.EnableAsync(default);
            check(status.Active && status.Remembered && connection == 2, "application authorization issues once and opens one reusable session");
            var protectedPath = Path.Combine(state, "cerebral-application-authorization.dat");
            var protectedBytes = await File.ReadAllBytesAsync(protectedPath);
            check(!Encoding.UTF8.GetString(protectedBytes).Contains(secret, StringComparison.Ordinal),
                "persisted application authorization does not contain the plaintext credential");

            var callbackRejected = false;
            try
            {
                _ = await authorization.ExecuteUnifiedAsync(CerebralJson.Element(new
                {
                    op = "dotnet_build", src = Path.Combine(root, "Sample.csproj"), cwd = root
                }), root, default, () => throw new InvalidOperationException("fixture collision before dispatch"));
            }
            catch (InvalidOperationException error)
            { callbackRejected = error.Message == "fixture collision before dispatch"; }
            check(callbackRejected && operation.ValueKind == JsonValueKind.Undefined && connection == 2,
                "a post-connection admission refusal sends no application operation frame or replacement session");

            var receipt = await authorization.ExecuteUnifiedAsync(CerebralJson.Element(new
            {
                op = "dotnet_build",
                src = Path.Combine(root, "Sample.csproj"),
                cwd = root,
                cfg = "Release",
                arg = new[] { "--no-restore" },
                output = "agent"
            }), root, default);
            check(receipt.GetProperty("ok").GetBoolean(), "authenticated application session returns the PAO receipt");
            check(operation.GetProperty("op").GetString() == "dotnet" &&
                  operation.GetProperty("arg").GetProperty("op").GetString() == "build" &&
                  operation.GetProperty("arg").GetProperty("arg0").GetString() == "--no-restore",
                "unified PAO operation is translated to the Cortex wire contract");
            check(!operation.TryGetProperty("root", out _) &&
                  !operation.TryGetProperty("pathRoots", out _) &&
                  !operation.GetProperty("arg").TryGetProperty("credential", out _),
                "ordinary operation frames contain no credential or root authorization");
        }

        denyIssue = true;
        var deniedState = Path.Combine(root, "denied-application-authorization");
        await using (var authorization = new CortexApplicationAuthorization(deniedState, Connect))
        {
            var rejected = false;
            try { await authorization.EnableAsync(default); }
            catch (UnauthorizedAccessException error) { rejected = error.Message.EndsWith("(auth)", StringComparison.Ordinal); }
            check(rejected && !Directory.Exists(deniedState),
                "an injected transport still honors broker authentication rejection and stores no credential");
        }
        await Task.WhenAll(handlers);
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var header = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[used..], cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            used += read;
        }
    }
}
