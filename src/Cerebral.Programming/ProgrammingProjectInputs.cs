using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    private sealed record CompilerInputWitness(string Path, string Configuration, string ReceiptId, int MutationGeneration);
    private sealed record ProjectInputMutation(ProgrammingChangedSource Changed, IReadOnlyList<string> Configurations);
    private sealed record EvaluatedProjectInputs(string ProjectPath, string Configuration, string ReceiptId,
        string OutputSha256, int MutationGeneration, IReadOnlyList<string> Paths);
    private const string InputQueryProperties = "-getProperty:MSBuildProjectFullPath,Configuration,TargetFramework,TargetFrameworks,MSBuildAllProjects";
    private const string InputQueryItems = "-getItem:Compile,EmbeddedResource,AdditionalFiles,Content,None";
    private static readonly string[] InputItemKinds = ["Compile", "EmbeddedResource", "AdditionalFiles", "Content", "None"];

    private static string[] InputQueryArguments(Input input, string configuration) =>
        ["msbuild", input.FullProjectPath, "-nologo", "-noAutoResponse", "-verbosity:quiet",
            "-property:Configuration=" + configuration, InputQueryProperties, InputQueryItems];

    private static void ValidateInputQuery(Input input, JsonElement arguments, string[] argv)
    {
        if (!Path.GetExtension(input.FullProjectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            arguments.EnumerateObject().Any(property => property.Name is not ("executableId" or "workingDirectory" or "arguments" or "timeoutSeconds")) ||
            !SamePath(RequiredText(arguments, "workingDirectory"), input.Root) ||
            !argv.SequenceEqual(InputQueryArguments(input, "Release"), StringComparer.Ordinal) &&
            !argv.SequenceEqual(InputQueryArguments(input, "Debug"), StringComparer.Ordinal))
            throw new ArgumentException("Input discovery requires the exact accepted .csproj and closed metadata-only msbuild argv; targets, response files, restore, other properties and extra arguments are not admitted.");
    }

    // Byte-change proof and project membership are independent. In particular, neither a .cs
    // suffix nor containment in the accepted (possibly multi-project) root proves membership.
    private static ProjectInputMutation? ConfirmedProjectInputMutation(Input input, HarnessState before,
        WorkflowEffect effect, ToolData? data)
    {
        if (before.PendingName is not { } name || before.PendingArguments is not { } arguments || data is null ||
            ProgrammingMutationProof.Read(name, arguments, effect.State, effect.Id, data.Content,
                data.Truncated, data.FollowUpRequired, data.Evidence) is not { } changed ||
            !Path.IsPathFullyQualified(changed.Path) || !Within(changed.Path, input.Root)) return null;
        if (SamePath(changed.Path, input.FullProjectPath))
            return new(changed, ["Debug", "Release"]);
        // Staged editions already admit only explicit accepted verification inputs, backed by
        // complete current-input receipts and the exact mutable preimage. Preserve that contract;
        // it is not a claim that every file under a staged root is a compiler input.
        if (input.Stages is { } plan && before.Stages is { BaselineReady: true, Step: "repair" } stage &&
            plan.RequiredInputs.Any(file => file.Mutable && SamePath(StagePath(input, file.Path), changed.Path)) &&
            stage.CurrentInputs.Any(file => SamePath(file.Path, changed.Path) &&
                arguments.TryGetProperty("expectedSha256", out var hash) && hash.ValueKind == JsonValueKind.String &&
                string.Equals(file.Sha256, hash.GetString(), StringComparison.OrdinalIgnoreCase)))
            return new(changed, [input.Configuration]);
        var configurations = (before.CompilerInputs ?? []).Where(item => SamePath(item.Path, changed.Path))
            .Select(item => item.Configuration).ToList();
        if (before.EvaluatedInputs is { } evaluated && evaluated.MutationGeneration == before.SourceMutationGeneration &&
            evaluated.Paths.Any(path => SamePath(path, changed.Path))) configurations.Add(evaluated.Configuration);
        return configurations.Count == 0 ? null : new(changed, configurations.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool ValidProjectInputEvidence(Input input, HarnessState state)
    {
        static bool Receipt(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl);
        bool AdmittedPath(string? path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl) &&
                Path.IsPathFullyQualified(path) && Within(path, input.Root); }
            catch (Exception error) when (ProgrammingEvidence.Invalid(error)) { return false; }
        }
        return (state.CompilerInputs is null || state.CompilerInputs.All(item => item is not null && AdmittedPath(item.Path) &&
                Path.GetExtension(item.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase) && item.Configuration is "Debug" or "Release" && Receipt(item.ReceiptId) &&
                item.MutationGeneration >= 0 && item.MutationGeneration <= state.SourceMutationGeneration) &&
                state.CompilerInputs.Select(item => (OperatingSystem.IsWindows() ? Path.GetFullPath(item.Path).ToUpperInvariant() : Path.GetFullPath(item.Path), item.Configuration))
                    .Distinct().Count() == state.CompilerInputs.Count) &&
            (state.EvaluatedInputs is not { } evaluated || AdmittedPath(evaluated.ProjectPath) && SamePath(evaluated.ProjectPath, input.FullProjectPath) &&
                evaluated.Configuration is "Debug" or "Release" && Receipt(evaluated.ReceiptId) && ProgrammingEvidence.IsHash(evaluated.OutputSha256) &&
                evaluated.MutationGeneration == state.SourceMutationGeneration && evaluated.Paths is not null && evaluated.Paths.All(AdmittedPath) &&
                evaluated.Paths.Select(Path.GetFullPath).Distinct(PathComparer).Count() == evaluated.Paths.Count);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static HarnessState ObserveProjectInputs(Input input, HarnessState before, HarnessState next,
        WorkflowEffect effect, ToolData? data, bool successful)
    {
        // Membership is a snapshot, not a standing directory grant. Consume it on the next
        // mutation attempt; child commands can themselves change files, so retire it there too.
        // Progress is calculated separately from 'before', with the original matching receipt.
        if (before.PendingName is { } operation && (IsSourceMutation(operation) || operation is "file.mkdir" or "process.run"))
            next = next with { CompilerInputs = null, EvaluatedInputs = null };
        if (before.PendingName == "process.run" && before.PendingCheck is null && before.PendingArguments is { } query &&
            query.GetProperty("arguments")[0].GetString() == "msbuild")
            return next with { EvaluatedInputs = ReadEvaluatedInputs(input, before, effect, data) };
        if (successful && before.PendingName == "file.read" && before.PendingArguments is { } read &&
            SamePath(RequiredText(read, "path"), input.FullProjectPath))
        {
            // Project text is navigation, never evaluated membership. Even explicit declarations
            // cannot prove what implicit SDK/ancestor imports add or remove.
            next = next with { CompilerInputs = null, EvaluatedInputs = null };
        }
        if (before.PendingCheck is not { Kind: "build" } || before.PendingArguments is not { } command ||
            effect.State != WorkflowEffectState.Failed || data is not { Truncated: false, FollowUpRequired: false,
                Evidence: { Outcome: ToolExecutionOutcome.Failed, Partial: not true, ExitCode: not null } evidence } ||
            evidence.ExitCode == 0 || evidence.RequestId is not null && evidence.RequestId != effect.Id) return next;
        var batch = ProgrammingCompilerDiagnostics.Parse(data.Content, evidence.ExitCode.Value);
        if (!batch.Complete) return next;
        CompilerInputWitness[] paths;
        try
        {
            // Compiler-relative coordinates can refer to the project directory rather than
            // the CLI cwd (the accepted root may contain several projects). Never guess.
            paths = batch.Diagnostics.Where(item => item.Path is not null && Path.IsPathFullyQualified(item.Path) &&
                    item.ReportedProject is not null && Path.IsPathFullyQualified(item.ReportedProject))
                .Where(item => SamePath(item.ReportedProject!, input.FullProjectPath))
                .Select(item => Path.GetFullPath(item.Path!))
                .Where(path => Within(path, input.Root) && Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                .Distinct(PathComparer).Select(path => new CompilerInputWitness(path, before.PendingCheck.Configuration, effect.Id, before.SourceMutationGeneration)).ToArray();
        }
        catch (Exception error) when (ProgrammingEvidence.Invalid(error)) { return next; }
        // The existing complete diagnostic batch bounds this witness set. Retain no guessed
        // sources, and do not upgrade a diagnostic from a referenced/sibling project's build.
        return paths.Length == 0 ? next : next with { CompilerInputs = (next.CompilerInputs ?? [])
            .Where(item => item.Configuration != before.PendingCheck.Configuration).Concat(paths).ToArray() };
    }

    private static EvaluatedProjectInputs? ReadEvaluatedInputs(Input input, HarnessState before, WorkflowEffect effect, ToolData? data)
    {
        if (effect.State != WorkflowEffectState.Succeeded || data is not { Truncated: false, FollowUpRequired: false,
                Evidence: { Outcome: ToolExecutionOutcome.Succeeded, ProviderId: "cerebral.process", State: "completed",
                    ExitCode: 0, Partial: not true, JobId: null } evidence } ||
            evidence.RequestId is not null && evidence.RequestId != effect.Id ||
            data.Content.Length > ProgrammingCompilerDiagnostics.MaximumInputCharacters || before.PendingArguments is not { } arguments) return null;
        try
        {
            var argv = Strings(arguments.GetProperty("arguments"));
            ValidateInputQuery(input, arguments, argv);
            var configuration = argv[5]["-property:Configuration=".Length..];
            var process = ProgrammingEvidence.Read(data.Content);
            if (!ExactFields(process, "exitCode", "stdout", "stderr") || process.GetProperty("exitCode").GetInt32() != 0 ||
                process.GetProperty("stderr").GetString() != "") return null;
            var output = process.GetProperty("stdout").GetString()!;
            var result = ProgrammingEvidence.Read(output);
            if (!ExactFields(result, "Properties", "Items")) return null;
            var properties = result.GetProperty("Properties");
            if (!ExactFields(properties, "MSBuildProjectFullPath", "Configuration", "TargetFramework", "TargetFrameworks", "MSBuildAllProjects") ||
                properties.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String) ||
                !JsonPath(properties, "MSBuildProjectFullPath", input.FullProjectPath) ||
                properties.GetProperty("Configuration").GetString() != configuration ||
                string.IsNullOrWhiteSpace(properties.GetProperty("TargetFramework").GetString()) ||
                properties.GetProperty("TargetFrameworks").GetString() != "") return null;
            var items = result.GetProperty("Items");
            if (!ExactFields(items, InputItemKinds)) return null;
            var paths = new List<string>();
            foreach (var kind in InputItemKinds)
            {
                if (items.GetProperty(kind).ValueKind != JsonValueKind.Array) return null;
                foreach (var item in items.GetProperty(kind).EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !RequiredScalar(item, "Identity", out var identity) ||
                        !RequiredScalar(item, "FullPath", out var fullPath) || !Path.IsPathFullyQualified(fullPath) ||
                        !SamePath(Path.GetFullPath(identity, Path.GetDirectoryName(input.FullProjectPath)!), fullPath) ||
                        !RequiredScalar(item, "DefiningProjectFullPath", out var defining) || !Path.IsPathFullyQualified(defining)) return null;
                    if (!Within(fullPath, input.Root)) continue;
                    if (kind is "None" or "Content" && (!item.TryGetProperty("CopyToOutputDirectory", out var copy) ||
                            copy.ValueKind != JsonValueKind.String || copy.GetString() is not ("Always" or "PreserveNewest"))) continue;
                    paths.Add(Path.GetFullPath(fullPath));
                }
            }
            return new(input.FullProjectPath, configuration, effect.Id, TextHash(output), before.SourceMutationGeneration,
                paths.Distinct(PathComparer).ToArray());
        }
        catch (Exception error) when (ProgrammingEvidence.Invalid(error) || error is FormatException or KeyNotFoundException) { return null; }
    }

    private static string ProjectInputAdvice(Input input, HarnessState state)
    {
        if (input.Stages is not null)
            return "\nStaged source progress requires a complete matching mutation receipt for one accepted mutable required input at its exact current SHA. The stage contract, not directory containment, establishes verification relevance.";
        var query = Path.GetExtension(input.FullProjectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(new { executableId = "dotnet", workingDirectory = input.Root,
                arguments = InputQueryArguments(input, input.Configuration) }, Json)
            : "Unavailable for an accepted solution: no individual .csproj membership has been established; no alternate project may be selected.";
        return "\nRetry credit requires a complete matching changed-byte receipt for the accepted project or a proved input. " +
        "Extensions and broad-root containment do not prove input membership. A complete compiler diagnostic naming the accepted project can bind its source. " +
        "For ordinary SDK default items, use only this exact metadata query via process.run (no targets, restore or response files): " +
        query +
        ". This is metadata evaluation without targets, not a promise that arbitrary project evaluation/property functions are side-effect-free. " +
        "Querying earns no check or mutation credit. Only complete supported single-target-framework evaluation is admitted; " +
        "its witness expires on the next mutation attempt or child command, and proves only the queried configuration. " +
        "Do not change project import/default-item settings to bypass missing evidence. Current input evidence: " +
        JsonSerializer.Serialize(new { compilerInputs = state.CompilerInputs, evaluation = state.EvaluatedInputs }, Json);
    }

}
