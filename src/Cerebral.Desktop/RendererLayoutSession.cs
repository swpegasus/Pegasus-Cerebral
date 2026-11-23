using System.IO;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Captured owner transport for the existing opaque renderer layout contract.</summary>
internal sealed class RendererLayoutClient(string applicationId,
    Func<string, object?, CancellationToken, Task<CommandResult>> dispatch, Func<bool> isCurrent,
    string? backendIdentity = null)
{
    internal string ApplicationId { get; } = applicationId;
    internal bool IsCurrent => isCurrent();
    internal bool SameBackend(RendererLayoutClient other) => ReferenceEquals(this, other) ||
        backendIdentity is not null && other.BackendIdentity is not null &&
        string.Equals(backendIdentity, other.BackendIdentity, StringComparison.Ordinal);
    private string? BackendIdentity => backendIdentity;

    // Only authenticated, nonsecret endpoint/owner facts participate. A client object or a
    // changing pipe instance is not the durable identity of a known installation/data root.
    internal static string CaptureBackendIdentity(HostInfo info, string transport, string endpoint)
    {
        var local = transport is "pipe" or "installed-pipe" or "installed-user-pipe";
        var data = local ? Path.GetFullPath(info.DataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant() : info.DataDirectory;
        var address = local && !string.IsNullOrWhiteSpace(info.InstallationId) ? "local-owner" : local ? endpoint :
            new Uri(endpoint, UriKind.Absolute).GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        return JsonSerializer.Serialize(new[] { transport, address, data, info.InstallationId ?? "" });
    }

    internal async Task<InterfaceLayoutState> ReadAsync(CancellationToken token)
    {
        RequireCurrent();
        var result = await dispatch("interface.layout.get", new InterfaceLayoutGetRequest(ApplicationId), token);
        RequireCurrent();
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "Layout storage could not be read.");
        var state = CerebralJson.Read<InterfaceLayoutState>(result.Data);
        ValidateEnvelope(state);
        return state;
    }

    internal async Task<InterfaceLayoutSaveResult> SaveAsync(InterfaceLayoutSaveRequest request, CancellationToken token)
    {
        RequireCurrent();
        if (request.ApplicationId != ApplicationId || request.ExpectedRevision < 0 || request.SchemaVersion <= 0 ||
            request.Values.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The layout request did not match this application.");
        var result = await dispatch("interface.layout.save", request, token);
        RequireCurrent();
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "The layout save was not confirmed.");
        var saved = CerebralJson.Read<InterfaceLayoutSaveResult>(result.Data);
        ValidateEnvelope(saved.State);
        var expected = saved.Changed ? checked(request.ExpectedRevision + 1) : request.ExpectedRevision;
        if (saved.State.Revision != expected || saved.State.SchemaVersion != request.SchemaVersion ||
            saved.State.Values is null || !JsonElement.DeepEquals(saved.State.Values.Value, request.Values))
            throw new InvalidDataException("The layout acknowledgement did not match this save.");
        return saved;
    }

    private void ValidateEnvelope(InterfaceLayoutState state)
    {
        if (state.ApplicationId != ApplicationId || state.Revision < 0 ||
            (state.Revision == 0 && (state.SchemaVersion is not null || state.Values is not null)) ||
            (state.Revision > 0 && (state.SchemaVersion is null or <= 0 || state.Values?.ValueKind != JsonValueKind.Object)))
            throw new InvalidDataException("The layout state did not match this application.");
    }

    private void RequireCurrent()
    {
        if (!IsCurrent) throw new InvalidOperationException("The layout connection changed. Reload layout on the intended service.");
    }
}

/// <summary>One explicitly supported older format, validated and projected to the current renderer format for reads only.</summary>
internal sealed record RendererLayoutReadTransition(int StoredSchemaVersion, Func<JsonElement, JsonElement> ToCurrent);

/// <summary>One renderer's CAS state. Hydration never writes; a missing receipt never causes replay.</summary>
internal sealed class RendererLayoutSession(string applicationId, int schemaVersion, Action<JsonElement> validate,
    Func<JsonElement> capture, Func<JsonElement?, Task> apply, RendererLayoutReadTransition? readTransition = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly RendererLayoutReadTransition? transition = CheckTransition(schemaVersion, readTransition);
    private RendererLayoutClient? client;
    private InterfaceLayoutState? baseline;
    private InterfaceLayoutSaveRequest? uncertain;
    private long localVersion;
    internal bool Hydrating { get; private set; }
    internal bool Dirty { get; private set; }
    internal bool Blocked { get; private set; }
    internal bool Supported { get; private set; }
    internal string Status { get; private set; } = "Layout storage has not been loaded.";

    internal void Changed()
    {
        if (Hydrating) return;
        localVersion++; Dirty = true;
    }

    internal async Task LoadAsync(RendererLayoutClient owner, bool force, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (owner.ApplicationId != applicationId) throw new InvalidDataException("The layout owner belongs to another application.");
            if (ReferenceEquals(client, owner) && baseline is not null && !Blocked && !force) return;
            var version = localVersion;
            var differentBackend = client is not null && !owner.SameBackend(client);
            // A connection switch cannot discard a dirty draft or carry it to another backend.
            // Keep its original baseline so returning to that backend can reconcile and save it.
            if (differentBackend && Dirty && !force)
            {
                Blocked = true;
                Status = "Unsaved layout changes belong to the previous service. They are retained; return to that service to save them. The new service layout has not been changed.";
                return;
            }
            var previousBaseline = baseline;
            var state = await owner.ReadAsync(token);
            client = owner; baseline = state;
            Blocked = true; // Validation/application failure must never enable fallback writes.
            Supported = state.Revision == 0 || state.SchemaVersion == schemaVersion ||
                transition is not null && state.SchemaVersion == transition.StoredSchemaVersion;
            if (!Supported)
            {
                Blocked = true;
                Status = "The saved layout version is unsupported here. It is retained and will not be overwritten by fallback defaults.";
                return;
            }
            JsonElement? hydrateValues = state.Values;
            try
            {
                if (state.Values is { } values)
                {
                    // The opt-in callback must strictly validate the named stored format before
                    // projecting it. The current validator checks the projection separately.
                    hydrateValues = state.SchemaVersion == schemaVersion ? values : transition!.ToCurrent(values);
                    if (hydrateValues.Value.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("The layout read transition returned an invalid object.");
                    validate(hydrateValues.Value);
                }
            }
            catch (Exception)
            {
                Supported = false; Blocked = true;
                Status = "The saved layout is invalid for this renderer. It is retained and will not be overwritten by fallback defaults.";
                throw;
            }
            var confirmsUncertain = uncertain is { } submitted && state.Revision == submitted.ExpectedRevision + 1 &&
                EqualValues(state.Values, submitted.Values);
            if (!differentBackend && Dirty && !force && previousBaseline is not null && !confirmsUncertain &&
                (state.Revision != previousBaseline.Revision || state.SchemaVersion != previousBaseline.SchemaVersion ||
                 !EqualValues(state.Values, previousBaseline.Values)) && !EqualValues(state.Values, capture()))
            {
                baseline = previousBaseline; // Further reconnects cannot silently adopt the external writer as this draft's baseline.
                Status = "The saved layout changed while this draft was open. The unsaved layout is retained; no automatic overwrite was sent.";
                return;
            }
            if ((force || !Dirty) && localVersion == version)
            {
                Hydrating = true;
                try { await apply(hydrateValues); }
                finally { Hydrating = false; }
                Dirty = false;
            }
            else if (state.Values is { } current && JsonElement.DeepEquals(current, capture())) Dirty = false;
            Blocked = false;
            Status = state.Revision == 0 ? "No central layout is saved yet." : $"Central layout loaded at revision {state.Revision}.";
            // A read confirms current state only. It never replays the old mutation.
            if (differentBackend || (uncertain is not null && state.Values is { } stored && JsonElement.DeepEquals(stored, uncertain.Values)))
                uncertain = null;
        }
        finally { gate.Release(); }
    }

    private static bool EqualValues(JsonElement? left, JsonElement? right) =>
        left is null ? right is null : right is { } value && JsonElement.DeepEquals(left.Value, value);

    private static RendererLayoutReadTransition? CheckTransition(int currentVersion, RendererLayoutReadTransition? requested)
    {
        if (requested is not null && (requested.StoredSchemaVersion <= 0 || requested.StoredSchemaVersion >= currentVersion ||
                                      requested.ToCurrent is null))
            throw new ArgumentException("The optional layout read transition must name one older schema and a validator/projection.", nameof(requested));
        return requested;
    }

    internal async Task FlushAsync(RendererLayoutClient owner, bool captureCurrent, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!ReferenceEquals(owner, client) || baseline is null || !owner.IsCurrent)
                throw new InvalidOperationException("Load layout on the intended service before saving it.");
            if (!Supported || Blocked) throw new InvalidOperationException("The layout is not ready to save. Reload its stored state.");
            // An older read is not a save request. Even a caller's capture-current flush must
            // wait for a real changed draft before it can advance the stored schema.
            if (!Dirty && (!captureCurrent || baseline.Revision > 0 && baseline.SchemaVersion != schemaVersion)) return;
            var values = capture(); validate(values);
            if (baseline.Values is { } existing && JsonElement.DeepEquals(existing, values)) { Dirty = false; return; }
            var request = new InterfaceLayoutSaveRequest(applicationId, baseline.Revision, schemaVersion, values);
            if (uncertain is { } previous && previous.ExpectedRevision == request.ExpectedRevision &&
                JsonElement.DeepEquals(previous.Values, request.Values))
                throw new InvalidOperationException("The unconfirmed layout save will not be repeated. Read current state before making a new layout change.");
            var version = localVersion;
            try
            {
                var result = await owner.SaveAsync(request, token);
                baseline = result.State;
                if (localVersion == version) Dirty = false;
                uncertain = null;
                Status = $"Layout saved at revision {result.State.Revision}.";
            }
            catch (Exception)
            {
                uncertain = request; Blocked = true;
                Status = "Layout save was not confirmed. Window changes are retained; reload on the original service before another save.";
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
