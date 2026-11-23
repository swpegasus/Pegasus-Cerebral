using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Worker-to-user-session control bridge; never runs UI code in the service process.</summary>
public sealed class PluginControlRequests(StateStore store, EventHub events)
{
    private const string Kind = "plugin-control";
    private readonly InterfaceRequests presentations = new(store, events);

    public PluginControlRequest Create(ToolExecutionContext context, string presentationId,
        string operation, string mountId, string? buttonName, IReadOnlyList<string>? expectedTexts = null,
        PluginControlTextEdit? textEdit = null)
    {
        var savedExpectedTexts = PluginControlAssertions.CopyExpectedTexts(operation, expectedTexts);
        var savedTextEdit = PluginControlTextEdits.Copy(operation, textEdit?.FieldId, textEdit?.ExpectedText, textEdit?.Text);
        ValidateInput(presentationId, operation, mountId, buttonName, savedExpectedTexts, savedTextEdit);
        if (!Guid.TryParseExact(context.EffectId, "N", out var effect) || effect == Guid.Empty)
            throw new ArgumentException("Plugin control needs a durable host effect identity.");
        var created = false;
        var value = store.Locked(() =>
        {
            var presentation = OwnedPresentation(presentationId, context);
            if (store.Get<PluginControlRequest>(Kind, context.EffectId!) is { } existing)
            {
                PluginControlAssertions.ValidatePolicyVersion(existing.AssertionPolicyVersion);
                var existingExpectedTexts = PluginControlAssertions.CopyExpectedTexts(existing.Operation, existing.ExpectedTexts);
                var existingTextEdit = PluginControlTextEdits.Copy(existing.Operation, existing.TextEdit?.FieldId,
                    existing.TextEdit?.ExpectedText, existing.TextEdit?.Text);
                if (existing.ProfileId != context.Profile.Id || existing.SessionId != context.SessionId ||
                    existing.PresentationId != presentationId || existing.Operation != operation ||
                    existing.MountId != mountId || existing.ButtonName != buttonName ||
                    !PluginControlAssertions.SameExpectedTexts(existingExpectedTexts, savedExpectedTexts) ||
                    !SameTextEdit(existingTextEdit, savedTextEdit))
                    throw new InvalidOperationException("The saved control effect has different input. No action was replayed.");
                return existing;
            }
            if (!presentation.Plugin!.Mounts.Any(x => x.Slot == "sidebar" && x.Id == mountId))
                throw new UnauthorizedAccessException("That sidebar is not in the owner's installed plugin receipt.");
            foreach (var stale in store.List<PluginControlRequest>(Kind).Where(x => x.SessionId == context.SessionId &&
                x.Status is "pending" or "claimed" && x.ExpiresAt <= DateTimeOffset.UtcNow))
            {
                stale.Status = stale.Status == "pending" ? "rejected" : "uncertain";
                stale.Error = "The control deadline passed. A claimed effect must be inspected, never replayed.";
                store.Put(Kind, stale.Id, stale);
            }
            if (store.List<PluginControlRequest>(Kind).Count(x => x.SessionId == context.SessionId &&
                    x.Status is "pending" or "claimed") >= 8)
                throw new InvalidOperationException("This conversation already has eight unfinished plugin control requests.");
            var packageFingerprint = NativeDesktopPluginPackage.ComputeFingerprint(presentation.Plugin.ManifestPath);
            if (presentation.PackageFingerprint is not null && presentation.PackageFingerprint != packageFingerprint)
                throw new InvalidOperationException("The installed plugin bytes changed after owner review. Verify and present the current package for a new owner review before invoking controls; no control request was dispatched.");
            var result = new PluginControlRequest
            {
                Id = context.EffectId!, PresentationId = presentationId, SessionId = context.SessionId,
                ProfileId = context.Profile.Id, JobId = context.JobId, Operation = operation,
                MountId = mountId, ButtonName = buttonName, ExpectedTexts = savedExpectedTexts,
                TextEdit = savedTextEdit, AssertionPolicyVersion = PluginControlAssertions.CurrentPolicyVersion,
                Plugin = presentation.Plugin,
                PackageFingerprint = packageFingerprint
            };
            store.Put(Kind, result.Id, result); created = true; return result;
        });
        if (created) Publish(value);
        return value;
    }

    // Reading a retained effect never republishes a request, including on restart/reconciliation.
    public PluginControlRequest Read(ToolExecutionContext context, string id)
    {
        return store.Locked(() =>
        {
            var request = Get(id);
            if (request.SessionId != context.SessionId || request.ProfileId != context.Profile.Id)
                throw new UnauthorizedAccessException("No control receipt belongs to this profile and conversation.");
            _ = presentations.GetForContext(request.PresentationId, context,
                PluginControlToolExtension.ToolName, "desktop.plugins.manage");
            return request;
        });
    }

    public PluginControlClaim Claim(string id, string sessionId, string claimId)
    {
        RequireId(claimId);
        var changed = false;
        var result = store.Locked(() =>
        {
            var request = Get(id);
            if (request.SessionId != sessionId) throw new UnauthorizedAccessException("The selected conversation does not own this control request.");
            if (request.Status != "pending") return new PluginControlClaim(false, request);
            try
            {
                if (DateTimeOffset.UtcNow >= request.ExpiresAt)
                    throw new InvalidOperationException("The control request expired before delivery. No action was invoked.");
                var profile = store.Get<AgentProfile>("agent", request.ProfileId) ??
                    throw new UnauthorizedAccessException("The presenting profile was removed.");
                var presentation = OwnedPresentation(request.PresentationId,
                    new ToolExecutionContext(profile, request.SessionId, request.JobId));
                if (!SamePlugin(presentation.Plugin!, request.Plugin))
                    throw new InvalidOperationException("The installed presentation binding changed. No action was invoked.");
                RequireCurrentPackage(request);
                request.Status = "claimed"; request.ClaimId = claimId;
            }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or
                                          InvalidOperationException or IOException or InvalidDataException)
            { request.Status = "rejected"; request.Error = Bound(error.Message); }
            store.Put(Kind, id, request); changed = true; return new PluginControlClaim(request.Status == "claimed", request);
        });
        if (changed) Publish(result.Request);
        return result;
    }

    // Owner-only read preflight after layout has settled. It neither reserves delivery nor
    // changes a claim: cancellation can still race the later cross-process native invocation.
    public PluginControlRequest Ready(string id, string sessionId, string claimId,
        Action<InterfaceRequest, PluginControlRequest> validateRuns)
    {
        RequireId(claimId);
        ArgumentNullException.ThrowIfNull(validateRuns);
        return store.Locked(() =>
        {
            var request = Get(id);
            if (request.SessionId != sessionId || request.ClaimId != claimId)
                throw new UnauthorizedAccessException("The owner control claim does not match.");
            if (request.Status != "claimed")
                throw new InvalidOperationException("The control claim is no longer eligible for delivery. No action was invoked.");
            if (DateTimeOffset.UtcNow >= request.ExpiresAt)
                throw new InvalidOperationException("The control deadline passed before delivery. No action was invoked.");
            var profile = store.Get<AgentProfile>("agent", request.ProfileId) ??
                throw new UnauthorizedAccessException("The presenting profile was removed.");
            var presentation = OwnedPresentation(request.PresentationId,
                new ToolExecutionContext(profile, request.SessionId, request.JobId));
            if (!SamePlugin(presentation.Plugin!, request.Plugin))
                throw new InvalidOperationException("The installed presentation binding changed. No action was invoked.");
            RequireCurrentPackage(request);
            validateRuns(presentation, request);
            return request;
        });
    }

    public PluginControlRequest Complete(string id, string sessionId, string claimId,
        string status, PluginControlObservation? observation, string? error)
    {
        RequireId(claimId);
        if (status is not ("completed" or "rejected" or "uncertain"))
            throw new ArgumentException("Report completed, rejected, or uncertain.");
        if (error?.Length > 2000 || error?.Contains('\0') == true ||
            (status != "completed" && string.IsNullOrWhiteSpace(error)) ||
            (status == "completed" && !string.IsNullOrEmpty(error)))
            throw new ArgumentException("Report one bounded, consistent control outcome.");
        ValidateObservation(observation);
        var changed = false;
        var result = store.Locked(() =>
        {
            var request = Get(id);
            if (request.SessionId != sessionId || request.ClaimId != claimId)
                throw new UnauthorizedAccessException("The owner control claim does not match.");
            if (observation is not null && (observation.Before.Id != request.MountId ||
                observation.After is not null && observation.After.Id != request.MountId))
                throw new ArgumentException("The observation is for another mounted view.");
            if (observation?.ClickDelivered == true && request.Operation != "invoke")
                throw new ArgumentException("Only an invocation can deliver a click.");
            if (observation?.InstanceRecreated == true && request.Operation != "reopen")
                throw new ArgumentException("Only a reopen can report a recreated plugin instance.");
            if (observation?.TextSet == true && request.Operation != "setText")
                throw new ArgumentException("Only setText can report a delivered text update.");
            if (status == "completed" && (observation?.After is null ||
                request.Operation == "invoke" && !observation.ClickDelivered ||
                request.Operation == "reopen" && !observation.InstanceRecreated ||
                request.Operation == "setText" && !observation.TextSet))
                throw new ArgumentException("Completion needs the actual after-view and the requested native delivery evidence.");
            if (status == "completed" && request.Operation == "setText")
                ValidateCompletedTextEdit(request, observation!);
            if (request.Status is not ("claimed" or "uncertain"))
            {
                if (request.Status == status && request.Error == error && SameObservation(request.Observation, observation)) return request;
                throw new InvalidOperationException("This control request already has a different outcome or was never claimed.");
            }
            // A late owner observation may resolve an unknown claimed effect; it never invokes it again.
            request.Status = status; request.Error = error; request.Observation = observation;
            store.Put(Kind, id, request); changed = true; return request;
        });
        if (changed) Publish(result);
        return result;
    }

    public PluginControlRequest Interrupt(ToolExecutionContext context, string id, bool cancelled)
    {
        var changed = false;
        var request = store.Locked(() =>
        {
            var value = Read(context, id);
            if (value.Status == "pending")
            {
                value.Status = cancelled ? "cancelled" : "rejected";
                value.Error = cancelled ? "Cancelled before control delivery." : "The owner desktop did not claim the request before its deadline.";
            }
            else if (value.Status == "claimed")
            {
                value.Status = "uncertain";
                value.Error = "The owner desktop claimed this effect but its outcome is unconfirmed. Inspect this exact receipt; do not repeat a native effect that may have started.";
            }
            else return value;
            store.Put(Kind, id, value); changed = true; return value;
        });
        if (changed) Publish(request);
        return request;
    }

    public async Task<PluginControlRequest> WaitAsync(ToolExecutionContext context, string id, CancellationToken token)
    {
        // Poll durable state with a short bound. Notifications are not proof and may be dropped by EventHub.
        while (true)
        {
            var value = Read(context, id);
            if (value.Status is not ("pending" or "claimed")) return value;
            if (token.IsCancellationRequested) return Interrupt(context, id, true);
            if (DateTimeOffset.UtcNow >= value.ExpiresAt) return Interrupt(context, id, false);
            try { await Task.Delay(100, token); }
            catch (OperationCanceledException) { return Interrupt(context, id, true); }
        }
    }

    private InterfaceRequest OwnedPresentation(string id, ToolExecutionContext context)
    {
        var value = presentations.GetForContext(id, context, PluginControlToolExtension.ToolName, "desktop.plugins.manage");
        if (value.Kind != "plugin" || value.Status != "completed" || value.Plugin is not { ControlTestsAllowed: true, SchemaVersion: 1 })
            throw new UnauthorizedAccessException("Control tests require the owner's completed installed plugin receipt and explicit opt-in.");
        return value;
    }

    private PluginControlRequest Get(string id)
    {
        RequireId(id);
        var request = store.Get<PluginControlRequest>(Kind, id) ?? throw new ArgumentException("Plugin control receipt not found.");
        PluginControlAssertions.ValidatePolicyVersion(request.AssertionPolicyVersion);
        if (request.AssertionPolicyVersion == PluginControlAssertions.CurrentPolicyVersion)
            ValidateObservation(request.Observation);
        _ = PluginControlAssertions.CopyExpectedTexts(request.Operation, request.ExpectedTexts);
        _ = PluginControlTextEdits.Copy(request.Operation, request.TextEdit?.FieldId,
            request.TextEdit?.ExpectedText, request.TextEdit?.Text);
        return request;
    }

    internal static void ValidateInput(string presentationId, string operation, string mountId, string? buttonName,
        IReadOnlyList<string>? expectedTexts = null, PluginControlTextEdit? textEdit = null)
    {
        RequireId(presentationId);
        if (operation is not ("inspect" or "invoke" or "reopen" or "setText"))
            throw new ArgumentException("Use inspect, invoke, reopen, or setText.");
        if (string.IsNullOrWhiteSpace(mountId) || mountId.Length > 129 || mountId.Any(char.IsControl))
            throw new ArgumentException("Use the exact bounded sidebar mount ID.");
        if ((operation is "inspect" or "reopen" or "setText") && buttonName is not null ||
            operation == "invoke" && (string.IsNullOrWhiteSpace(buttonName) || buttonName.Length > 256 || buttonName.Any(char.IsControl)))
            throw new ArgumentException("Only invoke takes the exact visible buttonName.");
        _ = PluginControlAssertions.CopyExpectedTexts(operation, expectedTexts);
        _ = PluginControlTextEdits.Copy(operation, textEdit?.FieldId, textEdit?.ExpectedText, textEdit?.Text);
    }

    private static void RequireCurrentPackage(PluginControlRequest request)
    {
        if (request.PackageFingerprint is null)
        {
            if (request.Operation is "reopen" or "setText")
                throw new InvalidOperationException("This older control receipt has no immutable package fingerprint and cannot mutate a native plugin instance. Queue a new owner-approved control from the current presentation.");
            return;
        }
        var current = NativeDesktopPluginPackage.ComputeFingerprint(request.Plugin.ManifestPath);
        if (!string.Equals(current, request.PackageFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed plugin bytes changed after control admission. Verify and present the current package before using controls; no action was invoked.");
    }

    private static void RequireId(string? id)
    {
        if (!Guid.TryParseExact(id, "N", out var value) || value == Guid.Empty)
            throw new ArgumentException("Use the exact nonempty 32-character receipt identifier.");
    }
    private static string Bound(string text) => text[..Math.Min(text.Length, 2000)];
    private void Publish(PluginControlRequest request) => events.Publish("plugin.control.updated", request.JobId, request);
    private static bool SamePlugin(InterfacePluginReceipt a, InterfacePluginReceipt b) =>
        a.Id == b.Id && a.Version == b.Version && a.Generation == b.Generation &&
        a.ManifestPath == b.ManifestPath && a.ControlTestsAllowed == b.ControlTestsAllowed &&
        a.SchemaVersion == b.SchemaVersion && a.Mounts.SequenceEqual(b.Mounts);
    private static bool SameObservation(PluginControlObservation? a, PluginControlObservation? b) =>
        JsonSerializer.Serialize(a, CerebralJson.Options) == JsonSerializer.Serialize(b, CerebralJson.Options);
    private static bool SameTextEdit(PluginControlTextEdit? a, PluginControlTextEdit? b) =>
        a is null ? b is null : b is not null && a.FieldId == b.FieldId &&
            a.ExpectedText == b.ExpectedText && a.Text == b.Text;

    private static void ValidateCompletedTextEdit(PluginControlRequest request, PluginControlObservation observation)
    {
        var edit = request.TextEdit ?? throw new ArgumentException("The completed setText receipt has no text input.");
        var before = observation.Before.Fields.SingleOrDefault(field => field.Id == edit.FieldId);
        if (before is null || before.Limited || before.Text != edit.ExpectedText)
            throw new ArgumentException("Completed setText evidence must contain the exact unlimited pre-mutation field value.");
    }

    private static void ValidateObservation(PluginControlObservation? value)
    {
        if (value is null) return;
        if (value.Before is null) throw new ArgumentException("An observation needs the actual before-view.");
        foreach (var snapshot in new[] { value.Before, value.After }.Where(x => x is not null))
        {
            if (string.IsNullOrWhiteSpace(snapshot!.Id) || snapshot.Id.Length > 129 || snapshot.Id.Any(char.IsControl) || snapshot.Texts is null || snapshot.Buttons is null || snapshot.Fields is null || snapshot.Texts.Count > 20 || snapshot.Buttons.Count > 20 || snapshot.Fields.Count > 8 ||
                snapshot.Texts.Any(x => x is null || x.Length > 2048 || x.Contains('\0')) ||
                snapshot.Texts.Sum(x => x.Length) > 4096 ||
                snapshot.Buttons.Any(x => x is null || x.Name is null || x.Name.Length > 256 || x.Name.Any(char.IsControl)) ||
                snapshot.Fields.Any(x => x is null || !Guid.TryParseExact(x.Id, "N", out var identity) || identity == Guid.Empty ||
                    x.Text is null || x.Text.Length > PluginControlTextEdits.MaximumCharacters || x.Text.Contains('\0') ||
                    Encoding.UTF8.GetByteCount(x.Text) > PluginControlTextEdits.MaximumUtf8Bytes) ||
                snapshot.Fields.Sum(x => x.Text.Length) > PluginControlTextEdits.MaximumCharacters)
                throw new ArgumentException("Control observations exceed the bounded native view contract.");
        }
    }
}
