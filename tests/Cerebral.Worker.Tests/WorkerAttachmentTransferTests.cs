using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Resources;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Real SQLite and shared dispatcher; Windows additionally crosses an actual same-user named pipe. No model is run.</summary>
public static class WorkerAttachmentTransferTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-attachment-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("FAIL: " + message); checks++; }
        try
        {
            var state = new StateStore(root); var configuration = new Configuration(state); var events = new EventHub();
            var lead = new AgentProfile { Id = "lead-attachment", Name = "Lead", Role = "lead", AllowedRoots = [root],
                Capabilities = ["administration", "filesystem.read", "process.execute", "desktop.plugins.manage"],
                AllowedTools = ["file.read", "process.run"] };
            var other = new AgentProfile { Id = "other-attachment", Name = "Other", Capabilities = ["administration"] };
            state.Put("agent", lead.Id, lead); state.Put("agent", other.Id, other);
            var journal = new EventJournal(Path.Combine(root, "engine.db"));
            var originals = new AttachmentStore(journal);
            var clock = new FixtureClock();
            using var transfers = new WorkerAttachmentTransfers(state, originals, clock);
            await using var models = new ModelRuntime(state, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get);
            using var scheduler = new JobScheduler(state, configuration, new NoModelExecutor(), models, events, NullLogger<JobScheduler>.Instance);
            var workflows = new WorkerWorkflows(state, configuration, events, [], [], journal: journal);
            var dispatcher = new CommandDispatcher(state, configuration, models, scheduler, tools, events, workflows: workflows, attachments: transfers);
            static string[] AttachmentOperations(CommandResult response) => response.Success
                ? response.Data.GetProperty("operations").EnumerateArray().Select(x => x.GetProperty("id").GetString()!)
                    .Where(x => x.StartsWith(WorkerAttachmentTransfers.Prefix, StringComparison.Ordinal)).ToArray()
                : throw new InvalidOperationException("SDK discovery failed: " + response.Error);
            var ownerOperations = AttachmentOperations(await dispatcher.ExecuteAsync(new("sdk.discover", CerebralJson.Element(new { }))));
            var actorOperations = AttachmentOperations(await dispatcher.ExecuteAsync(new("sdk.discover", CerebralJson.Element(new { }), lead.Id)));
            Check(ownerOperations.Length == 12, "owner SDK discovery advertises all available attachment and archive operations");
            Check(actorOperations.Order().SequenceEqual(new[] { "import.status", "describe", "read", "text", "archive.inspect", "archive.extract", "archive.status", "archive.text" }
                .Select(x => WorkerAttachmentTransfers.Prefix + x).Order()),
                "agent SDK discovery advertises only scoped attachment reads and status");
            Check(!(await dispatcher.ExecuteAsync(new("sdk.describe", CerebralJson.Element(new { id = WorkerAttachmentTransfers.Prefix + "import.begin" }), lead.Id))).Success,
                "agent cannot describe owner-only attachment mutations");
            var withoutAttachments = new CommandDispatcher(state, configuration, models, scheduler, tools, events, workflows: workflows);
            Check(AttachmentOperations(await withoutAttachments.ExecuteAsync(new("sdk.discover", CerebralJson.Element(new { })))).Length == 0,
                "SDK omits attachment operations when no transfer manager is mounted");
            async Task<CommandResult> Command(string name, object input, string? actor = null, CancellationToken token = default) =>
                await dispatcher.ExecuteAsync(new(WorkerAttachmentTransfers.Prefix + name, CerebralJson.Element(input), actor), token);
            async Task<AttachmentImportStatus> Accepted(string name, object input)
            {
                var result = await Command(name, input);
                if (!result.Success) throw new InvalidOperationException("Attachment " + name + " failed: " + result.Error);
                return CerebralJson.Read<AttachmentImportStatus>(result.Data);
            }
            var data = Encoding.UTF8.GetBytes("Private source text, café.\r\nKeep all bytes.");
            var begin = new AttachmentImportBeginRequest("first-import", lead.Id, "chat", "source.txt", data.Length);
            var subscription = events.Subscribe();
            try
            {
                Check(!(await Command("import.begin", begin, lead.Id)).Success &&
                    !(await Command("import.begin", begin with { ProfileId = other.Id }, other.Id)).Success,
                    "administration agents cannot use owner-selected import commands");
                var start = await Accepted("import.begin", begin);
                Check(start.State == "receiving" && start.ReceivedBytes == 0 && start.Length == data.Length && start.UploadId is not null,
                    "begin reserves bounded staging and returns a concrete upload generation");
                Check(journal.ReadPage("chat").Events.Count == 0 && originals.FindImportReceipt(new(lead.Id, "chat"), begin.RequestId) is null,
                    "partial staging creates neither transcript content nor an original receipt");
                var same = await Accepted("import.begin", begin);
                Check(same.UploadId == start.UploadId, "lost begin response reconciles to the same staging generation");
                Check(!(await Command("import.begin", begin with { OriginalName = "changed.txt" })).Success &&
                    !(await Command("import.begin", begin with { Length = data.Length + 1 })).Success,
                    "changed begin metadata cannot redefine an existing request");
                var operation = new AttachmentImportUploadRequest(begin.RequestId, lead.Id, "chat", start.UploadId!);
                Check(!(await Command("import.commit", operation)).Success, "incomplete original cannot commit");
                var firstChunk = new AttachmentImportChunkRequest(begin.RequestId, lead.Id, "chat", start.UploadId!, 0, data[..10]);
                var received = await Accepted("import.chunk", firstChunk);
                var repeated = await Accepted("import.chunk", firstChunk);
                Check(received.ReceivedBytes == 10 && repeated.ReceivedBytes == 10, "an exact repeated chunk acknowledges the retained bytes without appending twice");
                Check(!(await Command("import.chunk", firstChunk with { Bytes = "different!"u8.ToArray() })).Success &&
                    !(await Command("import.chunk", firstChunk with { Offset = 12 })).Success &&
                    !(await Command("import.chunk", firstChunk with { UploadId = Guid.NewGuid().ToString("N") })).Success,
                    "changed chunks, missing offsets, and stale staging generations are rejected");
                Check(!(await Command("import.chunk", firstChunk with { ProfileId = other.Id })).Success &&
                    !(await Command("import.chunk", firstChunk, lead.Id)).Success,
                    "neither another scope nor an administration agent can append bytes");
                await Accepted("import.chunk", firstChunk with { Offset = 10, Bytes = data[10..] });
                var complete = await Accepted("import.commit", operation);
                var attachment = complete.Attachment ?? throw new InvalidOperationException("Complete import did not return its immutable attachment.");
                Check(complete.State == "complete" && complete.UploadId is null &&
                    complete.ReceivedBytes == data.Length && originals.ReadRange(new(lead.Id, "chat"), attachment.Id, 1, 0, data.Length).Bytes.Span.SequenceEqual(data),
                    "commit stores one complete exact original and returns only its immutable reference");
                Check((await Accepted("import.commit", operation)).Attachment?.Id == attachment.Id &&
                    (await Accepted("import.begin", begin)).Attachment?.Id == attachment.Id,
                    "lost commit and begin responses resolve the original receipt without reopening or replaying source");
                Check(!(await Command("import.begin", begin with { OriginalName = "renamed.txt" })).Success &&
                    !(await Command("import.begin", begin with { Length = data.Length + 1 })).Success,
                    "committed request identity still rejects changed begin metadata");
                Check((await Accepted("import.abort", operation)).State == "complete",
                    "abort after commit reports the retained original instead of deleting it");
                var statusInput = new AttachmentImportStatusRequest(begin.RequestId, lead.Id, "chat");
                Check((await Accepted("import.status", statusInput)).Attachment?.Id == attachment.Id &&
                    CerebralJson.Read<AttachmentImportStatus>((await Command("import.status", statusInput, lead.Id)).Data).State == "complete" &&
                    !(await Command("import.status", statusInput, other.Id)).Success &&
                    (await Accepted("import.status", statusInput with { ConversationId = "foreign" })).State == "missing",
                    "receipt status is scoped for owner and agent callers without exposing foreign imports");
                var describe = new AttachmentDescribeRequest(lead.Id, "chat", attachment.Id, 1);
                Check((await Command("describe", describe, lead.Id)).Success &&
                    !(await Command("describe", describe, other.Id)).Success &&
                    !(await Command("describe", describe with { ConversationId = "foreign" })).Success &&
                    !(await Command("describe", describe with { Revision = 2 })).Success,
                    "describe enforces exact profile, conversation, and immutable revision");
                var ranged = await Command("read", new AttachmentRangeRequest(lead.Id, "chat", attachment.Id, 1, 1, 7), lead.Id);
                Check(ranged.Success && CerebralJson.Read<AttachmentRange>(ranged.Data).Bytes.Span.SequenceEqual(data.AsSpan(1, 7)) &&
                    !(await Command("read", new AttachmentRangeRequest(lead.Id, "chat", attachment.Id, 1, data.Length - 1, 2))).Success,
                    "dispatcher ranges return exact bytes and reject past-EOF reads");
                var text = await Command("text", new AttachmentTextRequest(lead.Id, "chat", attachment.Id, 1));
                Check(text.Success && CerebralJson.Read<AttachmentText>(text.Data).Text == Encoding.UTF8.GetString(data),
                    "text resolution preserves original source text outside the conversation transcript");
                Check(!subscription.Reader.TryRead(out _) && journal.ReadPage("chat").Events.Count == 0 &&
                    Scalar(Path.Combine(root, "engine.db"), "SELECT COUNT(*) FROM resource_attachments;") == 1 &&
                    state.List<AgentJob>("job").Count == 0,
                    "upload commands emit no chunk payloads, chat events, agent jobs, or duplicate originals");
                if (OperatingSystem.IsWindows())
                {
                    var chat = new TaskSession { Id = "chat", ProfileId = lead.Id }; state.Put("session", chat.Id, chat);
                    var job = new AgentJob { Id = "plugin-job", ProfileId = lead.Id, Profile = lead, SessionId = chat.Id, Status = "completed" }; state.Put("job", job.Id, job);
                    var pluginAssembly = Path.Combine(root, "fixture-plugin.dll"); await File.WriteAllTextAsync(pluginAssembly, "Admission fixture; never loaded.");
                    var manifest = Path.Combine(root, "desktop-plugin.json");
                    await File.WriteAllTextAsync(manifest, """
                        {"schemaVersion":1,"contract":"pegasus.cerebral.desktop","apiVersion":1,"id":"acceptance.attachment-fixture","name":"Attachment fixture","version":"1.0.0","description":"Admission only","assembly":"fixture-plugin.dll","entryType":"Acceptance.AttachmentFixture"}
                        """);
                    var interfaceRequests = new InterfaceRequests(state, events);
                    var presentation = interfaceRequests.Create(job, lead, "plugin", manifest);
                    var claim = interfaceRequests.Claim(presentation.Id, "desktop-window");
                    var pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                    await using var png = new MemoryStream(pngBytes, writable: false);
                    var captureRequestId = "interface-" + presentation.Id;
                    var captured = await originals.ImportAsync(new(lead.Id, chat.Id), captureRequestId, "PluginPanel-fixture.png", png);
                    var completion = await dispatcher.ExecuteAsync(new("interface.complete", CerebralJson.Element(new
                    {
                        id = presentation.Id, claimId = claim.Request.ClaimId, success = true,
                        message = "Mounted and captured the native plugin panel.", resourceRequestId = captureRequestId
                    })));
                    var completed = completion.Success ? CerebralJson.Read<InterfaceRequest>(completion.Data) :
                        throw new InvalidOperationException("Interface capture completion failed: " + completion.Error);
                    Check(completed.Resource is { } resource && resource.Id == captured.Id && resource.Revision == captured.Revision && resource.MediaType == "image/png",
                        "owner completion resolves a committed scoped capture request into one immutable interface receipt");
                }

                var poison = new AttachmentImportBeginRequest("path-probe", lead.Id, "chat", "safe.txt", 1);
                Check(!(await Command("import.begin", new { poison.RequestId, poison.ProfileId, poison.ConversationId, poison.OriginalName, poison.Length, path = @"C:\private.txt" })).Success &&
                    !(await Command("import.begin", poison with { OriginalName = @"C:\private.txt" })).Success &&
                    !(await Command("import.begin", poison with { OriginalName = "large.png", Length = AttachmentStore.MaximumAttachmentBytes + 1L })).Success &&
                    !(await Command("import.begin", poison with { ProfileId = "missing-profile" })).Success,
                    "strict metadata rejects path imports, undeclared fields, over-limit reservations, and unknown target profiles");
                var raw = CerebralJson.Element(poison).GetRawText().Replace("\"requestId\": \"path-probe\"", "\"requestId\": \"path-probe\", \"requestId\": \"different\"", StringComparison.Ordinal);
                using (var duplicate = JsonDocument.Parse(raw))
                    Check(!(await dispatcher.ExecuteAsync(new(WorkerAttachmentTransfers.Prefix + "import.begin", duplicate.RootElement))).Success,
                        "duplicate command fields cannot override accepted upload identity");
                var chunkProbe = await Accepted("import.begin", poison);
                var tooLarge = new AttachmentImportChunkRequest(poison.RequestId, lead.Id, "chat", chunkProbe.UploadId!, 0, new byte[WorkerAttachmentTransfers.MaximumChunkBytes + 1]);
                Check(!(await Command("import.chunk", tooLarge)).Success, "oversized encoded chunks fail the transport admission bound");
                await Accepted("import.abort", new AttachmentImportUploadRequest(poison.RequestId, lead.Id, "chat", chunkProbe.UploadId!));

                var plusHeavyBytes = new byte[WorkerAttachmentTransfers.MaximumChunkBytes];
                ReadOnlySpan<byte> plusPattern = [0xfb, 0xef, 0xbe]; // base64: ++++
                for (var index = 0; index < plusHeavyBytes.Length; index++) plusHeavyBytes[index] = plusPattern[index % plusPattern.Length];
                var escapedBegin = new AttachmentImportBeginRequest("escaped-chunk", lead.Id, "chat", "escaped.bin", plusHeavyBytes.Length);
                var escapedStart = await Accepted("import.begin", escapedBegin);
                var escapedChunk = new AttachmentImportChunkRequest(escapedBegin.RequestId, lead.Id, "chat", escapedStart.UploadId!, 0, plusHeavyBytes);
                var escapedChunkJson = CerebralJson.Element(escapedChunk).GetRawText().Replace("+", "\\u002B", StringComparison.Ordinal);
                using var escapedChunkDocument = JsonDocument.Parse(escapedChunkJson);
                Check(escapedChunkDocument.RootElement.GetRawText().Length > Convert.ToBase64String(plusHeavyBytes).Length + 4096,
                    "chunk fixture exercises an equivalent Unicode-escaped base64 transport instead of only compact JSON");
                var escapedChunkResult = await dispatcher.ExecuteAsync(new(WorkerAttachmentTransfers.Prefix + "import.chunk",
                    escapedChunkDocument.RootElement));
                Check(escapedChunkResult.Success && CerebralJson.Read<AttachmentImportStatus>(escapedChunkResult.Data).ReceivedBytes == plusHeavyBytes.Length,
                    "a maximum-size binary chunk survives bounded JSON escaping without relaxing its decoded byte limit");
                await Accepted("import.commit", new AttachmentImportUploadRequest(escapedBegin.RequestId, lead.Id, "chat", escapedStart.UploadId!));

                var awaiting = await Accepted("import.begin", begin with { RequestId = "expires" });
                clock.Advance(WorkerAttachmentTransfers.IdleLifetime + TimeSpan.FromSeconds(1));
                Check((await Accepted("import.status", statusInput with { RequestId = "expires" })).State == "missing" &&
                    !(await Command("import.chunk", firstChunk with { RequestId = "expires", UploadId = awaiting.UploadId! })).Success,
                    "expired partial staging cannot be continued under an old upload generation");
                var restart = await Accepted("import.begin", begin with { RequestId = "expires" });
                Check(restart.UploadId != awaiting.UploadId, "explicit restart receives a new staging generation after expiry");
                await Accepted("import.abort", new AttachmentImportUploadRequest("expires", lead.Id, "chat", restart.UploadId!));

                var full = new List<AttachmentImportStatus>();
                for (var i = 0; i < WorkerAttachmentTransfers.MaximumUploads; i++)
                    full.Add(await Accepted("import.begin", begin with { RequestId = "capacity-" + i, Length = 0 }));
                Check(!(await Command("import.begin", begin with { RequestId = "capacity-extra", Length = 0 })).Success,
                    "four declared reservations exhaust staging even when files are empty");
                foreach (var item in full)
                    await Accepted("import.abort", new AttachmentImportUploadRequest(item.RequestId, lead.Id, "chat", item.UploadId!));
                var aborting = await Accepted("import.begin", begin with { RequestId = "aborting" });
                var abortRequest = new AttachmentImportUploadRequest("aborting", lead.Id, "chat", aborting.UploadId!);
                Check((await Accepted("import.abort", abortRequest)).State == "aborted" &&
                    (await Accepted("import.abort", abortRequest)).State == "missing" &&
                    originals.FindImportReceipt(new(lead.Id, "chat"), "aborting") is null,
                    "abort frees only incomplete staging and does not manufacture an original");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    Check(!(await Command("import.begin", begin with { RequestId = "cancelled" }, token: cancelled.Token)).Success &&
                        (await Accepted("import.status", statusInput with { RequestId = "cancelled" })).State == "missing",
                        "pre-cancelled command creates no staging state");
                }

                using (var replacement = new WorkerAttachmentTransfers(state, new AttachmentStore(new EventJournal(Path.Combine(root, "engine.db")))))
                {
                    var recovered = (AttachmentImportStatus)(await replacement.ExecuteAsync(WorkerAttachmentTransfers.Prefix + "import.status", CerebralJson.Element(statusInput), null))!;
                    Check(recovered.Attachment?.Id == attachment.Id, "new transfer manager recovers a durable import receipt after staging lifetime ends");
                }
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var racing = await Accepted("import.begin", begin with { RequestId = "status-race-" + attempt, Length = 0 });
                    var commitRace = Task.Run(() => Accepted("import.commit",
                        new AttachmentImportUploadRequest(racing.RequestId, lead.Id, "chat", racing.UploadId!)));
                    var observations = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                        Accepted("import.status", new AttachmentImportStatusRequest(racing.RequestId, lead.Id, "chat")))));
                    var settled = await commitRace;
                    Check(settled.State == "complete" && observations.All(x => x.State is "receiving" or "committing" or "complete"),
                        "concurrent receipt queries never lose an admitted upload during SQL commit and staging removal");
                }
                var escapedBytes = new byte[600_000]; Array.Fill(escapedBytes, (byte)1);
                var escapedReference = await originals.ImportAsync(new(lead.Id, "chat"), "escaped-text", "escaped.txt", new MemoryStream(escapedBytes));
                var escapedRequest = new AttachmentTextRequest(lead.Id, "chat", escapedReference.Id, 1);
                var expanded = await Command("text", escapedRequest);
                Check(!expanded.Success && expanded.Error?.Contains("transport response limit", StringComparison.Ordinal) == true &&
                    originals.ReadRange(new(lead.Id, "chat"), escapedReference.Id, 1, 0, 5).Bytes.Span.SequenceEqual(escapedBytes.AsSpan(0, 5)),
                    "whole-text JSON expansion fails explicitly before the frame limit without clipping or changing the original");
                if (OperatingSystem.IsWindows())
                {
                    var endpoint = new LocalWorkerEndpoint("cerebral-attachment-test-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
                    await using var server = new LocalWorkerPipeServer(endpoint, dispatcher, events);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await server.StartAsync(deadline.Token);
                    using var client = new LocalWorkerClient(endpoint);
                    async Task<CommandResult> Pipe(string name, object value) =>
                        await client.CommandAsync(new(WorkerAttachmentTransfers.Prefix + name, CerebralJson.Element(value)), deadline.Token);
                    var pipeBytes = new byte[WorkerAttachmentTransfers.MaximumChunkBytes + 13]; Array.Fill(pipeBytes, (byte)'z');
                    var pipeBegin = new AttachmentImportBeginRequest("pipe-import", lead.Id, "pipe-chat", "pipe.txt", pipeBytes.Length);
                    var pipeStarted = await Pipe("import.begin", pipeBegin);
                    Check(pipeStarted.Success, "actual named pipe accepts bounded import begin through shared dispatcher");
                    var pipeStatus = CerebralJson.Read<AttachmentImportStatus>(pipeStarted.Data);
                    Check((await Pipe("import.chunk", new AttachmentImportChunkRequest("pipe-import", lead.Id, "pipe-chat", pipeStatus.UploadId!, 0,
                        pipeBytes[..WorkerAttachmentTransfers.MaximumChunkBytes]))).Success &&
                        (await Pipe("import.chunk", new AttachmentImportChunkRequest("pipe-import", lead.Id, "pipe-chat", pipeStatus.UploadId!,
                        WorkerAttachmentTransfers.MaximumChunkBytes, pipeBytes[WorkerAttachmentTransfers.MaximumChunkBytes..]))).Success,
                        "actual named pipe transfers a maximum-size chunk and exact remainder within its frame budget");
                    var pipeCommitted = await Pipe("import.commit", new AttachmentImportUploadRequest("pipe-import", lead.Id, "pipe-chat", pipeStatus.UploadId!));
                    Check(pipeCommitted.Success, "actual named pipe commits the staged original");
                    var pipeRef = CerebralJson.Read<AttachmentImportStatus>(pipeCommitted.Data).Attachment!;
                    var pipeRead = await Pipe("read", new AttachmentRangeRequest(lead.Id, "pipe-chat", pipeRef.Id, pipeRef.Revision, 0, WorkerAttachmentTransfers.MaximumChunkBytes));
                    Check(pipeRead.Success && CerebralJson.Read<AttachmentRange>(pipeRead.Data).Bytes.Span.SequenceEqual(pipeBytes.AsSpan(0, WorkerAttachmentTransfers.MaximumChunkBytes)),
                        "actual named pipe resolves the maximum bounded range without placing binary bytes into events");
                    Check((await Pipe("import.status", new AttachmentImportStatusRequest("pipe-import", lead.Id, "pipe-chat"))).Success &&
                        journal.ReadPage("pipe-chat").Events.Count == 0,
                        "actual pipe receipt query is durable while the upload creates no conversation transcript");
                    using var adapterTransport = new CerebralClient(endpoint);
                    var adapterBytes = Encoding.UTF8.GetBytes("Actual client through local pipe · café.");
                    var adapterBegin = new AttachmentImportBeginRequest("adapter-pipe", lead.Id, "adapter-chat", "adapter.txt", adapterBytes.Length);
                    var adapter = new AttachmentTransferClient(adapterBegin, adapterTransport);
                    var adapterResult = await adapter.UploadAsync(new MemoryStream(adapterBytes), deadline.Token);
                    var adapterReference = adapterResult.Attachment ?? throw new InvalidOperationException("Client pipe import had no original receipt.");
                    Check(originals.ReadRange(new(lead.Id, "adapter-chat"), adapterReference.Id, adapterReference.Revision, 0, adapterBytes.Length).Bytes.Span.SequenceEqual(adapterBytes),
                        "new client adapter crosses the actual pipe and persists exact UTF-8 original bytes in scoped SQLite");
                    var adapterRetry = await adapter.UploadAsync(null, deadline.Token);
                    Check(adapterRetry.Attachment?.Id == adapterReference.Id && !adapterRetry.Uncertain,
                        "actual client reconciles its completed pipe import without reopening or resending source bytes");
                    var rejectedExpansion = await Pipe("text", escapedRequest);
                    Check(!rejectedExpansion.Success && rejectedExpansion.Error?.Contains("transport response limit", StringComparison.Ordinal) == true,
                        "actual pipe returns an actionable whole-text overflow receipt instead of disconnecting on an oversized frame");
                    await server.StopAsync(deadline.Token);
                }
                else Console.WriteLine("SKIP: actual attachment named-pipe checks require Windows current-user pipe ACLs.");
            }
            finally { events.Unsubscribe(subscription.Id); }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        Console.WriteLine($"Worker attachments: {checks} dispatcher/SQLite/stream checks passed, including actual OS pipe checks on Windows; no model inference.");
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private sealed class FixtureClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
    private sealed class NoModelExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Unexpected model execution.");
    }
}
