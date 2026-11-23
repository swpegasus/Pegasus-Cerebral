using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Tools;

/// <summary>Deterministic PAO receipt classification fixtures; no provider dispatch or mutation.</summary>
internal static class PaoTerminalFailureTests
{
    private const string Provider = "mcp.cortex";
    private const string Request = "operation-request";
    private const string Job = "operation-job";
    private const string Remedy = "Refresh the target identity and operation preconditions; do not blindly retry.";
    private static readonly MethodInfo Classify = typeof(ToolDispatcher).GetMethod("PaoResult", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo Confirm = typeof(ToolDispatcher).GetMethod("ConfirmEffectReceipt", BindingFlags.NonPublic | BindingFlags.Static)!;

    public static int Run(Action<bool, string>? check = null)
    {
        var checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException("PAO terminal failure: " + reason);
            checks++;
            check?.Invoke(value, reason);
        }
        JsonObject Receipt() => new()
        {
            ["ok"] = false, ["rid"] = Request, ["jid"] = Job, ["state"] = "failed", ["op"] = "replace",
            ["exit"] = 3, ["err"] = "precondition", ["partial"] = false, ["remedy"] = Remedy
        };
        JsonObject Envelope(JsonObject receipt, bool isError = true) => new()
        {
            ["isError"] = isError, ["structuredContent"] = receipt,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = receipt.ToJsonString() })
        };
        ToolResult Result(JsonObject envelope, string? effectId = Request) =>
            Fence((ToolResult)Classify.Invoke(null, [CerebralJson.Element(envelope), Provider])!, effectId);
        ToolResult Fence(ToolResult result, string? effectId) => (ToolResult)Confirm.Invoke(null, [result, effectId])!;
        string? Error(ToolResult result)
        {
            using var document = JsonDocument.Parse(result.Content);
            return document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        }
        void RetainedFailure(ToolResult result, ToolExecutionOutcome outcome, bool partial, string label)
        {
            Check(!result.Success && Error(result) != "pao_transport_error" && result.Evidence ==
                new ToolExecutionEvidence(outcome, Provider, Request, Job, "failed", partial, 3), label);
            using var document = JsonDocument.Parse(result.Content);
            Check(document.RootElement.GetProperty("ok").ValueKind == JsonValueKind.False &&
                document.RootElement.GetProperty("rid").GetString() == Request &&
                document.RootElement.GetProperty("jid").GetString() == Job &&
                document.RootElement.GetProperty("partial").GetBoolean() == partial,
                label + " retains the original failure receipt fields");
        }

        var failed = Result(Envelope(Receipt()));
        RetainedFailure(failed, ToolExecutionOutcome.Failed, false,
            "delivered MCP isError with a correlated terminal precondition receipt is an operation failure");
        var status = ToolModelProjection.Create(Request, "file.edit", "Failed", failed.Content, failed.Truncated, failed.Evidence,
            "The tool returned failed. Inspect its retained result and evidence.");
        Check(status.Message == "precondition\nRemedy: " + Remedy && status.Status == "failed" && status.CommandsFailed == 1,
            "ordinary Core projection preserves only the actual returned code and remedy");
        Check(!status.Message!.Contains("preimage_changed", StringComparison.Ordinal) &&
            !status.Message.Contains("not_dispatched", StringComparison.Ordinal),
            "a broad precondition failure never acquires a guessed guard or effect outcome");

        var rejectedReceipt = Receipt(); rejectedReceipt["err"] = "permission_denied";
        RetainedFailure(Result(Envelope(rejectedReceipt)), ToolExecutionOutcome.Rejected, false,
            "correlated terminal admission rejection remains rejected despite MCP isError");
        var partialReceipt = Receipt(); partialReceipt["partial"] = true;
        RetainedFailure(Result(Envelope(partialReceipt)), ToolExecutionOutcome.Failed, true,
            "terminal partial failure retains partial evidence without implying safe replay");
        RetainedFailure(Result(Envelope(Receipt(), false)), ToolExecutionOutcome.Failed, false,
            "ordinary delivered failed receipt classification is unchanged");
        var textReceipt = Receipt();
        var textEnvelope = new JsonObject { ["isError"] = true,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = textReceipt.ToJsonString() }) };
        RetainedFailure(Result(textEnvelope), ToolExecutionOutcome.Failed, false,
            "an exact legacy text terminal receipt has the same operation-failure classification");

        var mismatch = Result(Envelope(Receipt()), "different-effect");
        Check(!mismatch.Success && Error(mismatch) == "pao_effect_identity_unconfirmed" &&
            mismatch.Evidence is { Outcome: ToolExecutionOutcome.Unknown, RequestId: Request, JobId: Job },
            "a mismatched workflow request remains unknown and retains the provider identity");
        var succeededReceipt = Receipt(); succeededReceipt["ok"] = true; succeededReceipt["state"] = "done";
        succeededReceipt["err"] = null; succeededReceipt["exit"] = 0;
        var contradictorySuccess = Result(Envelope(succeededReceipt));
        Check(!contradictorySuccess.Success && Error(contradictorySuccess) == "pao_transport_error" &&
            contradictorySuccess.Evidence?.Outcome == ToolExecutionOutcome.Unknown,
            "an affirmative receipt cannot override a contradictory MCP isError flag");
        var childFailureReceipt = succeededReceipt.DeepClone().AsObject();
        childFailureReceipt["data"] = new JsonObject { ["exitCode"] = 1 };
        var childFailure = Result(Envelope(childFailureReceipt));
        Check(!childFailure.Success && Error(childFailure) == "pao_transport_error",
            "an affirmative PAO receipt with a failed child process does not erase MCP envelope ambiguity");

        foreach (var field in new[] { "rid", "jid", "state" })
        {
            var incomplete = Receipt(); incomplete.Remove(field);
            var result = Result(Envelope(incomplete), null);
            Check(!result.Success && Error(result) == "pao_transport_error",
                "an MCP error without a complete retained terminal identity remains a transport error: " + field);
        }
        foreach (var field in new[] { "rid", "jid", "partial", "exit" })
        {
            var malformed = Receipt(); malformed[field] = field is "rid" or "jid" ? "invalid identity!" : "invalid";
            var result = Result(Envelope(malformed), null);
            Check(!result.Success && Error(result) == "pao_transport_error" && result.Evidence?.Outcome == ToolExecutionOutcome.Unknown,
                "malformed receipt evidence cannot establish a terminal operation failure: " + field);
        }
        var runningReceipt = Receipt(); runningReceipt["state"] = "running";
        var running = Result(Envelope(runningReceipt));
        Check(!running.Success && Error(running) == "pao_transport_error" && running.Evidence?.Outcome == ToolExecutionOutcome.Unknown,
            "a nonterminal failed envelope remains unconfirmed");
        var timeoutReceipt = Receipt(); timeoutReceipt["data"] = new JsonObject { ["timed_out"] = true };
        var timeout = Result(Envelope(timeoutReceipt));
        Check(!timeout.Success && Error(timeout) == "pao_transport_error" && timeout.Evidence?.Outcome == ToolExecutionOutcome.Unknown,
            "a timeout cannot be promoted to a confirmed terminal operation failure");
        foreach (var flag in new JsonNode?[] { JsonValue.Create("true"), null })
        {
            var malformedEnvelope = Envelope(Receipt()); malformedEnvelope["isError"] = flag;
            var result = Result(malformedEnvelope);
            Check(!result.Success && Error(result) == "pao_receipt_missing" && result.Evidence?.Outcome == ToolExecutionOutcome.Unknown,
                "a malformed MCP error flag remains unconfirmed");
        }
        var conflicting = Envelope(Receipt());
        var different = Receipt(); different["rid"] = "different-request";
        conflicting["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = different.ToJsonString() });
        var conflict = Result(conflicting);
        Check(!conflict.Success && Error(conflict) is not null && conflict.Evidence?.Outcome == ToolExecutionOutcome.Unknown,
            "conflicting structured and text receipt copies cannot establish an operation failure");
        var duplicate = Receipt(); duplicate["RID"] = Request;
        var duplicated = Result(Envelope(duplicate));
        Check(!duplicated.Success && Error(duplicated) is not null && duplicated.Evidence?.Outcome == ToolExecutionOutcome.Unknown,
            "case-conflicting receipt identity fields remain unconfirmed");
        var oversized = Receipt(); oversized["data"] = new JsonObject(Enumerable.Range(0, 4000)
            .Select(index => new KeyValuePair<string, JsonNode?>("item" + index, JsonValue.Create(index))));
        var wide = Result(Envelope(oversized));
        Check(!wide.Success && wide.Evidence?.Outcome == ToolExecutionOutcome.Unknown && wide.Truncated,
            "failure to retain a bounded result envelope remains unconfirmed");
        return checks;
    }
}
