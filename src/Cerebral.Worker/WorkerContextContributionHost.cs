using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Trusted host-only source binding, also used by deterministic package-boundary fixtures.</summary>
internal sealed record WorkerContextContributionSource(ServicePluginPackageBinding PackageBinding,
    IWorkflowContextContribution Contribution, Action EnsureAvailable);

internal sealed record WorkerContextContributionRestoration(
    WorkerWorkflowEffects.ContextContributionsData View, WorkflowContextFacts? CurrentFacts);

/// <summary>Generic accepted-state mechanics. Package policy and opaque payload codecs stay in callbacks.</summary>
internal sealed class WorkerContextContributionHost
{
    private readonly StateStore store;
    private readonly WorkflowPackageBindings packages;
    private readonly ServicePluginRuntime? servicePlugins;
    private readonly Func<WorkflowSubmission, IReadOnlyList<string>, IReadOnlyList<string>> requiredGrants;
    private readonly Func<string, IReadOnlyList<string>, string?>? currentPackageProblem;
    private readonly Func<string, string, WorkflowDefinitionDescriptor>? definitionDescriptor;
    private readonly IReadOnlyList<Source> sources;
    private sealed record Source(WorkerContextContributionSource Binding, WorkflowContextContributionDescriptor Descriptor,
        WorkflowToolContextProjectionDescriptor? ProjectionDescriptor);

    internal WorkerContextContributionHost(StateStore store, WorkflowPackageBindings packages,
        ServicePluginRuntime? runtime, Func<WorkflowSubmission, IReadOnlyList<string>, IReadOnlyList<string>> requiredGrants,
        Func<string, IReadOnlyList<string>, string?>? currentPackageProblem = null,
        IReadOnlyList<WorkerContextContributionSource>? fixtureSources = null,
        Func<string, string, WorkflowDefinitionDescriptor>? definitionDescriptor = null)
    {
        this.store = store; this.packages = packages; this.servicePlugins = runtime; this.requiredGrants = requiredGrants;
        this.currentPackageProblem = currentPackageProblem;
        this.definitionDescriptor = definitionDescriptor;
        var bindings = fixtureSources ?? runtime?.WorkflowContextContributions.Select(registration =>
            new WorkerContextContributionSource(registration.PackageBinding, registration.Contribution,
                registration.EnsureAvailable)).ToArray() ?? [];
        if (bindings.Count > WorkflowContextContributionContract.MaximumContributions)
            throw Invalid("Too many package context contributors.");
        sources = Array.AsReadOnly(bindings.Select(binding =>
        {
            var descriptor = WorkerContextContributionSnapshot.FreezeDescriptor(binding.PackageBinding, binding.Contribution.Descriptor);
            return new Source(binding, descriptor,
                WorkerToolContextProjectionAdmission.CaptureDescriptor(descriptor, binding.Contribution));
        }).ToArray());
        if (sources.Select(source => source.Descriptor.Id).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw Invalid("Package context contribution identities are duplicated.");
    }

    internal IReadOnlyList<string> RegisteredProviderIds => Array.AsReadOnly(sources
        .Select(source => source.Descriptor.Provider.Id).Distinct(StringComparer.Ordinal).ToArray());

    internal int ProviderVersion(string providerId) => sources.First(
        source => source.Descriptor.Provider.Id == providerId).Descriptor.Provider.Version;

    internal async Task<WorkerExecutionSnapshot.Snapshot> CaptureAsync(WorkflowSubmission identity,
        WorkerExecutionSnapshot.Snapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var installation = Installation();
        var captured = new List<WorkerContextContributionAdmission>();
        _ = CurrentConsumer(identity, snapshot);
        var facts = Facts(identity, snapshot.Profile, installation, snapshot.ToolReceiptConsumer);
        foreach (var source in sources)
        {
            source.Binding.EnsureAvailable();
            var selection = await source.Binding.Contribution.CaptureAsync(facts, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            source.Binding.EnsureAvailable();
            _ = CurrentConsumer(identity, snapshot);
            captured.Add(WorkerContextContributionSnapshot.Freeze(source.Binding.PackageBinding, source.Descriptor, selection,
                source.ProjectionDescriptor));
        }
        var frozen = WorkerContextContributionSnapshot.ValidateCollection(captured);
        WorkerToolContextProjectionAdmission.RequireConsumer(identity, snapshot, frozen);
        // Freeze the selected service-tool owners together with provider owners. A provider-only
        // replacement would drop a tool package from the accepted graph and admit an update race.
        var additional = WorkerContextContributionSnapshot.ProviderIds(frozen)
            .Select(id => id.Split(':', 2)[0])
            .Concat((snapshot.SelectedToolOwners ?? []).Where(item => item.PackageId is not null)
                .Select(item => item.PackageId!))
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var result = snapshot with { ContextContributions = frozen, ContextContributionInstallationId = installation };
        if (additional.Length != 0)
            result = result with { PackageBindings = packages.Capture(identity.DefinitionId, additional) };
        ValidateSnapshot(result);
        return result;
    }

    internal void RecheckBeforeAccept(WorkflowSubmission submission)
    {
        var snapshot = WorkerExecutionSnapshot.Read(submission.SettingsSnapshot);
        WorkerToolContextProjectionAdmission.RequireConsumer(submission, snapshot, snapshot.ContextContributions);
        RequireCurrent(submission, snapshot, snapshot.ContextContributions, CancellationToken.None);
    }

    internal async Task<WorkerWorkflowEffects.ContextContributionsData> RestoreAsync(WorkflowSubmission accepted,
        long contextRevision, long exclusionRevision, CancellationToken token) =>
        (await RestoreWithObservationAsync(accepted, contextRevision, exclusionRevision, token).ConfigureAwait(false)).View;

    internal async Task<WorkerContextContributionRestoration> RestoreWithObservationAsync(WorkflowSubmission accepted,
        long contextRevision, long exclusionRevision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (contextRevision < 0 || exclusionRevision < 0) throw Invalid("Invalid context revisions.");
        var snapshot = WorkerExecutionSnapshot.Read(accepted.SettingsSnapshot);
        var admissions = HistoricalAdmissions(accepted);
        RequireCurrent(accepted, snapshot, admissions, token);
        var items = new List<WorkerWorkflowEffects.ContextContributionData>();
        // Receipt facets never enter the v1 once-only initial-provider path.
        foreach (var admission in admissions.Where(item => !WorkerToolContextProjectionAdmission.IsReceipt(item.Descriptor)))
        {
            var status = JsonNamingPolicy.CamelCase.ConvertName(admission.Selection.Disposition.ToString());
            ContextProviderRequest? request = null;
            if (admission.Selection.Disposition == WorkflowContextDisposition.Ready)
            {
                var source = Find(admission);
                var facts = new WorkflowContextAcceptedFacts(
                    AcceptedFacts(accepted, snapshot),
                    accepted.ProjectId, accepted.ConversationSequence, contextRevision, exclusionRevision);
                request = await source.Binding.Contribution.RestoreAsync(facts, admission.Selection, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                source.Binding.EnsureAvailable();
                var expected = WorkerContextContributionSnapshot.Template(admission, contextRevision, exclusionRevision);
                var normalized = Cerebral.Engine.Context.ContextProviderNormalizer.ReadRequest(
                    admission.Descriptor.Provider, CerebralJson.Element(request));
                if (!JsonElement.DeepEquals(CerebralJson.Element(normalized), CerebralJson.Element(expected)))
                    throw Invalid("Restored context differs from its accepted request template.");
                RequireCurrent(accepted, snapshot, admissions, token);
                request = expected;
            }
            items.Add(new(admission.Descriptor.Id, status, request));
        }
        var currentFacts = RequireCurrent(accepted, snapshot, admissions, token);
        return new(new(WorkflowContextContributionContract.SchemaVersion, Array.AsReadOnly(items.ToArray())), currentFacts);
    }

    internal async Task<WorkerReceiptProjectionAdmissionsData> GetReceiptProjectionAdmissionsAsync(
        WorkflowSubmission accepted, long contextRevision, long exclusionRevision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (contextRevision < 0 || exclusionRevision < 0) throw Invalid("Invalid context revisions.");
        var snapshot = WorkerExecutionSnapshot.Read(accepted.SettingsSnapshot);
        var admissions = HistoricalAdmissions(accepted);
        RequireCurrent(accepted, snapshot, admissions, token);
        foreach (var admission in admissions.Where(item => WorkerToolContextProjectionAdmission.IsReceipt(item.Descriptor) &&
            item.Selection.Disposition == WorkflowContextDisposition.Ready))
        {
            var source = Find(admission);
            await RestoreReceiptTemplateAsync(accepted, snapshot, admissions, admission, source,
                contextRevision, exclusionRevision, token).ConfigureAwait(false);
        }
        RequireCurrent(accepted, snapshot, admissions, token);
        return WorkerToolContextProjectionAdmission.View(WorkerToolContextProjectionAdmission.SupportsConsumer(accepted, snapshot)
            ? admissions : []);
    }

    internal WorkerToolProjectionContext GetToolProjectionContext(WorkflowSubmission accepted, string contributionId,
        string toolName, long contextRevision, long exclusionRevision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (contextRevision < 0 || exclusionRevision < 0 || !WorkerToolContextProjectionAdmission.ToolName(toolName))
            throw Invalid("Invalid receipt projection coordinate.");
        var snapshot = WorkerExecutionSnapshot.Read(accepted.SettingsSnapshot);
        var admissions = HistoricalAdmissions(accepted);
        var admission = admissions.SingleOrDefault(item => item.Descriptor.Id == contributionId &&
            WorkerToolContextProjectionAdmission.IsReceipt(item.Descriptor))
            ?? throw Invalid("The accepted receipt contribution is unavailable.");
        if (admission.Selection.Disposition != WorkflowContextDisposition.Ready ||
            admission.ProjectionDescriptor is not { } projection ||
            !projection.ToolNames.Contains(toolName, StringComparer.Ordinal))
            throw Invalid("The saved tool does not match a Ready accepted receipt contribution.");
        // Tool filtering precedes package Validate/Restore/Project callbacks.
        var source = Find(admission);
        if (source.Binding.Contribution is not IWorkflowToolContextProjectionContribution facet)
            throw Invalid("The accepted receipt contribution no longer exposes its facet.");
        var current = RequireCurrent(accepted, snapshot, admissions, token)
            ?? throw Invalid("Ready receipt context has no current consumer observation.");
        source.Binding.EnsureAvailable();
        return new(this, accepted, admission, new(AcceptedFacts(accepted, snapshot),
            accepted.ProjectId, accepted.ConversationSequence, contextRevision, exclusionRevision), facet, current, toolName);
    }

    internal async Task<WorkflowToolContextProjectionResolution> InvokeToolProjectionAsync(
        WorkerToolProjectionContext context, WorkflowToolContextProjectionRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(context.Owner, this)) throw Invalid("Receipt projection coordinate has a different host.");
        var accepted = context.Accepted;
        var snapshot = WorkerExecutionSnapshot.Read(accepted.SettingsSnapshot);
        var admissions = HistoricalAdmissions(accepted);
        var source = Find(context.Admission);
        if (!ReferenceEquals(source.Binding.Contribution, context.Source) ||
            !admissions.Any(item => JsonElement.DeepEquals(CerebralJson.Element(item), CerebralJson.Element(context.Admission))) ||
            context.Admission.Selection.Disposition != WorkflowContextDisposition.Ready ||
            context.Admission.ProjectionDescriptor is not { } projection ||
            !projection.ToolNames.Contains(context.ToolName, StringComparer.Ordinal))
            throw Invalid("Receipt projection coordinate does not match its accepted contribution.");
        RequireSameFacts(context.CurrentFacts, RequireCurrent(accepted, snapshot, admissions, token));
        if (request.SchemaVersion != WorkflowToolContextProjectionContract.SchemaVersion ||
            request.ToolName != context.ToolName ||
            !JsonElement.DeepEquals(CerebralJson.Element(request.Facts), CerebralJson.Element(context.Facts)) ||
            !JsonElement.DeepEquals(CerebralJson.Element(request.Selection), CerebralJson.Element(context.Admission.Selection)) ||
            request.Link is null || request.Link.SchemaVersion != WorkflowToolContextProjectionContract.SchemaVersion ||
            request.Link.ContributionId != context.Admission.Descriptor.Id || request.Link.Slot is null ||
            request.SavedEffect is null || request.SavedEffect.SchemaVersion != WorkflowToolContextProjectionContract.SchemaVersion ||
            request.SavedEffect.RunId != request.Link.RunId || request.SavedEffect.EffectId != request.Link.ToolEffectId ||
            request.PublicationRequest is null)
            throw Invalid("Receipt projection input does not match its host-issued coordinate.");
        var template = projection.PublicationTemplate;
        var slot = template.References.Single();
        if (request.Link.Slot.Id != slot.Id || request.Link.Slot.CurrentSourceRevision is < 1)
            throw Invalid("Receipt projection slot does not match its accepted template.");
        var nextRevision = checked((request.Link.Slot.CurrentSourceRevision ?? 0) + 1);
        var expected = template with
        {
            ContextRevision = context.Facts.ContextRevision, ExclusionRevision = context.Facts.ExclusionRevision,
            References = Array.AsReadOnly(new[] { slot with { ExpectedRevision = nextRevision } })
        };
        var normalized = Cerebral.Engine.Context.ContextProviderNormalizer.ReadRequest(
            context.Admission.Descriptor.Provider, CerebralJson.Element(request.PublicationRequest));
        if (!JsonElement.DeepEquals(CerebralJson.Element(normalized), CerebralJson.Element(expected)))
            throw Invalid("Receipt publication request changed its admitted template.");
        if (request.PriorState is { } state)
            WorkerToolContextProjectionAdmission.Bound(state, WorkflowToolContextProjectionContract.MaximumProviderStateUtf8Bytes);
        // Copy all package-visible JSON and use only host-frozen selection/reference collections.
        var frozen = request with
        {
            Facts = context.Facts, Selection = context.Admission.Selection, PublicationRequest = expected,
            PriorState = request.PriorState?.Clone(),
            SavedEffect = request.SavedEffect with
                { Arguments = request.SavedEffect.Arguments.Clone(), Data = request.SavedEffect.Data?.Clone() }
        };
        WorkerToolContextProjectionAdmission.Bound(frozen, WorkflowToolContextProjectionContract.MaximumCallbackInputUtf8Bytes);
        await RestoreReceiptTemplateAsync(accepted, snapshot, admissions, context.Admission, source,
            context.Facts.ContextRevision, context.Facts.ExclusionRevision, token).ConfigureAwait(false);
        RequireSameFacts(context.CurrentFacts, RequireCurrent(accepted, snapshot, admissions, token));
        var result = await context.Source.ProjectAsync(frozen, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        source.Binding.EnsureAvailable();
        RequireSameFacts(context.CurrentFacts, RequireCurrent(accepted, snapshot, admissions, token));
        // Parent command validates the complete closed outcome matrix and normalizes publication.
        // Detach the raw callback's JSON/collections so a package cannot mutate a returned result later.
        var detached = WorkerToolContextProjectionAdmission.FreezeResolution(result);
        token.ThrowIfCancellationRequested();
        source.Binding.EnsureAvailable();
        RequireSameFacts(context.CurrentFacts, RequireCurrent(accepted, snapshot, admissions, token));
        return detached;
    }

    private async Task RestoreReceiptTemplateAsync(WorkflowSubmission accepted, WorkerExecutionSnapshot.Snapshot snapshot,
        IReadOnlyList<WorkerContextContributionAdmission> admissions, WorkerContextContributionAdmission admission,
        Source source, long contextRevision, long exclusionRevision, CancellationToken token)
    {
        RequireCurrent(accepted, snapshot, admissions, token);
        var facts = new WorkflowContextAcceptedFacts(AcceptedFacts(accepted, snapshot),
            accepted.ProjectId, accepted.ConversationSequence, contextRevision, exclusionRevision);
        var request = await source.Binding.Contribution.RestoreAsync(facts, admission.Selection, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        source.Binding.EnsureAvailable();
        var expected = WorkerContextContributionSnapshot.Template(admission, contextRevision, exclusionRevision);
        var normalized = Cerebral.Engine.Context.ContextProviderNormalizer.ReadRequest(
            admission.Descriptor.Provider, CerebralJson.Element(request));
        if (!JsonElement.DeepEquals(CerebralJson.Element(normalized), CerebralJson.Element(expected)))
            throw Invalid("Restored receipt context differs from its accepted base request template.");
        RequireCurrent(accepted, snapshot, admissions, token);
    }

    internal void RequireCurrent(WorkflowSubmission accepted)
    {
        var snapshot = WorkerExecutionSnapshot.Read(accepted.SettingsSnapshot);
        RequireCurrent(accepted, snapshot, HistoricalAdmissions(accepted), CancellationToken.None);
    }

    internal void RecheckCurrent(WorkflowSubmission accepted, WorkflowContextFacts? validatedFacts)
    {
        var snapshot = WorkerExecutionSnapshot.Read(accepted.SettingsSnapshot);
        _ = CurrentConsumer(accepted, snapshot);
        var observation = ObserveCurrent(accepted, snapshot, HistoricalAdmissions(accepted), CancellationToken.None);
        RequireSameFacts(validatedFacts, observation is { } current
            ? CurrentFacts(accepted, snapshot, current.Profile, current.Installation) : null);
    }

    private WorkflowContextFacts? RequireCurrent(WorkflowSubmission accepted, WorkerExecutionSnapshot.Snapshot snapshot,
        IReadOnlyList<WorkerContextContributionAdmission> admissions, CancellationToken token)
    {
        _ = CurrentConsumer(accepted, snapshot);
        var ready = admissions.Where(admission => admission.Selection.Disposition == WorkflowContextDisposition.Ready).ToArray();
        if (ready.Length == 0) return null;
        WorkflowContextFacts? validatedFacts = null;
        foreach (var admission in ready)
        {
            var observation = ObserveCurrent(accepted, snapshot, admissions, token)
                ?? throw Invalid("Ready context has no current owner observation.");
            var source = Find(admission);
            var facts = CurrentFacts(accepted, snapshot, observation.Profile, observation.Installation);
            if (validatedFacts is not null) RequireSameFacts(validatedFacts, facts);
            source.Binding.Contribution.Validate(
                AcceptedFacts(accepted, snapshot), facts, admission.Selection, token);
            // Validate is a package callback. Its earlier host observation cannot authorize a
            // dispatch after a concurrent revocation, even when the callback returns successfully.
            var refreshed = ObserveCurrent(accepted, snapshot, admissions, token)
                ?? throw Invalid("Ready context has no current owner observation.");
            RequireSameFacts(facts, CurrentFacts(accepted, snapshot, refreshed.Profile, refreshed.Installation));
            validatedFacts = facts;
        }
        var final = ObserveCurrent(accepted, snapshot, admissions, token)
            ?? throw Invalid("Ready context has no current owner observation.");
        RequireSameFacts(validatedFacts, CurrentFacts(accepted, snapshot, final.Profile, final.Installation));
        return validatedFacts;
    }

    private static void RequireSameFacts(WorkflowContextFacts? validated, WorkflowContextFacts? observed)
    {
        if (!JsonElement.DeepEquals(CerebralJson.Element(validated), CerebralJson.Element(observed)))
            throw Invalid("Current context consumer facts changed after package validation.");
    }

    private (AgentProfile Profile, string Installation)? ObserveCurrent(WorkflowSubmission accepted,
        WorkerExecutionSnapshot.Snapshot snapshot, IReadOnlyList<WorkerContextContributionAdmission> admissions,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ready = admissions.Where(admission => admission.Selection.Disposition == WorkflowContextDisposition.Ready).ToArray();
        if (ready.Length == 0) return null;
        var current = store.Get<AgentProfile>("agent", accepted.ProfileId)
            ?? throw new UnauthorizedAccessException("The accepted context consumer is unavailable.");
        var installation = Installation();
        var providers = WorkerContextContributionSnapshot.ProviderIds(admissions);
        if (current.WorkerId != "local" || snapshot.Profile.WorkerId != "local" ||
            current.Id != snapshot.Profile.Id || accepted.ProfileId != snapshot.Profile.Id ||
            AcceptedInstallation(snapshot) is not { Length: > 0 } acceptedInstallation || installation != acceptedInstallation ||
            requiredGrants(accepted, providers).Any(grant => !snapshot.Capabilities.Contains(grant, StringComparer.Ordinal) ||
                !current.Capabilities.Contains(grant, StringComparer.Ordinal)))
            throw new UnauthorizedAccessException("Accepted context consumer ownership, origin or grants changed.");
        if (currentPackageProblem is null)
            throw Invalid("Accepted context requires current package lifecycle observation.");
        if (currentPackageProblem(accepted.DefinitionId, providers) is { } problem)
            throw new InvalidOperationException(problem);
        foreach (var admission in ready)
        {
            token.ThrowIfCancellationRequested();
            if (packages.Check(accepted, admission.Descriptor.Provider.Id) is { } mismatch)
                throw new InvalidOperationException(mismatch);
            var source = Find(admission);
            source.Binding.EnsureAvailable();
            if (source.Binding.PackageBinding.Version != admission.PackageVersion ||
                source.Binding.PackageBinding.Generation != admission.Generation ||
                !JsonElement.DeepEquals(CerebralJson.Element(source.Descriptor), CerebralJson.Element(admission.Descriptor)) ||
                !JsonElement.DeepEquals(CerebralJson.Element(source.ProjectionDescriptor), CerebralJson.Element(admission.ProjectionDescriptor)))
                throw Invalid("Accepted context package generation or descriptor changed.");
        }
        token.ThrowIfCancellationRequested();
        return (current, installation);
    }

    /// <summary>Read-only ORIGINAL manifests. New snapshots need no current package or selection lookup.
    /// Legacy extension interpretation is delegated to the package's declared pure compatibility reader.</summary>
    internal IReadOnlyList<WorkerContextContributionAdmission> HistoricalAdmissions(WorkflowRun run) =>
        HistoricalAdmissions(run.Submission);

    private IReadOnlyList<WorkerContextContributionAdmission> HistoricalAdmissions(WorkflowSubmission accepted)
    {
        if (!accepted.SettingsSnapshot.TryGetProperty("resolvedSettingsVersion", out _)) return [];
        var snapshot = WorkerExecutionSnapshot.Read(accepted.SettingsSnapshot);
        if (snapshot.SourceSettingsVersion == 5 ||
            WorkerExecutionSnapshot.IsCurrentEdition(snapshot.SourceSettingsVersion))
        {
            WorkerToolContextProjectionAdmission.RequireConsumer(accepted, snapshot, snapshot.ContextContributions);
            return snapshot.ContextContributions;
        }
        if (snapshot.LegacyExtensions.Count == 0) return [];
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var converted = new List<WorkerContextContributionAdmission>();
        foreach (var source in sources.Where(item => !WorkerToolContextProjectionAdmission.IsReceipt(item.Descriptor)))
        {
            var format = source.Descriptor.LegacyFormats.SingleOrDefault(
                item => item.SnapshotVersion == snapshot.SourceSettingsVersion);
            if (format is null) continue;
            if (snapshot.SourceSettingsVersion != 4 ||
                format.Fields.Any(WorkerContextContributionSnapshot.IsReservedLegacyField))
                throw Invalid("Unsupported legacy context extension format.");
            var fields = format.Fields.Where(snapshot.LegacyExtensions.ContainsKey).ToArray();
            if (fields.Length == 0) continue;
            foreach (var field in fields)
                if (!claimed.Add(field)) throw Invalid("Legacy context extension ownership is ambiguous.");
            var fragment = JsonSerializer.SerializeToElement(fields.ToDictionary(
                field => field, field => snapshot.LegacyExtensions[field].Clone(), StringComparer.Ordinal), CerebralJson.Options);
            if (JsonSerializer.SerializeToUtf8Bytes(fragment).Length > WorkflowContextContributionContract.MaximumLegacyFragmentUtf8Bytes)
                throw Invalid("Legacy context fragments exceed their bound.");
            var prior = snapshot.PackageBindings.SingleOrDefault(binding => binding.Id == source.Binding.PackageBinding.Id)
                ?? throw Invalid("Legacy context has no accepted package association.");
            source.Binding.EnsureAvailable();
            var facts = new WorkflowContextAcceptedFacts(
                AcceptedFacts(accepted, snapshot), accepted.ProjectId,
                accepted.ConversationSequence, 0, 0);
            var selection = source.Binding.Contribution.ReadLegacy(new(facts, snapshot.SourceSettingsVersion, fragment),
                CancellationToken.None);
            source.Binding.EnsureAvailable();
            if (selection.Disposition == WorkflowContextDisposition.Unsupported)
                throw Invalid("The saved context extension has no supported compatibility view.");
            converted.Add(WorkerContextContributionSnapshot.Freeze(prior, source.Descriptor, selection));
        }
        if (claimed.Count != snapshot.LegacyExtensions.Count)
            throw Invalid("Legacy context has no compatible package reader.");
        return WorkerContextContributionSnapshot.ValidateCollection(converted);
    }

    private Source Find(WorkerContextContributionAdmission admission) =>
        sources.SingleOrDefault(source => source.Descriptor.Id == admission.Descriptor.Id &&
            source.Binding.PackageBinding.Id == admission.PackageId)
        ?? throw Invalid("The accepted context contribution is unavailable.");

    internal static void ValidateSnapshot(WorkerExecutionSnapshot.Snapshot snapshot)
    {
        var admissions = WorkerContextContributionSnapshot.ValidateCollection(snapshot.ContextContributions);
        foreach (var admission in admissions.Where(item => item.Selection.Disposition == WorkflowContextDisposition.Ready))
        {
            var binding = snapshot.PackageBindings.SingleOrDefault(item => item.Id == admission.PackageId);
            if (binding is null || !binding.Loaded || binding.Version != admission.PackageVersion ||
                binding.Generation != admission.Generation || string.IsNullOrWhiteSpace(snapshot.ContextContributionInstallationId))
                throw Invalid("Ready context has no exact accepted host/package binding.");
        }
        // Admit only a collection whose body-free execution view fits even after revision rebasing.
        WorkerWorkflowEffects.ValidateContextContributionView(new(WorkflowContextContributionContract.SchemaVersion,
            Array.AsReadOnly(admissions.Where(admission => !WorkerToolContextProjectionAdmission.IsReceipt(admission.Descriptor))
                .Select(admission => new WorkerWorkflowEffects.ContextContributionData(
                admission.Descriptor.Id, JsonNamingPolicy.CamelCase.ConvertName(admission.Selection.Disposition.ToString()),
                admission.Selection.Disposition == WorkflowContextDisposition.Ready
                    ? WorkerContextContributionSnapshot.Template(admission, long.MaxValue, long.MaxValue) : null)).ToArray())));
        _ = WorkerToolContextProjectionAdmission.View(admissions);
    }

    private static string? AcceptedInstallation(WorkerExecutionSnapshot.Snapshot snapshot) =>
        snapshot.ContextContributionInstallationId;

    private string Installation() => store.Get<HostSettings>("settings", "host")?.InstallationId is { Length: > 0 and <= 128 } id
        ? id : throw Invalid("The context host installation is unavailable.");

    private WorkflowToolReceiptContextConsumer? CurrentConsumer(WorkflowSubmission accepted,
        WorkerExecutionSnapshot.Snapshot snapshot)
    {
        if (!WorkerExecutionSnapshot.IsCurrentEdition(snapshot.SourceSettingsVersion)) return null;
        var current = definitionDescriptor is null
            ? new WorkflowToolReceiptContextConsumer(WorkflowToolReceiptContextConsumerContract.SchemaVersion, false)
            : WorkerToolContextProjectionAdmission.FreezeConsumer(
                definitionDescriptor(accepted.DefinitionId, accepted.DefinitionVersion));
        if (current != snapshot.ToolReceiptConsumer)
            throw Invalid("The loaded workflow receipt consumer changed after accepted snapshot capture.");
        return current;
    }

    private static WorkflowContextFacts AcceptedFacts(WorkflowSubmission accepted, WorkerExecutionSnapshot.Snapshot snapshot) =>
        Facts(accepted, snapshot.Profile, AcceptedInstallation(snapshot), snapshot.ToolReceiptConsumer);

    private WorkflowContextFacts CurrentFacts(WorkflowSubmission accepted, WorkerExecutionSnapshot.Snapshot snapshot,
        AgentProfile profile, string installation) =>
        Facts(accepted, profile, installation, CurrentConsumer(accepted, snapshot));

    private static WorkflowContextFacts Facts(WorkflowSubmission submission, AgentProfile profile, string? installation,
        WorkflowToolReceiptContextConsumer? consumer)
    {
        if (submission.ProfileId != profile.Id || submission.RequestId.Length is < 1 or > 256 ||
            submission.ConversationId.Length is < 1 or > 256 || profile.Id.Length is < 1 or > 128 ||
            submission.DefinitionId.Length is < 1 or > 128 || submission.DefinitionVersion.Length is < 1 or > 64 ||
            profile.Role.Length is < 1 or > 64 || profile.Revision < 0 ||
            installation is not null && (string.IsNullOrWhiteSpace(installation) || installation.Length > 128))
            throw Invalid("Invalid host context identity.");
        return new(new(submission.RequestId, submission.ConversationId, submission.ProfileId,
            submission.DefinitionId, submission.DefinitionVersion,
            submission.Caller is { } caller ? new(caller.Kind, caller.Id) : null, submission.ParentRunId),
            profile.Role, profile.Revision, profile.WorkerId, installation) { ToolReceiptConsumer = consumer };
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
