using System.Text.Json;
using Cerebral.PersonalityPlugin;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>Bounded package-owned user data, outside portable/complete settings. No automatic eviction.</summary>
internal sealed class PersonalityChatStore
{
    internal const string FileName = "personality-chat-state.v1.json";
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    internal const int MaximumChats = 2048;
    internal const int MaximumEffectiveSources = 512;
    private readonly string directory;
    private readonly string path;
    private readonly string lockPath;
    private readonly PersonalityStore legacy;

    internal PersonalityChatStore(string directory, PersonalityStore legacy)
    {
        this.directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        this.legacy = legacy;
        path = Path.Combine(this.directory, FileName);
        lockPath = Path.Combine(this.directory, "personality-chat-state.lock");
        using var lease = Acquire(create: true);
        _ = Read();
    }

    // Read atomically published private counts without the chat lock while the legacy source lease
    // is held. Import takes chat -> legacy; the reverse path must never take the chat lease.
    internal (int Ids, int Versions) PrivateRetention()
    {
        var state = Read();
        return (state.PrivateDefinitions.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count(), state.PrivateDefinitions.Length);
    }

    internal PersonalityChatState Selection(string conversationId)
    {
        PersonalityChatContract.ValidateConversationId(conversationId);
        using var lease = Acquire();
        return FindChat(Read(), conversationId);
    }

    internal (PersonalityChatState Selection, PersonalityEffectiveSource? Source) Capture(string conversationId)
    {
        PersonalityChatContract.ValidateConversationId(conversationId);
        using var lease = Acquire();
        var state = Read();
        var selection = FindChat(state, conversationId);
        return (selection, selection.Enabled ? state.Sources.SingleOrDefault(s => s.Pin == selection.EffectiveSource) : null);
    }

    internal PersonalityEffectiveSource Resolve(string sourceId, long? revision)
    {
        using var lease = Acquire();
        var source = Read().Sources.SingleOrDefault(s => s.SourceId == sourceId);
        if (source is null || revision != 1) throw new InvalidDataException("The exact effective personality source is unavailable.");
        PersonalityChatContract.ValidateSource(source);
        return source;
    }

    internal PersonalityChatView Get(string conversationId, Func<PersonalitySettingsPreferences> preferences)
    {
        PersonalityChatContract.ValidateConversationId(conversationId);
        using var lease = Acquire();
        var state = Read();
        return View(state, FindChat(state, conversationId), preferences);
    }

    internal JsonElement PrivateMetadata()
    {
        using var lease = Acquire();
        var state = Read();
        return PersonalityChatContract.Reply(new { schemaVersion = 1, libraryRevision = state.LibraryRevision,
            entries = state.PrivateDefinitions.GroupBy(d => d.Id).Select(g => g.MaxBy(d => d.Revision)!).Select(d => new
            { entry = PrivateEntry(d), provenance = d.Provenance }).ToArray() });
    }

    internal PersonalityChatSaveReply Save(JsonElement arguments, Func<PersonalitySettingsPreferences> preferences,
        CancellationToken token)
    {
        // Syntax errors are transport refusals before effects. Valid mutation replies use the frozen
        // committed/unchanged/rejected/uncertain union; transport loss after issue is always uncertain.
        PersonalityChatContract.Version(arguments);
        var id = PersonalityChatContract.ConversationId(arguments);
        var enabled = PersonalityChatContract.Boolean(arguments, "enabled");
        PersonalityServicePlugin.Fields(arguments, enabled
            ? ["schemaVersion", "conversationId", "expectedRevision", "enabled", "baseSelection", "bluntMode", "catalogToken"]
            : ["schemaVersion", "conversationId", "expectedRevision", "enabled", "bluntMode"]);
        var expected = PersonalityServicePlugin.Integer(arguments, "expectedRevision", 0);
        var blunt = PersonalityChatContract.Boolean(arguments, "bluntMode");
        if (!enabled && blunt) throw new ArgumentException("No personality must disable its modifier.");
        PersonalityDefinitionPin? requested = null;
        long? requestedRevision = null;
        string? catalogToken = null;
        if (enabled)
        {
            var selection = arguments.GetProperty("baseSelection");
            PersonalityServicePlugin.Fields(selection, ["kind", "id", "revision", "sha256"]);
            var kind = PersonalityServicePlugin.Text(selection, "kind", 16);
            var key = PersonalityServicePlugin.Text(selection, "id", 128);
            requestedRevision = selection.GetProperty("revision").ValueKind == JsonValueKind.Null ? null
                : PersonalityServicePlugin.Integer(selection, "revision", 1);
            requested = new(kind, key, requestedRevision ?? 1, PersonalityChatContract.Hash(selection, "sha256"));
            PersonalityChatContract.ValidatePin(requested);
            if (requestedRevision is null && kind != "custom") throw new ArgumentException("Only current custom definitions can be unprepared.");
            catalogToken = PersonalityChatContract.Hash(arguments, "catalogToken");
        }
        var publicationAttempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            using var lease = Acquire();
            var state = Read();
            var current = FindChat(state, id);
            if (current.Revision != expected) throw new PersonalityChatFailure("chat-revision-conflict");
            PersonalityChatState next;
            var sourceAdded = false;
            if (!enabled)
                next = current with { Enabled = false, BluntMode = false, BluntPin = null, EffectiveSource = null };
            else
            {
                var catalog = RequiredCatalog(state, preferences);
                if (catalog.Token != catalogToken) throw new PersonalityChatFailure("catalog-changed");
                var row = catalog.Entries.SingleOrDefault(e => e.Kind == requested!.Kind && e.Id == requested.Id &&
                    (e.Revision == requestedRevision || requestedRevision is null && e.Kind == "custom") && e.Sha256 == requested.Sha256);
                // The current saved pin can outlive a public definition update. Changing only its
                // modifier must not silently retarget it to today's body, or admit an arbitrary
                // historical/foreign definition. Its immutable archived base is the only exception.
                var retainedBase = requestedRevision is not null && requested == current.BaseSelection
                    ? state.Sources.FirstOrDefault(s => s.BaseSelection == requested) : null;
                if (row is { Availability: "unavailable" } || row is null && retainedBase is null)
                    throw new PersonalityChatFailure("selection-unavailable");
                var prepared = retainedBase is null ? Prepare(state, requested!, requestedRevision, preferences, token)
                    : (Pin: requested!, Text: retainedBase.BaseText);
                var modifier = blunt ? PersonalityChatContract.BluntPin : null;
                var text = prepared.Text + (blunt ? "\n" + PersonalityTemplates.BluntModeText : string.Empty);
                var effective = new PersonalityEffectiveSource(prepared.Pin, modifier, prepared.Text,
                    blunt ? PersonalityTemplates.BluntModeText : null,
                    PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(text)));
                PersonalityChatContract.ValidateSource(effective);
                // The same qualified revision cannot acquire a different body after a package update.
                if (state.Sources.Any(s => s.BaseSelection.Kind == prepared.Pin.Kind && s.BaseSelection.Id == prepared.Pin.Id &&
                    s.BaseSelection.Revision == prepared.Pin.Revision && s.BaseSelection.Sha256 != prepared.Pin.Sha256))
                    throw new PersonalityChatFailure("selection-unavailable");
                var afterCatalog = RequiredCatalog(state, preferences);
                var afterEntries = afterCatalog.Entries.Select(e => row is { Revision: null } && e.Kind == "custom" &&
                    e.Id == requested!.Id && e.Revision == prepared.Pin.Revision && e.Sha256 == requested.Sha256
                        ? e with { Revision = null, Availability = "unprepared" } : e).ToArray();
                if (!afterEntries.SequenceEqual(catalog.Entries)) throw new PersonalityChatFailure("catalog-changed");
                if (!state.Sources.Any(s => s.SourceId == effective.SourceId))
                {
                    if (state.Sources.Length >= MaximumEffectiveSources) throw new PersonalityChatFailure("source-capacity");
                    state = state with { Sources = [.. state.Sources, effective] };
                    sourceAdded = true;
                }
                next = current with { Enabled = true, BaseSelection = prepared.Pin, BluntMode = blunt,
                    BluntPin = modifier, EffectiveSource = effective.Pin };
            }
            if (next == current && !sourceAdded)
                return new(1, id, "unchanged", false, View(state, current, preferences), null);
            if (current.Revision == 0 && state.Chats.Length >= MaximumChats) throw new PersonalityChatFailure("source-capacity");
            next = next with { Revision = checked(current.Revision + 1) };
            state = state with { Chats = [.. state.Chats.Where(c => c.ConversationId != id), next] };
            Publish(state, token, () => publicationAttempted = true);
            // No cancellation check after publication: a cancellation cannot honestly mean no commit.
            var persisted = Read();
            if (FindChat(persisted, id) != next) throw new IOException("Chat readback differs from the published selection.");
            return new(1, id, "committed", true, View(persisted, next, preferences), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or
            InvalidDataException or InvalidOperationException or OperationCanceledException or OverflowException or KeyNotFoundException or NotSupportedException)
        {
            var code = publicationAttempted ? "commit-unconfirmed" : error switch
            {
                PersonalityChatFailure failure => failure.Code,
                OperationCanceledException => "cancelled-before-commit",
                _ => "selection-unavailable"
            };
            return new(1, id, publicationAttempted ? "uncertain" : "rejected", publicationAttempted ? null : false,
                null, PersonalityChatContract.Diagnostic(code));
        }
    }

    internal JsonElement Import(JsonElement arguments, CancellationToken token)
    {
        PersonalityServicePlugin.Fields(arguments, ["schemaVersion", "expectedLibraryRevision", "definition", "provenance"]);
        PersonalityChatContract.Version(arguments);
        var expected = PersonalityServicePlugin.Integer(arguments, "expectedLibraryRevision", 0);
        var definition = PersonalitySettingsCodec.ReadDefinition(arguments.GetProperty("definition"));
        var provenanceValue = arguments.GetProperty("provenance");
        PersonalityServicePlugin.Fields(provenanceValue, ["namespace", "recordType", "recordId", "revision", "sha256"]);
        var provenance = new PersonalityPrivateProvenance(PersonalitySettingsCodec.Text(provenanceValue.GetProperty("namespace")),
            PersonalitySettingsCodec.Text(provenanceValue.GetProperty("recordType")),
            PersonalitySettingsCodec.Text(provenanceValue.GetProperty("recordId")),
            PersonalityServicePlugin.Integer(provenanceValue, "revision", 1), PersonalityChatContract.Hash(provenanceValue, "sha256"));
        ValidateProvenance(provenance);
        var attempted = false;
        try
        {
            using var lease = Acquire();
            var state = Read();
            if (state.LibraryRevision != expected) throw new PersonalityChatFailure("catalog-changed");
            var previous = state.PrivateDefinitions.Where(d => d.Id == definition.Id).MaxBy(d => d.Revision);
            if (previous is not null && previous.Name == definition.Name && previous.Text == definition.Text && previous.Provenance == provenance)
                return PersonalityChatContract.Reply(new { schemaVersion = 1, outcome = "unchanged", changed = (bool?)false,
                    libraryRevision = state.LibraryRevision, entry = PrivateEntry(previous), diagnostic = (PersonalityDiagnostic?)null });
            var item = new PersonalityPrivateDefinition(definition.Id, definition.Name, checked((previous?.Revision ?? 0) + 1),
                definition.Text, PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(definition.Text)), provenance);
            // Private identities and retained versions consume the original 8 / 32 custom capacity.
            // A shared legacy lease makes the quota check atomic with cooperating legacy source saves.
            return legacy.WithRetention(snapshot =>
            {
                var privateIds = state.PrivateDefinitions.Select(d => d.Id).Append(item.Id).Distinct(StringComparer.Ordinal).Count();
                if (privateIds + snapshot.Document.Versions.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() > PersonalityContract.MaximumCustomPresets ||
                    state.PrivateDefinitions.Length + 1 + snapshot.Document.Versions.Length > PersonalityContract.MaximumRetainedVersions)
                    throw new PersonalityChatFailure("source-capacity");
                var next = state with { LibraryRevision = checked(state.LibraryRevision + 1),
                    PrivateDefinitions = [.. state.PrivateDefinitions, item] };
                Publish(next, token, () => attempted = true);
                var readback = Read();
                if (readback.LibraryRevision != next.LibraryRevision || !readback.PrivateDefinitions.Contains(item))
                    throw new IOException("Private import readback could not be confirmed.");
                return PersonalityChatContract.Reply(new { schemaVersion = 1, outcome = "committed", changed = (bool?)true,
                    libraryRevision = readback.LibraryRevision, entry = PrivateEntry(item), diagnostic = (PersonalityDiagnostic?)null });
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or
            InvalidDataException or InvalidOperationException or OperationCanceledException or OverflowException or KeyNotFoundException or NotSupportedException)
        {
            var code = attempted ? "commit-unconfirmed" : error is PersonalityChatFailure failure ? failure.Code :
                error is OperationCanceledException ? "cancelled-before-commit" : "selection-unavailable";
            return PersonalityChatContract.Reply(new { schemaVersion = 1, outcome = attempted ? "uncertain" : "rejected",
                changed = attempted ? (bool?)null : false, libraryRevision = (long?)null,
                entry = (object?)null, diagnostic = PersonalityChatContract.Diagnostic(code) });
        }
    }

    private static PersonalityCatalogEntry PrivateEntry(PersonalityPrivateDefinition d) =>
        new("private", d.Id, d.Revision, d.Sha256, d.Name, "ready", null);

    private (PersonalityDefinitionPin Pin, string Text) Prepare(PersonalityChatDocument state,
        PersonalityDefinitionPin requested, long? revision, Func<PersonalitySettingsPreferences> preferences, CancellationToken token)
    {
        if (requested.Kind == "packaged")
        {
            var match = PersonalityChatContract.Packaged().SingleOrDefault(p => p.Pin == requested);
            if (match.Pin is null) throw new PersonalityChatFailure("selection-unavailable");
            return (match.Pin, match.Text);
        }
        if (requested.Kind == "private")
        {
            var match = state.PrivateDefinitions.SingleOrDefault(d => d.Id == requested.Id && d.Revision == requested.Revision && d.Sha256 == requested.Sha256);
            return match is null ? throw new PersonalityChatFailure("selection-unavailable") : (requested, match.Text);
        }
        var definition = preferences().CustomPresets.SingleOrDefault(d => d.Id == requested.Id &&
            PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(d.Text)) == requested.Sha256)
            ?? throw new PersonalityChatFailure("selection-unavailable");
        var sources = legacy.Load();
        var source = sources.Document.Versions.Where(d => d.Id == definition.Id && d.Name == definition.Name && d.Text == definition.Text).MaxBy(d => d.Revision);
        if (revision is not null && source?.Revision != revision) throw new PersonalityChatFailure("selection-unavailable");
        if (source is null)
        {
            if (sources.Document.Versions.Length + state.PrivateDefinitions.Length >= PersonalityContract.MaximumRetainedVersions ||
                !sources.Document.Versions.Any(d => d.Id == definition.Id) &&
                sources.Document.Versions.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() +
                state.PrivateDefinitions.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() >= PersonalityContract.MaximumCustomPresets)
                throw new PersonalityChatFailure("source-capacity");
            source = legacy.Save(sources.Document.Revision, sources.Sha256, definition.Id, definition.Name, definition.Text, token).Preset;
        }
        return (new("custom", source.Id, source.Revision, source.Sha256), source.Text);
    }

    private (string Token, PersonalityCatalogEntry[] Entries) Catalog(PersonalityChatDocument state, PersonalitySettingsPreferences preferences)
    {
        var entries = new List<PersonalityCatalogEntry>();
        entries.AddRange(PersonalityChatContract.Packaged().Select(p => new PersonalityCatalogEntry("packaged", p.Pin.Id,
            p.Pin.Revision, p.Pin.Sha256, p.Name, "ready", null)));
        var sources = legacy.Load();
        foreach (var definition in preferences.CustomPresets)
        {
            var source = sources.Document.Versions.Where(p => p.Id == definition.Id && p.Name == definition.Name && p.Text == definition.Text).MaxBy(p => p.Revision);
            entries.Add(new("custom", definition.Id, source?.Revision,
                PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(definition.Text)), definition.Name,
                source is null ? "unprepared" : "ready", null));
        }
        entries.AddRange(state.PrivateDefinitions.GroupBy(d => d.Id).Select(g => PrivateEntry(g.MaxBy(d => d.Revision)!)));
        if (entries.Count > 32) throw new InvalidDataException("The personality catalog exceeds its bound.");
        // Chat mappings, global enable/selection and unrelated settings revisions are not catalog inputs.
        // Preparing identical current custom text is not a library edit. Another chat may prepare
        // it without invalidating this chat's catalog token or null-revision selection request.
        var identities = entries.Select(e => new { e.Kind, e.Id,
            revision = e.Kind == "custom" ? (long?)null : e.Revision, e.Sha256, e.Name }).ToArray();
        var token = PersonalityContributionCodec.Hash(PersonalityContract.Element(new
        { entries = identities, bluntPin = PersonalityChatContract.BluntPin }));
        return (token, entries.ToArray());
    }

    private (string Token, PersonalityCatalogEntry[] Entries) RequiredCatalog(PersonalityChatDocument state,
        Func<PersonalitySettingsPreferences> preferences)
    {
        try { return Catalog(state, preferences()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
            ArgumentException or JsonException or NotSupportedException) { throw new PersonalityChatFailure("catalog-unavailable"); }
    }

    private PersonalityChatView View(PersonalityChatDocument document, PersonalityChatState current,
        Func<PersonalitySettingsPreferences> preferences)
    {
        string? token = null;
        PersonalityCatalogEntry[] entries = [];
        PersonalityDiagnostic? diagnostic = null;
        try { var catalog = Catalog(document, preferences()); token = catalog.Token; entries = catalog.Entries; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
            ArgumentException or JsonException or NotSupportedException) { diagnostic = PersonalityChatContract.Diagnostic("catalog-unavailable"); }
        var available = !current.Enabled ? "none" : document.Sources.Any(s => s.Pin == current.EffectiveSource) ? "ready" : "unavailable";
        if (available == "unavailable") diagnostic = PersonalityChatContract.Diagnostic("selection-unavailable");
        return new(1, current.ConversationId, current.Revision, current.Enabled, current.BaseSelection, current.BluntMode,
            current.BluntPin, available, diagnostic, current.Enabled ? current.EffectiveSource : null, token, entries);
    }

    private static PersonalityChatState FindChat(PersonalityChatDocument state, string id) =>
        state.Chats.SingleOrDefault(c => c.ConversationId == id) ?? PersonalityChatContract.Absent(id);

    private PersonalityChatDocument Read()
    {
        Guard(directory); Guard(path);
        if (Directory.Exists(path)) throw new IOException("Personality chat storage must be a regular file.");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is < 1 or > MaximumFileBytes) throw new InvalidDataException("Personality chat storage exceeds its bound.");
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Personality chat storage changed during read.");
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            Shape(parsed.RootElement);
            var result = parsed.RootElement.Deserialize<PersonalityChatDocument>(PersonalityContract.Json)
                ?? throw new InvalidDataException("Invalid personality chat storage.");
            Validate(result);
            return result;
        }
        catch (FileNotFoundException) { return new(1, 0, [], [], []); }
    }

    private void Publish(PersonalityChatDocument next, CancellationToken token, Action publicationAttempt)
    {
        Validate(next);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, PersonalityContract.Json);
        if (bytes.Length > MaximumFileBytes) throw new PersonalityChatFailure("source-capacity");
        token.ThrowIfCancellationRequested();
        Guard(directory); Guard(path);
        var temporary = Path.Combine(directory, "." + FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested(); Guard(directory); Guard(path);
            publicationAttempt();
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private FileStream Acquire(bool create = false)
    {
        Guard(directory); Guard(lockPath);
        return new FileStream(lockPath, create ? FileMode.OpenOrCreate : FileMode.Open,
            create ? FileAccess.ReadWrite : FileAccess.Read, FileShare.None);
    }
    private static void Guard(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Personality data cannot traverse links."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private static void ValidateProvenance(PersonalityPrivateProvenance p)
    {
        if (p is null || p.Namespace != "knowledge.user" || p.RecordType != "knowledge" || p.Revision < 1 ||
            p.RecordId is not { Length: > 0 and <= 256 } || PersonalityContract.Utf8.GetByteCount(p.RecordId) > 256 || p.RecordId.Any(char.IsControl))
            throw new InvalidDataException("Private preset provenance requires a bounded user-owned source coordinate.");
        PersonalityAdmissionEngine.RequireHash(p.Sha256);
    }
    private static void Validate(PersonalityChatDocument d)
    {
        if (d.SchemaVersion != 1 || d.LibraryRevision < 0 || d.PrivateDefinitions is null || d.Sources is null || d.Chats is null ||
            d.LibraryRevision != d.PrivateDefinitions.LongLength ||
            d.PrivateDefinitions.Any(p => p is null) || d.Sources.Any(s => s is null) || d.Chats.Any(c => c is null) ||
            d.PrivateDefinitions.Length > PersonalityContract.MaximumRetainedVersions ||
            d.PrivateDefinitions.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() > PersonalityContract.MaximumCustomPresets ||
            d.Sources.Length > MaximumEffectiveSources || d.Chats.Length > MaximumChats ||
            d.Sources.Select(s => s.SourceId).Distinct(StringComparer.Ordinal).Count() != d.Sources.Length ||
            d.Chats.Select(c => c.ConversationId).Distinct(StringComparer.Ordinal).Count() != d.Chats.Length)
            throw new InvalidDataException("Personality chat storage schema or bounds are invalid.");
        foreach (var p in d.PrivateDefinitions)
        {
            PersonalitySettingsCodec.ValidateDefinition(new(p.Id, p.Name, p.Text));
            if (p.Revision < 1 || p.Sha256 != PersonalityContract.Digest(PersonalityContract.Utf8.GetBytes(p.Text)))
                throw new InvalidDataException("Invalid private definition pin.");
            ValidateProvenance(p.Provenance);
        }
        foreach (var group in d.PrivateDefinitions.GroupBy(p => p.Id))
            if (!group.OrderBy(p => p.Revision).Select(p => p.Revision).SequenceEqual(Enumerable.Range(1, group.Count()).Select(n => (long)n)))
                throw new InvalidDataException("Private preset history must have unique consecutive revisions.");
        foreach (var source in d.Sources) PersonalityChatContract.ValidateSource(source);
        if (d.Sources.GroupBy(s => (s.BaseSelection.Kind, s.BaseSelection.Id, s.BaseSelection.Revision))
            .Any(g => g.Select(s => s.BaseSelection.Sha256).Distinct(StringComparer.Ordinal).Count() != 1))
            throw new InvalidDataException("A qualified definition revision cannot change content.");
        foreach (var c in d.Chats)
        {
            PersonalityChatContract.ValidateConversationId(c.ConversationId);
            if (c.Revision < 1 || c.Enabled && (c.BaseSelection is null || c.EffectiveSource is null) ||
                !c.Enabled && (c.BluntMode || c.BluntPin is not null || c.EffectiveSource is not null) ||
                c.BluntMode != (c.BluntPin is not null)) throw new InvalidDataException("Invalid chat personality selection.");
            if (c.BaseSelection is not null) PersonalityChatContract.ValidatePin(c.BaseSelection);
            if (c.BluntPin is not null) PersonalityChatContract.ValidateModifier(c.BluntPin);
            if (c.EffectiveSource is not null)
            {
                PersonalityChatContract.ValidateEffectivePin(c.EffectiveSource);
                var source = d.Sources.SingleOrDefault(s => s.Pin == c.EffectiveSource);
                if (source is not null && (source.BaseSelection != c.BaseSelection || source.BluntPin != c.BluntPin))
                    throw new InvalidDataException("Chat and effective source pins differ.");
            }
        }
    }
    private static void Shape(JsonElement root)
    {
        PersonalityServicePlugin.Fields(root, ["schemaVersion", "libraryRevision", "privateDefinitions", "sources", "chats"]);
        if (root.GetProperty("privateDefinitions").ValueKind != JsonValueKind.Array || root.GetProperty("sources").ValueKind != JsonValueKind.Array ||
            root.GetProperty("chats").ValueKind != JsonValueKind.Array ||
            root.GetProperty("privateDefinitions").GetArrayLength() > PersonalityContract.MaximumRetainedVersions ||
            root.GetProperty("sources").GetArrayLength() > MaximumEffectiveSources || root.GetProperty("chats").GetArrayLength() > MaximumChats)
            throw new InvalidDataException("Personality chat array bounds are invalid.");
        ValidStrings(root);
        foreach (var p in root.GetProperty("privateDefinitions").EnumerateArray())
        {
            PersonalityServicePlugin.Fields(p, ["id", "name", "revision", "text", "sha256", "provenance"]);
            PersonalityServicePlugin.Fields(p.GetProperty("provenance"), ["namespace", "recordType", "recordId", "revision", "sha256"]);
        }
        foreach (var s in root.GetProperty("sources").EnumerateArray())
        {
            PersonalityServicePlugin.Fields(s, ["baseSelection", "bluntPin", "baseText", "modifierText", "sha256"]);
            Pins(s);
        }
        foreach (var c in root.GetProperty("chats").EnumerateArray())
        {
            PersonalityServicePlugin.Fields(c, ["conversationId", "revision", "enabled", "baseSelection", "bluntMode", "bluntPin", "effectiveSource"]);
            Pins(c);
            if (c.GetProperty("effectiveSource").ValueKind != JsonValueKind.Null)
                PersonalityServicePlugin.Fields(c.GetProperty("effectiveSource"), ["sourceId", "revision", "sha256"]);
        }
        static void Pins(JsonElement value)
        {
            if (value.GetProperty("baseSelection").ValueKind != JsonValueKind.Null)
                PersonalityServicePlugin.Fields(value.GetProperty("baseSelection"), ["kind", "id", "revision", "sha256"]);
            if (value.GetProperty("bluntPin").ValueKind != JsonValueKind.Null)
                PersonalityServicePlugin.Fields(value.GetProperty("bluntPin"), ["id", "revision", "sha256"]);
        }
        static void ValidStrings(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String) _ = PersonalitySettingsCodec.Text(value);
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var p in value.EnumerateObject()) ValidStrings(p.Value);
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) ValidStrings(item);
        }
    }
}
