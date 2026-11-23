using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static class ConfigurationRestoreClientTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException)
            { checks++; return; }
            throw new Exception("FAIL: " + label);
        }
        var request = new ConfigurationRestoreApplyRequest(Guid.NewGuid().ToString("N"), 4, true);
        var result = new ConfigurationRestoreResult(5, ["agent"], [], PromptRoles: ["system"]);
        var receipt = new ConfigurationRestoreReceipt(request.PreviewId, 4, true, DateTimeOffset.UtcNow, result, "failed");
        var committed = new ConfigurationRestoreStatus(request.PreviewId, "committed", receipt);
        var calls = new List<(string Operation, string Input)>();
        CommandResult statusReply = CommandResult.Ok(committed);
        var client = new ConfigurationRestoreClient(request, (operation, args, _) =>
        {
            calls.Add((operation, JsonSerializer.Serialize(args, CerebralJson.Options)));
            if (operation == "configuration.restore.apply") throw new IOException("fixture lost apply response");
            return Task.FromResult(statusReply);
        });
        await Reject(() => client.ApplyOnceAsync(), "lost apply is reported");
        Check(client.Snapshot is { Dispatched: true, Uncertain: true, Result: null }, "lost apply retains immutable reviewed request");
        await Reject(() => client.ApplyOnceAsync(), "unconfirmed apply cannot be automatically replayed");
        Check(calls.Count == 1, "second apply never reached the transport");
        statusReply = CommandResult.Ok(new ConfigurationRestoreStatus(request.PreviewId, "unknown", null));
        await client.RefreshAsync();
        Check(client.Snapshot.Uncertain && client.Snapshot.Result is null, "unknown receipt does not authorize another apply");
        statusReply = CommandResult.Fail("fixture no access");
        await Reject(() => client.RefreshAsync(), "status permission rejection is visible");
        Check(client.Snapshot.Uncertain, "a rejected status lookup cannot prove original nonacceptance");
        statusReply = CommandResult.Ok(committed with { Receipt = receipt with { ExpectedHostRevision = 7 } });
        await Reject(() => client.RefreshAsync(), "mismatched receipt cannot clear uncertainty");
        Check(client.Snapshot.Uncertain, "mismatched accepted fields preserve the original request");
        statusReply = CommandResult.Ok(committed);
        await client.RefreshAsync();
        Check(!client.Snapshot.Uncertain && client.Snapshot.Result?.HostRevision == 5 &&
            client.Snapshot.Receipt?.NotificationOutcome == "failed", "matching committed receipt confirms settings despite notification failure");
        var savedCalls = calls.Count;
        var exact = await client.ApplyOnceAsync();
        Check(exact.HostRevision == 5 && calls.Count == savedCalls, "already confirmed apply returns local receipt without resending");
        statusReply = CommandResult.Ok(committed with { Receipt = receipt with { Result = result with { AgentIds = ["different"] } } });
        await Reject(() => client.RefreshAsync(), "later status cannot replace an already confirmed result");
        Check(client.Snapshot.Result!.AgentIds.Single() == "agent" && !client.Snapshot.Uncertain, "bad later status leaves confirmation intact");
        statusReply = CommandResult.Ok(new ConfigurationRestoreStatus(request.PreviewId, "unknown", null));
        await client.RefreshAsync();
        Check(client.Snapshot.Result!.HostRevision == 5 && !client.Snapshot.Uncertain, "retention expiry cannot erase a previously confirmed result");
        Check(calls.Where(x => x.Operation == "configuration.restore.apply").Single().Input == JsonSerializer.Serialize(request, CerebralJson.Options),
            "dispatch preserved the exact approved fields");
        Check(calls.Where(x => x.Operation == "configuration.restore.status").All(x =>
            x.Input == JsonSerializer.Serialize(new ConfigurationRestoreStatusRequest(request.PreviewId), CerebralJson.Options)),
            "reconciliation only sends the exact preview ID");

        var blocked = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentCalls = 0;
        var gated = new ConfigurationRestoreClient(request, (_, _, _) => { concurrentCalls++; return blocked.Task; });
        var pending = gated.ApplyOnceAsync();
        try
        {
            await Reject(() => gated.RefreshAsync(), "status cannot race a pending apply");
            await Reject(() => gated.ApplyOnceAsync(), "a concurrent apply is rejected before dispatch");
            Check(concurrentCalls == 1 && gated.Snapshot.Uncertain, "pending apply retains one request under concurrency");
        }
        finally { blocked.TrySetResult(CommandResult.Ok(result)); await pending; }
        var copy = gated.Snapshot.Result!;
        if (copy.AgentIds is string[] mutable) mutable[0] = "changed by caller";
        Check(gated.Snapshot.Result!.AgentIds.Single() == "agent", "public snapshots cannot mutate the retained result");
        var denied = new ConfigurationRestoreClient(request, (_, _, _) => Task.FromResult(CommandResult.Fail("fixture rejected")));
        await Reject(() => denied.ApplyOnceAsync(), "explicit first rejection is surfaced");
        Check(!denied.Snapshot.Uncertain && denied.Snapshot.Result is null, "explicit first rejection is distinct from lost response");
        var neverCalled = 0;
        var cancelled = new ConfigurationRestoreClient(request, (_, _, _) => { neverCalled++; return Task.FromResult(CommandResult.Ok(result)); });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Reject(() => cancelled.ApplyOnceAsync(cancellation.Token), "predispatch cancellation is honored");
        Check(!cancelled.Snapshot.Dispatched && neverCalled == 0, "cancel before dispatch does not create unknown outcome");
        using var afterDispatchCancellation = new CancellationTokenSource();
        var timedOut = new ConfigurationRestoreClient(request, async (operation, _, ct) =>
        {
            if (operation == "configuration.restore.apply") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return CommandResult.Ok(committed);
        });
        var interrupted = timedOut.ApplyOnceAsync(afterDispatchCancellation.Token);
        afterDispatchCancellation.Cancel();
        await Reject(() => interrupted, "cancellation after dispatch interrupts waiting for the reply");
        Check(timedOut.Snapshot is { Dispatched: true, Uncertain: true }, "postdispatch timeout preserves the uncertain request");
        await timedOut.RefreshAsync();
        Check(timedOut.Snapshot.Result?.HostRevision == 5 && !timedOut.Snapshot.Uncertain,
            "a new bounded status call can confirm a request whose response timed out");
        var badResponse = new ConfigurationRestoreClient(request, (_, _, _) => Task.FromResult(CommandResult.Ok(result with { HostRevision = 99 })));
        await Reject(() => badResponse.ApplyOnceAsync(), "malformed success cannot be adopted");
        Check(badResponse.Snapshot.Uncertain, "untrusted success response retains request for authoritative status");
        Console.WriteLine($"Configuration restore client: {checks} deterministic delegate assertions passed. No network or UI interaction.");
    }
}
