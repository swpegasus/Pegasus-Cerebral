using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Real OS pipe framing/lifecycle tests. The dispatcher is a deterministic fixture; no model, PAO or service runs.</summary>
public static class LocalWorkerPipeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: Local Worker pipe integration requires Windows current-user pipe ACLs.");
            return;
        }
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException("FAIL: " + label); checks++; }
        var endpoint = Endpoint();
        var dispatcher = new RecordingDispatcher();
        var events = new EventHub();
        await using (var server = new LocalWorkerPipeServer(endpoint, dispatcher, events))
        {
            await server.StartAsync(default).WaitAsync(Deadline);
            using var client = new LocalWorkerClient(endpoint);
            var arguments = CerebralJson.Element(new { text = "Hello é中 🦄", nested = new { enabled = true, count = 7 } });
            var result = await client.CommandAsync(new("fixture.echo", arguments, "coding-profile")).WaitAsync(Deadline);
            Check(result.Success && JsonElement.DeepEquals(result.Data.GetProperty("arguments"), arguments), "actual pipe command preserves Unicode and nested structured arguments");
            Check(dispatcher.Requests.TryPeek(out var received) && received.ProfileId == "coding-profile" && received.Operation == "fixture.echo",
                "same dispatcher receives the selected profile without replacement or elevation");
            var failure = await client.CommandAsync(new("fixture.fail", CerebralJson.Element(new { }), "limited-profile")).WaitAsync(Deadline);
            Check(!failure.Success && failure.Error == "Fixture refusal" && dispatcher.Requests.Last().ProfileId == "limited-profile",
                "dispatcher failure and restricting profile survive the pipe roundtrip");

            var before = dispatcher.Requests.Count;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var error = await FailureAsync(() => client.CommandAsync(new("fixture.echo", arguments), cancelled.Token));
                Check(error is OperationCanceledException && dispatcher.Requests.Count == before, "pre-dispatch cancellation does not invoke a command");
            }
            using (var wrongInstance = new LocalWorkerClient(endpoint with { InstanceId = Guid.NewGuid().ToString("N") }))
            {
                var error = await FailureAsync(() => wrongInstance.CommandAsync(new("fixture.echo", arguments)));
                Check(error is not LocalWorkerCommandUncertainException && dispatcher.Requests.Count == before,
                    "wrong instance fails during admission before command dispatch");
            }
            var oversized = await FailureAsync(() => client.CommandAsync(new("fixture.echo",
                CerebralJson.Element(new { text = new string('x', LocalWorkerProtocol.MaximumFrameBytes) }))));
            Check(oversized is InvalidDataException && oversized is not LocalWorkerCommandUncertainException && dispatcher.Requests.Count == before,
                "oversized outbound command is rejected before its frame starts dispatch");

            foreach (var hello in new[]
            {
                new LocalWorkerHello("Another product", LocalWorkerProtocol.Version, endpoint.InstanceId),
                new LocalWorkerHello(LocalWorkerProtocol.Product, LocalWorkerProtocol.Version + 1, endpoint.InstanceId),
                new LocalWorkerHello(LocalWorkerProtocol.Product, LocalWorkerProtocol.Version, Guid.NewGuid().ToString("N"))
            })
            {
                await RejectedHelloAsync(endpoint, hello);
                Check(dispatcher.Requests.Count == before, "server rejects mismatched product/protocol/instance hello before dispatch");
            }
            foreach (var invalid in new byte[][]
            {
                Frame(Encoding.UTF8.GetBytes("{")),
                Frame([0xff]),
                Header(0), Header(-1), Header(LocalWorkerProtocol.MaximumFrameBytes + 1)
            })
            {
                await RejectedFrameAsync(endpoint, invalid);
                Check(dispatcher.Requests.Count == before, "malformed, invalid UTF-8, empty, negative or oversized initial frame never dispatches");
            }

            foreach (var request in new[]
            {
                "{\"kind\":\"command\",\"command\":null}",
                "{\"kind\":\"command\",\"command\":{\"operation\":\"\",\"arguments\":{}}}",
                "{\"kind\":\"command\",\"command\":{\"operation\":\"fixture.echo\"}}",
                "{\"kind\":\"events\",\"command\":{\"operation\":\"fixture.echo\",\"arguments\":{}}}",
                "{\"kind\":\"events\",\"kind\":\"command\"}",
                "{\"kind\":\"events\",\"unrecognized\":true}",
                "{\"kind\":\"unknown\"}"
            })
            {
                await using var pipe = await ConnectAsync(endpoint);
                await HandshakeAsync(pipe, endpoint);
                await pipe.WriteAsync(Frame(Encoding.UTF8.GetBytes(request)));
                var error = await FailureAsync(() => LocalWorkerProtocol.ReadAsync<CommandResult>(pipe));
                Check(IsProtocolFailure(error) && dispatcher.Requests.Count == before, "malformed post-hello request is rejected before dispatcher entry");
            }
            foreach (var incomplete in new byte[][] { [0x20, 0x00], [.. Header(32), (byte)'{'] })
            {
                await using var interrupted = await ConnectAsync(endpoint);
                await interrupted.WriteAsync(incomplete);
                interrupted.Dispose();
                Check(dispatcher.Requests.Count == before, "disconnect during an incomplete header or payload cannot dispatch a command");
            }
            var healthy = await client.CommandAsync(new("fixture.echo", arguments)).WaitAsync(Deadline);
            Check(healthy.Success, "server continues serving after rejecting malformed connections");
            await using (var collision = new LocalWorkerPipeServer(endpoint, new RecordingDispatcher(), new EventHub()))
            {
                var error = await FailureAsync(() => collision.StartAsync(default));
                Check(error is IOException or UnauthorizedAccessException, "a second host cannot take over the existing pipe namespace");
            }

            // Fragment both the length prefix and payload, rather than relying on write boundaries.
            await using (var fragmented = await ConnectAsync(endpoint))
            {
                var bytes = Frame(JsonSerializer.SerializeToUtf8Bytes(new LocalWorkerHello(LocalWorkerProtocol.Product, LocalWorkerProtocol.Version, endpoint.InstanceId), CerebralJson.Options));
                foreach (var value in bytes) await fragmented.WriteAsync(new[] { value });
                var hello = await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(fragmented).WaitAsync(Deadline);
                LocalWorkerProtocol.ValidateHello(hello, endpoint);
                await LocalWorkerProtocol.WriteAsync(fragmented, new LocalWorkerRequest("command", new("fixture.echo", arguments, "fragmented-profile")));
                var response = await LocalWorkerProtocol.ReadAsync<CommandResult>(fragmented).WaitAsync(Deadline);
                Check(response.Success && dispatcher.Requests.Last().ProfileId == "fragmented-profile", "fragmented real pipe frame still dispatches one intact request");
            }

            long previousSequence;
            using (var eventCancellation = new CancellationTokenSource())
            await using (var subscription = client.EventsAsync(eventCancellation.Token).GetAsyncEnumerator())
            {
                var next = subscription.MoveNextAsync().AsTask();
                await PublishUntilDeliveredAsync(events, next, "fixture.first", "job-one", new { value = 11 });
                Check(await next && subscription.Current is { Type: "fixture.first", JobId: "job-one" } && subscription.Current.Data.GetProperty("value").GetInt32() == 11,
                    "actual event subscription streams structured events after admission");
                previousSequence = subscription.Current.Sequence;
                eventCancellation.Cancel();
                var waiting = subscription.MoveNextAsync().AsTask();
                await ObserveStreamEndAsync(waiting);
                Check(true, "event cancellation ends the pending read and permits disposal");
            }
            using (var reconnectCancellation = new CancellationTokenSource())
            await using (var reconnected = client.EventsAsync(reconnectCancellation.Token).GetAsyncEnumerator())
            {
                var next = reconnected.MoveNextAsync().AsTask();
                await PublishUntilDeliveredAsync(events, next, "fixture.reconnected", "job-two", new { value = 22 });
                Check(await next && reconnected.Current.Type == "fixture.reconnected" && reconnected.Current.Sequence > previousSequence,
                    "a new explicit subscription reconnects and receives fresh ordered events");
            }

            // Occupy all eight admitted connections with acknowledged subscriptions. The extra
            // command must wait for a slot rather than increasing concurrent dispatcher admission.
            var held = new List<NamedPipeClientStream>();
            try
            {
                for (var index = 0; index < 8; index++)
                {
                    var pipe = await ConnectAsync(endpoint); held.Add(pipe);
                    await HandshakeAsync(pipe, endpoint);
                    await LocalWorkerProtocol.WriteAsync(pipe, new LocalWorkerRequest("events"));
                    Check((await LocalWorkerProtocol.ReadAsync<CommandResult>(pipe).WaitAsync(Deadline)).Success, "bounded subscription slot is admitted");
                }
                before = dispatcher.Requests.Count;
                var waiting = client.CommandAsync(new("fixture.echo", arguments));
                await Task.Delay(100);
                Check(!waiting.IsCompleted && dispatcher.Requests.Count == before, "ninth connection cannot bypass the eight-client limit");
                held[0].Dispose(); held.RemoveAt(0);
                Check((await waiting.WaitAsync(Deadline)).Success, "disconnect releases capacity for the waiting command");
            }
            finally { foreach (var pipe in held) pipe.Dispose(); }

            await server.StopAsync(default).WaitAsync(Deadline);
        }

        foreach (var mode in new[] { "cancel", "disconnect", "stop" })
        {
            var isolated = Endpoint();
            var blocked = new RecordingDispatcher();
            await using var server = new LocalWorkerPipeServer(isolated, blocked, new EventHub());
            await server.StartAsync(default).WaitAsync(Deadline);
            using var client = new LocalWorkerClient(isolated);
            if (mode == "disconnect")
            {
                await using var pipe = await ConnectAsync(isolated);
                await HandshakeAsync(pipe, isolated);
                await LocalWorkerProtocol.WriteAsync(pipe, new LocalWorkerRequest("command", new("fixture.block", CerebralJson.Element(new { }), "blocking-profile")));
                await blocked.Started.Task.WaitAsync(Deadline);
                pipe.Dispose();
            }
            else
            {
                using var cancellation = new CancellationTokenSource();
                var command = client.CommandAsync(new("fixture.block", CerebralJson.Element(new { }), "blocking-profile"), cancellation.Token);
                await blocked.Started.Task.WaitAsync(Deadline);
                if (mode == "cancel") cancellation.Cancel();
                else await server.StopAsync(default).WaitAsync(Deadline);
                var error = await FailureAsync(() => command);
                Check(error is LocalWorkerCommandUncertainException, "post-dispatch " + mode + " is reported as uncertain, not safe-to-replay cancellation");
            }
            await blocked.Cancelled.Task.WaitAsync(Deadline);
            Check(blocked.Requests.Count == 1 && blocked.Requests.Single().ProfileId == "blocking-profile", mode + " cancels the single dispatcher invocation without replay or profile change");
        }

        foreach (var mismatch in new[] { "product", "protocol", "instance" })
        {
            var fakeEndpoint = Endpoint();
            await using var pipe = new NamedPipeServerStream(fakeEndpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            using var timeout = new CancellationTokenSource(Deadline);
            var accept = Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                _ = await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(pipe, timeout.Token);
                var hello = new LocalWorkerHello(mismatch == "product" ? "Another product" : LocalWorkerProtocol.Product,
                    mismatch == "protocol" ? LocalWorkerProtocol.Version + 1 : LocalWorkerProtocol.Version,
                    mismatch == "instance" ? Guid.NewGuid().ToString("N") : fakeEndpoint.InstanceId);
                await LocalWorkerProtocol.WriteAsync(pipe, hello, timeout.Token);
                return await FailureAsync(() => LocalWorkerProtocol.ReadAsync<LocalWorkerRequest>(pipe, timeout.Token));
            });
            using var client = new LocalWorkerClient(fakeEndpoint);
            var error = await FailureAsync(() => client.CommandAsync(new("fixture.echo", CerebralJson.Element(new { }))));
            Check(error is not LocalWorkerCommandUncertainException && IsProtocolFailure(error), "client rejects mismatched server " + mismatch + " before sending a command");
            Check(IsProtocolFailure(await accept.WaitAsync(Deadline)), "mismatched server never receives a command frame");
        }

        var uncooperativeEndpoint = Endpoint();
        var uncooperative = new RecordingDispatcher();
        var boundedServer = new LocalWorkerPipeServer(uncooperativeEndpoint, uncooperative, new EventHub());
        var disposed = false;
        try
        {
            await boundedServer.StartAsync(default).WaitAsync(Deadline);
            using var client = new LocalWorkerClient(uncooperativeEndpoint);
            var pending = client.CommandAsync(new("fixture.ignore-cancellation", CerebralJson.Element(new { }), "uncooperative-profile"));
            await uncooperative.Started.Task.WaitAsync(Deadline);
            using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            {
                try { await boundedServer.StopAsync(deadline.Token).WaitAsync(Deadline); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }
            await uncooperative.Cancelled.Task.WaitAsync(Deadline);
            Check(!uncooperative.Release.Task.IsCompleted, "bounded host stop does not require an uncooperative dispatcher to finish");
            var error = await FailureAsync(() => pending);
            Check(error is LocalWorkerCommandUncertainException, "host stop closes the client pipe even while a dispatcher ignores cancellation");
            await boundedServer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); disposed = true;
            Check(!uncooperative.Release.Task.IsCompleted && uncooperative.Requests.Count == 1,
                "pipe disposal remains bounded without replaying or forcibly completing the uncooperative command");
        }
        finally
        {
            // The fixture, unlike a production plugin, exposes an explicit release so the test
            // leaves no blocked task after demonstrating bounded transport shutdown.
            uncooperative.Release.TrySetResult();
            if (uncooperative.Started.Task.IsCompleted) await uncooperative.Completed.Task.WaitAsync(Deadline);
            if (!disposed) await boundedServer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Console.WriteLine($"Local Worker pipe integration: {checks} assertions passed against real Windows named pipes with a deterministic command dispatcher. No model, PAO, installed service or cross-user access was exercised.");
    }

    private static LocalWorkerEndpoint Endpoint() => new("Pegasus.Cerebral.Tests." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));

    private static async Task<NamedPipeClientStream> ConnectAsync(LocalWorkerEndpoint endpoint)
    {
        var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { using var timeout = new CancellationTokenSource(Deadline); await pipe.ConnectAsync(timeout.Token); return pipe; }
        catch { pipe.Dispose(); throw; }
    }

    private static async Task HandshakeAsync(Stream pipe, LocalWorkerEndpoint endpoint)
    {
        await LocalWorkerProtocol.WriteAsync(pipe, new LocalWorkerHello(LocalWorkerProtocol.Product, LocalWorkerProtocol.Version, endpoint.InstanceId));
        LocalWorkerProtocol.ValidateHello(await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(pipe).WaitAsync(Deadline), endpoint);
    }

    private static async Task RejectedHelloAsync(LocalWorkerEndpoint endpoint, LocalWorkerHello hello)
    {
        await using var pipe = await ConnectAsync(endpoint);
        await LocalWorkerProtocol.WriteAsync(pipe, hello);
        var error = await FailureAsync(() => LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(pipe));
        if (!IsProtocolFailure(error)) throw new InvalidOperationException("Invalid hello did not produce a protocol rejection.", error);
    }

    private static async Task RejectedFrameAsync(LocalWorkerEndpoint endpoint, byte[] bytes)
    {
        await using var pipe = await ConnectAsync(endpoint);
        var error = await FailureAsync(async () =>
        {
            await pipe.WriteAsync(bytes);
            _ = await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(pipe);
        });
        if (!IsProtocolFailure(error)) throw new InvalidOperationException("Malformed frame did not produce a protocol rejection.", error);
    }

    private static async Task<Exception> FailureAsync(Func<Task> action)
    {
        try { await action().WaitAsync(Deadline); }
        catch (Exception error) when (error is not TimeoutException) { return error; }
        throw new InvalidOperationException("Expected the operation to reject or disconnect.");
    }

    private static bool IsProtocolFailure(Exception error) => error is IOException or InvalidDataException or JsonException or ArgumentException;

    private static async Task ObserveStreamEndAsync(Task<bool> pending)
    {
        try { if (await pending.WaitAsync(Deadline)) throw new InvalidOperationException("Cancelled event stream produced another event."); }
        catch (Exception error) when (error is OperationCanceledException or IOException) { }
    }

    private static async Task PublishUntilDeliveredAsync(EventHub events, Task<bool> pending, string type, string job, object data)
    {
        // The public async enumerable deliberately hides the subscription ack. Publish a bounded
        // probe until the first item arrives instead of assuming scheduler/connect timing.
        for (var attempt = 0; attempt < 100 && !pending.IsCompleted; attempt++)
        {
            events.Publish(type, job, data);
            await Task.WhenAny(pending, Task.Delay(20));
        }
        if (!await pending.WaitAsync(Deadline)) throw new InvalidOperationException("Event stream ended before a probe was delivered.");
    }

    private static byte[] Frame(byte[] payload) => [.. Header(payload.Length), .. payload];
    private static byte[] Header(int size)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, size); return bytes;
    }

    private sealed class RecordingDispatcher : ICommandDispatcher
    {
        public ConcurrentQueue<CommandRequest> Requests { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (request.Operation == "fixture.fail") return CommandResult.Fail("Fixture refusal");
            if (request.Operation == "fixture.block")
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            if (request.Operation == "fixture.ignore-cancellation")
            {
                using var cancellation = cancellationToken.Register(() => Cancelled.TrySetResult());
                Started.TrySetResult();
                try { await Release.Task; }
                finally { Completed.TrySetResult(); }
            }
            return CommandResult.Ok(new { request.Operation, request.ProfileId, request.Arguments });
        }
    }
}
