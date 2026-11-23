using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.ConversationTitlePlugin;

// These are package-private values, not an SDK, authorization token or proof of acceptance.
// A future host adapter must supply committed source facts and current owner observations.
internal sealed record AcceptedTitleInput(string OwnerBinding, string ConversationId, string RequestId,
    string EventId, string DefinitionId, string? ProjectId, bool HasPriorUserInput, bool UserAuthored,
    string Text, string CurrentTitle, long TitleRevision, string? SourceProfileId = null);
internal sealed record TitleSeed(int SchemaVersion, string OwnerBinding, string ConversationId, string FirstRequestId,
    string FirstEventId, string FirstInputSha256, string Excerpt, string DefaultTitle, long ExpectedTitleRevision,
    string? ProjectId, string ProfileId, long ProfileRevision, long SettingsRevision);
internal sealed record TitleTarget(bool Exists, string OwnerBinding, string ConversationId, string FirstRequestId,
    string FirstEventId, string FirstInputSha256, string Title, long TitleRevision, string? ProjectId,
    bool NamingEnabled, string? ProfileId, long? ProfileRevision, long SettingsRevision);
internal sealed record TitleDecision(string WorkKey, string? Title, bool Apply, bool UsedFallback, string DiagnosticCode);

internal static class TitlePolicy
{
    internal const int MaximumInputBytes = 128 * 1024, MaximumExcerptBytes = 4096;
    internal const int MaximumTitleScalars = 72, MaximumTitleBytes = 288, MaximumTitleWords = 10;
    internal const int ResponseTokenLimit = 96;
    internal const string WorkUserText = "Create a short descriptive conversation title.";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static TitleSeed? Capture(AcceptedTitleInput input, TitlePreferences settings, long settingsRevision)
    {
        if (!settings.Enabled || input.SourceProfileId is null ||
            !settings.SourceProfileIds.Contains(input.SourceProfileId, StringComparer.Ordinal) ||
            settings.ProfileId is null || settings.ProfileRevision is null ||
            settingsRevision < 1 || !input.UserAuthored || input.HasPriorUserInput ||
            input.DefinitionId != ConversationWorkflowContract.DefinitionId || input.TitleRevision != 0) return null;
        // This package may omit optional naming, never reject/change accepted input.
        // Empty UserText also covers attachment-only submissions; keep the derived title.
        if (!TryCaptureText(input.Text, out var hash, out var excerpt)) return null;
        var seed = new TitleSeed(1, input.OwnerBinding, input.ConversationId, input.RequestId, input.EventId,
            hash, excerpt,
            input.CurrentTitle, 0, input.ProjectId, settings.ProfileId, settings.ProfileRevision.Value, settingsRevision);
        Validate(seed);
        return seed;
    }

    internal static void Validate(TitleSeed seed)
    {
        if (seed.SchemaVersion != 1 || seed.ExpectedTitleRevision != 0 || seed.ProfileRevision < 1 || seed.SettingsRevision < 1)
            throw new ArgumentException("The title snapshot versions are invalid.");
        Identifier(seed.OwnerBinding, 256); Identifier(seed.ConversationId); Identifier(seed.FirstRequestId); Identifier(seed.FirstEventId);
        Identifier(seed.ProfileId); if (seed.ProjectId is not null) Identifier(seed.ProjectId);
        if (seed.FirstInputSha256 is not { Length: 64 } || seed.FirstInputSha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("A lowercase first-input SHA256 is required.");
        Bounded(seed.Excerpt, MaximumExcerptBytes, allowBlank: true);
        // This is an exact source-display compare value, not a NEW rename proposal.
        // The store can derive tabs/separators from accepted input; preserve them for CAS.
        Bounded(seed.DefaultTitle, ConversationRenameContract.MaximumTitleUtf8Bytes, allowBlank: true);
        if (seed.DefaultTitle.EnumerateRunes().Count() > ConversationRenameContract.MaximumTitleScalars)
            throw new ArgumentException("The source display title exceeds its bound.");
    }

    // Stable across configuration changes/reconnects: host admission must retain the winning
    // input for this exact first event, rather than resubmitting an uncertain generation.
    internal static string WorkKey(TitleSeed seed)
    {
        Validate(seed);
        return SourceWorkKey(seed.ConversationId, seed.FirstRequestId, seed.FirstEventId, seed.FirstInputSha256);
    }
    internal static string SourceWorkKey(string conversationId, string requestId, string eventId, string inputSha256)
    {
        Identifier(conversationId); Identifier(requestId); Identifier(eventId);
        if (inputSha256 is not { Length: 64 } || inputSha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("A lowercase full-input SHA256 is required.");
        var value = "cerebral.conversation-title.v1\0" + conversationId + "\0" + requestId + "\0" + eventId + "\0" + inputSha256;
        return Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(value)));
    }

    internal static bool TryCaptureText(string? text, out string fullHash, out string excerpt)
    {
        fullHash = excerpt = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { Bounded(text, MaximumInputBytes); }
        catch (ArgumentException) { return false; }
        fullHash = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(text)));
        excerpt = Prefix(text, MaximumExcerptBytes);
        return true;
    }

    internal static ConversationRenameRequest? ProposeRename(TitleSeed seed, TitleDecision result)
    {
        Validate(seed);
        if (!result.Apply || result.WorkKey != WorkKey(seed) || !TryTitle(result.Title, out var title) || title == seed.DefaultTitle) return null;
        return new(RenameRequestId(seed), ConversationWorkflowContract.DefinitionId, seed.ConversationId, 0, title);
    }

    internal static string WorkRequestId(TitleSeed seed) => "title-" + WorkKey(seed);
    internal static string WorkConversationId(TitleSeed seed) => "title-work-" + WorkKey(seed);
    internal static string RenameRequestId(TitleSeed seed) => "title-rename-" + WorkKey(seed);

    internal static TitleDecision FromModel(TitleSeed seed, string? completedJson, bool completed)
    {
        Validate(seed);
        if (completed && completedJson is not null)
        {
            try
            {
                Bounded(completedJson, 4096);
                using var document = JsonDocument.Parse(completedJson, new JsonDocumentOptions { MaxDepth = 4 });
                ExactFields(document.RootElement, "title");
                if (document.RootElement.GetProperty("title").ValueKind == JsonValueKind.String &&
                    TryTitle(document.RootElement.GetProperty("title").GetString(), out var title))
                    return new(WorkKey(seed), title, true, false, "title-generated");
            }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException) { }
        }
        return new(WorkKey(seed), Fallback(seed.Excerpt), true, true, completed ? "title-output-invalid" : "title-generation-unavailable");
    }

    internal static ConversationRenameRequest? PrepareRename(TitleSeed seed, TitleDecision result, TitleTarget current)
    {
        Validate(seed);
        var proposed = ProposeRename(seed, result);
        if (proposed is null ||
            !current.Exists || current.OwnerBinding != seed.OwnerBinding || current.ConversationId != seed.ConversationId ||
            current.FirstRequestId != seed.FirstRequestId || current.FirstEventId != seed.FirstEventId ||
            current.FirstInputSha256 != seed.FirstInputSha256 || current.ProjectId != seed.ProjectId ||
            current.TitleRevision != seed.ExpectedTitleRevision || current.Title != seed.DefaultTitle ||
            !current.NamingEnabled || current.SettingsRevision != seed.SettingsRevision ||
            current.ProfileId != seed.ProfileId || current.ProfileRevision != seed.ProfileRevision) return null;
        // The owner must use this exact CAS. The desktop workspace/session rename is not equivalent.
        // This function proposes metadata only; it performs no command or database mutation.
        return proposed;
    }

    internal static bool TryTitle(string? value, out string title)
    {
        title = "";
        if (value is null || value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029')) return false;
        try { Bounded(value, MaximumTitleBytes); }
        catch (ArgumentException) { return false; }
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var normalized = string.Join(" ", words);
        if (words.Length is < 1 or > MaximumTitleWords || normalized.EnumerateRunes().Count() > MaximumTitleScalars) return false;
        title = normalized; return true;
    }

    internal static string Fallback(string text)
    {
        var printable = new string(text.Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c).ToArray());
        var words = printable.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(6);
        var title = string.Join(" ", words);
        if (title.Length == 0) return "New conversation";
        var runes = title.EnumerateRunes().Take(48).ToArray();
        return string.Concat(runes.Select(r => r.ToString())).Trim();
    }
    internal static string Prefix(string text, int maximumBytes)
    {
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes) break;
            result.Append(rune); bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
    internal static void Identifier(string? value, int maximum = 128)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("A bounded explicit identifier is required.");
    }
    internal static void Bounded(string? text, int maximumBytes, bool allowBlank = false)
    {
        if (text is null || !allowBlank && string.IsNullOrWhiteSpace(text) || text?.Contains('\0') == true)
            throw new ArgumentException("A bounded text value is required.");
        try { if (Utf8.GetByteCount(text!) > maximumBytes) throw new ArgumentException("Text exceeds its UTF8 bound."); }
        catch (EncoderFallbackException error) { throw new ArgumentException("Text contains invalid Unicode.", error); }
    }
    internal static void ExactFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object.");
        var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != fields.Length || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new ArgumentException("Unexpected, duplicate or missing fields.");
    }
}
