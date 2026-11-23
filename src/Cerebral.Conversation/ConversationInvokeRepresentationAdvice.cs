using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const string InvokeLineEndingRepresentationAdvice =
        "\nPossible line-ending representation mismatch: one exact whole editable-field expectation differs from the complete actual field only by CRLF or CR versus LF. " +
        "This proves neither a plugin defect nor a pass. Compare the original requirement and do not edit source solely for this mismatch. The retained failed receipt remains unresolved.";

    private static bool InvokeLineEndingRepresentationMismatch(WorkflowEffect effect, ToolData data,
        JsonElement body, string mount, string? button, IReadOnlyList<string>? expected)
    {
        if (button is null || expected is null ||
            !CompleteAssertedInvoke(effect, data, body, mount, button, failed: true, out var observed) ||
            !body.TryGetProperty("assertion", out var assertion) ||
            !assertion.TryGetProperty("missingTexts", out var missingValue)) return false;

        var visible = ExactControlTexts(observed.After!).ToHashSet(StringComparer.Ordinal);
        var missingIndexes = Enumerable.Range(0, expected.Count)
            .Where(index => !visible.Contains(expected[index])).ToArray();
        if (missingIndexes.Length != 1) return false;
        var returnedMissing = PluginControlAssertions.CopyExpectedTexts("invoke",
            missingValue.Deserialize<string[]>(Json));
        var missing = expected[missingIndexes[0]];
        if (returnedMissing is null || returnedMissing.Length != 1 ||
            !string.Equals(returnedMissing[0], missing, StringComparison.Ordinal) ||
            !HasLineEnding(missing)) return false;

        var normalizedExpected = NormalizeLineEndings(missing);
        var candidates = observed.After!.Fields.Where(field =>
            field.Enabled && field.Visible && !field.ReadOnly && !field.Limited &&
            !string.Equals(field.Text, missing, StringComparison.Ordinal) &&
            HasLineEnding(field.Text) &&
            string.Equals(NormalizeLineEndings(field.Text), normalizedExpected, StringComparison.Ordinal)).ToArray();
        return candidates.Length == 1;
    }

    private static string BoundNativeControlDiagnostic(string diagnostic, string? advice)
    {
        const int maximumCharacters = 512;
        if (advice is null) return diagnostic[..Math.Min(diagnostic.Length, maximumCharacters)];
        if (advice.Length >= maximumCharacters) return advice[..maximumCharacters];
        var prefix = diagnostic[..Math.Min(diagnostic.Length, maximumCharacters - advice.Length)].TrimEnd();
        return prefix + advice;
    }

    private static bool HasLineEnding(string value) => value.IndexOfAny(['\r', '\n']) >= 0;

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
