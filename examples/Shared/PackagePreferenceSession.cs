using System.IO;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.PackageSettings;

/// <summary>One package owner scope. Only acknowledged values reach runtime; a failed save requires an explicit read.</summary>
internal sealed class PackagePreferenceSession<T>(
    IDesktopPluginHost host, string pluginId, string packageVersion, IPluginSettingsPolicy policy,
    Func<JsonElement, T> decode, Func<T, JsonElement> encode, Func<T> defaults, int settingsVersion = 1) : IDisposable where T : class
{
    private readonly int selectedSettingsVersion = settingsVersion is >= 1 and <= 1_000_000
        ? settingsVersion : throw new ArgumentOutOfRangeException(nameof(settingsVersion));
    private readonly PackageSettingsOwnerClient? client = host is IDesktopServiceHost service
        ? new(service, new(pluginId, "global"), packageVersion) : null;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private PluginSettingsState? baseline;
    private T? committed;
    private PluginSettingsState? committedState;
    private long revision = -1;
    private readonly Dictionary<string, long> preparedComplete = new(StringComparer.Ordinal);
    private bool disposed;
    internal PluginSettingsState? State => baseline;
    internal T Current => !disposed && baseline is not null && committed is not null
        ? decode(encode(committed)) : throw new InvalidOperationException("Load saved preferences before recording.");
    internal async Task<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await gate.WaitAsync(timeout.Token);
        try
        {
            var state = await (client ?? throw new InvalidOperationException("Shared preferences are unavailable.")).ReadAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            var value = state.Active is { } active ? Admit(active, null, false) : defaults();
            if (state.Revision < revision || state.Revision == revision && committedState is not null &&
                !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(state), JsonSerializer.SerializeToElement(committedState)))
                throw new InvalidDataException("The preference revision is inconsistent.");
            if (state.Revision != revision) preparedComplete.Clear();
            baseline = state; committedState = state; revision = state.Revision; committed = value;
            return Current;
        }
        catch { baseline = null; throw; }
        finally { gate.Release(); }
    }
    internal T PreparePending(string id)
    {
        var state = baseline ?? throw new InvalidOperationException("Reload saved preferences first.");
        var pending = state.Pending.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Select a retained preference document.");
        var purpose = client!.PendingPurpose(state, id);
        if (purpose != SettingsTransferPurpose.Complete)
            return Admit(pending.Document, state.Active?.Values, purpose == SettingsTransferPurpose.Portable);
        if (policy is not IPluginCompleteSettingsPolicy complete) throw PackagePreferenceJson.Unsupported();
        var admitted = complete.AdmitComplete(pending.Document, state.Active?.Values);
        if (!admitted.Accepted || admitted.SettingsVersion != selectedSettingsVersion) throw PackagePreferenceJson.Unsupported();
        preparedComplete[id] = state.Revision;
        return decode(admitted.Values);
    }
    private T Admit(PluginSettingsDocument document, JsonElement? current, bool portable)
    {
        var admission = policy.Admit(document, current, portable);
        if (!admission.Accepted || admission.SettingsVersion != selectedSettingsVersion) throw PackagePreferenceJson.Unsupported();
        return decode(admission.Values);
    }
    internal async Task<T> SaveAsync(T draft, string? pendingId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await gate.WaitAsync(timeout.Token);
        try
        {
            var state = baseline ?? throw new InvalidOperationException("Reload saved preferences before saving.");
            var completeAdmitted = false;
            if (pendingId is not null && client!.PendingPurpose(state, pendingId) == SettingsTransferPurpose.Complete)
            {
                if (!preparedComplete.TryGetValue(pendingId, out var preparedRevision) || preparedRevision != state.Revision ||
                    policy is not IPluginCompleteSettingsPolicy complete)
                    throw new InvalidOperationException("Prepare the selected complete preferences again before saving.");
                var selected = state.Pending.Single(p => p.Id == pendingId);
                var admission = complete.AdmitComplete(selected.Document, state.Active?.Values);
                if (!admission.Accepted || admission.SettingsVersion != selectedSettingsVersion) throw PackagePreferenceJson.Unsupported();
                completeAdmitted = true;
            }
            var document = new PluginSettingsDocument(pluginId, "global", null, packageVersion, selectedSettingsVersion, encode(draft));
            var value = Admit(document, null, false);
            var projection = policy.ExportPortable(document);
            if (projection is null || projection.SettingsVersion != selectedSettingsVersion) throw PackagePreferenceJson.Unsupported();
            var completeProjection = client!.ProjectComplete(policy, document);
            var result = await client.SaveAsync(state, selectedSettingsVersion, encode(value), projection, pendingId, timeout.Token, completeProjection, completeAdmitted);
            timeout.Token.ThrowIfCancellationRequested();
            preparedComplete.Clear();
            baseline = result.State; committedState = result.State; committed = value; revision = result.State.Revision;
            return Current;
        }
        catch (PackageSettingsSaveUncertainException) { baseline = null; throw; }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; preparedComplete.Clear(); baseline = null; committed = null; committedState = null;
        lifetime.Cancel(); lifetime.Dispose();
    }
}
