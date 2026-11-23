using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>Central preferences and immutable package sources. The host owns accepted provenance/lifecycle.</summary>
public sealed class PersonalityServicePlugin : IServicePlugin, IServiceCommandExtension, IWorkflowModule,
    IServiceWorkflowContextModule, IPluginCompleteSettingsPolicy
{
    private readonly object gate = new();
    private readonly PersonalitySettingsPolicy policy = new();
    private readonly IReadOnlyList<IWorkflowCommandHandler> commands;
    private readonly IReadOnlyList<IWorkflowContextContribution> contributions;
    private PersonalityStore? store;
    private PersonalityChatStore? chats;
    private IScopedPluginSettings? settings;
    private bool disposed;

    public PersonalityServicePlugin()
    {
        commands = Array.AsReadOnly<IWorkflowCommandHandler>([new PersonalityContextProvider(this)]);
        contributions = Array.AsReadOnly<IWorkflowContextContribution>([new PersonalityWorkflowContextContribution(this)]);
    }
    public string Id => PersonalityContract.PluginId;
    public IReadOnlyList<IToolExtension> ToolExtensions { get; } = Array.Empty<IToolExtension>();
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [this];
    public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = Array.Empty<IWorkflowDefinition>();
    public IReadOnlyList<IWorkflowCommandHandler> Commands => commands;
    public IReadOnlyList<IWorkflowContextContribution> ContextContributions => contributions;
    public IReadOnlyList<string> Operations { get; } = Array.AsReadOnly<string>(
        ["personality.list", "personality.get", "personality.settings", "personality.preferences.get",
         "personality.preferences.save", "personality.sources.get", "personality.source.prepare",
         PersonalitySettingsTransferCommands.GetOperation, PersonalitySettingsTransferCommands.SaveOperation,
         PersonalitySettingsTransferCommands.CurrentPrepareOperation, PersonalitySettingsTransferCommands.PrepareOperation,
         PersonalitySettingsTransferCommands.ApplyOperation, PersonalityChatContract.GetOperation,
         PersonalityChatContract.SaveOperation, PersonalityChatContract.ImportOperation, PersonalityChatContract.PrivateGetOperation]);

    public void Initialize(IServicePluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (store is not null) throw new InvalidOperationException("The personality service plugin is already initialized.");
            if (host.PluginId != Id) throw new InvalidOperationException("Unexpected personality service package identity.");
            store = new(host.DataDirectory);
            chats = new(host.DataDirectory, store);
            store.PrivateRetention = chats.PrivateRetention;
            if (host is IServicePluginSettingsHost central)
            {
                try { settings = central.Settings; }
                catch (NotSupportedException) { settings = null; }
            }
        }
    }

    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, JsonElement? currentValues, bool portable)
        => policy.Admit(incoming, currentValues, portable);
    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current) => policy.ExportPortable(current);
    public PluginSettingsAdmission AdmitComplete(PluginSettingsDocument incoming, JsonElement? currentValues)
        => policy.AdmitComplete(incoming, currentValues);
    public PluginSettingsCompleteProjection ExportComplete(PluginSettingsDocument current) => policy.ExportComplete(current);

    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Old shapes cannot mutate the old combined authority or become an alternate host admission path.
        if (operation is "personality.save" or "personality.select" or
            PersonalityAdmissionCommands.CaptureOperation or PersonalityAdmissionCommands.RestoreOperation)
            throw new NotSupportedException("This personality version requires central preferences and the host-only contribution facet.");
        lock (gate)
        {
            RequireAdmissionAvailability();
            ValidateArguments(arguments, operation is "personality.preferences.save" or
                PersonalitySettingsTransferCommands.SaveOperation or PersonalityChatContract.ImportOperation ? 40 * 1024 : 8192);
            var currentStore = store!;
            object result;
            switch (operation)
            {
                case PersonalityChatContract.GetOperation:
                    Fields(arguments, ["schemaVersion", "conversationId"]);
                    PersonalityChatContract.Version(arguments);
                    result = PersonalityChatContract.Reply(chats!.Get(PersonalityChatContract.ConversationId(arguments),
                        () => ReadPreferences().Preferences));
                    break;
                case PersonalityChatContract.SaveOperation:
                    result = PersonalityChatContract.Reply(chats!.Save(arguments, () => ReadPreferences().Preferences, token));
                    break;
                case PersonalityChatContract.ImportOperation:
                    result = chats!.Import(arguments, token);
                    break;
                case PersonalityChatContract.PrivateGetOperation:
                    Fields(arguments, ["schemaVersion"]);
                    PersonalityChatContract.Version(arguments);
                    result = chats!.PrivateMetadata();
                    break;
                case PersonalitySettingsTransferCommands.GetOperation:
                case PersonalitySettingsTransferCommands.SaveOperation:
                case PersonalitySettingsTransferCommands.CurrentPrepareOperation:
                case PersonalitySettingsTransferCommands.PrepareOperation:
                case PersonalitySettingsTransferCommands.ApplyOperation:
                    result = new PersonalitySettingsTransferCommands(this).Execute(operation, arguments, token);
                    break;
                case "personality.preferences.get":
                case "personality.settings":
                    Fields(arguments, []);
                    var current = ReadPreferences();
                    result = new { schemaVersion = 2, settingsVersion = PersonalitySettingsPolicy.SettingsVersion,
                        revision = current.State.Revision, values = PersonalitySettingsCodec.Write(current.Preferences),
                        state = current.State,
                        provider = PersonalityContextProvider.Description };
                    break;
                case "personality.preferences.save":
                    Fields(arguments, ["schemaVersion", "expectedRevision", "values"]);
                    RequireVersion(arguments, 2);
                    var values = PersonalitySettingsCodec.Write(PersonalitySettingsCodec.Read(arguments.GetProperty("values")));
                    result = RequireSettings().Save(PersonalitySettingsPolicy.SettingsVersion, values,
                        Integer(arguments, "expectedRevision", 0));
                    break;
                case "personality.sources.get":
                    Fields(arguments, []);
                    var retained = currentStore.Load();
                    result = new { schemaVersion = 1, revision = retained.Document.Revision, sha256 = retained.Sha256,
                        retention = Retention(retained) };
                    break;
                case "personality.source.prepare":
                    Fields(arguments, ["schemaVersion", "expectedSettingsRevision", "expectedStoreRevision", "expectedStoreSha256"]);
                    RequireVersion(arguments, 1);
                    var preferences = ReadPreferences();
                    if (preferences.State.Revision != Integer(arguments, "expectedSettingsRevision", 0))
                        throw new InvalidOperationException("Personality preferences changed; reload before preparation.");
                    var source = PrepareSource(preferences.Preferences,
                        Integer(arguments, "expectedStoreRevision", 0), Text(arguments, "expectedStoreSha256", 64), token)
                        ?? throw new InvalidOperationException("The desired personality source is unavailable.");
                    var confirmed = ReadPreferences();
                    if (confirmed.State.Revision != preferences.State.Revision || confirmed.Digest != preferences.Digest)
                        throw new InvalidOperationException("Preferences changed during preparation; the unused source was retained. Reload; do not replay an uncertain save.");
                    token.ThrowIfCancellationRequested();
                    result = new { schemaVersion = 1, settingsRevision = confirmed.State.Revision,
                        preset = PresetReply(source, true), sources = SourceState(currentStore.Load()) };
                    break;
                case "personality.list":
                    Fields(arguments, []);
                    var list = currentStore.Load();
                    result = new { schemaVersion = 1,
                        presets = list.AllVersions.GroupBy(item => item.Id).Select(group =>
                            PresetReply(group.MaxBy(item => item.Revision)!, false)).ToArray(),
                        retention = Retention(list) };
                    break;
                case "personality.get":
                    Fields(arguments, ["id"], ["revision"]);
                    var id = Text(arguments, "id", 64);
                    var versions = currentStore.Load();
                    var revision = arguments.TryGetProperty("revision", out _) ? Integer(arguments, "revision", 1) : (long?)null;
                    var exact = revision is null ? versions.Latest(id) :
                        versions.AllVersions.SingleOrDefault(item => item.Id == id && item.Revision == revision)
                        ?? throw new InvalidDataException("The exact personality preset revision is unavailable.");
                    result = PresetReply(exact, true);
                    break;
                default: throw new ArgumentException("Unsupported personality owner operation.");
            }
            return Task.FromResult<object?>(result);
        }
    }

    internal (PluginSettingsState State, PersonalitySettingsPreferences Preferences, string Digest) ReadPreferences()
    {
        lock (gate)
        {
            RequireAdmissionAvailability();
            var state = RequireSettings().Get();
            if (state.Coordinate != new PluginSettingsCoordinate(Id, SettingsPreferenceContract.GlobalScope) ||
                state.Revision < 0 || state.Active is { } active && active.Coordinate != state.Coordinate) throw new InvalidDataException("Unexpected personality settings coordinate.");
            var values = state.Active is null
                ? new PersonalitySettingsPreferences(false, "cerebral", Array.Empty<PersonalityCustomDefinition>())
                : state.Active.SettingsVersion == PersonalitySettingsPolicy.SettingsVersion
                    ? PersonalitySettingsCodec.Read(state.Active.Values)
                    : throw new InvalidDataException("Personality active preferences require explicit schema admission.");
            var serialized = PersonalitySettingsCodec.Write(values);
            return (state, values, PersonalityContributionCodec.Hash(serialized));
        }
    }

    internal (PersonalityDocumentSnapshot Store, PersonalityPreset? Preset) ReadPrepared(PersonalitySettingsPreferences preferences)
    {
        lock (gate)
        {
            RequireAdmissionAvailability();
            var snapshot = store!.Load();
            return (snapshot, FindPrepared(preferences, snapshot));
        }
    }

    private static PersonalityPreset? FindPrepared(PersonalitySettingsPreferences preferences, PersonalityDocumentSnapshot sources)
    {
        var builtin = PersonalityContract.BuiltIns.SingleOrDefault(item => item.Id == preferences.SelectedPresetId);
        if (builtin is not null) return builtin;
        var current = preferences.CustomPresets.SingleOrDefault(item => item.Id == preferences.SelectedPresetId);
        return current is null ? null : sources.Document.Versions.Where(item => item.Id == current.Id &&
            item.Name == current.Name && item.Text == current.Text).MaxBy(item => item.Revision);
    }

    internal PersonalityPreset? PrepareSource(PersonalitySettingsPreferences preferences,
        long expectedRevision, string expectedHash, CancellationToken token, bool requireSource = true)
    {
        lock (gate)
        {
            RequireAdmissionAvailability();
            token.ThrowIfCancellationRequested();
            var sourceState = store!.Load();
            if (sourceState.Document.Revision != expectedRevision || sourceState.Sha256 != expectedHash)
                throw new InvalidOperationException("Personality sources changed; reload before preparation.");
            var source = FindPrepared(preferences, sourceState);
            if (source is not null) return source;
            var definition = preferences.CustomPresets.SingleOrDefault(item => item.Id == preferences.SelectedPresetId);
            if (definition is null)
            {
                // A disabled missing desired reference is a valid preference; never manufacture a source/default.
                if (requireSource) throw new InvalidOperationException("The desired personality definition is unavailable.");
                return null;
            }
            return store.Save(sourceState.Document.Revision, sourceState.Sha256,
                definition.Id, definition.Name, definition.Text, token).Preset;
        }
    }

    internal IScopedPluginSettingsTransfer RequireTransfer()
    {
        RequireAdmissionAvailability();
        return settings as IScopedPluginSettingsTransfer ??
            throw new NotSupportedException("The host does not expose purpose-aware personality settings transfer.");
    }

    private IScopedPluginSettings RequireSettings() => settings ??
        throw new NotSupportedException("The host does not expose central personality settings.");

    internal static object SourceState(PersonalityDocumentSnapshot state) =>
        new { revision = state.Document.Revision, sha256 = state.Sha256, retention = Retention(state) };
    private static object Retention(PersonalityDocumentSnapshot state) => new
    {
        retainedCustomVersions = state.Document.Versions.Length + state.PrivateVersions,
        maximumRetainedCustomVersions = PersonalityContract.MaximumRetainedVersions,
        customPresetCount = state.Document.Versions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() + state.PrivateIds,
        maximumCustomPresets = PersonalityContract.MaximumCustomPresets, automaticallyEvicts = false
    };
    internal static object PresetReply(PersonalityPreset preset, bool includeText) => new
    {
        preset.Id, preset.Name, preset.Revision, preset.Sha256, sourceId = PersonalityContract.SourceId(preset),
        readOnly = PersonalityContract.BuiltIns.Any(item => item.Id == preset.Id), text = includeText ? preset.Text : null,
        utf8Bytes = PersonalityContract.Utf8.GetByteCount(preset.Text)
    };
    internal PersonalityPreset Resolve(string sourceId, long? revision)
    {
        lock (gate)
        {
            RequireAdmissionAvailability();
            if (sourceId is null) throw new ArgumentException("An exact personality source is required.");
            return sourceId.StartsWith("personality/v2/", StringComparison.Ordinal)
                ? PersonalityChatContract.AsPreset(chats!.Resolve(sourceId, revision)) : store!.Resolve(sourceId, revision);
        }
    }
    internal (PersonalityChatState Selection, PersonalityEffectiveSource? Source) CaptureChat(string conversationId)
    {
        lock (gate) { RequireAdmissionAvailability(); return chats!.Capture(conversationId); }
    }
    internal static void ValidateArguments(JsonElement value, int maximumBytes = 8192)
    {
        if (value.ValueKind != JsonValueKind.Object || PersonalityContract.Utf8.GetByteCount(value.GetRawText()) > maximumBytes)
            throw new ArgumentException("Personality commands require a bounded JSON object.");
    }
    internal static void Fields(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Personality command object required.");
        var fields = value.EnumerateObject().Select(item => item.Name).ToArray();
        var names = fields.ToHashSet(StringComparer.Ordinal);
        if (fields.Length != names.Count || !required.All(names.Contains) ||
            !names.IsSubsetOf(required.Concat(optional ?? []).ToHashSet(StringComparer.Ordinal)))
            throw new ArgumentException("Personality command fields do not match its schema.");
    }
    internal static string Text(JsonElement value, string name, int maximum)
    {
        if (value.GetProperty(name).ValueKind != JsonValueKind.String ||
            value.GetProperty(name).GetString() is not { Length: > 0 } text ||
            text.Contains('\0') || PersonalityContract.Utf8.GetByteCount(text) > maximum)
            throw new ArgumentException("Invalid personality command string.");
        return text;
    }
    internal static long Integer(JsonElement value, string name, long minimum)
    {
        if (value.GetProperty(name).ValueKind != JsonValueKind.Number ||
            !value.GetProperty(name).TryGetInt64(out var result) || result < minimum)
            throw new ArgumentException("Invalid personality revision.");
        return result;
    }
    internal static void RequireVersion(JsonElement value, int version)
    { if (Integer(value, "schemaVersion", 1) != version) throw new NotSupportedException("Unsupported personality command version."); }
    internal void RequireAdmissionAvailability()
    {
        lock (gate) { if (disposed || store is null) throw new InvalidOperationException("The personality service is unavailable."); }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; store = null; chats = null; settings = null; }
    }
}

internal sealed class PersonalityContextProvider(PersonalityServicePlugin plugin) : IContextProviderCommandHandler
{
    private const string Summary = "Resolve one exact optional communication style source for an owner Cerebral conversation.";
    internal static ContextProviderDescriptor Description { get; } = new(
        PersonalityContract.ProviderId, PersonalityContract.ProviderVersion, Summary,
        Array.AsReadOnly<string>([PersonalityContract.ReferenceKind]), 1, PersonalityContract.MaximumUtf8Bytes);
    public ContextProviderDescriptor Provider => Description;
    public WorkflowCommandDescriptor Descriptor { get; } = new(
        PersonalityContract.ProviderId, PersonalityContract.ProviderVersion, Summary,
        WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, Array.Empty<string>(),
        WorkflowReplayPolicy.Idempotent, TimeoutSeconds: 5);

    public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // This check is defense in depth. The SPI is not an authorization channel and cannot select
        // the user-facing agent by role/name. Caller and parent lineage must be host-authored.
        if (context.Submission.DefinitionId != ConversationWorkflowContract.DefinitionId ||
            context.Submission.ParentRunId is not null ||
            context.Submission.Caller is not { Kind: EventActorKind.User, Id: "owner" })
            return Task.FromResult(Failed("scope-mismatch"));
        try
        {
            PersonalityServicePlugin.ValidateArguments(context.Arguments);
            PersonalityServicePlugin.Fields(context.Arguments,
                ["providerId", "providerVersion", "references", "maximumUtf8Bytes"],
                ["contextRevision", "exclusionRevision"]);
            var references = context.Arguments.GetProperty("references");
            if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() != 1)
                return Task.FromResult(Failed("invalid-request"));
            foreach (var item in references.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return Task.FromResult(Failed("invalid-reference"));
                PersonalityServicePlugin.Fields(item, ["id", "kind", "sourceId", "expectedRevision"],
                    ["requirement", "priority", "beforeCurrentInput"]);
            }
            var request = context.Arguments.Deserialize<ContextProviderRequest>(PersonalityContract.Json);
            if (request is null || request.ProviderId != Provider.Id || request.ProviderVersion != Provider.Version ||
                request.ContextRevision < 0 || request.ExclusionRevision < 0 ||
                request.MaximumUtf8Bytes is < 1 or > PersonalityContract.MaximumUtf8Bytes ||
                request.References is not { Count: 1 })
                return Task.FromResult(Failed("invalid-request"));
            var reference = request.References[0];
            if (reference is null || reference.Id != PersonalityContract.ContributionId ||
                reference.Kind != PersonalityContract.ReferenceKind ||
                reference.Requirement != ContextRequirement.Optional || reference.Priority != 0 ||
                !reference.BeforeCurrentInput)
                return Task.FromResult(Failed("invalid-reference"));
            var preset = plugin.Resolve(reference.SourceId, reference.ExpectedRevision);
            var resolution = PersonalityContract.Resolution(preset);
            var data = PersonalityContract.Element(resolution);
            if (PersonalityContract.Utf8.GetByteCount(data.GetRawText()) > request.MaximumUtf8Bytes)
                return Task.FromResult(Failed("requested-byte-bound"));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, data,
                ProviderReceiptId: context.EffectId));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            // No source text, paths, or serializer body excerpts are copied into effect diagnostics.
            return Task.FromResult(Failed("source-unavailable"));
        }
    }

    public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        => ExecuteAsync(context, cancellationToken);

    private static WorkflowCommandResult Failed(string kind) => new(WorkflowEffectState.Failed,
        PersonalityContract.Element(new { failureKind = kind }),
        Error: "The exact optional personality context could not be resolved. The host may omit it explicitly; no replacement style or permissions were supplied.");
}
