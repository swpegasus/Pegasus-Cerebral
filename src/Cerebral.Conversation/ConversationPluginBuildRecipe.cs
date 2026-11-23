using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    /// <summary>
    /// Converts a source-authoring stall into one real compiler observation for the exact retained
    /// plugin recipe. This is deliberately narrower than a general workflow recipe: the model must
    /// already have selected process.run, a confirmed mutation must identify the stalled file, and
    /// ordinary tool admission still owns the eventual effect.
    /// </summary>
    private static WorkflowStep? TryRequestPluginDiagnosticBuild(Input input, HarnessState state,
        string stalledTool, JsonElement stalledArguments, string runId)
    {
        if (state.PluginWork is not { } work || work.SourceGeneration <= 0 ||
            work.SuccessfulBuildGeneration == work.SourceGeneration ||
            work.DiagnosticBuildAttemptGeneration == work.SourceGeneration ||
            work.LastMutationPath is null ||
            !StalledActionTargetsLastMutation(input, stalledTool, stalledArguments, work.LastMutationPath))
            return null;

        var process = state.Selected.FirstOrDefault(tool =>
            tool.Name == "process.run" && tool.InputSchema is { ValueKind: JsonValueKind.Object });
        if (process?.InputSchema is not { }) return null;

        var arguments = Element(new
        {
            executableId = work.Build.ExecutableId,
            arguments = work.Build.Arguments,
            workingDirectory = work.Build.WorkingDirectory
        });
        // Reuse the normal Conversation request path so current binding, selected-schema, rejection,
        // size and host tool admission all remain authoritative. Mark the generation only after that
        // path produced a dispatch, but before returning the effect, so unknown/cancelled outcomes and
        // durable reopen cannot silently replay the automatic attempt.
        var requested = RequestTool(input, state, "process.run", arguments, runId);
        if (requested.Next is not InvokeWorkflowCommand { CommandId: Tool } command ||
            command.Arguments.ValueKind != JsonValueKind.Object ||
            !command.Arguments.TryGetProperty("name", out var name) || name.GetString() != "process.run")
            return requested;

        var dispatched = requested.State.Deserialize<HarnessState>(Json)
            ?? throw new ArgumentException("The diagnostic build state is missing.");
        if (dispatched.PluginWork is not { } bound || bound.SourceGeneration != work.SourceGeneration ||
            !SameBuild(bound.Build, work.Build))
            throw new ArgumentException("The diagnostic build no longer matches its retained plugin recipe.");
        dispatched = dispatched with
        {
            PluginWork = bound with { DiagnosticBuildAttemptGeneration = bound.SourceGeneration }
        };
        if (!Fits(dispatched))
            return Repair(input, state,
                "The bounded diagnostic-build marker does not fit the saved workflow state. No build was dispatched.");
        return requested with { State = Element(dispatched) };
    }

    private static bool StalledActionTargetsLastMutation(Input input, string name, JsonElement arguments,
        string lastMutationPath)
    {
        if (name == "file.read")
            return TryActionPath(input, arguments, "path", out var readPath) &&
                   PathEquals(readPath, lastMutationPath);
        if (name != "file.edit" ||
            !TryActionPath(input, arguments, "path", out var editPath) ||
            !PathEquals(editPath, lastMutationPath) ||
            !arguments.TryGetProperty("oldText", out var oldText) || oldText.ValueKind != JsonValueKind.String ||
            !arguments.TryGetProperty("newText", out var newText) || newText.ValueKind != JsonValueKind.String)
            return false;
        return string.Equals(oldText.GetString(), newText.GetString(), StringComparison.Ordinal);
    }

    private static PluginWorkBinding RecordConfirmedCanonicalBuildAttempt(Input input, HarnessState state,
        PluginWorkBinding work, JsonElement arguments, ToolData? data, WorkflowEffect effect)
    {
        if (state.PendingName != "process.run" || !MatchesBuild(input, work.Build, arguments) ||
            data is null || data.Evidence?.Outcome == ToolExecutionOutcome.Rejected)
            return work;
        // Retain only the latest confirmed canonical build coordinate for this binding. A
        // source edit is not a successful build, and unrelated commands cannot replace it.
        var confirmed = data.Evidence is { Outcome: ToolExecutionOutcome.Succeeded or ToolExecutionOutcome.Failed } &&
            data.Evidence.Partial != true;
        return work with
        {
            DiagnosticBuildAttemptGeneration = work.SourceGeneration,
            LastCanonicalBuildEffectId = confirmed ? effect.Id : work.LastCanonicalBuildEffectId,
            LastCanonicalBuildGeneration = confirmed ? work.SourceGeneration : work.LastCanonicalBuildGeneration,
            LastCanonicalBuildFailure = confirmed
                ? CaptureCanonicalBuildFailure(work, data, effect)
                : work.LastCanonicalBuildFailure
        };
    }

    private const int MaximumCanonicalBuildFailureBytes = MaximumProjectionBytes + 2048;

    private static CanonicalBuildFailure? CaptureCanonicalBuildFailure(PluginWorkBinding work,
        ToolData data, WorkflowEffect effect)
    {
        if (effect.State != WorkflowEffectState.Failed || data.Evidence?.Outcome != ToolExecutionOutcome.Failed)
            return null;
        // Capture the same bounded host projection used by ordinary tool feedback before its
        // contribution can become reducible. Never retain process stdout, source bodies or argv.
        var status = ToolModelProjection.Create(effect.Id, "process.run", effect.State.ToString(),
            data.Content, data.Truncated, data.Evidence, effect.Error);
        return new(effect.Id, work.SourceGeneration, CanonicalBuildProjectKey(work.Build),
            data.Evidence?.ExitCode, status.Errors, status.Warnings,
            CanonicalBuildDiagnostic(ToolModelProjection.Render(status)));
    }

    private static string CanonicalBuildProjectKey(PluginBuildCandidate build)
    {
        var root = OperatingSystem.IsWindows() ? build.Root.ToUpperInvariant() : build.Root;
        var project = OperatingSystem.IsWindows() ? build.ProjectFile.ToUpperInvariant() : build.ProjectFile;
        var identity = JsonSerializer.SerializeToUtf8Bytes(new { root, build.Component, project }, Json);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(identity)).ToLowerInvariant();
    }

    private static string? CanonicalBuildDiagnostic(string text)
    {
        const string marker = "\nDiagnostic: ";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = text.IndexOf("\nOperation evidence:\n", start, StringComparison.Ordinal);
        return Excerpt(text[(start + marker.Length)..(end < 0 ? text.Length : end)]);
    }
}
