using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const int MaximumAdmissionRejections = 16, MaximumSameAdmissionRejections = 4;
    private sealed record RejectedRequestSignature(string Sha256, string EffectId, string? GeneratedContentProperty);
    private sealed record AdmissionRejection(string Name, string DiagnosticSha256, string RouteSha256,
        IReadOnlyList<RejectedRequestSignature> Requests, int Corrections = 0,
        IReadOnlyList<string>? ResourceSha256 = null);

    private static HarnessState PrepareAdmissionRecovery(Input input, HarnessState state, string runId)
    {
        // A new accepted run owns a new correction budget. Steering within the same run does not.
        if (state.AdmissionRecoveryRunId is { } priorRun && priorRun != runId)
            return state with { AdmissionRecoveryRunId = runId, AdmissionRejections = null,
                LastRejectedTool = null, RepeatedRejectionCorrections = 0, ConsecutiveSameRejections = 0 };
        ValidateAdmissionRecovery(state);
        state = state with { AdmissionRecoveryRunId = runId };
        // Upgrade an in-flight legacy rejection before an inspection can erase LastRejectedTool.
        if (state.AdmissionRejections is null && state.LastRejectedTool is { Diagnostic: { } diagnostic } rejected &&
            state.ToolReceipts.Any(receipt => receipt.EffectId == rejected.EffectId &&
                receipt.Name == rejected.Name && receipt.Outcome == nameof(ToolExecutionOutcome.Rejected)))
            state = state with { AdmissionRejections = [new(rejected.Name, AdmissionDiagnosticHash(diagnostic),
                AdmissionRouteHash(input, rejected.Name, rejected.Arguments, runId),
                [new(AdmissionArgumentHash(rejected.Arguments), rejected.EffectId, rejected.GeneratedContentProperty)],
                Math.Clamp(state.RepeatedRejectionCorrections, 0, MaximumRepairs),
                 AdmissionResourceHashes(input, rejected.Name, rejected.Arguments, runId))] };
        return state;
    }

    private static WorkflowStep? GuardAdmissionReplay(Input input, HarnessState state, string name, JsonElement arguments)
    {
        var route = AdmissionRouteHash(input, name, arguments, state.AdmissionRecoveryRunId);
        foreach (var rejection in state.AdmissionRejections ?? [])
        {
            if (rejection.Name != name || rejection.RouteSha256 != route) continue;
            var request = rejection.Requests.FirstOrDefault(request => request.Sha256 ==
                AdmissionArgumentHash(RejectedToolArguments(arguments, request.GeneratedContentProperty)));
            if (request is null) continue;
            var diagnostic = "This exact tool request was rejected in receipt " + request.EffectId +
                ". Correct the arguments or make a confirmed change to the rejected precondition. " +
                "Inspections and schema lookups do not reset this correction budget. No duplicate request was dispatched.";
            if (rejection.Corrections >= MaximumRepairs)
                return Finish(state, false, "Stopped after two corrections for the unchanged rejected precondition. " + diagnostic);
            var updated = rejection with { Corrections = rejection.Corrections + 1 };
            return Repair(input, state with { AdmissionRejections = state.AdmissionRejections!
                .Select(item => item == rejection ? updated : item).ToArray() }, diagnostic);
        }
        return null;
    }

    private static WorkflowStep ReceiveGeneratedModelWithAdmissionGuard(Input input, HarnessState state, WorkflowEffect effect)
    {
        // Validate the real raw-generation receipt and sink preconditions first. Suppression
        // drops only the proposed duplicate sink, never a previously dispatched effect.
        var proposed = ReceiveGeneratedModel(input, state, effect);
        if (proposed.Next is not InvokeWorkflowCommand { CommandId: Tool }) return proposed;
        var bound = proposed.State.Deserialize<HarnessState>(Json)!;
        var arguments = PendingToolArguments(bound)!.Value;
        return GuardAdmissionReplay(input, ClearGeneratedTool(bound), bound.PendingName!, arguments) ?? proposed;
    }

    private static WorkflowStep? AdoptAdmissionRecovery(Input input, HarnessState before, ref HarnessState next,
        WorkflowEffect effect, ToolData? data)
    {
        if (before.PendingName is not { } name || PendingToolArguments(before) is not { } arguments) return null;
        var entries = (next.AdmissionRejections ?? []).ToList();
        var route = AdmissionRouteHash(input, name, arguments, before.AdmissionRecoveryRunId);
        if (ConfirmedAdmissionPreconditionMutation(input, before, effect, data, arguments))
        {
            var changedRoute = name == "file.copy" && TryActionPath(input, arguments, "destinationPath", out var destination)
                ? AdmissionRouteHash(input, "file.write", Element(new { path = destination }), before.AdmissionRecoveryRunId) : route;
            entries.RemoveAll(item => item.RouteSha256 == changedRoute ||
                (name == "file.mkdir" || item.Name != "process.run") && item.ResourceSha256?.Contains(changedRoute) == true);
        }
        // A partial or unknown execution is not a proved pre-dispatch rejection.
        if (data?.Evidence is not { Outcome: ToolExecutionOutcome.Rejected, Partial: not true })
        {
            next = next with { AdmissionRejections = entries.Count == 0 ? null : entries.ToArray() };
            return null;
        }
        var generatedProperty = before.Phase == "generated-sink" ? before.PendingGeneration?.Request.ContentProperty : null;
        var request = new RejectedRequestSignature(
            AdmissionArgumentHash(RejectedToolArguments(arguments, generatedProperty)), effect.Id, generatedProperty);
        var diagnostic = AdmissionDiagnosticHash(data.Content);
        var index = entries.FindIndex(item => item.Name == name && item.RouteSha256 == route && item.DiagnosticSha256 == diagnostic);
        if (index < 0 && entries.Count >= MaximumAdmissionRejections)
            return Finish(next, false, "The bounded capacity of sixteen unresolved admission rejections was reached. " +
                "Its exact receipt is retained; no further attempt was dispatched.");
        var rejection = index < 0 ? new AdmissionRejection(name, diagnostic, route, [], ResourceSha256:
            AdmissionResourceHashes(input, name, arguments, before.AdmissionRecoveryRunId)) : entries[index];
        rejection = rejection with { Requests = [.. rejection.Requests, request] };
        if (index < 0) entries.Add(rejection); else entries[index] = rejection;
        next = next with { AdmissionRejections = entries.ToArray() };
        return rejection.Requests.Count >= MaximumSameAdmissionRejections
            ? Finish(next, false, "The tool rejected the same unchanged precondition four times across inspections. " +
                "Its exact receipts are retained; no further attempt was dispatched. Latest receipt: " + effect.Id)
            : null;
    }

    private static bool ConfirmedAdmissionPreconditionMutation(Input input, HarnessState state, WorkflowEffect effect,
        ToolData? data, JsonElement arguments)
    {
        if (!SuccessfulEffect(effect, data) || data!.Truncated || data.FollowUpRequired || data.Generation is not null) return false;
        if (state.PendingName is "file.write" or "file.edit" or "file.append")
            return ReadMutationDisposition(input, state.PendingName, arguments, data, true, effect.Id) == MutationDisposition.Actual;
        if (state.PendingName == "file.copy")
            return Encoding.UTF8.GetByteCount(data.Content) <= MaximumBindingPayloadBytes &&
                (data.Evidence?.RequestId is null || data.Evidence.RequestId == effect.Id) &&
                TryCopyObservation(input, state, effect, data, out _, out _);
        if (state.PendingName == "file.mkdir")
            return TryDirectoryEnsureObservation(input, state, effect, data, out var existed) && !existed;
        if (state.PendingName != "plugin.controls" || data.Evidence?.ProviderId != "cerebral.plugin.controls" ||
            Encoding.UTF8.GetByteCount(data.Content) > 32 * 1024 ||
            !TryText(arguments, "presentationId", 32, out var presentation) ||
            !TryText(arguments, "mountId", 129, out var mount) ||
            !TryText(arguments, "operation", 16, out var operation) || operation is not ("setText" or "invoke")) return false;
        try
        {
            if (presentation == PluginControlReferences.ReviewedReference &&
                !PluginControlReferences.TryResolveReviewedPresentationId(state.AdmissionRecoveryRunId!, out presentation)) return false;
            var edit = operation == "setText" ? ReadControlTextEdit(arguments, operation) : null;
            using var parsed = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 16 });
            var body = parsed.RootElement;
            UniqueProperties(body);
            return TryText(body, "receipt", 256, out var receipt) && receipt == data.Evidence.RequestId &&
                TryText(body, "presentationId", 32, out var returnedPresentation) && presentation == returnedPresentation &&
                TryText(body, "mountId", 129, out var returnedMount) && mount == returnedMount &&
                TryText(body, "operation", 16, out var returnedOperation) && operation == returnedOperation &&
                TryText(body, "status", 32, out var status) && status == "completed" &&
                TryText(body, "nativeStatus", 32, out var nativeStatus) && nativeStatus == "completed" &&
                (operation == "setText"
                    ? edit is not null && edit.ExpectedText != edit.Text && CompletedControlTextEdit(body, mount, edit)
                    : TryCompleteControlObservation(body, mount, out var observation) && observation.ClickDelivered && !observation.TextSet &&
                      TryText(arguments, "buttonName", 256, out var button) &&
                      observation.Before.Buttons.Count(item => item.Name == button && item.Enabled && item.Visible) == 1 &&
                      !JsonElement.DeepEquals(Element(observation.Before), Element(observation.After)));
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }

    private static IReadOnlyList<string>? AdmissionResourceHashes(Input input, string name, JsonElement arguments, string? runId)
    {
        var fields = name switch { "file.copy" => new[] { "sourcePath", "destinationPath" },
            "process.run" => new[] { "workingDirectory" }, _ => [] };
        if (fields.Length == 0) return null;
        var resources = fields.Select(field =>
            TryActionPath(input, arguments, field, out var path)
                ? AdmissionRouteHash(input, "file.write", Element(new { path }), runId) : null)
            .Where(hash => hash is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        return resources.Length == 0 ? null : resources;
    }

    private static string AdmissionRouteHash(Input input, string name, JsonElement arguments, string? runId)
    {
        var route = new SortedDictionary<string, string>(StringComparer.Ordinal);
        // Resource identity is separate from argument variations and operation choice.
        var family = name.StartsWith("file.", StringComparison.Ordinal) ? "file" : name;
        route["tool"] = family;
        foreach (var field in new[] { "path", "targetPath", "sourcePath", "destinationPath", "root", "workingDirectory", "executableId", "presentationId", "mountId" })
        {
            if (!arguments.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString()!;
            var key = field == "destinationPath" ? "path" : field;
            if (field is "path" or "targetPath" or "sourcePath" or "destinationPath" or "root" or "workingDirectory")
            {
                if (TryActionPath(input, arguments, field, out var path)) text = path;
                if (OperatingSystem.IsWindows()) text = text.Replace('/', '\\').ToUpperInvariant();
            }
            if (field == "presentationId" && text == PluginControlReferences.ReviewedReference &&
                PluginControlReferences.TryResolveReviewedPresentationId(runId ?? "", out var resolved)) text = resolved;
            route[key] = text;
        }
        return AdmissionArgumentHash(Element(route));
    }

    private static string AdmissionDiagnosticHash(string diagnostic)
    {
        // Structured rejection receipts vary their IDs. Their exact error is the stable reason;
        // neither raw result bodies nor generated source are retained in this private ledger.
        try
        {
            using var parsed = JsonDocument.Parse(diagnostic, new JsonDocumentOptions { MaxDepth = 32 });
            if (parsed.RootElement.ValueKind == JsonValueKind.String) diagnostic = parsed.RootElement.GetString()!;
            else if (parsed.RootElement.ValueKind == JsonValueKind.Object &&
                parsed.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                diagnostic = error.GetString()!;
        }
        catch (JsonException) { }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(diagnostic))).ToLowerInvariant();
    }

    private static string AdmissionArgumentHash(JsonElement arguments)
    {
        using var bytes = new MemoryStream();
        using var writer = new Utf8JsonWriter(bytes);
        Write(arguments);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();

        void Write(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(property.Value); }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item); writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
    }

    private static void ValidateAdmissionRecovery(HarnessState state)
    {
        if (state.AdmissionRecoveryRunId is { } run && !ValidEffectId(run) ||
            state.AdmissionRejections is { } entries &&
            (state.AdmissionRecoveryRunId is null || entries.Count > MaximumAdmissionRejections || entries.Any(item => item is null ||
                item.Name is not { Length: > 0 and <= 256 } || item.Name.Any(char.IsControl) ||
                !ValidPackageFingerprint(item.DiagnosticSha256) || !ValidPackageFingerprint(item.RouteSha256) ||
                item.Corrections is < 0 or > MaximumRepairs || item.Requests is null ||
                item.ResourceSha256 is { } resources && (item.Name is not ("file.copy" or "process.run") || resources.Count is < 1 or > 2 ||
                    item.Name == "process.run" && resources.Count != 1 ||
                    resources.Any(hash => !ValidPackageFingerprint(hash)) || resources.Distinct(StringComparer.Ordinal).Count() != resources.Count) ||
                item.Requests.Count is < 1 or > MaximumSameAdmissionRejections ||
                item.Requests.Any(request => request is null || !ValidPackageFingerprint(request.Sha256) ||
                    !ValidEffectId(request.EffectId) || request.GeneratedContentProperty is { } content &&
                    (content.Length is < 1 or > 100 || content.Any(character =>
                        !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))) ||
                    !state.ToolReceipts.Any(receipt => receipt.EffectId == request.EffectId &&
                        receipt.Name == item.Name && receipt.Outcome == nameof(ToolExecutionOutcome.Rejected)))) ||
                entries.Select(item => (item.Name, item.DiagnosticSha256, item.RouteSha256)).Distinct().Count() != entries.Count))
            throw new ArgumentException("The saved bounded admission-recovery ledger is invalid.");
    }
}
