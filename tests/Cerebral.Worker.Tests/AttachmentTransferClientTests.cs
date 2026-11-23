using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic transport fixtures. Separate transfer tests exercise actual SQLite and OS pipes.</summary>
public static class AttachmentTransferClientTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        async Task Reject(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException)
            { checks++; return; }
            throw new InvalidOperationException("Invalid or uncertain transfer unexpectedly succeeded.");
        }
        var bytes = Enumerable.Range(0, AttachmentTransferClient.MaximumChunkBytes + 13).Select(x => (byte)(x % 127)).ToArray();
        var request = new AttachmentImportBeginRequest("client-import", "lead", "chat", "source.txt", bytes.Length);
        var service = new Fixture(request);
        var client = new AttachmentTransferClient(request, service.Send);
        using var source = new MemoryStream(bytes);
        var complete = await client.UploadAsync(source);
        Check(complete.Attachment is not null && !complete.Busy && !complete.Uncertain, "Successful transfer returns a settled immutable receipt.");
        Check(service.Bytes.SequenceEqual(bytes) && service.Chunks.SequenceEqual(new[] { AttachmentTransferClient.MaximumChunkBytes, 13 }),
            "Client transfers exact original bytes in bounded chunks.");
        Check(service.Calls.All(x => x.ProfileId is null && x.Arguments.GetProperty("profileId").GetString() == "lead" &&
            x.Arguments.GetProperty("conversationId").GetString() == "chat" && x.Arguments.GetProperty("requestId").GetString() == request.RequestId),
            "Captured request and scope stay exact through every command without impersonating an agent.");
        var before = service.Calls.Count;
        await client.UploadAsync(null);
        Check(service.Calls.Count == before + 1 && service.Calls[^1].Operation.EndsWith(".status", StringComparison.Ordinal),
            "Completed import reconciles before source access and does not retransmit bytes.");
        service.Override = result => result with { RequestId = "other" };
        await Reject(() => client.RefreshAsync());
        Check(client.Snapshot.Attachment?.Id == complete.Attachment!.Id, "Malformed receipt cannot replace the confirmed reference.");
        service.Override = null;

        var lost = new Fixture(request with { RequestId = "lost-commit" }) { LoseCommit = true };
        var recovering = new AttachmentTransferClient(lost.Request, lost.Send);
        await Reject(() => recovering.UploadAsync(new MemoryStream(bytes)));
        Check(recovering.Snapshot.Uncertain && !recovering.Snapshot.Busy, "Lost commit reply retains uncertain identity.");
        var commits = lost.Calls.Count(x => x.Operation.EndsWith(".commit", StringComparison.Ordinal));
        var recovered = await recovering.UploadAsync(null);
        Check(recovered.Attachment is not null && !recovered.Uncertain && lost.Calls.Count(x => x.Operation.EndsWith(".commit", StringComparison.Ordinal)) == commits,
            "Lost commit is resolved by read-only receipt without reopening source or replaying commit.");

        var lostBegin = new Fixture(request with { RequestId = "lost-begin" }) { LoseBegin = true };
        var retrying = new AttachmentTransferClient(lostBegin.Request, lostBegin.Send);
        await Reject(() => retrying.UploadAsync(new MemoryStream(bytes)));
        lostBegin.HideStatus = true; lostBegin.RejectBegin = true;
        await Reject(() => retrying.UploadAsync(new MemoryStream(bytes)));
        Check(retrying.Snapshot.Uncertain && retrying.Snapshot.Request == lostBegin.Request,
            "Explicit retry rejection cannot erase earlier uncertainty or change the stable request.");
        await Reject(() => retrying.AbortAsync());
        Check(retrying.Snapshot.Uncertain, "Missing status alone cannot discard an uncertain prior begin.");
        lostBegin.HideStatus = false; lostBegin.RejectBegin = false;
        await retrying.UploadAsync(new MemoryStream(bytes));
        Check(retrying.Snapshot.Attachment is not null && !retrying.Snapshot.Uncertain, "Matching retained staging permits an explicit safe resume.");

        var malformed = new Fixture(request with { RequestId = "bad-chunk" }) { BadChunkReceipt = true };
        var invalid = new AttachmentTransferClient(malformed.Request, malformed.Send);
        await Reject(() => invalid.UploadAsync(new MemoryStream(bytes)));
        Check(invalid.Snapshot.Uncertain && malformed.Calls.All(x => !x.Operation.EndsWith(".commit", StringComparison.Ordinal)),
            "Incorrect chunk acknowledgement prevents commit and retains uncertainty.");

        var held = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new AttachmentTransferClient(request with { RequestId = "pending" }, (_, _) => held.Task);
        var waiting = pending.RefreshAsync();
        await Reject(() => pending.RefreshAsync());
        Check(pending.Snapshot.Busy, "A pending transport has one serialized owner.");
        held.SetResult(CommandResult.Ok(new AttachmentImportStatus("pending", "missing", null, 0, null)));
        await waiting;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var calls = service.Calls.Count;
        await Reject(() => client.RefreshAsync(cancelled.Token));
        Check(service.Calls.Count == calls, "Pre-cancelled refresh never dispatches.");

        Console.WriteLine($"Attachment client: {checks} deterministic transport checks passed; no network or model inference.");
    }

    private sealed class Fixture(AttachmentImportBeginRequest request)
    {
        public AttachmentImportBeginRequest Request { get; } = request;
        public List<CommandRequest> Calls { get; } = [];
        public List<byte> Bytes { get; } = [];
        public List<int> Chunks { get; } = [];
        public bool LoseBegin, LoseCommit, RejectBegin, HideStatus, BadChunkReceipt;
        public Func<AttachmentImportStatus, AttachmentImportStatus>? Override;
        private readonly string upload = Guid.NewGuid().ToString("N");
        private readonly string original = Guid.NewGuid().ToString("N");
        private bool begun, committed;
        public Task<CommandResult> Send(CommandRequest command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(command);
            var operation = command.Operation.Split('.').Last();
            switch (operation)
            {
                case "status": break;
                case "begin":
                    if (RejectBegin) return Task.FromResult(CommandResult.Fail("Permission changed."));
                    begun = true;
                    if (LoseBegin) { LoseBegin = false; throw new IOException("Lost begin response."); }
                    break;
                case "chunk":
                    var chunk = CerebralJson.Read<AttachmentImportChunkRequest>(command.Arguments);
                    if (chunk.Offset != Bytes.Count || chunk.UploadId != upload) throw new InvalidOperationException("Wrong byte offset or generation.");
                    Bytes.AddRange(chunk.Bytes); Chunks.Add(chunk.Bytes.Length);
                    break;
                case "commit":
                    if (Bytes.Count != Request.Length) throw new InvalidOperationException("Missing bytes.");
                    committed = true;
                    if (LoseCommit) { LoseCommit = false; throw new IOException("Lost commit response."); }
                    break;
                case "abort": begun = false; Bytes.Clear(); break;
                default: throw new InvalidOperationException("Unexpected transfer command.");
            }
            AttachmentImportStatus status = committed
                ? new(Request.RequestId, "complete", null, Request.Length, Request.Length, new AttachmentReference(original, 1, "text/plain", Request.OriginalName, Request.Length))
                : begun && !(operation == "status" && HideStatus) ? new(Request.RequestId, "receiving", upload, Bytes.Count, Request.Length)
                : new(Request.RequestId, "missing", null, 0, null);
            if (operation == "chunk" && BadChunkReceipt) status = status with { ReceivedBytes = status.ReceivedBytes - 1 };
            return Task.FromResult(CommandResult.Ok(Override?.Invoke(status) ?? status));
        }
    }
}
