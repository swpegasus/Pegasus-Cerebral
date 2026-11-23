using System.Text;

namespace Cerebral.Core;

/// <summary>Pure identity syntax shared by host-owned plugin QA surfaces; this grants no receipt access.</summary>
public static class PluginControlReferences
{
    public const string ReviewedReference = "reviewed";
    public const string TestRequestPrefix = "plugin-tests-";

    public static bool TryResolveReviewedPresentationId(string? requestId, out string presentationId)
    {
        presentationId = "";
        if (requestId is null || requestId.Length != TestRequestPrefix.Length + 32 ||
            !requestId.StartsWith(TestRequestPrefix, StringComparison.Ordinal))
            return false;
        var suffix = requestId[TestRequestPrefix.Length..];
        if (!Guid.TryParseExact(suffix, "N", out var value) || value == Guid.Empty) return false;
        presentationId = suffix;
        return true;
    }
}

/// <summary>Pure validation for optional host-evaluated native control assertions; this grants no control access.</summary>
public static class PluginControlAssertions
{
    // Host-owned admission provenance, not caller input. Missing versions retain the original
    // visible-text-only assertion semantics when old native receipts are read or reconciled.
    public const int CurrentPolicyVersion = 1;

    public static void ValidatePolicyVersion(int? version)
    {
        if (version is not null && version != CurrentPolicyVersion)
            throw new ArgumentException("The retained native assertion policy version is unsupported; no control was dispatched or reinterpreted.");
    }

    public static string[]? CopyExpectedTexts(string operation, IReadOnlyList<string>? expectedTexts)
    {
        if (expectedTexts is null) return null;
        if (operation is not ("invoke" or "reopen"))
            throw new ArgumentException("expectedTexts is available only for invoke postconditions or reopen persistence checks.");
        if (expectedTexts.Count is < 1 or > 8)
            throw new ArgumentException("expectedTexts needs one to eight exact visible text values.");

        var result = new string[expectedTexts.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalUtf8Bytes = 0;
        for (var index = 0; index < expectedTexts.Count; index++)
        {
            var text = expectedTexts[index];
            if (text is null || text.Length is < 1 or > 256 || text.Contains('\0'))
                throw new ArgumentException("Each expected text must contain 1 to 256 characters and no NUL.");
            if (!seen.Add(text))
                throw new ArgumentException("expectedTexts must contain distinct exact values.");
            totalUtf8Bytes += Encoding.UTF8.GetByteCount(text);
            if (totalUtf8Bytes > 2048)
                throw new ArgumentException("expectedTexts exceeds its 2,048-byte UTF-8 limit.");
            result[index] = text;
        }
        return result;
    }

    public static bool SameExpectedTexts(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);
}

/// <summary>Pure validation for one exact native TextBox update; this grants no control access.</summary>
public static class PluginControlTextEdits
{
    public const int MaximumCharacters = 4096;
    public const int MaximumUtf8Bytes = 16 * 1024;

    public static PluginControlTextEdit? Copy(string operation, string? fieldId, string? expectedText, string? text)
    {
        if (!string.Equals(operation, "setText", StringComparison.Ordinal))
        {
            if (fieldId is not null || expectedText is not null || text is not null)
                throw new ArgumentException("Only setText takes fieldId, expectedText, and text.");
            return null;
        }
        if (!Guid.TryParseExact(fieldId, "N", out var identity) || identity == Guid.Empty)
            throw new ArgumentException("setText needs the exact nonempty fieldId returned by native inspection.");
        var expected = CopyText(expectedText, "expectedText");
        var replacement = CopyText(text, "text");
        if (string.Equals(expected, replacement, StringComparison.Ordinal))
            throw new ArgumentException("setText needs replacement text different from expectedText; inspect is sufficient for a no-op.");
        return new(fieldId!, expected, replacement);
    }

    private static string CopyText(string? value, string name)
    {
        if (value is null || value.Length > MaximumCharacters || value.Contains('\0') ||
            Encoding.UTF8.GetByteCount(value) > MaximumUtf8Bytes)
            throw new ArgumentException($"{name} must contain at most {MaximumCharacters:N0} characters, " +
                                        $"at most {MaximumUtf8Bytes:N0} UTF-8 bytes, and no NUL.");
        return value;
    }
}

public sealed record PluginControlTextEdit(string FieldId, string ExpectedText, string Text);

/// <summary>Owner-approved observations from one actual mounted plugin view. A delivered click is not a semantic test pass.</summary>
public sealed record PluginControlButton(string Name, bool Enabled, bool Visible);
public sealed record PluginControlTextField(string Id, string Text, bool Enabled, bool Visible,
    bool ReadOnly, bool Limited = false);
public sealed record PluginControlSnapshot(string Id, IReadOnlyList<string> Texts,
    IReadOnlyList<PluginControlButton> Buttons, bool Limited = false)
{
    /// <summary>Opaque identities exist only for the actual live TextBox instances in this mounted view.</summary>
    public IReadOnlyList<PluginControlTextField> Fields { get; init; } = [];
}
public sealed record PluginControlObservation(PluginControlSnapshot Before, PluginControlSnapshot? After,
    bool ClickDelivered = false, bool InstanceRecreated = false)
{
    /// <summary>True only when this effect delivered one native TextBox value update.</summary>
    public bool TextSet { get; init; }
}

/// <summary>Durable host effect bound to one owner-approved immutable plugin generation. Claims are owner-only.</summary>
public sealed class PluginControlRequest
{
    public required string Id { get; init; }
    public required string PresentationId { get; init; }
    public required string SessionId { get; init; }
    public required string ProfileId { get; init; }
    public required string JobId { get; init; }
    public required string Operation { get; init; }
    public required string MountId { get; init; }
    public string? ButtonName { get; init; }
    public string[]? ExpectedTexts { get; init; }
    // Private host policy frozen only at new admission; it is never accepted as a tool argument.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? AssertionPolicyVersion { get; init; }
    public PluginControlTextEdit? TextEdit { get; init; }
    public required InterfacePluginReceipt Plugin { get; init; }
    // Captured from the approved installed package once; retained older effects may have no hash.
    public string? PackageFingerprint { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddSeconds(20);
    public string Status { get; set; } = "pending";
    public string? ClaimId { get; set; }
    public string? Error { get; set; }
    public PluginControlObservation? Observation { get; set; }
}
public sealed record PluginControlClaim(bool Claimed, PluginControlRequest Request);

/// <summary>Optional exact-receipt recovery. This must inspect the original host effect and never dispatch it again.</summary>
public interface IToolRecoveryExtension
{
    Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
        ToolExecutionEvidence previousEvidence, CancellationToken cancellationToken);
}
