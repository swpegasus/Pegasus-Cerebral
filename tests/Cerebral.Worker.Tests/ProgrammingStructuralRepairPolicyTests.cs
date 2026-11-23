using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ProgrammingHarnessTests
{
    static partial void StructuralRepairPolicy(Action<bool, string> check)
    {
        Fixture New() => new(requiredChecks: ["build"],
            selectedTools: ["file.read", "file.write", "file.edit-range", "program.source.structure", "process.run"],
            guardFinalization: true);
        void Proposal(Fixture f, int first = 10, int count = 3, string text = "") => f.Model(new
        {
            action = "tool", name = "file.edit-range", arguments = new
            { path = Path.Combine(f.Root, "Program.cs"), expectedSha256 = f.Step.State.GetProperty("workingSource").GetProperty("sha256").GetString(), startLine = first, lineCount = count, newText = text }
        });
        JsonElement Diagnostic(Fixture f) => f.Step.State.GetProperty("lastFailedBuild").GetProperty("diagnostic");

        var repair = New(); repair.SourceWindow(1, 5);
        Proposal(repair);
        check(repair.IsTool && repair.PendingTool == "program.source.structure" &&
            repair.Step.State.GetProperty("editCount").GetInt32() == 0, "destructive C# proposal prepares selected geometry without dispatching or crediting the edit");
        repair.PreparedStructure(recovered: true);
        check(repair.IsModel && repair.Step.State.GetProperty("structurePreparation").GetProperty("recovered").GetBoolean(),
            "recovered syntax is retained as a caveat, not an eligible-edit-span requirement");
        Proposal(repair);
        check(repair.IsTool && repair.PendingTool == "file.read" &&
            repair.Command.Arguments.GetProperty("arguments").GetProperty("startLine").GetInt32() == 9 &&
            repair.Command.Arguments.GetProperty("arguments").GetProperty("lineCount").GetInt32() == 5,
            "preparation reads only missing proposed lines plus immediate boundary anchors");
        repair.SourceReply(9, 5);
        Proposal(repair);
        check(repair.IsTool && repair.PendingTool == "file.edit-range",
            "a reconsidered proposal can proceed with recovered geometry and actual same-preimage boundary source");
        check(repair.Step.State.GetProperty("structurePreparation").ValueKind == JsonValueKind.Null &&
            repair.Step.State.GetProperty("structuralIntent").ValueKind == JsonValueKind.Null,
            "all structural preparation is invalidated before mutation dispatch");
        repair.RangeResult();
        check(repair.Step.State.GetProperty("editCount").GetInt32() == 1 &&
            repair.Step.State.GetProperty("lastMutation").GetProperty("startLine").GetInt32() == 10,
            "only admitted changed semantic receipt records mutation credit and bounded focus metadata");

        var failure = New(); failure.SourceWindow(1, 5); Proposal(failure); failure.Tool(false, "structure provider unavailable");
        Proposal(failure);
        check(failure.Step.Next is CompleteWorkflow { Success: false } &&
            failure.Step.State.GetProperty("editCount").GetInt32() == 0, "failed same-preimage preparation cannot cause automatic tool retry or mutation");
        var forged = New(); forged.SourceWindow(1, 5); Proposal(forged); forged.PreparedStructure(wrongHash: true); Proposal(forged);
        check(forged.Step.Next is CompleteWorkflow { Success: false }, "mismatched geometry preimage cannot satisfy structural preparation");
        var partialGeometry = New(); partialGeometry.SourceWindow(1, 5); Proposal(partialGeometry);
        partialGeometry.PreparedStructure(truncated: true); Proposal(partialGeometry);
        check(partialGeometry.Step.Next is CompleteWorkflow { Success: false }, "a valid-shaped but truncated geometry response is not successful preparation");
        var impossibleGeometry = New(); impossibleGeometry.SourceWindow(1, 5); Proposal(impossibleGeometry);
        impossibleGeometry.PreparedStructure(physicalLines: 2); Proposal(impossibleGeometry);
        check(impossibleGeometry.Step.Next is CompleteWorkflow { Success: false }, "geometry cannot claim a focus outside its physical file");
        var ordinary = New(); ordinary.SourceWindow(1, 5); Proposal(ordinary, text: "corrected");
        check(ordinary.IsTool && ordinary.PendingTool == "file.edit-range", "ordinary replacement without a structural failure keeps opt-in survey behavior");
        var unselected = new Fixture(requiredChecks: ["build"], selectedTools: ["file.read", "file.edit-range", "process.run"],
            guardFinalization: true);
        unselected.SourceWindow(1, 5); Proposal(unselected);
        check(unselected.IsTool && unselected.PendingTool == "file.edit-range", "unselected structure capability is never called or implicitly granted");

        var diagnostic = New(); diagnostic.CoverageWindow(1, 417, 143); diagnostic.CoverageWindow(144, 60, 60);
        var compiler = Path.Combine(diagnostic.Root, "Program.cs") + "(350,2): error CS1513: } expected";
        diagnostic.Dotnet("build"); diagnostic.Tool(false, compiler, 1, error: compiler);
        check(Diagnostic(diagnostic).GetProperty("code").GetString() == "CS1513" &&
            Diagnostic(diagnostic).GetProperty("line").GetInt32() == 350, "bounded actual-status compiler code/location is retained as typed progress");
        var guide = diagnostic.Progress().GetProperty("sourceGuidance");
        check(guide.GetProperty("nextUncoveredRange").GetProperty("startLine").GetInt32() == 330 &&
            guide.GetProperty("repairWork").GetProperty("basis").GetString() == "last-compiler-diagnostic-context",
            "compiler context takes precedence over another linear whole-file sweep");
        diagnostic.ContextRejected(ContextProjectionStatus.NeedsCompaction); diagnostic.CompactSuccess();
        check(Diagnostic(diagnostic).GetProperty("code").GetString() == "CS1513" &&
            diagnostic.Progress().GetProperty("repairAdvice").GetProperty("diagnostic").GetProperty("line").GetInt32() == 350,
            "compaction preserves exact compiler facts without relying on generated checkpoint text");
        diagnostic.CoverageWindow(330, 60, 60);
        diagnostic.ContextRejected(ContextProjectionStatus.NeedsCompaction); diagnostic.CompactSuccess();
        guide = diagnostic.Progress().GetProperty("sourceGuidance");
        check(guide.GetProperty("nextUncoveredRange").ValueKind == JsonValueKind.Null &&
            guide.GetProperty("repairWork").GetProperty("sourceComplete").GetBoolean() &&
            guide.GetProperty("activePhysicalIntervals").GetArrayLength() == 1,
            "visible diagnostic work unit stops recommending an unrelated missing head after compaction");
        check(!diagnostic.Step.State.GetProperty("lastFailedBuild").GetRawText().Contains("Source:", StringComparison.Ordinal),
            "durable compiler progress contains no source-body or raw-output cache");
        Proposal(diagnostic, first: 340, count: 2, text: "}");
        check(diagnostic.IsTool && diagnostic.PendingTool == "program.source.structure",
            "a nonempty C# replacement after a structural compiler diagnostic also requires selected preparation");
        diagnostic.PreparedStructure(physicalLines: 417);
        diagnostic.Write();
        check(diagnostic.Step.State.GetProperty("lastFailedBuild").GetProperty("diagnostic").ValueKind == JsonValueKind.Null,
            "a mutation attempt invalidates active diagnostic applicability before outcome");
        diagnostic.Uncertain();
        check(diagnostic.Step.Next is CompleteWorkflow { Success: false }, "unknown write stays terminal rather than restoring stale repair facts");

        var changed = New(); changed.SourceWindow(1, 5);
        var oldError = Path.Combine(changed.Root, "Program.cs") + "(18,2): error CS1513: } expected";
        changed.Dotnet("build"); changed.Tool(false, oldError, 1, error: oldError);
        changed.SourceWindow(6, 5, " external change");
        check(changed.Progress().GetProperty("sourceGuidance").GetProperty("repairWork").ValueKind == JsonValueKind.Null &&
            Diagnostic(changed).ValueKind == JsonValueKind.Null,
            "a new observed preimage cannot inherit current applicability of an older correlated compiler location");
        var foreign = New(); foreign.Read(); foreign.Dotnet("build");
        foreign.Tool(false, "C:\\outside\\External.cs(1,1): error CS1513: } expected", 1,
            error: "C:\\outside\\External.cs(1,1): error CS1513: } expected");
        check(Diagnostic(foreign).ValueKind == JsonValueKind.Null, "compiler paths outside the accepted root cannot become repair targets");

        var mixedPaths = New(); mixedPaths.SourceWindow(1, 5); mixedPaths.Dotnet("build");
        var absoluteDiagnostic = Path.Combine(mixedPaths.Root, "Program.cs") + "(4,2):\terror\tCS1002: ; expected";
        mixedPaths.Tool(false, JsonSerializer.Serialize(new
        {
            exitCode = 1, stdout = "Program.cs(1,1): error CS1003: relative location\n" + absoluteDiagnostic, stderr = ""
        }), 1, error: "dotnet exited1");
        check(Diagnostic(mixedPaths).GetProperty("code").GetString() == "CS1002" &&
            Diagnostic(mixedPaths).GetProperty("path").GetString() == Path.Combine(mixedPaths.Root, "Program.cs") &&
            Diagnostic(mixedPaths).GetProperty("line").GetInt32() == 4,
            "producer two-row compiler batch skips an inadmissible relative target and retains the later canonical in-root diagnostic");
        var relativeOnly = New(); relativeOnly.SourceWindow(1, 5); relativeOnly.Dotnet("build");
        relativeOnly.Tool(false, JsonSerializer.Serialize(new
        {
            exitCode = 1, stdout = "Program.cs(1,1): error CS1513: } expected", stderr = ""
        }), 1, error: "dotnet exited1");
        check(Diagnostic(relativeOnly).ValueKind == JsonValueKind.Null,
            "relative compiler paths remain passive evidence and are never guessed into repair targets");

        var bounded = New(); bounded.CoverageWindow(1, 417, 143);
        check(!bounded.Step.State.GetProperty("workingSource").TryGetProperty("projectionLimited", out _),
            "provider truncation alone does not claim a proved clipped readable projection");
        var clipped = New(); clipped.ReadRequest(1, 1000, "GeneratedServicePlugin.cs"); clipped.Tool(true, clipped.ProjectionResult());
        var working = clipped.Step.State.GetProperty("workingSource"); var visible = working.GetProperty("visibleLineCount").GetInt32();
        check(working.GetProperty("projectionLimited").GetBoolean() && visible < 176,
            "clipped-prefix marker is derived from raw returned lines beyond actual displayed source");
        clipped.ChangeState(state => state["lastDiagnosticBuildEditCount"] = 0); // Isolate the guard after a prior diagnostic attempt.
        clipped.ReadRequest(1, 900, "GeneratedServicePlugin.cs");
        check(clipped.IsModel && clipped.Step.State.GetProperty("repeatedReadAttempts").GetInt32() == 1 &&
            clipped.Messages.Contains("clipped source prefix", StringComparison.Ordinal),
            "a changed broad count cannot pretend to extend a retained proved clipped prefix");
        var overlap = New(); overlap.ReadRequest(1, 1000, "GeneratedServicePlugin.cs"); overlap.Tool(true, overlap.ProjectionResult());
        var shown = overlap.Step.State.GetProperty("workingSource").GetProperty("visibleLineCount").GetInt32();
        overlap.ReadRequest(1, shown, "GeneratedServicePlugin.cs");
        check(overlap.IsTool, "an explicit bounded overlap remains advisory rather than a new broad read ban");
        var lost = New(); lost.ReadRequest(1, 1000, "GeneratedServicePlugin.cs"); lost.Tool(true, lost.ProjectionResult());
        var id = lost.Step.State.GetProperty("workingSource").GetProperty("contributionId").GetString();
        lost.ChangeState(state => { var list = state["contributions"]!.AsArray(); list.Remove(list.Single(value => value!["id"]!.GetValue<string>() == id)); });
        lost.ReadRequest(1, 900, "GeneratedServicePlugin.cs");
        check(lost.IsTool, "a lost original prefix body permits legitimate broad reacquisition despite retained markers");

        (Fixture Harness, string OldReference, string OtherId, string OldHash) AcrossFiles(bool changedPreimage)
        {
            var f = New(); f.ReadRequest(1, 1000, "GeneratedServicePlugin.cs"); f.Tool(true, f.ProjectionResult());
            var a = f.Step.State.GetProperty("workingSource");
            var old = f.Step.State.GetProperty("readProjectionReferences").EnumerateArray()
                .Single(item => item.GetProperty("contributionId").GetString() == a.GetProperty("contributionId").GetString()).GetRawText();
            var hash = a.GetProperty("sha256").GetString()!;
            f.SourceWindow(1, 5);
            var other = f.Step.State.GetProperty("workingSource").GetProperty("contributionId").GetString()!;
            f.ReadRequest(144, 10, "GeneratedServicePlugin.cs");
            f.Tool(true, changedPreimage ? f.ChangedProjectionResult(144, 10) : f.ProjectionResult(144, 10), truncated: true);
            return (f, old, other, hash);
        }
        var (multi, oldReference, otherId, oldHash) = AcrossFiles(changedPreimage: true);
        var current = multi.Step.State.GetProperty("workingSource");
        var references = multi.Step.State.GetProperty("readProjectionReferences").EnumerateArray().ToArray();
        check(current.GetProperty("sha256").GetString() != oldHash &&
            references.Where(item => item.GetProperty("source").GetProperty("path").GetString() == current.GetProperty("path").GetString())
                .All(item => item.GetProperty("source").GetProperty("sha256").GetString() == current.GetProperty("sha256").GetString()),
            "A/hash1 then B then bounded A/hash2 retires every old validated mapping for A");
        check(references.Any(item => item.GetProperty("contributionId").GetString() == otherId) &&
            multi.Step.State.GetProperty("contributions").EnumerateArray().Any(item => item.GetProperty("id").GetString() == otherId),
            "changed A preimage preserves B's exact mapping and original body");
        check(multi.Step.State.GetProperty("readHistory").EnumerateArray().Any(item =>
            item.GetProperty("span").GetProperty("sha256").GetString() == oldHash),
            "retired A preimage may remain bounded logical history without current visibility");
        multi.ReadRequest(1, 900, "GeneratedServicePlugin.cs");
        check(multi.IsTool, "broad A reacquisition is admitted after an interleaved new A preimage");
        var (same, _, _, _) = AcrossFiles(changedPreimage: false);
        same.ChangeState(state => state["lastDiagnosticBuildEditCount"] = 0);
        same.ReadRequest(1, 900, "GeneratedServicePlugin.cs");
        check(same.IsModel && same.Messages.Contains("clipped source prefix", StringComparison.Ordinal),
            "interleaving B does not bypass a retained same-hash clipped A prefix");
        var (missing, missingOld, _, _) = AcrossFiles(changedPreimage: false);
        var missingId = JsonNode.Parse(missingOld)!["contributionId"]!.GetValue<string>();
        missing.ChangeState(state => { var bodies = state["contributions"]!.AsArray(); bodies.Remove(bodies.Single(item => item!["id"]!.GetValue<string>() == missingId)); });
        missing.ReadRequest(1, 900, "GeneratedServicePlugin.cs");
        check(missing.IsTool, "interleaved lost clipped body permits reacquisition despite same-hash metadata");
        var (legacyMixed, legacyOld, _, _) = AcrossFiles(changedPreimage: true);
        var legacyId = JsonNode.Parse(legacyOld)!["contributionId"]!.GetValue<string>();
        check(legacyMixed.Step.State.GetProperty("contributions").EnumerateArray().Any(item => item.GetProperty("id").GetString() == legacyId),
            "mixed-preimage recovery fixture retains the original clipped body");
        legacyMixed.ChangeState(state => state["readProjectionReferences"]!.AsArray().Insert(0, JsonNode.Parse(legacyOld)));
        legacyMixed.ReadRequest(1, 900, "GeneratedServicePlugin.cs");
        check(legacyMixed.IsTool, "legacy mixed-preimage mappings cannot override the latest active A hash in alias selection");

        var unknown = New(); unknown.ReadRequest(1, 1000, "GeneratedServicePlugin.cs"); unknown.Tool(true, unknown.ProjectionResult());
        unknown.ChangeState(state => { state["workingSource"] = null; foreach (var reference in state["readProjectionReferences"]!.AsArray()) reference!.AsObject().Remove("source"); });
        unknown.ReadRequest(1, 900, "GeneratedServicePlugin.cs");
        check(unknown.IsTool, "legacy missing typed projection evidence remains unknown and permits reacquisition");
        CoverageDiagnosticPolicy(check);
        DraftBoundaryPolicy(check);
    }

    private static void DraftBoundaryPolicy(Action<bool, string> check)
    {
        Fixture New() => new(requiredChecks: ["build"],
            selectedTools: ["file.read", "file.edit-range", "program.source.structure", "process.run"],
            guardFinalization: true);
        void Propose(Fixture f, int first = 10, int count = 1, string text = "nextCall();") => f.Model(new
        {
            action = "tool", name = "file.edit-range", arguments = new
            { path = Path.Combine(f.Root, "Program.cs"), expectedSha256 = f.Step.State.GetProperty("workingSource").GetProperty("sha256").GetString(),
                startLine = first, lineCount = count, newText = text }
        });
        Fixture Failed()
        {
            var f = New(); f.SourceWindow(1, 20);
            var message = Path.Combine(f.Root, "Program.cs") + "(10,2): error CS1002: ; expected";
            f.Dotnet("build"); f.Tool(false, message, 1, error: message);
            return f;
        }
        var exact = Failed(); Propose(exact);
        check(exact.IsTool && exact.PendingTool == "program.source.structure" &&
            exact.Command.Arguments.GetProperty("arguments").GetProperty("proposedEdit").GetProperty("newText").GetString() == "nextCall();",
            "CS1002 nonempty replacement prepares the exact proposed fragment through the existing selected tool");
        exact.PreparedStructure();
        check(exact.IsModel && exact.Step.State.GetProperty("editCount").GetInt32() == 0 &&
            !exact.Step.State.GetProperty("structurePreparation").GetRawText().Contains("nextCall", StringComparison.Ordinal),
            "draft evidence returns to the model with fingerprint-only preparation, without edit credit or replacement cache");
        Propose(exact, count: 2);
        check(exact.IsTool && exact.PendingTool == "program.source.structure",
            "a changed range with the same focus requires its own preparation");
        exact.PreparedStructure(); Propose(exact, count: 2, text: "otherCall();");
        check(exact.IsTool && exact.PendingTool == "program.source.structure",
            "a changed replacement with the same preimage, range and focus requires fresh preparation");
        exact.PreparedStructure(); Propose(exact, count: 2, text: "otherCall();");
        check(exact.IsTool && exact.PendingTool == "file.edit-range",
            "an exact reconsidered draft can proceed despite recovered syntax, without a zero-error gate");

        var legacy = New(); legacy.SourceWindow(1, 20);
        legacy.Model(new { action = "tool", name = "program.source.structure", arguments = new {
            path = Path.Combine(legacy.Root, "Program.cs"),
            expectedSha256 = legacy.Step.State.GetProperty("workingSource").GetProperty("sha256").GetString(), focusLine = 10 } });
        legacy.PreparedStructure(); Propose(legacy, text: "");
        check(legacy.IsTool && legacy.PendingTool == "program.source.structure",
            "ordinary no-draft inspection remains valid but cannot satisfy exact draft preparation");

        foreach (var field in new[] { "path", "sourceSha256", "startLine", "lineCount", "newTextSha256" })
        {
            var forged = Failed(); Propose(forged);
            forged.PreparedStructure(change: response =>
            {
                var fingerprint = response["proposedEdit"]!["fingerprint"]!;
                fingerprint[field] = field is "startLine" or "lineCount" ? JsonValue.Create(99) : JsonValue.Create("wrong");
            });
            Propose(forged);
            check(forged.Step.Next is CompleteWorkflow { Success: false } && forged.Step.State.GetProperty("editCount").GetInt32() == 0,
                "mismatched draft fingerprint " + field + " cannot admit a mutation");
        }
        foreach (var mode in new[] { "missing", "malformed", "extra", "missing-boundary", "invalid-token" })
        {
            var malformed = Failed(); Propose(malformed);
            malformed.PreparedStructure(change: response =>
            {
                if (mode == "missing") response.Remove("proposedEdit");
                else if (mode == "malformed") response["proposedEdit"]!["fingerprint"] = "bad";
                else if (mode == "extra") response["proposedEdit"]!["fingerprint"]!["newText"] = "forbidden";
                else if (mode == "missing-boundary") response["proposedEdit"]!.AsObject().Remove("startBoundary");
                else response["proposedEdit"]!["startBoundary"]!["tokenKind"] = "not-a-token";
            });
            Propose(malformed);
            check(malformed.Step.Next is CompleteWorkflow { Success: false },
                "incomplete or malformed draft evidence is rejected: " + mode);
        }
        var shortFile = Failed(); Propose(shortFile, first: 20);
        shortFile.PreparedStructure(physicalLines: 19); Propose(shortFile, first: 20);
        check(shortFile.Step.Next is CompleteWorkflow { Success: false },
            "returned physical extent must contain the exact proposal even when its clamped focus is valid");
        var changed = Failed(); Propose(changed); changed.PreparedStructure();
        changed.SourceWindow(6, 5, " changed");
        check(changed.Step.State.GetProperty("structurePreparation").ValueKind == JsonValueKind.Null,
            "a newly observed source preimage retires exact draft preparation");
        var badDraft = New(); badDraft.SourceWindow(1, 20);
        badDraft.Model(new { action = "tool", name = "program.source.structure", arguments = new {
            path = Path.Combine(badDraft.Root, "Program.cs"),
            expectedSha256 = badDraft.Step.State.GetProperty("workingSource").GetProperty("sha256").GetString(),
            proposedEdit = new { startLine = 10, lineCount = 0, newText = "" } } });
        check(badDraft.IsModel && badDraft.Step.State.GetProperty("repairs").GetInt32() == 1,
            "malformed proposed range is corrected before any structure effect dispatch");
    }

    private static void CoverageDiagnosticPolicy(Action<bool, string> check)
    {
        Fixture New(string[]? checks = null) => new(requiredChecks: checks ?? ["build"],
            selectedTools: ["file.read", "file.write", "file.edit-range", "program.source.structure", "process.run"],
            guardFinalization: true);
        void Head(Fixture f) => f.InspectionWindow(1, 500);
        void Remainder(Fixture f, bool terminalEmpty = false)
        {
            var through = f.Step.State.GetProperty("workingSource").GetProperty("visibleLineCount").GetInt32();
            f.InspectionWindow(through + 1, 60, terminalEmpty);
            f.InspectionWindow(through + 61, 323 - through - 60, terminalEmpty);
        }
        void Alias(Fixture f) => f.ReadRequest(1, 323);
        JsonElement Guide(Fixture f) => f.Progress().GetProperty("sourceGuidance");

        var inspected = New(); Head(inspected);
        check(inspected.Step.State.GetProperty("workingSource").GetProperty("projectionLimited").GetBoolean(),
            "generation-zero diagnostic fixture has a proved clipped broad head");
        Remainder(inspected);
        var guide = Guide(inspected);
        check(guide.GetProperty("allPhysicalSourceLinesCovered").GetBoolean() &&
            guide.GetProperty("nextUncoveredRange").ValueKind == JsonValueKind.Null &&
            !guide.GetProperty("terminalCoordinateMayBeEmpty").GetBoolean(),
            "three retained same-preimage windows cover all323 nonempty physical coordinates");
        check(guide.GetProperty("inspectionAdvice").GetString()!.Contains("All physical source lines", StringComparison.Ordinal) &&
            !guide.GetProperty("inspectionAdvice").GetString()!.Contains("startLine=", StringComparison.Ordinal),
            "complete coverage gives no stale continuation after the clipped head");
        Alias(inspected);
        check(inspected.IsTool && inspected.PendingTool == "process.run" &&
            inspected.Argv.SequenceEqual(new[] { "build", "Hello.csproj", "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal" }),
            "a typed zero-edit service inspection stall dispatches only the already accepted canonical build");
        check(inspected.Step.State.GetProperty("lastDiagnosticBuildEditCount").GetInt32() == 0 &&
            inspected.Step.State.GetProperty("readObservations").GetInt32() == 3 &&
            inspected.Step.State.GetProperty("editCount").GetInt32() == 0 && inspected.CheckCount == 0,
            "diagnostic attempt is marked before dispatch without repeat-read, edit or passing-check credit");
        var compiler = Path.Combine(inspected.Root, "Program.cs") + "(323,1): error CS1513: } expected";
        inspected.Tool(false, compiler, 1, error: compiler);
        check(inspected.Step.State.GetProperty("lastFailedBuild").GetProperty("diagnostic").GetProperty("code").GetString() == "CS1513" &&
            inspected.CheckCount == 0 && inspected.IsModel,
            "failed baseline check supplies typed compiler evidence, never completion or an injected repair");
        Alias(inspected);
        check(inspected.IsModel && inspected.Step.State.GetProperty("repairs").GetInt32() == 1 &&
            inspected.Messages.Contains("All physical source lines", StringComparison.Ordinal),
            "same-generation retry keeps no-repeat protection and truthful complete coverage after the one diagnostic attempt");
        Alias(inspected);
        check(inspected.IsModel && inspected.Step.State.GetProperty("repairs").GetInt32() == 2,
            "the second ordinary retained-read correction remains bounded");
        Alias(inspected);
        check(inspected.Step.Next is CompleteWorkflow { Success: false } &&
            inspected.Step.State.GetProperty("repairs").GetInt32() == 2 &&
            inspected.Step.State.GetProperty("lastDiagnosticBuildEditCount").GetInt32() == 0,
            "a third unproductive correction terminates without another automatic build or mutation");

        var missing = New(["test"]); Head(missing);
        var firstGap = Guide(missing).GetProperty("nextUncoveredRange").GetProperty("startLine").GetInt32();
        Alias(missing);
        check(missing.IsModel && missing.Messages.Contains("startLine=" + firstGap + ", lineCount=60", StringComparison.Ordinal),
            "rejected clipped head and progress use the same actual-body missing range");
        check(missing.Step.State.GetProperty("lastDiagnosticBuildEditCount").GetInt32() == -1,
            "an unconfigured build is never added to a test-only assignment");
        var exact = New(["test"]); Head(exact); Remainder(exact);
        exact.ReadRequest(1, 500);
        check(exact.IsModel && exact.Messages.Contains("All physical source lines", StringComparison.Ordinal),
            "exact-repeat correction shares complete physical coverage advice with clipped-alias correction");

        var unknown = New(); Head(unknown); Alias(unknown); unknown.Uncertain();
        check(unknown.Step.Next is CompleteWorkflow { Success: false } &&
            unknown.Step.State.GetProperty("lastDiagnosticBuildEditCount").GetInt32() == 0 && unknown.CheckCount == 0,
            "unknown baseline execution remains terminal with attempt marker retained, never replayed");
        var refused = New(); Head(refused); Alias(refused); refused.Tool(false, "build execution was not admitted");
        Alias(refused);
        check(refused.IsModel && refused.Step.State.GetProperty("lastDiagnosticBuildEditCount").GetInt32() == 0 &&
            refused.CheckCount == 0, "unconfirmed failed launch cannot cause a second automatic baseline build");
        var passed = New(); Head(passed); passed.Dotnet("build"); passed.Tool(true, "Build succeeded", 0);
        Alias(passed);
        check(passed.IsModel && passed.CheckCount == 1,
            "an existing matching successful check is not automatically executed again on a read stall");

        var eof = New(["test"]); eof.InspectionWindow(1, 500, terminalEmpty: true);
        Remainder(eof, terminalEmpty: true);
        guide = Guide(eof);
        check(guide.GetProperty("terminalCoordinateMayBeEmpty").GetBoolean() &&
            guide.GetProperty("nextUncoveredRange").GetProperty("startLine").GetInt32() == 324 &&
            guide.GetProperty("nextUncoveredRange").GetProperty("lineCount").GetInt32() == 1,
            "unseen producer EOF coordinate stays an explicit bounded uncertainty rather than claimed physical coverage");
        Alias(eof);
        check(eof.IsModel && eof.Messages.Contains("startLine=324, lineCount=1", StringComparison.Ordinal) &&
            eof.Messages.Contains("terminal empty segment", StringComparison.Ordinal),
            "clipped correction points to the current EOF probe, not an already-covered old continuation");
        eof.InspectionWindow(324, 1, terminalEmpty: true);
        check(Guide(eof).GetProperty("allPhysicalSourceLinesCovered").GetBoolean() &&
            !Guide(eof).GetProperty("terminalCoordinateMayBeEmpty").GetBoolean() &&
            eof.Step.State.GetProperty("workingSource").GetProperty("physicalLineCount").GetInt32() == 0,
            "observed terminal empty segment resolves extent and grants no editable physical line");

        var lost = New(); Head(lost);
        var bodyId = lost.Step.State.GetProperty("workingSource").GetProperty("contributionId").GetString();
        lost.ChangeState(state => { var bodies = state["contributions"]!.AsArray(); bodies.Remove(bodies.Single(item => item!["id"]!.GetValue<string>() == bodyId)); });
        Alias(lost);
        check(lost.IsTool && lost.PendingTool == "file.read" &&
            lost.Step.State.GetProperty("lastDiagnosticBuildEditCount").GetInt32() == -1,
            "actual source-body loss permits reacquisition without a cache or forced diagnostic build");
    }

    private sealed partial class Fixture
    {
        public void InspectionWindow(int start, int count, bool terminalEmpty = false)
        {
            var source = string.Join('\n', Enumerable.Range(1, 323).Select(line => "// line " + line + " " + new string('x', 25))) +
                (terminalEmpty ? "\n" : "");
            var lines = source.Split('\n');
            ReadRequest(start, count);
            Tool(true, JsonSerializer.Serialize(new
            {
                path = Path.Combine(Root, "Program.cs"),
                sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(),
                startLine = start, totalLines = lines.Length, content = string.Join('\n', lines.Skip(start - 1).Take(count))
            }), truncated: (long)start + count - 1 < lines.Length);
        }
        public string ChangedProjectionResult(int start, int count)
        {
            var source = ProgrammingReadProjectionFixture.Source.Replace("using System;", "using System; // changed preimage", StringComparison.Ordinal);
            var lines = source.Split('\n');
            return JsonSerializer.Serialize(new
            {
                path = Path.Combine(Root, "GeneratedServicePlugin.cs"),
                sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(),
                startLine = start, totalLines = lines.Length, content = string.Join('\n', lines.Skip(start - 1).Take(count))
            });
        }
        public void PreparedStructure(bool recovered = true, bool wrongHash = false, bool truncated = false, int physicalLines = 20,
            Action<JsonObject>? change = null)
        {
            var args = Command.Arguments.GetProperty("arguments");
            var response = JsonSerializer.SerializeToNode(new
            {
                schema = "program.source.structure/1", path = args.GetProperty("path").GetString(),
                sha256 = wrongHash ? new string('f', 64) : args.GetProperty("expectedSha256").GetString(),
                focusLine = args.GetProperty("focusLine").GetInt32(), physicalLines, recovered,
                syntaxErrors = recovered ? 1 : 0, nodes = Array.Empty<object>(), omittedNodes = 0
            })!.AsObject();
            if (args.TryGetProperty("proposedEdit", out var draft))
            {
                var text = draft.GetProperty("newText").GetString()!;
                var boundary = new { tokenKind = "CloseBraceToken", tokenStart = new { line = 10, column = 1 },
                    tokenEnd = new { line = 10, column = 2 }, statement = (object?)null, initializer = (object?)null };
                response["proposedEdit"] = JsonSerializer.SerializeToNode(new
                {
                    fingerprint = new { path = args.GetProperty("path").GetString(), sourceSha256 = args.GetProperty("expectedSha256").GetString(),
                        startLine = draft.GetProperty("startLine").GetInt32(), lineCount = draft.GetProperty("lineCount").GetInt32(),
                        newTextSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() },
                    startBoundary = boundary, endBoundary = boundary,
                    replacement = new { firstTokenKind = string.IsNullOrWhiteSpace(text) ? null : "IdentifierToken",
                        lastTokenKind = string.IsNullOrWhiteSpace(text) ? null : "SemicolonToken", utf8Bytes = Encoding.UTF8.GetByteCount(text),
                        lfCount = text.Count(ch => ch == '\n'), crCount = text.Count(ch => ch == '\r'), endsWithLf = text.EndsWith('\n') }
                });
            }
            change?.Invoke(response);
            Tool(true, response.ToJsonString(), truncated: truncated);
        }
    }
}
