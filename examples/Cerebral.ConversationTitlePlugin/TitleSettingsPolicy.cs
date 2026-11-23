using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.ConversationTitlePlugin;

internal sealed record TitlePreferences
{
    internal const int MaximumSourceProfileIds = 1024;
    public bool Enabled { get; }
    public string? ProfileId { get; }
    public long? ProfileRevision { get; }
    public IReadOnlyList<string> SourceProfileIds { get; }
    internal static TitlePreferences Default { get; } = new(false, null, null, []);

    // Legacy private construction remains unconfigured; it never infers source scope.
    internal TitlePreferences(bool enabled, string? profileId, long? profileRevision)
        : this(enabled, profileId, profileRevision, []) { }

    internal TitlePreferences(bool enabled, string? profileId, long? profileRevision, IEnumerable<string> sourceProfileIds)
    {
        if (profileId is not null || profileRevision is not null)
        {
            if (profileId is null || profileRevision is null || profileRevision.Value < 1)
                throw new ArgumentException("An exact local inference profile ID and revision are required together.");
            TitlePolicy.Identifier(profileId);
        }
        ArgumentNullException.ThrowIfNull(sourceProfileIds);
        var copied = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        // Resource bound on this package's settings, never a system profile-count limit.
        // Enumerate at most1025 entries; caller Count/indexer is not trusted.
        foreach (var id in sourceProfileIds)
        {
            if (copied.Count == MaximumSourceProfileIds)
                throw new ArgumentException("Naming source-profile settings exceed their metadata ID bound.", nameof(sourceProfileIds));
            if (string.IsNullOrEmpty(id) || id.Length > 128 || id == "*" ||
                id.Any(value => char.IsWhiteSpace(value) || char.IsControl(value)) || !unique.Add(id))
                throw new ArgumentException("Naming source profiles require unique exact IDs without wildcards, whitespace or control characters.", nameof(sourceProfileIds));
            // Preserve exact valid Unicode when the local reference is serialized.
            TitlePolicy.Bounded(id, 512);
            copied.Add(id);
        }
        Enabled = enabled; ProfileId = profileId; ProfileRevision = profileRevision;
        SourceProfileIds = Array.AsReadOnly(copied.ToArray());
        TitlePolicy.Bounded(Values().GetRawText(), SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes);
    }

    internal JsonElement Values() => JsonSerializer.SerializeToElement(this, TitlePolicy.Json);
    internal TitlePreferences WithEnabled(bool enabled) => new(enabled, ProfileId, ProfileRevision, SourceProfileIds);

    internal static TitlePreferences Read(JsonElement value)
    {
        TitlePolicy.ExactFields(value, "enabled", "profileId", "profileRevision", "sourceProfileIds");
        var (profile, revision) = InferenceReference(value);
        var source = value.GetProperty("sourceProfileIds");
        if (source.ValueKind != JsonValueKind.Array) throw new ArgumentException("Naming source profiles must be an array.");
        return new(ReadEnabled(value), profile, revision, SourceIds(source));
    }

    // Legacy values are explicit schema1, not a heuristic source-profile assignment.
    // Their enabled intent may be projected portably, but local upgrade disables it.
    internal static TitlePreferences ReadLegacy(JsonElement value)
    {
        TitlePolicy.ExactFields(value, "enabled", "profileId", "profileRevision");
        var (profile, revision) = InferenceReference(value);
        return new(ReadEnabled(value), profile, revision, []);
    }
    internal static TitlePreferences UpgradeLegacy(JsonElement value) => ReadLegacy(value).WithEnabled(false);
    internal static TitlePreferences ReadRetained(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("sourceProfileIds", out _)
            ? Read(value) : ReadLegacy(value);
    internal static bool ReadEnabled(JsonElement value)
    {
        if (value.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("Naming enabled must be boolean.");
        return value.GetProperty("enabled").GetBoolean();
    }
    private static (string? Profile, long? Revision) InferenceReference(JsonElement value)
    {
        var profile = value.GetProperty("profileId"); var revision = value.GetProperty("profileRevision");
        if (profile.ValueKind == JsonValueKind.Null && revision.ValueKind == JsonValueKind.Null) return (null, null);
        if (profile.ValueKind != JsonValueKind.String || !revision.TryGetInt64(out var number) || number < 1)
            throw new ArgumentException("An exact local inference profile ID and revision are required together.");
        return (profile.GetString(), number);
    }
    private static IEnumerable<string> SourceIds(JsonElement source)
    {
        foreach (var item in source.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new ArgumentException("Each naming source profile must be an exact string ID.");
            yield return item.GetString()!;
        }
    }
}

/// <summary>Pure settings-shape admission only. No subscription, store, inference or UI side effect.
/// A later host adapter must validate current local refs and register its effective subscription.</summary>
internal sealed class TitleSettingsPolicy : IPluginSettingsPolicy
{
    internal const int SettingsVersion = 2;
    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
    {
        try
        {
            Coordinate(incoming);
            TitlePreferences preferences;
            if (portable)
            {
                TitlePolicy.ExactFields(incoming.Values, "enabled");
                var current = currentValues is { } retained ? TitlePreferences.ReadRetained(retained) : TitlePreferences.Default;
                // Portable enabled is requested intent. Missing local refs/source scope
                // remain unconfigured; import must never invent profiles or authorize all.
                preferences = current.WithEnabled(TitlePreferences.ReadEnabled(incoming.Values));
            }
            else
            {
                preferences = incoming.SettingsVersion == 1 ? TitlePreferences.UpgradeLegacy(incoming.Values) : TitlePreferences.Read(incoming.Values);
                // A new executable edition requires a new explicit source subscription.
                if (incoming.SourcePackageVersion == TitlePackageIdentity.LegacyPackageVersion)
                    preferences = new(false, preferences.ProfileId, preferences.ProfileRevision, []);
            }
            return new(true, SettingsVersion, preferences.Values());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
        { return new(false, SettingsVersion, TitlePreferences.Default.Values(), "Conversation naming preferences have an unsupported coordinate, version or shape."); }
    }
    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current)
    {
        Coordinate(current);
        var value = current.SettingsVersion == 1 ? TitlePreferences.ReadLegacy(current.Values) : TitlePreferences.Read(current.Values);
        // Both source IDs and inference refs are local references, never portable grants.
        return new(SettingsVersion, JsonSerializer.SerializeToElement(new { enabled = value.Enabled }),
            (value.ProfileId is null ? 0 : 2) + (value.SourceProfileIds.Count == 0 ? 0 : 1));
    }
    private static void Coordinate(PluginSettingsDocument document)
    {
        if (document.PluginId != TitlePackageIdentity.PackageId || document.Scope != SettingsPreferenceContract.GlobalScope ||
            document.ScopeId is not null || (document.SourcePackageVersion != TitlePackageIdentity.PackageVersion && document.SourcePackageVersion != TitlePackageIdentity.LegacyPackageVersion) ||
            document.SettingsVersion is not (1 or SettingsVersion))
            throw new ArgumentException("The naming settings coordinate or version is unsupported.");
        TitlePolicy.Bounded(document.Values.GetRawText(), SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes);
    }
}
