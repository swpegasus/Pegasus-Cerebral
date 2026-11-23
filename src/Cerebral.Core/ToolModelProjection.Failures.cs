using System.Text;
using System.Text.Json;

namespace Cerebral.Core;

public static partial class ToolModelProjection
{
    private const int MaximumStructuredFailureCharacters = 256 * 1024;
    private const int MaximumFailureFieldUtf8Bytes = 512;
    private const int MaximumFailureDiagnosticUtf8Bytes = 2048;

    // Codes describe only what the provider actually returned. A broad precondition
    // must never be expanded into a guessed preimage, publication or replay outcome.
    private static readonly HashSet<string> FailureCodes = new(StringComparer.Ordinal)
    {
        "invalid", "queue_full", "unsupported_command", "unsupported_option", "path_style",
        "encoding_required", "not_found", "pwsh_unconfigured", "git_unconfigured",
        "nsis_unconfigured", "nsis_unsafe_directive", "nsis_output_contract", "collision",
        "precondition", "root_target", "pid_reused", "service_not_admitted", "reentrant_mutation",
        "reparse_boundary", "access", "auth", "application_auth", "root_app_denied", "root_path_denied",
        "timeout", "cancelled", "canceled", "external_failed", "process_host_runtime",
        "user_host_unavailable", "svc_down", "service_unavailable", "copy_incomplete", "clean_failed",
        "recovery_required", "ui_unavailable", "ui_not_found", "ui_ambiguous", "stale_target",
        "ui_unsupported", "postcondition_failed", "ui_protocol", "tool_output_limit",
        "user_dispatch_required", "app_capacity", "app_owner_lost", "app_launch_failed", "app_stop_incomplete",
        "app_observation_incomplete", "internal", "pao_arguments_invalid", "invalid_arguments",
        "permission_denied", "access_denied", "operation_not_allowed",
        "preimage_changed", "preimage_invalid", "preimage_missing", "literal_not_found",
        "literal_not_unique", "literal_count_mismatch"
    };

    private static string? ConfirmedStructuredFailureDiagnostic(string content, ToolExecutionEvidence? evidence)
    {
        if (evidence?.Outcome is not (ToolExecutionOutcome.Failed or ToolExecutionOutcome.Rejected) ||
            content.Length > MaximumStructuredFailureCharacters) return null;
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!UniqueFailureObject(root)) return null;
            var receipt = root;
            if (root.TryGetProperty("receipt", out var nested))
            {
                // This is the actual ToolDispatcher wrapper, not an arbitrary nested
                // object or MCP body. Its retained IDs must match host-owned evidence.
                if (FailureString(root, "error") != "pao_transport_error") return null;
                receipt = nested;
            }
            else if (!root.TryGetProperty("ok", out _))
            {
                var code = ClosedFailureCode(root, "error");
                var message = FailureString(root, "message");
                return code is null || message is null ? null : BoundedFailureText(code + ": " + message);
            }

            if (!UniqueFailureObject(receipt) ||
                !receipt.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False ||
                evidence.JobId is null || evidence.RequestId is null || evidence.State is null ||
                FailureString(receipt, "jid") != evidence.JobId || FailureString(receipt, "rid") != evidence.RequestId ||
                FailureString(receipt, "state") is not { } state ||
                !state.Equals(evidence.State, StringComparison.OrdinalIgnoreCase) ||
                state is not ("failed" or "error" or "rejected" or "denied")) return null;

            var lines = new List<string>();
            var messages = new HashSet<string>(StringComparer.Ordinal);
            void Add(string? message, string? prefix = null)
            {
                var bounded = BoundedFailureText(message);
                if (bounded is not null && messages.Add(bounded))
                    lines.Add(prefix is null ? bounded : prefix + ": " + bounded);
            }
            Add(ClosedFailureCode(receipt, "err"));
            if (receipt.TryGetProperty("failure", out var failure) && failure.ValueKind != JsonValueKind.Null)
            {
                if (!UniqueFailureObject(failure)) return null;
                if (failure.TryGetProperty("diagnostics", out var diagnostics))
                {
                    if (diagnostics.ValueKind != JsonValueKind.Array) return null;
                    foreach (var item in diagnostics.EnumerateArray().Take(4))
                    {
                        if (!UniqueFailureObject(item)) return null;
                        Add(FailureString(item, "message"), ClosedFailureCode(item, "code"));
                    }
                }
                Add(FailureString(failure, "message"));
                Add(FailureFact(failure, "execution", ["not_dispatched", "started", "partial", "unknown"]), "Execution");
                // These are source-stamped provider facts, never permission to retry.
                Add(FailureFact(failure, "retry", ["safe", "after_correction", "reconcile_first", "not_supported"]), "Retry");
                var next = FailureString(failure, "next");
                Add(next, "Next");
                if (next is null) Add(FailureString(receipt, "remedy"), "Remedy");
            }
            else Add(FailureString(receipt, "remedy"), "Remedy");

            return lines.Count == 0 ? null : BoundedFailureText(string.Join('\n', lines),
                MaximumFailureDiagnosticUtf8Bytes, preserveLines: true);
        }
        catch (JsonException)
        {
            // Malformed or truncated JSON cannot establish a structured failure cause.
            return null;
        }
    }

    private static bool UniqueFailureObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
        value.EnumerateObject().Count();

    private static string? FailureString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(field.GetString()) ? field.GetString() : null;

    private static string? ClosedFailureCode(JsonElement value, string name) =>
        FailureString(value, name) is { } code && FailureCodes.Contains(code) ? code : null;

    private static string? FailureFact(JsonElement failure, string name, string[] values)
    {
        if (!failure.TryGetProperty(name, out var fact) || !UniqueFailureObject(fact) ||
            FailureString(fact, "value") is not { } value || !values.Contains(value, StringComparer.Ordinal) ||
            FailureString(fact, "source") is not { Length: > 0 and <= 64 } source ||
            source.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')))
            return null;
        return value + " (" + source + ")";
    }

    private static string? BoundedFailureText(string? text, int maximumBytes = MaximumFailureFieldUtf8Bytes,
        bool preserveLines = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            return null;
        text = text.Trim().Replace('\r', ' ').Replace('\t', ' ');
        if (!preserveLines) text = text.Replace('\n', ' ');
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.Length == 0 ? null : result.ToString();
    }
}
