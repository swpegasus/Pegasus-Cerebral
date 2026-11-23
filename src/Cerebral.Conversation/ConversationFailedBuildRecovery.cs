using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    // Only new-run admission opts into this policy. Retain failures across target selection;
    // neither source-generation counters nor an A/B project switch erase known failed bytes.
    private sealed record FailedBuildRecoveryPolicy(int SchemaVersion, string RunId)
    {
        public IReadOnlyList<FailedBuildObservation> Failures { get; init; } = [];
        public bool ProjectionCacheDegraded { get; init; }
    }
    private sealed record CanonicalReceiptProof(string BodySha256, int BodyBytes, string ProjectionSha256,
        string? ProviderId, string? RequestId, string? JobId, bool ConfirmedFailure, bool CompleteFailure,
        KnownFileRevision? SourceRevision, int? SourceGeneration = null, string? RecipeSha256 = null, string? SourcePath = null,
        string? InputRecipeSha256 = null, string? ScopeRoot = null, string? ProjectPath = null,
        string? BeforeSha256 = null, bool Created = false, IReadOnlyList<string>? ProjectReferenceRoots = null,
        string? InputAnchorSha256 = null, int? InputAnchorReceiptCount = null, string? InputIdentitySha256 = null,
        IReadOnlyList<string>? LinkedInputPaths = null);
    private sealed record FailedBuildObservation(string EffectId, int SourceGeneration, string RecipeSha256,
        bool ConfirmedFailure, KnownFileRevision? SourceRevision, string? BodySha256, int BodyBytes,
        string? Projection, string? SourcePath, PluginBuildCandidate Build, string? InputIdentitySha256);

    private static string BuildRecipeSha256(PluginBuildCandidate build) => AdmissionArgumentHash(Element(new
    {
        project = CanonicalBuildProjectKey(build), build.ExecutableId, build.Arguments,
        workingDirectory = OperatingSystem.IsWindows() ? build.WorkingDirectory.ToUpperInvariant() : build.WorkingDirectory
    }));
    private static string EvidenceSha256(string text) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string? BuildSourcePath(PluginWorkBinding work) => work.Build.SourceFile ?? work.LastMutationPath;
    private static string CanonicalFailureProjection(WorkflowEffect effect, ToolData data) =>
        ToolModelProjection.Render(ToolModelProjection.Create(effect.Id, "process.run", effect.State.ToString(),
            data.Content, data.Truncated, data.Evidence, effect.Error));

    // This classification is only for a host-retained semantic process.run result whose exact
    // pending request matches the captured recipe. PAO support depends on the coherent host cohort
    // fencing built-in Execute/Reconcile from extension shadowing; a provider prefix is not proof.
    // Keep the provider state and raw body unchanged: a known failed process is not "completed" success.
    private static bool FailedBuildProcessIdentity(string effectId, string? provider, string? request, string? job)
    {
        if (provider == "cerebral.process") return request is null || request == effectId;
        return provider is { Length: > 4 and <= 256 } && provider.StartsWith("mcp.", StringComparison.Ordinal) &&
            !provider.Any(char.IsControl) && request == effectId && ValidEffectId(job);
    }

    private static bool CompleteCanonicalFailure(Input input, PluginBuildCandidate build,
        WorkflowEffect effect, ToolData? data)
    {
        if (effect.CommandId != Tool || effect.CommandVersion != 1 || effect.State != WorkflowEffectState.Failed ||
            data is not { Name: "process.run", Content: not null, Truncated: false,
                Evidence: { Outcome: ToolExecutionOutcome.Failed, Partial: not true, ExitCode: not null } evidence } ||
            evidence.ExitCode == 0 || !FailedBuildProcessIdentity(effect.Id, evidence.ProviderId, evidence.RequestId, evidence.JobId) ||
            effect.Arguments.ValueKind != JsonValueKind.Object ||
            !effect.Arguments.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            name.GetString() != "process.run" || !effect.Arguments.TryGetProperty("arguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Object || !MatchesBuild(input, build, arguments) ||
            Encoding.UTF8.GetByteCount(data.Content) > MaximumBindingPayloadBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 8 });
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object) return false;
            UniqueProperties(body);
            if (evidence.ProviderId == "cerebral.process")
                return evidence.State == "completed" &&
                    body.TryGetProperty("exitCode", out var exit) && exit.TryGetInt32(out var code) && code == evidence.ExitCode &&
                    body.TryGetProperty("stdout", out var stdout) && stdout.ValueKind == JsonValueKind.String &&
                    body.TryGetProperty("stderr", out var stderr) && stderr.ValueKind == JsonValueKind.String;

            // The original PAO command receipt, not a later status wrapper or retrieved JSON,
            // must independently join the effect/RID and provider receipt/JID at this boundary.
            if (evidence.State != "failed" || evidence.Partial != false || evidence.JobId != effect.ProviderReceiptId ||
                !build.ExecutableId.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || build.Arguments.Count == 0)
                return false;
            var operation = build.Arguments[0] switch { "build" => "dotnet_build", "test" => "dotnet_test", _ => null };
            static bool Text(JsonElement value, string field, string? expected) =>
                expected is not null && value.TryGetProperty(field, out var item) &&
                item.ValueKind == JsonValueKind.String && item.GetString() == expected;
            static bool False(JsonElement value, string field) =>
                value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.False;
            static bool Unique(JsonElement value) =>
                value.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                value.EnumerateObject().Count();
            if (!Unique(body) || !False(body, "ok") || !False(body, "partial") ||
                !Text(body, "state", evidence.State) || !Text(body, "err", "external_failed") ||
                !Text(body, "op", operation) || !Text(body, "rid", effect.Id) || !Text(body, "jid", evidence.JobId) ||
                !body.TryGetProperty("data", out var process) || process.ValueKind != JsonValueKind.Object || !Unique(process))
                return false;
            return process.TryGetProperty("exit", out var processExit) && processExit.TryGetInt32(out var actualExit) &&
                actualExit != 0 && actualExit == evidence.ExitCode &&
                process.TryGetProperty("out", out var output) && output.ValueKind == JsonValueKind.String &&
                process.TryGetProperty("err_tail", out var error) && error.ValueKind == JsonValueKind.String &&
                False(process, "out_truncated") && False(process, "err_truncated") && False(process, "timed_out");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }
    private static bool CompleteSourceRevision(string content, string effectId, out KnownFileRevision revision)
    {
        revision = null!;
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            UniqueProperties(document.RootElement);
            return TryReadFileRevision(content, effectId, out revision);
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }

    private static bool CompleteCopiedRevision(string content, string effectId, out KnownFileRevision revision)
    {
        revision = null!;
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 8 });
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object) return false;
            UniqueProperties(body);
            if (!TryText(body, "destinationPath", MaximumBindingPathCharacters, out var destination) ||
                !TryCanonicalAbsolutePath(destination, out var path) || !TryText(body, "sha256", 64, out var hash) ||
                !ValidPackageFingerprint(hash) || !body.TryGetProperty("bytes", out var bytes) || !bytes.TryGetInt64(out var count) || count < 0) return false;
            revision = new(path, hash.ToLowerInvariant(), effectId);
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }

    private static CanonicalReceiptProof? FailedBuildReceiptProof(Input input, HarnessState state,
        WorkflowEffect effect, ToolData? data)
    {
        if (state.FailedBuildRecovery is null || state.PluginWork is not { } work) return null;
        var arguments = PendingToolArguments(state);
        if (state.PendingName == "process.run" && arguments is { } buildArgs && MatchesBuild(input, work.Build, buildArgs))
        {
            var inputs = ReadCanonicalInputs(state, work.Build);
            var projection = data is null ? "" : CanonicalFailureProjection(effect, data);
            var confirmed = CompleteCanonicalFailure(input, work.Build, effect, data);
            return new(EvidenceSha256(data?.Content ?? ""), Encoding.UTF8.GetByteCount(data?.Content ?? ""), EvidenceSha256(projection),
                data?.Evidence?.ProviderId, data?.Evidence?.RequestId, data?.Evidence?.JobId, confirmed, confirmed,
                CanonicalPrimarySource(inputs, BuildSourcePath(work)), work.SourceGeneration,
                BuildRecipeSha256(work.Build), BuildSourcePath(work), InputAnchorSha256: inputs.AnchorSha256,
                InputAnchorReceiptCount: inputs.AnchorReceiptCount, InputIdentitySha256: CanonicalInputIdentity(inputs));
        }
        if (data is null || Encoding.UTF8.GetByteCount(data.Content) > MaximumBindingPayloadBytes || arguments is not { } fileArgs) return null;
        var path = state.PendingName is "file.read" or "file.inspect" && TryActionPath(input, fileArgs, "path", out var readPath)
            ? readPath : MutationPath(input, state.PendingName, fileArgs);
        var scopeRoot = EvidenceWorkspaceRoot(input, work.Build);
        var related = path is not null && RelatedCanonicalInput(input, state, work.Build, path);
        if (path is null || !PathWithin(path, scopeRoot) || !related && !PathWithin(path, work.Build.Root)) return null;
        KnownFileRevision? sourceRevision = null;
        var successful = SuccessfulEffect(effect, data) && !data.Truncated &&
            (data.Evidence?.RequestId is null || data.Evidence?.RequestId == effect.Id);
        // Stock file producers omit explicit Evidence. The matched successful effect and
        // complete typed path/hash result are their actual provenance contract.
        if (state.PendingName is "file.read" or "file.inspect" or "file.write" or "file.edit" or "file.append" &&
            successful && CompleteSourceRevision(data.Content, effect.Id, out var revision) && PathEquals(revision.Path, path))
            sourceRevision = revision;
        if (state.PendingName == "file.copy" && successful && CompleteCopiedRevision(data.Content, effect.Id, out var copied) &&
            PathEquals(copied.Path, path)) sourceRevision = copied;
        string? before = null;
        if (successful && state.PendingName is "file.write" or "file.edit" or "file.append" &&
            TryText(fileArgs, "expectedSha256", 64, out var expected) && ValidPackageFingerprint(expected)) before = expected.ToLowerInvariant();
        var created = successful && (state.PendingName == "file.copy" || state.PendingName == "file.write" &&
            (!fileArgs.TryGetProperty("collision", out var collision) || collision.ValueKind == JsonValueKind.String && collision.GetString() == "fail"));
        var references = successful && state.PendingName == "file.read" && PathEquals(path, work.Build.ProjectFile)
            ? ReadLiteralBuildInputs(work.Build, data, scopeRoot) : null;
        return new(EvidenceSha256(data.Content), Encoding.UTF8.GetByteCount(data.Content), EvidenceSha256(""),
            data.Evidence?.ProviderId, data.Evidence?.RequestId, data.Evidence?.JobId, false, false,
            sourceRevision, SourcePath: path, InputRecipeSha256: related ? BuildRecipeSha256(work.Build) : null, ScopeRoot: scopeRoot,
            ProjectPath: work.Build.ProjectFile, BeforeSha256: before, Created: created, ProjectReferenceRoots: references?.ReferenceRoots,
            LinkedInputPaths: references?.Files);
    }

    private static PluginWorkBinding ObserveFailedBuildRecovery(Input input, HarnessState state,
        PluginWorkBinding work, JsonElement arguments, ToolData? data, WorkflowEffect effect)
    {
        if (state.FailedBuildRecovery is null) return work;
        var proof = FailedBuildReceiptProof(input, state, effect, data);
        if (proof?.SourcePath is { } path && BuildSourcePath(work) is { } source && PathEquals(path, source) &&
            state.PendingName != "process.run") work = work with { BuildSourceRevision = proof.SourceRevision };
        if (state.PendingName != "process.run" || !MatchesBuild(input, work.Build, arguments) ||
            data?.Evidence?.Outcome == ToolExecutionOutcome.Rejected) return work;
        if (SuccessfulEffect(effect, data) && data?.Evidence?.ExitCode == 0)
            return work with { FailedBuildObservation = null };
        return work with { FailedBuildObservation = new(effect.Id, work.SourceGeneration, BuildRecipeSha256(work.Build),
            CompleteCanonicalFailure(input, work.Build, effect, data), proof?.SourceRevision, proof?.BodySha256, proof?.BodyBytes ?? 0,
            proof?.CompleteFailure == true && ProjectionComplete(CanonicalFailureProjection(effect, data!))
                ? CanonicalFailureProjection(effect, data!) : null, BuildSourcePath(work), work.Build, proof?.InputIdentitySha256) };
    }

    private static WorkflowStep? RetainFailedBuildEvidence(ref HarnessState state, string effectId)
    {
        if (state.FailedBuildRecovery is not { } policy || state.PluginWork?.FailedBuildObservation is not { } failure ||
            failure.EffectId != effectId || policy.Failures.Any(item => item.EffectId == effectId)) return null;
        if (policy.Failures.Count >= MaximumLookups)
        {
            if (!policy.ProjectionCacheDegraded)
            {
                state = state with { FailedBuildRecovery = policy with { ProjectionCacheDegraded = true } };
                state = Append(state, "failed-build-cache-capacity", "failed-build-recovery", "user",
                    "The retained compiler-projection cache is full. Original receipt metadata remains authoritative. " +
                    "Further failure feedback may omit its diagnostic and point to the exact raw receipt; " +
                    "this does not restrict editable files, build targets or ordinary workflow resource bounds.",
                    ContextRequirement.Required, effectId);
            }
            return null;
        }
        state = state with { FailedBuildRecovery = policy with { Failures = [.. policy.Failures, failure] } };
        return null;
    }

    private static WorkflowStep? GuardFailedCanonicalBuild(Input input, HarnessState state, string name, JsonElement arguments)
    {
        if (state.FailedBuildRecovery is not { } policy || name != "process.run") return null;
        var candidates = policy.Failures.Select(item => item.Build)
            .Concat(state.PluginBuildCandidates.Where(candidate => candidate.Origin != "discovery"))
            .Concat(state.PluginWork is { } selectedWork ? [selectedWork.Build] : Array.Empty<PluginBuildCandidate>());
        var build = candidates.FirstOrDefault(candidate => MatchesBuild(input, candidate, arguments));
        if (build is null) return null;
        var recipe = BuildRecipeSha256(build);
        var receipts = state.ToolReceipts.Where(receipt => receipt.Name == "process.run" &&
            receipt.CanonicalProof?.RecipeSha256 == recipe && receipt.State == "Failed" && receipt.Outcome != "Rejected").ToArray();
        if (receipts.Length == 0) return null;
        // This receipt-derived check remains active even if the projection cache fills.
        if (receipts.FirstOrDefault(receipt => !ReusableFailedBuildReceipt(receipt)) is { } uncertain)
            return Finish(state, false, "The previous canonical build has incomplete or uncertain execution evidence in receipt " +
                uncertain.EffectId + ". No build was replayed. Reconcile that exact receipt before starting another assignment.");
        var inputs = ReadCanonicalInputs(state, build);
        var identity = CanonicalInputIdentity(inputs);
        var path = build.SourceFile ?? policy.Failures.LastOrDefault(item => item.RecipeSha256 == recipe)?.SourcePath;
        var current = CanonicalPrimarySource(inputs, path);
        var completeInputs = inputs.Files.Count > 0 && inputs.Files.All(file => file.Current is not null);
        // Content identity precedes generation. Project/other-source repairs change the
        // normalized input deltas; A -> B -> A and project switching return to the old identity.
        var originalReceipt = receipts.LastOrDefault(receipt => receipt.CanonicalProof!.InputIdentitySha256 == identity);
        if (originalReceipt is null && completeInputs) return null;
        originalReceipt ??= receipts[^1];
        var proof = originalReceipt.CanonicalProof!;
        var retained = policy.Failures.LastOrDefault(item => item.EffectId == originalReceipt.EffectId);
        var missingSourceProof = !completeInputs || current is null || proof.SourceRevision is null;
        var recovery = state.ReadRecovery!;
        if (recovery.Reuses >= recovery.ReuseLimit)
            return Finish(state, false, "The retained-evidence recovery window is exhausted without new evidence or verified work progress. " +
                "No canonical build was dispatched; failed receipt " + originalReceipt.EffectId + " remains preserved.");
        var (updated, uses) = IncrementRetainedEvidenceUse(recovery, originalReceipt.EffectId);
        var status = JsonSerializer.Serialize(new
        {
            kind = "retained-failed-build-reuse", receipt = originalReceipt.EffectId, outcome = "Failed",
            freshExecution = false, toolDispatched = false, rawResultComplete = true,
            sourceProofMissing = missingSourceProof, requiredSourcePath = path,
            requiredInputPaths = inputs.Files.Where(file => file.Current is null).Take(MaximumLookups).Select(file => file.Path).ToArray(),
            additionalInputPathsOmitted = inputs.Files.Count(file => file.Current is null) > MaximumLookups,
            sourceSha256 = proof.SourceRevision?.Sha256, currentSourceSha256 = current?.Sha256,
            inputIdentitySha256 = identity, originalInputIdentitySha256 = proof.InputIdentitySha256,
            recipeSha256 = recipe, diagnosticOmitted = retained?.Projection is null,
            reuse = updated.Reuses, sameEvidenceUses = uses, remaining = updated.ReuseLimit - updated.Reuses
        }, Json);
        var evidenceId = "known-build-failure." + originalReceipt.EffectId;
        var next = state with { ReadRecovery = updated, PendingPluginTargetAction = null,
            Contributions = state.Contributions.Select(source => source.Id == evidenceId
                ? source with { Requirement = ContextRequirement.Required } : source).ToArray() };
        if (!next.Contributions.Any(source => source.Id == evidenceId))
        {
            var projection = retained?.Projection ?? JsonSerializer.Serialize(new
            {
                tool = "process.run", receipt = originalReceipt.EffectId, outcome = "Failed", originalReceipt.ExitCode,
                rawBodySha256 = proof.BodySha256, rawBodyBytes = proof.BodyBytes, diagnosticOmitted = true,
                message = "The original complete raw receipt retains the compiler diagnostic. Inspect that exact receipt before repair."
            }, Json);
            next = Append(next, evidenceId, "failed-build-evidence", "user", projection, ContextRequirement.Required, originalReceipt.EffectId);
        }
        var correction = missingSourceProof
            ? "The compiler failure is complete, but a precise source/input fingerprint is unavailable or current inputs are unproved. " +
              "Read the exact requiredSourcePath and requiredInputPaths for their complete path/SHA receipts. " +
              "A later read does not retroactively prove the original failed bytes."
            : "This exact canonical recipe already failed for these observed input bytes. Correct the source/project/dependency using the retained diagnostic.";
        next = Append(next, "failed-build-reuse." + next.ContextRevision + "." + updated.Reuses, "failed-build-recovery", "user",
            status + "\n" + correction + " The exact raw result remains in the original receipt. " +
            "Obtain materially missing evidence or return blocked with the exact precondition. " +
            "Argument aliases, no-op edits, source reverts and project switching are not a new compiler observation.",
            ContextRequirement.Required, originalReceipt.EffectId);
        return RequestModel(input, next);
    }

    private static bool RecordedRelatedMutation(HarnessState state, PluginWorkBinding work, string path) =>
        state.FailedBuildRecovery is not null && state.ToolReceipts.Any(receipt =>
            receipt.Name is "file.write" or "file.edit" or "file.append" or "file.copy" &&
            receipt is { State: "Succeeded", Outcome: null or "Succeeded", Truncated: false } &&
            receipt.CanonicalProof is { InputRecipeSha256: { } recipe, ScopeRoot: { } scope, SourceRevision: { } revision } &&
            recipe == BuildRecipeSha256(work.Build) && PathEquals(revision.Path, path) && PathWithin(path, scope));

    // Existing CompleteFailure is set only by the strict capture classifier above. Reuse/reopen
    // validate the same immutable identity/proof, never reinterpret an old incomplete receipt.
    private static bool KnownFailedBuildReceipt(ToolReceipt receipt) => receipt is
        { Name: "process.run", State: "Failed", Outcome: "Failed", Truncated: false, ExitCode: not null,
            CanonicalProof: { ConfirmedFailure: true } proof } && receipt.ExitCode != 0 &&
        FailedBuildProcessIdentity(receipt.EffectId, proof.ProviderId, proof.RequestId, proof.JobId);
    private static bool ReusableFailedBuildReceipt(ToolReceipt receipt) =>
        KnownFailedBuildReceipt(receipt) && receipt.CanonicalProof!.CompleteFailure;

    private static void ValidateFailedBuildObservation(HarnessState state, FailedBuildObservation observation)
    {
        if (observation.Build is null) throw new ArgumentException("The failed build recipe is missing.");
        ValidateBoundCandidate(observation.Build);
        var receipt = state.ToolReceipts.LastOrDefault(item => item.EffectId == observation.EffectId && item.Name == "process.run");
        var proof = receipt?.CanonicalProof;
        if (receipt is null || observation.ConfirmedFailure != (proof?.ConfirmedFailure == true) ||
            observation.SourceGeneration < 0 || observation.SourceGeneration > MaximumSourceGeneration ||
            !ValidPackageFingerprint(observation.RecipeSha256) || observation.Build.Origin == "discovery" ||
            observation.RecipeSha256 != BuildRecipeSha256(observation.Build) ||
            observation.SourcePath is not null && ((!PathWithin(observation.SourcePath, observation.Build.Root) &&
                !RecordedRelatedMutation(state, new(observation.Build), observation.SourcePath)) ||
                observation.Build.SourceFile is { } sourcePath && !PathEquals(sourcePath, observation.SourcePath)) ||
            proof is not null && (observation.RecipeSha256 != proof.RecipeSha256 || observation.SourceGeneration != proof.SourceGeneration ||
                observation.SourceRevision != proof.SourceRevision || observation.SourcePath != proof.SourcePath ||
                observation.InputIdentitySha256 != proof.InputIdentitySha256) ||
            observation.BodySha256 != proof?.BodySha256 || observation.BodyBytes != (proof?.BodyBytes ?? 0) ||
            observation.SourcePath is not null && !CanonicalStoredPath(observation.SourcePath) ||
            observation.Projection is { } projection && (!ProjectionComplete(projection) ||
                proof is not { CompleteFailure: true } || proof.ProjectionSha256 != EvidenceSha256(projection)) ||
            observation.Projection is not null && !observation.ConfirmedFailure)
            throw new ArgumentException("The saved failed canonical build does not match its original receipt, source or recipe.");
    }

    private static void ValidateFailedBuildRecovery(HarnessState state, string runId)
    {
        var policy = state.FailedBuildRecovery;
        if (policy is not null && (policy.SchemaVersion != 1 || policy.RunId != runId || state.ReadRecovery is null ||
            policy.Failures is null || policy.Failures.Count > MaximumLookups || policy.Failures.Any(item => item is null) ||
            policy.Failures.Select(item => item.EffectId).Distinct(StringComparer.Ordinal).Count() != policy.Failures.Count))
            throw new ArgumentException("The saved failed-build recovery policy is invalid or belongs to another run.");
        foreach (var receipt in state.ToolReceipts)
        {
            if (receipt.CanonicalProof is not { } proof) continue;
            if (policy is null || !ValidPackageFingerprint(proof.BodySha256) || proof.BodyBytes < 0 ||
                proof.BodyBytes > WorkflowContract.MaximumStateBytes || !ValidPackageFingerprint(proof.ProjectionSha256) ||
                proof.SourceGeneration is < 0 or > MaximumSourceGeneration ||
                (proof.SourceGeneration is null) != (proof.RecipeSha256 is null) ||
                proof.RecipeSha256 is not null && !ValidPackageFingerprint(proof.RecipeSha256) ||
                proof.SourcePath is not null && !CanonicalStoredPath(proof.SourcePath) ||
                proof.RequestId is not null && proof.RequestId != receipt.EffectId ||
                proof.JobId is not null && !ValidEffectId(proof.JobId) ||
                proof.ProviderId is { } provider && (provider.Length > 256 || provider.Any(char.IsControl)))
                throw new ArgumentException("A canonical receipt proof has invalid provenance or bounds.");
            if (proof.RecipeSha256 is not null && (receipt.Name != "process.run" ||
                !ValidPackageFingerprint(proof.InputAnchorSha256) || !ValidPackageFingerprint(proof.InputIdentitySha256) ||
                proof.InputAnchorReceiptCount is null or < 0 || proof.InputAnchorReceiptCount > state.ToolReceipts.ToList().IndexOf(receipt)))
                throw new ArgumentException("A canonical-input receipt anchor is invalid.");
            if (proof.InputRecipeSha256 is not null && (!ValidPackageFingerprint(proof.InputRecipeSha256) ||
                !CanonicalStoredPath(proof.ScopeRoot) || !CanonicalStoredPath(proof.ProjectPath) ||
                !PathWithin(proof.ProjectPath!, proof.ScopeRoot!) || proof.SourcePath is null || !PathWithin(proof.SourcePath, proof.ScopeRoot!)))
                throw new ArgumentException("A related input proof has invalid project scope.");
            if (proof.BeforeSha256 is not null && (!ValidPackageFingerprint(proof.BeforeSha256) ||
                receipt.Name is not ("file.write" or "file.edit" or "file.append")))
                throw new ArgumentException("An input preimage has invalid provenance.");
            if (proof.Created && (receipt.State != "Succeeded" || receipt.Outcome is not (null or "Succeeded") ||
                receipt.Name is not ("file.write" or "file.copy")))
                throw new ArgumentException("An input creation proof is invalid.");
            if (proof.ProjectReferenceRoots is { } roots && (proof.InputRecipeSha256 is null || proof.SourceRevision is null ||
                receipt.Name != "file.read" || proof.SourcePath is null || proof.ProjectPath is null || !PathEquals(proof.SourcePath, proof.ProjectPath) ||
                roots.Any(root => !CanonicalStoredPath(root) || !PathWithin(root, proof.ScopeRoot!))))
                throw new ArgumentException("Local project-reference evidence has invalid scope.");
            if (proof.LinkedInputPaths is { } links && (proof.ProjectReferenceRoots is null || proof.ScopeRoot is null ||
                links.Any(path => !CanonicalStoredPath(path) || !PathWithin(path, proof.ScopeRoot))))
                throw new ArgumentException("Literal linked compile evidence has invalid scope.");
            if (proof.SourceRevision is { } source)
            {
                var sourceReceipt = state.ToolReceipts.LastOrDefault(item => item.EffectId == source.EffectId);
                if (!CanonicalStoredPath(source.Path) || !ValidPackageFingerprint(source.Sha256) ||
                    sourceReceipt is not { State: "Succeeded", Outcome: null or "Succeeded", Truncated: false } ||
                    sourceReceipt.Name is not ("file.read" or "file.inspect" or "file.write" or "file.edit" or "file.append" or "file.copy") ||
                    sourceReceipt.CanonicalProof?.SourceRevision != source || proof.SourcePath is null || !PathEquals(source.Path, proof.SourcePath))
                    throw new ArgumentException("A canonical source fingerprint has no exact successful receipt.");
            }
            if (proof.ConfirmedFailure && !KnownFailedBuildReceipt(receipt) ||
                proof.CompleteFailure && (!proof.ConfirmedFailure || !ReusableFailedBuildReceipt(receipt) ||
                    proof.SourceGeneration is null || !ValidPackageFingerprint(proof.RecipeSha256)))
                throw new ArgumentException("A complete failed-build receipt is inconsistent.");
        }
        if (policy is not null) foreach (var observation in policy.Failures) ValidateFailedBuildObservation(state, observation);
        if (state.PluginWork is not { } work) return;
        if (work.BuildSourceRevision is { } current && (policy is null || BuildSourcePath(work) is not { } path ||
            !PathEquals(current.Path, path) || !state.ToolReceipts.Any(receipt => receipt.EffectId == current.EffectId && receipt.CanonicalProof?.SourceRevision == current)))
            throw new ArgumentException("The selected build source fingerprint has invalid provenance.");
        if (work.FailedBuildObservation is { } selected)
        {
            if (policy is null || selected.RecipeSha256 != BuildRecipeSha256(work.Build))
                throw new ArgumentException("The selected failed build has no matching admitted policy or recipe.");
            ValidateFailedBuildObservation(state, selected);
        }
    }
}
