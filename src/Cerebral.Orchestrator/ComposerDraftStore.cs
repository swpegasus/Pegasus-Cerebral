using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

internal sealed record ComposerDraftSnapshot(int SchemaVersion, string Binding, string? SelectedConversationId,
    bool SelectedIsNew, string? SelectedProjectId, IReadOnlyDictionary<string, string> Drafts);

/// <summary>Bounded plain-composer state owned by one user and one explicit desktop storage binding.</summary>
internal sealed class ComposerDraftStore
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal const int MaximumDraftCharacters = 1_048_576;
    internal const int MaximumDrafts = 1_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly string binding;
    private bool initialized;
    internal string FilePath { get; }
    internal ComposerDraftSnapshot? Persisted { get; private set; }
    internal int PersistedBytes { get; private set; }

    internal ComposerDraftStore(string directory, string ownerBinding)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Contains('\0') || string.IsNullOrWhiteSpace(ownerBinding))
            throw new ArgumentException("Composer drafts require an absolute, explicit owner directory and binding.");
        FilePath = Path.Combine(Path.GetFullPath(directory), "composer-drafts.txt");
        binding = Hash(ownerBinding);
    }

    internal ComposerDraftSnapshot Read()
    {
        ScopedFilePolicy.RejectLinks(FilePath);
        string text;
        try { text = DesktopPluginFiles.ReadUtf8(FilePath, MaximumBytes); }
        catch (FileNotFoundException) { return Empty(); }
        catch (DirectoryNotFoundException) { return Empty(); }
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
            throw new InvalidDataException("The composer draft snapshot has an invalid or duplicate property.");
        if (root.TryGetProperty("drafts", out var entries) && entries.ValueKind == JsonValueKind.Object &&
            entries.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != entries.EnumerateObject().Count())
            throw new InvalidDataException("The composer draft snapshot repeats a conversation.");
        var snapshot = JsonSerializer.Deserialize<ComposerDraftSnapshot>(text, Json)
            ?? throw new InvalidDataException("The composer draft snapshot is empty.");
        Validate(snapshot);
        initialized = true;
        Persisted = snapshot;
        PersistedBytes = Encoding.UTF8.GetByteCount(text);
        return snapshot;

        ComposerDraftSnapshot Empty()
        {
            initialized = true;
            return new(1, binding, null, true, null, new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    internal void Write(IReadOnlyDictionary<string, string> drafts, string? selectedId, bool selectedIsNew, string? selectedProjectId)
    {
        // Refuse to replace an existing unread/unsupported snapshot. The window retains its
        // unsaved text and exposes the failure; it never adopts another instance's content.
        if (!initialized) _ = Read();
        var snapshot = new ComposerDraftSnapshot(1, binding, selectedId, selectedIsNew, selectedProjectId,
            new Dictionary<string, string>(drafts, StringComparer.Ordinal));
        Validate(snapshot);
        var text = JsonSerializer.Serialize(snapshot, Json);
        ScopedFilePolicy.RejectLinks(FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        ScopedFilePolicy.RejectLinks(FilePath);
        DesktopPluginFiles.WriteUtf8Atomically(FilePath, text, MaximumBytes);
        Persisted = snapshot;
        PersistedBytes = Encoding.UTF8.GetByteCount(text);
    }

    private void Validate(ComposerDraftSnapshot value)
    {
        if (value.SchemaVersion != 1 || value.Binding != binding)
            throw new InvalidDataException("The composer draft snapshot belongs to a different user/desktop instance or unsupported version.");
        ValidateId(value.SelectedConversationId, optional: true);
        ValidateId(value.SelectedProjectId, optional: true);
        if (value.Drafts is null || value.Drafts.Count > MaximumDrafts)
            throw new InvalidDataException($"Composer drafts exceed the retained count limit ({MaximumDrafts}); no drafts were removed.");
        foreach (var pair in value.Drafts)
        {
            ValidateId(pair.Key);
            if (pair.Value is null || pair.Value.Length is < 1 or > MaximumDraftCharacters)
                throw new InvalidDataException($"Composer draft text must contain 1–{MaximumDraftCharacters:N0} characters; no text was truncated.");
        }
    }

    private static void ValidateId(string? value, bool optional = false)
    {
        if (value is null && optional) return;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new InvalidDataException("Composer draft conversation/project identities must contain 1–128 visible characters.");
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
