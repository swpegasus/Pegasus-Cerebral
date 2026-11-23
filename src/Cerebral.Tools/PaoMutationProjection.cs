using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Tools;

/// <summary>Projects confirmed fixed PAO file mutations into the native semantic receipt contract.</summary>
internal static class PaoMutationProjection
{
    internal static ToolResult Project(ToolResult result, string tool, string requestedDestination,
        string? expectedPreimage, string? writeCollision = null,
        int? requestedStartLine = null, int? requestedLineCount = null)
    {
        if (tool is not ("file.write" or "file.edit" or ToolPolicy.RangeEditToolName) || !result.Success ||
            result.Evidence is not { Outcome: ToolExecutionOutcome.Succeeded } evidence)
            return result;

        ToolResult Unconfirmed() => ToolPolicy.Result(new
        {
            error = "pao_mutation_receipt_invalid",
            message = "The provider receipt does not prove this exact semantic file mutation. Inspect its retained receipt before retrying; no operation was replayed."
        }) with { Success = false, Evidence = evidence with { Outcome = ToolExecutionOutcome.Unknown } };

        if (result.Truncated || evidence.Partial == true || evidence.JobId is not { Length: > 0 and <= 256 } jobId ||
            !jobId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':'))
            return Unconfirmed();
        try
        {
            using var document = JsonDocument.Parse(result.Content);
            var receipt = document.RootElement;
            var operation = tool == ToolPolicy.RangeEditToolName ? "edit" : tool == "file.edit" ? "replace" : "wr";
            if (!UniqueObject(receipt) || !receipt.TryGetProperty("ok", out var ok) ||
                ok.ValueKind != JsonValueKind.True || !Text(receipt, "op", out var actualOperation) ||
                actualOperation != operation || !receipt.TryGetProperty("data", out var data) ||
                !UniqueObject(data) || !Text(data, "dst", out var destination) ||
                !SameDestination(destination, requestedDestination) ||
                !Hash(data, "sha", out var hash) ||
                !data.TryGetProperty("len", out var length) || length.ValueKind != JsonValueKind.Number ||
                !length.TryGetInt64(out var bytes) || bytes is < 0 or > int.MaxValue ||
                !Text(data, "enc", out var encoding) ||
                (!encoding.Equals("utf8", StringComparison.OrdinalIgnoreCase) &&
                 !encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase)) ||
                !data.TryGetProperty("bom", out var bom) || bom.ValueKind != JsonValueKind.False)
                return Unconfirmed();

            if (tool == "file.edit" &&
                (!data.TryGetProperty("count", out var count) || count.ValueKind != JsonValueKind.Number ||
                 !count.TryGetInt32(out var replacements) || replacements != 1 ||
                 !Hash(data, "sha_before", out var before) || expectedPreimage is null ||
                 !before.Equals(expectedPreimage, StringComparison.OrdinalIgnoreCase)))
                return Unconfirmed();
            string? rangePreimage = null;
            if (tool == ToolPolicy.RangeEditToolName)
            {
                // Independently classify the retained receipt so contradictory partial, state,
                // error, exit or timeout metadata cannot borrow a successful outer outcome.
                var rawEvidence = ToolDispatcher.PaoEvidence(receipt, evidence.ProviderId);
                if (evidence.Partial != false || rawEvidence is not { Outcome: ToolExecutionOutcome.Succeeded, Partial: false } ||
                    rawEvidence.State != evidence.State || rawEvidence.JobId != jobId ||
                    rawEvidence.RequestId is null || rawEvidence.RequestId != evidence.RequestId ||
                    requestedStartLine is not (>= 1 and <= 1_000_000) || requestedLineCount is not (>= 1 and <= 512) ||
                    !Hash(data, "sha_before", out var beforeRange) || expectedPreimage is null ||
                    !beforeRange.Equals(expectedPreimage, StringComparison.OrdinalIgnoreCase) ||
                    beforeRange.Equals(hash, StringComparison.OrdinalIgnoreCase) || bytes > ToolPolicy.MaxInput ||
                    !Integer(data, "line", out var line) || line != requestedStartLine ||
                    !Integer(data, "count", out var removed) || removed != requestedLineCount ||
                    !Integer(data, "lines_before", out var linesBefore) || linesBefore > ToolPolicy.MaxInput || linesBefore < (long)line + removed - 1 ||
                    !Integer(data, "lines_after", out var linesAfter) || linesAfter < 0 || linesAfter > bytes ||
                    (linesAfter == 0) != (bytes == 0))
                    return Unconfirmed();
                rangePreimage = beforeRange;
            }
            // Legacy wr receipts cannot prove create-only or skip semantics: the current fixed
            // contract supplies no absent-destination proof. Replacement recovery additionally
            // needs the actual provider preimage, never merely the requested SHA.
            if (tool == "file.write" &&
                (writeCollision != "replace" || expectedPreimage is null ||
                 !Hash(data, "sha_before", out var beforeWrite) ||
                 !beforeWrite.Equals(expectedPreimage, StringComparison.OrdinalIgnoreCase)))
                return Unconfirmed();

            // All completion values come from the matched provider receipt. Do not recount
            // requested text, reread mutable files or manufacture display-only line/word counts.
            var semantic = new Dictionary<string, object> { ["path"] = destination, ["sha256"] = hash, ["bytes"] = bytes };
            if (rangePreimage is not null)
            {
                semantic["sha256Before"] = rangePreimage;
                semantic["startLine"] = requestedStartLine!.Value;
                semantic["lineCount"] = requestedLineCount!.Value;
                semantic["changed"] = true; // Derived from distinct, matched provider pre/post hashes.
            }
            foreach (var name in new[] { "lines", "words" })
            {
                if (!data.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0)
                    return Unconfirmed();
                semantic[name] = number;
            }
            var projected = ToolPolicy.Result(semantic);
            return projected.Truncated ? Unconfirmed() : projected with { Evidence = evidence };
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        {
            return Unconfirmed();
        }
    }

    private static bool UniqueObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
        value.EnumerateObject().Count();

    private static bool Text(JsonElement value, string name, out string text)
    {
        text = "";
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String ||
            item.GetString() is not { Length: > 0 } found || found.Any(char.IsControl)) return false;
        text = found;
        return true;
    }

    private static bool Hash(JsonElement value, string name, out string hash) =>
        Text(value, name, out hash) && hash.Length == 64 && hash.All(char.IsAsciiHexDigit);

    private static bool Integer(JsonElement value, string name, out int number)
    {
        number = 0;
        return value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out number);
    }

    private static bool SameDestination(string actual, string requested)
    {
        if (!Path.IsPathFullyQualified(actual)) return false;
        var canonical = Path.GetFullPath(actual);
        return canonical.Equals(actual, ToolPolicy.PathComparison) &&
            canonical.Equals(requested, ToolPolicy.PathComparison);
    }
}
