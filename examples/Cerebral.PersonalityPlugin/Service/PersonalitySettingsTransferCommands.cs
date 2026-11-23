using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>Explicit owner-only pending review/preparation. Retained host purposes and package policy own admission.</summary>
internal sealed class PersonalitySettingsTransferCommands(PersonalityServicePlugin plugin)
{
    internal const string GetOperation = "personality.preferences.transfer.get";
    internal const string SaveOperation = "personality.preferences.transfer.save";
    internal const string CurrentPrepareOperation = "personality.preferences.transfer.prepare";
    internal const string PrepareOperation = "personality.preferences.pending.prepare";
    internal const string ApplyOperation = "personality.preferences.pending.apply";

    internal object Execute(string operation, JsonElement arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (operation == GetOperation)
        {
            PersonalityServicePlugin.Fields(arguments, []);
            return Read();
        }
        if (operation == SaveOperation)
        {
            PersonalityServicePlugin.Fields(arguments, ["schemaVersion", "expectedRevision", "values"]);
            PersonalityServicePlugin.RequireVersion(arguments, 2);
            var view = Read();
            var expected = PersonalityServicePlugin.Integer(arguments, "expectedRevision", 0);
            if (view.State.Revision != expected)
                throw new InvalidOperationException("Personality preferences changed; reload before saving.");
            RequireActiveSchema(view);
            var preferences = PersonalitySettingsCodec.Read(arguments.GetProperty("values"));
            var values = PersonalitySettingsCodec.Write(preferences);
            if (preferences.Enabled && plugin.ReadPrepared(preferences).Preset is null)
                throw new InvalidOperationException("Enabling personality requires an exact prepared source.");
            var proposal = new PluginSettingsDocument(PersonalityContract.PluginId, SettingsPreferenceContract.GlobalScope,
                null, PersonalitySettingsPolicy.PackageVersion, PersonalitySettingsPolicy.SettingsVersion, values);
            token.ThrowIfCancellationRequested();
            // No PendingId is local admission. This is the explicit disable/edit lane while deferred
            // inputs remain present; it never consumes, relabels or claims admission of those inputs.
            return plugin.RequireTransfer().SaveTransfer(new(new(proposal, expected)));
        }
        if (operation == CurrentPrepareOperation)
        {
            PersonalityServicePlugin.Fields(arguments,
                ["schemaVersion", "expectedRevision", "expectedStoreRevision", "expectedStoreSha256"]);
            PersonalityServicePlugin.RequireVersion(arguments, 1);
            var current = Current(arguments);
            var source = plugin.PrepareSource(current.Preferences,
                PersonalityServicePlugin.Integer(arguments, "expectedStoreRevision", 0),
                Hash(arguments, "expectedStoreSha256"), token)
                ?? throw new InvalidOperationException("The current personality source is unavailable.");
            var confirmed = Current(arguments);
            if (confirmed.ValuesHash != current.ValuesHash)
                throw new InvalidOperationException("Current personality values changed during preparation; reload. Any unused source remains retained.");
            var prepared = plugin.ReadPrepared(current.Preferences);
            if (prepared.Preset is null || PersonalityContract.SourceId(prepared.Preset) != PersonalityContract.SourceId(source))
                throw new InvalidOperationException("The prepared current source is no longer available; reload.");
            token.ThrowIfCancellationRequested();
            return new
            {
                schemaVersion = 1, settingsRevision = confirmed.Transfer.State.Revision,
                valuesSha256 = current.ValuesHash, values = current.Values,
                preset = PersonalityServicePlugin.PresetReply(source, true),
                sources = PersonalityServicePlugin.SourceState(prepared.Store)
            };
        }
        if (operation == PrepareOperation)
        {
            PersonalityServicePlugin.Fields(arguments,
                ["schemaVersion", "expectedRevision", "pendingId", "expectedPurpose",
                 "expectedStoreRevision", "expectedStoreSha256"]);
            PersonalityServicePlugin.RequireVersion(arguments, 1);
            var selected = Select(arguments);
            var source = plugin.PrepareSource(selected.Preferences,
                PersonalityServicePlugin.Integer(arguments, "expectedStoreRevision", 0),
                Hash(arguments, "expectedStoreSha256"), token, requireSource: selected.Preferences.Enabled);
            // Preparation does not consume pending state. A concurrent central change retains any unused append.
            var confirmed = Select(arguments);
            if (confirmed.PendingHash != selected.PendingHash || confirmed.ValuesHash != selected.ValuesHash)
                throw new InvalidOperationException("Pending preferences changed during preparation; reload. Any unused source remains retained.");
            var prepared = plugin.ReadPrepared(selected.Preferences);
            if (source is not null && (prepared.Preset is null || prepared.Preset.Revision != source.Revision ||
                PersonalityContract.SourceId(prepared.Preset) != PersonalityContract.SourceId(source)))
                throw new InvalidOperationException("The prepared pending source is no longer available; reload before applying.");
            token.ThrowIfCancellationRequested();
            return new
            {
                schemaVersion = 1, pendingId = selected.Pending.Id, purpose = selected.Purpose,
                settingsRevision = confirmed.Transfer.State.Revision,
                pendingSha256 = selected.PendingHash, valuesSha256 = selected.ValuesHash,
                values = selected.Values, preset = source is null ? null : PersonalityServicePlugin.PresetReply(source, true),
                sources = PersonalityServicePlugin.SourceState(prepared.Store)
            };
        }
        if (operation == ApplyOperation)
        {
            PersonalityServicePlugin.Fields(arguments,
                ["schemaVersion", "expectedRevision", "pendingId", "expectedPurpose",
                 "pendingSha256", "valuesSha256", "expectedSourceId", "expectedSourceRevision", "expectedSourceSha256"]);
            PersonalityServicePlugin.RequireVersion(arguments, 1);
            var selected = Select(arguments);
            if (Hash(arguments, "pendingSha256") != selected.PendingHash ||
                Hash(arguments, "valuesSha256") != selected.ValuesHash)
                throw new InvalidOperationException("The reviewed pending input or admitted values changed; reload before applying.");
            var noSource = arguments.GetProperty("expectedSourceId").ValueKind == JsonValueKind.Null &&
                arguments.GetProperty("expectedSourceRevision").ValueKind == JsonValueKind.Null &&
                arguments.GetProperty("expectedSourceSha256").ValueKind == JsonValueKind.Null;
            if (noSource)
            {
                if (selected.Preferences.Enabled)
                    throw new InvalidOperationException("Enabling pending personality requires the exact prepared source.");
            }
            else
            {
                var prepared = plugin.ReadPrepared(selected.Preferences).Preset
                    ?? throw new InvalidOperationException("The selected pending personality source is not prepared.");
                if (PersonalityServicePlugin.Text(arguments, "expectedSourceId", 256) != PersonalityContract.SourceId(prepared) ||
                    PersonalityServicePlugin.Integer(arguments, "expectedSourceRevision", 1) != prepared.Revision ||
                    Hash(arguments, "expectedSourceSha256") != prepared.Sha256)
                    throw new InvalidOperationException("The selected pending source does not match the exact prepared source.");
            }

            var proposal = new PluginSettingsDocument(PersonalityContract.PluginId, SettingsPreferenceContract.GlobalScope,
                null, PersonalitySettingsPolicy.PackageVersion, PersonalitySettingsPolicy.SettingsVersion, selected.Values);
            // Core validates the submitted proposal, not the retained original. Complete/local use the admitted
            // canonical shape; portable must retain its projection shape so destination-owned fields stay local.
            if (selected.Purpose == SettingsTransferPurpose.Portable)
            {
                var portable = plugin.ExportPortable(proposal);
                proposal = proposal with { SettingsVersion = portable.SettingsVersion, Values = portable.Values };
                var repeated = plugin.Admit(proposal, selected.Transfer.State.Active?.Values, portable: true);
                if (!repeated.Accepted || !JsonElement.DeepEquals(repeated.Values, selected.Values))
                    throw new InvalidDataException("The portable proposal would change the reviewed admission.");
            }
            token.ThrowIfCancellationRequested();
            // Scoped host revalidates with the actual registered service policy and retains other pending items.
            // No owner-client validation flags, cached export projection or caller-supplied purpose are forwarded.
            return plugin.RequireTransfer().SaveTransfer(new(new(proposal, selected.Transfer.State.Revision)
                { PendingId = selected.Pending.Id }));
        }
        throw new ArgumentException("Unsupported personality transfer operation.");
    }

    private CurrentSelection Current(JsonElement arguments)
    {
        var view = Read();
        if (view.State.Revision != PersonalityServicePlugin.Integer(arguments, "expectedRevision", 0))
            throw new InvalidOperationException("Current personality preferences changed; reload before preparing.");
        RequireActiveSchema(view);
        var active = view.State.Active ?? throw new InvalidOperationException("Current personality preferences are unavailable.");
        var preferences = PersonalitySettingsCodec.Read(active.Values);
        var values = PersonalitySettingsCodec.Write(preferences);
        return new(view, preferences, values, PersonalityContributionCodec.Hash(values));
    }

    private PendingSelection Select(JsonElement arguments)
    {
        var view = Read();
        if (view.State.Revision != PersonalityServicePlugin.Integer(arguments, "expectedRevision", 0))
            throw new InvalidOperationException("Personality pending preferences changed; reload before continuing.");
        var id = PersonalityServicePlugin.Text(arguments, "pendingId", 160);
        if (id.Any(char.IsControl)) throw new ArgumentException("Invalid personality pending identity.");
        var pending = view.State.Pending.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The selected pending personality input is unavailable.");
        var purpose = view.PendingPurposes.Single(item => item.PendingId == id).Purpose;
        if (purpose != PersonalityServicePlugin.Text(arguments, "expectedPurpose", 32))
            throw new InvalidOperationException("The selected pending purpose changed; reload before continuing.");
        RequireActiveSchema(view);
        var admitted = purpose == SettingsTransferPurpose.Complete
            ? plugin.AdmitComplete(pending.Document, view.State.Active?.Values)
            : plugin.Admit(pending.Document, view.State.Active?.Values, purpose == SettingsTransferPurpose.Portable);
        if (!admitted.Accepted || admitted.SettingsVersion != PersonalitySettingsPolicy.SettingsVersion)
            throw new InvalidDataException("The package rejected the selected pending personality input.");
        var preferences = PersonalitySettingsCodec.Read(admitted.Values);
        var values = PersonalitySettingsCodec.Write(preferences);
        var original = PersonalityContract.Element(new { pendingId = pending.Id, purpose, document = pending.Document });
        return new(view, pending, purpose, preferences, values,
            PersonalityContributionCodec.Hash(original), PersonalityContributionCodec.Hash(values));
    }

    private PluginSettingsTransferState Read()
    {
        var view = plugin.RequireTransfer().GetTransfer();
        if (view is null || view.SchemaVersion != CompleteSettingsContract.PluginEnvelopeVersion || view.State is null ||
            view.State.Coordinate != new PluginSettingsCoordinate(PersonalityContract.PluginId, SettingsPreferenceContract.GlobalScope) ||
            view.State.Revision < 0 || view.State.Pending is null || view.PendingPurposes is null ||
            view.State.Pending.Count > SettingsPreferenceContract.MaximumPendingPerDocument ||
            view.PendingPurposes.Count != view.State.Pending.Count ||
            view.State.Active is { } active && active.Coordinate != view.State.Coordinate)
            throw new InvalidDataException("The personality settings transfer envelope is invalid.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var purposes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in view.PendingPurposes)
        {
            if (item is null || item.PendingId is not { Length: > 0 } || !purposes.TryAdd(item.PendingId, item.Purpose) ||
                item.Purpose is not (SettingsTransferPurpose.Complete or SettingsTransferPurpose.Portable or SettingsTransferPurpose.Local))
                throw new InvalidDataException("The personality pending purpose mapping is invalid.");
        }
        foreach (var item in view.State.Pending)
        {
            if (item is null || item.Id is not { Length: > 0 } || !ids.Add(item.Id) || item.Document is null ||
                item.Document.Coordinate != view.State.Coordinate || !purposes.TryGetValue(item.Id, out var purpose) ||
                item.Portable != (purpose == SettingsTransferPurpose.Portable))
                throw new InvalidDataException("The personality pending document mapping is invalid.");
        }
        return view;
    }

    private static void RequireActiveSchema(PluginSettingsTransferState view)
    {
        if (view.State.Active is { } active && active.SettingsVersion != PersonalitySettingsPolicy.SettingsVersion)
            throw new InvalidDataException("Active personality preferences require explicit current-schema admission.");
    }

    private static string Hash(JsonElement arguments, string name)
    {
        var value = PersonalityServicePlugin.Text(arguments, name, 64);
        PersonalityAdmissionEngine.RequireHash(value);
        return value;
    }

    private sealed record CurrentSelection(PluginSettingsTransferState Transfer,
        PersonalitySettingsPreferences Preferences, JsonElement Values, string ValuesHash);

    private sealed record PendingSelection(PluginSettingsTransferState Transfer, PluginSettingsDeferredDocument Pending,
        string Purpose, PersonalitySettingsPreferences Preferences, JsonElement Values, string PendingHash, string ValuesHash);
}
