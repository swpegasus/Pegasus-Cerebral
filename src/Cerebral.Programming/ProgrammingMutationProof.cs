using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

internal sealed record ProgrammingChangedSource(string Path, string Sha256);
internal static class ProgrammingMutationProof
{
    internal const string RangeEdit = "file.edit-range";
    internal static void ValidateRangeArguments(JsonElement arguments)
    {
        string[] fields = ["path", "expectedSha256", "startLine", "lineCount", "newText"];
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != fields.Length ||
            arguments.EnumerateObject().Any(property => !fields.Contains(property.Name, StringComparer.Ordinal)))
            throw new ArgumentException("file.edit-range requires only path, expectedSha256, startLine, lineCount and newText.");
        ProgrammingEvidence.Unique(arguments);
        if (!ProgrammingEvidence.IsHash(ProgrammingEvidence.Text(arguments, "expectedSha256")))
            throw new ArgumentException("file.edit-range expectedSha256 must be the current whole-file SHA-256.");
        _ = ProgrammingEditProposal.Read(arguments, closedShape: false);
    }

    internal static ProgrammingChangedSource? Read(string name, JsonElement arguments, WorkflowEffectState state,
        string effectId, string content, bool truncated, bool followUpRequired, ToolExecutionEvidence? evidence)
    {
        if (name is not ("file.write" or "file.edit" or RangeEdit) || state != WorkflowEffectState.Succeeded ||
            truncated || followUpRequired || evidence is not null && (evidence.Outcome != ToolExecutionOutcome.Succeeded ||
                evidence.Partial == true || evidence.RequestId is not null && evidence.RequestId != effectId)) return null;
        if (name == RangeEdit && (evidence is not { Partial: false } ||
            string.IsNullOrWhiteSpace(evidence.ProviderId) || evidence.ProviderId.Length > 256 ||
            evidence.ProviderId.Any(char.IsControl))) return null;
        if (name != RangeEdit && content.Length > 8192) return null;
        try
        {
            if (name == RangeEdit) ValidateRangeArguments(arguments);
            var result = ProgrammingEvidence.Read(content, name == RangeEdit ? 8 : 32);
            if (result.ValueKind != JsonValueKind.Object ||
                result.TryGetProperty("skipped", out var skipped) && skipped.ValueKind != JsonValueKind.False) return null;
            var path = ProgrammingEvidence.Text(result, "path");
            var after = ProgrammingEvidence.Text(result, "sha256");
            if (!Path.IsPathFullyQualified(path) || !ProgrammingEvidence.SamePath(path, ProgrammingEvidence.Text(arguments, "path")) ||
                !ProgrammingEvidence.IsHash(after) || !result.TryGetProperty("bytes", out var size) ||
                size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var length) || length is < 0 or > 1024 * 1024) return null;
            var changed = new ProgrammingChangedSource(Path.GetFullPath(path), after.ToLowerInvariant());
            if (name == RangeEdit)
            {
                var before = ProgrammingEvidence.Text(result, "sha256Before");
                return ProgrammingEvidence.IsHash(before) && before.Equals(ProgrammingEvidence.Text(arguments, "expectedSha256"), StringComparison.OrdinalIgnoreCase) &&
                    !before.Equals(after, StringComparison.OrdinalIgnoreCase) &&
                    result.TryGetProperty("startLine", out var first) && first.ValueKind == JsonValueKind.Number &&
                    first.TryGetInt32(out var start) && start == arguments.GetProperty("startLine").GetInt32() &&
                    result.TryGetProperty("lineCount", out var count) && count.ValueKind == JsonValueKind.Number &&
                    count.TryGetInt32(out var lines) && lines == arguments.GetProperty("lineCount").GetInt32() &&
                    result.TryGetProperty("changed", out var flag) && flag.ValueKind == JsonValueKind.True ? changed : null;
            }
            var collision = arguments.TryGetProperty("collision", out var option) ? option.GetString() : "fail";
            if (name == "file.edit" || collision == "replace")
            {
                var before = ProgrammingEvidence.Text(arguments, "expectedSha256");
                return ProgrammingEvidence.IsHash(before) && !before.Equals(after, StringComparison.OrdinalIgnoreCase) ? changed : null;
            }
            if (collision is not ("fail" or "skip") || !arguments.TryGetProperty("content", out var text) ||
                text.ValueKind != JsonValueKind.String) return null;
            var created = new UTF8Encoding(false, true).GetBytes(text.GetString()!);
            return length == created.Length && after.Equals(Convert.ToHexString(SHA256.HashData(created)), StringComparison.OrdinalIgnoreCase) ? changed : null;
        }
        catch (Exception error) when (ProgrammingEvidence.Invalid(error)) { return null; }
    }
}

