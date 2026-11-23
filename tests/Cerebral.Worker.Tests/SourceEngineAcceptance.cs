using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Engine.Activation;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in owner-pipe canary for the existing Source app. Never starts a Worker,
/// reads credentials or changes models. The explicit coordination/cache submit verbs admit only
/// their bounded retained test chats. Candidate promotion remains explicit.</summary>
internal static class SourceEngineAcceptance
{
    public static async Task RunAsync(string dataDirectory, string action, long expectedEpoch)
    {
        var source = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pegasus", "Cerebral", "Source"));
        if (!OperatingSystem.IsWindows() || !string.Equals(Path.GetFullPath(dataDirectory), source, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This canary is restricted to the existing default Source data root.");
        if (action is not ("status" or "pause" or "resume" or "activate" or "promote" or "rollback" or "coordination-inspect" or "coordination-submit" or "cache-inspect" or "cache-submit" or "audit-inspect" or "audit-submit" or "audit-status" or "asset-inspect" or "harness-submit" or "harness-inspect"))
            throw new ArgumentException("Use status, activate, promote, rollback, coordination-inspect, coordination-submit, cache-inspect, cache-submit, audit-inspect or audit-submit.");
        var path = Path.Combine(source, "Desktop", "connection.json");
        if (new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > 16_384)
            throw new InvalidOperationException("Unexpected Source connection declaration.");
        using var declaration = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = declaration.RootElement;
        if (root.GetProperty("transport").GetString() != "pipe" ||
            !string.Equals(Path.GetFullPath(root.GetProperty("localDataDirectory").GetString()!), source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Source declaration is not its local owner pipe.");
        var endpoint = root.GetProperty("localEndpoint").Deserialize<LocalWorkerEndpoint>(CerebralJson.Options)
            ?? throw new InvalidOperationException("Missing local endpoint.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var client = new CerebralClient(endpoint);
        var health = await client.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
        if (!health.LocalRun || !string.Equals(Path.GetFullPath(health.DataDirectory), source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The endpoint does not own this Source data root.");
        var status = await client.GetAsync<EngineStatus>("engine.status", cancellationToken: deadline.Token);
        if (action.StartsWith("harness-", StringComparison.Ordinal))
        {
            if (status.Epoch != expectedEpoch) throw new InvalidOperationException("The expected Source engine epoch changed.");
            await SourceHarnessCoordinationAcceptance.RunAsync(client, status, action == "harness-submit", deadline.Token);
            return;
        }
        if (action == "asset-inspect")
        {
            if (status.Epoch != expectedEpoch || status.Active?.Slot != "A" || status.Error is not null)
                throw new InvalidOperationException("The expected healthy Source engine changed.");
            await SourceCortexAssetAcceptance.RunAsync(client, deadline.Token);
            return;
        }
        if (action == "audit-status")
        {
            if (status.Epoch != expectedEpoch) throw new InvalidOperationException("The expected Source engine epoch changed.");
            var auditor = await client.GetAsync<JsonElement>("laya.status", cancellationToken: deadline.Token);
            var profiles = await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: deadline.Token);
            var lead = profiles.Single(p => p.Id == "7980ce1190f648a8b832b7516a67adbc");
            if (lead.ModelWorkerId != "worker-3f6ab53f3a5a" || lead.ModelId != "source-qwen35-9b" || lead.WorkerId != "local")
                throw new InvalidOperationException("The verified Source chat route is no longer GB10.");
            Console.WriteLine(JsonSerializer.Serialize(new { status.Epoch, status.Active, status.Paused, status.CandidatePending,
                status.ActiveEvaluations, status.Error, lead = new { lead.Id, lead.ModelId, lead.WorkerId, lead.ModelWorkerId }, auditor }, CerebralJson.Options));
            return;
        }
        if (action.StartsWith("audit-", StringComparison.Ordinal))
        {
            if (status.Epoch != expectedEpoch) throw new InvalidOperationException("The expected Source engine epoch changed.");
            await SourceLayaAcceptance.RunAsync(client, status, action == "audit-submit", deadline.Token);
            return;
        }
        if (action.StartsWith("cache-", StringComparison.Ordinal))
        {
            if (status.Epoch != expectedEpoch) throw new InvalidOperationException("The expected Source engine epoch changed.");
            await SourceCacheDiagnosticsAcceptance.RunAsync(client, status, action == "cache-submit", deadline.Token);
            return;
        }
        if (action.StartsWith("coordination-", StringComparison.Ordinal))
        {
            if (status.Epoch != expectedEpoch) throw new InvalidOperationException("The expected Source engine epoch changed.");
            await SourceCoordinationAcceptance.RunAsync(client, status, action == "coordination-submit", deadline.Token);
            return;
        }
        if (action is "pause" or "resume")
        {
            if (status.Epoch != expectedEpoch || status.Error is not null || status.CandidatePending ||
                status.Active?.Slot != status.KnownGoodSlot || status.Active?.Id != "pegasus.default-engine")
                throw new InvalidOperationException("The expected healthy known-good Source engine changed.");
            status = await client.GetAsync<EngineStatus>("engine." + action, new { expectedEpoch }, cancellationToken: deadline.Token);
        }
        else if (action != "status")
        {
            if (status.Epoch != expectedEpoch || status.ActiveEvaluations != 0 || status.Error is not null)
                throw new InvalidOperationException("The expected healthy engine generation is no longer quiescent.");
            if (action == "activate")
            {
                var candidate = status.Active?.Slot == "A" ? "B" : "A";
                if (status.CandidatePending || status.Active?.Slot != status.KnownGoodSlot ||
                    !status.Slots.Any(x => x.Slot == candidate && x.Available && x.BuildAllowed && x.Package?.Id == "pegasus.default-engine"))
                    throw new InvalidOperationException("Expected a known-good active engine and an admitted inactive default candidate.");
                await client.GetAsync<EngineStatus>("engine.pause", new { expectedEpoch }, cancellationToken: deadline.Token);
                status = await client.GetAsync<EngineStatus>("engine.activate", new { slot = candidate, expectedEpoch }, cancellationToken: deadline.Token);
                status = await client.GetAsync<EngineStatus>("engine.resume", new { expectedEpoch = status.Epoch }, cancellationToken: deadline.Token);
            }
            else
            {
                if (!status.CandidatePending || status.Active?.Slot == status.KnownGoodSlot || status.KnownGoodSlot is not ("A" or "B"))
                    throw new InvalidOperationException("No expected candidate with a protected known-good fallback.");
                if (action == "rollback")
                    await client.GetAsync<EngineStatus>("engine.pause", new { expectedEpoch }, cancellationToken: deadline.Token);
                status = await client.GetAsync<EngineStatus>("engine." + action, new { expectedEpoch }, cancellationToken: deadline.Token);
                if (action == "rollback")
                    status = await client.GetAsync<EngineStatus>("engine.resume", new { expectedEpoch = status.Epoch }, cancellationToken: deadline.Token);
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            action, status.Epoch, status.Paused, status.CandidatePending, status.CommittedDecisions,
            status.ActiveEvaluations, activeSlot = status.Active?.Slot, status.KnownGoodSlot, status.Error,
            engine = status.Active?.Id, status.Recovery
        }, CerebralJson.Options));
    }
}
