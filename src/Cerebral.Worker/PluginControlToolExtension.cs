using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Narrow agent access to owner-approved, installed sidebar controls; no installation or owner approvals.</summary>
public sealed class PluginControlToolExtension(StateStore store, EventHub events) : IToolExtension, IToolRecoveryExtension
{
    public const string ToolName = "plugin.controls";
    public const string Provider = "cerebral.plugin.controls";
    private readonly PluginControlRequests requests = new(store, events);
    private readonly InterfaceRequests presentations = new(store, events);
    private static readonly JsonSerializerOptions Compact = new(CerebralJson.Options)
        { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(ToolName,
            "Inspect, set one exact inspected editable native TextBox, invoke one named native button, or reopen one installed plugin sidebar as a genuinely new instance, only after the owner approved control testing. First read interface.status using plugin.present's requestId, then reuse that same value as presentationId with an exact reported mountId. Inspect returns opaque field IDs scoped to the actual live TextBox instances. setText requires one returned fieldId plus its exact current expectedText and preserves ordinary Unicode and newlines. The literal reviewed is available only inside the automatic owner-approved plugin QA run and resolves its already-bound presentation; it never selects a recent presentation. Invoke may declare expectedTexts before delivery to check exact whole visible TextBox or TextBlock values in the actual after-view; they need not be present before the click. Each array item is a separate whole field or label value, not a line or substring within one multiline value. Declare a multiline field as one complete string with its exact newlines. Compare failed assertions with the actual after-view before inferring a source defect; correct a mistaken expectation instead of rewriting source. A passed invocation assertion proves only its declared visible postconditions. Reopen is the persistence test: it disposes and reloads only that reviewed plugin, rejects loaded dependents, and reports whether a new instance and view were created. For an editable-data persistence test, inspect the actual field, set a known changed nonempty value, perform the requested save action while it is current, confirm the actual save result/errors, then include that exact currently visible saved field value in expectedTexts before reopen. Unsaved edits may disappear under an explicit-save contract; that alone is not a persistence defect. New asserted reopens of editable views require a declared nonempty value in a real editable field before and after the new instance; titles, button labels, default-to-default checks and static status labels cannot establish editable-data persistence. A reopen without expectedTexts remains lifecycle-only. Read-only views retain ordinary visible-text assertions. A plugin generation is never a presentation ID. Each returned receipt identifies only this control effect for reconciliation; never use it as presentationId. Returns actual before/after state and visible errors; delivered text or a click is not proof of correct behavior or persistence. Unknown means inspect the retained effect and never repeat a native mutation. Cannot install, approve, use other UI, or change conversations.",
            ControlInputSchema(), "desktop.plugins.manage") { PrerequisiteTools = [InterfaceToolExtension.StatusToolName] }
    ];

    private static JsonElement ControlInputSchema()
    {
        object Variant(string operation)
        {
            var properties = new Dictionary<string, object>
            {
                ["presentationId"] = new
                {
                    oneOf = new object[]
                    {
                        new { type = "string", minLength = 32, maxLength = 32 },
                        new { type = "string", @enum = new[] { InterfaceRequests.ReviewedReference } }
                    },
                    description = "Exact requestId returned by plugin.present and echoed by interface.status, or reviewed only in its bound automatic plugin QA run."
                },
                ["operation"] = new { type = "string", @enum = new[] { operation } },
                ["mountId"] = new { type = "string", minLength = 1, maxLength = 129,
                    description = "Exact mountId reported by interface.status for this installed presentation." }
            };
            var required = new List<string> { "presentationId", "operation", "mountId" };
            if (operation == "invoke")
            {
                properties["buttonName"] = new { type = "string", minLength = 1, maxLength = 256 };
                required.Add("buttonName");
            }
            if (operation is "invoke" or "reopen")
                properties["expectedTexts"] = new
                {
                    type = "array", minItems = 1, maxItems = 8,
                    items = new { type = "string", minLength = 1, maxLength = 256 },
                    description = operation == "invoke"
                        ? "Optional exact whole visible TextBox or TextBlock after-values declared before the enabled button invocation. Each array item is a separate whole field or label value; use one complete string with exact newlines for a multiline field, not separate lines or substrings. They need not be present before the click; a match proves only these visible postconditions, not persistence or all requested behavior."
                        : "Optional exact currently visible text required after the new instance opens. For an editable view include an actual nonempty editable field value; static labels alone are inconclusive for editable-data persistence."
                };
            if (operation == "setText")
            {
                properties["fieldId"] = new { type = "string", minLength = 32, maxLength = 32,
                    description = "Opaque exact fieldId returned by the latest native inspect for this live mounted TextBox." };
                properties["expectedText"] = new { type = "string", maxLength = PluginControlTextEdits.MaximumCharacters,
                    description = "Exact currently inspected field text. A mismatch rejects stale input before any mutation." };
                properties["text"] = new { type = "string", maxLength = PluginControlTextEdits.MaximumCharacters,
                    description = "Replacement native TextBox text. Ordinary Unicode and newlines are preserved exactly." };
                required.AddRange(["fieldId", "expectedText", "text"]);
            }
            return new { type = "object", properties, required, additionalProperties = false };
        }
        // Separate operation shapes prevent generation of assertion/button fields that the
        // native operation cannot accept. Runtime admission still validates every argument.
        return CerebralJson.Element(new { oneOf = new[] { Variant("inspect"), Variant("setText"), Variant("invoke"), Variant("reopen") } });
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string? receipt = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = Input(call, context);
            var request = requests.Create(context, input.PresentationId, input.Operation, input.MountId,
                input.ButtonName, input.ExpectedTexts, input.TextEdit);
            receipt = request.Id;
            return Project(await requests.WaitAsync(context, request.Id, cancellationToken));
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or InvalidOperationException or IOException or InvalidDataException)
        {
            return receipt is null ? Rejected(error.Message) : new ToolResult(false, CoordinateGuidance(error.Message) + " Inspect the retained control effect; do not repeat a native effect that may have started.")
                { Evidence = new(ToolExecutionOutcome.Unknown, Provider, RequestId: receipt, State: "unconfirmed") };
        }
    }

    public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
        ToolExecutionEvidence previousEvidence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var input = Input(call, context);
            if (previousEvidence.ProviderId != Provider || previousEvidence.RequestId != context.EffectId)
                throw new ArgumentException("The saved receipt does not belong to this plugin control effect.");
            var request = requests.Read(context, context.EffectId!);
            if (request.PresentationId != input.PresentationId || request.Operation != input.Operation ||
                request.MountId != input.MountId || request.ButtonName != input.ButtonName ||
                !PluginControlAssertions.SameExpectedTexts(request.ExpectedTexts, input.ExpectedTexts) ||
                !SameTextEdit(request.TextEdit, input.TextEdit))
                throw new ArgumentException("The saved control effect has different input.");
            if (request.Status is "pending" or "claimed")
                request = requests.Interrupt(context, request.Id, false);
            return Task.FromResult(Project(request));
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(new ToolResult(false, CoordinateGuidance(error.Message))
                { Evidence = previousEvidence with { Outcome = ToolExecutionOutcome.Unknown } });
        }
    }

    private (string PresentationId, string Operation, string MountId, string? ButtonName,
        string[]? ExpectedTexts, PluginControlTextEdit? TextEdit) Input(
        ToolCall call, ToolExecutionContext context)
    {
        if (call.Name != ToolName || call.Arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Plugin controls accepts only presentationId, operation, mountId, and operation-specific native control fields.");
        var properties = call.Arguments.EnumerateObject().ToArray();
        if (properties.Any(x => x.Name is not ("presentationId" or "operation" or "mountId" or "buttonName" or "expectedTexts" or "fieldId" or "expectedText" or "text")) ||
            properties.GroupBy(x => x.Name, StringComparer.Ordinal).Any(group => group.Skip(1).Any()))
            throw new ArgumentException("Plugin controls accepts each admitted input property exactly once.");
        string Text(string name) => call.Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ArgumentException("Missing string " + name + ".");
        var button = call.Arguments.TryGetProperty("buttonName", out _) ? Text("buttonName") : null;
        var operation = Text("operation");
        IReadOnlyList<string>? expectedTexts = null;
        if (call.Arguments.TryGetProperty("expectedTexts", out var expected))
        {
            if (expected.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("expectedTexts must be an array of exact visible strings.");
            expectedTexts = expected.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new ArgumentException("Each expectedTexts item must be a string.")).ToArray();
        }
        var savedExpectedTexts = PluginControlAssertions.CopyExpectedTexts(operation, expectedTexts);
        string? OptionalText(string name) => call.Arguments.TryGetProperty(name, out _) ? Text(name) : null;
        var textEdit = PluginControlTextEdits.Copy(operation, OptionalText("fieldId"),
            OptionalText("expectedText"), OptionalText("text"));
        var presentationId = presentations.ResolvePresentationReference(Text("presentationId"), context,
            ToolName, "desktop.plugins.manage");
        var input = (PresentationId: presentationId, Operation: operation, MountId: Text("mountId"),
            ButtonName: button, ExpectedTexts: savedExpectedTexts, TextEdit: textEdit);
        PluginControlRequests.ValidateInput(input.PresentationId, input.Operation, input.MountId, input.ButtonName,
            input.ExpectedTexts, input.TextEdit);
        return input;
    }

    private static string CoordinateGuidance(string error) => error +
        " presentationId must be plugin.present's exact requestId, also echoed by interface.status, or reviewed only in its bound automatic plugin QA run. A plugin generation and a plugin.controls receipt are never presentation IDs.";
    private static ToolResult Rejected(string error) => new(false, CoordinateGuidance(error))
        { Evidence = new(ToolExecutionOutcome.Rejected, Provider) };
    private static bool SameTextEdit(PluginControlTextEdit? left, PluginControlTextEdit? right) =>
        left is null ? right is null : right is not null && left.FieldId == right.FieldId &&
            left.ExpectedText == right.ExpectedText && left.Text == right.Text;
    private ToolResult Project(PluginControlRequest request)
    {
        var assertion = EvaluateAssertion(request);
        var projectedStatus = request.Status == "completed" ? assertion?.Status switch
        {
            "failed" => "failed",
            "inconclusive" => "uncertain",
            _ => "completed"
        } : request.Status;
        var outcome = request.Status == "completed" ? assertion?.Status switch
        {
            "failed" => ToolExecutionOutcome.Failed,
            "inconclusive" => ToolExecutionOutcome.Failed,
            _ => ToolExecutionOutcome.Succeeded
        } : request.Status is "claimed" or "uncertain" ? ToolExecutionOutcome.Unknown :
            request.Status == "cancelled" ? ToolExecutionOutcome.Cancelled : ToolExecutionOutcome.Rejected;
        return new(outcome == ToolExecutionOutcome.Succeeded,
            JsonSerializer.Serialize(new
            {
                receipt = request.Id, presentationId = request.PresentationId, mountId = request.MountId,
                sourceManifest = store.Get<InterfaceRequest>("interface", request.PresentationId)?.Path,
                installedGeneration = request.Plugin.Generation,
                installedManifest = request.Plugin.ManifestPath,
                packageFingerprint = request.PackageFingerprint,
                status = projectedStatus, nativeStatus = request.Status, request.Operation,
                assertion, observation = request.Observation,
                error = request.Error ?? (assertion?.Status is "failed" or "inconclusive" ? assertion.Message : null)
            }, Compact))
        {
            Evidence = new(outcome, Provider, RequestId: request.Id, State: projectedStatus)
        };
    }

    private static AssertionProjection? EvaluateAssertion(PluginControlRequest request)
    {
        PluginControlAssertions.ValidatePolicyVersion(request.AssertionPolicyVersion);
        if (request.Operation == "setText")
        {
            var edit = request.TextEdit ?? throw new InvalidOperationException("The retained setText request has no text input.");
            var expected = new[] { edit.Text };
            if (request.Status != "completed" || request.Observation?.After is null)
                return new("inconclusive", expected, [],
                    "The native text delivery did not complete with an after-view, so the exact value assertion is inconclusive.");
            var after = request.Observation.After;
            var field = after.Fields.SingleOrDefault(item => item.Id == edit.FieldId);
            if (field is null)
                return after.Limited
                    ? new("inconclusive", expected, [],
                        "The after-view was limited and did not include the edited field, so its exact value is inconclusive.")
                    : new("failed", expected, expected,
                        "The native text delivery completed, but the edited field was absent from the complete after-view.");
            if (field.Limited)
                return new("inconclusive", expected, [],
                    "The edited field's after-value was limited, so its exact value is inconclusive.");
            return field.Text == edit.Text
                ? new("passed", expected, [], "The native text delivery completed and the field retained the exact requested value.")
                : new("failed", expected, expected,
                    "The native text delivery completed, but the field did not retain the exact requested value; no automatic replay was attempted.");
        }
        var expectedTexts = PluginControlAssertions.CopyExpectedTexts(request.Operation, request.ExpectedTexts);
        if (expectedTexts is null) return null;
        static IEnumerable<string> ExactVisibleTexts(PluginControlSnapshot snapshot) =>
            snapshot.Texts.Concat(snapshot.Fields.Where(field => field.Visible && !field.Limited).Select(field => field.Text));
        var currentPolicy = request.AssertionPolicyVersion == PluginControlAssertions.CurrentPolicyVersion;
        if (request.Operation == "invoke")
        {
            AssertionProjection Postcondition(string status, string[] missing, string message) => new(status, expectedTexts, missing, message)
                { PolicyVersion = currentPolicy ? request.AssertionPolicyVersion : null };
            if (request.Status != "completed" || request.Observation is not { After: not null, ClickDelivered: true,
                    TextSet: false, InstanceRecreated: false } observed)
                return Postcondition("inconclusive", [],
                    "The native invocation did not complete with a delivered click and both views, so its visible postconditions are inconclusive.");
            if (observed.Before.Limited || observed.After.Limited ||
                observed.Before.Fields.Any(field => field.Visible && field.Limited) ||
                observed.After.Fields.Any(field => field.Visible && field.Limited))
                return Postcondition("inconclusive", [],
                    "The native before or after view was limited, so the exact invocation postconditions are inconclusive.");
            var buttons = observed.Before.Buttons.Where(button => button.Name == request.ButtonName).ToArray();
            if (buttons.Length != 1 || !buttons[0].Visible || !buttons[0].Enabled)
                return Postcondition("inconclusive", [],
                    "The before-view did not establish exactly one visible enabled button with the requested name, so the invocation postconditions are inconclusive.");
            // Invocation can deliberately replace an unsaved value. Only its actual after-view
            // must contain the declared values; reopen retains separate before/after preservation rules.
            var missing = expectedTexts.Where(expected =>
                !ExactVisibleTexts(observed.After).Contains(expected, StringComparer.Ordinal)).ToArray();
            return missing.Length == 0
                ? Postcondition("passed", [], "All declared exact text values were visible after the native invocation. This proves only the declared visible postconditions.")
                : Postcondition("failed", missing, "The completed native invocation is missing expected text in its after-view; no automatic replay was attempted." +
                    (MissingExpectationsAreFieldLines(observed.After, missing) ? MultilineExpectationGuidance : ""));
        }
        EditableFieldPreservation? preservation = currentPolicy ? new("unverified", []) : null;
        AssertionProjection Result(string status, string[] missing, string message) => new(status, expectedTexts, missing, message)
        {
            PolicyVersion = currentPolicy ? request.AssertionPolicyVersion : null,
            EditableFieldPreservation = preservation
        };
        if (request.Status != "completed" || request.Observation?.After is null)
            return Result("inconclusive", [],
                "The native reopen did not complete with both before and after views, so the text assertion is inconclusive.");
        if (currentPolicy && !request.Observation.InstanceRecreated)
            return Result("inconclusive", [], "The native observation did not establish a recreated instance, so editable-data preservation is unverified.");
        if (request.Observation.Before.Limited || request.Observation.After.Limited)
            return Result("inconclusive", [],
                "The native before or after view was limited, so the exact text assertion is inconclusive.");
        var missingBefore = expectedTexts.Where(expected =>
            !ExactVisibleTexts(request.Observation.Before).Contains(expected, StringComparer.Ordinal)).ToArray();
        if (missingBefore.Length != 0)
            return Result("inconclusive", missingBefore,
                "Expected text was not present before reopen, so it cannot establish preserved state.");

        var beforeEditable = request.Observation.Before.Fields.Where(field => field.Visible && !field.ReadOnly).ToArray();
        if (currentPolicy)
        {
            if (beforeEditable.Length == 0) preservation = new("not-applicable", []);
            else
            {
                if (beforeEditable.Any(field => field.Limited) ||
                    request.Observation.After.Fields.Any(field => field.Visible && !field.ReadOnly && field.Limited))
                    return Result("inconclusive", [],
                        "An editable field observation was limited, so editable-data preservation is unverified. Inspect an exact current value before choosing a bounded test value.");
                var declaredValues = beforeEditable.Where(field => field.Enabled && field.Text.Length != 0 &&
                    expectedTexts.Contains(field.Text, StringComparer.Ordinal))
                    .GroupBy(field => field.Text, StringComparer.Ordinal).Select(group => group.First()).ToArray();
                if (declaredValues.Length == 0)
                    return Result("inconclusive", [],
                        "The expected texts select no actual nonempty enabled editable before-field value. Static labels do not establish editable-data persistence. Inspect the current field, set a known changed nonempty value, perform the requested save action, and use that exact current value in expectedTexts before reopen.");
                var beforeFieldIds = request.Observation.Before.Fields.Select(field => field.Id).ToHashSet(StringComparer.Ordinal);
                var afterEditable = request.Observation.After.Fields.Where(field =>
                    field.Visible && field.Enabled && !field.ReadOnly && !field.Limited &&
                    !beforeFieldIds.Contains(field.Id)).ToArray();
                // Opaque field identities belong to each live view. Compare exact values across
                // actual before/after fields; never reuse the old field ID in the new instance.
                var witnesses = declaredValues.Select(before =>
                {
                    var after = afterEditable.FirstOrDefault(field => string.Equals(field.Text, before.Text, StringComparison.Ordinal));
                    return after is null ? null : new EditableValueWitness(before.Id, after.Id,
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(before.Text))).ToLowerInvariant(),
                        Encoding.UTF8.GetByteCount(before.Text));
                }).Where(item => item is not null).Select(item => item!).ToArray();
                preservation = new(witnesses.Length == 0 ? "unverified" : "observed", witnesses);
            }
        }
        var missingAfter = expectedTexts.Where(expected =>
            !ExactVisibleTexts(request.Observation.After).Contains(expected, StringComparer.Ordinal)).ToArray();
        if (missingAfter.Length != 0)
            return Result("failed", missingAfter,
                "The new plugin instance is missing expected text that was present before reopen.");
        if (currentPolicy && beforeEditable.Length != 0 && preservation!.Witnesses.Count == 0)
            return Result("inconclusive", [],
                "Declared text appeared in the view, but no declared nonempty value was preserved in a visible enabled editable after-field. A static label, read-only field, disabled field or hidden field cannot substitute for editable-data preservation.");
        return Result("passed", [], !currentPolicy
            ? "All expected text was present before and after the new plugin instance opened."
            : beforeEditable.Length == 0
                ? "All expected text was present before and after the new plugin instance opened. This is visible-text evidence; editable-data persistence was not established."
                : "All expected text was present before and after the new plugin instance opened. The witness records exact nonempty values preserved in actual editable fields of the recreated instance.");
    }

    private const string MultilineExpectationGuidance =
        " The complete after-view contains every missing expectation as a separate exact line in one visible field. " +
        "expectedTexts compares whole visible field or label values, not individual lines or substrings. " +
        "Declare the full multiline field value as one expectation, preserving its exact newline characters; " +
        "the actual value is retained in observation.after.fields. This assertion remains failed. " +
        "These observations alone do not establish a runtime or source defect; correct the test setup before considering source repair.";

    private static bool MissingExpectationsAreFieldLines(PluginControlSnapshot after, IReadOnlyList<string> missing)
    {
        // This is diagnostic classification only. Admission already bounds eight fields and
        // their total text at 4,096 characters; exact whole-value matching above never changes.
        if (missing.Count == 0 || missing.Any(value => value.Contains('\r') || value.Contains('\n'))) return false;
        foreach (var field in after.Fields.Where(field => field.Visible && !field.Limited &&
                     (field.Text.Contains('\r') || field.Text.Contains('\n'))))
        {
            var lines = field.Text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).ToHashSet(StringComparer.Ordinal);
            if (missing.All(lines.Contains)) return true;
        }
        return false;
    }

    private sealed record EditableValueWitness(string BeforeFieldId, string AfterFieldId, string Sha256, int Utf8Bytes);
    private sealed record EditableFieldPreservation(string Status, IReadOnlyList<EditableValueWitness> Witnesses);
    private sealed record AssertionProjection(string Status, string[] ExpectedTexts, string[] MissingTexts,
        string Message)
    {
        public int? PolicyVersion { get; init; }
        public EditableFieldPreservation? EditableFieldPreservation { get; init; }
    }
}
