using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const int MaximumOperationEvidenceBytes = 2048;

    private static string AppendOperationEvidence(Input input, HarnessState state, WorkflowEffect effect,
        ToolData? data, string content)
    {
        if (state.PendingName is not ("file.mkdir" or "file.copy" or "process.run") ||
            PendingToolArguments(state) is not { } arguments) return content;

        // Coordinates describe the submitted request, not permission or proof of completion.
        // Keep argv and arbitrary result bodies in the exact receipt, never in this summary.
        var requested = new Dictionary<string, object?>();
        var summary = new Dictionary<string, object?>
        {
            ["receipt"] = effect.Id,
            ["tool"] = state.PendingName
        };
        if (state.PendingName == "file.mkdir")
        {
            if (TryActionPath(input, arguments, "path", out var path)) requested["path"] = path;
            if (TryDirectoryEnsureObservation(input, state, effect, data, out var existedBefore))
                summary["confirmedResult"] = new { ensured = true, existedBefore };
        }
        else if (state.PendingName == "file.copy")
        {
            if (TryActionPath(input, arguments, "sourcePath", out var source)) requested["sourcePath"] = source;
            if (TryActionPath(input, arguments, "destinationPath", out var destination)) requested["destinationPath"] = destination;
            if (TryCopyObservation(input, state, effect, data, out var bytes, out var sha256))
                summary["confirmedResult"] = new { bytes, sha256 };
        }
        else
        {
            if (TryText(arguments, "executableId", 256, out var executable)) requested["executableId"] = executable;
            if (TryActionPath(input, arguments, "workingDirectory", out var working)) requested["workingDirectory"] = working;
            summary["argumentsOmitted"] = true;
            summary["outputOmitted"] = true;
            if (ConfirmedCurrentInputCheck(input, state.PluginWork, effect, data))
            {
                summary["confirmedResult"] = "Current pinned inputs passed all 10 admitted checker assertions.";
                summary["nextAction"] = "Continue to plugin.verify, then plugin.present with the saved current build.";
            }
        }
        if (requested.Count > 0) summary["requestedCoordinates"] = requested;
        else summary["requestedCoordinatesOmitted"] = true;

        // Charge JSON escaping and the marker against the whole cap. Omit oversized coordinates
        // together instead of turning a clipped path into an apparently exact target.
        if (!OperationEvidenceFits(summary))
        {
            summary.Remove("requestedCoordinates");
            summary["requestedCoordinatesOmitted"] = true;
            summary["note"] = "Fetch this receipt for the exact requested coordinates.";
        }
        if (!OperationEvidenceFits(summary))
        {
            summary.Remove("receipt");
            summary["receiptInHeader"] = true;
        }
        return OperationEvidenceFits(summary) ? content + OperationEvidenceText(summary) : content;
    }

    private static bool TryDirectoryEnsureObservation(Input input, HarnessState state, WorkflowEffect effect,
        ToolData? data, out bool existedBefore)
    {
        existedBefore = false;
        if (state.PendingName != "file.mkdir" || !SuccessfulEffect(effect, data) || data!.Truncated ||
            PendingToolArguments(state) is not { } arguments ||
            !TryActionPath(input, arguments, "path", out var requested)) return false;
        try
        {
            using var document = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 4 });
            var result = document.RootElement;
            if (!ExactOperationProperties(result, "path", "ensured", "existedBefore") ||
                !TryText(result, "path", MaximumBindingPathCharacters, out var path) ||
                !TryCanonicalAbsolutePath(path, out var actual) || !PathEquals(actual, requested) ||
                result.GetProperty("ensured").ValueKind != JsonValueKind.True ||
                result.GetProperty("existedBefore").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            existedBefore = result.GetProperty("existedBefore").GetBoolean();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryCopyObservation(Input input, HarnessState state, WorkflowEffect effect,
        ToolData? data, out long bytes, out string sha256)
    {
        bytes = 0;
        sha256 = "";
        if (state.PendingName != "file.copy" || !SuccessfulEffect(effect, data) || data!.Truncated ||
            PendingToolArguments(state) is not { } arguments ||
            !TryActionPath(input, arguments, "sourcePath", out var requestedSource) ||
            !TryActionPath(input, arguments, "destinationPath", out var requestedDestination)) return false;
        try
        {
            using var document = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 4 });
            var result = document.RootElement;
            if (!ExactOperationProperties(result, "sourcePath", "destinationPath", "bytes", "sha256") ||
                !TryText(result, "sourcePath", MaximumBindingPathCharacters, out var source) ||
                !TryCanonicalAbsolutePath(source, out var actualSource) || !PathEquals(actualSource, requestedSource) ||
                !TryText(result, "destinationPath", MaximumBindingPathCharacters, out var destination) ||
                !TryCanonicalAbsolutePath(destination, out var actualDestination) || !PathEquals(actualDestination, requestedDestination) ||
                result.GetProperty("bytes").ValueKind != JsonValueKind.Number ||
                !result.GetProperty("bytes").TryGetInt64(out bytes) || bytes < 0 ||
                !TryText(result, "sha256", 64, out sha256) || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit)) return false;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool ExactOperationProperties(JsonElement result, params string[] expected)
    {
        if (result.ValueKind != JsonValueKind.Object) return false;
        var names = result.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == expected.Length &&
            names.All(name => expected.Contains(name, StringComparer.Ordinal));
    }

    private static string OperationEvidenceText(Dictionary<string, object?> summary) =>
        "\nOperation evidence:\n" + JsonSerializer.Serialize(summary, Json);

    private static bool OperationEvidenceFits(Dictionary<string, object?> summary) =>
        Encoding.UTF8.GetByteCount(OperationEvidenceText(summary)) <= MaximumOperationEvidenceBytes;
}
