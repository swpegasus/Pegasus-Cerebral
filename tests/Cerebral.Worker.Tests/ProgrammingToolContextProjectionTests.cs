using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Programming;

namespace Cerebral.Worker.Tests;

/// <summary>Pure package/contract fixtures. Retained content is real; new projection envelopes are synthetic.</summary>
public static class ProgrammingToolContextProjectionTests
{
    public static async Task<int> RunAsync(string leaf)
    {
        var checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Programming tool context: " + message); checks++; }
        var content13 = await File.ReadAllTextAsync(Path.Combine(leaf, "Fixtures", "compiler-13.json"));
        var content10 = await File.ReadAllTextAsync(Path.Combine(leaf, "Fixtures", "compiler-10.json"));
        Check(Hash(content13) == "a67c4ea08137945dfd468a99c3104e121a5583daf22ea207c9aecf5b3e456547" &&
            Hash(content10) == "8b833efdcb5a965979473a01dd65e2e17a4d748a69811d82276869831b5a258a",
            "only exact retained content shapes are used; no historical commandVersion/origin is invented");
        var f = new Fixture();
        Check(ReferenceEquals(f.Contribution, f.Command), "one contribution object also exports its matching provider command");
        Check(f.Contribution.Descriptor.SchemaVersion == 2 &&
            f.Contribution.Descriptor.HistoryProjection == WorkflowContextHistoryProjection.ReferenceOnly &&
            f.Projection.ProjectionDescriptor.ToolNames.SequenceEqual(new[] { "process.run", "file.write", "file.edit", "file.edit-range" }),
            "v2 marker, ReferenceOnly and closed exact relevant-tool filter are declared");
        Check(f.Command.Descriptor.RequiredCapabilities.Count == 0 && f.Provider.Provider.MaxReferences == 1 &&
            f.Provider.Provider.MaxUtf8Bytes == 32768, "pure provider adds no grant and has one bounded slot");
        Check(f.Command.Descriptor.Description == f.Provider.Provider.Description,
            "the same actual provider/command descriptions satisfy the registry identity contract");
        var omitted = await f.Contribution.CaptureAsync(f.Facts with
            { Identity = f.Facts.Identity with { DefinitionVersion = "1.0.1" }, ToolReceiptConsumer = new(1, false) }, CancellationToken.None);
        Check(omitted.Disposition == WorkflowContextDisposition.Omitted && omitted.Request is null &&
            omitted.Payload is null && omitted.PayloadDigest is null, "older consumer receives closed Omitted2");
        var selection = await f.SelectionAsync();
        Check(selection.Disposition == WorkflowContextDisposition.Ready && selection.Payload is null && selection.PayloadDigest is null &&
            f.Active is null, "stateless ready admission does not fabricate an initial Required publication");
        var editions = new[]
        {
            Cerebral.Conversation.ConversationAgentHarness.CreateRepeatedSuccessGuardEdition().Descriptor,
            Cerebral.Conversation.ConversationAgentHarness.CreateRepeatPreparedEdition().Descriptor,
            Cerebral.Conversation.ConversationAgentHarness.CreateOwnerVerifiedPreparedEdition().Descriptor
        };
        Check(editions.Select(edition => edition.Version).SequenceEqual(new[]
            { "1.0.6-repeat-guard.1", "1.0.7-repeat-prepared.1", "1.0.8-owner-verified-prepared.1" }),
            "actual Conversation factories retain exact 106/107 editions and expose only the named opt-in 108 edition");
        foreach (var edition in editions)
        {
            var supported = edition.Version != "1.0.6-repeat-guard.1";
            Check(edition.Id == "pegasus.conversation:chat" && (supported
                ? edition.ToolReceiptConsumer is { SchemaVersion: 1, Supported: true }
                : edition.ToolReceiptConsumer is null),
                "actual 106 remains unsupported while 107/108 declare the existing receipt-consumer contract: " + edition.Version);
            var versioned = new Fixture(edition.Version);
            var facts = versioned.Facts with { ToolReceiptConsumer = edition.ToolReceiptConsumer };
            var admitted = await versioned.Contribution.CaptureAsync(facts, CancellationToken.None);
            versioned.Contribution.Validate(facts, facts, admitted, CancellationToken.None);
            if (!supported)
            {
                Check(admitted.Disposition == WorkflowContextDisposition.Omitted && admitted.Request is null,
                    "historical 106 retains its actual unsupported consumer and receives no compiler context");
                continue;
            }
            Check(admitted.Disposition == WorkflowContextDisposition.Ready &&
                admitted.Request?.ProviderId == f.Provider.Provider.Id,
                "the unchanged Programming registration admits the exact supported edition: " + edition.Version);
            var versionedPass = await versioned.SendAsync("process.run", versioned.BuildArgs(),
                versioned.Data(Native("Build succeeded.", 0), exit: 0), WorkflowEffectState.Succeeded, "versioned-pass");
            var versionedPacket = Packet(versioned.Active!);
            Check(versionedPass.Disposition == WorkflowToolContextProjectionDisposition.Publish &&
                versionedPacket.GetProperty("validationFollowthrough").GetProperty("compilationVerified").GetBoolean() &&
                versionedPacket.GetProperty("validationFollowthrough").GetProperty("behaviorVerification").GetString() ==
                    "not-established-by-compiler-receipt",
                "the actual provider preserves compilation-only followthrough on the exact edition: " + edition.Version);
        }
        var unsupported108 = await f.Contribution.CaptureAsync(f.Facts with
        {
            Identity = f.Facts.Identity with { DefinitionVersion = "1.0.8-owner-verified-prepared.1" },
            ToolReceiptConsumer = new(1, false)
        }, CancellationToken.None);
        Check(unsupported108.Disposition == WorkflowContextDisposition.Omitted && unsupported108.Request is null,
            "the opt-in version name alone never bypasses an unsupported consumer declaration");
        var direct = await f.Command.ExecuteAsync(new("direct", Submission(), CerebralJson.Element(selection.Request)), CancellationToken.None);
        Check(direct.State == WorkflowEffectState.Failed, "direct v1 initial provider dispatch refuses explicitly");
        var restored = await f.Contribution.RestoreAsync(new(f.Facts, null, 1, 99, 3), selection, CancellationToken.None);
        Check(restored.ContextRevision == 99 && restored.ExclusionRevision == 3 &&
            restored.References.Single().ExpectedRevision is null, "Restore only rebases the immutable base request");
        var r13 = await f.BuildAsync(content13, "compiler-13");
        Check(r13.Disposition == WorkflowToolContextProjectionDisposition.Publish && r13.Resolution!.Sources.Count == 1,
            "complete retained 13-error shape publishes one raw source");
        var first = f.Active!; var firstBytes = Serialize(first); var packet13 = Packet(first);
        Check(first.Requirement == ContextRequirement.Required && first.Complete && first.SourceRevision == 1 &&
            packet13.GetProperty("shown").GetInt32() == 13 && packet13.GetProperty("total").GetInt32() == 13 &&
            packet13.GetProperty("complete").GetBoolean(), "all13 distinct diagnostics are exact Required intake");
        Check(Codes(packet13, "CS8618") == 11 && Codes(packet13, "CS0168") == 2 &&
            packet13.GetProperty("diagnostics").EnumerateArray().Any(row => row.GetProperty("message").GetString()!.Contains("requestTokensPerSecond", StringComparison.Ordinal)),
            "whole batch includes diagnostics beyond the old four-row preview");
        Check(first.Messages.Count == 1 && first.Messages.Single().Content.Count == 2 &&
            first.Messages.SelectMany(message => message.Content).All(part => part is TextContentPart) &&
            ((TextContentPart)first.Messages.Single().Content[0]).Text.Contains("complete compact JSON packet", StringComparison.Ordinal),
            "actual publication is text-only and identifies the complete receipt-attributed JSON format");
        Check(PacketText(first) == JsonSerializer.Serialize(packet13, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false }) &&
            packet13.GetProperty("schema").GetString() == "pegasus.programming.compiler-diagnostic-batch/1" &&
            packet13.GetProperty("buildEffectId").GetString() == r13.Link.ToolEffectId &&
            packet13.GetProperty("savedEffectSha256").GetString() == r13.Link.SavedEffectSha256 &&
            packet13.GetProperty("projectPath").GetString() == Path.Combine(f.Root, "InferenceStatsSummary.csproj") &&
            packet13.GetProperty("configuration").GetString() == "Debug" &&
            packet13.GetProperty("buildState").GetString() == "Failed" && packet13.GetProperty("exitCode").GetInt32() == 1,
            "canonical text JSON retains original packet schema, receipt digest, project/configuration and actual outcome");
        var failedFollowthrough = packet13.GetProperty("validationFollowthrough");
        Check(!failedFollowthrough.GetProperty("compilationVerified").GetBoolean() &&
            failedFollowthrough.GetProperty("behaviorVerification").GetString() == "not-established-by-compiler-receipt" &&
            failedFollowthrough.GetProperty("nextAction").GetString() == "repair-compiler-diagnostics",
            "a complete compiler failure supplies repair advice without claiming behavior validation");

        Check(failedFollowthrough.GetProperty("evidenceScope").GetString() == "this-compiler-record" &&
            failedFollowthrough.GetProperty("nextActionCondition").GetString() == "when-compiler-diagnostics-remain",
            "compiler failure advice is scoped to this record and remaining diagnostics");


        Check(!Serialize(r13.Resolution).Contains("exactReadHandle", StringComparison.Ordinal), "package emits raw resolution, never a minted handle");
        var mutation = await f.MutateAsync("changed");
        Check(mutation.Disposition == WorkflowToolContextProjectionDisposition.StateOnly && mutation.Resolution is null &&
            Serialize(f.Active) == firstBytes && f.Revision == 1 && State(f).GetProperty("sourceGeneration").GetInt64() == 1,
            "confirmed mutation updates opaque generation and preserves exact last actual batch");
        Check(State(f).GetProperty("lastBuild").GetProperty("effectId").GetString() == "compiler-13" &&
            State(f).GetProperty("lastBuild").GetProperty("sourceGeneration").GetInt64() == 0,
            "an edit never relabels or repairs the old actual compiler observation");
        // This is a deterministic normalization/compaction contract fixture, not host-origin proof.
        var required = new ContextContribution(first.Id, f.Provider.Provider.Id, first.Kind, first.SourceRevision,
            first.Requirement, first.Messages, "fixture-normalized-handle", first.Complete);
        var reducible = new ContextContribution("fixture.history", "fixture", "history", 1, ContextRequirement.Reducible,
            [new("user", [new TextContentPart("old reference")])]);
        var window = new ContextWindow(7, 0, [required, reducible]);
        Check(ContextValidation.GetCoveredManifest(window).Select(x => x.Id).SequenceEqual(["fixture.history"]),
            "compaction fixture cannot cover Required diagnostic text");
        var checkpoint = new ContextCheckpoint("fixture.checkpoint", 7, 0, ContextValidation.GetCoveredManifest(window),
            "Old reference summarized.", "fixture.compaction") { SourceLocators = ContextValidation.GetCoveredLocators(window) };
        ContextValidation.ValidateCandidate(window, checkpoint, "fixture.compaction");
        Check(Serialize(f.Active) == firstBytes, "compaction metadata does not replace the retained actual diagnostic batch");
        var r10 = await f.BuildAsync(content10, "compiler-10");
        var packet10 = Packet(f.Active!);
        Check(r10.Disposition == WorkflowToolContextProjectionDisposition.Publish && f.Revision == 2 &&
            packet10.GetProperty("shown").GetInt32() == 10 && Codes(packet10, "CS8618") == 8 && Codes(packet10, "CS0168") == 2,
            "only newer same-project/config actual compiler receipt supersedes13 with10");
        Check(packet10.GetProperty("buildEffectId").GetString() == "compiler-10" &&
            packet10.GetProperty("sourceGeneration").GetInt64() == 1 && Serialize(first) == firstBytes,
            "new batch has exact new anchors and does not mutate historical source");
        var replay = await f.BuildAsync(content10, "compiler-10");
        Check(replay.Disposition == WorkflowToolContextProjectionDisposition.StateOnly && f.Revision == 2,
            "duplicate observation cannot add a source revision or progress");
        var beforeReplay = f.Snapshot();
        var request = await f.RequestAsync("process.run", f.BuildArgs(), f.Data(content10), WorkflowEffectState.Failed, "reconcile-fixture");
        var once = await f.Projection.ProjectAsync(request, CancellationToken.None);
        var twice = await f.Projection.ProjectAsync(request, CancellationToken.None);
        Check(Serialize(once) == Serialize(twice) && f.Snapshot() == beforeReplay,
            "same pure frozen request recomputes identically without state/store effects");

        foreach (var mode in new[] { "skip", "identical", "missing", "truncated", "partial", "wrong-path", "wrong-request" })
        {
            var g = new Fixture(); await g.BuildAsync(content13, "initial");
            var active = Serialize(g.Active); var result = await g.MutateAsync(mode);
            Check(result.Disposition == WorkflowToolContextProjectionDisposition.StateOnly && Serialize(g.Active) == active &&
                State(g).GetProperty("sourceGeneration").GetInt64() == 0, "no-op/unproved mutation preserves generation and Required source: " + mode);
        }
        foreach (var mode in new[] { "changed", "create", "edit", "range" })
        {
            var g = new Fixture(); await g.BuildAsync(content13, "initial"); var active = Serialize(g.Active);
            var result = await g.MutateAsync(mode);
            Check(result.Disposition == WorkflowToolContextProjectionDisposition.StateOnly &&
                State(g).GetProperty("sourceGeneration").GetInt64() == 1 && Serialize(g.Active) == active,
                "complete confirmed mutation advances current generation without clearing actual compiler evidence: " + mode);
        }
        var initialMutation = new Fixture(); var stateOnly = await initialMutation.MutateAsync("create");
        Check(stateOnly.Disposition == WorkflowToolContextProjectionDisposition.StateOnly && initialMutation.Active is null &&
            State(initialMutation).GetProperty("sourceGeneration").GetInt64() == 1, "state-only before first build preserves absence, not a fake initial source");

        foreach (var mode in new[] { "unknown", "foreign-project", "foreign-config", "partial", "unsupported", "huge",
            "many", "long-row", "missing-stderr", "contradictory-success" })
        {
            var g = new Fixture(); await g.BuildAsync(content13, "initial"); var before = g.Snapshot();
            var args = g.BuildArgs(mode == "foreign-config" ? "Release" : "Debug", mode == "foreign-project" ? "Other.csproj" : null);
            var output = mode switch
            {
                "unsupported" => Native("project.csproj : error MSB1001: Unknown switch."),
                "huge" => Native(new string('x', 65537)),
                "many" => Native(string.Join('\n', Enumerable.Range(1, 17).Select(n => $"Program.cs({n},1): error CS1002: ; expected"))),
                "long-row" => Native("error CS1002: " + new string('x', 2048)),
                "missing-stderr" => JsonSerializer.Serialize(new { exitCode = 1, stdout = "error CS1002: ; expected" }),
                "contradictory-success" => Native("error CS1002: ; expected", 0),
                _ => content10
            };
            var savedState = mode == "unknown" ? WorkflowEffectState.Unknown :
                mode == "contradictory-success" ? WorkflowEffectState.Succeeded : WorkflowEffectState.Failed;
            var data = g.Data(output, mode == "partial", mode == "contradictory-success" ? 0 : 1);
            var result = await g.SendAsync("process.run", args, data, savedState, "fault-" + mode);
            Check(result.Disposition == WorkflowToolContextProjectionDisposition.Blocked && result.NextState is null &&
                result.Resolution is null && g.Snapshot() == before, "invalid/incomplete/unsupported evidence blocks without clearing state/source: " + mode);
            if (mode == "many") Check(result.Reason!.Contains("shown=16; total=17; complete=false", StringComparison.Ordinal),
                "fully scanned count-cap distinguishes shown16 from known total17");
        }
        var passing = new Fixture(); await passing.BuildAsync(content13, "failed");
        var pass = await passing.SendAsync("process.run", passing.BuildArgs(), passing.Data(Native("Build succeeded.", 0), false, 0),
            WorkflowEffectState.Succeeded, "passing-build");
        Check(pass.Disposition == WorkflowToolContextProjectionDisposition.Publish &&
            Packet(passing.Active!).GetProperty("total").GetInt32() == 0 &&
            Packet(passing.Active!).GetProperty("compilerOutcome").GetString() == "passed",
            "a newer actual passing compiler receipt alone can publish zero compiler errors");
        var passingPacket = Packet(passing.Active!);
        var followthrough = passingPacket.GetProperty("validationFollowthrough");
        Check(followthrough.GetProperty("compilationVerified").GetBoolean() &&
            followthrough.GetProperty("behaviorVerification").GetString() == "not-established-by-compiler-receipt",
            "an actual compiler pass explicitly establishes compilation only, never a behavior pass");
        Check(followthrough.GetProperty("nextAction").GetString() == "use-supplied-admitted-behavior-check" &&
            followthrough.GetProperty("checkSource").GetString() == "accepted-task-or-host-admitted-context",
            "the next validation action exposes the supplied admitted check without inventing checker coordinates");
        Check(followthrough.GetProperty("evidenceScope").GetString() == "this-compiler-record" &&
            followthrough.GetProperty("nextActionCondition").GetString() == "when-no-current-typed-checker-pass-is-retained",
            "compiler-success followthrough is conditional and makes no global claim that behavior is unverified");
        var followthroughText = ((TextContentPart)passing.Active!.Messages.Single().Content[0]).Text;
        Check(followthroughText.Contains("A successful build proves compilation, not the requested behavior.", StringComparison.Ordinal) &&
            followthroughText.Contains("class library is not a standalone app", StringComparison.Ordinal) &&
            followthroughText.Contains("admitted host checker", StringComparison.Ordinal),
            "the actual compiler-success contribution includes the accepted shared artifact-aware guidance");
        Check(followthroughText.Contains("If a current passing receipt is already retained", StringComparison.Ordinal) &&
            followthroughText.Contains("without rerunning that unchanged check", StringComparison.Ordinal) &&
            followthroughText.Contains("never invent a checker or its arguments", StringComparison.Ordinal),
            "follow-through respects existing current checks and missing admission rather than causing repeated success or invented routes");
        var guidanceWords = System.Text.RegularExpressions.Regex.Replace(followthroughText, @"\s+", " ");
        Check(guidanceWords.Contains("Compiled DLL/EXE files are binary artifacts, not text source.", StringComparison.Ordinal) &&
            guidanceWords.Contains("Do not use file.read or file.inspect as a binary reader.", StringComparison.Ordinal) &&
            guidanceWords.Contains("canonical original source and manifest metadata", StringComparison.Ordinal) &&
            guidanceWords.Contains("only when it is advertised and permitted", StringComparison.Ordinal),
            "the actual publication routes compiled artifacts to source/metadata without inventing binary-tool support");
        Check(guidanceWords.Contains("After a complete current typed checker pass, retain it", StringComparison.Ordinal) &&
            guidanceWords.Contains("remaining verify/present/final steps required by the accepted task", StringComparison.Ordinal) &&
            guidanceWords.Contains("Reuse current verification and an existing pending presentation", StringComparison.Ordinal) &&
            guidanceWords.Contains("do not create a second presentation or rerun unchanged successful checks", StringComparison.Ordinal),
            "a current typed pass advances only remaining accepted steps and reuses current verification/presentation");
        Check(followthroughText.Contains("This compiler record does not establish behavior or completion authority.", StringComparison.Ordinal) &&
            followthroughText.Contains("if no complete current typed checker pass is retained", StringComparison.Ordinal) &&
            followthroughText.Contains("confirm it is a complete typed host receipt for the current inputs", StringComparison.Ordinal),
            "compiler facts stay record-scoped while conditional guidance recognizes a current successful behavior receipt");
        Check(passingPacket.GetProperty("buildEffectId").GetString() == "passing-build" &&
            passingPacket.GetProperty("savedEffectSha256").GetString() == pass.Link.SavedEffectSha256 &&
            passingPacket.GetProperty("diagnostics").GetArrayLength() == 0 &&
            JsonSerializer.SerializeToUtf8Bytes(pass.Resolution, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false }).Length <= 32768 - 4096,
            "the added checklist preserves complete compiler receipt attribution and the original publication budget");

        var duplicates = new Fixture(); const string a = "Program.cs(1,1): error CS1002: ; expected";
        const string b = "Program.cs(2,1): error CS1003: Syntax error";
        await duplicates.BuildAsync(Native(a + "\n" + b), "one");
        var fingerprint = Packet(duplicates.Active!).GetProperty("diagnosticFingerprint").GetString();
        await duplicates.BuildAsync(Native(b + "\n" + a + "\n" + b), "two");
        Check(Packet(duplicates.Active!).GetProperty("total").GetInt32() == 2 &&
            Packet(duplicates.Active!).GetProperty("diagnosticFingerprint").GetString() == fingerprint,
            "one shared parser deduplicates summary/order noise with stable fingerprint");

        var escaped = new Fixture(); const string escapedMessage = "Expected \"quoted\" member \\path\\ with caf\u00e9 <value> & evidence";
        var escapedResult = await escaped.BuildAsync(Native("Program.cs(1,1): error CS1002: " + escapedMessage), "escaped-diagnostic");
        Check(escapedResult.Disposition == WorkflowToolContextProjectionDisposition.Publish &&
            Packet(escaped.Active!).GetProperty("diagnostics")[0].GetProperty("message").GetString() == escapedMessage &&
            escaped.Active!.Messages.SelectMany(message => message.Content).All(part => part is TextContentPart),
            "complete text JSON preserves quoted, backslash, Unicode and markup-like diagnostic values as passive data");
        Check(JsonSerializer.SerializeToUtf8Bytes(escapedResult.Resolution,
                new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false }).Length <= 32768 - 4096,
            "publication allowance measures actual serialized text JSON escaping with the normalization reserve");
        var escapedBound = new Fixture(); await escapedBound.BuildAsync(content13, "initial"); var beforeEscapedBound = escapedBound.Snapshot();
        var escapedRows = string.Join('\n', Enumerable.Range(1, 16).Select(n =>
            $"Program.cs({n},1): error CS1002: " + string.Concat(Enumerable.Repeat("\"\\", 150))));
        var escapedOverflow = await escapedBound.BuildAsync(Native(escapedRows), "escaped-publication-overflow");
        Check(escapedOverflow.Disposition == WorkflowToolContextProjectionDisposition.Blocked && escapedOverflow.NextState is null &&
            escapedOverflow.Resolution is null && escapedOverflow.Reason!.Contains("publication exceeds", StringComparison.Ordinal) &&
            escapedBound.Snapshot() == beforeEscapedBound,
            "serialized text JSON escaping overflow refuses intact without clipping rows or adopting state/source");

        foreach (var shape in new[] { "native", "pao" })
        foreach (var separator in new[] { "\t", "\u2003" })
        {
            var whitespace = new Fixture();
            var output = "A.cs(1,1): error CS1002: first\nB.cs(2,1):" + separator + "error" + separator + "CS1003: second";
            var produced = shape == "native" ? Native(output) : JsonSerializer.Serialize(new
                { data = new { exit = 1, @out = output, err = "", out_truncated = false, err_truncated = false, timed_out = false } });
            var result = await whitespace.BuildAsync(produced, "whitespace-build");
            var packet = Packet(whitespace.Active!);
            Check(result.Disposition == WorkflowToolContextProjectionDisposition.Publish &&
                packet.GetProperty("shown").GetInt32() == 2 && packet.GetProperty("total").GetInt32() == 2 &&
                packet.GetProperty("complete").GetBoolean() && Codes(packet, "CS1002") == 1 && Codes(packet, "CS1003") == 1,
                "producer envelope retains every supported whitespace diagnostic row: " + shape + "/" + (separator == "\t" ? "tab" : "unicode-space"));
        }
        foreach (var leading in new[] { true, false })
        {
            var padding = new Fixture(); const string diagnostic = "Program.cs(1,1): error CS1002: first";
            var atBound = leading ? diagnostic.PadLeft(2048) : diagnostic.PadRight(2048);
            var admitted = await padding.BuildAsync(Native(atBound), "row-at-bound");
            Check(admitted.Disposition == WorkflowToolContextProjectionDisposition.Publish &&
                Packet(padding.Active!).GetProperty("total").GetInt32() == 1,
                "original diagnostic row at2048 characters is supported before padding normalization");
            var before = padding.Snapshot();
            var oversized = await padding.BuildAsync(Native(leading ? " " + atBound : atBound + " "), "row-over-bound");
            Check(oversized.Disposition == WorkflowToolContextProjectionDisposition.Blocked && oversized.NextState is null &&
                oversized.Resolution is null && oversized.Reason!.Contains("compiler-row-character-bound", StringComparison.Ordinal) &&
                padding.Snapshot() == before, "original2049-character diagnostic row cannot hide overflow in trimmed padding");
        }

        foreach (var mode in new[] { "state-missing-field", "state-unknown-field", "state-oversize", "wrong-link",
            "wrong-source-revision", "wrong-requirement", "callback-oversize", "publication-bound" })
        {
            var g = new Fixture(); await g.BuildAsync(content13, "initial"); var before = g.Snapshot();
            var r = await g.RequestAsync("process.run", g.BuildArgs(), g.Data(content10), WorkflowEffectState.Failed, "input-fault");
            if (mode.StartsWith("state-", StringComparison.Ordinal))
            {
                var node = JsonNode.Parse(r.PriorState!.Value.GetRawText())!.AsObject();
                if (mode == "state-missing-field") node.Remove("sourceGeneration");
                else node["extra"] = mode == "state-oversize" ? new string('x', 8192) : "unrecognized";
                r = r with { PriorState = CerebralJson.Element(node) };
            }
            else if (mode == "wrong-link") r = r with { Link = r.Link with { PreviousProjectionEffectId = "wrong" } };
            else if (mode is "wrong-source-revision" or "wrong-requirement")
            {
                var reference = r.PublicationRequest.References[0];
                reference = mode == "wrong-source-revision" ? reference with { ExpectedRevision = reference.ExpectedRevision + 1 } :
                    reference with { Requirement = ContextRequirement.Optional };
                r = r with { PublicationRequest = r.PublicationRequest with { References = [reference] } };
            }
            else if (mode == "callback-oversize") r = r with { SavedEffect = r.SavedEffect with { Data = g.Data(new string('x', 330000)) } };
            else
            {
                var many = Native(string.Join('\n', Enumerable.Range(1, 16).Select(n =>
                    $"Program.cs({n},1): error CS1002: " + new string('q', 1950))));
                r = await g.RequestAsync("process.run", g.BuildArgs(), g.Data(many), WorkflowEffectState.Failed, "large-publication");
            }
            var result = await g.Projection.ProjectAsync(r, CancellationToken.None);
            Check(result.Disposition == WorkflowToolContextProjectionDisposition.Blocked && result.NextState is null &&
                result.Resolution is null && g.Snapshot() == before, "bounded/strict input or publication refuses without clipping/adoption: " + mode);
        }
        var cancelled = new Fixture(); await cancelled.BuildAsync(content13, "initial"); var old = Serialize(cancelled.Active);
        var cancel = await cancelled.SendAsync("process.run", cancelled.BuildArgs(), null, WorkflowEffectState.Cancelled, "cancelled");
        Check(cancel.Disposition == WorkflowToolContextProjectionDisposition.StateOnly && Serialize(cancelled.Active) == old,
            "cancelled source effect cannot replace compiler evidence");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var observedCancellation = false;
        try
        {
            var r = await cancelled.RequestAsync("process.run", cancelled.BuildArgs(), cancelled.Data(content10), WorkflowEffectState.Failed, "cancel-request");
            await cancelled.Projection.ProjectAsync(r, cancellation.Token);
        }
        catch (OperationCanceledException) { observedCancellation = true; }
        Check(observedCancellation, "pure callback honors cancellation without a late successful result");
        Console.WriteLine("PASS toolContext=" + checks + " tier=deterministic-package-contract-fixtures retainedContent=13,10 modelCalls=0 activeRuntimeCalls=0");
        return checks;
    }

    private static int Codes(JsonElement packet, string code) => packet.GetProperty("diagnostics").EnumerateArray()
        .Count(row => row.GetProperty("code").GetString() == code);
    private static string PacketText(ContextProviderSource source) => ((TextContentPart)source.Messages.Single().Content[1]).Text;
    private static JsonElement Packet(ContextProviderSource source)
    {
        using var document = JsonDocument.Parse(PacketText(source));
        return document.RootElement.Clone();
    }
    private static JsonElement State(Fixture f) => f.Prior!.Value;
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, CerebralJson.Options);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Native(string output, int exit = 1) => JsonSerializer.Serialize(new { exitCode = exit, stdout = output, stderr = "" });
    private static WorkflowSubmission Submission() => new("fixture-run", "fixture-conversation", "fixture-profile",
        "pegasus.conversation:chat", "1.0.3-tool-context.1", "fixture only", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject);

    private sealed class Fixture(string definitionVersion = "1.0.3-tool-context.1")
    {
        private readonly ProgrammingServicePlugin plugin = new();
        private int sequence;
        public IWorkflowContextContribution Contribution => ((IServiceWorkflowContextModule)plugin).ContextContributions.Single();
        public IWorkflowToolContextProjectionContribution Projection => (IWorkflowToolContextProjectionContribution)Contribution;
        public IWorkflowCommandHandler Command => ((IWorkflowModule)plugin).Commands.Single();
        public IContextProviderCommandHandler Provider => (IContextProviderCommandHandler)Command;
        public WorkflowContextFacts Facts { get; } = new(new("fixture-run", "fixture-conversation", "fixture-profile",
            "pegasus.conversation:chat", definitionVersion, null, null), "helper", 1, "local", "fixture-installation") { ToolReceiptConsumer = new(1, true) };
        public string Root => Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "build", "Core", "OwnerQuiescence",
            "ConversationProgress-sample-source", "InferenceStatsSummary"));
        public JsonElement? Prior { get; private set; }
        public ContextProviderSource? Active { get; private set; }
        public long? Revision { get; private set; }
        private string? previous;
        private long contextRevision = 1;
        public string Snapshot() => Serialize(new { Prior, Active, Revision, previous, contextRevision });
        public Task<WorkflowContextSelection> SelectionAsync() => Contribution.CaptureAsync(Facts, CancellationToken.None);
        public JsonElement BuildArgs(string configuration = "Debug", string? project = null) => CerebralJson.Element(new
        { executableId = "dotnet", workingDirectory = Root, arguments = new[] { "build", project ?? "InferenceStatsSummary.csproj", "--configuration", configuration, "--nologo", "-m:1" } });
        public JsonElement Data(string content, bool truncated = false, int exit = 1) => CerebralJson.Element(new
        {
            name = "process.run", content, truncated,
            evidence = new ToolExecutionEvidence(exit == 0 ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed, "fixture", State: "completed", ExitCode: exit),
            followUpRequired = false
        });
        public Task<WorkflowToolContextProjectionResolution> BuildAsync(string content, string id) =>
            SendAsync("process.run", BuildArgs(), Data(content), WorkflowEffectState.Failed, id);
        public async Task<WorkflowToolContextProjectionRequest> RequestAsync(string name, JsonElement arguments, JsonElement? data,
            WorkflowEffectState state, string effectId)
        {
            var selection = await SelectionAsync(); var projectionId = "projection-" + ++sequence;
            var saved = new WorkflowToolContextSavedEffect(1, Facts.Identity.RequestId, effectId, "cerebral.host:tool", 1,
                CerebralJson.Element(new { name, arguments, workingDirectory = Root }), state, data, null, null);
            var slot = new WorkflowToolContextSlotExpectation(Projection.ProjectionDescriptor.PublicationTemplate.References[0].Id, Revision);
            var link = new WorkflowToolContextProjectionLink(1, Contribution.Descriptor.Id, Facts.Identity.RequestId, projectionId,
                effectId, SavedDigest(saved), previous, slot);
            var template = Projection.ProjectionDescriptor.PublicationTemplate;
            var publication = template with { ContextRevision = contextRevision, ExclusionRevision = 0,
                References = [template.References[0] with { ExpectedRevision = checked((Revision ?? 0) + 1) }] };
            return new(1, new(Facts, null, 1, contextRevision, 0), selection, link, name, saved, Prior, publication);
        }
        public async Task<WorkflowToolContextProjectionResolution> SendAsync(string name, JsonElement arguments, JsonElement? data,
            WorkflowEffectState state, string effectId)
        {
            var request = await RequestAsync(name, arguments, data, state, effectId);
            var result = await Projection.ProjectAsync(request, CancellationToken.None);
            if (result.Disposition is WorkflowToolContextProjectionDisposition.StateOnly or WorkflowToolContextProjectionDisposition.Publish)
            {
                // Simulated adoption, not authenticated host-origin or Engine integration proof.
                Prior = result.NextState?.Clone(); previous = result.Link.ProjectionEffectId;
                if (result.Disposition == WorkflowToolContextProjectionDisposition.Publish)
                { Active = result.Resolution!.Sources.Single(); Revision = Active.SourceRevision; contextRevision++; }
            }
            return result;
        }
        public Task<WorkflowToolContextProjectionResolution> MutateAsync(string mode)
        {
            const string text = "class Changed { }";
            var path = Path.Combine(Root, "Program.cs"); var before = new string('a', 64);
            var after = mode == "identical" ? before : mode == "create" ? Hash(text) : new string('b', 64);
            var name = mode == "edit" ? "file.edit" : mode == "range" ? "file.edit-range" : "file.write";
            object args = mode == "range" ? new { path, expectedSha256 = before, startLine = 1, lineCount = 1, newText = text } :
                mode == "edit" ? new { path, expectedSha256 = before, oldText = "old", newText = text } :
                (object)new { path, expectedSha256 = before, content = text, collision = mode == "create" ? "fail" : mode == "skip" ? "skip" : "replace" };
            var content = mode == "skip" ? JsonSerializer.Serialize(new { path, skipped = true }) :
                mode == "missing" ? "no mutation proof" :
                mode == "range" ? JsonSerializer.Serialize(new { path, sha256Before = before, sha256 = after, bytes = text.Length, startLine = 1, lineCount = 1, changed = true }) :
                JsonSerializer.Serialize(new { path = mode == "wrong-path" ? Path.Combine(Root, "Other.cs") : path, sha256 = after, bytes = text.Length });
            var data = CerebralJson.Element(new { name, content, truncated = mode == "truncated",
                evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Succeeded, "fixture", RequestId: mode == "wrong-request" ? "other" : null, Partial: mode == "partial"),
                followUpRequired = false });
            return SendAsync(name, CerebralJson.Element(args), data, WorkflowEffectState.Succeeded, "mutation-" + (sequence + 1));
        }
    }

    // Synthetic host fixture codec follows the reviewed contract; it is not historical origin proof.
    private static string SavedDigest(WorkflowToolContextSavedEffect saved)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", saved.SchemaVersion);
            writer.WriteString("runId", saved.RunId); writer.WriteString("effectId", saved.EffectId);
            writer.WriteString("commandId", saved.CommandId); writer.WriteNumber("commandVersion", saved.CommandVersion);
            writer.WritePropertyName("arguments"); saved.Arguments.WriteTo(writer); writer.WriteString("state", saved.State.ToString());
            writer.WritePropertyName("data"); if (saved.Data is { } data) data.WriteTo(writer); else writer.WriteNullValue();
            writer.WriteString("error", saved.Error); writer.WriteString("providerReceiptId", saved.ProviderReceiptId);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}

