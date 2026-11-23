using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Context;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.Programming;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

internal sealed record WorkerContextContributionCheckResult(int SnapshotChecks, int HostChecks, int ProjectionChecks, int RuntimeChecks)
{
    internal int Total => SnapshotChecks + HostChecks + ProjectionChecks + RuntimeChecks;
}

/// <summary>Deterministic fake package/provider boundaries over actual private SQLite. No package Initialize,
/// Worker process, scheduler, model runtime, provider network or inference is used.</summary>
internal static class WorkerContextContributionTests
{
    private const string Alpha = "fixture.alpha";
    private const string Beta = "fixture.beta";
    private const string DefinitionId = Alpha + ":workflow";
    private const string AlphaBody = "ALPHA MODEL-ONLY FIXTURE BODY";
    private const string BetaBody = "BETA PRESERVED FIXTURE BODY";
    private static readonly JsonSerializerOptions Compact = new(CerebralJson.Options) { WriteIndented = false };

    internal static async Task<WorkerContextContributionCheckResult> RunAsync(string fixtureParent)
    {
        if (!Path.IsPathFullyQualified(fixtureParent)) throw new ArgumentException("Supply the exact absolute fixture parent.");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureParent));
        Directory.CreateDirectory(parent);
        ScopedFilePolicy.RejectLinks(parent);
        var root = Path.Combine(parent, "generic-context-" + Guid.NewGuid().ToString("N"));
        RequireOwned(root, parent);
        Directory.CreateDirectory(root);
        var snapshot = new Checks("snapshot");
        var host = new Checks("host");
        var projection = new Checks("projection");
        var runtime = new Checks("runtime");
        try
        {
            SnapshotChecks(snapshot);
            await HostChecks(Path.Combine(root, "host"), host);
            await ReceiverHostChecks(Path.Combine(root, "receiver"), host);
            ProjectionChecks(Path.Combine(root, "projection"), projection);
            await RuntimeChecks(Path.Combine(root, "runtime"), runtime);
            return new(snapshot.Count, host.Count, projection.Count, runtime.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            RequireOwned(root, parent);
            ScopedFilePolicy.RejectLinks(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void SnapshotChecks(Checks checks)
    {
        var kinds = new List<string> { "fixture.alpha.record" };
        var fields = new List<string> { "fixtureLegacy" };
        var legacy = new List<WorkflowContextLegacyFormat> { new(4, fields) };
        var descriptor = new WorkflowContextContributionDescriptor(1, Alpha + ":contribution",
            new(Alpha + ":resolve", 1, "Generic fixture context", kinds, 4, 8192),
            WorkflowContextHistoryProjection.ReferenceOnly, legacy);
        var references = new List<ContextProviderReference>
            { new(Alpha + ":record", kinds[0], "source-A", 3, BeforeCurrentInput: true) };
        var request = new ContextProviderRequest(descriptor.Provider.Id, 1, references, 4096);
        WorkerContextContributionAdmission frozen;
        using (var payload = JsonDocument.Parse("""{"privateCodec":{"a":1,"A":2},"selectedSource":"source-A"}"""))
            frozen = WorkerContextContributionSnapshot.Freeze(Binding(Alpha), descriptor,
                new(WorkflowContextDisposition.Ready, null, payload.RootElement, "package-codec-digest-A", request));
        kinds.Clear(); fields.Clear(); legacy.Clear(); references.Clear();
        checks.That(frozen.Descriptor.Provider.SupportedReferenceKinds.SequenceEqual(["fixture.alpha.record"]) &&
            frozen.Descriptor.LegacyFormats.Single().Fields.SequenceEqual(["fixtureLegacy"]) &&
            frozen.Selection.Request!.References.Single().SourceId == "source-A" &&
            frozen.Selection.Payload!.Value.GetProperty("privateCodec").GetProperty("a").GetInt32() == 1 &&
            frozen.Selection.Payload.Value.GetProperty("privateCodec").GetProperty("A").GetInt32() == 2 &&
            frozen.Selection.PayloadDigest == "package-codec-digest-A",
            "freeze clones disposed JSON and every mutable descriptor request and legacy collection");
        var wire = Element(frozen);
        var reread = WorkerContextContributionSnapshot.ReadCollection(Element(new[] { frozen })).Single();
        checks.That(JsonElement.DeepEquals(wire, Element(reread)),
            "generic envelope roundtrip preserves opaque payload and package digest without reinterpretation");
        var restored = WorkerContextContributionSnapshot.Template(frozen, 41, 7);
        checks.That(restored.ContextRevision == 41 && restored.ExclusionRevision == 7 &&
            JsonElement.DeepEquals(Element(restored with { ContextRevision = 0, ExclusionRevision = 0 }), Element(frozen.Selection.Request)),
            "request restoration changes exactly two host revisions");

        var stateless = WorkerContextContributionSnapshot.Freeze(Binding(Alpha), frozen.Descriptor,
            frozen.Selection with { Payload = null, PayloadDigest = null });
        checks.That(stateless.Selection.Payload is null && stateless.Selection.PayloadDigest is null &&
            WorkerContextContributionSnapshot.ReadCollection(Element(new[] { stateless })).Single().Selection.Request is not null,
            "ready stateless contribution keeps its exact request without inventing codec state");
        foreach (var invalid in new[]
        {
            frozen.Selection with { PayloadDigest = null },
            frozen.Selection with { Payload = null },
            frozen.Selection with { Reason = "unexpected" },
            frozen.Selection with { Request = null },
            frozen.Selection with { Request = request with { References = frozen.Selection.Request!.References, ContextRevision = 1 } },
            frozen.Selection with { Disposition = (WorkflowContextDisposition)99 }
        })
            checks.Refuses(() => WorkerContextContributionSnapshot.Freeze(Binding(Alpha), frozen.Descriptor, invalid),
                "ready shape refuses partial codec state nonzero frozen revisions and undefined disposition");
        foreach (var disposition in new[] { WorkflowContextDisposition.Omitted, WorkflowContextDisposition.Unavailable,
            WorkflowContextDisposition.Blocked, WorkflowContextDisposition.Unsupported })
        {
            var omitted = WorkerContextContributionSnapshot.Freeze(Binding(Alpha), frozen.Descriptor,
                new(disposition, "fixture-unavailable", null, null, null));
            checks.That(omitted.Selection.Disposition == disposition &&
                WorkerContextContributionSnapshot.ProviderIds([omitted]).Count == 0,
                "nonready disposition does not invent a provider request");
            checks.Refuses(() => WorkerContextContributionSnapshot.Freeze(Binding(Alpha), frozen.Descriptor,
                omitted.Selection with { Payload = Parse("""{"opaque":1}"""), PayloadDigest = "codec" }),
                "nonready disposition cannot carry opaque codec bodies");
        }
        checks.Refuses(() => WorkerContextContributionSnapshot.Freeze(Binding(Alpha), frozen.Descriptor,
            new(WorkflowContextDisposition.Omitted, "Private Reason", null, null, null)), "reason remains a bounded lowercase token");
        checks.Refuses(() => WorkerContextContributionSnapshot.Freeze(Binding(Alpha), frozen.Descriptor with
            { HistoryProjection = (WorkflowContextHistoryProjection)99 }, frozen.Selection), "undefined history projection refuses");
        checks.Refuses(() => WorkerContextContributionSnapshot.Freeze(Binding(Alpha) with { Loaded = false },
            frozen.Descriptor, frozen.Selection), "unloaded package cannot author a frozen ready selection");
        checks.Refuses(() => WorkerContextContributionSnapshot.Freeze(Binding(Alpha) with { Generation = null },
            frozen.Descriptor, frozen.Selection), "missing package generation refuses");
        checks.Refuses(() => WorkerContextContributionSnapshot.ValidateCollection([frozen, frozen]), "duplicate contribution identity refuses");
        var sibling = frozen with { Descriptor = frozen.Descriptor with { Id = Alpha + ":sibling" },
            Selection = frozen.Selection with { Request = frozen.Selection.Request! with
                { References = [frozen.Selection.Request.References.Single() with { SourceId = "source-B" }] } } };
        checks.That(WorkerContextContributionSnapshot.ValidateCollection([frozen, sibling]).Count == 2,
            "distinct exact templates can share a provider descriptor");
        checks.Refuses(() => WorkerContextContributionSnapshot.ValidateCollection([frozen,
            sibling with { Selection = frozen.Selection }]), "duplicate ready template is ambiguous");
        checks.Refuses(() => WorkerContextContributionSnapshot.ValidateCollection([frozen,
            sibling with { Generation = "changed-generation" }]), "one package cannot claim inconsistent generations");
        checks.Refuses(() => WorkerContextContributionSnapshot.ValidateCollection([frozen,
            sibling with { Descriptor = sibling.Descriptor with { Provider = sibling.Descriptor.Provider with
                { Description = "Different provider contract" } } }]), "one provider cannot claim conflicting descriptors");
        var four = Enumerable.Range(0, 4).Select(index => frozen with
        {
            Descriptor = frozen.Descriptor with { Id = Alpha + ":bounded-" + index },
            Selection = frozen.Selection with { Request = frozen.Selection.Request! with
                { References = [frozen.Selection.Request.References.Single() with { SourceId = "bounded-" + index }] } }
        }).ToArray();
        checks.That(WorkerContextContributionSnapshot.ValidateCollection(four).Count == 4, "maximum contribution count is admitted");
        checks.Refuses(() => WorkerContextContributionSnapshot.ValidateCollection([.. four, sibling]), "one extra contribution refuses without truncation");

        checks.Refuses(() => WorkerContextContributionSnapshot.ReadCollection(default), "undefined contribution collection refuses");
        foreach (var invalidPayload in new[] { default(JsonElement), Parse("null") })
            checks.Refuses(() => WorkerContextContributionSnapshot.Freeze(Binding(Alpha), frozen.Descriptor,
                frozen.Selection with { Payload = invalidPayload }), "undefined or explicit null codec payload refuses");
        var heavyLegacy = Enumerable.Range(1, 8).Select(edition => new WorkflowContextLegacyFormat(edition,
            Enumerable.Range(0, 8).Select(field => "fixture" + field + "-" + new string('x', 119)).ToArray())).ToArray();
        var heavy = Enumerable.Range(0, 4).Select(index => WorkerContextContributionSnapshot.Freeze(Binding(Alpha),
            frozen.Descriptor with { Id = Alpha + ":heavy-" + index, LegacyFormats = heavyLegacy,
                Provider = frozen.Descriptor.Provider with { Description = new string('d', 4096) } },
            frozen.Selection with { Payload = Payload(8192), Request = frozen.Selection.Request! with
                { References = [frozen.Selection.Request.References.Single() with { SourceId = "heavy-" + index }] } })).ToArray();
        checks.Refuses(() => WorkerContextContributionSnapshot.ValidateCollection(heavy),
            "individually valid contributions cannot exceed the shared compact collection budget");


        var nearLegacy = Enumerable.Range(1, 4).Select(edition => new WorkflowContextLegacyFormat(edition,
            Enumerable.Range(0, 8).Select(field => "field" + field + "-" + new string('x', 95)).ToArray())).ToArray();
        var near = Enumerable.Range(0, 4).Select(index => WorkerContextContributionSnapshot.Freeze(Binding(Alpha),
            frozen.Descriptor with { Id = Alpha + ":near-" + index, LegacyFormats = nearLegacy,
                Provider = frozen.Descriptor.Provider with { Description = new string('d', 3000) } },
            frozen.Selection with { Payload = Payload(8192), Request = frozen.Selection.Request! with
                { References = [frozen.Selection.Request.References.Single() with { SourceId = "near-" + index }] } })).ToArray();
        var nearBytes = JsonSerializer.SerializeToUtf8Bytes(near, Compact).Length;
        checks.That(nearBytes is > 50_000 and <= WorkflowContextContributionContract.MaximumCollectionUtf8Bytes &&
            WorkerContextContributionSnapshot.ReadCollection(Element(near)).Count == 4,
            "near-limit four-contribution collection survives indented outer wire without changing compact budgets");

        var node = JsonNode.Parse(Element(new[] { frozen }).GetRawText())!;
        node[0]!["PackageId"] = Alpha;
        checks.Refuses(() => WorkerContextContributionSnapshot.ReadCollection(Element(node)), "typed envelope alias refuses");
        node = JsonNode.Parse(Element(new[] { frozen }).GetRawText())!;
        node[0]!["selection"]!["disposition"] = "READY";
        checks.Refuses(() => WorkerContextContributionSnapshot.ReadCollection(Element(node)), "enum wire name must be exact");
        var original = Element(new[] { frozen }).GetRawText();
        using (var duplicate = JsonDocument.Parse(original.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1,\"schemaVersion\":1", StringComparison.Ordinal)))
            checks.Refuses(() => WorkerContextContributionSnapshot.ReadCollection(duplicate.RootElement), "exact duplicate typed field refuses");
        checks.Refuses(() => WorkerContextContributionSnapshot.Freeze(Binding(Alpha), frozen.Descriptor,
            frozen.Selection with { Payload = Payload(8193) }), "payload one byte over its compact bound refuses");
        var limit = frozen with { Selection = frozen.Selection with { Payload = Payload(8192) } };
        var atLimit = WorkerContextContributionSnapshot.ValidateCollection([limit]).Single();
        var indented = JsonSerializer.SerializeToElement(new[] { atLimit }, CerebralJson.Options);
        checks.That(JsonSerializer.SerializeToUtf8Bytes(atLimit.Selection.Payload!.Value, Compact).Length == 8192 &&
            WorkerContextContributionSnapshot.ReadCollection(indented).Count == 1,
            "exact compact payload limit survives indented outer envelope roundtrip");
        using (var deep = JsonDocument.Parse(DeepJson(40), new JsonDocumentOptions { MaxDepth = 64 }))
            checks.Refuses(() => WorkerContextContributionSnapshot.Freeze(Binding(Alpha), frozen.Descriptor,
                frozen.Selection with { Payload = deep.RootElement }), "deep opaque payload respects the envelope depth boundary");
        checks.That(WorkerContextContributionSnapshot.IsReservedLegacyField("profile") &&
            WorkerContextContributionSnapshot.IsReservedLegacyField("PROFILE") &&
            WorkerContextContributionSnapshot.IsReservedLegacyField("contextContributions") &&
            !WorkerContextContributionSnapshot.IsReservedLegacyField("fixtureLegacy"),
            "legacy dispatcher protects Core fields and aliases while leaving package slots generic");
        var future = frozen.Descriptor with { LegacyFormats = [new(1_000_000, ["fixtureFuture"])] };
        checks.That(WorkerContextContributionSnapshot.FreezeDescriptor(Binding(Alpha), future).LegacyFormats.Single().SnapshotVersion == 1_000_000,
            "descriptor freezing can retain bounded future legacy declarations without dispatching them");
        ReceiverConsumerChecks(checks);
    }

    private static void ReceiverConsumerChecks(Checks checks)
    {
        var descriptor = new WorkflowDefinitionDescriptor(DefinitionId, "42", "Receiver fixture",
            WorkflowContract.EmptyObject, []);
        var supported = new WorkflowToolReceiptContextConsumer(1, true);
        var unsupported = new WorkflowToolReceiptContextConsumer(1, false);
        checks.That(WorkerToolContextProjectionAdmission.FreezeConsumer(descriptor) == unsupported &&
            WorkerToolContextProjectionAdmission.FreezeConsumer(descriptor with { ToolReceiptConsumer = unsupported }) == unsupported &&
            WorkerToolContextProjectionAdmission.FreezeConsumer(descriptor with { ToolReceiptConsumer = supported }) == supported,
            "absent and explicit false are unsupported; exact loaded true is supported");
        checks.Refuses(() => WorkerToolContextProjectionAdmission.FreezeConsumer(descriptor with
            { ToolReceiptConsumer = new(2, true) }), "unknown loaded receiver schema");

        var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, Profile(), "", "", null,
            [], ["files"], [], [], false, "fixture-route", [Binding(Alpha)], [])
            { ToolReceiptConsumer = supported };
        var parsed = WorkerExecutionSnapshot.Read(Element(snapshot));
        var generic = new WorkflowSubmission("receipt-fixture", "receipt-chat", "fixture-profile", DefinitionId, "42",
            "", null, WorkflowContract.EmptyObject, Element(snapshot));
        checks.That(parsed.ToolReceiptConsumer == supported &&
            WorkerToolContextProjectionAdmission.SupportsConsumer(generic, parsed),
            "a non-Conversation loaded definition may support the typed receipt contract");
        checks.That(!WorkerToolContextProjectionAdmission.SupportsConsumer(generic,
            WorkerExecutionSnapshot.Read(Element(snapshot with { ToolReceiptConsumer = unsupported }))),
            "an explicit false binding cannot receive receipts");

        JsonElement Changed(Action<JsonObject> change)
        {
            var node = JsonNode.Parse(Element(snapshot).GetRawText())!.AsObject();
            change(node);
            return Element(node);
        }
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Changed(node => node.Remove("toolReceiptConsumer"))),
            "edition 6 cannot enter the absent historical bridge");
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Changed(node => node.Remove("selectedToolOwners"))),
            "edition 6 requires an explicit selected tool-owner collection");
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Changed(node => node["selectedToolOwners"] = null)),
            "edition 6 refuses a null selected tool-owner collection");
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Changed(node => node["toolReceiptConsumer"] = null)),
            "edition 6 cannot carry a null receiver binding");
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Changed(node => node["toolReceiptConsumer"]!["schemaVersion"] = 2)),
            "edition 6 refuses unknown receiver schema");
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Changed(node => node["toolReceiptConsumer"]!["extra"] = 1)),
            "edition 6 refuses extra receiver metadata");
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Changed(node => node["resolvedSettingsVersion"] = 5)),
            "edition 5 cannot acquire a receiver binding");
        var historic = WorkerExecutionSnapshot.Read(Changed(node =>
        {
            node["resolvedSettingsVersion"] = 5;
            node.Remove("toolReceiptConsumer");
            node.Remove("approvedPluginSettings");
            node.Remove("selectedToolOwners");
        }));
        var old = generic with { DefinitionId = "pegasus.conversation:chat",
            DefinitionVersion = "1.0.3-tool-context.1" };
        checks.That(historic.ToolReceiptConsumer is null &&
            WorkerToolContextProjectionAdmission.SupportsConsumer(old, historic) &&
            !WorkerToolContextProjectionAdmission.SupportsConsumer(generic, historic),
            "only the exact accepted edition-5 Conversation tool-context pair enters the closed bridge");
        var selectedTool = new ToolDescriptor("fixture.selected", "Selected fixture",
            Element(new { type = "object" }), "files");
        var bound = snapshot with { Tools = [selectedTool],
            SelectedToolOwners = [new(selectedTool.Name, Alpha)] };
        checks.That(WorkerExecutionSnapshot.Read(Element(bound)).SelectedToolOwners?.Single().PackageId == Alpha,
            "the exact selected tool-to-service-owner binding survives the edition-6 wire");
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Element(bound with
            { SelectedToolOwners = [new(selectedTool.Name, Beta)] })),
            "a selected tool owner must have an exact saved package binding");
        checks.That(WorkerExecutionSnapshot.Read(Element(bound with
            { SelectedToolOwners = [new(selectedTool.Name, null)] })).SelectedToolOwners?.Single().PackageId is null,
            "a built-in selected tool explicitly freezes no service owner");
    }

    private static async Task ReceiverHostChecks(string directory, Checks checks)
    {
        var fixture = new Fixture(directory);
        var receiver = new WorkflowToolReceiptContextConsumer(1, true);
        var baseDefinition = new WorkflowDefinitionDescriptor(DefinitionId, "1", "Loaded fixture receiver",
            WorkflowContract.EmptyObject, ["files"]) { ToolReceiptConsumer = receiver };
        var registry = new WorkflowRegistry([new Module(Alpha, [new ReceiverDefinition(baseDefinition)], [])]);
        var current = registry.GetDefinition(DefinitionId, "1").Descriptor;
        var host = fixture.Host(definitionDescriptor: (_, _) => current);
        var identity = fixture.Identity("receiver-host");
        var snapshot = fixture.Snapshot() with { ToolReceiptConsumer = receiver };
        var captured = await host.CaptureAsync(identity, snapshot, default);
        checks.That(fixture.Alpha.CaptureFacts?.ToolReceiptConsumer == receiver &&
            fixture.Beta.CaptureFacts?.ToolReceiptConsumer == receiver &&
            captured.ToolReceiptConsumer == receiver,
            "the actual frozen registry declaration reaches both package Capture callbacks and the accepted snapshot");
        var accepted = fixture.Receipts.Submit(identity with { SettingsSnapshot = Element(captured) });
        host.RecheckBeforeAccept(accepted.Submission);
        checks.That(fixture.Alpha.ValidateAcceptedFacts?.ToolReceiptConsumer == receiver &&
            fixture.Beta.ValidateAcceptedFacts?.ToolReceiptConsumer == receiver,
            "accepted and current receiver facts reach host validation without replacing other identity fields");
        var emptyHost = fixture.Host(sources: [], definitionDescriptor: (_, _) => current);
        var emptyIdentity = fixture.Identity("receiver-no-provider");
        var emptyCaptured = await emptyHost.CaptureAsync(emptyIdentity, snapshot, default);
        var emptyAccepted = fixture.Receipts.Submit(emptyIdentity with { SettingsSnapshot = Element(emptyCaptured) });
        emptyHost.RecheckBeforeAccept(emptyAccepted.Submission);
        var validations = fixture.Alpha.Validated + fixture.Beta.Validated;
        current = current with { ToolReceiptConsumer = new(1, false) };
        checks.Refuses(() => host.RecheckBeforeAccept(accepted.Submission),
            "a stable no-op package callback cannot authorize changed loaded receiver support");
        checks.Refuses(() => emptyHost.RecheckBeforeAccept(emptyAccepted.Submission),
            "receiver drift refuses even when no package callback or Ready provider can run");
        checks.That(fixture.Alpha.Validated + fixture.Beta.Validated == validations &&
            fixture.Receipts.Get(accepted.Id).Revision == accepted.Revision,
            "host rejects receiver drift before callbacks and without mutating the durable run");

        var ready = new ReadyReceipt();
        var program = Binding("pegasus.programming");
        var closed = new WorkerContextContributionHost(fixture.State,
            new([Binding(Alpha), program, Binding("cerebral.host")]), null, (_, _) => ["files"],
            (_, _) => null, [new(program, ready, () => { })], (_, _) =>
                baseDefinition with { ToolReceiptConsumer = new(1, false) });
        var unsupported = fixture.Snapshot() with { ToolReceiptConsumer = new(1, false) };
        var refusedId = fixture.Identity("receiver-refusal");
        checks.Refuses(() => closed.CaptureAsync(refusedId, unsupported, default).GetAwaiter().GetResult(),
            "a non-Omitted receipt selection cannot pass unsupported host Capture");
        checks.That(fixture.Receipts.TryGet(refusedId.RequestId) is null &&
            fixture.Journal.GetRevision(refusedId.ConversationId) == 0,
            "unsupported receipt selection is refused before durable acceptance");
    }

    private static async Task HostChecks(string directory, Checks checks)
    {
        var fixture = new Fixture(directory);
        var host = fixture.Host();
        var identity = fixture.Identity("capture") with { Caller = new(EventActorKind.User, "owner", "Capture label") };
        var before = Fingerprint(fixture.State);
        var captured = await host.CaptureAsync(identity, fixture.Snapshot(), default);
        var accepted = fixture.Receipts.Submit(identity with { SettingsSnapshot = Element(captured),
            Caller = new(EventActorKind.User, "owner", "Accepted label") });
        checks.That(captured.ContextContributions.Count == 2 &&
            captured.ContextContributionInstallationId == fixture.Installation &&
            captured.PackageBindings.Select(item => item.Id).Contains(Beta) &&
            Fingerprint(fixture.State) == before, "two read-only contributors capture into exact host-owned package and installation state");
        fixture.Alpha.Selection = "later-A";
        fixture.Beta.Selection = "later-B";
        host.RecheckBeforeAccept(accepted.Submission);
        var restored = await host.RestoreAsync(accepted.Submission, 29, 5, default);
        checks.That(restored.Items.Count == 2 && restored.Items.All(item => item.Status == "ready") &&
            restored.Items[0].Request!.References.Single().SourceId == "selected-alpha-A" &&
            restored.Items[1].Request!.References.Single().SourceId == "selected-beta-A" &&
            restored.Items.All(item => item.Request!.ContextRevision == 29 && item.Request.ExclusionRevision == 5) &&
            fixture.Alpha.Captured == 1 && fixture.Beta.Captured == 1,
            "restoration follows frozen A sources after current selection changes and rebases only host revisions");
        checks.That(fixture.Alpha.CaptureFacts?.Identity.Caller is { Kind: EventActorKind.User, Id: "owner", DisplayName: null } &&
            fixture.Alpha.ValidateAcceptedFacts?.Identity.Caller is { Kind: EventActorKind.User, Id: "owner", DisplayName: null } &&
            fixture.Alpha.RestoreFacts?.Accepted.Identity.Caller is { Kind: EventActorKind.User, Id: "owner", DisplayName: null },
            "capture validation and restore facts retain caller Kind and Id while excluding changing display labels");
        var newer = await host.CaptureAsync(identity, fixture.Snapshot(), default);
        checks.Refuses(() => fixture.Receipts.Submit(accepted.Submission with { SettingsSnapshot = Element(newer) }),
            "a different recaptured snapshot cannot overwrite an already accepted request ID");
        var duplicate = fixture.Receipts.Submit(accepted.Submission);
        checks.That(JsonElement.DeepEquals(duplicate.Submission.SettingsSnapshot, accepted.Submission.SettingsSnapshot) &&
            duplicate.Submission.SettingsSnapshot.GetProperty("contextContributions")[0].GetProperty("selection")
                .GetProperty("request").GetProperty("references")[0].GetProperty("sourceId").GetString() == "selected-alpha-A",
            "real SQLite request-ID duplicate reuses the winning original accepted snapshot");
        var reopened = new WorkflowStore(new EventJournal(fixture.JournalPath)).Get(accepted.Id);
        checks.That((await fixture.Host().RestoreAsync(reopened.Submission, 31, 6, default)).Items[0].Request!
            .References.Single().SourceId == "selected-alpha-A", "reopened durable submission retains the winning frozen selection");

        foreach (var change in new[] { "grants", "installation" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var validationCalls = fixture.Beta.Validated;
            fixture.Alpha.BeforeValidate = (_, _, _, _) =>
            {
                entered.TrySetResult();
                released.Task.GetAwaiter().GetResult();
            };
            var validating = Task.Run(() => host.RecheckBeforeAccept(accepted.Submission));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (change == "grants") fixture.ProfileChange(profile => profile.Capabilities.Clear());
                else fixture.ChangeInstallation("validation-changed-installation");
                released.TrySetResult();
                await checks.RefusesAsync(() => validating,
                    "fresh host observation refuses ownership change while a successful package Validate callback is paused");
                checks.That(fixture.Beta.Validated == validationCalls,
                    "post-Validate refusal prevents the next package callback from observing stale authorization");
            }
            finally
            {
                released.TrySetResult();
                // Observe the owned task before restoring state or removing its SQLite fixture,
                // including timeout/setup-failure paths that did not reach the expected refusal.
                try { await validating.ConfigureAwait(false); }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                    ArgumentException or UnauthorizedAccessException or JsonException) { }
                fixture.Alpha.BeforeValidate = null;
                fixture.ProfileChange(profile => { if (!profile.Capabilities.Contains("files")) profile.Capabilities.Add("files"); });
                fixture.ChangeInstallation(fixture.Installation);
            }
        }

        foreach (var change in new[] { "role", "revision" })
        {
            var validations = fixture.Beta.Validated;
            fixture.Beta.BeforeValidate = (_, _, _, _) => fixture.ProfileChange(profile =>
            {
                if (change == "role") profile.Role = "changed-helper";
                else profile.Revision++;
            });
            try
            {
                checks.Refuses(() => host.RecheckBeforeAccept(accepted.Submission),
                    "last package Validate cannot authorize changed current role or revision facts");
                var current = fixture.State.Get<AgentProfile>("agent", "fixture-profile")!;
                checks.That(fixture.Beta.Validated == validations + 1 && current.WorkerId == "local" &&
                    current.Capabilities.Contains("files") &&
                    fixture.State.Get<HostSettings>("settings", "host")!.InstallationId == fixture.Installation,
                    "validation fact drift refusal is independent of unchanged owner grants and installation");
            }
            finally
            {
                fixture.Beta.BeforeValidate = null;
                fixture.ProfileChange(profile => { profile.Role = "helper"; profile.Revision = 1; });
            }
        }

        var observedRestoration = await host.RestoreWithObservationAsync(accepted.Submission, 30, 6, default);
        var observedValidationCount = fixture.Alpha.Validated + fixture.Beta.Validated;
        host.RecheckCurrent(accepted.Submission, observedRestoration.CurrentFacts);
        checks.That(observedRestoration.CurrentFacts is { ProfileRole: "helper", ProfileRevision: 1 } &&
            observedRestoration.View.Items.Count == 2 &&
            !Json(observedRestoration.View).Contains("currentFacts", StringComparison.Ordinal) &&
            fixture.Alpha.Validated + fixture.Beta.Validated == observedValidationCount,
            "outer-restore witness stays private and a fresh current observation performs no package validation callback");
        fixture.ProfileChange(profile => profile.Revision++);
        checks.Refuses(() => host.RecheckCurrent(accepted.Submission, observedRestoration.CurrentFacts),
            "current fact drift after the outer Restore await refuses against its exact validated observation");
        fixture.ProfileChange(profile => profile.Revision = 1);

        fixture.ProfileChange(profile => profile.Capabilities.Clear());
        await checks.RefusesAsync(() => host.RestoreAsync(accepted.Submission, 1, 0, default), "current grant revocation refuses before restore");
        fixture.ProfileChange(profile => profile.Capabilities.Add("files"));
        fixture.ProfileChange(profile => profile.WorkerId = "remote");
        checks.Refuses(() => host.RequireCurrent(accepted.Submission), "current local consumer ownership cannot move to another worker");
        fixture.ProfileChange(profile => profile.WorkerId = "local");
        fixture.ChangeInstallation("fixture-changed-installation");
        checks.Refuses(() => host.RecheckBeforeAccept(accepted.Submission), "current installation must match accepted owner");
        fixture.ChangeInstallation(fixture.Installation);
        var changed = fixture.Bindings.Select(item => item.Id == Alpha ? item with { Generation = "different-generation" } : item).ToArray();
        checks.Refuses(() => fixture.Host(changed).RequireCurrent(accepted.Submission), "current package graph must retain the exact accepted generation");
        fixture.PackageProblem = "fixture-package-generation-unavailable";
        checks.Refuses(() => host.RequireCurrent(accepted.Submission), "current package lifecycle observation can refuse accepted work");
        fixture.PackageProblem = null;
        fixture.Alpha.Available = false;
        checks.Refuses(() => host.RequireCurrent(accepted.Submission), "retired package source refuses without replacement");
        fixture.Alpha.Available = true;
        var restoreCalls = fixture.Alpha.Restored;
        await checks.ThrowsAsync<OperationCanceledException>(() => host.RestoreAsync(accepted.Submission, 1, 0, new(true)),
            "pre-cancelled restoration never enters package restore");
        checks.That(fixture.Alpha.Restored == restoreCalls, "pre-cancellation preserves package callback count");
        fixture.Alpha.BeforeRestore = (_, _, token) => { fixture.ProfileChange(profile => profile.Capabilities.Clear()); return Task.CompletedTask; };
        await checks.RefusesAsync(() => host.RestoreAsync(accepted.Submission, 1, 0, default),
            "grant change during awaited restore refuses before any request is returned");
        fixture.Alpha.BeforeRestore = null;
        fixture.ProfileChange(profile => profile.Capabilities.Add("files"));
        fixture.Alpha.BeforeRestore = (_, _, token) => { fixture.ChangeInstallation("late-installation"); return Task.CompletedTask; };
        await checks.RefusesAsync(() => host.RestoreAsync(accepted.Submission, 1, 0, default),
            "installation change during awaited restore refuses returned context");
        fixture.Alpha.BeforeRestore = null;
        fixture.ChangeInstallation(fixture.Installation);
        using (var cancellation = new CancellationTokenSource())
        {
            fixture.Alpha.BeforeRestore = (_, _, token) => { cancellation.Cancel(); return Task.CompletedTask; };
            await checks.ThrowsAsync<OperationCanceledException>(() => host.RestoreAsync(accepted.Submission, 1, 0, cancellation.Token),
                "cancellation after package entry does not return a restored request");
            fixture.Alpha.BeforeRestore = null;
        }
        fixture.Alpha.ChangeRequest = true;
        await checks.RefusesAsync(() => host.RestoreAsync(accepted.Submission, 9, 3, default),
            "package restore cannot substitute any provider reference beyond the two revisions");
        fixture.Alpha.ChangeRequest = false;
        var historicalBefore = fixture.Alpha.Captured;
        fixture.Alpha.Available = false;
        checks.That(host.HistoricalAdmissions(reopened).Count == 2 && fixture.Alpha.Captured == historicalBefore,
            "edition5 historical manifest requires no current package availability or fresh selection");
        fixture.Alpha.Available = true;

        fixture.Alpha.Stateless = true;
        fixture.Beta.Disposition = WorkflowContextDisposition.Unavailable;
        var optionalIdentity = fixture.Identity("stateless-optional");
        var optional = await host.CaptureAsync(optionalIdentity, fixture.Snapshot(), default);
        var optionalRun = fixture.Receipts.Submit(optionalIdentity with { SettingsSnapshot = Element(optional) });
        var optionalRestored = await host.RestoreAsync(optionalRun.Submission, 12, 4, default);
        checks.That(optional.ContextContributions[0].Selection.Payload is null &&
            optional.ContextContributions[0].Selection.PayloadDigest is null &&
            optionalRestored.Items[0].Request is { ContextRevision: 12, ExclusionRevision: 4 } &&
            optionalRestored.Items[1] is { Status: "unavailable", Request: null },
            "actual host restores stateless ready input and explicit nonready status without substitute defaults");
        fixture.Alpha.Stateless = false;
        fixture.Beta.Disposition = WorkflowContextDisposition.Ready;

        var legacyNode = JsonNode.Parse(accepted.Submission.SettingsSnapshot.GetRawText())!;
        legacyNode["resolvedSettingsVersion"] = 4;
        legacyNode.AsObject().Remove("contextContributions");
        legacyNode.AsObject().Remove("contextContributionInstallationId");
        legacyNode.AsObject().Remove("approvedPluginSettings");
        legacyNode.AsObject().Remove("toolReceiptConsumer");
        legacyNode.AsObject().Remove("selectedToolOwners");
        legacyNode["fixtureLegacy"] = new JsonObject { ["source"] = "legacy-alpha",
            ["privateOrigin"] = new JsonObject { ["installationId"] = fixture.Installation } };
        var legacyRun = new WorkflowRun { Id = "legacy", Submission = accepted.Submission with { RequestId = "legacy", SettingsSnapshot = Element(legacyNode) } };
        checks.Refuses(() => host.HistoricalAdmissions(legacyRun), "unclaimed legacy extension has no raw fallback");
        var protectedContributor = new Contributor(Alpha, WorkflowContextHistoryProjection.ReferenceOnly,
            legacyFields: ["profile"]);
        var protectedHost = fixture.Host(sources: [new(Binding(Alpha), protectedContributor, protectedContributor.EnsureAvailable)]);
        checks.Refuses(() => protectedHost.HistoricalAdmissions(legacyRun), "legacy reader never receives a reserved Core field declaration");
        checks.That(protectedContributor.LegacyRead == 0,
            "reserved Core declaration refuses before invoking any package compatibility reader");
        var compatible = new Contributor(Alpha, WorkflowContextHistoryProjection.ReferenceOnly, legacyFields: ["fixtureLegacy"]);
        var legacyHost = fixture.Host(sources: [new(Binding(Alpha), compatible, compatible.EnsureAvailable)]);
        var converted = legacyHost.HistoricalAdmissions(legacyRun);
        checks.That(converted.Count == 1 && converted[0].Selection.Disposition == WorkflowContextDisposition.Omitted &&
            compatible.LegacyRead == 1 && compatible.Captured == 0 &&
            JsonElement.DeepEquals(legacyRun.Submission.SettingsSnapshot, Element(legacyNode)),
            "pure package legacy conversion preserves omission without capture or canonical snapshot mutation");
        var legacyRestored = await legacyHost.RestoreAsync(legacyRun.Submission, 23, 4, default);
        checks.That(compatible.LegacyFacts?.Facts.Accepted.InstallationId is null &&
            compatible.LegacyFacts!.Fragments.GetProperty("fixtureLegacy").GetProperty("privateOrigin")
                .GetProperty("installationId").GetString() == fixture.Installation &&
            WorkerExecutionSnapshot.Read(legacyRun.Submission.SettingsSnapshot).ContextContributionInstallationId is null &&
            legacyRestored.Items.Single() is { Status: "omitted", Request: null },
            "legacy unknown installation stays null despite an opaque nested package origin while omitted compatibility restores");
        compatible.ReadyLegacy = true;
        var legacyReady = legacyHost.HistoricalAdmissions(legacyRun);
        checks.That(legacyReady.Single().Selection.Disposition == WorkflowContextDisposition.Ready &&
            compatible.LegacyFacts?.Facts.Accepted.InstallationId is null,
            "package compatibility can retain a ready request without inventing an accepted legacy installation");
        await checks.ThrowsAsync<UnauthorizedAccessException>(() => legacyHost.RestoreAsync(legacyRun.Submission, 23, 4, default),
            "ready legacy input with unknown origin refuses before package restore or provider dispatch");
        checks.That(compatible.Restored == 0,
            "unknown legacy origin never enters the package restoration callback");
        legacyNode["contextContributionInstallationId"] = fixture.Installation;
        var witnessedLegacy = legacyRun with { Submission = legacyRun.Submission with { SettingsSnapshot = Element(legacyNode) } };
        var witnessedRestored = await legacyHost.RestoreAsync(witnessedLegacy.Submission, 24, 5, default);
        checks.That(compatible.LegacyFacts?.Facts.Accepted.InstallationId == fixture.Installation &&
            witnessedRestored.Items.Single().Request is { ContextRevision: 24, ExclusionRevision: 5 } &&
            compatible.RestoreFacts?.Accepted.InstallationId == fixture.Installation,
            "explicit generic installation in edition4 stays accepted provenance and permits exact ready restoration");
        legacyNode.AsObject().Remove("contextContributionInstallationId");
        legacyNode["resolvedSettingsVersion"] = 7;
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Element(legacyNode)), "future snapshot edition refuses before compatibility reader");
        var badSnapshot = JsonNode.Parse(accepted.Submission.SettingsSnapshot.GetRawText())!;
        badSnapshot["ContextContributions"] = new JsonArray();
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Element(badSnapshot)), "snapshot aliases cannot shadow admitted context");
        badSnapshot = JsonNode.Parse(accepted.Submission.SettingsSnapshot.GetRawText())!;
        badSnapshot.AsObject().Remove("contextContributions");
        checks.Refuses(() => WorkerExecutionSnapshot.Read(Element(badSnapshot)), "edition5 requires an explicit contribution collection");
        var budgetContributor = new Contributor(Alpha, WorkflowContextHistoryProjection.ReferenceOnly, maxReferences: 32)
            { Stateless = true };
        ContextProviderRequest? budgetRequest = null;
        int zeroBytes = 0, rebasedBytes = 0;
        for (var padding = 1; padding <= 256 && budgetRequest is null; padding++)
        {
            var references = Enumerable.Range(0, 25).Select(index =>
            {
                var id = "reference-" + index + "-";
                return new ContextProviderReference(id + new string('i', 256 - id.Length),
                    Alpha + ".record", new string('s', padding), 1, BeforeCurrentInput: true);
            }).ToArray();
            var request = new ContextProviderRequest(budgetContributor.Descriptor.Provider.Id, 1, references, 4096);
            int ViewBytes(ContextProviderRequest value) => JsonSerializer.SerializeToUtf8Bytes(
                new WorkerWorkflowEffects.ContextContributionsData(1,
                    [new(budgetContributor.Descriptor.Id, "ready", value)]), Compact).Length;
            zeroBytes = ViewBytes(request);
            rebasedBytes = ViewBytes(request with { ContextRevision = long.MaxValue, ExclusionRevision = long.MaxValue });
            if (zeroBytes <= WorkerWorkflowEffects.MaximumContextContributionProjectionBytes &&
                rebasedBytes > WorkerWorkflowEffects.MaximumContextContributionProjectionBytes)
                budgetRequest = request;
        }
        if (budgetRequest is null) throw new InvalidOperationException("Fixture cannot isolate the revision-rebased execution view bound.");
        budgetContributor.RequestTemplate = budgetRequest;
        var budgetSelection = await budgetContributor.CaptureAsync(
            new(new("budget", "budget-chat", "fixture-profile", DefinitionId, "1", null, null), "helper", 1, "local", fixture.Installation), default);
        var budgetAdmission = WorkerContextContributionSnapshot.Freeze(Binding(Alpha), budgetContributor.Descriptor, budgetSelection);
        checks.That(WorkerContextContributionSnapshot.ValidateCollection([budgetAdmission]).Count == 1 &&
            JsonSerializer.SerializeToUtf8Bytes(new[] { budgetAdmission }, Compact).Length <
                WorkflowContextContributionContract.MaximumCollectionUtf8Bytes &&
            zeroBytes <= WorkerWorkflowEffects.MaximumContextContributionProjectionBytes &&
            rebasedBytes > WorkerWorkflowEffects.MaximumContextContributionProjectionBytes,
            "structurally valid collection isolates the public execution view budget at worst-case revision digits");
        var budgetIdentity = fixture.Identity("oversized-public-view");
        var budgetBefore = Fingerprint(fixture.State);
        var budgetJournal = Json(fixture.Journal.ReadPage(budgetIdentity.ConversationId));
        var budgetHost = fixture.Host(sources: [new(Binding(Alpha), budgetContributor, budgetContributor.EnsureAvailable)]);
        await checks.ThrowsAsync<InvalidDataException>(() => budgetHost.CaptureAsync(budgetIdentity, fixture.Snapshot(), default),
            "public Id status request view over 16KiB after rebasing refuses during capture before durable acceptance");
        checks.That(Fingerprint(fixture.State) == budgetBefore &&
            Json(fixture.Journal.ReadPage(budgetIdentity.ConversationId)) == budgetJournal,
            "execution view refusal leaves both settings SQLite and workflow journal unchanged");

    }

    private static void ProjectionChecks(string directory, Checks checks)
    {
        Directory.CreateDirectory(directory);
        var journal = new EventJournal(Path.Combine(directory, "engine.db"));
        var receipts = new WorkflowStore(journal);
        var fixture = new ProjectionFixture(journal, receipts, "first");
        var alphaEffect = fixture.ProviderEffect(fixture.Alpha, AlphaBody);
        var betaEffect = fixture.ProviderEffect(fixture.Beta, BetaBody);
        var alphaEvent = fixture.Event(CerebralEventTypes.ToolResult, alphaEffect);
        var betaEvent = fixture.Event(CerebralEventTypes.ToolResult, betaEffect);
        var canonical = Json(journal.ReadPage(fixture.ConversationId)) + Json(receipts.Get(fixture.RunId));
        var projected = WorkerContextContributionProjection.ProjectEvent(journal, alphaEvent, default);
        checks.That(!Json(projected).Contains(AlphaBody, StringComparison.Ordinal) &&
            Json(projected).Contains("bodyOmitted", StringComparison.Ordinal),
            "original ReferenceOnly provider result becomes a generic body-free reference");
        checks.That(ReferenceEquals(betaEvent, WorkerContextContributionProjection.ProjectEvent(journal, betaEvent, default)) &&
            Json(betaEvent).Contains(BetaBody, StringComparison.Ordinal), "Preserve provider view retains its original body");
        checks.That(canonical == Json(journal.ReadPage(fixture.ConversationId)) + Json(receipts.Get(fixture.RunId)) &&
            Json(receipts.GetEffectResult(fixture.RunId, alphaEffect)).Contains(AlphaBody, StringComparison.Ordinal),
            "projection changes no original intent receipt or canonical workflow state");
        var raw = receipts.GetEffectResult(fixture.RunId, alphaEffect);
        var rawWindow = CerebralJson.Read<ContextProviderResult>(raw.Data!.Value).Window;
        var betaWindow = CerebralJson.Read<ContextProviderResult>(receipts.GetEffectResult(fixture.RunId, betaEffect).Data!.Value).Window;
        var context = rawWindow with { Contributions = [.. rawWindow.Contributions, .. betaWindow.Contributions] };
        var generated = fixture.Effect(WorkerWorkflowEffects.GenerateCommand, Element(new { context }),
            new(WorkflowEffectState.Succeeded, Element(new { nested = new { context } })));
        var generatedCall = fixture.Event(CerebralEventTypes.ToolCall, generated);
        var generatedResult = fixture.Event(CerebralEventTypes.ToolResult, generated);
        checks.That(!Json(WorkerContextContributionProjection.ProjectEvent(journal, generatedCall, default))
            .Contains(AlphaBody, StringComparison.Ordinal) &&
            Json(WorkerContextContributionProjection.ProjectEvent(journal, generatedCall, default))
                .Contains(BetaBody, StringComparison.Ordinal), "nested model input projects only the original ReferenceOnly source");
        checks.That(!Json(WorkerContextContributionProjection.ProjectEvent(journal, generatedResult, default))
            .Contains(AlphaBody, StringComparison.Ordinal) &&
            Json(journal.ReadExact(fixture.ConversationId, generatedCall.Id, journal.GetRevision(fixture.ConversationId)))
                .Contains(AlphaBody, StringComparison.Ordinal), "nested result projection preserves canonical model-only intent body");
        var altered = rawWindow.Contributions.Single() with
            { Messages = [new("user", [new TextContentPart("FORGED BODY")])] };
        var damaged = fixture.Effect(WorkerWorkflowEffects.GenerateCommand, Element(new
            { context = rawWindow with { Contributions = [altered] } }), new(WorkflowEffectState.Succeeded, Element(new { })));
        checks.Refuses(() => WorkerContextContributionProjection.ProjectEvent(journal,
            fixture.Event(CerebralEventTypes.ToolCall, damaged), default), "nested source mismatch refuses instead of exposing raw fallback");
        checks.Refuses(() => WorkerContextContributionProjection.ProjectEvent(journal, alphaEvent with
            { Provenance = alphaEvent.Provenance with { JobId = "foreign-run" } }, default), "original event ownership mismatch refuses");
        checks.Refuses(() => WorkerContextContributionProjection.ProjectEvent(journal, alphaEvent with
            { Content = [new StructuredContentPart("workflow.command.result", Element(raw with { Data = Element(new { copied = AlphaBody }) }))] }, default),
            "historical successful event must match its exact durable receipt");

        var joined = new StringBuilder();
        var offset = 0;
        var pages = 0;
        do
        {
            var page = WorkflowReceiptReader.Read(receipts, new(fixture.RunId, alphaEffect, offset, 17),
                value => WorkerContextContributionProjection.ProjectReceipt(receipts, fixture.RunId, alphaEffect, value, journal));
            joined.Append(page.Json);
            offset = page.NextOffset ?? -1;
            if (++pages > 512) throw new InvalidOperationException("Fixture paging bound exceeded.");
        } while (offset >= 0);
        using (var assembled = JsonDocument.Parse(joined.ToString()))
            checks.That(!joined.ToString().Contains(AlphaBody, StringComparison.Ordinal) &&
                assembled.RootElement.GetProperty("data").GetProperty("bodyOmitted").GetBoolean(),
                "paged receipt projects full normalized result before UTF8 slicing");
        var rawPage = WorkflowReceiptReader.Read(receipts, new(fixture.RunId, alphaEffect));
        var partial = rawPage with { Offset = 1, Json = AlphaBody, TotalBytes = 10_000, NextOffset = 18, Truncated = true };
        var nestedReceipt = fixture.Effect(WorkerWorkflowEffects.ReceiptCommand, Element(new { effectId = alphaEffect }),
            new(WorkflowEffectState.Succeeded, Element(new { nested = partial })));
        checks.That(!Json(WorkerContextContributionProjection.ProjectEvent(journal,
            fixture.Event(CerebralEventTypes.ToolResult, nestedReceipt), default)).Contains(AlphaBody, StringComparison.Ordinal),
            "nested partial receipt is reidentified from its original subject without parsing partial body");
        checks.Refuses(() => WorkflowReceiptReader.Read(receipts, new(fixture.RunId, alphaEffect),
            value => value with { ProviderReceiptId = "forged-receipt" }), "receipt projection cannot change receipt authority");

        fixture.Complete();
        var later = new ProjectionFixture(journal, receipts, "later", fixture.ConversationId, omitted: true);
        var historySource = new WorkerWorkflowEffects.HistorySourceData(alphaEvent.Id, alphaEvent.Sequence,
            later.AcceptedSequence, 1, 4096, 10_000, AlphaBody, 18, true);
        var history = later.Effect(WorkerWorkflowEffects.HistoryCommand, Element(new { eventId = alphaEvent.Id }),
            new(WorkflowEffectState.Succeeded, Element(historySource)));
        checks.That(!Json(WorkerContextContributionProjection.ProjectEvent(journal,
            later.Event(CerebralEventTypes.ToolResult, history), default)).Contains(AlphaBody, StringComparison.Ordinal),
            "later omitted selection cannot reclassify an original ReferenceOnly exact-history body");
        var reopenedJournal = new EventJournal(Path.Combine(directory, "engine.db"));
        checks.That(!Json(WorkerContextContributionProjection.ProjectEvent(reopenedJournal,
            reopenedJournal.ReadExact(fixture.ConversationId, alphaEvent.Id, reopenedJournal.GetRevision(fixture.ConversationId)), default))
            .Contains(AlphaBody, StringComparison.Ordinal), "original projection proof survives private SQLite reopen");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            checks.Throws<OperationCanceledException>(() => WorkerContextContributionProjection.ProjectEvent(journal, alphaEvent, cancel.Token),
                "projection propagates cancellation before returning a view");
        }
        var uncertain = new ProjectionFixture(journal, receipts, "unknown");
        var unknown = uncertain.Effect(uncertain.Alpha.Descriptor.Provider.Id,
            Element(WorkerContextContributionSnapshot.Template(uncertain.Alpha, 7, 2)),
            new(WorkflowEffectState.Unknown, Element(new { diagnostic = AlphaBody }), Error: AlphaBody));
        var originalUnknown = uncertain.Event(CerebralEventTypes.ToolResult, unknown);
        receipts.CompleteEffect(uncertain.RunId, unknown, uncertain.ProviderResult(uncertain.Alpha, unknown, AlphaBody));
        var historical = WorkerContextContributionProjection.ProjectEvent(journal, originalUnknown, default);
        checks.That(historical.Data!.Value.GetProperty("state").GetString()!.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
            !Json(historical).Contains(AlphaBody, StringComparison.Ordinal) &&
            receipts.GetEffectResult(uncertain.RunId, unknown).State == WorkflowEffectState.Succeeded,
            "historical Unknown view is preserved after later receipt reconciliation succeeds");
        var unrelated = later.Effect("fixture.other:resolve", Element(new { }),
            new(WorkflowEffectState.Succeeded, Element(new { body = BetaBody }), ProviderReceiptId: "fixture-other"));
        var otherEvent = later.Event(CerebralEventTypes.ToolResult, unrelated);
        checks.That(ReferenceEquals(otherEvent, WorkerContextContributionProjection.ProjectEvent(journal, otherEvent, default)),
            "unrelated provider is preserved without current registry reinterpretation");
    }

    private static async Task RuntimeChecks(string directory, Checks checks)
    {
        var fixture = new Fixture(directory);
        var alpha = new FakeProvider(fixture.Alpha.Descriptor.Provider);
        var beta = new FakeProvider(fixture.Beta.Descriptor.Provider);
        var definition = new Definition();
        var owner = new WorkerWorkflows(fixture.Sources(), fixture.State, new Configuration(fixture.State), new EventHub(),
            [new Module(Alpha, [definition], [alpha]), new Module(Beta, [], [beta])], fixture.Bindings,
            journal: fixture.Journal, contextPackageProblem: (_, _) => fixture.PackageProblem);
        alpha.Store = owner.Store; beta.Store = owner.Store;
        async Task<WorkflowRun> Submit(string id, Contributor contributor, bool mismatch = false)
        {
            var identity = fixture.Identity(id);
            var snapshot = await fixture.Host().CaptureAsync(identity, fixture.Snapshot(), default);
            var run = owner.Store.Submit(identity with { SettingsSnapshot = Element(snapshot) });
            var admission = snapshot.ContextContributions.Single(item => item.PackageId == contributor.Package);
            var request = WorkerContextContributionSnapshot.Template(admission, 7, 2);
            definition.Request = mismatch ? request with
                { References = [request.References.Single() with { SourceId = "unaccepted-source" }] } : request;
            return run;
        }

        var success = await Submit("runtime-alpha", fixture.Alpha);
        var completed = await owner.Runtime.RunAsync(success.Id);
        checks.That(completed.Status == WorkflowStatus.Completed && alpha.Executed == 1 && beta.Executed == 0 &&
            alpha.ObservedDispatched && owner.Store.GetEffectResult(success.Id, alpha.LastEffect!).State == WorkflowEffectState.Succeeded,
            "production guarded registry dispatches one fake provider only after its exact SQLite intent is Dispatched");
        success = await Submit("runtime-beta", fixture.Beta);
        completed = await owner.Runtime.RunAsync(success.Id);
        checks.That(completed.Status == WorkflowStatus.Completed && beta.Executed == 1 && beta.ObservedDispatched,
            "second unrelated contributor uses the same generic production lifecycle");
        var beforeAlpha = alpha.Executed; var beforeBeta = beta.Executed;
        var mismatch = await Submit("runtime-preflight", fixture.Alpha, mismatch: true);
        var refused = await owner.Runtime.RunAsync(mismatch.Id);
        var refusedId = owner.Journal.ReadPage(mismatch.Submission.ConversationId).Events.Single(item =>
            item.Type == CerebralEventTypes.ToolCall && item.TurnId == mismatch.Id).CorrelationId!;
        var refusedEffect = owner.Store.GetEffectResult(mismatch.Id, refusedId);
        checks.That(refusedEffect.State == WorkflowEffectState.Failed && refusedEffect.Data is null &&
            alpha.Executed == beforeAlpha && beta.Executed == beforeBeta,
            "valid-shaped but unaccepted reference is refused preflight with zero actual provider entry");
        var revoked = await Submit("runtime-validation-revocation", fixture.Alpha);
        fixture.Alpha.BeforeValidate = (_, _, _, _) => fixture.ProfileChange(profile => profile.Capabilities.Clear());
        var revokedRun = await owner.Runtime.RunAsync(revoked.Id);
        var revokedId = owner.Journal.ReadPage(revoked.Submission.ConversationId).Events.Single(item =>
            item.Type == CerebralEventTypes.ToolCall && item.TurnId == revoked.Id).CorrelationId!;
        checks.That(owner.Store.GetEffectResult(revoked.Id, revokedId) is { State: WorkflowEffectState.Failed, Data: null } &&
            alpha.Executed == beforeAlpha && beta.Executed == beforeBeta,
            "successful package validation cannot authorize actual provider entry after a grant change inside its callback");
        fixture.Alpha.BeforeValidate = null;
        fixture.ProfileChange(profile => profile.Capabilities.Add("files"));
        var factsChanged = await Submit("runtime-validation-fact-drift", fixture.Alpha);
        fixture.Beta.BeforeValidate = (_, _, _, _) => fixture.ProfileChange(profile => profile.Revision++);
        var factsChangedRun = await owner.Runtime.RunAsync(factsChanged.Id);
        var factsChangedId = owner.Journal.ReadPage(factsChanged.Submission.ConversationId).Events.Single(item =>
            item.Type == CerebralEventTypes.ToolCall && item.TurnId == factsChanged.Id).CorrelationId!;
        checks.That(owner.Store.GetEffectResult(factsChanged.Id, factsChangedId) is { State: WorkflowEffectState.Failed, Data: null } &&
            alpha.Executed == beforeAlpha && beta.Executed == beforeBeta &&
            fixture.State.Get<AgentProfile>("agent", "fixture-profile")!.Capabilities.Contains("files"),
            "last contributor validation fact drift refuses preflight with unchanged grants and zero actual provider entry");
        fixture.Beta.BeforeValidate = null;
        fixture.ProfileChange(profile => profile.Revision = 1);
        foreach (var failure in new[] { "data", "operation" })
        {
            alpha.Failure = failure;
            var unknown = await Submit("runtime-unknown-" + failure, fixture.Alpha);
            var executions = alpha.Executed;
            var captures = fixture.Alpha.Captured;
            var betaCaptures = fixture.Beta.Captured;
            var uncertain = await owner.Runtime.RunAsync(unknown.Id);
            var effectId = uncertain.Effect!.Id;
            checks.That(uncertain.Status == WorkflowStatus.Waiting && uncertain.Effect.State == WorkflowEffectState.Unknown &&
                alpha.Executed == executions + 1 && beta.Executed == beforeBeta && alpha.ObservedDispatched &&
                owner.Store.GetEffectResult(unknown.Id, effectId) is { State: WorkflowEffectState.Unknown, Data: null },
                "provider exception after entry remains durable Unknown rather than no-dispatch Failed");
            var reread = await owner.Runtime.RunAsync(unknown.Id);
            var retried = owner.Store.Submit(unknown.Submission);
            var reopened = new WorkflowStore(new EventJournal(fixture.JournalPath)).Get(unknown.Id);
            checks.That(reread.Effect!.Id == effectId && retried.Effect!.Id == effectId &&
                reopened.Effect!.State == WorkflowEffectState.Unknown &&
                alpha.Executed == executions + 1 && beta.Executed == beforeBeta && fixture.Alpha.Captured == captures &&
                fixture.Beta.Captured == betaCaptures,
                "runtime retry duplicate submit and reopen never replay an uncertain provider effect or recapture selection");
            checks.Refuses(() => owner.Store.Resume(unknown.Id, uncertain.Revision),
                "Unknown provider outcome cannot be resumed as a new execution");
            var inspections = alpha.Reconciled;
            var reconciled = await owner.Runtime.ReconcileAsync(unknown.Id);
            checks.That(reconciled.Effect!.State == WorkflowEffectState.Unknown && alpha.Reconciled == inspections + 1 &&
                alpha.Executed == executions + 1 && beta.Executed == beforeBeta && alpha.PreviousEffectId == effectId,
                "explicit reconciliation inspects the original effect without replaying Execute");
            fixture.Alpha.RejectValidation = true;
            var refusedInspection = await owner.Runtime.ReconcileAsync(unknown.Id);
            checks.That(refusedInspection.Effect!.State == WorkflowEffectState.Unknown &&
                alpha.Reconciled == inspections + 1 && alpha.Executed == executions + 1 && beta.Executed == beforeBeta,
                "reconcile preflight refusal preserves Unknown without entering provider inspection");
            fixture.Alpha.RejectValidation = false;
        }
    }

    private sealed class Fixture
    {
        internal StateStore State { get; }
        internal EventJournal Journal { get; }
        internal WorkflowStore Receipts { get; }
        internal string JournalPath { get; }
        internal string Installation { get; } = "fixture-" + Guid.NewGuid().ToString("N");
        internal Contributor Alpha { get; } = new(WorkerContextContributionTests.Alpha, WorkflowContextHistoryProjection.ReferenceOnly);
        internal Contributor Beta { get; } = new(WorkerContextContributionTests.Beta, WorkflowContextHistoryProjection.Preserve);
        internal ServicePluginPackageBinding[] Bindings { get; } =
            [Binding(WorkerContextContributionTests.Alpha), Binding(WorkerContextContributionTests.Beta), Binding("cerebral.host")];
        internal string? PackageProblem;
        internal Fixture(string directory)
        {
            State = new(Path.Combine(directory, "data"));
            State.Put("settings", "host", new HostSettings { InstallationId = Installation, Name = "Generic context fixture",
                ModelDirectory = directory, ProjectsDirectory = directory, SharedDirectory = directory, ToolsDirectory = directory,
                BackgroundTasksEnabled = false, Workers = [], McpServers = [], Executables = [] });
            State.Put("agent", "fixture-profile", Profile());
            JournalPath = Path.Combine(directory, "engine.db");
            Journal = new(JournalPath);
            Receipts = new(Journal);
        }
        internal WorkerContextContributionSource[] Sources() =>
            [new(Bindings.Single(item => item.Id == WorkerContextContributionTests.Alpha), Alpha, Alpha.EnsureAvailable),
             new(Bindings.Single(item => item.Id == WorkerContextContributionTests.Beta), Beta, Beta.EnsureAvailable)];
        internal WorkerContextContributionHost Host(IReadOnlyList<ServicePluginPackageBinding>? bindings = null,
            IReadOnlyList<WorkerContextContributionSource>? sources = null,
            Func<string, string, WorkflowDefinitionDescriptor>? definitionDescriptor = null) =>
            new(State, new(bindings ?? Bindings), null, (_, _) => ["files"], (_, _) => PackageProblem,
                sources ?? Sources(), definitionDescriptor);
        internal WorkflowSubmission Identity(string id) => new(id, "chat-" + id, "fixture-profile", DefinitionId, "1",
            "Generic fixture request.", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, StepLimit: 8,
            Caller: new(EventActorKind.User, "owner"));
        internal WorkerExecutionSnapshot.Snapshot Snapshot() => new(WorkerExecutionSnapshot.Version, Profile(), "", "", null,
            [], ["files"], [], [], false, "fixture-route", Bindings, []);
        internal void ProfileChange(Action<AgentProfile> change)
        {
            var profile = State.Get<AgentProfile>("agent", "fixture-profile")!;
            change(profile); State.Put("agent", profile.Id, profile);
        }
        internal void ChangeInstallation(string value)
        {
            var settings = State.Get<HostSettings>("settings", "host")!;
            settings.InstallationId = value; State.Put("settings", "host", settings);
        }
    }

    private sealed class Contributor : IWorkflowContextContribution
    {
        internal string Package { get; }
        internal string Selection;
        internal int Captured, Restored, Validated, LegacyRead;
        internal WorkflowContextFacts? CaptureFacts, ValidateAcceptedFacts;
        internal WorkflowContextAcceptedFacts? RestoreFacts;
        internal WorkflowContextLegacyFacts? LegacyFacts;
        internal ContextProviderRequest? RequestTemplate;
        internal bool Available = true, RejectValidation, ChangeRequest, Stateless, ReadyLegacy;
        internal Action<WorkflowContextFacts, WorkflowContextFacts, WorkflowContextSelection, CancellationToken>? BeforeValidate;
        internal WorkflowContextDisposition Disposition = WorkflowContextDisposition.Ready;
        internal Func<WorkflowContextAcceptedFacts, WorkflowContextSelection, CancellationToken, Task>? BeforeRestore;
        public WorkflowContextContributionDescriptor Descriptor { get; }
        internal Contributor(string package, WorkflowContextHistoryProjection policy, string[]? legacyFields = null, int maxReferences = 4)
        {
            Package = package;
            Selection = package == Alpha ? "selected-alpha-A" : "selected-beta-A";
            Descriptor = new(1, package + ":contribution",
                new(package + ":resolve", 1, "Generic fixture context", [package + ".record"], maxReferences, 8192), policy,
                legacyFields is null ? [] : [new(4, legacyFields)]);
        }
        internal void EnsureAvailable()
        { if (!Available) throw new InvalidOperationException("Fixture source unavailable."); }
        public Task<WorkflowContextSelection> CaptureAsync(WorkflowContextFacts facts, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Captured++; CaptureFacts = facts;
            if (Disposition != WorkflowContextDisposition.Ready)
                return Task.FromResult(new WorkflowContextSelection(Disposition, "fixture-unavailable", null, null, null));
            return Task.FromResult(new WorkflowContextSelection(WorkflowContextDisposition.Ready, null,
                Stateless ? null : Element(new { selectedSource = Selection }), Stateless ? null : "codec:" + Selection, RequestTemplate ?? Request(Selection)));
        }
        public void Validate(WorkflowContextFacts accepted, WorkflowContextFacts current,
            WorkflowContextSelection selection, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Validated++; ValidateAcceptedFacts = accepted;
            BeforeValidate?.Invoke(accepted, current, selection, token);
            var hasCodec = selection.Payload is not null || selection.PayloadDigest is not null;
            if (RejectValidation || accepted.ProfileRole != current.ProfileRole ||
                hasCodec && (selection.Payload?.GetProperty("selectedSource").GetString() != selection.Request?.References.Single().SourceId ||
                    selection.PayloadDigest != "codec:" + selection.Request?.References.Single().SourceId))
                throw new InvalidDataException("Fixture package codec validation refused.");
        }
        public async Task<ContextProviderRequest> RestoreAsync(WorkflowContextAcceptedFacts facts,
            WorkflowContextSelection selection, CancellationToken token)
        {
            Restored++; RestoreFacts = facts;
            if (BeforeRestore is not null) await BeforeRestore(facts, selection, token);
            token.ThrowIfCancellationRequested();
            var request = selection.Request! with { ContextRevision = facts.ContextRevision, ExclusionRevision = facts.ExclusionRevision };
            return ChangeRequest ? request with
                { References = [request.References.Single() with { SourceId = "substituted-source" }] } : request;
        }
        public WorkflowContextSelection ReadLegacy(WorkflowContextLegacyFacts facts, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); LegacyRead++; LegacyFacts = facts;
            return ReadyLegacy ? new(WorkflowContextDisposition.Ready, null, null, null, Request(Selection)) :
                new(WorkflowContextDisposition.Omitted, "fixture-legacy", null, null, null);
        }
        private ContextProviderRequest Request(string source) => new(Descriptor.Provider.Id, 1,
            [new(Package + ":record", Package + ".record", source, 1, BeforeCurrentInput: true)], 4096);
    }

    private sealed class ProjectionFixture
    {
        private readonly EventJournal journal;
        private readonly WorkflowStore receipts;
        internal string RunId { get; }
        internal string ConversationId { get; }
        internal long AcceptedSequence { get; }
        internal WorkerContextContributionAdmission Alpha { get; }
        internal WorkerContextContributionAdmission Beta { get; }
        internal ProjectionFixture(EventJournal journal, WorkflowStore receipts, string label,
            string? conversationId = null, bool omitted = false)
        {
            this.journal = journal; this.receipts = receipts;
            RunId = "projection-" + label;
            ConversationId = conversationId ?? "conversation-" + label;
            Alpha = Admission(WorkerContextContributionTests.Alpha, WorkflowContextHistoryProjection.ReferenceOnly);
            Beta = Admission(WorkerContextContributionTests.Beta, WorkflowContextHistoryProjection.Preserve);
            var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, Profile(), "", "", null, [], ["files"], [], [],
                false, "fixture-route", [Binding(WorkerContextContributionTests.Alpha), Binding(WorkerContextContributionTests.Beta), Binding("cerebral.host")], [])
            {
                ContextContributions = omitted ? [] : [Alpha, Beta],
                ContextContributionInstallationId = "fixture-projection-installation"
            };
            var accepted = receipts.Submit(new(RunId, ConversationId, "fixture-profile", DefinitionId, "1", "Projection fixture.",
                null, WorkflowContract.EmptyObject, Element(snapshot), StepLimit: 64, Caller: new(EventActorKind.User, "owner")));
            AcceptedSequence = accepted.Submission.ConversationSequence;
        }
        internal string ProviderEffect(WorkerContextContributionAdmission admission, string body) =>
            Effect(admission.Descriptor.Provider.Id, Element(WorkerContextContributionSnapshot.Template(admission, 7, 2)),
                effect => ProviderResult(admission, effect, body));
        internal WorkflowCommandResult ProviderResult(WorkerContextContributionAdmission admission, string effect, string body)
        {
            var request = WorkerContextContributionSnapshot.Template(admission, 7, 2);
            var reference = request.References.Single();
            var resolution = new ContextProviderResolution(admission.Descriptor.Provider.Id, 1,
                [new(reference.Id, reference.Kind, reference.ExpectedRevision!.Value, reference.Requirement,
                    [new("user", [new TextContentPart(body)])], Priority: reference.Priority, BeforeCurrentInput: reference.BeforeCurrentInput)]);
            return new(WorkflowEffectState.Succeeded,
                Element(ContextProviderNormalizer.Normalize(admission.Descriptor.Provider, request, resolution, effect, effect)),
                ProviderReceiptId: effect);
        }
        internal string Effect(string operation, JsonElement args, WorkflowCommandResult result) => Effect(operation, args, _ => result);
        private string Effect(string operation, JsonElement args, Func<string, WorkflowCommandResult> result)
        {
            var claimed = receipts.TryClaim(RunId) ?? throw new InvalidOperationException("Exact fixture claim is unavailable.");
            var descriptor = new WorkflowCommandDescriptor(operation, 1, "Generic fixture effect", WorkflowContract.EmptyObject,
                WorkflowContract.EmptyObject, [], WorkflowReplayPolicy.Idempotent);
            var prepared = receipts.ApplyStep(RunId, claimed.Revision,
                new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(operation, 1, args)), descriptor);
            var dispatched = receipts.DispatchEffect(RunId, prepared.Revision);
            receipts.CompleteEffect(RunId, dispatched.Effect!.Id, result(dispatched.Effect.Id));
            return dispatched.Effect.Id;
        }
        internal CerebralEvent Event(string type, string effect) => journal.ReadPage(ConversationId,
            snapshotSequence: journal.GetRevision(ConversationId)).Events.Single(item => item.Type == type && item.CorrelationId == effect);
        internal void Complete()
        {
            var claimed = receipts.TryClaim(RunId)!;
            receipts.ApplyStep(RunId, claimed.Revision, new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject)));
        }
    }

    private sealed class Definition : IWorkflowDefinition
    {
        internal ContextProviderRequest? Request;
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(DefinitionId, "1", "Generic fixture workflow",
            WorkflowContract.EmptyObject, ["files"]);
        public WorkflowStep Advance(WorkflowRun snapshot) => snapshot.Signal.Kind == WorkflowSignalKind.Start
            ? new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(Request!.ProviderId, Request.ProviderVersion, Element(Request)))
            : new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject,
                Success: snapshot.Effect?.State == WorkflowEffectState.Succeeded));
    }

    private sealed record ReceiverDefinition(WorkflowDefinitionDescriptor Descriptor) : IWorkflowDefinition
    {
        public WorkflowStep Advance(WorkflowRun snapshot) =>
            new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject));
    }

    private sealed class ReadyReceipt : IWorkflowToolContextProjectionContribution
    {
        private readonly IWorkflowToolContextProjectionContribution inner =
            (IWorkflowToolContextProjectionContribution)new ProgrammingServicePlugin().ContextContributions.Single();
        public WorkflowContextContributionDescriptor Descriptor => inner.Descriptor;
        public WorkflowToolContextProjectionDescriptor ProjectionDescriptor => inner.ProjectionDescriptor;
        public Task<WorkflowContextSelection> CaptureAsync(WorkflowContextFacts facts, CancellationToken token) =>
            inner.CaptureAsync(facts with
            {
                Identity = facts.Identity with
                {
                    DefinitionId = "pegasus.conversation:chat", DefinitionVersion = "1.0.3-tool-context.1"
                },
                ToolReceiptConsumer = new(1, true)
            }, token);
        public void Validate(WorkflowContextFacts accepted, WorkflowContextFacts current,
            WorkflowContextSelection selection, CancellationToken token) =>
            throw new InvalidOperationException("The unsupported receipt callback must not run.");
        public Task<ContextProviderRequest> RestoreAsync(WorkflowContextAcceptedFacts facts,
            WorkflowContextSelection selection, CancellationToken token) =>
            throw new InvalidOperationException("The unsupported receipt callback must not run.");
        public WorkflowContextSelection ReadLegacy(WorkflowContextLegacyFacts facts, CancellationToken token) =>
            throw new InvalidOperationException("The unsupported receipt callback must not run.");
        public Task<WorkflowToolContextProjectionResolution> ProjectAsync(
            WorkflowToolContextProjectionRequest request, CancellationToken token) =>
            throw new InvalidOperationException("The unsupported receipt callback must not run.");
    }

    private sealed record Module(string Id, IReadOnlyList<IWorkflowDefinition> Workflows,
        IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;

    private sealed class FakeProvider(ContextProviderDescriptor provider) : IContextProviderCommandHandler
    {
        internal WorkflowStore? Store;
        internal int Executed, Reconciled;
        internal bool ObservedDispatched;
        internal string? Failure, LastEffect, PreviousEffectId;
        public ContextProviderDescriptor Provider => provider;
        public WorkflowCommandDescriptor Descriptor { get; } = new(provider.Id, provider.Version, provider.Description,
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, ["files"], WorkflowReplayPolicy.Idempotent);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        {
            Executed++; LastEffect = context.EffectId;
            var durable = Store!.Get(context.RunId!);
            ObservedDispatched = durable.Effect?.State == WorkflowEffectState.Dispatched &&
                durable.Effect.Id == context.EffectId && JsonElement.DeepEquals(durable.Effect.Arguments, context.Arguments);
            if (!ObservedDispatched) throw new InvalidOperationException("Fixture provider entered without exact durable dispatch.");
            if (Failure == "data") throw new InvalidDataException("Fixture post-entry data uncertainty.");
            if (Failure == "operation") throw new InvalidOperationException("Fixture post-entry operation uncertainty.");
            var request = ContextProviderNormalizer.ReadRequest(provider, context.Arguments);
            var reference = request.References.Single();
            var resolution = new ContextProviderResolution(provider.Id, provider.Version,
                [new(reference.Id, reference.Kind, reference.ExpectedRevision!.Value, reference.Requirement,
                    [new("user", [new TextContentPart("DETERMINISTIC FAKE PROVIDER BODY")])],
                    Priority: reference.Priority, BeforeCurrentInput: reference.BeforeCurrentInput)]);
            return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, Element(resolution), ProviderReceiptId: context.EffectId));
        }
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token)
        {
            Reconciled++; PreviousEffectId = context.PreviousEffect?.Id;
            return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Unknown, Error: "Fixture read-only uncertainty remains."));
        }
    }

    private static WorkerContextContributionAdmission Admission(string package, WorkflowContextHistoryProjection policy)
    {
        var contributor = new Contributor(package, policy);
        var selection = contributor.CaptureAsync(new(new("fixture", "conversation", "fixture-profile", DefinitionId, "1", null, null),
            "helper", 1, "local", "fixture"), default).GetAwaiter().GetResult();
        return WorkerContextContributionSnapshot.Freeze(Binding(package), contributor.Descriptor, selection);
    }

    private static ServicePluginPackageBinding Binding(string id) => new(id, "1.0.0", true, [], Generation: "generation-" + id);
    private static AgentProfile Profile() => new() { Id = "fixture-profile", Revision = 1, Name = "Generic fixture",
        Role = "helper", WorkerId = "local", Capabilities = ["files"], AllowedRoots = [], AllowedTools = [] };
    private static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static string Json(object? value) => JsonSerializer.Serialize(value, Compact);
    private static JsonElement Payload(int bytes)
    {
        var overhead = JsonSerializer.SerializeToUtf8Bytes(Element(new { body = "" }), Compact).Length;
        return Element(new { body = new string('x', bytes - overhead) });
    }
    private static string DeepJson(int depth)
    {
        var value = "0";
        for (var index = 0; index < depth; index++) value = "{\"child\":" + value + "}";
        return value;
    }
    private static string Fingerprint(StateStore store)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(store.DirectoryPath, "cerebral.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind,id,CAST(body AS BLOB) FROM records ORDER BY kind,id";
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0) + "/" + reader.GetString(1) + "/" +
            Convert.ToHexString(SHA256.HashData((byte[])reader.GetValue(2))));
        return string.Join("\n", keys);
    }
    private static void RequireOwned(string root, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, comparison) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(resolved), "^generic-context-[a-f0-9]{32}$"))
            throw new InvalidOperationException("Refusing cleanup outside the exact owned generic context fixture.");
    }
    private sealed class Checks(string tier)
    {
        internal int Count;
        internal void That(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("generic_context_fixture_" + tier + ":" + label);
            Count++;
        }
        internal void Refuses(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or JsonException)
            { Count++; return; }
            throw new InvalidOperationException("generic_context_fixture_expected_refusal_" + tier + ":" + label);
        }
        internal async Task RefusesAsync(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or JsonException)
            { Count++; return; }
            throw new InvalidOperationException("generic_context_fixture_expected_refusal_" + tier + ":" + label);
        }
        internal void Throws<T>(Action action, string label) where T : Exception
        {
            try { action(); } catch (T) { Count++; return; }
            throw new InvalidOperationException("generic_context_fixture_expected_type_" + tier + ":" + label);
        }
        internal async Task ThrowsAsync<T>(Func<Task> action, string label) where T : Exception
        {
            try { await action(); } catch (T) { Count++; return; }
            throw new InvalidOperationException("generic_context_fixture_expected_type_" + tier + ":" + label);
        }
    }
}
