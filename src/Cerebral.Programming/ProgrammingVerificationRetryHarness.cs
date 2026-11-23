using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

/// <summary>Immutable Develop edition that preserves launch allowance for post-repair verification.</summary>
public sealed class ProgrammingVerificationRetryHarness : IAgentHarness
{
    public const string Version = "1.3.23";
    public const string RunArgumentsVersion = "1.3.24";
    private readonly ProgrammingAgentHarness policy = new();
    public AgentHarnessDescriptor Descriptor { get; }

    public ProgrammingVerificationRetryHarness() : this(runArguments: false) { }

    internal ProgrammingVerificationRetryHarness(bool runArguments)
    {
        var descriptor = new ProgrammingAgentHarness().Descriptor;
        Descriptor = descriptor with
        {
            Version = runArguments ? RunArgumentsVersion : Version,
            Description = runArguments
                ? "Develop one explicit .NET project with verified receipts, frozen console application arguments and proved source progress before repeating a failed run or test."
                : "Develop one explicit .NET project with verified receipts; require proved source progress before repeating a failed run or test.",
            InputSchema = runArguments ? RunArgumentsSchema(descriptor.InputSchema) : descriptor.InputSchema
        };
    }

    private static JsonElement RunArgumentsSchema(JsonElement schema)
    {
        var value = JsonNode.Parse(schema.GetRawText())!.AsObject();
        // This is only the process contract's outer ceiling. The normalized CLI head consumes
        // part of it, and actual serialized input/command/state envelopes remain authoritative.
        value["properties"]!["runArguments"] = new JsonObject
        {
            ["type"] = "array", ["maxItems"] = 256,
            ["items"] = new JsonObject { ["type"] = "string" }
        };
        return CerebralJson.Element(value);
    }

    public WorkflowStep Advance(AgentHarnessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Submission.DefinitionId != ProgrammingAgentHarness.Id ||
            snapshot.Submission.DefinitionVersion != Descriptor.Version)
            return new(snapshot.State.Clone(), new CompleteWorkflow(CerebralJson.Element(new
                { summary = "Verification retry protection requires the exact Develop " + Descriptor.Version + " edition." }), false));
        return policy.Advance(snapshot);
    }
}

public sealed partial class ProgrammingAgentHarness
{
    // Match process.run's visible argv ceiling. The host separately includes its private
    // registered prefix; no policy may assume that prefix is empty or bypass the host check.
    private const int MaximumProcessArguments = 256;
    private const int MaximumProcessArgumentCharacters = 32768;
    private const int MaximumToolInputBytes = 1048576;

    private static Input ReadRunArguments(Input input, JsonElement value)
    {
        var inputBytes = Encoding.UTF8.GetByteCount(value.GetRawText());
        if (inputBytes > WorkflowContract.MaximumStateBytes)
            throw new ArgumentException($"Accepted workflow input is {inputBytes}/{WorkflowContract.MaximumStateBytes} UTF-8 bytes; no action was dispatched.");
        var arguments = value.TryGetProperty("runArguments", out var supplied) ? Strings(supplied) : [];
        if (arguments.Any(argument => argument.Contains('\0')))
            throw new ArgumentException("runArguments may not contain NUL; no action was dispatched.");
        if (arguments.Length != 0 && !input.Checks.Contains("run", StringComparer.Ordinal))
            throw new ArgumentException("Nonempty runArguments require an accepted run check.");
        var result = input with { SupportsRunArguments = true, RunArguments = arguments };
        // The automatic verifier is part of this accepted contract, so reject an immutable tail
        // that already cannot fit its required head, rather than dropping arguments later.
        if (input.Checks.Contains("run", StringComparer.Ordinal))
        {
            var head = AutomaticVerificationArguments(input, "run", input.Checks.Contains("build", StringComparer.Ordinal));
            _ = CanonicalVerificationArguments(result, "run", head.Skip(3).ToArray(), suppliedTail: null);
        }
        return result;
    }

    private static List<string> AutomaticVerificationArguments(Input input, string kind, bool hasCurrentBuild)
    {
        var arguments = new List<string> { kind };
        if (kind == "run") arguments.Add("--project");
        arguments.Add(input.ProjectFile);
        arguments.AddRange(["--configuration", input.Configuration, "--disable-build-servers", "--verbosity", "minimal"]);
        if (kind is "test" or "run" && hasCurrentBuild) arguments.Add("--no-build");
        return arguments;
    }

    private static string[] CanonicalVerificationArguments(Input input, string kind, IReadOnlyList<string> options,
        string[]? suppliedTail)
    {
        string[] head = kind == "run" ? [kind, "--project", input.ProjectFile, .. options] : [kind, input.ProjectFile, .. options];
        if (!input.SupportsRunArguments) return head;
        if (suppliedTail is not null && !suppliedTail.SequenceEqual(input.RunArguments, StringComparer.Ordinal))
            throw new ArgumentException("Application argv after -- must exactly match the immutable accepted runArguments. Changing an output filename does not authorize a different invocation or reset failed checks.");
        var tail = kind == "run" ? input.RunArguments : [];
        if (tail.Length != 0) head = [.. head, "--"];
        var count = (long)head.Length + tail.Length;
        var characters = head.Sum(argument => (long)argument.Length) + tail.Sum(argument => (long)argument.Length);
        if (head.Any(argument => argument.Contains('\0')) || tail.Any(argument => argument.Contains('\0')))
            throw new ArgumentException("Process arguments may not contain NUL.");
        if (count > MaximumProcessArguments || characters > MaximumProcessArgumentCharacters)
            throw new ArgumentException($"Normalized argv has {count} arguments and {characters} UTF-16 code units; process.run allows {MaximumProcessArguments}/{MaximumProcessArgumentCharacters}. " +
                $"The accepted normalized head uses {head.Length} arguments/{head.Sum(argument => (long)argument.Length)} code units; remaining tail allowance is " +
                $"{MaximumProcessArguments - head.Length}/{MaximumProcessArgumentCharacters - head.Sum(argument => (long)argument.Length)} before any host-owned registered prefix. The frozen tail was not truncated; no action was dispatched.");
        return [.. head, .. tail];
    }

    private static bool HasCliNoBuild(JsonElement arguments) =>
        Strings(arguments.GetProperty("arguments")).TakeWhile(argument => argument != "--")
            .Contains("--no-build", StringComparer.Ordinal);

    private static bool CompleteVerificationReceipt(WorkflowEffect effect, ToolData data) =>
        effect.State == WorkflowEffectState.Succeeded && !data.Truncated && !data.FollowUpRequired &&
        data.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, Partial: not true, ExitCode: 0 } evidence &&
        (evidence.RequestId is null || evidence.RequestId == effect.Id);

    private static WorkflowStep InvokeModelCommand(Input input, HarnessState state, string command)
    {
        var arguments = GenerationArguments(input, state);
        if (input.SupportsRunArguments)
        {
            var bytes = Encoding.UTF8.GetByteCount(arguments.GetRawText());
            if (bytes > WorkflowContract.MaximumStateBytes)
                return Finish(state, false, $"The configured console invocation's model command is {bytes}/{WorkflowContract.MaximumStateBytes} UTF-8 bytes. " +
                    "The immutable arguments were not truncated and no effect was requested.");
        }
        return new(Element(state), new InvokeWorkflowCommand(command, 1, arguments));
    }

    // Input fixes the project. The four possible kind/configuration lanes remain until proved
    // source progress; reporting flags, rebuilds and context changes cannot spend another launch.
    private sealed record FailedVerificationObservation(string Kind, string Configuration, string ReceiptId, int ExitCode);

    private static bool ValidVerificationFailureState(HarnessState state) => state.FailedVerificationChecks is not { } rows ||
        rows.Count <= 4 && rows.All(item => item is not null && item.Kind is "run" or "test" &&
            item.Configuration is "Debug" or "Release" && item.ExitCode != 0 &&
            !string.IsNullOrWhiteSpace(item.ReceiptId) && item.ReceiptId.Length <= 128 && !item.ReceiptId.Any(char.IsControl)) &&
        rows.Select(item => (item.Kind, item.Configuration)).Distinct().Count() == rows.Count;

    private static void RequireVerificationProgress(Input input, HarnessState state, CheckRequest? check)
    {
        if (!input.GuardFailedChecks || check is not { Kind: "run" or "test" }) return;
        var failed = state.FailedVerificationChecks?.SingleOrDefault(item =>
            item.Kind == check.Kind && item.Configuration == check.Configuration);
        if (failed is not null)
            throw new ArgumentException($"The {check.Kind} check already failed for the accepted project in {check.Configuration} " +
                $"(exit {failed.ExitCode}, receipt {failed.ReceiptId}). Another equivalent check was not dispatched. " +
                "Inspect that exact failure receipt and repair the source before retrying, or stop truthfully if no safe repair is known. " +
                "Only a complete matching receipt proving changed source bytes or source creation unlocks it; " +
                "rebuilding, reporting/timeout flags, no-op writes, unrelated files, reads, compaction and steering do not.");
    }

    private static HarnessState ObserveVerificationFailure(Input input, HarnessState before, HarnessState next,
        WorkflowEffect effect, ToolData? data)
    {
        if (!input.GuardFailedChecks) return next;
        if (ConfirmedProjectInputMutation(input, before, effect, data) is { } mutation)
        {
            var remaining = (before.FailedVerificationChecks ?? []).Where(item =>
                !mutation.Configurations.Contains(item.Configuration, StringComparer.Ordinal)).ToArray();
            return next with { FailedVerificationChecks = remaining.Length == 0 ? null : remaining };
        }
        // Only the host's confirmed completed child failure establishes this policy observation.
        // Unknown/cancelled effects retain their existing terminal handling before this method.
        if (before.PendingCheck is not { Kind: "run" or "test" } check || effect.State != WorkflowEffectState.Failed ||
            data is not { Truncated: false, FollowUpRequired: false, Evidence: { Outcome: ToolExecutionOutcome.Failed,
                Partial: not true, ExitCode: not null } evidence } || evidence.ExitCode == 0 ||
            evidence.RequestId is not null && evidence.RequestId != effect.Id) return next;
        var observations = (before.FailedVerificationChecks ?? [])
            .Where(item => item.Kind != check.Kind || item.Configuration != check.Configuration)
            .Append(new(check.Kind, check.Configuration, effect.Id, evidence.ExitCode!.Value)).ToArray();
        return next with { FailedVerificationChecks = observations };
    }

    private static string VerificationFailureAdvice(HarnessState state) =>
        "\nAfter a confirmed nonzero run/test failure, inspect its exact receipt and correct source before another equivalent check. " +
        "A model claim or a successful rebuild cannot unlock it or count as an executed passing check. " +
        "Confirmed failed checks awaiting proved source repair:\n" +
        JsonSerializer.Serialize(state.FailedVerificationChecks ?? [], Json);
}
