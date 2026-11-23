using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Core;

namespace Cerebral.Desktop;

public sealed partial class WorkflowWorkspace
{
    /// <summary>Projects only the authorized public activity already read by this workspace.</summary>
    public ActivityTimelineSnapshot CaptureActivityTimeline(ActivityTimelineContext context)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.OwnerIdentity) || context.OwnerIdentity.Length > 1024)
            throw new ArgumentException("An exact bounded owner identity is required.", nameof(context));
        if (Snapshot is not { } snapshot) return new([], false, CoverageNote: "No activity has been read for this chat.");
        return ProjectActivityTimelineEvents(snapshot.Events, context, snapshot.HasOlder);
    }

    public static ActivityTimelineSnapshot ProjectActivityTimelineEvents(IReadOnlyList<CerebralEvent> events,
        ActivityTimelineContext context, bool hasEarlier = false, IReadOnlyDictionary<string, string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(events); ArgumentNullException.ThrowIfNull(context);
        // The optional dictionary is retained for call compatibility. Mutable
        // profile labels cannot establish the actor of a historical dispatch.
        var rows = ProjectActivityRows(events);
        var items = rows.TakeLast(ActivityTimelineSnapshot.MaximumItems).Select(row =>
        {
            var source = row.Call ?? row.Event;
            var known = ActivityCommand(source, out var tool, out var arguments, out _);
            var operation = known ? DescribeRecordedActivity(tool, arguments) : new ActivityOperationSummary(
                row.Event.Type == "workflow.audit" ? "Review checkpoint" : "Recorded update", "Route not recorded");
            var requested = known ? ActivityCommandText(source) : "Requested action was not recorded.";
            var outcome = row.Event.Type == CerebralEventTypes.ToolResult ? ActivityResultText(row.Event, row.Call) :
                ActivityAuditText(row.Event) ?? "No completion receipt is recorded for this event.";
            var agentId = source.Provenance.AgentId ?? (source.Actor.Kind == EventActorKind.Agent ? source.Actor.Id : null);
            var actor = RecordedActivityAgent(source);
            var owningMain = context.OwningMainConversationId ??
                (context.Role == ActivityTimelineRole.Main ? source.ConversationId : null);
            var ids = row.Call is { } call ? new[] { call.Id, row.Event.Id } : new[] { row.Event.Id };
            var correlation = row.Call is not null ? "Exact journal operation" : row.Event.Type == CerebralEventTypes.ToolResult
                ? "Unpaired or additional receipt; request correlation is not established" : "Recorded event";
            var evidence = $"Owner: {context.OwnerIdentity}\nOwning Main chat: {owningMain ?? "Not recorded"}\n" +
                $"Source conversation: {source.ConversationId}\nRun/turn: {source.TurnId}\n" +
                $"Member coordinate: {context.MemberCoordinate ?? "Not recorded"}\n" +
                $"Actor ID: {source.Actor.Id ?? "Not recorded"}\nOriginating agent ID: {agentId ?? "Not recorded"}\n" +
                $"Job ID: {source.Provenance.JobId ?? "Not recorded"}\nCall ID: {ToolCallId(source) ?? "Not recorded"}\n" +
                $"Task: {context.TaskId ?? "Not recorded"}\nTask title: {context.TaskTitle ?? "Not recorded"}\n" +
                $"Correlation: {correlation}\nPrimary target: {operation.TargetRole}\nRuntime: {operation.Runtime}\nRoute: {operation.Route}\n" +
                $"Events: {string.Join(", ", ids)}\nSequence: {row.FirstSequence}–{row.LastSequence}\n" +
                $"Recorded: {row.Event.CreatedAt:O}";
            return new ActivityTimelineItem(TimelineKey(context.OwnerIdentity, source.ConversationId, source.TurnId,
                context.TaskId ?? "", context.Role.ToString(), owningMain ?? "", context.MemberCoordinate ?? "", row.Key),
                context.OwnerIdentity, source.ConversationId, source.TurnId, context.TaskId, context.Role,
                BoundActivitySummary(actor.Length > 0 ? actor : "Agent not recorded"), operation.Action, operation.Tool,
                row.Event.Type == CerebralEventTypes.ToolResult && row.Call is null ? "Unpaired receipt" : TimelineOutcomeState(row.Event),
                requested, outcome.Length > 0 ? outcome : "No outcome detail was recorded.",
                BoundActivityText(evidence, ActivitySecrets(source.Data ?? default)), ids, row.LastSequence, row.Event.CreatedAt,
                operation.OperationType, operation.FileName, operation.FilePath, operation.Runtime, operation.Route, operation.TargetRole, correlation,
                context.TaskTitle is { } title ? BoundActivitySummary(title) : null)
            {
                OwningMainConversationId = owningMain, MemberCoordinate = context.MemberCoordinate, FirstSequence = row.FirstSequence,
                ContextAttribution = context.ContextAttribution is { } attribution &&
                    attribution.Matches(context.OwnerIdentity, owningMain, context.MemberCoordinate,
                        source.ConversationId, source.TurnId, context.Role) ? attribution : null,
                OperationKind = context.ClassifyOperations
                    ? known ? ClassifyRecordedActivity(source) : ActivityOperationKind.Unclassified : null
            };
        }).ToArray();
        return new(items, hasEarlier, Math.Max(0, rows.Count - items.Length),
            hasEarlier ? "Earlier journal events are available in the source chat." : null);
    }

    private static string TimelineKey(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));

    private static string TimelineOutcomeState(CerebralEvent item)
    {
        if (item.Type == CerebralEventTypes.ToolCall) return "Awaiting receipt";
        if (item.Type != CerebralEventTypes.ToolResult) return "Recorded";
        var success = ToolResult(item).Success;
        return ActivityReceiptState(item)?.ToLowerInvariant() switch
        {
            "unknown" => "Unconfirmed", "prepared" => "Prepared", "dispatched" => "Dispatched",
            "cancelled" => "Cancelled", "succeeded" when success != false => "Complete",
            "failed" when success != true => "Failed",
            null when success == true => "Complete", null when success == false => "Failed",
            _ => "Outcome unknown"
        };
    }

    // Exact host contracts describe internal work and handoff. The execution allowlist is
    // deliberately limited to the two registered Cerebral file contracts verified in the
    // current runtime evidence. Unknown providers/aliases remain unclassified, including
    // names that merely resemble an MCP/PAO route. A wrapper is not an execution contract.
    private static ActivityOperationKind ClassifyRecordedActivity(CerebralEvent source)
    {
        // Legacy descriptions unwrap host:tool. Classification must retain the original
        // journal envelope so nested names cannot borrow host identity or bypass wrapper shape.
        if (source.Type != CerebralEventTypes.ToolCall ||
            source.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("toolName", out var recordedName) || recordedName.ValueKind != JsonValueKind.String)
            return ActivityOperationKind.Unclassified;
        var tool = recordedName.GetString() ?? "";
        var arguments = data.TryGetProperty("arguments", out var recordedArguments) ? recordedArguments : default;
        if (tool is "cerebral.host:generate" or "cerebral.host:receipt" or "cerebral.host:history" or
            "cerebral.host:catalog" or "cerebral.host:compact") return ActivityOperationKind.Internal;
        if (tool is "cerebral.host:coordinate-owned" or "cerebral.host:delegate") return ActivityOperationKind.Handoff;
        if (tool == "cerebral.host:tool")
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !arguments.TryGetProperty("arguments", out var nested) || nested.ValueKind != JsonValueKind.Object)
                return ActivityOperationKind.Unclassified;
            tool = name.GetString() ?? "";
        }
        return tool switch
        {
            "file.read" or "file.list" => ActivityOperationKind.ExecutionTool,
            _ => ActivityOperationKind.Unclassified
        };
    }

    /// <summary>Uses fixed operation contracts and safe fields, never intent inferred from log prose.</summary>
    public static ActivityOperationSummary DescribeRecordedActivity(string tool, JsonElement arguments)
    {
        tool ??= "";
        if (tool == "cerebral.host:tool" && ActivityString(arguments, "name") is { Length: > 0 } nested)
        {
            tool = nested;
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("arguments", out var inner)) arguments = inner;
        }
        var isMcp = tool.StartsWith("mcp.", StringComparison.Ordinal) || tool.StartsWith("mcp__", StringComparison.Ordinal);
        var isPao = isMcp &&
            (tool.EndsWith(".pao", StringComparison.Ordinal) || tool.EndsWith(":pao", StringComparison.Ordinal) ||
             tool.EndsWith("__pao", StringComparison.Ordinal));
        var op = (isPao ? ActivityString(arguments, "op") : tool) ?? "";
        var verb = op switch
        {
            "file.read" or "rd" or "read_lines" or "head" or "tail" => "Read",
            "file.list" or "ls" or "find" => "List", "file.search" or "search" => "Search",
            "file.write" or "wr" => "Write", "file.edit" or "edit" or "patch" or "replace" => "Edit",
            "file.append" => "Append", "file.copy" or "cp" => "Copy", "file.move" or "mv" => "Move",
            "sha" => "Hash", "stat" => "Inspect", "mkdir" => "Create folder",
            "dotnet_build" => "Build", "dotnet_test" => "Test", "dotnet_restore" => "Restore dependencies",
            "process.run" or "run" or "pwsh" => "Run", "git_status" => "Read Git status",
            "git_diff" => "Read diff", "plugin.scaffold" => "Scaffold plug-in", "plugin.verify" => "Verify plug-in",
            "plugin.present" => "Present plug-in", "agents.delegate" => "Delegate task",
            "collaboration.submit" or "collaboration.coordinate" => "Request coordination", "cerebral.host:generate" => "Generate response",
            "cerebral.host:receipt" => "Read receipt", "cerebral.host:history" => "Read history",
            "cerebral.host:catalog" => "Discover tools", "cerebral.host:compact" or "context.compact" => "Compact context",
            _ => "Operation not recorded"
        };
        var fileOperation = op is "file.read" or "file.list" or "file.search" or "file.write" or "file.edit" or "file.append" or
            "file.copy" or "file.move" or "rd" or "read_lines" or "head" or "tail" or "ls" or "find" or "search" or
            "wr" or "edit" or "patch" or "replace" or "cp" or "mv" or "sha" or "stat" or "mkdir" or
            "dotnet_build" or "dotnet_test" or "dotnet_restore" or "plugin.scaffold" or "plugin.verify" or "pwsh";
        var destination = verb is "Copy" or "Move" or "Write" or "Edit";
        var target = fileOperation ? verb is "Copy" or "Move" ? ActivityString(arguments, "dst", "destination") :
            destination ? ActivityString(arguments, "dst", "destination", "path", "src", "source") :
            ActivityString(arguments, "path", "src", "source", "projectPath", "manifestPath") : "";
        var targetRole = target.Length == 0 ? verb is "Copy" or "Move"
            ? "Destination not recorded; inspect requested parameters for recorded sources" :
            fileOperation ? "File target not recorded" : "No file target" :
            destination && ActivityString(arguments, "dst", "destination").Length > 0
            ? "Primary destination; inspect requested parameters for all source/destination paths" : "Recorded primary target";
        var executable = ActivityString(arguments, isPao ? "cfg" : "executableId", "toolId");
        if (tool == "process.run" && executable == "dotnet" && arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("arguments", out var argv) && argv.ValueKind == JsonValueKind.Array &&
            argv.GetArrayLength() > 0 && argv[0].ValueKind == JsonValueKind.String)
        {
            verb = argv[0].GetString() switch { "build" => "Build", "test" => "Test", "restore" => "Restore dependencies", _ => verb };
            if (argv.GetArrayLength() > 1 && argv[1].ValueKind == JsonValueKind.String &&
                argv[1].GetString() is { } operand && !operand.StartsWith('-')) target = operand;
        }
        var secrets = ActivitySecrets(arguments);
        target = BoundActivityText(target, secrets);
        if (target.Any(char.IsControl)) { target = ""; targetRole = "Target was not recorded as a valid single-line path"; }
        var leaf = target.Length > 0 ? Path.GetFileName(target.TrimEnd('\\', '/')) : "";
        var action = verb + (leaf.Length > 0 ? " · " + leaf : "");
        var runtime = isPao && op == "pwsh" ? "PowerShell" : op.StartsWith("dotnet_", StringComparison.Ordinal) || executable == "dotnet"
            ? ".NET" : tool == "process.run" && executable.Length > 0 ? executable : "Not recorded";
        var transport = isPao ? "PAO over MCP" : isMcp ? "MCP" :
            tool.Length > 0 ? "Cerebral tool" : "Not recorded";
        var route = isPao ? (op == "pwsh" ? "PowerShell via PAO" : op.StartsWith("dotnet_", StringComparison.Ordinal) ? ".NET via PAO" : "PAO") + " · MCP" :
            isMcp ? tool + " · MCP" :
            tool == "process.run" ? executable.Length > 0 ? executable : "Process route not recorded" :
            tool.Length > 0 ? tool : "Route not recorded";
        return new(BoundActivitySummary(BoundActivityText(action, secrets)), BoundActivitySummary(BoundActivityText(route, secrets)),
            verb, leaf.Length > 0 ? leaf : "—", target.Length > 0 ? target : "—", runtime, transport, targetRole);
    }

    /// <summary>Legacy typed activity shares the compact projection without parsing an outcome from result prose.</summary>
    public static ActivityTimelineItem ProjectRecordedActivity(ActivityEntry entry, ActivityTimelineContext context)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(context);
        JsonElement args = default;
        if (entry.Arguments.Length <= 262_144)
            try { using var doc = JsonDocument.Parse(entry.Arguments); args = doc.RootElement.Clone(); } catch (JsonException) { }
        var operation = DescribeRecordedActivity(entry.ToolName, args);
        var state = entry.State.ToLowerInvariant() switch
        {
            "completed" or "succeeded" => "Complete", "failed" => "Failed", "cancelled" => "Cancelled",
            "uncertain" or "remote-unconfirmed" => "Unconfirmed", "running" => "Running", "queued" => "Queued",
            "waiting" or "waiting-human" => "Waiting", _ => "Outcome unknown"
        };
        var secrets = ActivitySecrets(args);
        var requestLines = new List<string> { "Recorded tool: " + entry.ToolName };
        AddActivityFields(args, requestLines, secrets, 0, command: true);
        var outcomeLines = new List<string>();
        if (entry.ToolName is "cerebral.host:generate" or "cerebral.host:compact" or "cerebral.host:history")
            outcomeLines.Add("Private generation/context output is not displayed in Activity.");
        else if (entry.Result.Length <= 262_144)
            try
            {
                using var result = JsonDocument.Parse(entry.Result);
                IncludeActivitySecrets(result.RootElement, secrets);
                AddActivityFields(result.RootElement, outcomeLines, secrets, 0, command: false);
            }
            catch (JsonException) { outcomeLines.Add("Unstructured legacy result is not displayed in Activity. Inspect its retained receipt."); }
        var correlation = "Legacy recorded activity; canonical request/receipt correlation is not established";
        return new(TimelineKey(context.OwnerIdentity, entry.SessionId, entry.ActorSessionId, entry.JobId,
            entry.RootJobId, context.TaskId ?? "", context.Role.ToString(), entry.ProfileId,
            entry.TurnSequence.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.Id), context.OwnerIdentity,
            entry.SessionId, entry.JobId, context.TaskId ?? (entry.RootJobId.Length > 0 ? entry.RootJobId : null), context.Role,
            "Agent not recorded", operation.Action, operation.Tool,
            "Recorded " + state.ToLowerInvariant() + " (legacy)",
            BoundActivityText(string.Join("\n", requestLines), secrets), BoundActivityText(string.Join("\n", outcomeLines), secrets),
            BoundActivityText($"Owner: {context.OwnerIdentity}\nChat: {entry.SessionId}\nActor chat: {entry.ActorSessionId}\n" +
                $"Job: {entry.JobId}\nRoot job: {entry.RootJobId}\nTurn: {entry.TurnSequence}\nEvent: {entry.Id}\nSequence: {entry.Sequence}" +
                $"\nCorrelation: {correlation}\nPrimary target: {operation.TargetRole}" +
                (entry.Truncated ? "\nStored arguments/result were truncated." : ""), secrets), [entry.Id], entry.Sequence, entry.UpdatedAt,
            operation.OperationType, operation.FileName, operation.FilePath, operation.Runtime, operation.Route, operation.TargetRole, correlation,
            context.TaskTitle is { } title ? BoundActivitySummary(title) : null)
        { OwningMainConversationId = context.OwningMainConversationId, MemberCoordinate = context.MemberCoordinate,
            FirstSequence = entry.Sequence };
    }
}
