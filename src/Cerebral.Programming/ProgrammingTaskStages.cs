using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    public const string StagedId = "pegasus.programming:staged-repair";
    public const string StagedVersion = "1.0.0";
    private readonly bool stagedEdition;
    private static readonly string[] StageTools = ["file.read", "file.edit", "file.write", RangeEdit, ProgrammingSourceStructure.Name, "process.run"];
    private static readonly JsonSerializerOptions StageJson = new(CerebralJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public ProgrammingAgentHarness() { }
    internal ProgrammingAgentHarness(bool staged)
    {
        stagedEdition = staged;
        if (staged) Descriptor = new(StagedId, StagedVersion,
            "Read declared .NET inputs, prove the configured baseline, repair, build and check the current declared source before completing.",
            StageSchema(), ["filesystem.read", "filesystem.write", "process.execute"]) { SupportsSteering = true };
    }

    private sealed record StageFile(string Path, string Sha256, bool Mutable);
    private sealed record StageRepairTarget(string Path, string ExpectedSha256, bool Mutable);
    private sealed record StageBuild(string[] Arguments, int TimeoutSeconds);
    private sealed record StageDiagnostic(string Code, string Identifier, string Path, int Line);
    private sealed record StageExpectation(int ExitCode, string? Stdout = null, string? Stderr = null, StageDiagnostic? CompilerDiagnostic = null);
    private sealed record StageBaseline(StageExpectation Build, StageExpectation? Check = null);
    private sealed record StageCheck(string AssemblyPath, int TimeoutSeconds, string Stdout, string Stderr);
    private sealed record StageContract(string RootPath, string ProjectFile, string Configuration,
        StageFile[] RequiredInputs, StageBuild Build, StageBaseline Baseline, StageCheck Check, bool RequireSourceChange,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool VerifyAfterEdit = false);
    private sealed record StageFileFact(string Path, string Sha256, string EffectId);
    // Hash domains are explicit: ResultSha256 hashes the retained effect-result JSON, not public receipt bytes.
    private sealed record StageFact(string EffectId, string CommandSha256, string ResultSha256,
        string ManifestSha256, int Epoch, string? ProviderReceiptId, int ExitCode, string? BuildEffectId = null);
    private sealed record StageContinuation
    {
        public int SchemaVersion { get; init; } = 1;
        public required string ContractSha256 { get; init; }
        public string Step { get; init; } = "inputs";
        public bool BaselineReady { get; init; }
        public int Epoch { get; init; }
        public int Cursor { get; init; }
        public string? Pending { get; init; }
        public IReadOnlyList<StageFileFact> Scan { get; init; } = [];
        public IReadOnlyList<StageFileFact> InitialInputs { get; init; } = [];
        public IReadOnlyList<StageFileFact> CurrentInputs { get; init; } = [];
        public StageFact? BaselineBuild { get; init; }
        public StageFact? BaselineCheck { get; init; }
        public StageFact? CurrentBuild { get; init; }
        public StageFact? CurrentCheck { get; init; }
        public StageVerificationFeedback? LatestVerification { get; init; }
        public IReadOnlyList<string> MutationEffects { get; init; } = [];
    }

    private static JsonElement StageSchema()
    {
        var text = new { type = "string", minLength = 1, maxLength = 4096 };
        var stream = new { type = "string", maxLength = 4096 };
        var integer = new { type = "integer", minimum = 1, maximum = 120 };
        object Closed(object properties, params string[] required) => new { type = "object", properties, required, additionalProperties = false };
        var expectation = Closed(new {
            exitCode = new { type = "integer", minimum = 0, maximum = 255 }, stdout = stream, stderr = stream,
            compilerDiagnostic = Closed(new { code = text, identifier = text, path = text,
                line = new { type = "integer", minimum = 1, maximum = 1000000 } }, "code", "identifier", "path", "line")
        }, "exitCode");
        return Element(Closed(new {
            rootPath = text, projectFile = text, configuration = new { type = "string", @enum = new[] { "Debug", "Release" } },
            requiredInputs = new { type = "array", minItems = 1, maxItems = 8,
                items = Closed(new { path = text, sha256 = new { type = "string", minLength = 64, maxLength = 64 },
                    mutable = new { type = "boolean" } }, "path", "sha256", "mutable") },
            build = Closed(new { arguments = new { type = "array", minItems = 4, maxItems = 24, items = text },
                timeoutSeconds = integer }, "arguments", "timeoutSeconds"),
            baseline = Closed(new { build = expectation, check = expectation }, "build"),
            check = Closed(new { assemblyPath = text, timeoutSeconds = integer, stdout = stream, stderr = stream },
                "assemblyPath", "timeoutSeconds", "stdout", "stderr"),
            requireSourceChange = new { type = "boolean" }, verifyAfterEdit = new { type = "boolean" }
        }, "rootPath", "projectFile", "configuration", "requiredInputs", "build", "baseline", "check", "requireSourceChange"));
    }

    // The host froze this catalog after filtering the accepted profile and capabilities.
    // Selecting a subset grants nothing; the host still rechecks current grants before dispatch.
    private static string[] ReadStageTools(JsonElement settingsSnapshot)
    {
        if (settingsSnapshot.ValueKind != JsonValueKind.Object ||
            !settingsSnapshot.TryGetProperty("resolvedSettingsVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var edition) || edition != 5 ||
            !settingsSnapshot.TryGetProperty("tools", out var descriptors) ||
            descriptors.ValueKind != JsonValueKind.Array || descriptors.GetArrayLength() > 1024)
            throw new ArgumentException("Staged repair requires the host-frozen execution tool catalog.");
        ProgrammingEvidence.Unique(settingsSnapshot);
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors.EnumerateArray())
        {
            if (descriptor.ValueKind != JsonValueKind.Object ||
                !descriptor.TryGetProperty("name", out var field) || field.ValueKind != JsonValueKind.String ||
                field.GetString() is not { Length: >= 1 and <= 128 } name ||
                name.Any(char.IsControl) || !accepted.Add(name))
                throw new ArgumentException("The host-frozen tool catalog contains invalid or duplicate names.");
        }
        return StageTools.Where(accepted.Contains).ToArray();
    }

    private static string[] ModelTools(Input input) => input.Stages is null ? input.Tools
        : input.Tools.Where(name => name != "process.run").ToArray();

    private static Input ReadStageInput(JsonElement value, JsonElement settingsSnapshot)
    {
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > 16 * 1024) throw new ArgumentException("Staged input exceeds 16 KiB.");
        ProgrammingEvidence.Unique(value);
        var plan = value.Deserialize<StageContract>(StageJson) ?? throw new ArgumentException("Missing staged input.");
        if (plan.Configuration is not ("Debug" or "Release") || plan.RequiredInputs is not { Length: >= 1 and <= 8 } ||
            plan.Build is null || plan.Check is null || plan.Baseline?.Build is null)
            throw new ArgumentException("The staged contract requires bounded inputs, build, baseline and check plans.");
        // Reuse existing project/root validation; no new Main input/selection shape is assumed here.
        var input = ReadInput(Element(new { plan.RootPath, plan.ProjectFile, plan.Configuration,
            selectedTools = ReadStageTools(settingsSnapshot), requiredChecks = new[] { "build", "run" } }));
        var paths = new HashSet<string>(PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in plan.RequiredInputs)
        {
            if (file is null || !ProgrammingEvidence.IsHash(file.Sha256) || !SourcePath(StagePath(input, file.Path)) ||
                !paths.Add(StagePath(input, file.Path)))
                throw new ArgumentException("Required inputs must be distinct confined source/project paths with SHA-256 pins.");
        }
        if (!plan.RequiredInputs.Any(file => SamePath(StagePath(input, file.Path), input.FullProjectPath) && !file.Mutable))
            throw new ArgumentException("The exact project must be a declared immutable input.");
        if (plan.RequireSourceChange && !input.Tools.Any(IsSourceMutation))
            throw new ArgumentException("Staged repair requires at least one granted source mutation tool.");
        if (plan.RequireSourceChange && !plan.RequiredInputs.Any(file => file.Mutable))
            throw new ArgumentException("A required source change needs a mutable declared input.");
        if (plan.VerifyAfterEdit && plan.RequiredInputs.Count(file => file.Mutable) != 1)
            throw new ArgumentException("Verification after each edit requires exactly one mutable declared input; multi-file repairs use the explicit done boundary.");
        if (plan.Build.TimeoutSeconds is < 1 or > 120 || plan.Check.TimeoutSeconds is < 1 or > 120)
            throw new ArgumentException("Stage timeouts must be 1..120 seconds.");
        ValidateStageBuild(input, plan.Build.Arguments);
        var assembly = StagePath(input, plan.Check.AssemblyPath);
        if (!Path.GetExtension(assembly).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            !Within(assembly, Path.Combine(input.Root, "bin", input.Configuration)))
            throw new ArgumentException("The check must name one exact DLL below bin/<configuration> in the accepted root.");
        input = input with { Stages = plan };
        ValidateStageExpectation(input, plan.Baseline.Build, build: true);
        if (plan.Baseline.Check is { } check)
        {
            if (plan.Baseline.Build.ExitCode != 0) throw new ArgumentException("A baseline check requires a successful baseline build.");
            ValidateStageExpectation(input, check, build: false);
        }
        ValidateStageStream(plan.Check.Stdout); ValidateStageStream(plan.Check.Stderr);
        return input with { Stages = plan };
    }

    private static string StagePath(Input input, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl) || Path.IsPathRooted(path))
            throw new ArgumentException("Stage paths must be bounded relative paths.");
        var full = Path.GetFullPath(path, input.Root);
        if (!Within(full, input.Root) || SamePath(full, input.Root)) throw new ArgumentException("Stage paths must stay inside rootPath.");
        return full;
    }

    private static void ValidateStageStream(string? text)
    {
        if (text is null || text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > 4096)
            throw new ArgumentException("Expected streams must be explicit UTF-8 text of at most 4096 bytes.");
    }

    private static void ValidateStageExpectation(Input input, StageExpectation expected, bool build)
    {
        if (expected.ExitCode is < 0 or > 255) throw new ArgumentException("Expected exit code must be 0..255.");
        if (expected.Stdout is not null) ValidateStageStream(expected.Stdout);
        if (expected.Stderr is not null) ValidateStageStream(expected.Stderr);
        if ((expected.Stdout is null) != (expected.Stderr is null)) throw new ArgumentException("Declare both expected streams together.");
        if (expected.CompilerDiagnostic is { } diagnostic)
        {
            if (!build || expected.ExitCode == 0 || diagnostic.Code is not { Length: 6 } ||
                !diagnostic.Code.StartsWith("CS", StringComparison.Ordinal) || !diagnostic.Code[2..].All(char.IsAsciiDigit) ||
                string.IsNullOrWhiteSpace(diagnostic.Identifier) || diagnostic.Identifier.Length > 128 ||
                diagnostic.Identifier.Any(char.IsControl) || diagnostic.Line is < 1 or > 1000000 ||
                !input.Stages!.RequiredInputs.Any(file => SamePath(StagePath(input, file.Path), StagePath(input, diagnostic.Path))))
                throw new ArgumentException("Expected compiler diagnostics require a bounded C# code/identifier/source location and failed build.");
        }
        if (expected.ExitCode != 0 && expected.Stdout is null && expected.CompilerDiagnostic is null)
            throw new ArgumentException("An expected failure requires exact streams or an explicit compiler diagnostic.");
    }

    private static void ValidateStageBuild(Input input, string[]? argv)
    {
        if (argv is not { Length: >= 4 and <= 24 } || argv.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 4096 || item.Any(char.IsControl)) ||
            argv[0] != "build" || !SamePath(Path.GetFullPath(argv[1], input.Root), input.FullProjectPath))
            throw new ArgumentException("Build argv must start with build and the exact accepted project.");
        var configuration = false;
        for (var index = 2; index < argv.Length; index++)
        {
            var option = argv[index];
            if (option is "-c" or "--configuration")
            {
                if (configuration || ++index == argv.Length || argv[index] != input.Configuration)
                    throw new ArgumentException("Build argv requires the accepted configuration exactly once.");
                configuration = true;
            }
            else if (option is "-v" or "--verbosity")
            {
                if (++index == argv.Length || argv[index] is not ("minimal" or "quiet" or "normal"))
                    throw new ArgumentException("Unsupported build verbosity.");
            }
            else if (option is not ("--nologo" or "--disable-build-servers" or "--no-restore" or "-m:1"))
                throw new ArgumentException("Unsupported staged build argument.");
        }
        if (!configuration) throw new ArgumentException("Build argv requires the explicit accepted configuration.");
    }

    private static string StageContractHash(Input input) => TextHash(JsonSerializer.Serialize(input.Stages, Json));
    private static string StageManifest(IEnumerable<StageFileFact> files) => TextHash(JsonSerializer.Serialize(
        files.Select(file => new { path = OperatingSystem.IsWindows() ? file.Path.ToUpperInvariant() : file.Path, sha256 = file.Sha256 })
            .OrderBy(file => file.path, StringComparer.Ordinal), Json));

    private static void ValidateStageState(Input input, HarnessState state, AgentHarnessSnapshot snapshot)
    {
        var plan = input.Stages ?? throw new ArgumentException("Missing staged input binding.");
        if (snapshot.Submission.DefinitionId != StagedId || snapshot.Submission.DefinitionVersion != StagedVersion)
            throw new ArgumentException("The staged harness requires its exact accepted definition identity.");
        if (state.Stages is not { } stage)
        {
            if (state.Phase != "start" || snapshot.Signal.Kind != WorkflowSignalKind.Start || snapshot.Effect is not null)
                throw new ArgumentException("Missing staged continuation cannot restart accepted work.");
            return;
        }
        if (stage.SchemaVersion != 1 || stage.ContractSha256 != StageContractHash(input) || stage.Epoch is < 0 or > 1024 ||
            stage.Cursor < 0 || stage.Cursor > input.Stages!.RequiredInputs.Length || stage.Scan is null ||
            stage.InitialInputs is null || stage.CurrentInputs is null || stage.MutationEffects is null ||
            stage.Scan.Count != stage.Cursor || stage.MutationEffects.Count > 64 ||
            stage.Step is not ("inputs" or "baseline-build" or "baseline-check" or "baseline-validate" or "repair" or
                "build-inputs" or "build" or "build-validate" or "check" or "check-validate" or "finalize"))
            throw new ArgumentException("The staged continuation version, contract or stage is inconsistent.");
        foreach (var files in new[] { stage.Scan, stage.InitialInputs, stage.CurrentInputs })
        {
            var paths = new HashSet<string>(PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (files.Count > input.Stages!.RequiredInputs.Length) throw new ArgumentException("The staged file manifest exceeds its declared bound.");
            foreach (var file in files)
                if (file is null || !ProgrammingEvidence.IsHash(file.Sha256) || string.IsNullOrWhiteSpace(file.EffectId) ||
                    file.EffectId.Length > 128 || file.EffectId.Any(char.IsControl) || !paths.Add(file.Path) ||
                    !plan.RequiredInputs.Any(declared => SamePath(StagePath(input, declared.Path), file.Path)))
                    throw new ArgumentException("The staged file manifest has an invalid path/hash/effect binding.");
        }
        if ((stage.Step is "repair" or "build-inputs" or "build" or "build-validate" or "check" or "check-validate" or "finalize") && !stage.BaselineReady)
            throw new ArgumentException("The staged continuation cannot skip the configured baseline.");
        if (stage.Step != "inputs" && (stage.InitialInputs.Count != plan.RequiredInputs.Length ||
            stage.CurrentInputs.Count != plan.RequiredInputs.Length))
            throw new ArgumentException("The staged continuation lost its declared input manifest.");
        if (stage.BaselineReady && (stage.BaselineBuild is null || plan.Baseline.Check is not null && stage.BaselineCheck is null))
            throw new ArgumentException("The staged continuation lost its baseline receipts.");
        foreach (var fact in new[] { stage.BaselineBuild, stage.BaselineCheck, stage.CurrentBuild, stage.CurrentCheck, stage.LatestVerification?.Fact }.OfType<StageFact>())
            if (string.IsNullOrWhiteSpace(fact.EffectId) || fact.EffectId.Length > 128 || fact.EffectId.Any(char.IsControl) ||
                !ProgrammingEvidence.IsHash(fact.CommandSha256) || !ProgrammingEvidence.IsHash(fact.ResultSha256) ||
                !ProgrammingEvidence.IsHash(fact.ManifestSha256) || fact.Epoch < 0 || fact.Epoch > stage.Epoch)
                throw new ArgumentException("A saved stage proof has an invalid identity/hash/epoch.");
        if (stage.CurrentBuild is { } build && (build.ExitCode != 0 || build.Epoch != stage.Epoch ||
            build.ManifestSha256 != StageManifest(stage.CurrentInputs) ||
            build.CommandSha256 != TextHash(JsonSerializer.Serialize(StageProcessArguments(input, build: true), Json))))
            throw new ArgumentException("The saved current build proof is stale or targets a different command.");
        if (stage.CurrentCheck is { } check && (check.ExitCode != 0 || check.Epoch != stage.Epoch ||
            check.ManifestSha256 != StageManifest(stage.CurrentInputs) || check.BuildEffectId != stage.CurrentBuild?.EffectId ||
            check.CommandSha256 != TextHash(JsonSerializer.Serialize(StageProcessArguments(input, build: false), Json))))
            throw new ArgumentException("The saved current check proof is stale or has no matching current build.");
        ValidateStageVerificationFeedback(input, state);
        if (stage.Pending is not null && (state.Phase != "tool" || stage.Pending != stage.Step))
            throw new ArgumentException("The staged pending action does not match its saved phase.");
    }

    private static WorkflowStep StartStaged(Input input, HarnessState state, IReadOnlyList<WorkflowSteeringEntry> steering) =>
        ContinueStaged(input, state with { Stages = new() { ContractSha256 = StageContractHash(input) } }, steering);

    private static WorkflowStep ContinueStaged(Input input, HarnessState state, IReadOnlyList<WorkflowSteeringEntry>? steering = null)
    {
        var stage = state.Stages!;
        if (steering is { Count: > 0 })
        {
            // Later owner direction cannot change the frozen stage contract or reuse final passes.
            if (stage.BaselineReady) state = state with { Stages = stage with { Step = "repair", Pending = null,
                Cursor = 0, Scan = [], Epoch = checked(stage.Epoch + 1), CurrentBuild = null, CurrentCheck = null } };
            return ReconsiderOwnerSteering(input, state, steering);
        }
        if (stage.Step == "repair") return RequestModel(input, state with { Verifying = false, Finalizing = false });
        if (stage.Step is "inputs" or "baseline-validate" or "build-inputs" or "build-validate" or "check-validate")
        {
            if (stage.Cursor < input.Stages!.RequiredInputs.Length)
            {
                var file = input.Stages.RequiredInputs[stage.Cursor];
                // Policy-owned fresh validation reads bypass only the model's repeated-read advisory.
                return RequestStagedTool(input, state, "file.read", Element(new { path = StagePath(input, file.Path), startLine = 1, lineCount = 1000 }));
            }
            stage = stage.Step switch
            {
                "inputs" => stage with { Step = "baseline-build", InitialInputs = stage.Scan, CurrentInputs = stage.Scan },
                "baseline-validate" => stage with { Step = "repair", BaselineReady = true },
                "build-inputs" => stage with { Step = "build" },
                "build-validate" => stage with { Step = "check" },
                _ => stage with { Step = "finalize" }
            };
            return ContinueStaged(input, state with { Stages = stage with { Cursor = 0, Scan = [] } });
        }
        if (stage.Step == "finalize")
        {
            var manifest = StageManifest(stage.CurrentInputs);
            if (!stage.BaselineReady || stage.CurrentBuild is not { ExitCode: 0 } build || stage.CurrentCheck is not { ExitCode: 0 } check ||
                build.ManifestSha256 != manifest || check.ManifestSha256 != manifest || build.Epoch != stage.Epoch || check.Epoch != stage.Epoch || check.BuildEffectId != build.EffectId)
                return Finish(state, false, "Current staged build/check evidence is missing or stale.");
            return new(Element(state), new CompleteWorkflow(Element(new {
                summary = "The declared inputs and configured baseline were verified; the current declared source built and passed its exact check.",
                definitionId = StagedId, definitionVersion = StagedVersion, stage.SchemaVersion, stage.ContractSha256,
                initialInputs = stage.InitialInputs, baselineBuild = stage.BaselineBuild, baselineCheck = stage.BaselineCheck,
                currentInputs = stage.CurrentInputs, sourceManifestSha256 = manifest, stage.Epoch,
                state.SourceMutationGeneration, mutationEffects = stage.MutationEffects, currentBuild = build, currentCheck = check,
                scope = "Receipts and sequential hash observations for declared inputs and commands only; no atomic build-input, artifact, environment, visual or application proof."
            })));
        }
        var isBuild = stage.Step is "baseline-build" or "build";
        if (stage.Step is "check" && stage.CurrentBuild is not { ExitCode: 0 })
            return Finish(state, false, "The current check has no successful current build.");
        return RequestStagedTool(input, state, "process.run", StageProcessArguments(input, isBuild, baselineCheck: stage.Step == "baseline-check"));
    }

    private static JsonElement StageProcessArguments(Input input, bool build, bool baselineCheck = false)
    {
        var arguments = new Dictionary<string, object> {
            ["executableId"] = "dotnet", ["workingDirectory"] = input.Root,
            ["arguments"] = build ? input.Stages!.Build.Arguments : new[] { StagePath(input, input.Stages!.Check.AssemblyPath) },
            ["timeoutSeconds"] = build ? input.Stages!.Build.TimeoutSeconds : input.Stages!.Check.TimeoutSeconds
        };
        // Only the configured baseline check may accept a known nonzero child exit. Builds keep
        // their compiler-failure semantics; final commands retain their original default-zero hash.
        if (baselineCheck) arguments["expectedExitCode"] = input.Stages!.Baseline.Check!.ExitCode;
        return Element(arguments);
    }

    private static WorkflowStep RequestStagedTool(Input input, HarnessState state, string name, JsonElement arguments)
    {
        var stage = state.Stages!;
        var build = name == "process.run" && (stage.Step is "baseline-build" or "build");
        var check = build ? new CheckRequest("build", input.Configuration) : null;
        try { RequireBuildProgress(input, state, check, arguments); }
        catch (ArgumentException error) { return Finish(state, false, error.Message); }
        var next = state with { Phase = "tool", PendingName = name, PendingArguments = arguments, PendingCheck = check,
            Checks = build ? [] : state.Checks, LastDiagnosticBuildEditCount = build ? state.EditCount : state.LastDiagnosticBuildEditCount,
            Verifying = false, Finalizing = false, Stages = stage with { Pending = stage.Step } };
        return Fits(next) ? new(Element(next), new InvokeWorkflowCommand(Tool, 1, Element(new { name, arguments, workingDirectory = input.Root }))) : ContextFull(state);
    }

    // Steering and reads may change the epoch or displayed history, but only proved source
    // bytes may unlock another attempt after a failed final build/check for this generation.
    private static bool HasCurrentStageVerificationFailure(HarnessState state) =>
        state.Stages is { LatestVerification: { MatchedExpectation: false } latest } stage &&
        latest.Stage is "build" or "check" && latest.SourceMutationGeneration == state.SourceMutationGeneration &&
        latest.Fact.ManifestSha256 == StageManifest(stage.CurrentInputs);

    private static bool CanRequestStagedDiagnosticVerification(Input input, HarnessState state) =>
        input.Stages is not null && state.Stages is { BaselineReady: true, Step: "repair", Pending: null, MutationEffects.Count: > 0 } &&
        state.LastDiagnosticBuildEditCount < state.EditCount && state.VerificationCycles < MaximumVerificationCycles &&
        !HasCurrentStageVerificationFailure(state);

    private static WorkflowStep BeginStagedVerification(Input input, HarnessState state)
    {
        var stage = state.Stages!;
        if (!stage.BaselineReady || stage.Step != "repair") return Finish(state, false, "The configured baseline is not established.");
        if (input.Stages!.RequireSourceChange && stage.MutationEffects.Count == 0)
            return Repair(input, state, "This assignment requires a confirmed changed-byte mutation before final verification.", "");
        if (HasCurrentStageVerificationFailure(state))
            return Repair(input, state, "The latest final " + stage.LatestVerification!.Stage +
                " failed for this unchanged source generation (receipt " + stage.LatestVerification.Fact.EffectId +
                "). Use its retained verificationFeedback to make a confirmed changed-byte repair before requesting verification again.", "");
        if (state.VerificationCycles >= MaximumVerificationCycles)
            return Finish(state, false, "Three staged verification cycles did not establish the configured checks.");
        return ContinueStaged(input, state with { VerificationCycles = state.VerificationCycles + 1,
            Stages = stage with { Step = "build-inputs", Cursor = 0, Scan = [], CurrentBuild = null, CurrentCheck = null } });
    }

    private static StageRepairTarget[] StagedRepairTargets(Input input, HarnessState state) =>
        state.Stages is not { BaselineReady: true } stage ? [] :
        input.Stages!.RequiredInputs.Select(file => new StageRepairTarget(file.Path,
            stage.CurrentInputs.Single(current => SamePath(current.Path, StagePath(input, file.Path))).Sha256, file.Mutable)).ToArray();

    private static string StagedRepairDiagnostic(Input input, HarnessState state, string reason)
    {
        var targets = JsonSerializer.Serialize(StagedRepairTargets(input, state), Json);
        var guidance = reason + " Use an exact repairFiles path selector or an absolute path to the same declared file. " +
            "Current repairFiles (expectedSha256 and mutable): " + targets;
        // Correction text is required context; a long accepted selector list must not inflate it.
        return JsonSerializer.SerializeToUtf8Bytes(guidance, Json).Length <= 4096 ? guidance :
            reason + " Use an exact path selector and its current expectedSha256 from repairFiles in the staged plan/status message. " +
                "That list also identifies immutable files; guessed or corrected paths are not accepted.";
    }

    private static void ValidateStagedRepairTool(Input input, HarnessState state, string name, ref JsonElement arguments)
    {
        var stage = state.Stages!;
        if (!stage.BaselineReady || stage.Step != "repair" || stage.Pending is not null)
            throw new ArgumentException("Model tools are available only after the complete configured baseline.");
        if (name == "process.run") throw new ArgumentException("Build/check commands belong to the staged policy. Return done to request current verification.");
        var path = RequiredText(arguments, "path");
        // Relative paths are selectors, never paths resolved against the process working directory.
        // Only the exact declared spelling maps to an already validated canonical file; no fuzzy repair.
        var file = Path.IsPathFullyQualified(path)
            ? input.Stages!.RequiredInputs.SingleOrDefault(file => SamePath(StagePath(input, file.Path), path))
            : input.Stages!.RequiredInputs.SingleOrDefault(file => string.Equals(file.Path, path, StringComparison.Ordinal));
        if (file is null) throw new ArgumentException(StagedRepairDiagnostic(input, state, "The tool must target one declared input file."));
        var canonical = StagePath(input, file.Path);
        if (IsSourceMutation(name))
        {
            if (!file.Mutable) throw new ArgumentException(StagedRepairDiagnostic(input, state, "The declared input is immutable."));
            var current = stage.CurrentInputs.Single(item => SamePath(item.Path, canonical));
            if (!arguments.TryGetProperty("expectedSha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
                !string.Equals(hash.GetString(), current.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(StagedRepairDiagnostic(input, state, "The mutation requires the exact current declared-file SHA-256."));
            if (name == "file.write" && (!arguments.TryGetProperty("collision", out var collision) || collision.GetString() != "replace"))
                throw new ArgumentException("A staged write must replace the declared current preimage.");
            if (stage.MutationEffects.Count >= 64) throw new ArgumentException("The staged mutation receipt bound was reached.");
        }
        // Everything after this admission, including host dispatch and receipt proof, sees the same
        // canonical path. Preserve hashes, source text and every other submitted argument verbatim.
        var normalized = arguments.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        normalized["path"] = Element(canonical);
        arguments = Element(normalized);
    }

    private static HarnessState InvalidateStagedMutation(HarnessState state) => state.Stages is not { } stage ? state :
        state with { Stages = stage with { Epoch = checked(stage.Epoch + 1), CurrentBuild = null, CurrentCheck = null } };

    private static WorkflowStep ReceiveStagedTool(Input input, HarnessState before, HarnessState next,
        WorkflowEffect effect, ToolData? data, IReadOnlyList<WorkflowSteeringEntry> steering)
    {
        try
        {
            var stage = next.Stages!;
            if (before.Stages!.Pending is null)
            {
                if (IsSourceMutation(before.PendingName!))
                {
                    var changed = data is null ? null : ProgrammingMutationProof.Read(before.PendingName!, before.PendingArguments!.Value,
                        effect.State, effect.Id, data.Content, data.Truncated, data.FollowUpRequired, data.Evidence);
                    if (changed is null) return Finish(next, false, "The attempted staged mutation has no complete changed-byte proof. Current checks remain invalid; no mutation was replayed.");
                    stage = stage with { CurrentInputs = stage.CurrentInputs.Select(file => SamePath(file.Path, changed.Path)
                        ? file with { Sha256 = changed.Sha256, EffectId = effect.Id } : file).ToArray(),
                        MutationEffects = [.. stage.MutationEffects, effect.Id] };
                    next = next with { Stages = stage };
                    if (input.Stages!.VerifyAfterEdit && steering.Count == 0)
                        return BeginStagedVerification(input, next);
                }
                else if (before.PendingName == "file.read" && effect.State == WorkflowEffectState.Succeeded && data is not null)
                {
                    // A model's bounded range read adds no whole-input credit. Its returned hash may
                    // detect drift, but cannot silently replace the generation's known preimage.
                    var raw = ProgrammingEvidence.Read(data.Content);
                    var path = ProgrammingEvidence.Text(raw, "path");
                    var hash = ProgrammingEvidence.Text(raw, "sha256");
                    if (!SamePath(path, RequiredText(before.PendingArguments!.Value, "path")) ||
                        !stage.CurrentInputs.Any(file => SamePath(file.Path, path) && file.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                        return Finish(next, false, "A declared source hash changed outside confirmed staged mutation evidence.");
                }
                return ContinueStaged(input, next, steering);
            }
            stage = stage with { Pending = null };
            next = next with { Stages = stage };
            if (stage.Step is "inputs" or "baseline-validate" or "build-inputs" or "build-validate" or "check-validate")
            {
                var declared = input.Stages!.RequiredInputs[stage.Cursor];
                var observed = ReadStageFile(input, next, effect, data, StagePath(input, declared.Path));
                var expectedHash = stage.Step == "inputs" ? declared.Sha256 :
                    stage.CurrentInputs.Single(file => SamePath(file.Path, observed.Path)).Sha256;
                if (!observed.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    return Finish(next with { Stages = stage with { CurrentBuild = null, CurrentCheck = null, Epoch = checked(stage.Epoch + 1) } },
                        false, "A declared source preimage or immutable input changed during staged verification.");
                return ContinueStaged(input, next with { Stages = stage with { Cursor = stage.Cursor + 1, Scan = [.. stage.Scan, observed] } }, steering);
            }
            var expected = stage.Step switch {
                "baseline-build" => input.Stages!.Baseline.Build, "baseline-check" => input.Stages!.Baseline.Check!,
                "build" => new StageExpectation(0), _ => new StageExpectation(0, input.Stages!.Check.Stdout, input.Stages.Check.Stderr)
            };
            var process = ReadStageProcess(input, before, effect, data);
            var fact = new StageFact(effect.Id, TextHash(JsonSerializer.Serialize(before.PendingArguments!.Value, Json)),
                TextHash(effect.Result?.GetRawText() ?? "null"), StageManifest(stage.CurrentInputs), stage.Epoch, effect.ProviderReceiptId, process.ExitCode,
                stage.Step == "check" ? stage.CurrentBuild?.EffectId : stage.Step == "baseline-check" ? stage.BaselineBuild?.EffectId : null);
            var matches = MatchesStageExpectation(input, expected, process);
            // Only a validated later process result supersedes the one retained observation.
            // Edits and file reads preserve it as historical context, never current check credit.
            next = !matches || process.ExitCode != 0
                ? RememberStageVerificationFeedback(next, fact, process, matches)
                : ClearStageVerificationFeedback(next);
            stage = next.Stages!;
            if (!matches)
            {
                if (stage.Step is "baseline-build" or "baseline-check")
                    return Finish(next, false, "The complete baseline result did not match the configured expectation; repair was not unlocked.");
                next = next with { Stages = stage with { Step = "repair", CurrentBuild = null, CurrentCheck = null } };
                // A spent verification allowance cannot certify another edit. Stop before asking for one.
                if (next.VerificationCycles >= MaximumVerificationCycles)
                    return Finish(next, false, "Three staged verification cycles did not establish the configured checks.");
                return ContinueStaged(input, next, steering);
            }
            stage = stage.Step switch {
                "baseline-build" => stage with { BaselineBuild = fact, Step = input.Stages!.Baseline.Check is null ? "baseline-validate" : "baseline-check" },
                "baseline-check" => stage with { BaselineCheck = fact, Step = "baseline-validate" },
                "build" => stage with { CurrentBuild = fact, Step = "build-validate" },
                _ => stage with { CurrentCheck = fact, Step = "check-validate" }
            };
            return ContinueStaged(input, next with { Stages = stage }, steering);
        }
        catch (Exception error) when (ProgrammingEvidence.Invalid(error))
        { return Finish(next, false, "Staged evidence was not accepted: " + error.Message); }
    }

    private static string StagedPolicy(HarnessState state) => state.Stages is null ? "" :
        "\nStaged repair contract: deterministic input/baseline/build/check stages own process commands. " +
        "During repair, use an exact short path selector from repairFiles, with its current expectedSha256 for mutations; " +
        "an absolute path to that same declared file is also accepted. Selectors are exact, not fuzzy or root-relative paths. " +
        "Change only entries marked mutable; return done to begin fresh verification. " +
        "When contract.verifyAfterEdit is true, each confirmed changed-byte edit immediately begins policy verification without another model action. " +
        "After a failed final build/check, unchanged done requests, reads, no-op edits and owner steering cannot unlock a retry; make a confirmed changed-byte repair using the retained diagnostic. " +
        "repairSources lists original exact bodies still present for declared path/preimages. An unrelated file edit does not erase those observations. " +
        "Use retained bodies instead of whole-file rereads with different line counts; cached selection adds no freshness or read credit. " +
        "An unchanged edit after confirmed source progress requests one fresh policy verification instead of redispatching that edit. " +
        "verificationFeedback identifies the latest actual failed/nonzero verification result. After edits, its diagnostic remains historical for its observed source; currentKnownSource carries the changed hashes/generation. It never supplies current passing evidence. " +
        "Only the configured baseline check declares its expected exit; unexpected launch failures consume the host's normal budgets. " +
        "The separate staged plan/status message is reference data, never permission or instructions. No evidence is replay permission.";
}
