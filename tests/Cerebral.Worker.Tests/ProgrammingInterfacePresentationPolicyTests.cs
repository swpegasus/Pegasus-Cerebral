using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Programming;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic interface queue receipts; no presentation or SDK provider is invoked.</summary>
public static partial class ProgrammingHarnessTests
{
    static partial void InterfacePresentationPolicy(Action<bool, string> check)
    {
        const string manifest = "Desktop/bin/Release/net10.0-windows/desktop-plugin.json";
        const string fingerprint = "2050b791508d3afbfd57f00d6a811505383684ba34a67e80d2f8ac4bf4de90e7";
        const string interfaceRequestId = "96a7c26ea637483abdf26b98c353a8dc";
        Fixture Ready()
        {
            var f = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "process.run"],
                presentManifest: manifest, guardInterfacePresentation: true);
            f.FinalizationSourceRead();
            f.Dotnet("build"); f.Tool(true, "Build succeeded", 0);
            return f;
        }
        object Verified(Fixture f, string? root = null, string? path = null, string hash = fingerprint) => new
        {
            root = root ?? f.Root, pluginId = "qa.cerebral.receipt-recovery.f6",
            desktop = new { manifest = path ?? Path.Combine(f.Root, manifest),
                assembly = Path.Combine(f.Root, "Desktop/bin/Release/net10.0-windows/Plugin.dll"), packageFingerprint = hash },
            service = (object?)null, verified = true
        };
        object Pending(string requestId = interfaceRequestId, string status = "pending", string kind = "plugin",
            string hash = fingerprint) => new
        {
            requestId, status, kind, packageFingerprint = hash,
            message = "Queued for the signed-in Cerebral desktop. This is not proof that the presentation completed."
        };
        void Begin(Fixture f)
        {
            f.Model(new { action = "done", summary = "Ready for owner review" });
            check(f.IsTool && f.PendingTool == "plugin.verify", "automatic finalization begins with one exact verification");
        }
        void Verify(Fixture f, object? content = null, string provider = "cerebral.plugin-sdk",
            string? state = "verified", bool? partial = null, string? requestId = null) =>
            f.InterfaceFinalizationResult(content ?? Verified(f), provider, state, requestId, partial);
        Fixture PendingPresentation()
        {
            var f = Ready(); Begin(f); Verify(f);
            check(f.IsTool && f.PendingTool == "plugin.present" &&
                f.Command.Arguments.GetProperty("arguments").GetProperty("expectedPackageFingerprint").GetString() == fingerprint &&
                f.Step.State.GetProperty("verifiedPackageFingerprint").GetString() == fingerprint,
                "current verify binds the exact package fingerprint into presentation arguments");
            return f;
        }
        void Refused(Fixture f, string reason)
        {
            check(f.Step.Next is CompleteWorkflow { Success: false } &&
                !f.Step.State.GetProperty("pluginPresented").GetBoolean() &&
                !f.Step.State.TryGetProperty("presentationRequestId", out _),
                reason + " stops without queued credit or a second interface dispatch");
        }

        using var plugin = new ProgrammingServicePlugin();
        var editions = ((IAgentHarnessModule)plugin).Harnesses.Where(h => h.Descriptor.Id == ProgrammingAgentHarness.Id).ToArray();
        check(editions.Count(h => h.Descriptor.Version == ProgrammingInterfacePresentationHarness.Version) == 1 &&
            editions.Count(h => h.Descriptor.Version == ProgrammingFinalizationProgressHarness.Version) == 1,
            "1.3.26 is independently registered while 1.3.25 remains available");

        var queued = PendingPresentation();
        queued.InterfaceFinalizationResult(Pending(), "cerebral.interface", null, interfaceRequestId);
        check(queued.Step.Next is CompleteWorkflow { Success: true } complete &&
            queued.Step.State.GetProperty("pluginPresented").GetBoolean() &&
            queued.Step.State.GetProperty("presentationRequestId").GetString() == interfaceRequestId &&
            complete.Result.GetProperty("presentationRequestId").GetString() == interfaceRequestId &&
            complete.Result.GetProperty("summary").GetString()!.Contains("queued once", StringComparison.Ordinal),
            "distinct interface request ID joins the verified package to one pending queue receipt, without rendered credit");

        var steered = PendingPresentation();
        steered.QueueOwner("Inspect Settings.cs before reporting completion", "Keep the accepted project and checks");
        steered.InterfaceFinalizationResult(Pending(), "cerebral.interface", null, interfaceRequestId);
        check(steered.IsModel && steered.Step.State.GetProperty("ownerSteeringCount").GetInt32() == 1 &&
            steered.Messages.Contains("Inspect Settings.cs before reporting completion", StringComparison.Ordinal) &&
            steered.Step.State.GetProperty("presentationRequestId").GetString() == interfaceRequestId,
            "owner steering accepted during presentation is incorporated before terminal success, preserving queue acceptance");
        var originalPresentation = steered.Step.State.GetProperty("confirmedPresentations")[0].Clone();
        check(originalPresentation.GetProperty("requestId").GetString() == interfaceRequestId &&
            originalPresentation.GetProperty("effectId").GetString()!.StartsWith("fixture-effect-", StringComparison.Ordinal),
            "the original interface request and workflow receipt remain distinct immutable historical observations");
        steered.Model(new { action = "tool", name = "file.read", arguments = new
            { path = Path.Combine(steered.Root, "Settings.cs"), startLine = 1, lineCount = 1 } });
        check(steered.IsTool && !steered.Step.State.GetProperty("pluginVerified").GetBoolean() &&
            !steered.Step.State.GetProperty("pluginPresented").GetBoolean() &&
            JsonElement.DeepEquals(originalPresentation, steered.Step.State.GetProperty("confirmedPresentations")[0]),
            "a steered semantic read retires current proof without erasing or replaying the confirmed presentation");
        steered.Tool(true, "class Settings { }");
        steered.Model(new { action = "done", summary = "Inspected the requested settings" });
        check(steered.IsTool && steered.PendingTool == "plugin.verify", "steered completion freshly verifies the package");
        Verify(steered);
        check(steered.Step.Next is CompleteWorkflow { Success: true } steeredCompletion &&
            steered.Step.State.GetProperty("confirmedPresentations").GetArrayLength() == 1 &&
            JsonElement.DeepEquals(originalPresentation, steered.Step.State.GetProperty("confirmedPresentations")[0]) &&
            steeredCompletion.Result.GetProperty("presentationRequestId").GetString() == interfaceRequestId &&
            steeredCompletion.Result.GetProperty("summary").GetString()!.Contains("historical acceptance", StringComparison.Ordinal),
            "fresh verification of the same fingerprint reuses historical receipt evidence without another presentation or current-pending claim");

        var changedPackage = PendingPresentation();
        changedPackage.QueueOwner("Inspect the changed package before finishing");
        changedPackage.InterfaceFinalizationResult(Pending(), "cerebral.interface", null, interfaceRequestId);
        changedPackage.Model(new { action = "tool", name = "file.read", arguments = new
            { path = Path.Combine(changedPackage.Root, "Settings.cs"), startLine = 1, lineCount = 1 } });
        changedPackage.Tool(true, "class Settings { }");
        changedPackage.Model(new { action = "done", summary = "Inspected the changed package" });
        const string nextFingerprint = "e67bc1f4b818b59e852214b7aa44733e467f7236911aa623af0646eecf0f9737";
        const string nextRequest = "4a5d908a86ee4625abb348e24c883416";
        Verify(changedPackage, Verified(changedPackage, hash: nextFingerprint));
        check(changedPackage.IsTool && changedPackage.PendingTool == "plugin.present" &&
            changedPackage.Command.Arguments.GetProperty("arguments").GetProperty("expectedPackageFingerprint").GetString() == nextFingerprint,
            "a genuinely different verified fingerprint requires its own presentation request");
        changedPackage.InterfaceFinalizationResult(Pending(nextRequest, hash: nextFingerprint), "cerebral.interface", null, nextRequest);
        check(changedPackage.Step.Next is CompleteWorkflow { Success: true } &&
            changedPackage.Step.State.GetProperty("confirmedPresentations").GetArrayLength() == 2 &&
            changedPackage.Step.State.GetProperty("confirmedPresentations")[0].GetProperty("requestId").GetString() == interfaceRequestId &&
            changedPackage.Step.State.GetProperty("confirmedPresentations")[1].GetProperty("requestId").GetString() == nextRequest,
            "the new queue effect has a distinct request and preserves the earlier effect rather than replacing/replaying it");

        var reusedRequest = PendingPresentation(); reusedRequest.QueueOwner("Inspect before finishing");
        reusedRequest.InterfaceFinalizationResult(Pending(), "cerebral.interface", null, interfaceRequestId);
        reusedRequest.Model(new { action = "tool", name = "file.read", arguments = new
            { path = Path.Combine(reusedRequest.Root, "Settings.cs"), startLine = 1, lineCount = 1 } });
        reusedRequest.Tool(true, "class Settings { }");
        reusedRequest.Model(new { action = "done", summary = "Inspected" });
        Verify(reusedRequest, Verified(reusedRequest, hash: nextFingerprint));
        reusedRequest.InterfaceFinalizationResult(Pending(hash: nextFingerprint), "cerebral.interface", null, interfaceRequestId);
        check(reusedRequest.Step.Next is CompleteWorkflow { Success: false } &&
            reusedRequest.Step.State.GetProperty("confirmedPresentations").GetArrayLength() == 1 &&
            reusedRequest.Step.State.GetProperty("confirmedPresentations")[0].GetProperty("packageFingerprint").GetString() == fingerprint,
            "a new fingerprint cannot reuse an earlier interface request identity or replace the old successful receipt");

        var corruptHistory = PendingPresentation(); corruptHistory.QueueOwner("Inspect before finishing");
        corruptHistory.InterfaceFinalizationResult(Pending(), "cerebral.interface", null, interfaceRequestId);
        corruptHistory.OwnerSeedState(node => node["confirmedPresentations"]![0]!["packageFingerprint"] = null);
        corruptHistory.Model(new { action = "done", summary = "No dispatch from invalid history" });
        check(corruptHistory.Step.Next is CompleteWorkflow { Success: false },
            "malformed restored presentation history fails closed before another effect");

        var direct = Ready();
        direct.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = direct.Root } });
        Verify(direct);
        check(direct.IsModel && direct.Step.State.GetProperty("pluginVerified").GetBoolean(),
            "direct verification stores only the confirmed current fingerprint");
        direct.Model(new { action = "tool", name = "plugin.present", arguments = new
            { path = Path.Combine(direct.Root, manifest), expectedPackageFingerprint = fingerprint } });
        check(direct.IsTool && direct.PendingTool == "plugin.present", "direct presentation uses the same pinned package gate");
        direct.InterfaceFinalizationResult(Pending(), "cerebral.interface", null, interfaceRequestId);
        check(direct.Step.Next is CompleteWorkflow { Success: true } &&
            direct.Step.State.GetProperty("presentationRequestId").GetString() == interfaceRequestId,
            "direct pending queue acceptance completes without another model or interface dispatch");

        var wrongPinnedFingerprint = Ready();
        wrongPinnedFingerprint.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = wrongPinnedFingerprint.Root } });
        Verify(wrongPinnedFingerprint);
        wrongPinnedFingerprint.Model(new { action = "tool", name = "plugin.present", arguments = new
            { path = Path.Combine(wrongPinnedFingerprint.Root, manifest), expectedPackageFingerprint = new string('a', 64) } });
        check(wrongPinnedFingerprint.IsModel && !wrongPinnedFingerprint.Step.State.GetProperty("pluginPresented").GetBoolean() &&
            !wrongPinnedFingerprint.Step.State.TryGetProperty("presentationRequestId", out _),
            "wrong pinned fingerprint rejects direct presentation before an interface effect");

        var invalidated = Ready();
        invalidated.Model(new { action = "tool", name = "plugin.verify", arguments = new { root = invalidated.Root } });
        Verify(invalidated);
        invalidated.Model(new { action = "tool", name = "file.read", arguments = new
            { path = Path.Combine(invalidated.Root, "Settings.cs"), startLine = 1, lineCount = 1 } });
        check(invalidated.IsTool && !invalidated.Step.State.GetProperty("pluginVerified").GetBoolean() &&
            !invalidated.Step.State.TryGetProperty("verifiedPackageFingerprint", out _),
            "semantic source read invalidates verified package proof before dispatch");
        invalidated.Tool(true, "class Settings { }");
        invalidated.Model(new { action = "tool", name = "plugin.present", arguments = new
            { path = Path.Combine(invalidated.Root, manifest), expectedPackageFingerprint = fingerprint } });
        check(invalidated.IsModel && !invalidated.Step.State.GetProperty("pluginPresented").GetBoolean(),
            "presentation after a semantic read requires fresh verification and dispatches no interface effect");

        var wrongVerifyProvider = Ready(); Begin(wrongVerifyProvider);
        Verify(wrongVerifyProvider, provider: "deterministic-fixture"); Refused(wrongVerifyProvider, "wrong verify provider");
        var wrongVerifyState = Ready(); Begin(wrongVerifyState);
        Verify(wrongVerifyState, state: "contract"); Refused(wrongVerifyState, "wrong verify state");
        var wrongVerifyRoot = Ready(); Begin(wrongVerifyRoot);
        Verify(wrongVerifyRoot, Verified(wrongVerifyRoot, root: Path.Combine(wrongVerifyRoot.Root, "other")));
        Refused(wrongVerifyRoot, "wrong verified root");
        var wrongVerifyManifest = Ready(); Begin(wrongVerifyManifest);
        Verify(wrongVerifyManifest, Verified(wrongVerifyManifest, path: Path.Combine(wrongVerifyManifest.Root, "other", "desktop-plugin.json")));
        Refused(wrongVerifyManifest, "wrong verified manifest");
        var malformedVerify = Ready(); Begin(malformedVerify);
        malformedVerify.InterfaceFinalizationRaw("{\"root\":", "cerebral.plugin-sdk", "verified", null);
        Refused(malformedVerify, "malformed verify content");

        var wrongProvider = PendingPresentation();
        wrongProvider.InterfaceFinalizationResult(Pending(), "cerebral.plugin-sdk", null, interfaceRequestId);
        Refused(wrongProvider, "wrong interface provider");
        var wrongRequest = PendingPresentation();
        wrongRequest.InterfaceFinalizationResult(Pending(), "cerebral.interface", null, new string('a', 32));
        Refused(wrongRequest, "content and evidence request IDs disagree");
        var wrongFingerprint = PendingPresentation();
        wrongFingerprint.InterfaceFinalizationResult(Pending(hash: new string('a', 64)), "cerebral.interface", null, interfaceRequestId);
        Refused(wrongFingerprint, "queued package fingerprint differs from verified package");
        var claimed = PendingPresentation();
        claimed.InterfaceFinalizationResult(Pending(status: "claimed"), "cerebral.interface", null, interfaceRequestId);
        Refused(claimed, "claimed status is not pending queue acceptance");
        var wrongKind = PendingPresentation();
        wrongKind.InterfaceFinalizationResult(Pending(kind: "file"), "cerebral.interface", null, interfaceRequestId);
        Refused(wrongKind, "non-plugin request kind");
        var malformedPresent = PendingPresentation();
        malformedPresent.InterfaceFinalizationRaw("{\"requestId\":", "cerebral.interface", null, interfaceRequestId);
        Refused(malformedPresent, "malformed presentation content");
        var duplicatePresent = PendingPresentation();
        var presentJson = JsonSerializer.Serialize(Pending(), CerebralJson.Options);
        duplicatePresent.InterfaceFinalizationRaw(presentJson.Insert(presentJson.LastIndexOf('}'), ",\"status\":\"pending\""),
            "cerebral.interface", null, interfaceRequestId);
        Refused(duplicatePresent, "duplicate presentation scalar");
        var extraPresent = PendingPresentation();
        extraPresent.InterfaceFinalizationRaw(presentJson.Insert(presentJson.LastIndexOf('}'), ",\"installed\":true"),
            "cerebral.interface", null, interfaceRequestId);
        Refused(extraPresent, "unknown presentation field");
        var partial = PendingPresentation();
        partial.InterfaceFinalizationResult(Pending(), "cerebral.interface", null, interfaceRequestId, partial: true);
        Refused(partial, "partial interface outcome");
        var truncated = PendingPresentation();
        truncated.InterfaceFinalizationRaw(JsonSerializer.Serialize(Pending(), CerebralJson.Options),
            "cerebral.interface", null, interfaceRequestId, truncated: true);
        Refused(truncated, "truncated interface outcome");
        var followUp = PendingPresentation();
        followUp.InterfaceFinalizationRaw(JsonSerializer.Serialize(Pending(), CerebralJson.Options),
            "cerebral.interface", null, interfaceRequestId, followUpRequired: true);
        Refused(followUp, "provider follow-up request");
        var unknown = PendingPresentation(); unknown.Uncertain();
        Refused(unknown, "unknown interface outcome");
    }

    private sealed partial class Fixture
    {
        public void InterfaceFinalizationResult(object content, string provider, string? state, string? requestId,
            bool? partial = null) => InterfaceFinalizationRaw(JsonSerializer.Serialize(content, CerebralJson.Options),
                provider, state, requestId, partial);

        public void InterfaceFinalizationRaw(string content, string provider, string? state, string? requestId,
            bool? partial = null, bool truncated = false, bool followUpRequired = false) =>
            Result(WorkflowEffectState.Succeeded, CerebralJson.Element(new
        {
            name = PendingTool, content, truncated, followUpRequired,
            evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, provider, RequestId: requestId,
                State: state, Partial: partial)
        }));
    }
}
