using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static void RunReceiptProgressAllowanceChecks(Action<bool, string> check)
    {
        static int Attempts(Fixture f) => f.Step.State.GetProperty("receiptReadAttempts").GetInt32();
        static int Requested(Fixture f) => f.Step.State.TryGetProperty("receiptReadRequestedBytes", out var bytes) ? bytes.GetInt32() : 0;
        static int Pages(Fixture f) => f.Step.State.GetProperty("receiptPages").GetArrayLength();
        static void Read(Fixture f, string effect, int offset, long? revision = null)
        {
            if (revision is { } value) f.Model(new { action = "receipt", effectId = effect, offset, expectedRevision = value });
            else f.Model(new { action = "receipt", effectId = effect, offset });
        }
        static void ToolProgress(Fixture f)
        {
            f.Describe("file.read"); f.Catalog(exact: true);
            f.Model(new { action = "tool", name = "file.read", arguments = new { path = "unrelated-allowance-fixture" } });
            f.Tool(true, "Unrelated confirmed fixture read.");
        }
        static void SeedAllowance(Fixture f, int attempts, int windows)
        {
            var state = JsonNode.Parse(f.Step.State.GetRawText())!.AsObject();
            state["receiptReadAccountingVersion"] = 1;
            state["receiptReadAttempts"] = attempts;
            state["receiptReadRequestedBytes"] = windows * WorkflowReceiptPage.MaximumPageBytes;
            state["receiptPages"] = new JsonArray(Enumerable.Range(0, windows).Select(i =>
                JsonSerializer.SerializeToNode(new { effectId = "old-window-" + i, offset = 0,
                    expectedRevision = (long?)null, limit = WorkflowReceiptPage.MaximumPageBytes, failed = true },
                    CerebralJson.Options)).ToArray());
            f.RestoreState(CerebralJson.Element(state));
        }

        var large = new Fixture();
        var payload = new string('p', 180 * 1024) + " exact-final-tail";
        var rebuilt = new StringBuilder();
        var offset = 0; var pageCount = 0; var compactions = 0;
        while (offset < payload.Length)
        {
            Read(large, "large-confirmed-receipt", offset);
            check(large.Is("receipt") && large.Command.Arguments.GetProperty("limit").GetInt32() == WorkflowReceiptPage.MaximumPageBytes,
                "forward receipt lookup uses the existing host 16 KiB page contract beyond eight pages");
            var end = Math.Min(payload.Length, offset + WorkflowReceiptPage.MaximumPageBytes);
            var text = payload[offset..end];
            large.ReceiptPage(text, WorkflowEffectState.Succeeded,
                nextOffset: end < payload.Length ? end : null, totalBytes: payload.Length, revision: 20 + pageCount);
            rebuilt.Append(text); offset = end; pageCount++;
            if (large.Is("compact")) { large.CompactSuccess(); compactions++; }
            check(large.Is("generate") && JsonSerializer.SerializeToUtf8Bytes(large.Step.State, CerebralJson.Options).Length <= WorkflowContract.MaximumStateBytes,
                "large receipt progression retains the saved-state byte guard and returns through generation");
            check(large.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
                "paged receipt references never manufacture tool execution credit");
        }
        check(pageCount > 8 && rebuilt.ToString() == payload && Pages(large) == pageCount && Attempts(large) == pageCount &&
              Requested(large) == pageCount * WorkflowReceiptPage.MaximumPageBytes && compactions > 0,
            "a receipt larger than 128 KiB is reconstructed across real harness compaction fixtures with fixed requested-window accounting");

        var duplicate = new Fixture(); Read(duplicate, "confirmed", 0);
        duplicate.ReceiptPage("first", WorkflowEffectState.Failed, nextOffset: 5, totalBytes: 9);
        var ledger = duplicate.Step.State.GetProperty("receiptPages").GetRawText();
        var requested = Requested(duplicate);
        ToolProgress(duplicate);
        check(duplicate.Step.State.GetProperty("receiptPages").GetRawText() == ledger && Requested(duplicate) == requested && Attempts(duplicate) == 1,
            "an unrelated tool does not reset receipt coordinates, attempts or requested-window charges");
        Read(duplicate, "confirmed", 0, 99);
        check(duplicate.Is("generate") && duplicate.LastContributionText.Contains("conversation.receipt_no_progress", StringComparison.Ordinal) &&
              Attempts(duplicate) == 2 && Pages(duplicate) == 1 && Requested(duplicate) == requested,
            "changing a revision argument cannot reread an immutable confirmed range or replenish its allowance");
        Read(duplicate, "confirmed", 5);
        duplicate.ReceiptPage("tail", WorkflowEffectState.Failed, totalBytes: 9, revision: 22);
        check(duplicate.Is("generate") && Attempts(duplicate) == 3 && Pages(duplicate) == 2,
            "confirmed immutable receipts may advance while the containing run revision grows");

        var skipped = new Fixture(); Read(skipped, "cursor", 0);
        skipped.ReceiptPage("part", WorkflowEffectState.Succeeded, nextOffset: 4, totalBytes: 8);
        Read(skipped, "cursor", 7);
        check(skipped.Is("generate") && skipped.LastContributionText.Contains("conversation.receipt_offset", StringComparison.Ordinal) &&
              skipped.LastContributionText.Contains("nextOffset 4", StringComparison.Ordinal) && Attempts(skipped) == 2,
            "non-advertised forward offsets identify the exact next cursor without dispatching another receipt command");

        var complete = new Fixture(); Read(complete, "complete", 0);
        complete.ReceiptPage("done", WorkflowEffectState.Failed, totalBytes: 4);
        Read(complete, "complete", 1);
        check(complete.Is("generate") && complete.LastContributionText.Contains("conversation.receipt_complete", StringComparison.Ordinal) &&
              complete.LastContributionText.Contains("nextOffset is null", StringComparison.Ordinal) && Attempts(complete) == 2 && Pages(complete) == 1,
            "a complete confirmed receipt rejects a fabricated offset and explains that no continuation exists");
        Read(complete, "complete", 1);
        check(complete.Is("generate") && Attempts(complete) == 3 && Pages(complete) == 1,
            "a repeated fabricated offset remains bounded and cannot dispatch another receipt lookup");
        Read(complete, "complete", 1);
        check(complete.Step.Next is CompleteWorkflow { Success: false } && Attempts(complete) == 4 && Pages(complete) == 1,
            "the third identical complete-receipt error reaches the existing two-correction stop");

        var unknown = new Fixture(); Read(unknown, "unknown", 0);
        unknown.ReceiptPage("part", WorkflowEffectState.Unknown, nextOffset: 4, totalBytes: 8);
        Read(unknown, "unknown", 4);
        check(unknown.Is("generate") && unknown.LastContributionText.Contains("conversation.receipt_revision", StringComparison.Ordinal) && Attempts(unknown) == 2,
            "an uncertain continuation requires the observed revision before host dispatch");
        Read(unknown, "unknown", 4, 20);
        check(unknown.Is("receipt"), "a corrected uncertain continuation admits the exact advertised offset and revision");
        unknown.ReceiptPage("tail", WorkflowEffectState.Unknown, totalBytes: 8);
        check(unknown.Is("generate") && unknown.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0 &&
              unknown.Step.State.GetProperty("receiptPages")[1].GetProperty("observation").GetProperty("state").GetString() == "unknown",
            "fully reading uncertain reference data preserves Unknown and supplies no retry or build credit");

        var uncertainComplete = new Fixture(); Read(uncertainComplete, "uncertain-complete", 0);
        uncertainComplete.ReceiptPage("done", WorkflowEffectState.Unknown, totalBytes: 4);
        Read(uncertainComplete, "uncertain-complete", 1, 20);
        check(uncertainComplete.Is("generate") &&
              uncertainComplete.LastContributionText.Contains("conversation.receipt_complete", StringComparison.Ordinal) &&
              Attempts(uncertainComplete) == 2 && Pages(uncertainComplete) == 1,
            "a complete uncertain page does not invent a continuation cursor");

        var transition = new Fixture(); Read(transition, "transition", 0);
        transition.ReceiptPage("part", WorkflowEffectState.Unknown, nextOffset: 4, totalBytes: 8);
        Read(transition, "transition", 4, 20); transition.ReceiptLookupFailed("The receipt revision changed; restart at zero.");
        Read(transition, "transition", 0);
        check(transition.Is("receipt"), "a failed uncertain continuation permits one newly accounted zero-offset observation");
        transition.ReceiptPage("done", WorkflowEffectState.Succeeded, nextOffset: 4, totalBytes: 8, revision: 21);
        Read(transition, "transition", 4);
        check(transition.Is("receipt"), "a newer terminal revision has its own forward cursor despite an older failed uncertain range");
        transition.ReceiptPage("tail", WorkflowEffectState.Succeeded, totalBytes: 8, revision: 22);
        check(transition.Is("generate") && transition.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0 &&
              Attempts(transition) == 4 && Requested(transition) == 4 * WorkflowReceiptPage.MaximumPageBytes,
            "Unknown-to-terminal reference transition retains every request charge without creating execution authority");

        var unchanged = new Fixture(); Read(unchanged, "unchanged", 0);
        unchanged.ReceiptPage("part", WorkflowEffectState.Unknown, nextOffset: 4, totalBytes: 8);
        Read(unchanged, "unchanged", 4, 20); unchanged.ReceiptLookupFailed("Stale continuation.");
        Read(unchanged, "unchanged", 0); unchanged.ReceiptPage("part", WorkflowEffectState.Unknown, nextOffset: 4, totalBytes: 8);
        check(unchanged.Step.Next is CompleteWorkflow { Success: false } && Attempts(unchanged) == 3,
            "an uncertain restart with no newer revision terminates without adopting duplicate data");

        var foreign = new Fixture(); Read(foreign, "foreign", 0);
        foreign.ReceiptPage("body", WorkflowEffectState.Succeeded, runId: "other-run");
        check(foreign.Step.Next is CompleteWorkflow { Success: false }, "receipt progress cannot adopt a foreign run identity");
        var revisionMismatch = new Fixture(); Read(revisionMismatch, "revision", 0, 20);
        revisionMismatch.ReceiptPage("body", WorkflowEffectState.Unknown, revision: 21);
        check(revisionMismatch.Step.Next is CompleteWorkflow { Success: false }, "an expected revision mismatch is never converted into read progress");
        var invalidLimit = new Fixture(); Read(invalidLimit, "limit", 0);
        invalidLimit.ReceiptPage("body", WorkflowEffectState.Succeeded, limit: 4096);
        check(invalidLimit.Step.Next is CompleteWorkflow { Success: false }, "a returned page must echo the exact admitted data-window limit");
        var changed = new Fixture(); Read(changed, "changed", 0);
        changed.ReceiptPage("part", WorkflowEffectState.Succeeded, nextOffset: 4, totalBytes: 8);
        Read(changed, "changed", 4);
        changed.ReceiptPage("tail!", WorkflowEffectState.Succeeded, totalBytes: 9);
        check(changed.Step.Next is CompleteWorkflow { Success: false }, "a supposedly immutable receipt cannot change its total byte length");
        var inPlaceTerminal = new Fixture(); Read(inPlaceTerminal, "in-place-terminal", 0);
        inPlaceTerminal.ReceiptPage("part", WorkflowEffectState.Unknown, nextOffset: 4, totalBytes: 8);
        Read(inPlaceTerminal, "in-place-terminal", 4, 20);
        inPlaceTerminal.ReceiptPage("tail", WorkflowEffectState.Succeeded, totalBytes: 8, revision: 20);
        check(inPlaceTerminal.Step.Next is CompleteWorkflow { Success: false },
            "an uncertain result cannot become terminal in place under the same revision pin");
        var uncertainLookup = new Fixture(); Read(uncertainLookup, "lookup-unknown", 0); uncertainLookup.Uncertain();
        check(uncertainLookup.Step.Next is CompleteWorkflow { Success: false } && Pages(uncertainLookup) == 1,
            "an Unknown lookup effect itself is not retried or adopted as a confirmed page");
        var cancelled = new Fixture(); Read(cancelled, "cancelled", 0); cancelled.CancelReceipt();
        check(cancelled.Step.Next is CompleteWorkflow { Success: false } && Pages(cancelled) == 1,
            "cancellation stops a pending receipt before any follow-up request");

        var byteLimit = new Fixture(); SeedAllowance(byteLimit, 32, 32); Read(byteLimit, "over-budget", 0);
        check(byteLimit.Is("generate") && byteLimit.LastContributionText.Contains("conversation.receipt_requested_bytes_exhausted", StringComparison.Ordinal) &&
              Attempts(byteLimit) == 33 && Requested(byteLimit) == 512 * 1024 && Pages(byteLimit) == 32,
            "the aggregate requested data-window cap refuses dispatch and still charges the refused proposal's attempt");
        var malformed = new Fixture(); SeedAllowance(malformed, 62, 0);
        malformed.Model(new { action = "receipt", effectId = "malformed", offset = "not-an-integer" });
        check(malformed.Is("generate") && Attempts(malformed) == 63 && Requested(malformed) == 0,
            "recognizable malformed receipt proposals consume attempts before schema correction");
        ToolProgress(malformed);
        malformed.Model(new { action = "receipt", effectId = "malformed", offset = "not-an-integer" });
        check(malformed.Is("generate") && Attempts(malformed) == 64, "unrelated tool progress cannot erase malformed receipt attempts");
        Read(malformed, "after-refusals", 0);
        check(malformed.Step.Next is CompleteWorkflow { Success: false } && Attempts(malformed) == 64,
            "the attempt ceiling terminates after corrections even when tools reset the ordinary repair streak");

        var legacy = new Fixture();
        var legacyState = JsonNode.Parse(legacy.Step.State.GetRawText())!.AsObject();
        legacyState["receiptPages"] = JsonSerializer.SerializeToNode(new[] { new { effectId = "legacy", offset = 0, expectedRevision = (long?)null } }, CerebralJson.Options);
        legacy.RestoreState(CerebralJson.Element(legacyState)); Read(legacy, "legacy", 4096);
        check(legacy.Is("receipt") && Attempts(legacy) == 2 && Requested(legacy) == 4096 + WorkflowReceiptPage.MaximumPageBytes,
            "a legacy coordinate-only ledger restores with conservative 4 KiB charges and no invented success");
        legacy.ReceiptPage("tail", WorkflowEffectState.Failed, totalBytes: 4100);
        check(legacy.Is("generate") && legacy.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "a legacy receipt continuation remains reference-only after the new accounting is adopted");

        var inFlight = new Fixture(); Read(inFlight, "legacy-in-flight", 0);
        var old = JsonNode.Parse(inFlight.Step.State.GetRawText())!.AsObject();
        old.Remove("receiptReadAccountingVersion"); old.Remove("receiptReadAttempts"); old.Remove("receiptReadRequestedBytes");
        old["receiptPages"]![0]!.AsObject().Remove("limit"); old["pendingReceipt"]!.AsObject().Remove("limit");
        old["pendingArguments"]!["limit"] = 4096;
        var oldArguments = CerebralJson.Element(old["pendingArguments"]);
        inFlight.RestoreStep(inFlight.Step with { State = CerebralJson.Element(old),
            Next = inFlight.Command with { Arguments = oldArguments } });
        inFlight.ReceiptPage("legacy body", WorkflowEffectState.Succeeded);
        check(inFlight.Is("generate") && Attempts(inFlight) == 1 && Requested(inFlight) == 4096,
            "an already dispatched legacy 4 KiB page restores and validates without changing its command arguments");

        var legacyOverflow = new Fixture();
        var oversizedLegacy = JsonNode.Parse(legacyOverflow.Step.State.GetRawText())!.AsObject();
        oversizedLegacy["receiptPages"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 9).Select(i =>
            new { effectId = "legacy-" + i, offset = 0, expectedRevision = (long?)null }).ToArray(), CerebralJson.Options);
        legacyOverflow.RestoreState(CerebralJson.Element(oversizedLegacy)); Read(legacyOverflow, "new", 0);
        check(legacyOverflow.Step.Next is CompleteWorkflow { Success: false },
            "legacy restore does not widen the original eight-request invariant without an accounting marker");
        var missingMarker = new Fixture(); Read(missingMarker, "missing-marker", 0);
        var unmarked = JsonNode.Parse(missingMarker.Step.State.GetRawText())!.AsObject();
        unmarked.Remove("receiptReadAccountingVersion"); unmarked.Remove("receiptReadAttempts"); unmarked.Remove("receiptReadRequestedBytes");
        unmarked["receiptPages"]![0]!.AsObject().Remove("limit"); unmarked["pendingReceipt"]!.AsObject().Remove("limit");
        missingMarker.RestoreState(CerebralJson.Element(unmarked)); missingMarker.ReceiptPage("body", WorkflowEffectState.Succeeded);
        check(missingMarker.Step.Next is CompleteWorkflow { Success: false },
            "removing a new accounting marker cannot undercharge a 16 KiB in-flight request as a legacy 4 KiB request");
        var corrupt = new Fixture(); Read(corrupt, "bound-run", 0);
        corrupt.ReceiptPage("body", WorkflowEffectState.Succeeded);
        var corruptState = JsonNode.Parse(corrupt.Step.State.GetRawText())!.AsObject();
        corruptState["receiptPages"]![0]!["observation"]!["runId"] = "foreign-run";
        corrupt.RestoreState(CerebralJson.Element(corruptState)); Read(corrupt, "other", 0);
        check(corrupt.Step.Next is CompleteWorkflow { Success: false }, "restored progress cannot cross its exact run binding");
        check(LocalGenerationConversationServicePlugin.ModuleVersion == "1.0.12" &&
              typeof(LocalGenerationConversationServicePlugin).Assembly.GetName().Version == new Version(1, 0, 12, 0),
            "the successor producer and assembly stamps agree without changing definition identities");
    }

    private static void RunConversationCapacityDiagnosticChecks(Action<bool, string> check)
    {
        var tokens = new Fixture(); tokens.RejectContext(ContextProjectionStatus.RequiredOverflow);
        if (tokens.Is("generate")) tokens.RejectContext(ContextProjectionStatus.RequiredOverflow);
        check(tokens.Step.Next is CompleteWorkflow { Success: false } tokenFailure &&
              tokenFailure.Error!.Contains("conversation.model_context_tokens_exhausted", StringComparison.Ordinal) &&
              tokenFailure.Error.Contains("cannot resume", StringComparison.Ordinal),
            "actual token admission failure identifies tokens and requires a fresh admitted request");

        var history = Enumerable.Range(0, 14).Select(i => new ContextContribution("required-bytes-" + i, "fixture", "history", 1,
            ContextRequirement.Required, [new("user", [new TextContentPart(new string('b', 8000))])],
            null, BeforeCurrentInput: true)).ToArray();
        var bytes = new Fixture();
        bytes.Describe("file.read"); bytes.Catalog(exact: true);
        bytes.Model(new { action = "tool", name = "file.read", arguments = new { path = "byte-bound-fixture" } });
        // Seed already-retained bounded pages, not one illegal >48 KiB history response.
        var saved = JsonNode.Parse(bytes.Step.State.GetRawText())!.AsObject();
        var contributions = saved["contributions"]!.AsArray();
        foreach (var source in history) contributions.Add(JsonSerializer.SerializeToNode(source, CerebralJson.Options));
        saved["toolReceipts"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
        { effectId = "prior-unknown", name = "process.run", state = "Unknown", outcome = "Unknown", exitCode = (int?)null, truncated = true }, CerebralJson.Options));
        var retained = CerebralJson.Element(saved);
        check(Encoding.UTF8.GetByteCount(retained.GetRawText()) <= WorkflowContract.MaximumStateBytes,
            "the byte-exhaustion fixture starts from a valid bounded saved projection");
        bytes.RestoreState(retained);
        bytes.Tool(false, new string('"', 8192));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            fixture = "terminal_capacity_boundary", priorStateBytes = Encoding.UTF8.GetByteCount(retained.GetRawText()),
            returnedStateBytes = Encoding.UTF8.GetByteCount(bytes.Step.State.GetRawText()),
            phase = bytes.Step.State.TryGetProperty("phase", out var observedPhase) ? observedPhase.GetString() : null,
            next = bytes.Step.Next.GetType().Name,
            error = bytes.Step.Next is CompleteWorkflow completion ? completion.Error : null
        }, CerebralJson.Options));
        check(bytes.Step.Next is CompleteWorkflow { Success: false } byteFailure &&
              byteFailure.Error!.Contains("conversation.durable_projection_bytes_exhausted", StringComparison.Ordinal) &&
              byteFailure.Error.Contains("131072-byte", StringComparison.Ordinal) &&
              byteFailure.Error.Contains("larger model token context does not raise", StringComparison.Ordinal) &&
              byteFailure.Error.Contains("cannot resume", StringComparison.Ordinal),
            "durable UTF-8 byte exhaustion has separate advice and never suggests resuming a failed run with more model context");

        check(Encoding.UTF8.GetByteCount(bytes.Step.State.GetRawText()) <= WorkflowContract.MaximumStateBytes &&
              bytes.Step.State.GetProperty("phase").GetString() == "terminal-capacity-failure",
            "the terminal byte failure itself fits the unchanged Engine state guard");
        var terminal = bytes.Step.State.GetProperty("terminalCapacityFailure");
        check(terminal.GetProperty("workingProjectionOmitted").GetBoolean() &&
              !terminal.GetProperty("historyCheckpointBaselineClaimed").GetBoolean() &&
              terminal.GetProperty("toolReceiptCoordinates").EnumerateArray().Any(receipt =>
                  receipt.GetProperty("effectId").GetString() == bytes.LastSnapshot!.Effect!.Id &&
                  receipt.GetProperty("state").GetString() == "Failed") &&
              terminal.GetProperty("toolReceiptCoordinates").EnumerateArray().Any(receipt =>
                  receipt.GetProperty("effectId").GetString() == "prior-unknown" &&
                  receipt.GetProperty("state").GetString() == "Unknown"),
            "terminal omission is explicit and preserves actual failed/Unknown receipt coordinates without upgrading outcomes");
        var harness = new ConversationAgentHarness();
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(bytes.Step.State, CerebralJson.Options));
        foreach (var signal in Enum.GetValues<WorkflowSignalKind>())
        {
            var restored = bytes.LastSnapshot! with { State = serialized.RootElement.Clone(),
                Signal = new(signal, WorkflowContract.EmptyObject), Status = WorkflowStatus.Running };
            var refused = harness.Advance(new(restored));
            check(refused.Next is CompleteWorkflow { Success: false } stopped &&
                  stopped.Error!.Contains("conversation.terminal_capacity_cannot_resume", StringComparison.Ordinal) &&
                  JsonElement.DeepEquals(refused.State, bytes.Step.State),
                "serialized terminal capacity state rejects every continuation/steering/recovery signal: " + signal);
        }
        var changedIdentity = bytes.LastSnapshot! with { State = bytes.Step.State, Id = "other-run" };
        check(harness.Advance(new(changedIdentity)).Next is CompleteWorkflow { Success: false } wrongRun &&
              wrongRun.Error!.Contains("conversation.terminal_capacity_identity_mismatch", StringComparison.Ordinal),
            "terminal restore cannot borrow another run identity");
        var changedSubmission = bytes.LastSnapshot! with { State = bytes.Step.State,
            Submission = bytes.LastSnapshot.Submission with { UserText = "Different accepted request" } };
        check(harness.Advance(new(changedSubmission)).Next is CompleteWorkflow { Success: false } wrongSubmission &&
              wrongSubmission.Error!.Contains("conversation.terminal_capacity_identity_mismatch", StringComparison.Ordinal),
            "terminal restore pins the accepted submission as well as visible run coordinates");
        var unknownEffect = bytes.LastSnapshot! with { State = bytes.Step.State,
            Effect = bytes.LastSnapshot.Effect! with { State = WorkflowEffectState.Unknown, Result = null } };
        check(harness.Advance(new(unknownEffect)).Next is CompleteWorkflow { Success: false } &&
              !ConversationAgentHarness.CreatePreparedInterventionEdition().CanRejectPrepared(new(unknownEffect)),
            "a restored terminal marker cannot dispatch or reject an Unknown/prepared effect");
        var missingPhase = JsonNode.Parse(bytes.Step.State.GetRawText())!.AsObject(); missingPhase.Remove("phase");
        check(harness.Advance(new(bytes.LastSnapshot! with { State = CerebralJson.Element(missingPhase) })).Next is CompleteWorkflow { Success: false },
            "a surviving terminal marker cannot be reinterpreted as fresh state by removing its phase");
        var missingMarker = JsonNode.Parse(bytes.Step.State.GetRawText())!.AsObject(); missingMarker.Remove("terminalCapacityFailure");
        check(harness.Advance(new(bytes.LastSnapshot! with { State = CerebralJson.Element(missingMarker) })).Next is CompleteWorkflow { Success: false },
            "a surviving terminal phase rejects a missing or malformed marker");


        foreach (var malformed in new[] { "resumable", "missing-resume", "wrong-resume-type", "missing-cause", "wrong-cause", "fractional-version", "overflow-version" })
        {
            var invalid = JsonNode.Parse(bytes.Step.State.GetRawText())!.AsObject();
            var marker = invalid["terminalCapacityFailure"]!.AsObject();
            switch (malformed)
            {
                case "resumable": marker["canResume"] = true; break;
                case "missing-resume": marker.Remove("canResume"); break;
                case "wrong-resume-type": marker["canResume"] = "false"; break;
                case "missing-cause": marker.Remove("causeCode"); break;
                case "wrong-cause": marker["causeCode"] = "unrelated"; break;
                case "fractional-version": marker["schemaVersion"] = 1.5; break;
                case "overflow-version": marker["schemaVersion"] = JsonNode.Parse("999999999999999999999999999999999"); break;
            }
            var rejected = harness.Advance(new(bytes.LastSnapshot! with { State = CerebralJson.Element(invalid) }));
            check(rejected.Next is CompleteWorkflow { Success: false } rejection &&
                  rejection.Error!.Contains("conversation.terminal_capacity_identity_mismatch", StringComparison.Ordinal) &&
                  Encoding.UTF8.GetByteCount(rejected.State.GetRawText()) <= WorkflowContract.MaximumStateBytes &&
                  rejected.State.GetProperty("terminalCapacityFailure").GetProperty("canResume").ValueKind == JsonValueKind.False,
                "malformed terminal marker returns a bounded nonresumable failure: " + malformed);
        }

        // Actual Engine adapter/schema and SQLite commit; the prior host result is a fixture.
        // This exercises no provider, model, installed Worker, network or live tool.
        var admitted = new AgentWorkflowDefinition(harness).Advance(bytes.LastSnapshot!);
        check(admitted.Next is CompleteWorkflow { Success: false } &&
              Encoding.UTF8.GetByteCount(admitted.State.GetRawText()) <= WorkflowContract.MaximumStateBytes,
            "the real Engine adapter admits the terminal failure under its unchanged 128 KiB JSON schema guard");
        var temporaryParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.Combine(temporaryParent, "cerebral-terminal-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new WorkflowStore(new EventJournal(Path.Combine(root, "engine.db")));
            var submitted = store.Submit(bytes.LastSnapshot!.Submission);
            var claimed = store.TryClaim(submitted.Id)!;
            var failed = store.ApplyStep(claimed.Id, claimed.Revision, admitted);
            var readback = store.Get(failed.Id);
            check(failed.Status == WorkflowStatus.Failed && readback.Error == ((CompleteWorkflow)admitted.Next).Error &&
                  JsonElement.DeepEquals(readback.State, admitted.State) && store.TryClaim(failed.Id) is null,
                "the actual private SQLite Engine store records failure and cannot claim it for replay");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var fullRoot = Path.GetFullPath(root);
            if (!fullRoot.StartsWith(temporaryParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Terminal-capacity fixture cleanup escaped its temporary parent.");
            if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, true);
        }
        Console.WriteLine("Terminal capacity: actual Engine adapter/schema and isolated SQLite admission passed; deterministic host outcome, no provider/model/network.");

    }
}
