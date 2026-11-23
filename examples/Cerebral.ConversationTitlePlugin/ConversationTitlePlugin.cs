using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.ConversationTitlePlugin;

public sealed class ConversationTitlePlugin : IServicePlugin, IWorkflowModule, IPluginSettingsPolicy,
    IServiceCommittedRootUserInputObserver, IServiceOwnSettingsRefresh
{
    public const string PackageId = TitlePackageIdentity.PackageId;
    public const string PackageVersion = TitlePackageIdentity.PackageVersion;
    public const string DefinitionId = TitlePackageIdentity.DefinitionId;
    private readonly TitleSettingsPolicy policy = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim refresh = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private IServicePluginHost? owner;
    private IServiceSourceBoundBackgroundHost? background;
    private IScopedPluginSettings? settings;
    private OwnPreferences? snapshot;
    private bool initialized, disposed;
    private sealed record OwnPreferences(TitlePreferences Values, long Revision);
    public string Id => PackageId;
    public IReadOnlyList<IToolExtension> ToolExtensions { get; } = [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions { get; } = [];
    public IReadOnlyList<IWorkflowCommandHandler> Commands { get; } = [];
    public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new SourceBoundConversationTitleWorkflow()];

    public void Initialize(IServicePluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.PluginId != PackageId || host is not IServiceSourceBoundBackgroundHost sourceHost ||
            host is not IServicePluginSettingsHost settingsHost)
            throw new InvalidOperationException("Conversation titles require the matching source-bound workflow and own-settings host.");
        var ownSettings = settingsHost.Settings;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized) throw new InvalidOperationException("Conversation title package is already initialized.");
            initialized = true; owner = host; background = sourceHost; settings = ownSettings;
        }
        // This reads only central own-settings and publishes metadata. No model or
        // workflow runs during initialization, including for enabled saved intent.
        RefreshOwnSettingsAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask RefreshOwnSettingsAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await refresh.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            IServiceSourceBoundBackgroundHost host; IScopedPluginSettings own;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                host = background ?? throw new InvalidOperationException("Title host is unavailable.");
                own = settings ?? throw new InvalidOperationException("Title own-settings are unavailable.");
            }
            var state = own.Get();
            if (state.Coordinate != new PluginSettingsCoordinate(PackageId, SettingsPreferenceContract.GlobalScope) || state.Revision < 0)
                throw new InvalidDataException("Title own-settings coordinate is invalid.");
            var values = TitlePreferences.Default;
            if (state.Active is { } active)
            {
                var admitted = policy.Admit(active, active.Values, false);
                if (!admitted.Accepted || admitted.SettingsVersion != TitleSettingsPolicy.SettingsVersion)
                    throw new InvalidDataException("Title saved settings require explicit repair.");
                values = TitlePreferences.Read(admitted.Values);
            }
            var enabled = state.Revision > 0 && values.Enabled && values.SourceProfileIds.Count > 0 &&
                values.ProfileId is not null && values.ProfileRevision is not null;
            linked.Token.ThrowIfCancellationRequested();
            // No own monitor is held across host/package/StateStore admission.
            host.SetSourceBoundSubscription(enabled, enabled ? values.SourceProfileIds : [], state.Revision);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                snapshot = new(values, state.Revision);
            }
        }
        catch
        {
            lock (gate) snapshot = null;
            // Core owns fail-closed refresh failure: it revokes the stale registration
            // and reports separately without falsifying an already committed save.
            throw;
        }
        finally { refresh.Release(); }
    }

    public async ValueTask ObserveCommittedRootUserInputAsync(CommittedRootUserInputObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        OwnPreferences? captured; IServiceSourceBoundBackgroundHost host;
        await refresh.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                captured = snapshot; host = background ?? throw new InvalidOperationException("Title host is unavailable.");
            }
        }
        finally { refresh.Release(); }
        if (captured is null || !captured.Values.Enabled || captured.Revision < 1 ||
            captured.Values.ProfileId is null || captured.Values.ProfileRevision is null ||
            observation.Source.DefinitionId != ConversationWorkflowContract.DefinitionId ||
            !captured.Values.SourceProfileIds.Contains(observation.Source.ProfileId, StringComparer.Ordinal) ||
            !TitlePolicy.TryCaptureText(observation.UserText, out var hash, out var excerpt)) return;
        var key = TitlePolicy.SourceWorkKey(observation.Source.ConversationId, observation.Source.RequestId,
            observation.Source.EventId, hash);
        var workRequestId = "title-" + key;
        var workConversationId = "title-work-" + key;
        var known = await host.InspectAsync(workRequestId, linked.Token).ConfigureAwait(false);
        CheckAdmission(known, workRequestId);
        if (known.State != SourceBoundBackgroundAdmissionState.Missing)
        {
            if (known.State == SourceBoundBackgroundAdmissionState.Uncertain) Report("title-work-uncertain", workRequestId);
            return;
        }
        var input = TitleSourceBoundInput.PackageValues(hash, excerpt);
        var intent = new SourceBoundBackgroundIntent(DefinitionId, TitlePackageIdentity.DefinitionVersion,
            workRequestId, workConversationId, captured.Values.ProfileId, captured.Values.ProfileRevision.Value,
            captured.Revision, TitlePolicy.WorkUserText, input, TitlePackageIdentity.StepLimit);
        // One immutable decision/revision and at most one admission attempt. Never
        // retag stale intent with freshly loaded settings or replay an uncertain call.
        var admitted = await host.AdmitAsync(observation, intent, linked.Token).ConfigureAwait(false);
        CheckAdmission(admitted, workRequestId);
        if (admitted.State is SourceBoundBackgroundAdmissionState.Uncertain or SourceBoundBackgroundAdmissionState.Missing)
            Report("title-work-unconfirmed", workRequestId);
    }

    private static void CheckAdmission(SourceBoundBackgroundAdmission value, string requestId)
    {
        if (value is null || value.WorkRequestId != requestId || !Enum.IsDefined(value.State))
            throw new InvalidDataException("Title work admission result is invalid.");
    }
    private void Report(string code, string requestId)
    {
        try { owner?.Publish("conversation-titles.status", requestId, new { code }); } catch { }
    }
    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming, System.Text.Json.JsonElement? currentValues, bool portable) =>
        policy.Admit(incoming, currentValues, portable);
    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current) => policy.ExportPortable(current);
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; snapshot = null; }
        try { lifetime.Cancel(); } catch (AggregateException) { }
        lifetime.Dispose(); refresh.Dispose();
    }
}
