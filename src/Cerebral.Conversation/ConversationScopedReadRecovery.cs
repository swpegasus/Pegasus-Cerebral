using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const string OutOfScopeRead = "The path is outside this conversation's current access scope.";

    /// <summary>
    /// A rejected read may be an attempt to inspect a host-owned receipt or a mistaken copy of a
    /// project source path. Point back to an already inspected project locator and the exact same-run
    /// receipt action; neither reference changes current path authority or proves current file bytes.
    /// </summary>
    private static string ScopedReadRecovery(Input input, HarnessState state, WorkflowEffect effect, ToolData? data,
        string boundedContent, string runId)
    {
        if (state.PendingName != "file.read" || effect.State != WorkflowEffectState.Failed ||
            data is not { Content: OutOfScopeRead, Truncated: false,
                Evidence: { Outcome: ToolExecutionOutcome.Rejected, Partial: not true } }) return "";

        var recovery = "\nRecovery: This file read was denied before dispatch. Do not retry this path with another range. " +
            "Use a source path from the inspected project within the current chat scope; inspect current bytes before editing.";
        var failed = state.ToolReceipts.LastOrDefault(receipt => receipt.RunId == runId && receipt.Name == "process.run" &&
            receipt.State == nameof(WorkflowEffectState.Failed) && receipt.Outcome == nameof(ToolExecutionOutcome.Failed) &&
            receipt.ExitCode is not null and not 0);
        var savedResult = failed is null ? "" :
            " For missing detail from the latest failed command, use the same-run saved receipt action: " +
            JsonSerializer.Serialize(new { action = "receipt", effectId = failed.EffectId, offset = 0 }, Json) +
            ". It reads the retained result without reading a file or rerunning the command.";
        // This text is appended after ToolModelProjection has bounded its own result. Keep the
        // complete receipt guidance first; omit an oversized locator rather than truncating a path.
        var remaining = MaximumProjectionBytes - Encoding.UTF8.GetByteCount(boundedContent);
        if (Encoding.UTF8.GetByteCount(recovery + savedResult) > remaining) return "";
        recovery += savedResult;
        var source = state.PluginWork?.Build.SourceFile;
        var requested = PendingToolArguments(state);
        if (source is null || input.WorkingDirectory is not { } working || !PathWithin(source, working) ||
            requested is not { } arguments || !TryActionPath(input, arguments, "path", out var denied) ||
            !string.Equals(Path.GetFileName(source), Path.GetFileName(denied),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return recovery;
        if (Encoding.UTF8.GetByteCount(source) > remaining) return recovery;
        var locator = " Previously inspected project source locator: " + JsonSerializer.Serialize(source, Json) +
            ". This is a locator only, not a current hash or a new path grant.";
        return Encoding.UTF8.GetByteCount(recovery + locator) <= remaining ? recovery + locator : recovery;
    }
}
