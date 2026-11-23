using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    public static Task RunRejectionRecoveryOnlyAsync()
    {
        Console.WriteLine($"Conversation rejection recovery: {RunRejectionRecoveryChecks()} deterministic policy checks passed.");
        return Task.CompletedTask;
    }

    private static int RunRejectionRecoveryChecks()
    {
        var checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException("Conversation rejection recovery: " + reason);
            checks++;
        }
        const string presentation = "11111111111111111111111111111111", mount = "notes.main",
            field = "22222222222222222222222222222222";
        var schema = CerebralJson.Element(new { type = "object", additionalProperties = true });
        var controlSchema = PluginControlFixtureSchema();
        var fixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-admission-recovery"));
        var target = Path.Combine(fixtureRoot, "source.cs");
        var hash = new string('a', 64);
        Fixture Create(string run = "rejection-recovery-run")
        {
            var fixture = new Fixture(warm: false, input: new { workingDirectory = fixtureRoot }, requestId: run);
            fixture.History([]);
            fixture.Catalog(forcedNames: ["plugin.controls", "file.read", "file.write", "file.inspect", "program.source.generate", "file.copy", "file.mkdir", "process.run"]);
            fixture.Select("plugin.controls", controlSchema);
            fixture.Select("file.read", schema);
            return fixture;
        }
        object NoOp(string text = "unchanged", string targetMount = mount) => new
        {
            action = "tool", name = "plugin.controls",
            arguments = new { presentationId = presentation, mountId = targetMount, operation = "setText", fieldId = field,
                expectedText = text, text }
        };
        void Inspect(Fixture fixture, string targetMount = mount)
        {
            fixture.Model(new { action = "tool", name = "plugin.controls",
                arguments = new { presentationId = presentation, mountId = targetMount, operation = "inspect" } });
            fixture.Tool(true, "Read-only control inspection.", provider: "cerebral.plugin.controls");
        }
        void Read(Fixture fixture, int sequence)
        {
            fixture.Model(new { action = "tool", name = "file.read",
                arguments = new { path = Path.Combine(fixtureRoot, "read-" + sequence + ".cs") } });
            fixture.Tool(true, "Bounded unrelated source read.");
        }
        JsonElement Ledger(Fixture fixture) => fixture.Step.State.GetProperty("admissionRejections");
        void Reject(Fixture fixture, string reason = "Replacement text must differ from expectedText.", bool? partial = null)
            => fixture.Tool(false, reason, outcome: ToolExecutionOutcome.Rejected, partial: partial);

        var scoped = Create("scoped-read-recovery-run");
        scoped.Select("process.run", schema);
        scoped.Model(new { action = "tool", name = "process.run", arguments = new { executableId = "dotnet" } });
        scoped.Tool(false, "Checker failed 9/10 on current inputs.", outcome: ToolExecutionOutcome.Failed, exitCode: 1);
        var failedCommand = scoped.Step.State.GetProperty("toolReceipts").EnumerateArray()
            .Last().GetProperty("effectId").GetString()!;
        scoped.Model(new { action = "tool", name = "file.read", arguments = new { path = target, startLine = 1, lineCount = 40 } });
        Reject(scoped, "The path is outside this conversation's current access scope.", partial: false);
        Check(scoped.LastContributionText.Contains("\"action\":\"receipt\"", StringComparison.Ordinal) &&
            scoped.LastContributionText.Contains(failedCommand, StringComparison.Ordinal) &&
            scoped.LastContributionText.Contains("Do not retry this path with another range", StringComparison.Ordinal) &&
            scoped.Step.State.GetProperty("admissionRejections")[0].GetProperty("requests").GetArrayLength() == 1,
            "a proven scoped read denial offers the exact same-run failed receipt without erasing rejection history");
        var otherRead = Create("ordinary-read-rejection-run");
        otherRead.Model(new { action = "tool", name = "file.read", arguments = new { path = target } });
        Reject(otherRead, "A different admission failure.", partial: false);
        Check(!otherRead.LastContributionText.Contains("Do not retry this path with another range", StringComparison.Ordinal),
            "unrelated read failures do not receive a path-scope recovery claim");

        var scopedPriorFixture = Create("scoped-prior-run");
        scopedPriorFixture.Select("process.run", schema);
        scopedPriorFixture.Model(new { action = "tool", name = "process.run", arguments = new { executableId = "dotnet" } });
        scopedPriorFixture.Tool(false, "Prior run command failed.", outcome: ToolExecutionOutcome.Failed, exitCode: 1);
        var priorReceipt = scopedPriorFixture.Step.State.GetProperty("toolReceipts").EnumerateArray().Last()
            .GetProperty("effectId").GetString()!;
        var legacyState = JsonNode.Parse(scopedPriorFixture.Step.State.GetRawText())!.AsObject();
        legacyState.Remove("admissionRecoveryRunId");
        legacyState.Remove("readRecovery");
        RemoveFailedBuildPolicy(legacyState);
        var legacyNext = Create("scoped-new-run");
        legacyNext.RestoreStep(scopedPriorFixture.Step with { State = CerebralJson.Element(legacyState) });
        legacyNext.Model(new { action = "tool", name = "file.read", arguments = new { path = target } });
        Reject(legacyNext, "The path is outside this conversation's current access scope.", partial: false);
        Check(legacyNext.LastContributionText.Contains("Do not retry this path with another range", StringComparison.Ordinal) &&
            !legacyNext.LastContributionText.Contains(priorReceipt, StringComparison.Ordinal) &&
            !legacyNext.LastContributionText.Contains("\"action\":\"receipt\"", StringComparison.Ordinal),
            "a markerless new run retains old receipts without advertising an old failed command as current");

        var interleaved = Create();
        interleaved.Model(NoOp());
        Reject(interleaved);
        var rejectedReceipt = Ledger(interleaved)[0].GetProperty("requests")[0].GetProperty("effectId").GetString()!;
        interleaved.Select("file.inspect", schema);
        Check(Ledger(interleaved)[0].GetProperty("requests").GetArrayLength() == 1,
            "exact schema success preserves the original confirmed rejection budget");
        for (var correction = 0; correction < 2; correction++)
        {
            Inspect(interleaved);
            Read(interleaved, correction);
            var receiptCount = interleaved.Step.State.GetProperty("toolReceipts").GetArrayLength();
            interleaved.Model(NoOp());
            Check(interleaved.Is("generate") &&
                Ledger(interleaved)[0].GetProperty("corrections").GetInt32() == correction + 1 &&
                interleaved.Step.State.GetProperty("toolReceipts").GetArrayLength() == receiptCount &&
                interleaved.LastContributionText.Contains(rejectedReceipt, StringComparison.Ordinal),
                "two duplicate corrections remain available across reads without redispatching the rejected request");
        }
        Inspect(interleaved);
        Read(interleaved, 2);
        var receiptsBeforeStop = interleaved.Step.State.GetProperty("toolReceipts").GetArrayLength();
        var priorRunGeneration = interleaved.Step;
        interleaved.Model(NoOp());
        Check(interleaved.Step.Next is CompleteWorkflow { Success: false } &&
            interleaved.Step.State.GetProperty("toolReceipts").GetArrayLength() == receiptsBeforeStop &&
            interleaved.Summary.Contains("two corrections", StringComparison.Ordinal),
            "a third identical proposal stops even after successful reads and inspections");

        var varied = Create();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            varied.Model(NoOp("variant-" + attempt));
            Reject(varied, JsonSerializer.Serialize(new { receipt = "different-receipt-" + attempt,
                error = "Replacement text must differ from expectedText." }));
            if (attempt < 3) Inspect(varied);
        }
        Check(varied.Step.Next is CompleteWorkflow { Success: false } &&
            Ledger(varied)[0].GetProperty("requests").GetArrayLength() == 4 &&
            varied.Step.State.GetProperty("toolReceipts").GetArrayLength() == 7,
            "argument variations and changing receipt IDs cannot erase the same stable rejection reason across inspections");

        var independent = Create();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            independent.Model(NoOp("reason-a-" + attempt)); Reject(independent, "Reason A."); Read(independent, attempt);
            independent.Model(NoOp("reason-b-" + attempt)); Reject(independent, "Reason B."); Read(independent, attempt + 10);
        }
        Check(independent.Is("generate") && Ledger(independent).GetArrayLength() == 2 &&
            Ledger(independent).EnumerateArray().All(entry => entry.GetProperty("requests").GetArrayLength() == 3),
            "different rejection reasons have independent budgets");
        independent.Model(NoOp("other-resource", "notes.other"));
        Reject(independent, "Reason A.");
        Check(independent.Is("generate") && Ledger(independent).GetArrayLength() == 3,
            "a different mount has an independent precondition budget");

        var mutation = Create();
        mutation.Model(NoOp()); Reject(mutation);
        Inspect(mutation);
        mutation.Model(new { action = "tool", name = "plugin.controls", arguments = new
        {
            presentationId = presentation, mountId = mount, operation = "setText",
            fieldId = field, expectedText = "before", text = "after\n☃"
        } });
        var before = new PluginControlSnapshot(mount, [], [], false)
            { Fields = [new(field, "before", true, true, false)] };
        var after = new PluginControlSnapshot(mount, [], [], false)
            { Fields = [new(field, "after\n☃", true, true, false)] };
        mutation.Tool(true, JsonSerializer.Serialize(new
        {
            receipt = "native-mutation", presentationId = presentation, mountId = mount, operation = "setText",
            status = "completed", nativeStatus = "completed",
            observation = new PluginControlObservation(before, after, false) { TextSet = true }
        }, CerebralJson.Options), provider: "cerebral.plugin.controls", requestId: "native-mutation");
        Check(!mutation.Step.State.TryGetProperty("admissionRejections", out _),
            "a complete matched native before/after mutation retires only the changed resource budget");
        mutation.Model(NoOp());
        Check(mutation.Is("tool"),
            "a previously rejected proposal becomes eligible for provider admission after a proven same-route precondition change");

        var writeMutation = Create();
        writeMutation.Select("file.write", schema);
        writeMutation.Model(new { action = "tool", name = "file.write", arguments = new { path = target } });
        Reject(writeMutation, "A write precondition failed.");
        writeMutation.Model(new { action = "tool", name = "file.write",
            arguments = new { path = target, content = "actual changed bytes" } });
        writeMutation.Tool(true, JsonSerializer.Serialize(new { path = target, sha256 = hash, bytes = 20 }));
        Check(!writeMutation.Step.State.TryGetProperty("admissionRejections", out _),
            "matched actual file mutation clears the same resource budget");
        writeMutation.Model(new { action = "tool", name = "file.write", arguments = new { path = target } });
        Check(writeMutation.Is("tool"), "actual file progress permits a fresh provider check for an earlier precondition");

        var noOpWrite = Create();
        noOpWrite.Select("file.write", schema);
        noOpWrite.Model(new { action = "tool", name = "file.write", arguments = new { path = target } });
        Reject(noOpWrite);
        noOpWrite.Model(new { action = "tool", name = "file.write",
            arguments = new { path = target, collision = "skip", content = "unused" } });
        noOpWrite.Tool(true, JsonSerializer.Serialize(new { path = target, skipped = true }));
        noOpWrite.Model(new { action = "tool", name = "file.write", arguments = new { path = target } });
        Check(noOpWrite.Is("generate") && Ledger(noOpWrite)[0].GetProperty("corrections").GetInt32() == 1,
            "a successful skipped write proves no precondition change and cannot reset the guard");

        foreach (var outcome in new[] { ToolExecutionOutcome.Failed, ToolExecutionOutcome.Unknown })
        {
            var failure = Create();
            failure.Model(NoOp());
            failure.Tool(false, "Execution may have failed after dispatch.", outcome: outcome);
            failure.Model(NoOp());
            Check(failure.Is("tool") && !failure.Step.State.TryGetProperty("admissionRejections", out _),
                "failed/unknown execution evidence never creates a confirmed admission rejection budget");
        }
        var partialRejection = Create();
        partialRejection.Model(NoOp()); Reject(partialRejection, partial: true);
        partialRejection.Model(NoOp());
        Check(partialRejection.Is("tool") && !partialRejection.Step.State.TryGetProperty("admissionRejections", out _),
            "partial execution cannot be classified as a proved pre-dispatch rejection");
        var unknownEffect = Create();
        unknownEffect.Model(NoOp()); unknownEffect.Uncertain();
        Check(unknownEffect.Step.Next is CompleteWorkflow { Success: false } &&
            !unknownEffect.Step.State.TryGetProperty("admissionRejections", out _),
            "an unconfirmed workflow effect retains the existing receipt-first stop without rejection credit");

        var generated = Create();
        generated.Select("program.source.generate", schema);
        var plan = new ToolGenerationRequest("Generate bounded source.", "file.write",
            CerebralJson.Element(new { path = target }), "content", MaxTokens: 1024, MaxAttempts: 2);
        generated.Model(new { action = "tool", name = "program.source.generate", arguments = new { path = target } });
        generated.Tool(true, "Confirmed plan.", followUp: true, generation: plan);
        const string rawSource = "public sealed class RawRecoverySourceSentinel { }";
        generated.RawGeneration(rawSource);
        Reject(generated, "Generated sink admission failed.");
        Check(!generated.Step.State.GetRawText().Contains("RawRecoverySourceSentinel", StringComparison.Ordinal) &&
            Ledger(generated)[0].GetProperty("requests")[0].GetProperty("sha256").GetString()!.Length == 64,
            "generated sink ledger stores digest identity and receipts without retaining raw source");
        var generatedReceipts = generated.Step.State.GetProperty("toolReceipts").GetArrayLength();
        generated.RawGeneration(rawSource);
        Check(generated.Is("generate") && generated.Step.State.GetProperty("phase").GetString() == "model" &&
            generated.Step.State.GetProperty("toolReceipts").GetArrayLength() == generatedReceipts &&
            !generated.Step.State.GetRawText().Contains("RawRecoverySourceSentinel", StringComparison.Ordinal),
            "an identical rejected generated sink is suppressed before a second dispatch");

        // A complete new-policy snapshot is owned by its original run. Restoring it under a
        // different submission must fail before its ledgers or pending action can be reused.
        var foreign = Create("foreign-restored-run");
        foreign.RestoreStep(priorRunGeneration);
        foreign.Model(NoOp());
        Check(foreign.Step.Next is CompleteWorkflow { Success: false } &&
            foreign.Summary.Contains("failed-build recovery", StringComparison.Ordinal) &&
            foreign.Summary.Contains("another run", StringComparison.Ordinal) &&
            foreign.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0 &&
            !foreign.Step.State.TryGetProperty("admissionRejections", out _),
            "a foreign run-bound snapshot is rejected rather than retagged or given fresh correction credit");

        var retainedReadForeign = Create("foreign-retained-read-run");
        var retainedReadPrior = JsonNode.Parse(priorRunGeneration.State.GetRawText())!.AsObject();
        RemoveFailedBuildPolicy(retainedReadPrior);
        retainedReadForeign.RestoreStep(priorRunGeneration with { State = CerebralJson.Element(retainedReadPrior) });
        retainedReadForeign.Model(NoOp());
        Check(retainedReadForeign.Step.Next is CompleteWorkflow { Success: false } &&
            retainedReadForeign.Summary.Contains("retained-read recovery", StringComparison.Ordinal) &&
            retainedReadForeign.Summary.Contains("another run", StringComparison.Ordinal) &&
            retainedReadForeign.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0 &&
            !retainedReadForeign.Step.State.TryGetProperty("admissionRejections", out _),
            "the earlier retained-read policy independently rejects a foreign snapshot without borrowing correction or execution credit");

        var fresh = Create("fresh-accepted-run");
        fresh.Model(NoOp());
        Check(fresh.Is("tool") && !fresh.Step.State.TryGetProperty("admissionRejections", out _) &&
            fresh.Step.State.GetProperty("admissionRecoveryRunId").GetString() == "fresh-accepted-run" &&
            fresh.Step.State.GetProperty("readRecovery").GetProperty("runId").GetString() == "fresh-accepted-run" &&
            fresh.Step.State.GetProperty("readRecovery").GetProperty("reuses").GetInt32() == 0 &&
            fresh.Step.State.GetProperty("failedBuildRecovery").GetProperty("runId").GetString() == "fresh-accepted-run" &&
            fresh.Step.State.GetProperty("failedBuildRecovery").GetProperty("failures").GetArrayLength() == 0 &&
            fresh.Step.State.GetProperty("toolReceipts").GetArrayLength() == 0,
            "a fresh accepted run does not inherit another run's rejection or correction budgets");
        Check(priorRunGeneration.State.GetProperty("admissionRejections")[0].GetProperty("corrections").GetInt32() == 2 &&
            priorRunGeneration.State.GetProperty("readRecovery").GetProperty("runId").GetString() == "rejection-recovery-run" &&
            priorRunGeneration.State.GetProperty("toolReceipts").GetArrayLength() == receiptsBeforeStop,
            "fresh admission and foreign-state rejection preserve the prior run's spent budget and receipt history");

        // Keep the original absent-marker compatibility branch explicit; it does not opt any
        // of the new-policy cases above out of exact-run recovery-state validation.
        var legacyFresh = Create("legacy-fresh-accepted-run");
        var legacyPrior = JsonNode.Parse(priorRunGeneration.State.GetRawText())!.AsObject();
        legacyPrior.Remove("readRecovery");
        RemoveFailedBuildPolicy(legacyPrior);
        legacyFresh.RestoreStep(priorRunGeneration with { State = CerebralJson.Element(legacyPrior) });
        legacyFresh.Model(NoOp());
        Check(legacyFresh.Is("tool") && !legacyFresh.Step.State.TryGetProperty("admissionRejections", out _) &&
            !legacyFresh.Step.State.TryGetProperty("readRecovery", out _) &&
            !legacyFresh.Step.State.TryGetProperty("failedBuildRecovery", out _) &&
            legacyFresh.Step.State.GetProperty("admissionRecoveryRunId").GetString() == "legacy-fresh-accepted-run",
            "legacy absent-marker admission rollover retains its existing compatibility without acquiring a new recovery policy");

        var legacy = Create();
        legacy.Model(NoOp()); Reject(legacy);
        var legacyNode = JsonNode.Parse(legacy.Step.State.GetRawText())!.AsObject();
        legacyNode.Remove("admissionRecoveryRunId"); legacyNode.Remove("admissionRejections");
        legacy.RestoreState(CerebralJson.Element(legacyNode));
        Inspect(legacy);
        legacy.Model(NoOp());
        Check(legacy.Is("generate") && Ledger(legacy)[0].GetProperty("corrections").GetInt32() == 1,
            "legacy snapshots without additive state upgrade the saved rejection before a read can erase it");

        var copy = Create();
        copy.Select("file.copy", schema);
        copy.Select("file.write", schema);
        var missingSource = Path.Combine(fixtureRoot, "missing-source.txt");
        var copyDestination = Path.Combine(fixtureRoot, "copied.txt");
        object CopyRequest(string sourcePath = "") => new { action = "tool", name = "file.copy",
            arguments = new { sourcePath = sourcePath.Length == 0 ? missingSource : sourcePath, destinationPath = copyDestination } };
        copy.Model(CopyRequest()); Reject(copy, "Copy requires an existing source file.");
        Check(Ledger(copy)[0].GetProperty("resourceSha256").GetArrayLength() == 2 &&
            !Ledger(copy).GetRawText().Contains(missingSource, StringComparison.Ordinal),
            "copy rejections retain only bounded source and destination dependency digests");
        copy.Model(CopyRequest(Path.Combine(fixtureRoot, "different-source.txt"))); Reject(copy, "Copy requires an existing source file.");
        Check(Ledger(copy).GetArrayLength() == 2, "same copy reason with a different source remains an independent resource budget");
        copy.Model(new { action = "tool", name = "file.write", arguments = new { path = missingSource, content = "created source" } });
        copy.Tool(true, JsonSerializer.Serialize(new { path = missingSource, sha256 = hash, bytes = 14 }));
        Check(Ledger(copy).GetArrayLength() == 1,
            "confirmed source creation retires only copy rejections dependent on that source");
        copy.Model(CopyRequest());
        Check(copy.Is("tool"), "the exact formerly rejected copy can be re-admitted after its missing source is actually created");
        copy.Tool(true, JsonSerializer.Serialize(new { sourcePath = missingSource, destinationPath = copyDestination,
            sha256 = hash, bytes = 14 }), omitEvidence: true);
        Check(!copy.Step.State.TryGetProperty("admissionRejections", out _),
            "a complete correlated copy receipt retires destination-dependent rejection budgets without claiming a source mutation");

        var copyNoProof = Create();
        copyNoProof.Select("file.copy", schema);
        copyNoProof.Select("file.write", schema);
        copyNoProof.Model(new { action = "tool", name = "file.write", arguments = new { path = copyDestination } });
        Reject(copyNoProof, "Destination precondition.");
        copyNoProof.Model(CopyRequest());
        copyNoProof.Tool(true, "Copy completed.", provider: "cerebral.filesystem");
        Check(Ledger(copyNoProof).GetArrayLength() == 1, "status-only copy success cannot retire a rejected destination precondition");

        var cwdRecovery = Create();
        cwdRecovery.Select("file.mkdir", schema);
        cwdRecovery.Select("file.write", schema);
        cwdRecovery.Select("process.run", schema);
        var missingDirectory = Path.Combine(fixtureRoot, "missing-working-directory");
        object RunRequest() => new { action = "tool", name = "process.run", arguments = new
            { executableId = "dotnet", arguments = Array.Empty<string>(), workingDirectory = missingDirectory } };
        cwdRecovery.Model(RunRequest()); Reject(cwdRecovery, "Working directory must exist.");
        cwdRecovery.Model(new { action = "tool", name = "file.write", arguments = new { path = missingDirectory, content = "file, not directory" } });
        cwdRecovery.Tool(true, JsonSerializer.Serialize(new { path = missingDirectory, sha256 = hash, bytes = 19 }));
        Check(Ledger(cwdRecovery).GetArrayLength() == 1, "file creation at the same path is not proof that a process working directory exists");
        cwdRecovery.Model(new { action = "tool", name = "file.mkdir", arguments = new { path = fixtureRoot } });
        cwdRecovery.Tool(true, JsonSerializer.Serialize(new { path = fixtureRoot, ensured = true, existedBefore = false }));
        Check(Ledger(cwdRecovery).GetArrayLength() == 1, "creation of an ancestor cannot retire an exact process working-directory rejection");
        cwdRecovery.Model(new { action = "tool", name = "file.mkdir", arguments = new { path = missingDirectory } });
        cwdRecovery.Tool(true, JsonSerializer.Serialize(new { path = missingDirectory, ensured = true, existedBefore = true }));
        Check(Ledger(cwdRecovery).GetArrayLength() == 1, "an existing-directory ensure cannot retire a process precondition");
        cwdRecovery.Model(new { action = "tool", name = "file.mkdir", arguments = new { path = missingDirectory } });
        cwdRecovery.Tool(true, JsonSerializer.Serialize(new { path = missingDirectory, ensured = true, existedBefore = false }), omitEvidence: true);
        cwdRecovery.Model(RunRequest());
        Check(cwdRecovery.Is("tool") && !cwdRecovery.Step.State.TryGetProperty("admissionRejections", out _),
            "a correlated actual creation of the exact working directory enables re-admission of its rejected process");

        var invalidCwd = Create();
        invalidCwd.Select("process.run", schema);
        invalidCwd.Model(new { action = "tool", name = "process.run", arguments = new
            { executableId = "dotnet", arguments = Array.Empty<string>(), workingDirectory = "invalid\0directory" } });
        Reject(invalidCwd, "Invalid working directory.");
        invalidCwd.Model(new { action = "tool", name = "process.run", arguments = new
            { executableId = "dotnet", arguments = Array.Empty<string>(), workingDirectory = missingDirectory } });
        Check(invalidCwd.Is("tool"), "a rejection with no canonical resource dependency preserves a usable next correction");

        var legacyCopy = Create();
        legacyCopy.Select("file.copy", schema);
        legacyCopy.Select("file.write", schema);
        legacyCopy.Model(CopyRequest()); Reject(legacyCopy, "Copy requires an existing source file.");
        var legacyCopyNode = JsonNode.Parse(legacyCopy.Step.State.GetRawText())!.AsObject();
        legacyCopyNode.Remove("admissionRecoveryRunId"); legacyCopyNode.Remove("admissionRejections");
        legacyCopy.RestoreState(CerebralJson.Element(legacyCopyNode));
        legacyCopy.Model(new { action = "tool", name = "file.write", arguments = new { path = missingSource, content = "legacy source" } });
        legacyCopy.Tool(true, JsonSerializer.Serialize(new { path = missingSource, sha256 = hash, bytes = 13 }));
        legacyCopy.Model(CopyRequest());
        Check(legacyCopy.Is("tool") && !legacyCopy.Step.State.TryGetProperty("admissionRejections", out _),
            "legacy copy rejection upgrade includes exact source dependencies before the correcting write");

        var missingOwner = Create();
        missingOwner.Model(NoOp()); Reject(missingOwner);
        var ownerNode = JsonNode.Parse(missingOwner.Step.State.GetRawText())!.AsObject();
        ownerNode.Remove("admissionRecoveryRunId");
        missingOwner.RestoreState(CerebralJson.Element(ownerNode));
        missingOwner.Model(NoOp());
        Check(missingOwner.Step.Next is CompleteWorkflow { Success: false } &&
            missingOwner.Summary.Contains("admission-recovery ledger", StringComparison.Ordinal),
            "non-legacy saved rejection entries without an owning accepted run fail closed");

        var bounded = Create();
        for (var attempt = 0; attempt < 17; attempt++)
        {
            bounded.Model(NoOp("capacity-" + attempt, "resource-" + attempt));
            Reject(bounded);
            if (attempt < 16) Read(bounded, attempt + 100);
        }
        Check(bounded.Step.Next is CompleteWorkflow { Success: false } &&
            Ledger(bounded).GetArrayLength() == 16 &&
            bounded.Summary.Contains("sixteen unresolved", StringComparison.Ordinal),
            "capacity stops visibly and preserves its exact effect instead of evicting an unresolved budget");
        var retainedBytes = Encoding.UTF8.GetByteCount(Ledger(bounded).GetRawText());
        Check(retainedBytes < 16 * 1024, "the measured sixteen-entry ledger remains small and contains no argument bodies");
        Console.WriteLine($"Admission recovery bounded ledger: {retainedBytes} UTF-8 bytes for sixteen unresolved fixture resources.");
        return checks;
    }
}
