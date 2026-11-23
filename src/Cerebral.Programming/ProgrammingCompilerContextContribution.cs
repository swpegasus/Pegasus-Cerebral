using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

/// <summary>One admitted contribution/provider instance; all callbacks are bounded, deterministic and store-free.</summary>
internal sealed class ProgrammingCompilerContextContribution : IWorkflowToolContextProjectionContribution, IContextProviderCommandHandler
{
    internal const string ContributionId = "pegasus.programming:compiler-diagnostics";
    internal const string ProviderId = "pegasus.programming:compiler-context.resolve";
    internal const string SlotId = "programming.compiler-diagnostic-bundle";
    internal const string SourceId = "programming/compiler-diagnostic-bundle/v1";
    internal const string ConversationId = "pegasus.conversation:chat";
    private const int PublicationBytes = 32 * 1024;
    private const int NormalizationReserveBytes = 4096; // Host-only handle (<=2048 ASCII chars) and single-window attribution/envelope.
    private const int MaximumObservedEffects = 64;
    private static readonly string[] RelevantTools = ["process.run", "file.write", "file.edit", "file.edit-range"];
    private static readonly JsonSerializerOptions StateJson = new(ProgrammingEvidence.Json)
    {
        PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly ContextProviderDescriptor ProviderDescriptor = new(ProviderId, 1,
        "Project bounded complete compiler observations from exact committed tool evidence.", Array.AsReadOnly(new[] { SlotId }), 1, PublicationBytes);
    private static ContextProviderRequest Template => new(ProviderId, 1,
        Array.AsReadOnly(new[] { new ContextProviderReference(SlotId, SlotId, SourceId, null, ContextRequirement.Required) }), PublicationBytes);
    public WorkflowContextContributionDescriptor Descriptor { get; } = new(
        WorkflowToolContextProjectionContract.RequiredContributionDescriptorSchemaVersion, ContributionId, ProviderDescriptor,
        WorkflowContextHistoryProjection.ReferenceOnly, []);
    public WorkflowToolContextProjectionDescriptor ProjectionDescriptor { get; } = new(
        WorkflowToolContextProjectionContract.SchemaVersion, Array.AsReadOnly(RelevantTools), Template);
    ContextProviderDescriptor IContextProviderCommandHandler.Provider => ProviderDescriptor;
    WorkflowCommandDescriptor IWorkflowCommandHandler.Descriptor { get; } = new(ProviderId, 1,
        ProviderDescriptor.Description,
        WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, [], WorkflowReplayPolicy.Idempotent, 30);

    public Task<WorkflowContextSelection> CaptureAsync(WorkflowContextFacts facts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Applicable(facts)
            ? new WorkflowContextSelection(WorkflowContextDisposition.Ready, null, null, null, Template)
            : new WorkflowContextSelection(WorkflowContextDisposition.Omitted, "The exact receipt-aware Conversation edition is required.", null, null, null));
    }
    public void Validate(WorkflowContextFacts accepted, WorkflowContextFacts current, WorkflowContextSelection selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Disposition == WorkflowContextDisposition.Omitted && !Applicable(accepted) && !Applicable(current) &&
            selection.Request is null && selection.Payload is null && selection.PayloadDigest is null &&
            !string.IsNullOrWhiteSpace(selection.Reason)) return;
        if (!Applicable(accepted) || !Applicable(current) ||
            !JsonElement.DeepEquals(Element(accepted), Element(current)) || selection.Disposition != WorkflowContextDisposition.Ready ||
            selection.Payload is not null || selection.PayloadDigest is not null || selection.Reason is not null ||
            selection.Request is null || !JsonElement.DeepEquals(Element(selection.Request), Element(Template)))
            throw new ArgumentException("The accepted Programming context selection or consumer changed.");
    }
    public Task<ContextProviderRequest> RestoreAsync(WorkflowContextAcceptedFacts facts, WorkflowContextSelection selection,
        CancellationToken cancellationToken)
    {
        Validate(facts.Accepted, facts.Accepted, selection, cancellationToken);
        if (facts.ContextRevision < 0 || facts.ExclusionRevision < 0) throw new ArgumentException("Invalid context revisions.");
        return Task.FromResult(Template with { ContextRevision = facts.ContextRevision, ExclusionRevision = facts.ExclusionRevision });
    }
    public WorkflowContextSelection ReadLegacy(WorkflowContextLegacyFacts facts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(WorkflowContextDisposition.Unsupported, "No legacy compiler projection selection is supported.", null, null, null);
    }
    public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Failed,
            Error: "This descriptor-v2 provider is projection-only; no direct v1 initial resolution is available."));
    }
    public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken cancellationToken) =>
        ExecuteAsync(context, cancellationToken);

    private sealed record BuildAnchor(string EffectId, string SavedEffectSha256, string ProjectPath,
        string Configuration, long SourceGeneration, int ExitCode, string? DiagnosticFingerprint, int Shown, int Total);
    private sealed record MutationAnchor(string EffectId, string Path, string Sha256);
    private sealed record ProviderState(int SchemaVersion, string RunId, string? LastProjectionEffectId,
        long SourceGeneration, BuildAnchor? LastBuild, MutationAnchor? LastMutation,
        string LastObservation, IReadOnlyList<string> ObservedEffects);
    private sealed record ToolData(string Name, string Content, bool Truncated, ToolExecutionEvidence? Evidence,
        bool FollowUpRequired = false);
    private sealed record BuildIdentity(string ProjectPath, string Configuration);

    public Task<WorkflowToolContextProjectionResolution> ProjectAsync(WorkflowToolContextProjectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Link);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ValidateRequest(request, cancellationToken);
            var state = ReadState(request);
            var saved = request.SavedEffect;
            if (state.ObservedEffects.Contains(saved.EffectId, StringComparer.Ordinal))
            {
                if (state.LastBuild is { } last && last.EffectId == saved.EffectId && last.SavedEffectSha256 != request.Link.SavedEffectSha256)
                    return Task.FromResult(Blocked(request, "A repeated build identity changed its saved-effect digest; prior state and batch remain retained."));
                return Task.FromResult(StateOnly(request, state with { LastObservation = "duplicate-observation" }));
            }
            if (state.ObservedEffects.Count == MaximumObservedEffects)
                return Task.FromResult(Blocked(request, "The bounded compiler observation state is full; the prior batch remains retained."));
            state = state with { ObservedEffects = state.ObservedEffects.Append(saved.EffectId).ToArray() };
            if (saved.State == WorkflowEffectState.Cancelled)
                return Task.FromResult(StateOnly(request, state with { LastObservation = "cancelled-tool" }));
            var arguments = saved.Arguments.GetProperty("arguments");
            var data = ReadToolData(saved.Data);
            if (data.Name != request.ToolName) throw new ArgumentException("Tool result identity mismatch.");
            if (request.ToolName != "process.run")
            {
                var changed = ProgrammingMutationProof.Read(request.ToolName, arguments, saved.State, saved.EffectId,
                    data.Content, data.Truncated, data.FollowUpRequired, data.Evidence);
                if (changed is null || state.LastBuild is { } bound &&
                    !ProgrammingEvidence.Within(changed.Path, Path.GetDirectoryName(bound.ProjectPath)!))
                    return Task.FromResult(StateOnly(request, state with { LastObservation = "no-proved-in-scope-source-change" }));
                return Task.FromResult(StateOnly(request, state with
                {
                    SourceGeneration = checked(state.SourceGeneration + 1), LastObservation = "proved-source-change",
                    LastMutation = new(saved.EffectId, changed.Path, changed.Sha256)
                }));
            }
            var build = ReadBuildIdentity(arguments);
            if (build is null) return Task.FromResult(StateOnly(request, state with { LastObservation = "non-build-process" }));
            if (state.LastBuild is { } prior && (!ProgrammingEvidence.SamePath(prior.ProjectPath, build.ProjectPath) ||
                prior.Configuration != build.Configuration))
                return Task.FromResult(Blocked(request, "The single compiler slot is already bound to another project/configuration; its actual batch was preserved."));
            if (data.Evidence is { Outcome: ToolExecutionOutcome.Rejected })
                return Task.FromResult(StateOnly(request, state with { LastObservation = "rejected-build-request" }));
            if (data.FollowUpRequired || data.Evidence is not { ExitCode: { } exit, Partial: not true } evidence ||
                data.Truncated || !KnownCompilerOutcome(saved.State, evidence.Outcome, exit))
                return Task.FromResult(Blocked(request, "Compiler intake is incomplete: shown=0; total=unknown; complete=false. The last actual batch remains retained."));
            var batch = ProgrammingCompilerDiagnostics.Parse(data.Content, exit);
            if (!batch.Complete || batch.Total is null)
                return Task.FromResult(Blocked(request, "Compiler intake is incomplete: shown=" + batch.Shown +
                    "; total=" + (batch.Total?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown") +
                    "; complete=false; reason=" + batch.Reason + ". The last actual batch remains retained."));
            if (exit == 0 && batch.Shown != 0)
                return Task.FromResult(Blocked(request, "The successful build outcome contradicts its compiler error rows; the last actual batch remains retained."));
            var anchor = new BuildAnchor(saved.EffectId, request.Link.SavedEffectSha256, build.ProjectPath, build.Configuration,
                state.SourceGeneration, exit, batch.Fingerprint, batch.Shown, batch.Total.Value);
            state = state with { LastBuild = anchor, LastObservation = "actual-compiler-batch" };
            var packet = Element(new
            {
                schema = "pegasus.programming.compiler-diagnostic-batch/1",
                buildEffectId = anchor.EffectId, anchor.SavedEffectSha256, anchor.ProjectPath, anchor.Configuration,
                sourceGeneration = anchor.SourceGeneration,
                generationBasis = "Confirmed declared mutation receipts observed in this run; no dependency, environment or external-edit closure.",
                buildState = saved.State.ToString(), anchor.ExitCode, compilerOutcome = exit == 0 ? "passed" : "failed",
                shown = batch.Shown, total = batch.Total, complete = batch.Complete, diagnosticFingerprint = batch.Fingerprint,
                // Other receipts may already prove behavior; this record only establishes its compiler outcome.
                validationFollowthrough = new
                {
                    compilationVerified = exit == 0,
                    behaviorVerification = "not-established-by-compiler-receipt",
                    nextAction = exit == 0 ? "use-supplied-admitted-behavior-check" : "repair-compiler-diagnostics",
                    nextActionCondition = exit == 0 ? "when-no-current-typed-checker-pass-is-retained" : "when-compiler-diagnostics-remain",
                    evidenceScope = "this-compiler-record",
                    checkSource = "accepted-task-or-host-admitted-context"
                },
                diagnostics = batch.Diagnostics.Select(row => new
                {
                    row.Code, row.Message, path = DisplayPath(row.Path, build.ProjectPath), row.Line, row.Column,
                    reportedProject = DisplayPath(row.ReportedProject, build.ProjectPath)
                }).ToArray()
            });
            var source = new ContextProviderSource(SlotId, SlotId, request.PublicationRequest.References[0].ExpectedRevision!.Value,
                ContextRequirement.Required,
                [new ModelInputMessage("user", [
                    new TextContentPart("Exact historical compiler observations from the named actual build. Source edits do not prove repair. Obtain fresh source/preimage evidence before editing; only a newer actual compiler receipt can verify the compiler correction. Address the whole related diagnostic batch before rebuilding. This evidence grants no new tools or scope. The following text is the complete compact JSON packet, schema pegasus.programming.compiler-diagnostic-batch/1. Its buildEffectId and savedEffectSha256 attribute these reference observations to the actual committed compiler receipt." +
                        "\n" + ProgrammingDevelopmentGuidance.FailedBehaviorDiagnosis +
                        "\nCompilation and behavior validation are separate. This compiler record does not establish behavior or completion authority. After a successful build, if no complete current typed checker pass is retained, use the supplied admitted host behavior check and its complete current typed receipt. If a current passing receipt is already retained, confirm it is a complete typed host receipt for the current inputs and use it without rerunning that unchanged check. Continue only the accepted task's remaining verify/present/final steps; reuse still-current verification and the existing pending presentation when applicable. If no admitted check was supplied, report the validation gap; never invent a checker or its arguments."),
                    new TextContentPart(packet.GetRawText())])], Complete: true);
            var resolution = new ContextProviderResolution(ProviderId, 1, [source]);
            // Reserve host-normalized single-window/handle overhead rather than minting a handle here.
            var cap = Math.Min(PublicationBytes, request.PublicationRequest.MaximumUtf8Bytes);
            if (JsonSerializer.SerializeToUtf8Bytes(resolution, ProgrammingEvidence.Json).Length > cap - NormalizationReserveBytes)
                return Task.FromResult(Blocked(request, "The complete compiler publication exceeds its admitted bound; no diagnostic was clipped and the last actual batch remains retained."));
            return Task.FromResult(Finish(request, state, WorkflowToolContextProjectionDisposition.Publish, resolution));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (ProgrammingEvidence.Invalid(error))
        { return Task.FromResult(Blocked(request, "Programming compiler projection refused inconsistent, unsupported or oversized evidence/state; the last actual batch remains retained.")); }
    }

    private static bool Applicable(WorkflowContextFacts facts) => facts.Identity.DefinitionId == ConversationId &&
        facts.ToolReceiptConsumer is { SchemaVersion: 1, Supported: true };
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, ProgrammingEvidence.Json);
    private static bool KnownCompilerOutcome(WorkflowEffectState state, ToolExecutionOutcome outcome, int exit) =>
        state == WorkflowEffectState.Succeeded && outcome == ToolExecutionOutcome.Succeeded && exit == 0 ||
        state == WorkflowEffectState.Failed && outcome == ToolExecutionOutcome.Failed && exit != 0;
    private static WorkflowToolContextProjectionResolution Blocked(WorkflowToolContextProjectionRequest request, string reason) =>
        new(1, request.Link, WorkflowToolContextProjectionDisposition.Blocked, null, null, reason);
    private static WorkflowToolContextProjectionResolution StateOnly(WorkflowToolContextProjectionRequest request, ProviderState state) =>
        Finish(request, state, WorkflowToolContextProjectionDisposition.StateOnly, null);
    private static WorkflowToolContextProjectionResolution Finish(WorkflowToolContextProjectionRequest request, ProviderState state,
        WorkflowToolContextProjectionDisposition disposition, ContextProviderResolution? resolution)
    {
        var next = Element(state with { LastProjectionEffectId = request.Link.ProjectionEffectId });
        if (JsonSerializer.SerializeToUtf8Bytes(next, ProgrammingEvidence.Json).Length >
            WorkflowToolContextProjectionContract.MaximumProviderStateUtf8Bytes)
            return Blocked(request, "The exact compiler provider state exceeds 8 KiB; prior state and batch remain retained.");
        var result = new WorkflowToolContextProjectionResolution(1, request.Link, disposition, next, resolution, null);
        return JsonSerializer.SerializeToUtf8Bytes(result, ProgrammingEvidence.Json).Length <=
            WorkflowToolContextProjectionContract.MaximumCommandResultUtf8Bytes ? result :
            Blocked(request, "The compiler projection result exceeds its durable bound; prior state and batch remain retained.");
    }

    private void ValidateRequest(WorkflowToolContextProjectionRequest request, CancellationToken token)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(request, ProgrammingEvidence.Json).Length >
            WorkflowToolContextProjectionContract.MaximumCallbackInputUtf8Bytes) throw new ArgumentException();
        Validate(request.Facts.Accepted, request.Facts.Accepted, request.Selection, token);
        var link = request.Link; var saved = request.SavedEffect;
        if (request.SchemaVersion != 1 || link.SchemaVersion != 1 || saved.SchemaVersion != 1 ||
            link.ContributionId != ContributionId || link.RunId != saved.RunId || link.RunId != request.Facts.Accepted.Identity.RequestId ||
            link.ToolEffectId != saved.EffectId || saved.CommandId != "cerebral.host:tool" || saved.CommandVersion != 1 ||
            saved.State is not (WorkflowEffectState.Succeeded or WorkflowEffectState.Failed or WorkflowEffectState.Cancelled) ||
            !RelevantTools.Contains(request.ToolName, StringComparer.Ordinal) ||
            link.Slot.Id != SlotId || link.Slot.CurrentSourceRevision < 1 ||
            !ProgrammingEvidence.IsHash(link.SavedEffectSha256) || link.SavedEffectSha256 != link.SavedEffectSha256.ToLowerInvariant())
            throw new ArgumentException();
        foreach (var id in new[] { link.RunId, link.ProjectionEffectId, link.ToolEffectId })
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw new ArgumentException();
        ProgrammingEvidence.Unique(saved.Arguments);
        if (saved.Arguments.ValueKind != JsonValueKind.Object ||
            ProgrammingEvidence.Text(saved.Arguments, "name") != request.ToolName ||
            !saved.Arguments.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object) throw new ArgumentException();
        var revision = checked((link.Slot.CurrentSourceRevision ?? 0) + 1);
        var expected = Template with
        {
            ContextRevision = request.Facts.ContextRevision, ExclusionRevision = request.Facts.ExclusionRevision,
            References = Array.AsReadOnly(new[] { Template.References[0] with { ExpectedRevision = revision } })
        };
        if (request.Facts.ContextRevision < 0 || request.Facts.ExclusionRevision < 0 ||
            !JsonElement.DeepEquals(Element(expected), Element(request.PublicationRequest))) throw new ArgumentException();
    }

    private static ProviderState ReadState(WorkflowToolContextProjectionRequest request)
    {
        if (request.PriorState is not { } value)
        {
            if (request.Link.PreviousProjectionEffectId is not null || request.Link.Slot.CurrentSourceRevision is not null)
                throw new ArgumentException();
            return new(1, request.Link.RunId, null, 0, null, null, "none", []);
        }
        if (JsonSerializer.SerializeToUtf8Bytes(value, ProgrammingEvidence.Json).Length >
            WorkflowToolContextProjectionContract.MaximumProviderStateUtf8Bytes) throw new ArgumentException();
        ProgrammingEvidence.Unique(value);
        var state = value.Deserialize<ProviderState>(StateJson) ?? throw new ArgumentException();
        string[] fields = ["schemaVersion", "runId", "lastProjectionEffectId", "sourceGeneration", "lastBuild", "lastMutation", "lastObservation", "observedEffects"];
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != fields.Length ||
            fields.Any(name => !value.TryGetProperty(name, out _)) || state.SchemaVersion != 1 ||
            state.RunId != request.Link.RunId || state.LastProjectionEffectId != request.Link.PreviousProjectionEffectId ||
            string.IsNullOrWhiteSpace(state.LastProjectionEffectId) || state.LastProjectionEffectId.Length > 256 ||
            state.LastProjectionEffectId.Any(char.IsControl) ||
            state.SourceGeneration < 0 || state.ObservedEffects is null || state.ObservedEffects.Count > MaximumObservedEffects ||
            state.ObservedEffects.Distinct(StringComparer.Ordinal).Count() != state.ObservedEffects.Count ||
            state.ObservedEffects.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) ||
            string.IsNullOrWhiteSpace(state.LastObservation) || state.LastObservation.Length > 64 ||
            (state.LastBuild is null) != (request.Link.Slot.CurrentSourceRevision is null)) throw new ArgumentException();
        if (state.LastBuild is not null)
            RequireFields(value.GetProperty("lastBuild"), ["effectId", "savedEffectSha256", "projectPath", "configuration", "sourceGeneration", "exitCode", "diagnosticFingerprint", "shown", "total"]);
        if (state.LastMutation is not null)
            RequireFields(value.GetProperty("lastMutation"), ["effectId", "path", "sha256"]);
        if (state.LastBuild is { } build && (!Path.IsPathFullyQualified(build.ProjectPath) ||
            build.Configuration is not ("Debug" or "Release") || build.SourceGeneration < 0 || build.SourceGeneration > state.SourceGeneration ||
            !ProgrammingEvidence.IsHash(build.SavedEffectSha256) || !state.ObservedEffects.Contains(build.EffectId, StringComparer.Ordinal) ||
            build.Shown != build.Total || build.Total is < 0 or > ProgrammingCompilerDiagnostics.MaximumDiagnostics ||
            build.ExitCode == 0 && build.Total != 0 || build.ExitCode != 0 && build.Total == 0 ||
            build.Total == 0 && build.DiagnosticFingerprint is not null ||
            build.Total > 0 && !ProgrammingEvidence.IsHash(build.DiagnosticFingerprint)))
            throw new ArgumentException();
        if (state.LastMutation is { } mutation && (!Path.IsPathFullyQualified(mutation.Path) ||
            !ProgrammingEvidence.IsHash(mutation.Sha256) || !state.ObservedEffects.Contains(mutation.EffectId, StringComparer.Ordinal)))
            throw new ArgumentException();
        return state;
    }

    private static void RequireFields(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != fields.Length ||
            fields.Any(name => !value.TryGetProperty(name, out _))) throw new ArgumentException();
    }

    private static ToolData ReadToolData(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } data) throw new ArgumentException();
        ProgrammingEvidence.Unique(data);
        if (!data.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("truncated", out var truncated) || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException();
        if (data.TryGetProperty("evidence", out var evidence) && evidence.ValueKind != JsonValueKind.Null)
        {
            if (evidence.ValueKind != JsonValueKind.Object ||
                !evidence.TryGetProperty("outcome", out var outcome) || outcome.ValueKind != JsonValueKind.String ||
                outcome.GetString() is not ("succeeded" or "failed" or "rejected" or "cancelled" or "unknown")) throw new ArgumentException();
        }
        return data.Deserialize<ToolData>(ProgrammingEvidence.Json) ?? throw new ArgumentException();
    }

    private static BuildIdentity? ReadBuildIdentity(JsonElement arguments)
    {
        if (ProgrammingEvidence.Text(arguments, "executableId") != "dotnet") return null;
        if (!arguments.TryGetProperty("arguments", out var values) || values.ValueKind != JsonValueKind.Array ||
            values.GetArrayLength() is < 1 or > 256 || values.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) throw new ArgumentException();
        var argv = values.EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (argv[0] != "build") return null;
        var cwd = ProgrammingEvidence.Text(arguments, "workingDirectory");
        if (!Path.IsPathFullyQualified(cwd)) throw new ArgumentException();
        string? project = null; var configuration = "Debug"; var explicitConfiguration = false;
        for (var i = 1; i < argv.Length; i++)
        {
            var item = argv[i];
            if (item is "-c" or "--configuration")
            {
                if (explicitConfiguration || ++i == argv.Length || argv[i] is not ("Debug" or "Release")) throw new ArgumentException();
                configuration = argv[i]; explicitConfiguration = true;
            }
            else if (item is "-v" or "--verbosity")
            { if (++i == argv.Length || argv[i] is not ("quiet" or "minimal" or "normal" or "detailed" or "diagnostic")) throw new ArgumentException(); }
            else if (item == "--project")
            { if (project is not null || ++i == argv.Length) throw new ArgumentException(); project = argv[i]; }
            else if (item is "--no-restore" or "--disable-build-servers" or "--nologo" or "-m:1") { }
            else if ((!item.StartsWith('-') && !item.StartsWith('/')) || Path.IsPathFullyQualified(item))
            { if (project is not null || string.IsNullOrWhiteSpace(item)) throw new ArgumentException(); project = item; }
            else throw new ArgumentException();
        }
        if (project is null || Path.GetExtension(project).ToLowerInvariant() is not (".csproj" or ".sln" or ".slnx")) throw new ArgumentException();
        return new(Path.GetFullPath(project, cwd), configuration);
    }
    private static string? DisplayPath(string? path, string project)
    {
        if (path is null) return null;
        var directory = Path.GetDirectoryName(project)!;
        return Path.IsPathFullyQualified(path) && ProgrammingEvidence.Within(path, directory)
            ? Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/') : path;
    }
}

