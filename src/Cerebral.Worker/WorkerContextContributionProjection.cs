using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Context;
using Cerebral.Engine.Persistence;

namespace Cerebral.Worker;

/// <summary>
/// Model-only copies of original admitted context evidence. Canonical events, receipts and owner
/// reads remain unchanged. Package payloads, current selection and the current registry are not read.
/// </summary>
internal static class WorkerContextContributionProjection
{
    internal const string Refusal = "The saved context contribution cannot be projected safely.";
    private const int MaximumIntentPages = 32;
    private const int MaximumProofEvents = MaximumIntentPages * EventJournal.MaximumPageEvents;
    private const int MaximumNodes = 65_536;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        WriteIndented = false, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static CerebralEvent ProjectEvent(EventJournal journal, CerebralEvent item,
        CancellationToken token,
        Func<WorkflowRun, IReadOnlyList<WorkerContextContributionAdmission>>? historicalAdmissions = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(item);
        token.ThrowIfCancellationRequested();
        try { return new Projection(journal, new WorkflowStore(journal), token, historicalAdmissions).Event(item); }
        catch (Exception error) when (InvalidProjection(error)) { throw new InvalidDataException(Refusal); }
    }

    internal static WorkflowCommandResult ProjectReceipt(WorkflowStore receipts, string runId, string effectId,
        WorkflowCommandResult original, EventJournal journal, CancellationToken cancellationToken = default,
        Func<WorkflowRun, IReadOnlyList<WorkerContextContributionAdmission>>? historicalAdmissions = null)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var projection = new Projection(journal, receipts, cancellationToken, historicalAdmissions);
            var run = receipts.Get(runId);
            return projection.Receipt(run, effectId, original, journal.GetRevision(run.Submission.ConversationId));
        }
        catch (Exception error) when (InvalidProjection(error)) { throw new InvalidDataException(Refusal); }
    }

    private sealed record Proof(WorkflowRun Run,
        IReadOnlyDictionary<string, IReadOnlyList<WorkerContextContributionAdmission>> Ready,
        IReadOnlySet<string> ReferenceOnly);
    private sealed record Intent(Proof Proof, CerebralEvent Event, ToolCallData Call,
        WorkerContextContributionAdmission? Admission, ContextProviderRequest? Request,
        WorkerToolContextProjectionEvidence.Origin? Projection = null);
    private sealed record SourceMatch(WorkerContextContributionAdmission Admission,
        ContextContribution Contribution, string SourceId);

    private sealed class Projection(EventJournal journal, WorkflowStore receipts, CancellationToken token,
        Func<WorkflowRun, IReadOnlyList<WorkerContextContributionAdmission>>? historicalAdmissions)
    {
        private readonly Dictionary<string, Proof> proofs = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Conversation, string Effect, long Upper), Intent> intents = [];
        private readonly HashSet<(string Run, string Effect)> readingReceipts = [];
        private readonly HashSet<(string Conversation, string Event)> readingEvents = [];
        private int nodes;

        internal CerebralEvent Event(CerebralEvent item)
        {
            token.ThrowIfCancellationRequested();
            if (item.Type is not (CerebralEventTypes.ToolCall or CerebralEventTypes.ToolResult)) return item;
            var run = receipts.TryGet(item.TurnId);
            var job = item.Provenance.JobId is { } jobId && jobId != item.TurnId ? receipts.TryGet(jobId) : null;
            if (item.Type == CerebralEventTypes.ToolCall && (run ?? job) is { } callOwner)
                RequireCanonicalProtectedCall(item, callOwner);
            if (job is not null)
            {
                var jobProof = Accepted(job);
                var jobOperation = Operation(item, jobProof);
                if (Internal(jobOperation) || jobOperation is not null && jobProof.ReferenceOnly.Contains(jobOperation))
                    RequireEventOwner(item, job, jobOperation!);
            }
            if (run is null)
            {
                // Ordinary legacy tool events need not have an engine run. A recognizable host
                // effect without its owner is corrupt rather than permission to expose its body.
                if (Internal(LooseOperation(item)) || Internal(item.Actor.Id)) throw Invalid();
                return item;
            }
            var proof = Accepted(run);
            var operation = Operation(item, proof);
            if (!Internal(operation) && (operation is null || !proof.ReferenceOnly.Contains(operation))) return item;
            RequireEventOwner(item, run, operation!);
            var effectId = Details(item).CallId;
            var intent = FindIntent(proof, effectId, item.Sequence);
            if (intent.Proof.Run.Id != run.Id || intent.Call.ToolName != operation) throw Invalid();
            if (intent.Admission is { } admission)
            {
                var result = receipts.GetEffectResult(run.Id, effectId);
                if (item.Type == CerebralEventTypes.ToolResult)
                {
                    var historical = Result(item);
                    RequireSameOutcome(historical, result);
                    if (intent.Projection is not null || historical.State == WorkflowEffectState.Succeeded)
                        _ = Sources(intent, historical);
                    result = historical; // Reconciliation must not rewrite a historical Unknown.
                }
                else if (intent.Projection is not null || result.State == WorkflowEffectState.Succeeded)
                    _ = Sources(intent, result);
                return item with
                {
                    Content = [new StructuredContentPart("context.contribution.reference", Reference(admission, intent.Request!, effectId, result))],
                    Data = Element(new
                    {
                        callId = effectId, toolName = operation, state = result.State,
                        success = result.State == WorkflowEffectState.Succeeded,
                        providerReceiptId = result.ProviderReceiptId,
                        diagnosticsRetained = result.Error is not null, bodyOmitted = true
                    })
                };
            }
            if (!Internal(operation)) return item; // An exact Preserve sibling remains an ordinary provider event.
            var changed = false;
            var content = item.Content.Select(part => part is StructuredContentPart structured
                ? structured with { Value = Project(structured.Value, proof, item.Sequence, ref changed) }
                : part).ToArray();
            var data = item.Data is { } value ? Project(value, proof, item.Sequence, ref changed) : (JsonElement?)null;
            return changed ? item with { Content = content, Data = data } : item;
        }

        private void RequireCanonicalProtectedCall(CerebralEvent candidate, WorkflowRun owner)
        {
            // ToolCall actors name the profile, not the command. Authenticate the indexed original
            // before an altered or absent toolName can make a protected envelope appear ordinary.
            var canonical = journal.ReadExact(owner.Submission.ConversationId, candidate.Id, candidate.Sequence, token);
            var proof = Accepted(owner);
            var operation = Operation(canonical, proof);
            if (!Internal(operation) && (operation is null || !proof.ReferenceOnly.Contains(operation))) return;
            RequireEventOwner(canonical, owner, operation!);
            if (!JsonElement.DeepEquals(Element(candidate), Element(canonical))) throw Invalid();
        }

        internal WorkflowCommandResult Receipt(WorkflowRun run, string effectId,
            WorkflowCommandResult original, long upper)
        {
            token.ThrowIfCancellationRequested();
            var proof = Accepted(run);
            var intent = FindIntent(proof, effectId, upper);
            if (intent.Proof.Run.Id != run.Id) throw Invalid();
            if (intent.Admission is { } admission)
            {
                var saved = receipts.GetEffectResult(run.Id, effectId);
                RequireSameOutcome(original, saved);
                if (intent.Projection is not null || original.State == WorkflowEffectState.Succeeded)
                    _ = Sources(intent, original);
                return original with
                {
                    Data = Reference(admission, intent.Request!, effectId, original),
                    Error = original.Error is null ? null :
                        "The stored context outcome has diagnostics; its original body is omitted."
                };
            }
            if (!Internal(intent.Call.ToolName)) return original;
            if (!readingReceipts.Add((run.Id, effectId))) throw Invalid();
            try
            {
                var changed = false;
                var data = original.Data is { } value ? Project(value, proof, upper, ref changed) : (JsonElement?)null;
                return changed ? original with { Data = data } : original;
            }
            finally { readingReceipts.Remove((run.Id, effectId)); }
        }

        private Proof Accepted(WorkflowRun run)
        {
            token.ThrowIfCancellationRequested();
            if (proofs.TryGetValue(run.Id, out var cached)) return cached;
            var accepted = run.Submission;
            if (accepted.RequestId != run.Id || string.IsNullOrWhiteSpace(accepted.ProfileId) ||
                string.IsNullOrWhiteSpace(accepted.ConversationId) || accepted.ConversationSequence < 0 ||
                accepted.SettingsSnapshot.ValueKind != JsonValueKind.Object) throw Invalid();
            var admissions = historicalAdmissions is null ? DefaultAdmissions(accepted.SettingsSnapshot) :
                WorkerContextContributionSnapshot.ValidateCollection(historicalAdmissions(run) ?? throw Invalid());
            token.ThrowIfCancellationRequested();
            var ready = admissions.Where(admission => admission.Selection.Disposition == WorkflowContextDisposition.Ready).ToArray();
            if (ready.Length != 0)
            {
                // These are host-owned outer coordinates. Opaque package payloads are never decoded.
                var settings = accepted.SettingsSnapshot;
                if (!settings.TryGetProperty("profile", out var profile) ||
                    profile.ValueKind != JsonValueKind.Object || Text(profile, "id") != accepted.ProfileId ||
                    !settings.TryGetProperty("packageBindings", out var rawBindings) ||
                    rawBindings.ValueKind != JsonValueKind.Array || rawBindings.GetArrayLength() > 128) throw Invalid();
                var bindings = rawBindings.Deserialize<ServicePluginPackageBinding[]>(Json) ?? throw Invalid();
                if (bindings.Any(binding => binding is null) ||
                    bindings.Select(binding => binding.Id).Distinct(StringComparer.Ordinal).Count() != bindings.Length)
                    throw Invalid();
                foreach (var admission in ready)
                {
                    var binding = bindings.SingleOrDefault(candidate => candidate.Id == admission.PackageId);
                    if (binding is not { Loaded: true, Error: null } || binding.Dependencies is null ||
                        binding.Version != admission.PackageVersion || binding.Generation != admission.Generation)
                        throw Invalid();
                }
            }
            var providers = ready.GroupBy(admission => admission.Descriptor.Provider.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyList<WorkerContextContributionAdmission>)Array.AsReadOnly(group.ToArray()), StringComparer.Ordinal);
            var referenceOnly = ready.Where(admission =>
                admission.Descriptor.HistoryProjection == WorkflowContextHistoryProjection.ReferenceOnly)
                .Select(admission => admission.Descriptor.Provider.Id).ToHashSet(StringComparer.Ordinal);
            var proof = new Proof(run, providers, referenceOnly);
            proofs.Add(run.Id, proof);
            return proof;
        }

        private static IReadOnlyList<WorkerContextContributionAdmission> DefaultAdmissions(JsonElement settings)
        {
            if (!settings.TryGetProperty("resolvedSettingsVersion", out _))
            {
                if (settings.TryGetProperty("contextContributions", out _)) throw Invalid();
                return [];
            }
            var snapshot = WorkerExecutionSnapshot.Read(settings);
            if (snapshot.SourceSettingsVersion == 5 ||
                WorkerExecutionSnapshot.IsCurrentEdition(snapshot.SourceSettingsVersion))
                return snapshot.ContextContributions;
            if (snapshot.LegacyExtensions.Count != 0) throw Invalid();
            return [];
        }

        private Intent FindIntent(Proof owner, string effectId, long upper) =>
            FindIntent(owner.Run.Submission.ConversationId, effectId, upper, owner);

        private Intent FindIntent(string conversationId, string effectId, long upper, Proof? expectedOwner = null)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(effectId) || effectId.Length > 256 || upper < 1 || upper == long.MaxValue)
                throw Invalid();
            var key = (conversationId, effectId, upper);
            if (intents.TryGetValue(key, out var cached))
            {
                if (expectedOwner is not null && cached.Proof.Run.Id != expectedOwner.Run.Id) throw Invalid();
                return cached;
            }
            Intent? found = null;
            var proofEvents = 0;
            var lower = expectedOwner?.Run.Submission.ConversationSequence ?? 0;
            if (upper <= lower) throw Invalid();
            long? before = upper + 1;
            for (var pageIndex = 0; pageIndex < MaximumIntentPages; pageIndex++)
            {
                var page = journal.ReadPreviousPage(conversationId, before, EventJournal.MaximumPageEvents, upper, token);
                proofEvents = checked(proofEvents + page.Events.Count);
                if (proofEvents > MaximumProofEvents) throw Invalid();
                foreach (var item in page.Events.Reverse())
                {
                    if (item.Sequence <= lower) return Save();
                    if (item.Type != CerebralEventTypes.ToolCall || item.CorrelationId != effectId) continue;
                    var source = Accepted(receipts.Get(item.TurnId));
                    if (source.Run.Submission.ConversationId != conversationId ||
                        expectedOwner is not null && source.Run.Id != expectedOwner.Run.Id) throw Invalid();
                    var operation = Operation(item, source) ?? throw Invalid();
                    RequireEventOwner(item, source.Run, operation);
                    var (call, _) = WorkerToolContextProjectionEvidence.ReadCall(item);
                    if (call.CallId != effectId || call.ToolName != operation || found is not null) throw Invalid();
                    ContextProviderRequest? request = null;
                    WorkerContextContributionAdmission? admission = null;
                    WorkerToolContextProjectionEvidence.Origin? projection = null;
                    if (operation == WorkerToolContextProjectionProtocol.CommandId)
                    {
                        var command = WorkerToolContextProjectionProtocol.ReadCommandRequest(call.Arguments);
                        var matches = source.Ready.Values.SelectMany(group => group).Where(candidate =>
                            candidate.Descriptor.SchemaVersion == WorkflowToolContextProjectionContract.RequiredContributionDescriptorSchemaVersion &&
                            candidate.Descriptor.Id == command.ContributionId && candidate.ProjectionDescriptor is not null).ToArray();
                        if (matches.Length != 1) throw Invalid();
                        admission = matches[0];
                        projection = WorkerToolContextProjectionEvidence.ReadOrigin(journal, receipts,
                            source.Run, item, admission, token);
                        request = projection.PublicationRequest;
                    }
                    else if (source.ReferenceOnly.Contains(operation))
                    {
                        var candidates = source.Ready[operation].Where(candidate =>
                            candidate.Descriptor.SchemaVersion == WorkflowContextContributionContract.SchemaVersion).ToArray();
                        if (candidates.Length == 0) throw Invalid();
                        var parsedRequest = ContextProviderNormalizer.ReadRequest(candidates[0].Descriptor.Provider, call.Arguments);
                        request = parsedRequest;
                        var matches = candidates.Where(candidate => JsonElement.DeepEquals(call.Arguments,
                            Element(WorkerContextContributionSnapshot.Template(candidate,
                                parsedRequest.ContextRevision, parsedRequest.ExclusionRevision)))).ToArray();
                        if (matches.Length != 1) throw Invalid();
                        if (matches[0].Descriptor.HistoryProjection == WorkflowContextHistoryProjection.ReferenceOnly)
                            admission = matches[0];
                    }
                    found = new(source, item, call, admission, request, projection);
                    lower = source.Run.Submission.ConversationSequence;
                }
                if (page.NextBeforeSequence is null) return Save();
                if (page.NextBeforeSequence >= before) throw Invalid();
                before = page.NextBeforeSequence;
            }
            throw Invalid(); // Bounded uncertainty must never fall back to the candidate body.

            Intent Save()
            {
                if (found is null) throw Invalid();
                intents.Add(key, found);
                return found;
            }
        }

        private static IReadOnlyList<ContextContribution> Sources(Intent intent, WorkflowCommandResult result)
        {
            var admission = intent.Admission ?? throw Invalid();
            var request = intent.Request ?? throw Invalid();
            if (intent.Projection is { } projection)
            {
                // The host command is not a provider command. Its closed wrapper and authenticated
                // origin must be checked before using the target provider's normalized publication.
                if (result.ConversationCheckpoint is not null || !Enum.IsDefined(result.State) ||
                    JsonSerializer.SerializeToUtf8Bytes(result, Json).Length >
                        WorkflowToolContextProjectionContract.MaximumCommandResultUtf8Bytes) throw Invalid();
                if (result.State == WorkflowEffectState.Unknown) return [];
                if (result.State == WorkflowEffectState.Cancelled)
                {
                    if (result.Data is not null) throw Invalid();
                    return [];
                }
                if (result.State == WorkflowEffectState.Failed && result.Data is null) return [];
                if (result.State is not (WorkflowEffectState.Succeeded or WorkflowEffectState.Failed) ||
                    result.Data is null || string.IsNullOrWhiteSpace(result.ProviderReceiptId)) throw Invalid();
                var parsed = WorkerToolContextProjectionProtocol.ReadResult(result.Data.Value);
                var projectionResult = WorkerToolContextProjectionProtocol.ValidateResult(admission.Descriptor.Provider,
                    projection.PublicationRequest, projection.Link, parsed, result.ProviderReceiptId);
                if ((projectionResult.Disposition == WorkflowToolContextProjectionDisposition.Blocked) !=
                    (result.State == WorkflowEffectState.Failed)) throw Invalid();
                return projectionResult.ProviderResult?.Window.Contributions ?? [];
            }
            if (result.State != WorkflowEffectState.Succeeded || string.IsNullOrWhiteSpace(result.ProviderReceiptId) ||
                result.Data is null || result.ConversationCheckpoint is not null) throw Invalid();
            var normalized = result.Data.Value.Deserialize<ContextProviderResult>(Json) ?? throw Invalid();
            if (normalized.ProviderId != admission.Descriptor.Provider.Id ||
                normalized.ProviderVersion != admission.Descriptor.Provider.Version || normalized.Window is null ||
                normalized.Window.Contributions is null || normalized.Window.Checkpoint is not null ||
                normalized.Window.Revision != request.ContextRevision ||
                normalized.Window.ExclusionRevision != request.ExclusionRevision) throw Invalid();
            ContextValidation.ValidateWindow(normalized.Window);
            if (normalized.Window.Contributions.Any(source => source.Owner != normalized.ProviderId)) throw Invalid();
            var resolution = new ContextProviderResolution(normalized.ProviderId, normalized.ProviderVersion,
                normalized.Window.Contributions.Select(source => new ContextProviderSource(source.Id, source.Category,
                    source.SourceRevision, source.Requirement, source.Messages, source.Complete,
                    source.Priority, source.BeforeCurrentInput)).ToArray());
            var verified = ContextProviderNormalizer.Normalize(admission.Descriptor.Provider, request, resolution,
                intent.Call.CallId, result.ProviderReceiptId);
            if (!JsonElement.DeepEquals(Element(verified), result.Data.Value)) throw Invalid();
            return verified.Window.Contributions;
        }

        private bool Contribution(JsonElement value, Proof owner, long upper, out SourceMatch? match)
        {
            match = null;
            if (value.ValueKind != JsonValueKind.Object ||
                !(value.TryGetProperty("messages", out _) || value.TryGetProperty("sourceRevision", out _) ||
                  value.TryGetProperty("requirement", out _))) return false;
            var provider = OptionalText(value, "owner");
            var handle = OptionalText(value, "exactReadHandle");
            if (handle is null || !handle.StartsWith("cerebral-context:v1.", StringComparison.Ordinal))
            {
                if (provider is not null && owner.ReferenceOnly.Contains(provider) &&
                    owner.Ready[provider].Where(declared => declared.Descriptor.HistoryProjection == WorkflowContextHistoryProjection.ReferenceOnly)
                        .Any(declared => References(declared).Any(reference => reference.Id == OptionalText(value, "id"))))
                    throw Invalid();
                return false;
            }
            var parts = handle.Split('.');
            if (parts.Length != 7) throw Invalid();
            var handleProvider = Untoken(parts[1]);
            var effectId = Untoken(parts[3]);
            var origin = FindIntent(owner.Run.Submission.ConversationId, effectId, upper);
            if (origin.Projection is null && origin.Call.ToolName != handleProvider) throw Invalid();
            if (origin.Admission is not { } admission) return false;
            if (origin.Projection is not null && (origin.Call.ToolName != WorkerToolContextProjectionProtocol.CommandId ||
                admission.Descriptor.Provider.Id != handleProvider)) throw Invalid();
            if (provider != handleProvider || parts[2] != admission.Descriptor.Provider.Version.ToString(CultureInfo.InvariantCulture))
                throw Invalid();
            var receipt = receipts.GetEffectResult(origin.Proof.Run.Id, effectId);
            if (receipt.ProviderReceiptId != Untoken(parts[4])) throw Invalid();
            var original = Sources(origin, receipt).SingleOrDefault(source => source.Id == OptionalText(value, "id"))
                ?? throw Invalid();
            var reference = origin.Request!.References.Single(reference => reference.Id == original.Id);
            if (Untoken(parts[5]) != reference.SourceId ||
                parts[6] != original.SourceRevision.ToString(CultureInfo.InvariantCulture) ||
                original.ExactReadHandle != handle || !JsonElement.DeepEquals(value, Element(original))) throw Invalid();
            // Preserve publications are still authenticated against their host origin; only their
            // body policy differs. Internal host state is hidden at the whole-envelope boundary.
            if (admission.Descriptor.HistoryProjection != WorkflowContextHistoryProjection.ReferenceOnly) return false;
            match = new(admission, original, reference.SourceId);
            return true;
        }

        private JsonElement Project(JsonElement value, Proof proof, long upper, ref bool changed)
        {
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output)) Write(writer, value, proof, upper, 0, ref changed);
            if (!changed) return value;
            if (output.Length > WorkflowContract.MaximumStateBytes) throw Invalid();
            using var parsed = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            return parsed.RootElement.Clone();
        }

        private void Write(Utf8JsonWriter writer, JsonElement value, Proof proof, long upper, int depth, ref bool changed)
        {
            token.ThrowIfCancellationRequested();
            if (++nodes > MaximumNodes || depth > 64) throw Invalid();
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (Contribution(value, proof, upper, out var match))
                {
                    changed = true;
                    Element(new
                    {
                        kind = "context-contribution-reference", bodyOmitted = true,
                        providerId = match!.Admission.Descriptor.Provider.Id,
                        providerVersion = match.Admission.Descriptor.Provider.Version,
                        contributionId = match.Contribution.Id, sourceId = match.SourceId,
                        sourceRevision = match.Contribution.SourceRevision,
                        exactReadHandle = match.Contribution.ExactReadHandle
                    }).WriteTo(writer);
                    return;
                }
                if (Page(value, proof, upper, out var projected))
                { changed = true; projected.WriteTo(writer); return; }
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                { writer.WritePropertyName(property.Name); Write(writer, property.Value, proof, upper, depth + 1, ref changed); }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item, proof, upper, depth + 1, ref changed);
                writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }

        private bool Page(JsonElement value, Proof proof, long upper, out JsonElement projected)
        {
            projected = default;
            if (!value.TryGetProperty("json", out var json) || json.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("offset", out _) || !value.TryGetProperty("totalBytes", out _) ||
                !value.TryGetProperty("truncated", out _)) return false;
            if (OptionalText(value, "runId") is { } runId && OptionalText(value, "effectId") is { } effectId)
            {
                // A page may contain only part of a JSON token. Its original durable subject, not
                // text matching or parsing the fragment, determines whether the model view changes.
                var source = receipts.Get(runId);
                if (source.Submission.ConversationId != proof.Run.Submission.ConversationId) throw Invalid();
                var result = receipts.GetEffectResult(runId, effectId);
                var next = Receipt(source, effectId, result, upper);
                if (JsonElement.DeepEquals(Element(result), Element(next))) return false;
                projected = Element(new
                {
                    kind = "context-projected-receipt-reference", runId, effectId,
                    state = value.GetProperty("state"), providerReceiptId = value.GetProperty("providerReceiptId"),
                    originalOffset = value.GetProperty("offset"), originalTotalBytes = value.GetProperty("totalBytes"),
                    bodyOmitted = true
                });
                return true;
            }
            if (OptionalText(value, "eventId") is not { } eventId ||
                !value.TryGetProperty("snapshotSequence", out var boundaryValue)) return false;
            var boundary = boundaryValue.GetInt64();
            var key = (proof.Run.Submission.ConversationId, eventId);
            if (boundary > proof.Run.Submission.ConversationSequence || boundary < 0 || !readingEvents.Add(key))
                throw Invalid();
            try
            {
                CerebralEvent original;
                try { original = journal.ReadExact(key.Item1, eventId, boundary, token); }
                catch (KeyNotFoundException) { return false; } // Ordinary retained legacy fragments may lack an indexed event.
                var next = Event(original);
                if (ReferenceEquals(original, next)) return false;
                projected = Element(new
                {
                    kind = "context-projected-history-reference", eventId, sequence = original.Sequence,
                    snapshotSequence = boundary, originalOffset = value.GetProperty("offset"),
                    originalTotalBytes = value.GetProperty("totalBytes"), bodyOmitted = true
                });
                return true;
            }
            finally { readingEvents.Remove(key); }
        }
    }

    private static IReadOnlyList<ContextProviderReference> References(WorkerContextContributionAdmission admission) =>
        admission.ProjectionDescriptor?.PublicationTemplate.References ?? admission.Selection.Request!.References;

    private static JsonElement Reference(WorkerContextContributionAdmission admission, ContextProviderRequest request, string effectId,
        WorkflowCommandResult result) => Element(new
        {
            kind = "context-contribution-reference", providerId = admission.Descriptor.Provider.Id,
            providerVersion = admission.Descriptor.Provider.Version, contributionId = admission.Descriptor.Id,
            references = request.References.Select(reference => new
                { reference.Id, reference.Kind, reference.SourceId, reference.ExpectedRevision }).ToArray(),
            effectId, providerReceiptId = result.ProviderReceiptId, state = result.State,
            diagnosticsRetained = result.Error is not null, bodyOmitted = true
        });

    private static string? Operation(CerebralEvent item, Proof proof)
    {
        var name = LooseOperation(item);
        // Recognize coordinates before authenticating the envelope. Damaged actor/data metadata
        // for an original admitted provider must refuse rather than hide its identity and expose it.
        if (Internal(name) || name is not null && proof.ReferenceOnly.Contains(name)) return name;
        if (Internal(item.Actor.Id) || item.Actor.Id is { } actor && proof.ReferenceOnly.Contains(actor)) return item.Actor.Id;
        return name ?? (item.Type == CerebralEventTypes.ToolResult && item.Actor.Kind == EventActorKind.Tool ? item.Actor.Id : null);
    }

    private static string? LooseOperation(CerebralEvent item) =>
        item.Data is { ValueKind: JsonValueKind.Object } data ? OptionalText(data, "toolName") : null;

    private static bool Internal(string? operation) => operation is WorkerWorkflowEffects.GenerateCommand or
        WorkerWorkflowEffects.CompactCommand or WorkerWorkflowEffects.HistoryCommand or WorkerWorkflowEffects.ReceiptCommand or
        WorkerToolContextProjectionProtocol.CommandId;

    private static void RequireEventOwner(CerebralEvent item, WorkflowRun run, string operation)
    {
        var details = Details(item);
        // Frozen display names are presentation metadata. Protected calls still match their
        // complete canonical event above; ownership uses actor kind/id and exact provenance.
        if (item.TurnId != run.Id || item.Provenance.JobId != run.Id ||
            item.ConversationId != run.Submission.ConversationId || item.Sequence <= run.Submission.ConversationSequence ||
            item.CorrelationId != details.CallId || details.ToolName != operation ||
            item.Type == CerebralEventTypes.ToolCall && (item.Actor.Kind != EventActorKind.Agent ||
                item.Actor.Id != run.Submission.ProfileId ||
                item.Provenance.Source != EventSourceKind.Service || item.Provenance.SourceId != "cerebral.engine") ||
            item.Type == CerebralEventTypes.ToolResult && (item.Actor != new EventActor(EventActorKind.Tool, operation) ||
                item.Provenance.Source != EventSourceKind.Tool || item.Provenance.SourceId != operation))
            throw Invalid();
    }

    private static void RequireSameOutcome(WorkflowCommandResult historical, WorkflowCommandResult durable)
    {
        if (!Enum.IsDefined(historical.State) ||
            historical.State != WorkflowEffectState.Unknown && !JsonElement.DeepEquals(Element(historical), Element(durable)))
            throw Invalid();
    }

    private static (string CallId, string ToolName) Details(CerebralEvent item) =>
        item.Data is { ValueKind: JsonValueKind.Object } data ? (Text(data, "callId"), Text(data, "toolName")) : throw Invalid();

    private static WorkflowCommandResult Result(CerebralEvent item) =>
        item.Content.Count == 1 && item.Content[0] is StructuredContentPart { Schema: "workflow.command.result" } part
            ? part.Value.Deserialize<WorkflowCommandResult>(Json) ?? throw Invalid() : throw Invalid();

    private static string Token(string value) => Convert.ToBase64String(Utf8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Untoken(string value)
    {
        if (value.Length > 2048 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw Invalid();
        var decoded = Utf8.GetString(Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') +
            new string('=', (4 - value.Length % 4) % 4)));
        if (Token(decoded) != value) throw Invalid();
        return decoded;
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
    private static string? OptionalText(JsonElement value, string name)
    {
        JsonElement item = default;
        var found = false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name != name) continue;
            if (found) throw Invalid();
            found = true;
            item = property.Value;
        }
        return found && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    }
    private static string Text(JsonElement value, string name) => OptionalText(value, name) is { Length: > 0 } text
        ? text : throw Invalid();
    private static InvalidDataException Invalid() => new(Refusal);
    private static bool InvalidProjection(Exception error) => error is ArgumentException or InvalidOperationException or
        InvalidDataException or JsonException or KeyNotFoundException or FormatException or OverflowException;
}
