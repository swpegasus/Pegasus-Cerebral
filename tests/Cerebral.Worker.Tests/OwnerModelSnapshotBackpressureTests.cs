using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Actual same-user OS pipe stalled readers and separate real Kestrel requests with controlled
/// response-stream failure injection. Synthetic task-owned metadata only; no provider, installed
/// service enrollment, remote owner, physical TCP backpressure or deployment is exercised.
/// </summary>
public static class OwnerModelSnapshotBackpressureTests
{
    private const string WorkerId = "123456781234123412341234567890ab";
    private const string LargeModelId = "snapshot-backpressure-model";
    private const string FixturePrefix = "owner-snapshot-backpressure-";
    private static readonly JsonSerializerOptions CompactJson = new(CerebralJson.Options) { WriteIndented = false };

    public static async Task<int> RunAsync(string fixtureRoot)
    {
        var parent = RequireFixtureParent(fixtureRoot);
        var root = Path.Combine(parent, FixturePrefix + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await using var host = new OwnerModelSnapshotPathTests.Fixture(root, "source");
            host.Store.Put("model", LargeModelId, new ModelRecord
            {
                Id = LargeModelId, Name = "Synthetic unread reply", Path = "/synthetic/backpressure.gguf",
                State = "unloaded", MaximumContextTokens = 4096, ChatTemplate = new string('t', 40000),
                StopSequences = Enumerable.Repeat(new string('s', 1024), 128).ToList()
            });
            await host.StartAsync(pipe: true, deadline.Token);
            var endpoint = host.PipeEndpoint ?? throw new InvalidOperationException("Pipe fixture did not start.");
            var capacity = ReadPipeCapacity(host);
            // The accept loop reserves one of eight slots while waiting for the next client.
            await WaitUntilAsync(() => capacity.CurrentCount == 7, deadline.Token);
            Check(capacity.CurrentCount == 7, "The actual idle pipe must retain seven available slots plus its reserved listener.");

            var request = SelectedRequest(host.ControllerId, LargeModelId);
            var wireRequest = new LocalWorkerRequest("command", request);
            Check(JsonSerializer.SerializeToUtf8Bytes(wireRequest, CompactJson).Length <= 4096,
                "Stalled readers must send a bounded original snapshot command frame.");
            var initial = host.Capture();
            using (var preflight = new CerebralClient(endpoint))
            {
                var reply = await preflight.CommandAsync(request.Operation, request.Arguments, cancellationToken: deadline.Token);
                Check(reply.Success, "The actual client must first observe the synthetic large selected registration successfully.");
                var binding = reply.Data.GetProperty("registration").GetProperty("binding");
                Check(binding.GetProperty("bindingMode").GetString() == "full" &&
                    binding.GetProperty("rawBindingAvailable").GetBoolean() &&
                    binding.GetProperty("rawBinding").GetProperty("chatTemplate").GetString()?.Length == 40000 &&
                    binding.GetProperty("rawBinding").GetProperty("stopSequences").GetArrayLength() == 128,
                    "The large selected reply must contain actual full raw binding evidence, not fingerprint-only degradation.");
                var replyBytes = JsonSerializer.SerializeToUtf8Bytes(reply, CerebralJson.Options).Length;
                Check(replyBytes >= 150 * 1024 && replyBytes <= 256 * 1024,
                    "The complete real reply must exceed the server's 16KiB pipe buffer and remain within the 256KiB envelope bound.");
            }
            host.VerifyNoEffects(initial, expectModelRead: true, Check, "large reply preflight");
            await WaitUntilAsync(() => capacity.CurrentCount == 7, deadline.Token);

            var before = host.Capture();
            var admissionBaseline = host.Commands.OwnerSnapshotVerifiedAdmissions;
            var readers = new List<NamedPipeClientStream>(8);
            var elapsed = Stopwatch.StartNew();
            try
            {
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                {
                    startup.CancelAfter(TimeSpan.FromSeconds(5));
                    for (var index = 0; index < 8; index++)
                    {
                        var reader = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
                            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
                        readers.Add(reader);
                        await reader.ConnectAsync(startup.Token);
                        await LocalWorkerProtocol.WriteAsync(reader, LocalWorkerProtocol.CreateHello(endpoint), startup.Token);
                        LocalWorkerProtocol.ValidateHello(
                            await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(reader, startup.Token), endpoint);
                        await LocalWorkerProtocol.WriteAsync(reader, wireRequest, startup.Token);
                        // Deliberately do not read any CommandResult bytes. Keep every client handle
                        // open until after the deadline recovery and fresh-client success checks.
                    }
                    await WaitUntilAsync(() =>
                        host.Commands.OwnerSnapshotVerifiedAdmissions == admissionBaseline + 8, startup.Token);
                }
                Check(readers.Count == 8 && host.Commands.OwnerSnapshotVerifiedAdmissions == admissionBaseline + 8,
                    "All eight actual same-user Identification clients must receive verified snapshot admission exactly once.");
                await Task.Delay(TimeSpan.FromMilliseconds(150), deadline.Token);
                Check(capacity.CurrentCount == 0,
                    "Eight unread full raw replies must hold all actual server slots before their deadlines.");
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(8),
                    "The held-capacity observation must precede expiry of the ten-second request deadlines.");

                await WaitUntilAsync(() => capacity.CurrentCount == 7, deadline.Token);
                Check(elapsed.Elapsed >= TimeSpan.FromSeconds(9) && elapsed.Elapsed < TimeSpan.FromSeconds(25),
                    "Real unread replies must release slots at their bounded request deadlines while all client handles remain open.");
                Check(capacity.CurrentCount == 7,
                    "Deadline cleanup must recover every handler slot, allowing only the normal listener reservation.");

                using var ninth = new CerebralClient(endpoint);
                var small = SelectedRequest(host.ControllerId, "snapshot-backpressure-not-found");
                var result = await ninth.CommandAsync(small.Operation, small.Arguments, cancellationToken: deadline.Token);
                Check(result.Success &&
                    result.Data.GetProperty("catalog").GetProperty("complete").GetBoolean() &&
                    result.Data.GetProperty("registration").GetProperty("state").GetString() == "not_found" &&
                    !result.Data.GetProperty("admission").GetBoolean(),
                    "A ninth real CerebralClient must complete a fresh small missing-selection snapshot after recovery.");
                Check(host.Commands.OwnerSnapshotVerifiedAdmissions == admissionBaseline + 9,
                    "Stalled reads and recovery must not replay or duplicate any accepted snapshot command.");
                host.VerifyNoEffects(before, expectModelRead: true, Check, "eight stalled pipe replies and ninth-client recovery");
            }
            finally
            {
                foreach (var reader in readers) reader.Dispose();
            }
            Console.WriteLine($"Owner snapshot pipe backpressure: {checks} actual same-user OS pipe/SQLite assertions passed with eight unread synthetic full-binding replies and deadline recovery. No provider/model launch, installed enrollment, remote owner or deployment was exercised.");
            return checks;
        }
        finally { Cleanup(root, parent); }
    }

    /// <summary>
    /// Real Kestrel/dispatcher/store/runtime requests with controlled Write and Flush stalls.
    /// This proves deadline-token propagation and native abort, not physical TCP backpressure.
    /// </summary>
    public static Task<int> RunHttpAsync(string fixtureRoot) => RunHttpStagesAsync(fixtureRoot, alias: false);

    /// <summary>Alias refusal uses the same bounded writer without entering owner admission or model observation.</summary>
    public static Task<int> RunAliasHttpAsync(string fixtureRoot) => RunHttpStagesAsync(fixtureRoot, alias: true);

    private static async Task<int> RunHttpStagesAsync(string fixtureRoot, bool alias)
    {
        var parent = RequireFixtureParent(fixtureRoot);
        var root = Path.Combine(parent, FixturePrefix + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }
        try
        {
            foreach (var stage in new[] { StallStage.Write, StallStage.Flush })
                await VerifyHttpStallAsync(Path.Combine(root, stage.ToString()), stage, Check, alias);
            var lane = alias ? "alias-refusal" : "canonical";
            Console.WriteLine($"Owner snapshot HTTP {lane} deadline: {checks} real Kestrel/SQLite assertions passed with controlled response Write/Flush failure injection. Token cancellation, native abort and recovery were observed; physical TCP backpressure, provider launch, installed enrollment and deployment were not exercised.");
            return checks;
        }
        finally { Cleanup(root, parent); }
    }

    private static async Task VerifyHttpStallAsync(string root, StallStage stage, Action<bool, string> check, bool alias)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using var host = new OwnerModelSnapshotPathTests.Fixture(root, "source");
        var entered = new TaskCompletionSource<StallEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequest = 0;
        host.BeforeOwnerRoute = context =>
        {
            if (Interlocked.CompareExchange(ref firstRequest, 1, 0) != 0) return;
            context.RequestAborted.Register(() => aborted.TrySetResult());
            context.Response.Body = new ControlledStallStream(context.Response.Body, stage, entered, canceled);
        };
        await host.StartAsync(pipe: false, deadline.Token);
        var before = host.Capture();
        var admissions = host.Commands.OwnerSnapshotVerifiedAdmissions;
        var request = SelectedRequest(host.ControllerId, "snapshot-http-backpressure-not-found");
        var route = alias ? "/API/COMMAND/MODELS.BINDING.SNAPSHOT/" : OwnerModelSnapshotService.HttpPath;
        var expectModelRead = !alias;
        var expectedAdmissions = alias ? 0 : 1;
        var label = (alias ? "Alias refusal " : "Canonical ") + stage;
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = new Uri(host.Address!) };
        using var message = new HttpRequestMessage(HttpMethod.Post, route)
        { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, CompactJson)) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Add("X-Cerebral-Key", host.OwnerKey);
        var client = ObserveHttpTerminationAsync(http, message, deadline.Token);
        var stall = await entered.Task.WaitAsync(deadline.Token);
        check(stall.TokenCanBeCanceled && !stall.TokenWasAlreadyCanceled,
            stage + " must receive the actual live cancellable response deadline token before stalling.");
        check(stage == StallStage.Flush ? stall.WriteBytes > 0 : stall.WriteBytes == 0,
            stage + " controlled stream must stall the intended response phase.");
        check(host.Commands.OwnerSnapshotVerifiedAdmissions == admissions + expectedAdmissions,
            alias ? label + " stalled refusal must never issue verified owner admission."
                : label + " stalled response must follow exactly one real verified owner snapshot admission.");
        var duration = await canceled.Task.WaitAsync(deadline.Token);
        check(duration >= TimeSpan.FromSeconds(8) && duration < TimeSpan.FromSeconds(15),
            stage + " must cancel at the remaining outer ten-second deadline, without waiting for client disposal.");
        await aborted.Task.WaitAsync(deadline.Token);
        check(aborted.Task.IsCompletedSuccessfully,
            stage + " cancellation must abort the actual Kestrel request connection.");
        var outcome = await client.WaitAsync(deadline.Token);
        check(outcome == ClientTermination.ConnectionFailure,
            stage + " native HTTP client must observe a closed/incomplete response rather than a false complete success.");
        check(!deadline.IsCancellationRequested,
            stage + " server cancellation/abort must precede the independent 25-second test deadline.");
        host.VerifyNoEffects(before, expectModelRead, check, label + " controlled HTTP response stall");

        // The hook controls only the first request. A second genuine request proves that the
        // server and exact owner route remain usable after their first connection was aborted.
        var recovered = await host.HttpAsync(route,
            JsonSerializer.SerializeToUtf8Bytes(request, CompactJson), host.OwnerKey, deadline.Token);
        check(alias
                ? recovered.Status == HttpStatusCode.BadRequest && recovered.Result?.Success == false &&
                    recovered.Result.Error == "snapshot_route_noncanonical"
                : recovered.Result?.Success == true &&
                    recovered.Result.Data.GetProperty("registration").GetProperty("state").GetString() == "not_found" &&
                    !recovered.Result.Data.GetProperty("admission").GetBoolean(),
            alias ? label + " fresh alias request must return the exact early400 refusal after connection recovery."
                : label + " real owner HTTP path must recover with a fresh complete snapshot response.");
        check(host.Commands.OwnerSnapshotVerifiedAdmissions == admissions + expectedAdmissions * 2,
            alias ? label + " refusal recovery must still issue no owner proof."
                : label + " response recovery must not replay or duplicate the first command.");
        host.VerifyNoEffects(before, expectModelRead, check, label + " HTTP recovery");
    }

    private static async Task<ClientTermination> ObserveHttpTerminationAsync(HttpClient http,
        HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            await using var body = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[4096];
            var bytes = 0;
            while (true)
            {
                var read = await body.ReadAsync(buffer, token);
                if (read == 0) return ClientTermination.CompleteResponse;
                bytes += read;
                if (bytes > 256 * 1024)
                    throw new InvalidOperationException("Controlled HTTP response exceeded the snapshot envelope budget.");
            }
        }
        catch (HttpRequestException) { return ClientTermination.ConnectionFailure; }
        catch (IOException) { return ClientTermination.ConnectionFailure; }
    }

    private static CommandRequest SelectedRequest(string controllerId, string modelId) => new(
        OwnerModelSnapshotService.Operation, CerebralJson.Element(new
        {
            schemaVersion = 1, expectedWorkerInstallationId = WorkerId,
            expectedControllerInstallationId = controllerId, modelId
        }));

    private static SemaphoreSlim ReadPipeCapacity(OwnerModelSnapshotPathTests.Fixture fixture)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var server = typeof(OwnerModelSnapshotPathTests.Fixture).GetField("pipe", flags)?.GetValue(fixture)
            as LocalWorkerPipeServer ?? throw new InvalidOperationException("The actual fixture pipe is unavailable for bounded capacity inspection.");
        return typeof(LocalWorkerPipeServer).GetField("capacity", flags)?.GetValue(server) as SemaphoreSlim
            ?? throw new InvalidOperationException("The actual pipe capacity counter is unavailable.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(TimeSpan.FromMilliseconds(10), token);
    }

    private static string RequireFixtureParent(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("Backpressure fixtures require an explicit absolute task-owned root.");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var allowed = Path.GetFullPath(@"C:\Development\Pegasus-Cerebral\.tmp\core-10000000-0000-4000-8000-000000000001\snapshot-candidate");
        if (!parent.Equals(allowed, StringComparison.OrdinalIgnoreCase) &&
            !parent.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backpressure fixtures must remain beneath this task's snapshot-candidate directory.");
        for (var directory = new DirectoryInfo(parent); directory is not null && directory.FullName.Length >= allowed.Length; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Backpressure fixtures refuse reparse-point roots.");
        Directory.CreateDirectory(parent);
        return parent;
    }

    private static void Cleanup(string root, string parent)
    {
        SqliteConnection.ClearAllPools();
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith(FixturePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the exact task-owned backpressure fixture.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    private enum StallStage { Write, Flush }
    private enum ClientTermination { CompleteResponse, ConnectionFailure }
    private sealed record StallEvidence(bool TokenCanBeCanceled, bool TokenWasAlreadyCanceled, long WriteBytes);

    private sealed class ControlledStallStream(Stream inner, StallStage stage,
        TaskCompletionSource<StallEvidence> entered, TaskCompletionSource<TimeSpan> canceled) : Stream
    {
        private long writeBytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException("This fixture requires the actual asynchronous response flush.");
        public override Task FlushAsync(CancellationToken token) =>
            stage == StallStage.Flush ? StallAsync(token) : inner.FlushAsync(token);
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("This fixture requires the actual asynchronous response write.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (stage == StallStage.Write) await StallAsync(token);
            else
            {
                await inner.WriteAsync(buffer, token);
                Interlocked.Add(ref writeBytes, buffer.Length);
            }
        }
        private async Task StallAsync(CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            entered.TrySetResult(new(token.CanBeCanceled, token.IsCancellationRequested, Interlocked.Read(ref writeBytes)));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                canceled.TrySetResult(watch.Elapsed);
                throw;
            }
        }
        // Kestrel owns the underlying response stream and request lifetime.
        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
