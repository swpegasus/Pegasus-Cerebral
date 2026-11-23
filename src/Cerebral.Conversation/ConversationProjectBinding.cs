using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const int MaximumPluginBuildCandidates = 8;
    private const int MaximumPluginDiscoveryRoots = 50;
    private const int MaximumKnownFileRevisions = 4;
    private const int MaximumBindingPathCharacters = 2048;
    private const int MaximumBuildArguments = 16;
    private const int MaximumBuildArgumentCharacters = 4096;
    private const int MaximumBindingPayloadBytes = 128 * 1024;
    private const int MaximumBindingEffectIdCharacters = 256;
    private const int MaximumSourceGeneration = 1_000_000;
    private const string InventoryDesktopAuthoringOrigin = "inventory.desktop-authoring-v1";
    private const string ScaffoldDesktopAuthoringOrigin = "scaffold.desktop-authoring-v1";

    private enum MutationDisposition { None, Actual, InvalidateOnly, KnownNoOp }

    private static HarnessState SelectPluginWork(Input input, HarnessState state, string name, JsonElement arguments)
    {
        if (state.PluginBuildCandidates.Count == 0) return state;
        var targets = ToolTargetPaths(input, name, arguments);
        if (targets.Count == 0) return state;

        var inspected = state.PluginBuildCandidates.Where(candidate => candidate.Origin != "discovery").ToArray();
        var direct = inspected.Where(candidate => targets.Any(path =>
            PathWithin(path, candidate.WorkingDirectory) || PathEquals(path, candidate.ProjectFile) ||
            PathEquals(path, candidate.BuiltManifest))).ToArray();
        var matches = direct.Length > 0
            ? direct
            : inspected.Where(candidate => targets.Any(path => PathWithin(path, candidate.Root))).ToArray();
        var distinct = matches.Distinct(PluginBuildCandidateComparer.Instance).ToArray();
        if (distinct.Length != 1) return state;
        var selected = distinct[0];
        if (state.PluginWork is { } current && SameBuild(current.Build, selected)) return state;
        return state with { PluginWork = new(selected) };
    }

    private static string? ValidatePluginAction(Input input, HarnessState state, string name, JsonElement arguments,
        string? runId = null)
    {
        if (name == "interface.present" && arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String &&
            string.Equals(kind.GetString(), "plugin", StringComparison.Ordinal))
            return "Plugin packages in Conversation must use plugin.present with the same path so the retained build and " +
                "verification are checked. interface.present(kind=plugin) is a compatibility route outside the " +
                "current-generation binding and is not dispatched here. No presentation was dispatched.";

        if (input.UseOwnerVerificationCompletion && name is ("plugin.verify" or "plugin.present") &&
            !HasCurrentOwnerCheck(input, state))
            return OwnerCheckDiagnostic(input);

        if (QaSourcePrerequisiteDiagnostic(input, state, name, arguments) is { } qaSourceDiagnostic)
            return qaSourceDiagnostic;

        // Broad discovery is a source index, not a selected build. Retain every bounded root
        // independently of the eight-recipe cache so later projects cannot bypass inspection.
        var targetPaths = ToolTargetPaths(input, name, arguments);
        var needsInspection = state.PluginDiscoveryRoots
            .Where(root => targetPaths.Any(path => PathWithin(path, root)) &&
                !state.PluginBuildCandidates.Any(candidate => candidate.Origin != "discovery" && PathEquals(root, candidate.Root)))
            .OrderByDescending(root => root.Length).FirstOrDefault();
        if (needsInspection is not null)
            return "This action targets a discovered plugin whose exact build recipe has not been selected. " +
                "Call plugin.projects with this exact root before reading, generating, editing, building, verifying, or presenting it: " +
                JsonSerializer.Serialize(new { root = needsInspection }, Json) +
                ". Discovery did not authorize an action, and no requested action was dispatched.";

        if (state.PluginWork is not { } work)
        {
            var ambiguousRoot = state.PluginBuildCandidates.Where(candidate => candidate.Origin != "discovery" &&
                    targetPaths.Any(path => PathWithin(path, candidate.Root)))
                .Select(candidate => candidate.Root).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .FirstOrDefault();
            if (ambiguousRoot is not null && name is not ("file.read" or "file.inspect" or "file.list" or "file.search"))
                return "The inspected plugin root has multiple components and this authoring action selects none. " +
                    "Use an exact returned component source/project coordinate so its build and source generation can be retained. " +
                    "No action was dispatched for root " + JsonSerializer.Serialize(ambiguousRoot, Json) + ".";
            return null;
        }
        var build = work.Build;
        if (PluginManifestFilePresentationDiagnostic(input, work, name, arguments) is { } presentationDiagnostic)
            return presentationDiagnostic;
        var mutation = MutationPath(input, name, arguments);
        if (build.Component == "desktop" && mutation is not null &&
            PathWithin(mutation, build.WorkingDirectory) &&
            !HasDesktopAuthoringContract(build))
            return "The selected generated desktop project has no retained host desktop-authoring contract. " +
                "Call plugin.projects with its exact root again before changing desktop source: " +
                JsonSerializer.Serialize(new { root = build.Root }, Json) + ". No source mutation was dispatched.";
        if (FailedPresentationRetestDiagnostic(state, work, name, arguments, runId) is { } failedPresentationDiagnostic)
            return failedPresentationDiagnostic;
        if (name == "process.run" && TargetsPluginBuild(input, build, arguments) && !MatchesBuild(input, build, arguments))
            return "The command targets the selected plugin but does not match its host-retained Release build envelope. " +
                "Use this exact process.run request; no command was dispatched: " + JsonSerializer.Serialize(new
                {
                    executableId = build.ExecutableId,
                    arguments = build.Arguments,
                    workingDirectory = build.WorkingDirectory
                }, Json) + ". A different explicit project, working directory, executable, configuration, or build option is not redirected.";

        if (name == "plugin.verify" && TargetsPathArgument(input, arguments, "root", build.Root))
        {
            if (!TryActionPath(input, arguments, "root", out var root) || !PathEquals(root, build.Root))
                return "plugin.verify for the selected plugin requires its exact host-retained project root: " +
                    JsonSerializer.Serialize(build.Root, Json) + ". No verification was dispatched.";
            if (work.SuccessfulBuildGeneration != work.SourceGeneration || work.BuildEffectId is null)
                return "The selected plugin source has no successful canonical Release build for generation " +
                    work.SourceGeneration + ". Run the exact retained process.run build envelope before plugin.verify.";
        }

        if (name == "plugin.present" && TargetsPathArgument(input, arguments, "path", build.Root))
        {
            if (build.Component != "desktop")
                return "The selected plugin component is not a desktop package and has no plugin.present target.";
            if (!TryActionPath(input, arguments, "path", out var path) || !PathEquals(path, build.BuiltManifest))
                return "plugin.present for the selected plugin requires its exact host-retained built desktop manifest: " +
                    JsonSerializer.Serialize(build.BuiltManifest, Json) + ". No presentation was dispatched.";
            if (work.VerifiedGeneration != work.SourceGeneration || work.VerifyEffectId is null)
                return "The selected plugin package has not been verified for current source generation " +
                    work.SourceGeneration + ". Call plugin.verify on the exact retained root before plugin.present.";
            if (work.VerifiedPackageFingerprint is not null &&
                (!TryText(arguments, "expectedPackageFingerprint", 64, out var expectedFingerprint) ||
                 !string.Equals(expectedFingerprint, work.VerifiedPackageFingerprint, StringComparison.OrdinalIgnoreCase)))
                return "plugin.present requires expectedPackageFingerprint=" + work.VerifiedPackageFingerprint +
                    " from the retained SDK verification. This is a preimage condition checked against current package bytes, not an authorization grant. " +
                    "No presentation was dispatched.";
            if (state.NativeControlFailures.Any(item => item.SourceManifest is not null &&
                PathEquals(item.SourceManifest, build.BuiltManifest) &&
                (item.PackageFingerprint is null || work.VerifiedPackageFingerprint is null ||
                 string.Equals(item.PackageFingerprint, work.VerifiedPackageFingerprint, StringComparison.OrdinalIgnoreCase))))
                return "The replacement has no verified package change from the installed generation with unresolved native-control failures. " +
                    "An edit followed by a revert, a rebuild, or a new presentation ID is not a repair. Repair the requested behavior in the existing source, " +
                    "build and verify it, then present a different package for owner review. If the failed package has no retained fingerprint, report that evidence gap; " +
                    "do not claim replacement or a functional pass. No presentation was dispatched.";
        }
        return null;
    }
    private static string? PluginManifestFilePresentationDiagnostic(Input input, PluginWorkBinding work,
        string name, JsonElement arguments)
    {
        var build = work.Build;
        if (name != "interface.present" || build.Component != "desktop" ||
            arguments.ValueKind != JsonValueKind.Object ||
            !TryText(arguments, "kind", 16, out var kind) || kind != "file" ||
            !TryActionPath(input, arguments, "path", out var path) ||
            !PathEquals(path, build.BuiltManifest)) return null;

        const string prefix = "The selected desktop plugin's built manifest cannot be presented as an ordinary file. ";
        if (work.SuccessfulBuildGeneration != work.SourceGeneration || work.BuildEffectId is null)
            return prefix + "Build current source generation " + work.SourceGeneration +
                " with this exact retained process.run request: " + JsonSerializer.Serialize(new
                {
                    executableId = build.ExecutableId,
                    arguments = build.Arguments,
                    workingDirectory = build.WorkingDirectory
                }, Json) + ". No file presentation was dispatched.";

        if (work.VerifiedGeneration != work.SourceGeneration || work.VerifyEffectId is null)
            return prefix + "Verify the current Release build with this exact plugin.verify request: " +
                JsonSerializer.Serialize(new { root = build.Root }, Json) +
                ". No file presentation was dispatched.";

        if (work.PresentedGeneration == work.SourceGeneration && work.PresentEffectId is not null)
            return prefix + "The current verified package already completed plugin.present for source generation " +
                work.SourceGeneration + "; retain presentation receipt " +
                JsonSerializer.Serialize(work.PresentEffectId, Json) +
                " and await owner review. No file presentation was dispatched.";

        var present = new Dictionary<string, object?> { ["path"] = build.BuiltManifest };
        if (work.VerifiedPackageFingerprint is not null)
            present["expectedPackageFingerprint"] = work.VerifiedPackageFingerprint;
        return prefix + "Queue the current verified package for owner review with this exact plugin.present request: " +
            JsonSerializer.Serialize(present, Json) + ". No file presentation was dispatched.";
    }

    private static string? FailedPresentationRetestDiagnostic(HarnessState state, PluginWorkBinding work,
        string name, JsonElement arguments, string? runId)
    {
        if (name != "plugin.controls" || work.SourceGeneration <= 0 || arguments.ValueKind != JsonValueKind.Object ||
            !TryText(arguments, "operation", 16, out var operation) || operation is not ("invoke" or "reopen" or "setText") ||
            !TryText(arguments, "presentationId", 32, out var presentation)) return null;

        if (string.Equals(presentation, PluginControlReferences.ReviewedReference, StringComparison.Ordinal) &&
            !PluginControlReferences.TryResolveReviewedPresentationId(runId, out presentation)) return null;
        var failure = state.NativeControlFailures.FirstOrDefault(item =>
            item.SourceManifest is not null && PathEquals(item.SourceManifest, work.Build.BuiltManifest) &&
            string.Equals(item.PresentationId, presentation, StringComparison.Ordinal));
        if (failure is null) return null;
        // A mutation generation can already be the installed bytes under test. When complete SDK
        // verification identifies those exact bytes, keep diagnostic retests available; a later
        // mutation clears verification and a different fingerprint remains replacement work.
        if (work.VerifiedGeneration == work.SourceGeneration &&
            ValidPackageFingerprint(work.VerifiedPackageFingerprint) &&
            ValidPackageFingerprint(failure.PackageFingerprint) &&
            string.Equals(work.VerifiedPackageFingerprint, failure.PackageFingerprint,
                StringComparison.OrdinalIgnoreCase)) return null;

        var prefix = "The selected plugin source changed after the retained native-control failure on this installed presentation. " +
            operation + " against that failed presentation is not a test of the changed source and was not dispatched. ";
        if (work.PresentedGeneration == work.SourceGeneration && work.PresentEffectId is not null)
            return prefix + "A current verified replacement is already queued for separate owner review. " +
                "Finish this turn truthfully pending owner review instead of continuing controls on the old presentation. " +
                "Its controls require the new owner-approved automatic QA run; this run's reviewed alias still refers to the old approval.";

        if (work.VerifiedGeneration == work.SourceGeneration && work.VerifyEffectId is not null)
        {
            var present = new Dictionary<string, object?> { ["path"] = work.Build.BuiltManifest };
            if (work.VerifiedPackageFingerprint is not null)
                present["expectedPackageFingerprint"] = work.VerifiedPackageFingerprint;
            return prefix + "Queue the current verified replacement for separate owner review with this exact plugin.present request: " +
                JsonSerializer.Serialize(present, Json) + ".";
        }

        if (work.SuccessfulBuildGeneration == work.SourceGeneration && work.BuildEffectId is not null)
            return prefix + "Verify the current Release build before presentation with this exact plugin.verify request: " +
                JsonSerializer.Serialize(new { root = work.Build.Root }, Json) + ".";

        return prefix + "Build the current source generation with this exact retained process.run request: " +
            JsonSerializer.Serialize(new
            {
                executableId = work.Build.ExecutableId,
                arguments = work.Build.Arguments,
                workingDirectory = work.Build.WorkingDirectory
            }, Json) + ".";
    }

    private static string? ValidateKnownFileRevision(Input input, HarnessState state, string name, JsonElement arguments)
    {
        if (name == "file.edit")
            return ValidateReplacementRevision(input, state, arguments, "file.edit");
        if (name == "file.write" && arguments.TryGetProperty("collision", out var collision) &&
            collision.ValueKind == JsonValueKind.String && collision.GetString() == "replace")
            return ValidateReplacementRevision(input, state, arguments, "file.write collision=replace");
        return null;
    }

    private static string? ValidateReplacementRevision(Input input, HarnessState state, JsonElement arguments, string operation)
    {
        if (!TryActionPath(input, arguments, "path", out var path))
            return operation + " requires an exact path from a successful current file.read. No mutation was dispatched.";
        var revision = state.KnownFileRevisions.LastOrDefault(item => PathEquals(item.Path, path));
        if (revision is null)
            return operation + " requires a successful current file.read of that exact path before mutation. " +
                "No cached hash from another file is accepted and no mutation was dispatched.";
        if (!arguments.TryGetProperty("expectedSha256", out var expected) || expected.ValueKind != JsonValueKind.String ||
            !string.Equals(expected.GetString(), revision.Sha256, StringComparison.OrdinalIgnoreCase))
            return operation + " must use the exact SHA-256 retained for this path from file.read receipt " +
                revision.EffectId + ". No hash was substituted and no mutation was dispatched.";
        return null;
    }

    private static HarnessState AdoptBoundToolResult(Input input, HarnessState state, WorkflowEffect effect, ToolData? data,
        out PluginInventoryReceiptKind? inventoryKind)
    {
        state = RetainCurrentQaPresentation(input, state);
        var inventoryParsed = false;
        var candidates = state.PluginBuildCandidates;
        var candidatesTruncated = state.PluginBuildCandidatesTruncated;
        var discoveryRoots = state.PluginDiscoveryRoots;
        var work = state.PluginWork;
        var toolArguments = PendingToolArguments(state);
        var successful = SuccessfulEffect(effect, data);
        var targetInventory = state.PendingName == "plugin.projects" && toolArguments is { } targetArguments &&
            targetArguments.TryGetProperty("targetPath", out _);
        if (targetInventory)
        {
            inventoryKind = PluginInventoryReceiptKind.Invalidates;
            if (!successful || data is not { Truncated: false,
                    Evidence: { ProviderId: "cerebral.plugin-sdk", Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } } ||
                data.Evidence.RequestId is { } targetReceipt && targetReceipt != effect.Id ||
                !TryPluginTargetResult(input, toolArguments, data.Content, out var targetMatched, out _, out _))
                return state with { ScaffoldReadiness = new(false, effect.Id, "Exact target lookup was not confirmed; inspect its retained receipt.") };
            if (!targetMatched)
            {
                inventoryKind = PluginInventoryReceiptKind.ExactReadOnly;
                return state with { ScaffoldReadiness = ScaffoldReadinessAfterExactRead(state, effect) };
            }
        }
        if (successful &&
            state.PendingName is "plugin.projects" or "plugin.scaffold")
        {
            if (!data!.Truncated && TryReadPluginBuildCandidates(input, state.PendingName, data.Content, toolArguments, work?.Build,
                    out var discovered, out var truncated, out var roots))
            {
                inventoryParsed = true;
                candidates = discovered;
                candidatesTruncated = truncated;
                discoveryRoots = state.PendingName == "plugin.scaffold" || toolArguments is { } scope && scope.TryGetProperty("root", out _)
                    ? roots.Concat(discoveryRoots).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                        .Take(MaximumPluginDiscoveryRoots).ToArray()
                    : roots;
                var explicitInventoryRoot = state.PendingName == "plugin.projects" && candidates.Count == 1 &&
                    (PendingToolArguments(state) is { } inventoryArguments &&
                        TryActionPath(input, inventoryArguments, "root", out var requestedRoot) && PathEquals(requestedRoot, candidates[0].Root) ||
                     targetInventory && TryPluginTargetResult(input, toolArguments, data.Content, out var matchedTarget, out var matchedRoot, out var matchedComponent) &&
                        matchedTarget && PathEquals(matchedRoot!, candidates[0].Root) && matchedComponent == candidates[0].Component);
                work = work is not null && candidates.FirstOrDefault(candidate => SameBuild(candidate, work.Build)) is { } retained
                    ? work with { Build = retained }
                    : candidates.Count == 1 && (state.PendingName == "plugin.scaffold" || explicitInventoryRoot) ? new(candidates[0]) : null;
            }
            else
            {
                // Never carry an older project's coordinates across a nominally successful but
                // incomplete or malformed provider envelope.
                candidates = [];
                candidatesTruncated = false;
                // Positive discovery is not erased by an incomplete later envelope.
                work = null;
            }
        }

        if (state.PendingName == "plugin.projects" && work is not null)
            work = work with { ApprovedControlReference = inventoryParsed && !candidatesTruncated
                ? ReadApprovedControlReference(input, work, effect, data, toolArguments) : null };

        var revisions = state.KnownFileRevisions;
        var existingDirectoryEnsure = ConfirmedExistingDirectoryEnsure(input, state, effect, data);
        var mutationDisposition = toolArguments is { } mutationArguments
            ? ReadMutationDisposition(input, state.PendingName, mutationArguments, data, successful, effect.Id)
            : MutationDisposition.None;
        if (state.PendingName == "file.read")
        {
            if (successful &&
                toolArguments is { } requestedRead && TryActionPath(input, requestedRead, "path", out var requestedPath) &&
                TryReadFileRevision(data!.Content, effect.Id, out var revision) && PathEquals(requestedPath, revision.Path))
                revisions = AddFileRevision(revisions, revision);
            else if (toolArguments is { } failedRead && TryActionPath(input, failedRead, "path", out var failedPath))
                revisions = revisions.Where(item => !PathEquals(item.Path, failedPath)).ToArray();
        }
        else if (ClearsFileRevisionCache(state.PendingName) && !existingDirectoryEnsure &&
                  !(successful && state.PendingName == "file.write" && mutationDisposition == MutationDisposition.None))
            revisions = [];

        if (work is not null && toolArguments is { } pending)
        {
            var confirmed = successful;
            var mutation = MutationPath(input, state.PendingName, pending);
            if (mutation is not null && (PathWithin(mutation, work.Build.Root) ||
                state.FailedBuildRecovery is not null && state.PendingName is "file.write" or "file.edit" or "file.append" or "file.copy" &&
                RelatedCanonicalInput(input, state, work.Build, mutation)))
                work = mutationDisposition switch
                {
                    MutationDisposition.Actual => ClearCompletionEvidence(work) with
                    {
                        SourceGeneration = NextSourceGeneration(work.SourceGeneration),
                        LastMutationPath = mutation
                    },
                    MutationDisposition.KnownNoOp when input.UseRepeatedSuccessGuard => work,
                    MutationDisposition.InvalidateOnly or MutationDisposition.KnownNoOp => ClearCompletionEvidence(work),
                    _ => work
                };

            work = ObserveFailedBuildRecovery(input, state, work, pending, data, effect);
            if (state.PendingName == "process.run")
                work = RecordConfirmedCanonicalBuildAttempt(input, state, work, pending, data, effect);

            var processOrGitMayHaveRun = state.PendingName is "process.run" or "git.run" && data is not null &&
                data.Evidence?.Outcome != ToolExecutionOutcome.Rejected;
            // The sole versioned checker route is host-attested against unchanged current
            // inputs. Generic process.run and every incomplete/failed checker still invalidate.
            var confirmedOwnerCheck = state.PendingName == "process.run" &&
                ConfirmedCurrentInputCheck(input, work, effect, data);
            if (processOrGitMayHaveRun && !confirmedOwnerCheck)
                work = ClearCompletionEvidence(work);
            if (input.UseOwnerVerificationCompletion && confirmedOwnerCheck && data?.Verification is { } verification)
                work = work with { OwnerCheck = new(work.SourceGeneration, work.BuildEffectId!,
                    "cerebral.owner-verification", verification) };

            if (state.PendingName == "process.run" && TargetsPluginBuild(input, work.Build, pending))
                work = confirmed && data?.Evidence?.ExitCode == 0 && MatchesBuild(input, work.Build, pending)
                    ? work with
                    {
                        SuccessfulBuildGeneration = work.SourceGeneration,
                        BuildEffectId = effect.Id,
                        OwnerCheck = null,
                        VerifiedGeneration = null,
                        VerifyEffectId = null,
                        VerifiedPackageFingerprint = null,
                        PresentedGeneration = null,
                        PresentEffectId = null, PresentRequestId = null,
                        PresentedPackageFingerprint = null
                    }
                    : work with
                    {
                        SuccessfulBuildGeneration = null,
                        BuildEffectId = null,
                        OwnerCheck = null,
                        VerifiedGeneration = null,
                        VerifyEffectId = null,
                        VerifiedPackageFingerprint = null,
                        PresentedGeneration = null,
                        PresentEffectId = null, PresentRequestId = null,
                        PresentedPackageFingerprint = null
                    };
            else if (state.PendingName == "plugin.verify" && TargetsPathArgument(input, pending, "root", work.Build.Root))
                work = confirmed && TryActionPath(input, pending, "root", out var verifyRoot) && PathEquals(verifyRoot, work.Build.Root)
                    ? work with
                    {
                        VerifiedGeneration = work.SourceGeneration,
                        VerifyEffectId = effect.Id,
                        VerifiedPackageFingerprint = ReadVerifiedPackageFingerprint(work.Build, data!),
                        PresentedGeneration = null,
                        PresentEffectId = null, PresentRequestId = null,
                        PresentedPackageFingerprint = null
                    }
                    : work with { VerifiedGeneration = null, VerifyEffectId = null, VerifiedPackageFingerprint = null,
                        PresentedGeneration = null, PresentEffectId = null, PresentRequestId = null,
                        PresentedPackageFingerprint = null };
            else if (state.PendingName == "plugin.present" && TargetsPathArgument(input, pending, "path", work.Build.Root))
                work = confirmed && work.VerifiedGeneration == work.SourceGeneration &&
                       TryActionPath(input, pending, "path", out var presentPath) && PathEquals(presentPath, work.Build.BuiltManifest)
                    ? AdoptPresentationEvidence(work, effect.Id, data!)
                    : work with { PresentedGeneration = null, PresentEffectId = null, PresentRequestId = null,
                        PresentedPackageFingerprint = null };
        }

        PluginScaffoldReadiness? scaffoldReadiness;
        if (targetInventory && successful && inventoryParsed && data is { Truncated: false,
            Evidence: { ProviderId: "cerebral.plugin-sdk", Outcome: ToolExecutionOutcome.Succeeded, Partial: not true } })
        {
            inventoryKind = PluginInventoryReceiptKind.ExactReadOnly;
            scaffoldReadiness = ScaffoldReadinessAfterExactRead(state, effect);
        }
        else scaffoldReadiness = CurrentScaffoldReadiness(state, effect, data, successful, inventoryParsed, out inventoryKind);
        return state with
        {
            AutomaticPresentationCorrections = work is not null && state.PluginWork is not null &&
                work.SourceGeneration > state.PluginWork.SourceGeneration ? 0 : state.AutomaticPresentationCorrections,
            PluginBuildCandidates = candidates,
            PluginBuildCandidatesTruncated = candidatesTruncated,
            PluginSource = AdoptQaSourceInventory(input, state, effect, data, inventoryParsed, candidates),
            PluginDiscoveryRoots = discoveryRoots,
            ScaffoldReadiness = scaffoldReadiness,
            PluginWork = work,
            KnownFileRevisions = revisions
        };
    }

    private static bool SuccessfulEffect(WorkflowEffect effect, ToolData? data) =>
        effect.State == WorkflowEffectState.Succeeded && data is not null &&
        data.Evidence?.Outcome is null or ToolExecutionOutcome.Succeeded &&
        data.Evidence?.Partial is not true;

    private static PluginWorkBinding AdoptPresentationEvidence(PluginWorkBinding work, string effectId, ToolData data)
    {
        var evidence = ReadNativePresentationEvidence(data);
        return work with { PresentedGeneration = work.SourceGeneration, PresentEffectId = effectId,
            PresentRequestId = evidence.RequestId, PresentedPackageFingerprint = evidence.PackageFingerprint };
    }

    private static (string? RequestId, string? PackageFingerprint) ReadNativePresentationEvidence(ToolData data)
    {
        // The workflow effect addresses its durable receipt. Only matching native provider
        // evidence can supply the separate desktop request coordinate used by status/controls.
        if (data.Evidence is not { ProviderId: "cerebral.interface", Outcome: ToolExecutionOutcome.Succeeded } evidence ||
            evidence.Partial is true || !ValidPresentationRequestId(evidence.RequestId) || data.Truncated ||
            Encoding.UTF8.GetByteCount(data.Content) > MaximumBindingPayloadBytes) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 8 });
            var body = document.RootElement;
            UniqueProperties(body);
            if (body.ValueKind != JsonValueKind.Object ||
                !TryText(body, "requestId", 32, out var requestId) ||
                !string.Equals(requestId, evidence.RequestId, StringComparison.Ordinal)) return (null, null);
            var fingerprint = TryText(body, "packageFingerprint", 64, out var hash) && ValidPackageFingerprint(hash)
                ? hash.ToLowerInvariant() : null;
            return (requestId, fingerprint);
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return (null, null); }
    }

    private static string? ReadVerifiedPackageFingerprint(PluginBuildCandidate build, ToolData data)
    {
        // Only the SDK provider's complete verification of this exact bound output supplies
        // replacement identity. Model text, file mutation counts and foreign results cannot.
        if (build.Component != "desktop" ||
            data.Evidence is not { ProviderId: "cerebral.plugin-sdk", Outcome: ToolExecutionOutcome.Succeeded } ||
            data.Evidence.Partial is true || data.Truncated ||
            Encoding.UTF8.GetByteCount(data.Content) > MaximumBindingPayloadBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 8 });
            var body = document.RootElement;
            UniqueProperties(body);
            if (body.ValueKind != JsonValueKind.Object ||
                !TryText(body, "root", MaximumBindingPathCharacters, out var root) ||
                !TryCanonicalAbsolutePath(root, out var canonicalRoot) || !PathEquals(canonicalRoot, build.Root) ||
                !body.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True ||
                !body.TryGetProperty("desktop", out var desktop) || desktop.ValueKind != JsonValueKind.Object ||
                !TryText(desktop, "manifest", MaximumBindingPathCharacters, out var manifest) ||
                !TryCanonicalAbsolutePath(manifest, out var canonicalManifest) || !PathEquals(canonicalManifest, build.BuiltManifest) ||
                !TryText(desktop, "packageFingerprint", 64, out var fingerprint) || !ValidPackageFingerprint(fingerprint)) return null;
            return fingerprint.ToLowerInvariant();
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; }
    }

    private static bool ValidPackageFingerprint(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static bool ValidPresentationRequestId(string? requestId) =>
        Guid.TryParseExact(requestId, "N", out var parsed) && parsed != Guid.Empty;

    private static PluginWorkBinding ClearCompletionEvidence(PluginWorkBinding work) => work with
    {
        SuccessfulBuildGeneration = null,
        BuildEffectId = null,
        OwnerCheck = null,
        VerifiedGeneration = null,
        VerifyEffectId = null,
        VerifiedPackageFingerprint = null,
        PresentedGeneration = null,
        PresentEffectId = null, PresentRequestId = null,
        PresentedPackageFingerprint = null,
        CompletedBuildCorrections = 0,
        ApprovedControlReference = null
    };

    private static int NextSourceGeneration(int generation) => generation < MaximumSourceGeneration
        ? generation + 1
        : throw new InvalidOperationException("The bounded plugin source-generation limit was reached.");

    private static MutationDisposition ReadMutationDisposition(Input input, string? name, JsonElement arguments,
        ToolData? data, bool successful, string effectId)
    {
        if (data is null || MutationPath(input, name, arguments) is null)
            return MutationDisposition.None;
        if (!successful)
            return data.Evidence is not null && data.Evidence.Outcome != ToolExecutionOutcome.Rejected
                ? MutationDisposition.InvalidateOnly
                : MutationDisposition.None;
        if (data.Evidence?.RequestId is { } receiptRequest && receiptRequest != effectId)
            return MutationDisposition.InvalidateOnly;
        if (data.Truncated || Encoding.UTF8.GetByteCount(data.Content) > MaximumBindingPayloadBytes)
            return MutationDisposition.InvalidateOnly;
        if (name is not ("file.write" or "file.edit" or "file.append"))
            return MutationDisposition.Actual;
        try
        {
            using var document = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 8 });
            var result = document.RootElement;
            if (!UniqueMutationObject(result) ||
                !TryActionPath(input, arguments, "path", out var requestedPath) ||
                !TryText(result, "path", MaximumBindingPathCharacters, out var returnedPathText) ||
                !TryCanonicalAbsolutePath(returnedPathText, out var returnedPath) || !PathEquals(requestedPath, returnedPath))
                return MutationDisposition.InvalidateOnly;

            if (name == "file.write" && result.TryGetProperty("skipped", out var skipped))
                return skipped.ValueKind == JsonValueKind.True &&
                       arguments.TryGetProperty("collision", out var collision) && collision.ValueKind == JsonValueKind.String &&
                       string.Equals(collision.GetString(), "skip", StringComparison.Ordinal)
                    ? MutationDisposition.None
                    : MutationDisposition.InvalidateOnly;
            if (!ValidFileDetail(result)) return MutationDisposition.InvalidateOnly;
            if (name == "file.edit" ||
                name == "file.write" && arguments.TryGetProperty("collision", out var writeCollision) &&
                writeCollision.ValueKind == JsonValueKind.String && writeCollision.GetString() == "replace")
            {
                if (!UniqueMutationObject(arguments) || !TryText(arguments, "expectedSha256", 64, out var preimage) ||
                    preimage.Length != 64 || !preimage.All(char.IsAsciiHexDigit))
                    return MutationDisposition.InvalidateOnly;
                if (string.Equals(preimage, result.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                {
                    // A completed edit can return unchanged bytes despite distinct requested strings.
                    // The matched receipt proves no source progress, not that the requested repair was applied.
                    // A whole-file write additionally needs compatible admitted content for its no-op claim.
                    return name == "file.edit" || ConfirmedNoOpWrite(arguments, result, data, effectId)
                        ? MutationDisposition.KnownNoOp : MutationDisposition.InvalidateOnly;
                }
            }
            if (name == "file.append")
            {
                if (!result.TryGetProperty("alreadyApplied", out var replay) ||
                    replay.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !NonnegativeInt(result, "appendedCharacters")) return MutationDisposition.InvalidateOnly;
                return replay.ValueKind == JsonValueKind.True
                    ? MutationDisposition.InvalidateOnly
                    : MutationDisposition.Actual;
            }
            return MutationDisposition.Actual;
        }
        catch (JsonException)
        {
            // A successful but malformed mutation receipt cannot prove either a no-op or a new
            // source generation. Invalidate completion evidence without fabricating a mutation.
            return MutationDisposition.InvalidateOnly;
        }
    }

    private static bool ValidFileDetail(JsonElement result) =>
        TryText(result, "sha256", 64, out var hash) && hash.Length == 64 && hash.All(char.IsAsciiHexDigit) &&
        NonnegativeInt(result, "bytes") &&
        // Line/word counts are display metadata. A matched returned path/hash/byte count
        // proves the mutation; validate counts when supplied without inventing absent values.
        (!result.TryGetProperty("lines", out _) || NonnegativeInt(result, "lines")) &&
        (!result.TryGetProperty("words", out _) || NonnegativeInt(result, "words"));

    private static bool NonnegativeInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetInt32(out var number) && number >= 0;

    private static void ValidateBoundState(HarnessState state)
    {
        ValidateQaPresentations(state);
        ValidateNativeControlFailures(state);
        ValidateNativeControlCoverage(state);
        ValidatePendingGeneration(state);
        ValidatePluginTargetAction(state);
        foreach (var receipt in state.ToolReceipts)
            if (receipt.InventoryKind is { } kind &&
                (!Enum.IsDefined(kind) || receipt.Name is not ("plugin.projects" or "plugin.scaffold") ||
                 (kind is PluginInventoryReceiptKind.BroadAdmission or PluginInventoryReceiptKind.ExactReadOnly) &&
                 (receipt.Name != "plugin.projects" || receipt.State != nameof(WorkflowEffectState.Succeeded) ||
                  receipt.Outcome != nameof(ToolExecutionOutcome.Succeeded) || receipt.Truncated)))
                throw new ArgumentException("The saved plugin inventory receipt classification is invalid.");
        if (state.ScaffoldReadiness is { } readiness &&
            (!ValidEffectId(readiness.EffectId) || readiness.Reason is not { Length: > 0 and <= 256 } ||
             readiness.Reason.Any(char.IsControl) || !HasScaffoldReadinessEvidence(state, readiness, allowLegacy: true)))
            throw new ArgumentException("The saved scaffold readiness has no matching broad admission and exact-read receipt chain.");
        if (state.PluginBuildCandidates is null || state.PluginBuildCandidates.Count > MaximumPluginBuildCandidates ||
            state.PluginBuildCandidates.Any(candidate => candidate is null))
            throw new ArgumentException("The saved plugin build candidates are invalid.");
        if (state.PluginDiscoveryRoots is null || state.PluginDiscoveryRoots.Count > MaximumPluginDiscoveryRoots ||
            state.PluginDiscoveryRoots.Any(root => !CanonicalStoredPath(root)) ||
            state.PluginDiscoveryRoots.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != state.PluginDiscoveryRoots.Count)
            throw new ArgumentException("The saved plugin discovery roots are invalid.");
        ValidateQaSourceState(state);
        foreach (var candidate in state.PluginBuildCandidates) ValidateBoundCandidate(candidate);
        if (state.PluginBuildCandidates.Distinct(PluginBuildCandidateComparer.Instance).Count() != state.PluginBuildCandidates.Count)
            throw new ArgumentException("The saved plugin build candidates contain duplicates.");

        if (state.PluginWork is { } work)
        {
            if (work.Build is null || !state.PluginBuildCandidates.Any(candidate => SameBuild(candidate, work.Build)))
                throw new ArgumentException("The saved selected plugin build is not present in its bounded candidate set.");
            ValidateBoundCandidate(work.Build);
            if (work.Build.Origin == "discovery")
                throw new ArgumentException("A broad discovery candidate is not an inspected plugin build.");
            if (work.SourceGeneration is < 0 or > MaximumSourceGeneration || work.CompletedBuildCorrections is < 0 or > MaximumRepairs)
                throw new ArgumentException("The saved plugin source generation is invalid.");
            ValidateGenerationEvidence(work.SourceGeneration, work.SuccessfulBuildGeneration, work.BuildEffectId, "build");
            ValidateGenerationEvidence(work.SourceGeneration, work.VerifiedGeneration, work.VerifyEffectId, "verification");
            ValidateGenerationEvidence(work.SourceGeneration, work.PresentedGeneration, work.PresentEffectId, "presentation");
            if (work.PresentedGeneration is not null && work.VerifiedGeneration != work.SourceGeneration)
                throw new ArgumentException("The saved plugin presentation has no current verification.");
            if (work.VerifiedPackageFingerprint is not null &&
                (!ValidPackageFingerprint(work.VerifiedPackageFingerprint) || work.VerifiedGeneration != work.SourceGeneration ||
                 work.VerifyEffectId is null))
                throw new ArgumentException("The saved verified plugin package fingerprint is invalid or stale.");
            if (work.PresentedPackageFingerprint is not null &&
                (!ValidPackageFingerprint(work.PresentedPackageFingerprint) || work.PresentedGeneration != work.SourceGeneration ||
                 work.PresentRequestId is null))
                throw new ArgumentException("The saved presented plugin package fingerprint is invalid or stale.");
            if (work.PresentRequestId is not null &&
                (!ValidPresentationRequestId(work.PresentRequestId) || work.PresentedGeneration != work.SourceGeneration ||
                 work.PresentEffectId is null))
                throw new ArgumentException("The saved native presentation request coordinate is invalid or stale.");
            if (work.LastMutationPath is not null &&
                (work.SourceGeneration == 0 || !CanonicalStoredPath(work.LastMutationPath) ||
                 !PathWithin(work.LastMutationPath, work.Build.Root) && !RecordedRelatedMutation(state, work, work.LastMutationPath)))
                throw new ArgumentException("The saved plugin mutation path is invalid or outside its selected project.");
            if (work.DiagnosticBuildAttemptGeneration is < 0 ||
                work.DiagnosticBuildAttemptGeneration > work.SourceGeneration)
                throw new ArgumentException("The saved plugin diagnostic-build generation is invalid.");
            if ((work.LastCanonicalBuildEffectId is null) != (work.LastCanonicalBuildGeneration is null) ||
                work.LastCanonicalBuildGeneration is < 0 || work.LastCanonicalBuildGeneration > work.SourceGeneration ||
                work.LastCanonicalBuildEffectId is { } buildAttempt &&
                (!ValidEffectId(buildAttempt) || !state.ToolReceipts.Any(receipt =>
                    receipt.EffectId == buildAttempt && receipt.Name == "process.run" &&
                    receipt.State is "Succeeded" or "Failed" && receipt.Outcome is "Succeeded" or "Failed")))
                throw new ArgumentException("The saved canonical build coordinate has no confirmed matching receipt.");
            ValidateCanonicalBuildFailure(state, work);
            ValidateApprovedControlReference(state, work);
        }

        if (state.KnownFileRevisions is null || state.KnownFileRevisions.Count > MaximumKnownFileRevisions ||
            state.KnownFileRevisions.Any(revision => revision is null))
            throw new ArgumentException("The saved file revisions are invalid.");
        var revisionPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var revision in state.KnownFileRevisions)
        {
            if (!CanonicalStoredPath(revision.Path) || revision.Sha256 is not { Length: 64 } ||
                !revision.Sha256.All(char.IsAsciiHexDigit) || !ValidEffectId(revision.EffectId) ||
                !revisionPaths.Add(revision.Path))
                throw new ArgumentException("A saved file revision is malformed.");
        }
    }

    private static void ValidateCanonicalBuildFailure(HarnessState state, PluginWorkBinding work)
    {
        if (work.LastCanonicalBuildFailure is not { } failure) return;
        var receipt = state.ToolReceipts.LastOrDefault(item => item.EffectId == failure.EffectId &&
            item.Name == "process.run");
        if (failure.EffectId != work.LastCanonicalBuildEffectId ||
            failure.SourceGeneration != work.LastCanonicalBuildGeneration ||
            failure.ProjectKey != CanonicalBuildProjectKey(work.Build) ||
            receipt is not { State: "Failed", Outcome: "Failed" } ||
            receipt.ExitCode != failure.ExitCode || failure.Errors is < 0 || failure.Warnings is < 0 ||
            failure.Diagnostic is { } diagnostic && !ProjectionComplete(diagnostic) ||
            JsonSerializer.SerializeToUtf8Bytes(failure, Json).Length > MaximumCanonicalBuildFailureBytes)
            throw new ArgumentException("The saved canonical build failure summary has invalid provenance or bounds.");

        // While the original host projection remains, it is an independent exact check on
        // the saved compact summary. Compaction may remove only that contribution; the
        // private project/effect/generation coordinate and immutable receipt remain required.
        var source = state.Contributions.FirstOrDefault(item => item.Id == "tool." + failure.EffectId);
        if (source is null) return;
        var status = RetainedStatus(source, receipt);
        if (status is null || failure.Errors != status.Errors || failure.Warnings != status.Warnings ||
            failure.Diagnostic != CanonicalBuildDiagnostic(RetainedText(source)))
            throw new ArgumentException("The saved canonical build failure summary does not match its retained host projection.");
    }

    private static void ValidateBoundCandidate(PluginBuildCandidate candidate)
    {
        if (!CanonicalStoredPath(candidate.Root) || !CanonicalStoredPath(candidate.ProjectFile) ||
            !CanonicalStoredPath(candidate.WorkingDirectory) || !CanonicalStoredPath(candidate.BuiltManifest) ||
            candidate.Component is not ("desktop" or "service") || candidate.Origin is not
                ("discovery" or "inventory" or "scaffold" or InventoryDesktopAuthoringOrigin or ScaffoldDesktopAuthoringOrigin) ||
            candidate.Component != "desktop" && candidate.Origin is InventoryDesktopAuthoringOrigin or ScaffoldDesktopAuthoringOrigin ||
            string.IsNullOrWhiteSpace(candidate.ExecutableId) || candidate.ExecutableId.Length > 256 ||
            candidate.ExecutableId.Any(char.IsControl) || candidate.Arguments is null ||
            candidate.Arguments.Count is < 7 or > MaximumBuildArguments ||
            candidate.Arguments.Any(argument => argument is null || argument.Length > MaximumBuildArgumentCharacters || argument.Any(char.IsControl)) ||
            candidate.Arguments[0] != "build" || !HasExactReleaseConfiguration(candidate.Arguments) ||
            !TryCanonicalAbsolutePath(candidate.Arguments[1], out var projectArgument) || !PathEquals(projectArgument, candidate.ProjectFile) ||
            !string.Equals(Path.GetExtension(candidate.ProjectFile), ".csproj", StringComparison.OrdinalIgnoreCase) ||
            !PathWithin(candidate.ProjectFile, candidate.Root) || !PathWithin(candidate.WorkingDirectory, candidate.Root) ||
            !PathWithin(candidate.BuiltManifest, candidate.Root) ||
            !PathEquals(Path.GetDirectoryName(candidate.ProjectFile)!, candidate.WorkingDirectory) ||
            candidate.SourceFile is not null && (!CanonicalStoredPath(candidate.SourceFile) ||
                !PathWithin(candidate.SourceFile, candidate.WorkingDirectory) ||
                !string.Equals(Path.GetExtension(candidate.SourceFile), ".cs", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A saved plugin build candidate is malformed.");
    }

    private static void ValidateGenerationEvidence(int sourceGeneration, int? generation, string? effectId, string kind)
    {
        if ((generation is null) != (effectId is null) || generation is not null && generation != sourceGeneration ||
            effectId is not null && !ValidEffectId(effectId))
            throw new ArgumentException("The saved plugin " + kind + " evidence is inconsistent.");
    }

    private static bool ValidEffectId(string? value) => value is { Length: > 0 and <= MaximumBindingEffectIdCharacters } &&
        !value.Any(char.IsControl);

    private static bool CanonicalStoredPath(string? value) => value is { Length: > 0 } &&
        TryCanonicalAbsolutePath(value, out var canonical) && PathEquals(value, canonical);

    private static IReadOnlyList<JsonElement> GeneratedToolArgumentSchemas(string toolName, JsonElement schema, HarnessState state)
    {
        var explicitSchema = ProjectConversationToolSchema(toolName, ExplicitDefaultArguments(schema));
        if (toolName == "plugin.present" && state.PluginWork?.VerifiedPackageFingerprint is { } fingerprint)
            return [ConstrainPresentationFingerprint(explicitSchema, fingerprint)];
        if (toolName == "file.edit")
            return state.KnownFileRevisions.Select(revision => ConstrainRevisionSchema(explicitSchema, revision, replace: false)).ToArray();
        if (toolName != "file.write") return [explicitSchema];

        // Admit the newest proven replacement before the open create branch. The grammar
        // byte bound may trim later choices; it must not preferentially leave only create.
        var variants = state.KnownFileRevisions.Reverse()
            .Select(revision => ConstrainRevisionSchema(explicitSchema, revision, replace: true)).ToList();
        variants.Add(CreateOnlyWriteSchema(explicitSchema));
        return variants;
    }

    private static JsonElement ConstrainPresentationFingerprint(JsonElement schema, string fingerprint)
    {
        var generated = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (generated["properties"] is not JsonObject properties)
            throw new ArgumentException("The plugin presentation schema cannot carry a package preimage condition.");
        properties["expectedPackageFingerprint"] = new JsonObject { ["type"] = "string", ["const"] = fingerprint };
        var required = generated["required"] as JsonArray ?? new JsonArray();
        if (!required.Any(item => item?.GetValue<string>() == "expectedPackageFingerprint"))
            required.Add("expectedPackageFingerprint");
        generated["required"] = required;
        return Element(generated);
    }

    private static JsonElement ProjectConversationToolSchema(string toolName, JsonElement schema)
    {
        if (toolName != "interface.present") return schema;

        var generated = JsonNode.Parse(schema.GetRawText()) as JsonObject;
        if (generated?["properties"] is not JsonObject properties ||
            properties["kind"] is not JsonObject kind || kind["enum"] is not JsonArray values ||
            values.Count == 0 || values.Any(value => value is not JsonValue || !value.AsValue().TryGetValue<string>(out _)))
            throw new ArgumentException("The interface.present schema cannot be safely projected for Conversation.");

        var pluginIndex = -1;
        for (var index = 0; index < values.Count; index++)
            if (values[index]!.GetValue<string>() == "plugin")
            {
                pluginIndex = index;
                break;
            }
        if (pluginIndex < 0) return schema;
        values.RemoveAt(pluginIndex);
        if (values.Count == 0)
            throw new ArgumentException("The interface.present schema has no Conversation-compatible presentation kinds.");
        return Element(generated);
    }

    private static JsonElement CreateOnlyWriteSchema(JsonElement schema)
    {
        var generated = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (generated["properties"] is not JsonObject properties) return schema;
        properties.Remove("expectedSha256");
        if (properties.ContainsKey("collision"))
            properties["collision"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("fail", "skip") };
        if (generated["required"] is JsonArray required)
            for (var index = required.Count - 1; index >= 0; index--)
                if (required[index]?.GetValue<string>() == "expectedSha256") required.RemoveAt(index);
        OrderMutationProperties(generated);
        return Element(generated);
    }

    private static JsonElement ConstrainRevisionSchema(JsonElement schema, KnownFileRevision revision, bool replace)
    {
        var generated = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (generated["properties"] is not JsonObject properties) return schema;
        properties["path"] = new JsonObject { ["type"] = "string", ["const"] = revision.Path };
        properties["expectedSha256"] = new JsonObject { ["type"] = "string", ["const"] = revision.Sha256 };
        if (replace) properties["collision"] = new JsonObject { ["type"] = "string", ["const"] = "replace" };
        var required = generated["required"] as JsonArray ?? new JsonArray();
        var requiredNames = required.Select(item => item?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var name in replace ? new[] { "path", "content", "collision", "expectedSha256" } : new[] { "path", "expectedSha256" })
            if (requiredNames.Add(name)) required.Add(name);
        generated["required"] = required;
        OrderMutationProperties(generated);
        return Element(generated);
    }

    private static void OrderMutationProperties(JsonObject generated)
    {
        if (generated["properties"] is not JsonObject properties) return;
        // Select destination semantics and the exact preimage before emitting a potentially
        // long body. Reorder only the generated copy, never the published tool contract.
        var ordered = new JsonObject();
        foreach (var name in new[] { "collision", "path", "expectedSha256" })
            if (properties.TryGetPropertyValue(name, out var value)) ordered[name] = value?.DeepClone();
        foreach (var property in properties)
            if (property.Key is not ("collision" or "path" or "expectedSha256" or "content"))
                ordered[property.Key] = property.Value?.DeepClone();
        if (properties.TryGetPropertyValue("content", out var content)) ordered["content"] = content?.DeepClone();
        generated["properties"] = ordered;
    }

    private static object? PluginWorkAnchor(HarnessState state) => state.PluginWork is not { } work ? null : new
    {
        referenceOnly = true,
        root = work.Build.Root,
        component = work.Build.Component,
        project = work.Build.ProjectFile,
        source = work.Build.SourceFile,
        sourceLookup = work.Build.SourceFile is null
            ? "Use bounded file.list or file.search in the retained build workingDirectory to discover source; do not guess filenames."
            : null,
        desktopAuthoring = HasDesktopAuthoringContract(work.Build)
            ? new
            {
                contract = ToolModelProjection.DesktopPluginAuthoringContractId
            }
            : null,
        build = new { executableId = work.Build.ExecutableId, arguments = work.Build.Arguments, workingDirectory = work.Build.WorkingDirectory },
        builtManifest = work.Build.BuiltManifest,
        approvedControlReference = ApprovedControlReferenceAnchor(work),
        sourceGeneration = work.SourceGeneration,
        successfulReleaseBuild = work.SuccessfulBuildGeneration == work.SourceGeneration ? new { generation = work.SuccessfulBuildGeneration, receipt = work.BuildEffectId } : null,
        verified = work.VerifiedGeneration == work.SourceGeneration ? new
            { generation = work.VerifiedGeneration, receipt = work.VerifyEffectId, packageFingerprint = work.VerifiedPackageFingerprint } : null,
        presentationQueued = work.PresentedGeneration == work.SourceGeneration ? new
        {
            generation = work.PresentedGeneration, effectId = work.PresentEffectId, requestId = work.PresentRequestId,
            packageFingerprint = work.PresentedPackageFingerprint,
            state = "pending-owner-review",
            approved = false,
            installed = false,
            functionallyTested = false,
            next = work.PresentRequestId is null
                ? "Read the effect receipt to recover requestId before interface.status or plugin.controls; effectId is not requestId."
                : "Await separate owner review. This queued package is not installed or functionally tested by the presentation receipt."
        } : null
    };

    private static object[] PluginCandidateAnchors(HarnessState state) => state.PluginBuildCandidates.Select(candidate => (object)new
    {
        candidate.Root,
        candidate.Component,
        source = candidate.SourceFile
    }).ToArray();

    private static object[] FileRevisionAnchors(HarnessState state) => state.KnownFileRevisions.Select(revision => (object)new
    {
        revision.Path,
        sha256 = revision.Sha256,
        receipt = revision.EffectId
    }).ToArray();

    private static bool TryReadPluginBuildCandidates(Input input, string toolName, string content, JsonElement? pendingArguments,
        PluginBuildCandidate? currentBuild, out IReadOnlyList<PluginBuildCandidate> candidates, out bool truncated,
        out IReadOnlyList<string> discoveryRoots)
    {
        candidates = [];
        truncated = false;
        discoveryRoots = [];
        if (Encoding.UTF8.GetByteCount(content) > MaximumBindingPayloadBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var hasDesktopAuthoringContract = root.TryGetProperty("desktopAuthoringContract", out var desktopAuthoringContract) &&
                desktopAuthoringContract.ValueKind == JsonValueKind.String &&
                desktopAuthoringContract.GetString() == ToolModelProjection.DesktopPluginAuthoringContractId;
            var found = new List<PluginBuildCandidate>();
            var roots = new List<string>();
            string? requestedScaffoldComponent = null;
            if (toolName == "plugin.projects")
            {
                if (!root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() > 50)
                    return false;
                if (!root.TryGetProperty("limited", out var limited) ||
                    limited.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !root.TryGetProperty("count", out var countValue) || countValue.ValueKind != JsonValueKind.Number ||
                    !countValue.TryGetInt32(out var count) ||
                    count != projects.GetArrayLength()) return false;
                truncated = limited.ValueKind == JsonValueKind.True;
                string? requestedRoot = null;
                if (pendingArguments is not { } pendingProjects || pendingProjects.ValueKind != JsonValueKind.Object) return false;
                if (pendingProjects.TryGetProperty("root", out _))
                {
                    if (!TryActionPath(input, pendingProjects, "root", out requestedRoot) || truncated ||
                        projects.GetArrayLength() != 1 || count != 1 ||
                        !TryText(projects[0], "root", MaximumBindingPathCharacters, out var soleRootText) ||
                        !TryCanonicalAbsolutePath(soleRootText, out var soleRoot) || !PathEquals(requestedRoot, soleRoot)) return false;
                }
                if (pendingProjects.TryGetProperty("targetPath", out _))
                {
                    if (!TryPluginTargetResult(input, pendingProjects, content, out var matched, out requestedRoot, out var selectedComponent) ||
                        !matched || truncated || projects.GetArrayLength() != 1 ||
                        !projects[0].TryGetProperty("components", out var selectedComponents) || selectedComponents.ValueKind != JsonValueKind.Array ||
                        selectedComponents.GetArrayLength() != 1 ||
                        !TryText(selectedComponents[0], "component", 32, out var returnedComponent) || returnedComponent != selectedComponent)
                        return false;
                }
                var summaryOnly = root.TryGetProperty("discoverySummaryOnly", out var summaryMarker) &&
                    summaryMarker.ValueKind == JsonValueKind.True;
                if (summaryOnly && (requestedRoot is not null || hasDesktopAuthoringContract)) return false;
                foreach (var project in projects.EnumerateArray())
                {
                    if (!TryText(project, "root", MaximumBindingPathCharacters, out var projectRoot) ||
                        !TryCanonicalAbsolutePath(projectRoot, out var canonicalProjectRoot) ||
                        !project.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array ||
                        components.GetArrayLength() > 4) continue;
                    if (requestedRoot is not null && (!TryCanonicalAbsolutePath(projectRoot, out var returnedRoot) ||
                        !PathEquals(requestedRoot, returnedRoot))) return false;
                    if (summaryOnly)
                    {
                        // Compact discovery identifies known source roots, never inspected build authority.
                        // Keep the exact-root prerequisite even when no recipe was returned in this phase.
                        var kinds = components.EnumerateArray().Select(component =>
                            TryText(component, "component", 32, out var kind) ? kind : null).ToArray();
                        if (kinds.Length is > 0 and <= 2 && kinds.All(kind => kind is "desktop" or "service") &&
                            kinds.Distinct(StringComparer.Ordinal).Count() == kinds.Length &&
                            !components.EnumerateArray().Any(component => component.TryGetProperty("build", out var summaryBuild) &&
                                summaryBuild.ValueKind != JsonValueKind.Null) &&
                            !roots.Any(root => PathEquals(root, canonicalProjectRoot))) roots.Add(canonicalProjectRoot);
                        continue;
                    }
                    var priorCandidates = found.Count;
                    foreach (var component in components.EnumerateArray())
                        if (TryCandidate(projectRoot, component, null, requestedRoot is null ? "discovery" : "inventory",
                                out var candidate))
                        {
                            if (requestedRoot is not null && hasDesktopAuthoringContract && candidate.Component == "desktop")
                                candidate = candidate with { Origin = InventoryDesktopAuthoringOrigin };
                            found.Add(currentBuild is not null && SameBuild(candidate, currentBuild)
                                ? candidate with { Origin = RetainedCandidateOrigin(currentBuild, candidate) } : candidate);
                        }
                    if (found.Count > priorCandidates && !roots.Any(root => PathEquals(root, canonicalProjectRoot))) roots.Add(canonicalProjectRoot);
                    if (requestedRoot is not null && found.Count == priorCandidates) return false;
                }
                // Summary discovery contributes no new recipe. Preserve a previously inspected
                // recipe and its generation evidence when that exact root is still in the catalog.
                if (summaryOnly && currentBuild is not null && roots.Any(root => PathEquals(root, currentBuild.Root)))
                    found.Add(currentBuild);
            }
            else if (toolName == "plugin.scaffold")
            {
                if (pendingArguments is not { } pendingScaffold || pendingScaffold.ValueKind != JsonValueKind.Object ||
                    !TryActionPath(input, pendingScaffold, "targetDirectory", out var requestedRoot) ||
                    !TryText(pendingScaffold, "pluginId", 64, out var requestedId) ||
                    !TryText(pendingScaffold, "name", 100, out var requestedName) ||
                    !TryText(pendingScaffold, "component", 32, out requestedScaffoldComponent) ||
                    requestedScaffoldComponent is not ("desktop" or "service" or "cross-surface") ||
                    !TryText(root, "root", MaximumBindingPathCharacters, out var projectRoot) ||
                    !TryCanonicalAbsolutePath(projectRoot, out var returnedRoot) || !PathEquals(requestedRoot, returnedRoot) ||
                    !TryText(root, "pluginId", 64, out var returnedId) || returnedId != requestedId ||
                    !TryText(root, "name", 100, out var returnedName) || returnedName != requestedName ||
                    !TryText(root, "component", 32, out var returnedComponent) || returnedComponent != requestedScaffoldComponent)
                    return false;
                roots.Add(returnedRoot);
                var desktopExpected = requestedScaffoldComponent is "desktop" or "cross-surface";
                var serviceExpected = requestedScaffoldComponent is "service" or "cross-surface";
                if (!root.TryGetProperty("desktop", out var desktop) ||
                    desktop.ValueKind != (desktopExpected ? JsonValueKind.Object : JsonValueKind.Null) ||
                    !root.TryGetProperty("service", out var service) ||
                    service.ValueKind != (serviceExpected ? JsonValueKind.Object : JsonValueKind.Null)) return false;
                foreach (var componentName in new[] { "desktop", "service" })
                    if (root.TryGetProperty(componentName, out var component) && component.ValueKind == JsonValueKind.Object &&
                        TryCandidate(projectRoot, component, componentName, "scaffold", out var candidate) &&
                        candidate.SourceFile is not null)
                    {
                        if (hasDesktopAuthoringContract && candidate.Component == "desktop")
                            candidate = candidate with { Origin = ScaffoldDesktopAuthoringOrigin };
                        found.Add(candidate);
                    }
            }
            else return false;

            var unique = new List<PluginBuildCandidate>();
            foreach (var candidate in found)
                if (!unique.Any(existing => SameBuild(existing, candidate))) unique.Add(candidate);
            if (requestedScaffoldComponent is not null)
            {
                var expected = requestedScaffoldComponent == "cross-surface"
                    ? new[] { "desktop", "service" }
                    : new[] { requestedScaffoldComponent };
                if (unique.Count != expected.Length || expected.Any(component => unique.Count(item => item.Component == component) != 1))
                    return false;
            }
            if (unique.Count > MaximumPluginBuildCandidates) truncated = true;
            // A read-only inventory may list the current exact recipe beyond the bounded
            // discovery cache. Retain that validated match before trimming, so its source
            // generations and completion evidence cannot be erased by unrelated projects.
            discoveryRoots = roots.ToArray();
            candidates = unique.OrderByDescending(candidate => currentBuild is not null && SameBuild(candidate, currentBuild))
                .Take(MaximumPluginBuildCandidates).ToArray();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string RetainedCandidateOrigin(PluginBuildCandidate current, PluginBuildCandidate candidate) =>
        !HasDesktopAuthoringContract(current) && HasDesktopAuthoringContract(candidate)
            ? candidate.Origin
            : current.Origin;

    private static bool HasDesktopAuthoringContract(PluginBuildCandidate candidate) =>
        candidate.Component == "desktop" && candidate.Origin is InventoryDesktopAuthoringOrigin or ScaffoldDesktopAuthoringOrigin;

    private static bool TryCandidate(string rootText, JsonElement component, string? knownComponent, string origin,
        out PluginBuildCandidate candidate)
    {
        candidate = null!;
        if (!TryCanonicalAbsolutePath(rootText, out var root) ||
            !TryText(component, "build", out var build) || build.ValueKind != JsonValueKind.Object ||
            !TryText(build, "executableId", 256, out var executableId) || executableId.Any(char.IsControl) ||
            !TryText(build, "workingDirectory", MaximumBindingPathCharacters, out var workingText) ||
            !TryText(build, "builtManifest", MaximumBindingPathCharacters, out var manifestText) ||
            !build.TryGetProperty("arguments", out var argumentsValue) || argumentsValue.ValueKind != JsonValueKind.Array ||
            argumentsValue.GetArrayLength() is < 7 or > MaximumBuildArguments) return false;
        var componentName = knownComponent;
        if (componentName is null && !TryText(component, "component", 32, out componentName)) return false;
        if (componentName is not ("desktop" or "service")) return false;
        var arguments = new List<string>();
        foreach (var argument in argumentsValue.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String || argument.GetString() is not { } text ||
                text.Length > MaximumBuildArgumentCharacters || text.Any(char.IsControl)) return false;
            arguments.Add(text);
        }
        if (arguments[0] != "build" || !TryCanonicalAbsolutePath(arguments[1], out var project) ||
            !string.Equals(Path.GetExtension(project), ".csproj", StringComparison.OrdinalIgnoreCase) ||
            !TryCanonicalAbsolutePath(workingText, out var working) || !TryCanonicalAbsolutePath(manifestText, out var manifest) ||
            !PathWithin(project, root) || !PathWithin(working, root) || !PathWithin(manifest, root) ||
            !PathEquals(Path.GetDirectoryName(project)!, working) ||
            !HasExactReleaseConfiguration(arguments)) return false;
        if (component.TryGetProperty("project", out var explicitProject))
        {
            if (explicitProject.ValueKind != JsonValueKind.String || !TryCanonicalAbsolutePath(explicitProject.GetString()!, out var declared) ||
                !PathEquals(declared, project)) return false;
        }
        if (component.TryGetProperty("builtManifest", out var explicitManifest))
        {
            if (explicitManifest.ValueKind != JsonValueKind.String || !TryCanonicalAbsolutePath(explicitManifest.GetString()!, out var declared) ||
                !PathEquals(declared, manifest)) return false;
        }
        string? sourceFile = null;
        if (component.TryGetProperty("source", out var source) && source.ValueKind != JsonValueKind.Null)
        {
            if (source.ValueKind != JsonValueKind.String ||
                !TryCanonicalAbsolutePath(source.GetString()!, out sourceFile) ||
                !PathWithin(sourceFile, working) ||
                !string.Equals(Path.GetExtension(sourceFile), ".cs", StringComparison.OrdinalIgnoreCase)) return false;
        }
        candidate = new(root, componentName, project, executableId, arguments.ToArray(), working, manifest, origin, sourceFile);
        return true;
    }

    private static bool HasExactReleaseConfiguration(IReadOnlyList<string> arguments)
    {
        var configurations = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
            if (arguments[index] is "--configuration" or "-c")
            {
                if (++index >= arguments.Count) return false;
                configurations.Add(arguments[index]);
            }
        return configurations.Count == 1 && configurations[0] == "Release";
    }

    private static bool TryText(JsonElement value, string name, int maximum, out string text)
    {
        text = "";
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String ||
            item.GetString() is not { } found || string.IsNullOrWhiteSpace(found) || found.Length > maximum || found.Any(char.IsControl)) return false;
        text = found;
        return true;
    }

    private static bool TryText(JsonElement value, string name, out JsonElement item)
    {
        item = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out item);
    }

    private static bool TryReadFileRevision(string content, string effectId, out KnownFileRevision revision)
    {
        revision = null!;
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
            var value = document.RootElement;
            if (!TryText(value, "path", MaximumBindingPathCharacters, out var pathText) ||
                !TryCanonicalAbsolutePath(pathText, out var path) || !TryText(value, "sha256", 64, out var hash) ||
                hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) return false;
            revision = new(path, hash.ToLowerInvariant(), effectId);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static IReadOnlyList<KnownFileRevision> AddFileRevision(IReadOnlyList<KnownFileRevision> existing, KnownFileRevision revision) =>
        existing.Where(item => !PathEquals(item.Path, revision.Path)).Append(revision).TakeLast(MaximumKnownFileRevisions).ToArray();

    private static bool ClearsFileRevisionCache(string? name) => name is not null && name is not
        ("file.read" or "file.inspect" or "file.list" or "file.search" or "plugin.projects" or "plugin.contract" or
         "plugin.verify" or "interface.status" or "math.calculate");

    private static JsonElement? PendingToolArguments(HarnessState state) => state.PendingArguments is { } pending &&
        pending.ValueKind == JsonValueKind.Object && pending.TryGetProperty("arguments", out var arguments) &&
        arguments.ValueKind == JsonValueKind.Object ? arguments : null;

    private static string? MutationPath(Input input, string? name, JsonElement arguments)
    {
        var field = name switch
        {
            "file.write" or "file.edit" or "file.append" => "path",
            "file.copy" => "destinationPath",
            _ => null
        };
        return field is not null && TryActionPath(input, arguments, field, out var path) ? path : null;
    }

    private static IReadOnlyList<string> ToolTargetPaths(Input input, string name, JsonElement arguments)
    {
        if (name == "process.run" && arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("arguments", out var processArguments) && processArguments.ValueKind == JsonValueKind.Array)
        {
            var operand = processArguments.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString())
                .FirstOrDefault(IsProjectBuildOperand);
            if (operand is not null)
            {
                var directory = TryActionPath(input, arguments, "workingDirectory", out var working) ? working : input.WorkingDirectory;
                return TryResolvePath(operand, directory, out var project) ? [project] : [];
            }
        }
        var fields = name switch
        {
            "file.read" or "file.inspect" or "file.write" or "file.edit" or "file.append" or "file.list" or "file.search" or "file.mkdir" or "program.source.generate" => new[] { "path" },
            "file.copy" => new[] { "sourcePath", "destinationPath" },
            "plugin.verify" => new[] { "root" },
            "plugin.present" => new[] { "path" },
            "process.run" => new[] { "workingDirectory" },
            _ => []
        };
        var paths = new List<string>();
        foreach (var field in fields)
            if (TryActionPath(input, arguments, field, out var path)) paths.Add(path);
        return paths;
    }

    private static bool TargetsPluginBuild(Input input, PluginBuildCandidate build, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("executableId", out var executable) || executable.ValueKind != JsonValueKind.String ||
            executable.GetString() != build.ExecutableId) return false;
        if (!arguments.TryGetProperty("arguments", out var values) || values.ValueKind != JsonValueKind.Array) return false;
        var command = values.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).ToArray();
        var buildIntent = command.Length > 0 && string.Equals(command[0], "build", StringComparison.OrdinalIgnoreCase);
        if (!buildIntent) return false;
        var hasWorking = TryActionPath(input, arguments, "workingDirectory", out var working);
        // An explicit project/solution target can follow build options. When supplied, it controls
        // target identity even if the process starts under this plugin's directory; never capture
        // a deliberately foreign build by cwd alone.
        var explicitOperand = command.Skip(1).FirstOrDefault(IsProjectBuildOperand);
        if (explicitOperand is not null)
            return TryResolvePath(explicitOperand, hasWorking ? working : input.WorkingDirectory, out var explicitTarget) &&
                   PathWithin(explicitTarget, build.Root);
        if (hasWorking && PathWithin(working, build.Root)) return true;
        foreach (var value in command.Where(value => value is not null))
            if (TryResolvePath(value!, hasWorking ? working : input.WorkingDirectory, out var path) &&
                PathWithin(path, build.Root)) return true;
        return false;
    }

    private static bool IsProjectBuildOperand(string? value) => value is { Length: > 0 } && value[0] != '-' &&
        !value.StartsWith("/p:", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("/property:", StringComparison.OrdinalIgnoreCase) &&
        (value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
         value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
         value.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

    private static bool MatchesBuild(Input input, PluginBuildCandidate build, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("executableId", out var executable) || executable.ValueKind != JsonValueKind.String ||
            executable.GetString() != build.ExecutableId || !TryActionPath(input, arguments, "workingDirectory", out var working) ||
            !PathEquals(working, build.WorkingDirectory) || !arguments.TryGetProperty("arguments", out var values) ||
            values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != build.Arguments.Count) return false;
        var actual = values.EnumerateArray().ToArray();
        for (var index = 0; index < actual.Length; index++)
        {
            if (actual[index].ValueKind != JsonValueKind.String) return false;
            var text = actual[index].GetString()!;
            if (index == 1)
            {
                if (!TryResolvePath(text, working, out var project) || !PathEquals(project, build.ProjectFile)) return false;
            }
            else if (text != build.Arguments[index]) return false;
        }
        return true;
    }

    private static bool TargetsPathArgument(Input input, JsonElement arguments, string field, string root) =>
        TryActionPath(input, arguments, field, out var path) && PathWithin(path, root);

    private static bool TryActionPath(Input input, JsonElement arguments, string field, out string path)
    {
        path = "";
        return arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(field, out var value) &&
            value.ValueKind == JsonValueKind.String && TryResolvePath(value.GetString()!, input.WorkingDirectory, out path);
    }

    private static bool TryResolvePath(string value, string? baseDirectory, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumBindingPathCharacters || value.Any(char.IsControl)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(value))
            {
                if (baseDirectory is null || !Path.IsPathFullyQualified(baseDirectory)) return false;
                value = Path.Combine(baseDirectory, value);
            }
            path = Path.GetFullPath(value);
            return path.Length <= MaximumBindingPathCharacters;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool TryCanonicalAbsolutePath(string value, out string path) => TryResolvePath(value, null, out path);

    private static bool PathWithin(string path, string root)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative == "." || !Path.IsPathFullyQualified(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool PathEquals(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool SameBuild(PluginBuildCandidate left, PluginBuildCandidate right) =>
        PathEquals(left.Root, right.Root) && left.Component == right.Component && PathEquals(left.ProjectFile, right.ProjectFile) &&
        left.ExecutableId == right.ExecutableId && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal) &&
        PathEquals(left.WorkingDirectory, right.WorkingDirectory) && PathEquals(left.BuiltManifest, right.BuiltManifest);

    private sealed class PluginBuildCandidateComparer : IEqualityComparer<PluginBuildCandidate>
    {
        public static PluginBuildCandidateComparer Instance { get; } = new();
        public bool Equals(PluginBuildCandidate? left, PluginBuildCandidate? right) => left is not null && right is not null && SameBuild(left, right);
        public int GetHashCode(PluginBuildCandidate value) => StringComparer.OrdinalIgnoreCase.GetHashCode(value.ProjectFile);
    }
}
