using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Integrator;

/// <summary>Deterministic stage-caller checks with synthetic dispatch; no service, saved configuration or WPF window.</summary>
internal static class IntegratorServicePackageStageChecks
{
    public static async Task<int> VerifyAsync()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            count++;
        }
        var descriptor = CerebralJson.Element(new
        {
            id = "service-packages.stage", version = 1, ownerOnly = true,
            inputSchema = new { properties = new { expectedId = new { type = new[] { "string", "null" }, minLength = 1, maxLength = 100 } } }
        });
        var staged = CommandResult.Ok(new { id = "fixture.guarded", revision = 8, stagedGeneration = "1.0.0-fixture" });
        var calls = new List<CommandRequest>();
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            calls.Add(request); token.ThrowIfCancellationRequested();
            return Task.FromResult(request.Operation == "sdk.describe" ? CommandResult.Ok(descriptor) : staged);
        }
        var operation = new ServicePackageStageOperation("fixture.guarded", "worker-local/service-plugin.json", 7, Dispatch, () => { });
        var result = await operation.StageOnceAsync(CancellationToken.None);
        Check(result.Success && operation.Outcome == ServicePackageStageOutcome.Confirmed, "an advertised identity-aware caller confirms its exact staged result");
        Check(calls.Select(x => x.Operation).SequenceEqual(["sdk.describe", "service-packages.stage"]) &&
            calls.All(x => x.ProfileId is null), "one owner capability read precedes one owner mutation");
        var request = calls[1].Arguments;
        Check(request.GetProperty("expectedId").GetString() == "fixture.guarded" &&
            request.GetProperty("expectedRevision").GetInt64() == 7 &&
            request.GetProperty("manifestPath").GetString() == "worker-local/service-plugin.json",
            "the stage payload retains reviewed ID/revision and the selected Worker's path");
        await Reject(() => operation.StageOnceAsync(CancellationToken.None));
        Check(calls.Count == 2, "a consumed attempt never dispatches a second mutation");

        var old = JsonNode.Parse(descriptor.GetRawText())!.AsObject();
        old["inputSchema"]!["properties"]!.AsObject().Remove("expectedId");
        var mutations = 0;
        operation = new("fixture.guarded", "service-plugin.json", 7, (command, _) =>
        {
            if (command.Operation == "service-packages.stage") mutations++;
            return Task.FromResult(CommandResult.Ok(CerebralJson.Element(old)));
        }, () => { });
        await Reject(() => operation.StageOnceAsync(CancellationToken.None));
        Check(!operation.WasDispatched && mutations == 0 && operation.Outcome == ServicePackageStageOutcome.NotSubmitted,
            "an old host that can ignore extra fields is refused before stage dispatch");
        foreach (var malformed in new[]
        {
            CerebralJson.Element(new { id = "service-packages.stage", version = "1", ownerOnly = true }),
            CerebralJson.Element(new { id = "other", version = 1, ownerOnly = true }),
            CerebralJson.Element(new { id = "service-packages.stage", version = 2, ownerOnly = true }),
            CerebralJson.Element(new { id = "service-packages.stage", version = 1, ownerOnly = false })
        })
            Check(!ServicePackageStageOperation.SupportsReviewedIdentity(malformed), "unsupported descriptor shape/version/owner is rejected");

        mutations = 0;
        operation = new("fixture.guarded", "service-plugin.json", 7, (command, _) =>
        {
            if (command.Operation == "sdk.describe") return Task.FromResult(CommandResult.Ok(descriptor));
            if (command.Operation == "service-packages.inspect") return Task.FromResult(staged);
            mutations++;
            return Task.FromException<CommandResult>(new IOException("Synthetic reply lost after dispatch."));
        }, () => { });
        await Reject(() => operation.StageOnceAsync(CancellationToken.None));
        Check(operation.WasDispatched && operation.Outcome == ServicePackageStageOutcome.Unknown && mutations == 1,
            "a lost reply retains one uncertain dispatched attempt");
        await Reject(() => operation.StageOnceAsync(CancellationToken.None));
        Check(mutations == 1, "uncertainty cannot replay the mutation");
        var current = await operation.InspectCurrentAsync(CancellationToken.None);
        Check(current.Success && operation.Outcome == ServicePackageStageOutcome.Unknown && mutations == 1,
            "current-state readback neither attributes the lost request nor retries it");

        mutations = 0;
        operation = new("fixture.guarded", "service-plugin.json", 7, (command, _) =>
        {
            if (command.Operation == "sdk.describe") return Task.FromResult(CommandResult.Ok(descriptor));
            mutations++;
            return Task.FromResult(CommandResult.Fail("Synthetic revision conflict."));
        }, () => { });
        result = await operation.StageOnceAsync(CancellationToken.None);
        Check(!result.Success && operation.Outcome == ServicePackageStageOutcome.Unknown && mutations == 1,
            "a generic failure reply cannot prove that stage did not commit");
        await Reject(() => operation.StageOnceAsync(CancellationToken.None));
        Check(mutations == 1, "a failed reply does not permit a second dispatch");

        mutations = 0;
        operation = new("fixture.guarded", "service-plugin.json", 7, (command, _) =>
        {
            if (command.Operation == "sdk.describe") return Task.FromResult(CommandResult.Ok(descriptor));
            mutations++;
            return Task.FromResult(CommandResult.Ok(new { id = "fixture.other", revision = 8, stagedGeneration = "other" }));
        }, () => { });
        await Reject(() => operation.StageOnceAsync(CancellationToken.None));
        Check(operation.Outcome == ServicePackageStageOutcome.Unknown && mutations == 1,
            "a success reply with another identity is not a confirmed stage");

        var epoch = 1;
        mutations = 0;
        operation = new("fixture.guarded", "service-plugin.json", 7, (command, _) =>
        {
            if (command.Operation == "sdk.describe") { epoch++; return Task.FromResult(CommandResult.Ok(descriptor)); }
            mutations++; return Task.FromResult(staged);
        }, () => { if (epoch != 1) throw new InvalidOperationException("Synthetic connection changed."); });
        await Reject(() => operation.StageOnceAsync(CancellationToken.None));
        Check(!operation.WasDispatched && mutations == 0, "connection replacement during capability read prevents mutation");

        epoch = 1; mutations = 0;
        operation = new("fixture.guarded", "service-plugin.json", 7, (command, _) =>
        {
            if (command.Operation == "sdk.describe") return Task.FromResult(CommandResult.Ok(descriptor));
            mutations++; epoch++; return Task.FromResult(staged);
        }, () => { if (epoch != 1) throw new InvalidOperationException("Synthetic connection changed."); });
        await Reject(() => operation.StageOnceAsync(CancellationToken.None));
        Check(operation.WasDispatched && operation.Outcome == ServicePackageStageOutcome.Unknown && mutations == 1,
            "connection replacement after dispatch cannot adopt the outcome on another Worker");
        await Reject(() => operation.InspectCurrentAsync(CancellationToken.None));
        Check(mutations == 1, "a stale connection guard prevents readback on another owner");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel(); calls.Clear();
        operation = new("fixture.guarded", "service-plugin.json", 7, Dispatch, () => { });
        await Reject(() => operation.StageOnceAsync(cancellation.Token));
        Check(!operation.WasDispatched && calls.Count == 0, "pre-dispatch cancellation has no effect");
        return count;
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected a bounded stage-caller rejection.");
    }
}
