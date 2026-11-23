using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static partial class PluginControlTests
{
    public static async Task RunInvokeAssertionsOnlyAsync()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Invoke postcondition: " + name);
            checks++;
        }
        bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or IOException or InvalidOperationException or InvalidDataException)
            { return true; }
        }
        await RunInvokeExpectedTextAssertionChecksAsync(Check, Rejects);
        var invokeChecks = checks;
        await RunExpectedTextAssertionChecksAsync(Check, Rejects);
        Console.WriteLine($"Invoke postconditions: {invokeChecks} SQLite/metadata checks plus {checks - invokeChecks} existing reopen assertion checks passed ({checks} total). No native plugin, UI or model execution.");
    }

    private static async Task RunInvokeExpectedTextAssertionChecksAsync(Action<bool, string> check, Func<Action, bool> rejects)
    {
        const string saved = "café saved\nsecond line";
        const string unsaved = "unsaved draft";
        using var f = new Fixture();
        var presentation = f.Present(f.Context());
        var field = Fixture.Id();
        PluginControlSnapshot Snapshot(string value, bool limited = false, bool fieldLimited = false, bool fieldVisible = true) =>
            new(f.MountId, ["Ready"], [new("Increment", true, true)], limited)
            { Fields = [new(field, value, true, fieldVisible, false, fieldLimited)] };
        PluginControlObservation Observation(string after, bool beforeLimited = false, bool afterLimited = false,
            bool fieldLimited = false, bool fieldVisible = true) =>
            new(Snapshot(unsaved, beforeLimited), Snapshot(after, afterLimited, fieldLimited, fieldVisible), ClickDelivered: true);
        async Task<(ToolExecutionContext Context, ToolCall Call, PluginControlRequest Request, ToolResult Result)> Run(
            IReadOnlyList<string>? expectedTexts, PluginControlObservation observation, string nativeStatus = "completed")
        {
            var context = f.Context();
            var call = f.Call(presentation.Id, "invoke", expectedTexts);
            var executing = f.Tool.ExecuteAsync(call, context, CancellationToken.None);
            var request = f.Controls.Read(context, context.EffectId!);
            var claim = f.Controls.Claim(request.Id, request.SessionId, Fixture.Id()).Request;
            f.Controls.Complete(request.Id, request.SessionId, claim.ClaimId!, nativeStatus, observation,
                nativeStatus == "completed" ? null : "The delivered native effect has no confirmed after-view.");
            return (context, call, request, await executing);
        }
        static JsonElement Body(ToolResult result)
        {
            using var parsed = JsonDocument.Parse(result.Content);
            return parsed.RootElement.Clone();
        }

        var supplied = new[] { saved };
        var copied = PluginControlAssertions.CopyExpectedTexts("invoke", supplied)!;
        supplied[0] = "changed caller list";
        check(copied.SequenceEqual([saved], StringComparer.Ordinal), "invocation admission detaches exact Unicode/newline expectations");
        var excessiveUtf8 = Enumerable.Range(0, 3).Select(index => new string('\u754c', 255) + index).ToArray();
        check(rejects(() => PluginControlAssertions.CopyExpectedTexts("invoke", [])) &&
            rejects(() => PluginControlAssertions.CopyExpectedTexts("invoke", Enumerable.Repeat("x", 9).ToArray())) &&
            rejects(() => PluginControlAssertions.CopyExpectedTexts("invoke", ["same", "same"])) &&
            rejects(() => PluginControlAssertions.CopyExpectedTexts("invoke", [""])) &&
            rejects(() => PluginControlAssertions.CopyExpectedTexts("invoke", [new string('x', 257)])) &&
            rejects(() => PluginControlAssertions.CopyExpectedTexts("invoke", ["bad\0text"])) &&
            rejects(() => PluginControlAssertions.CopyExpectedTexts("invoke", excessiveUtf8)) &&
            rejects(() => PluginControlAssertions.CopyExpectedTexts("inspect", [saved])) &&
            rejects(() => PluginControlAssertions.CopyExpectedTexts("setText", [saved])),
            "invocation uses the unchanged operation/count/character/uniqueness/NUL/UTF-8 bounds");
        var passed = await Run([saved], Observation(saved));
        var passBody = Body(passed.Result);
        check(passed.Result.Success && passed.Result.EffectiveOutcome == ToolExecutionOutcome.Succeeded &&
            passBody.GetProperty("assertion").GetProperty("status").GetString() == "passed" &&
            passBody.GetProperty("observation").GetProperty("before").GetProperty("fields")[0].GetProperty("text").GetString() == unsaved &&
            passBody.GetProperty("observation").GetProperty("after").GetProperty("fields")[0].GetProperty("text").GetString() == saved,
            "an expected value absent before the click can pass only from the exact visible after-field");
        check(!passBody.GetProperty("observation").GetProperty("instanceRecreated").GetBoolean() &&
            !passBody.GetProperty("assertion").TryGetProperty("editableFieldPreservation", out _),
            "invocation postconditions claim no instance recreation or editable persistence witness");
        var stored = new StateStore(f.Store.DirectoryPath).Get<PluginControlRequest>("plugin-control", passed.Request.Id)!;
        check(stored.ExpectedTexts!.SequenceEqual([saved]) && stored.PackageFingerprint == passed.Request.PackageFingerprint &&
            stored.Plugin.Generation == presentation.Plugin!.Generation && stored.AssertionPolicyVersion == PluginControlAssertions.CurrentPolicyVersion,
            "the existing durable receipt freezes expectations and exact approved generation before delivery");

        var failed = await Run([saved], Observation(unsaved));
        var failBody = Body(failed.Result);
        check(!failed.Result.Success && failed.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
            failed.Result.Evidence is { State: "failed", RequestId: not null } &&
            failBody.GetProperty("status").GetString() == "failed" && failBody.GetProperty("nativeStatus").GetString() == "completed" &&
            failBody.GetProperty("assertion").GetProperty("missingTexts").EnumerateArray().Single().GetString() == saved &&
            f.Controls.Read(failed.Context, failed.Request.Id).Status == "completed",
            "a delivered no-op invocation fails its declared postcondition while preserving actual native completion");

        const string multilineHint = "every missing expectation as a separate exact line in one visible field";
        static string AssertionMessage(ToolResult result) => Body(result).GetProperty("assertion").GetProperty("message").GetString()!;
        var contract = f.Tool.Tools.Single();
        var invokeSchema = contract.InputSchema.GetProperty("oneOf").EnumerateArray()
            .Single(variant => variant.GetProperty("properties").GetProperty("operation").GetProperty("enum")[0].GetString() == "invoke");
        check(contract.Description.Contains("exact whole visible TextBox or TextBlock", StringComparison.Ordinal) &&
            contract.Description.Contains("not a line or substring", StringComparison.Ordinal) &&
            invokeSchema.GetProperty("properties").GetProperty("expectedTexts").GetProperty("description").GetString()!
                .Contains("one complete string with exact newlines", StringComparison.Ordinal),
            "private contract advertises exact whole control values and one expectation for a multiline field");

        var fragments = new[] { "café saved", "second line" };
        var fragmentFailure = await Run(fragments, Observation(saved));
        var fragmentBody = Body(fragmentFailure.Result);
        check(!fragmentFailure.Result.Success && fragmentFailure.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
            fragmentBody.GetProperty("nativeStatus").GetString() == "completed" &&
            fragmentBody.GetProperty("assertion").GetProperty("missingTexts").EnumerateArray().Select(value => value.GetString())
                .SequenceEqual(fragments, StringComparer.Ordinal) &&
            fragmentBody.GetProperty("observation").GetProperty("after").GetProperty("fields")[0].GetProperty("text").GetString() == saved &&
            AssertionMessage(fragmentFailure.Result).Contains(multilineHint, StringComparison.Ordinal),
            "mistaken multiline fragments retain failed exact assertions and full native output while adding an actionable shape hint");
        check(AssertionMessage(fragmentFailure.Result).StartsWith("The completed native invocation is missing expected text", StringComparison.Ordinal) &&
            AssertionMessage(fragmentFailure.Result).Contains("This assertion remains failed.", StringComparison.Ordinal) &&
            AssertionMessage(fragmentFailure.Result).Contains("do not establish a runtime or source defect", StringComparison.Ordinal) &&
            fragmentBody.GetProperty("error").GetString() == AssertionMessage(fragmentFailure.Result) &&
            fragmentFailure.Result.Evidence is { ProviderId: PluginControlToolExtension.Provider, RequestId: not null, State: "failed" },
            "fixed correction text preserves original failure prefix, error provenance and exact receipt identity without claiming a source defect");
        var fragmentEvents = f.Events.Subscribe();
        var fragmentCount = f.Store.List<PluginControlRequest>("plugin-control").Count;
        var fragmentRecovery = await f.Tool.ReconcileAsync(fragmentFailure.Call, fragmentFailure.Context,
            fragmentFailure.Result.Evidence!, CancellationToken.None);
        check(fragmentRecovery.EffectiveOutcome == ToolExecutionOutcome.Failed &&
            fragmentRecovery.Evidence!.RequestId == fragmentFailure.Request.Id &&
            AssertionMessage(fragmentRecovery).Contains(multilineHint, StringComparison.Ordinal) &&
            f.Store.List<PluginControlRequest>("plugin-control").Count == fragmentCount && !fragmentEvents.Reader.TryRead(out _),
            "hinted failure reconciliation reads the same retained effect without replay or new native admission");
        f.Events.Unsubscribe(fragmentEvents.Id);
        foreach (var newline in new[] { "\r\n", "\r" })
        {
            var alternate = await Run(fragments, Observation(string.Join(newline, fragments)));
            check(alternate.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
                AssertionMessage(alternate.Result).Contains(multilineHint, StringComparison.Ordinal),
                "diagnostic recognizes exact line boundaries without changing CR or CRLF whole-value equality");
        }
        check(!AssertionMessage(passed.Result).Contains(multilineHint, StringComparison.Ordinal) &&
            !AssertionMessage(failed.Result).Contains(multilineHint, StringComparison.Ordinal),
            "a valid full-field expectation and actually missing output receive no mistaken-shape hint");
        var absentFragment = await Run(["café saved", "missing output"], Observation(saved));
        check(absentFragment.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
            !AssertionMessage(absentFragment.Result).Contains(multilineHint, StringComparison.Ordinal),
            "all missing expectations must be exact lines in the same field before diagnostic classification");
        foreach (var (expectation, output) in new[]
        {
            ("cafe saved", saved), ("Café saved", saved), ("café", saved), ("café saved", " café saved \nsecond line")
        })
        {
            var notExactLine = await Run([expectation], Observation(output));
            check(notExactLine.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
                !AssertionMessage(notExactLine.Result).Contains(multilineHint, StringComparison.Ordinal),
                "line-shape diagnostics do not normalize accents, whitespace or substrings");
        }
        foreach (var variant in new[] { "before-limited", "after-limited", "field-limited" })
        {
            var limitedFragments = await Run(fragments, Observation(saved, beforeLimited: variant == "before-limited",
                afterLimited: variant == "after-limited", fieldLimited: variant == "field-limited"));
            check(!AssertionMessage(limitedFragments.Result).Contains(multilineHint, StringComparison.Ordinal) &&
                Body(limitedFragments.Result).GetProperty("assertion").GetProperty("status").GetString() == "inconclusive",
                variant + " views cannot justify a multiline expectation correction hint");
        }
        var hiddenFragments = await Run(fragments, Observation(saved, fieldVisible: false));
        check(!AssertionMessage(hiddenFragments.Result).Contains(multilineHint, StringComparison.Ordinal),
            "hidden field lines cannot justify a visible multiline correction hint");
        var noClick = await Run(fragments, new(Snapshot(unsaved), Snapshot(saved)), "uncertain");
        check(!AssertionMessage(noClick.Result).Contains(multilineHint, StringComparison.Ordinal) &&
            Body(noClick.Result).GetProperty("assertion").GetProperty("status").GetString() == "inconclusive",
            "an undelivered click cannot be classified as a completed assertion-shape mismatch");
        foreach (var nativeStatus in new[] { "uncertain", "rejected" })
        {
            var incomplete = await Run(fragments, Observation(saved), nativeStatus);
            check(!incomplete.Result.Success && !AssertionMessage(incomplete.Result).Contains(multilineHint, StringComparison.Ordinal) &&
                Body(incomplete.Result).GetProperty("nativeStatus").GetString() == nativeStatus &&
                Body(incomplete.Result).GetProperty("error").GetString() == "The delivered native effect has no confirmed after-view.",
                nativeStatus + " runtime provenance remains unchanged and receives no completed-effect correction hint");
        }
        var secondField = Fixture.Id();
        var separateValues = await Run(fragments, new(Snapshot(unsaved), Snapshot("") with
        {
            Fields = [new(field, fragments[0], true, true, false), new(secondField, fragments[1], true, true, false)]
        }, ClickDelivered: true));
        check(separateValues.Result.Success && !AssertionMessage(separateValues.Result).Contains(multilineHint, StringComparison.Ordinal),
            "separate whole field values still satisfy separate declared expectations");
        var separateFragments = await Run(fragments, new(Snapshot(unsaved), Snapshot("") with
        {
            Fields = [new(field, fragments[0] + "\nother", true, true, false),
                new(secondField, fragments[1] + "\nother", true, true, false)]
        }, ClickDelivered: true));
        check(separateFragments.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
            !AssertionMessage(separateFragments.Result).Contains(multilineHint, StringComparison.Ordinal),
            "fragments distributed across different multiline fields do not trigger the one-field hint");

        var subscription = f.Events.Subscribe();
        var beforeRetryCount = f.Store.List<PluginControlRequest>("plugin-control").Count;
        var recovered = await f.Tool.ReconcileAsync(failed.Call, failed.Context, failed.Result.Evidence!, CancellationToken.None);
        var exactRetry = await f.Tool.ExecuteAsync(failed.Call, failed.Context, CancellationToken.None);
        var weaker = await f.Tool.ReconcileAsync(f.Call(presentation.Id, "invoke"), failed.Context, failed.Result.Evidence!, CancellationToken.None);
        var changed = await f.Tool.ReconcileAsync(f.Call(presentation.Id, "invoke", ["different expected value"]), failed.Context, failed.Result.Evidence!, CancellationToken.None);
        check(recovered.EffectiveOutcome == ToolExecutionOutcome.Failed && exactRetry.EffectiveOutcome == ToolExecutionOutcome.Failed &&
            recovered.Evidence!.RequestId == failed.Request.Id && exactRetry.Evidence!.RequestId == failed.Request.Id &&
            f.Store.List<PluginControlRequest>("plugin-control").Count == beforeRetryCount && !subscription.Reader.TryRead(out _),
            "exact recovery and idempotent retry read the retained failure without publishing or replaying the native mutation");
        check(weaker.EffectiveOutcome == ToolExecutionOutcome.Unknown && changed.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
            !subscription.Reader.TryRead(out _), "reconciliation cannot remove or change expectations on the retained effect");
        f.Events.Unsubscribe(subscription.Id);

        foreach (var variant in new[] { "before-limited", "after-limited", "field-limited" })
        {
            var limited = await Run([saved], Observation(saved, beforeLimited: variant == "before-limited",
                afterLimited: variant == "after-limited", fieldLimited: variant == "field-limited"));
            var body = Body(limited.Result);
            check(!limited.Result.Success && limited.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
                body.GetProperty("status").GetString() == "uncertain" && body.GetProperty("nativeStatus").GetString() == "completed" &&
                body.GetProperty("assertion").GetProperty("status").GetString() == "inconclusive",
                variant + " evidence cannot prove a visible invocation postcondition despite the delivered click");
        }
        foreach (var variant in new[] { "absent", "disabled", "hidden", "ambiguous" })
        {
            PluginControlButton[] buttons = variant switch
            {
                "absent" => [],
                "disabled" => [new("Increment", false, true)],
                "hidden" => [new("Increment", true, false)],
                _ => [new("Increment", true, true), new("Increment", false, true)]
            };
            var contradictory = await Run([saved], new(Snapshot(unsaved) with { Buttons = buttons }, Snapshot(saved), ClickDelivered: true));
            var body = Body(contradictory.Result);
            check(!contradictory.Result.Success && contradictory.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
                body.GetProperty("status").GetString() == "uncertain" && body.GetProperty("nativeStatus").GetString() == "completed" &&
                body.GetProperty("assertion").GetProperty("status").GetString() == "inconclusive" &&
                f.Controls.Read(contradictory.Context, contradictory.Request.Id).Status == "completed",
                variant + " named before-button evidence cannot prove an invocation assertion even when the after-value matches");
        }
        var hidden = await Run([saved], Observation(saved, fieldVisible: false));
        check(hidden.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
            Body(hidden.Result).GetProperty("assertion").GetProperty("status").GetString() == "failed",
            "a hidden field value cannot satisfy visible postconditions");
        var ordinal = await Run([saved], Observation(saved.Replace("café", "cafe", StringComparison.Ordinal)));
        check(ordinal.Result.EffectiveOutcome == ToolExecutionOutcome.Failed, "post-state text equality preserves accents and newlines without normalization");
        var unknown = await Run([saved], new(Snapshot(unsaved), null, ClickDelivered: true), "uncertain");
        check(unknown.Result.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
            Body(unknown.Result).GetProperty("assertion").GetProperty("status").GetString() == "inconclusive" &&
            f.Controls.Read(unknown.Context, unknown.Request.Id).Status == "uncertain",
            "a delivered effect with missing after-view remains Unknown rather than a guessed semantic failure or success");
        var unknownEvents = f.Events.Subscribe();
        var unknownRecovery = await f.Tool.ReconcileAsync(unknown.Call, unknown.Context, unknown.Result.Evidence!, CancellationToken.None);
        check(unknownRecovery.EffectiveOutcome == ToolExecutionOutcome.Unknown && !unknownEvents.Reader.TryRead(out _),
            "missing-after reconciliation reads the same uncertain mutation without redelivery");
        f.Events.Unsubscribe(unknownEvents.Id);
        var unasserted = await Run(null, Observation(unsaved));
        check(unasserted.Result.Success && !Body(unasserted.Result).TryGetProperty("assertion", out _),
            "legacy unasserted invocation remains minimum click evidence without a semantic claim");

        var countBeforeInvalid = f.Store.List<PluginControlRequest>("plugin-control").Count;
        foreach (var arguments in new object[]
        {
            new { presentationId = presentation.Id, operation = "invoke", mountId = f.MountId, expectedTexts = new[] { saved } },
            new { presentationId = presentation.Id, operation = "invoke", mountId = f.MountId, buttonName = "Increment", expectedTexts = Array.Empty<string>() },
            new { presentationId = presentation.Id, operation = "inspect", mountId = f.MountId, expectedTexts = new[] { saved } },
            new { presentationId = presentation.Id, operation = "invoke", mountId = f.MountId, buttonName = "Increment", expectedTexts = new[] { saved }, assertionPolicyVersion = 1 }
        })
        {
            var result = await f.Tool.ExecuteAsync(new(PluginControlToolExtension.ToolName, CerebralJson.Element(arguments)), f.Context(), CancellationToken.None);
            check(result.EffectiveOutcome == ToolExecutionOutcome.Rejected && f.Store.List<PluginControlRequest>("plugin-control").Count == countBeforeInvalid,
                "invalid asserted input is rejected before durable/native admission");
        }
        var unapproved = f.Present(f.Context(), testsAllowed: false);
        var denied = await f.Tool.ExecuteAsync(f.Call(unapproved.Id, "invoke", [saved]), f.Context(), CancellationToken.None);
        check(denied.EffectiveOutcome == ToolExecutionOutcome.Rejected && f.Store.List<PluginControlRequest>("plugin-control").Count == countBeforeInvalid,
            "an expectation never adds owner opt-in or installation authority");

        var assignment = WorkerWorkflows.BuildPluginTestAssignment(presentation, presentation.Plugin!, presentation.PluginTests!, false,
            new("unavailable", null, [], [], true, "Fixture visual assessment unavailable."));
        check(assignment.Contains("each operation requested in the original task", StringComparison.Ordinal) &&
            assignment.Contains("distinct unsaved value B", StringComparison.Ordinal) && assignment.Contains("exact saved value A", StringComparison.Ordinal) &&
            assignment.Contains("save/reopen result does not exercise a separate restore action", StringComparison.Ordinal),
            "generic QA guidance asks for each requested operation's observable outcome and distinguishes restore from reopen");
        check(assignment.Contains("invoke the requested save operation while A is current", StringComparison.Ordinal) &&
            assignment.Contains("confirm its actual result and errors before reopening", StringComparison.Ordinal) &&
            assignment.Contains("Unsaved B disappearing under an explicit-save contract is not a persistence defect", StringComparison.Ordinal),
            "persistence guidance requires actual saving before reopen and rejects unsaved-loss defect inference");
    }
}
