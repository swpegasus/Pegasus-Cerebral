using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Core;

namespace Cerebral.Desktop.Plugins;

public sealed record DesktopHostedPanel(string PluginId, string Id, string Title, FrameworkElement View);
public sealed record DesktopHostedAction(string PluginId, string Id, string Label, string? ToolTip, Func<CancellationToken, Task> ExecuteAsync);
public sealed record DesktopHostedTextEntryAction(string PluginId, string Id, string Label, string? ToolTip,
    Func<IDesktopTextEntry, CancellationToken, Task> ExecuteAsync);
public sealed record DesktopHostedTextEntrySubmissionPreparation(string PluginId,
    Func<IDesktopTextEntry, CancellationToken, Task<DesktopOperationResult>> PrepareAsync);
public sealed record DesktopHostedInputMode(string PluginId, string Id, string Label, string? ToolTip, bool QuickToggle,
    Func<CancellationToken, Task<string?>>? PrepareAsync,
    Func<DesktopInputRequest, CancellationToken, Task<DesktopInputInterception>> InterceptAsync);
public sealed record DesktopHostedComposerCommand(string PluginId, string Id,
    Func<DesktopInputRequest, CancellationToken, Task<DesktopComposerCommandResult>> ExecuteAsync);
public sealed record DesktopHostedConversationWorkspace(string PluginId, string Id, string Title,
    Func<DesktopConversationWorkspaceContext, CancellationToken, Task<FrameworkElement>> CreateViewAsync);
public sealed record DesktopHostedAgentTemplate(string PluginId, string Id, string Name, string ExecutionRole, string Purpose, string Instructions,
    bool AutomaticBudgets, IReadOnlyList<string> RequiredCapabilities, IReadOnlyList<string> RequiredTools,
    IReadOnlyDictionary<string, JsonElement> PluginSettings, int MaxSteps, int MaxSeconds)
{
    public AgentProfile CreateProfile() => new()
    {
        Name = Name,
        Role = ExecutionRole,
        Purpose = Purpose,
        Instructions = Instructions,
        FollowsModelBudgets = AutomaticBudgets,
        MaxSteps = MaxSteps,
        MaxSeconds = MaxSeconds,
        Capabilities = RequiredCapabilities.Distinct(StringComparer.Ordinal).ToList(),
        AllowedTools = RequiredTools.Distinct(StringComparer.Ordinal).ToList(),
        PluginSettings = PluginSettings.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal)
    };
}
public sealed record DesktopPluginLoadStatus(string Id, bool Loaded, string? Error);
public sealed record DesktopPluginLoadProgress(string Id, string State, string? Diagnostic = null);
public sealed record DesktopPluginReopenResult(bool InstanceRecreated, string? Error = null);

/// <summary>Native UI composition with cooperative cancellation and immutable-generation hot replacement.</summary>
public sealed partial class DesktopPluginRuntime(DesktopPluginCatalog catalog, DesktopPluginHostAdapter host, Dispatcher dispatcher) : IDisposable
{
    // Strong ownership lasts until confirmed cleanup. A failed native cleanup requires actual
    // process exit; recreating the runtime must not discard the retired instance or its OS lease.
    private static readonly object retirementGate = new();
    private static readonly Dictionary<string, HashSet<LoadedPlugin>> processRetirements = new(StringComparer.OrdinalIgnoreCase);
    private int loadingDepth;
    private TaskCompletionSource? constructionSettlement;
    private readonly Dictionary<string, LoadedPlugin> loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedPlugin> retiring = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> startupErrors = new(StringComparer.Ordinal);
    private bool disposed;
    private bool listening;
    private int presentationDepth;
    private bool changingContributions;
    private bool notifyingChanged;
    private FileSystemWatcher? watcher;
    private int refreshQueued;
    private DispatcherTimer? publicationRefreshTimer;
    private void OnPresentationRefresh(object? sender, EventArgs args) => RaiseChanged();
    private void RaiseChanged()
    {
        // Refresh requests from render factories or mounting callbacks must not recursively redraw the UI.
        if (disposed || changingContributions || notifyingChanged || presentationDepth != 0) return;
        try { notifyingChanged = true; Changed?.Invoke(this, EventArgs.Empty); }
        finally { notifyingChanged = false; }
    }
    public event EventHandler? Changed;
    public event EventHandler<DesktopPluginLoadProgress>? LoadProgress;
    private IReadOnlyList<DesktopMountedContribution> ActiveMounts =>
        DesktopMountRegistry.Resolve(loaded.Values.SelectMany(item => item.Mounts), host.Surface);
    public IReadOnlyList<DesktopHostedPanel> Panels => ActiveMounts
        .Where(item => item.Descriptor.Slot is DesktopMountSlots.SidebarTabs or DesktopMountSlots.ConfigurationPages)
        .Select(item => (DesktopHostedPanel)item.Value).ToArray();
    public IReadOnlyList<DesktopHostedPanel> ComposerAccessories => ActiveMounts
        .Where(item => item.Descriptor.Slot == DesktopMountSlots.ComposerAccessories)
        .Select(item => (DesktopHostedPanel)item.Value).ToArray();
    public IReadOnlyList<DesktopHostedPanel> WorkspaceTrees => ActiveMounts
        .Where(item => item.Descriptor.Slot == DesktopMountSlots.WorkspaceTree)
        .Select(item => (DesktopHostedPanel)item.Value).ToArray();
    public IReadOnlyList<DesktopHostedPanel> WorkspacePanels => ActiveMounts
        .Where(item => item.Descriptor.Slot == DesktopMountSlots.WorkspacePanels)
        .Select(item => (DesktopHostedPanel)item.Value).ToArray();
    public IReadOnlyList<DesktopHostedPanel> NewConversationViews => ActiveMounts
        .Where(item => item.Descriptor.Slot == DesktopMountSlots.NewConversationStart)
        .Select(item => (DesktopHostedPanel)item.Value).ToArray();
    public IReadOnlyList<DesktopHostedPanel> StatusItems => ActiveMounts
        .Where(item => item.Descriptor.Slot == DesktopMountSlots.StatusBar)
        .Select(item => (DesktopHostedPanel)item.Value).ToArray();
    /// <summary>Compatibility union of ordinary composer and workspace actions.</summary>
    public IReadOnlyList<DesktopHostedAction> ComposerActions => ActiveMounts
        .Where(item => item.Kind == DesktopMountKind.Action)
        .Select(item => (DesktopHostedAction)item.Value).ToArray();
    public IReadOnlyList<DesktopHostedAction> ActionsFor(string slot) => ActiveMounts
        .Where(item => item.Descriptor.Slot == slot && item.Kind == DesktopMountKind.Action)
        .Select(item => (DesktopHostedAction)item.Value).ToArray();
    /// <summary>Compatibility union, de-duplicated because v1 actions are adapted to composer and answer slots.</summary>
    public IReadOnlyList<DesktopHostedTextEntryAction> TextEntryActions => ActiveMounts
        .Where(item => item.Kind == DesktopMountKind.TextEntryAction)
        .GroupBy(item => ((DesktopHostedTextEntryAction)item.Value).PluginId + ":" + ((DesktopHostedTextEntryAction)item.Value).Id, StringComparer.Ordinal)
        .Select(group => (DesktopHostedTextEntryAction)group.First().Value).ToArray();
    public IReadOnlyList<DesktopHostedTextEntryAction> TextEntryActionsFor(string slot, string targetId) => ActiveMounts
        .Where(item => item.Descriptor.Slot == slot && item.Kind == DesktopMountKind.TextEntryAction &&
                       DesktopMountRegistry.AppliesToTextEntry(item.Descriptor, targetId))
        .Select(item => (DesktopHostedTextEntryAction)item.Value).ToArray();
    private SubmissionPreparation? submissionPreparation;
    // This implementation and its SDK facet ship together. Core/product version
    // alone is deliberately not evidence of submission lifecycle support.
    internal static bool SupportsRequiredHostFacet(string facet) =>
        facet == CerebralPluginPackage.SubmissionLifecycleHostFacet &&
        typeof(IDesktopTextEntrySubmissionPlugin).IsAssignableFrom(typeof(IDesktopTextEntrySubmissionLifecyclePlugin));
    private sealed class SubmissionPreparation(string requestId, DesktopConversationBinding binding, LoadedPlugin[] participants)
    {
        internal readonly string RequestId = requestId;
        internal readonly DesktopConversationBinding Binding = binding;
        internal readonly LoadedPlugin[] Participants = participants;
        internal readonly HashSet<LoadedPlugin> Successful = [];
        internal long? Revision;
    }
    public IReadOnlyList<DesktopHostedTextEntrySubmissionPreparation> TextEntrySubmissionPreparations => CreateSubmissionPreparations(null);
    public IReadOnlyList<DesktopHostedTextEntrySubmissionPreparation> TextEntrySubmissionPreparationsFor(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (requestId.Length > 256 || requestId.Contains('\0')) throw new ArgumentException("Invalid submission request identity.", nameof(requestId));
        return CreateSubmissionPreparations(requestId);
    }
    private IReadOnlyList<DesktopHostedTextEntrySubmissionPreparation> CreateSubmissionPreparations(string? requestId)
    {
        dispatcher.VerifyAccess();
        var participants = loaded.Values
        .Where(item => host.Surface == DesktopPluginSurface.Orchestrator && item.Plugin is IDesktopTextEntrySubmissionPlugin)
        .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var attempt = requestId is null ? null : new SubmissionPreparation(requestId, host.GetConversation().Binding, participants);
        if (attempt is not null) submissionPreparation = attempt;
        return participants.Select(item => new DesktopHostedTextEntrySubmissionPreparation(item.Id,
            (target, token) => PrepareTextEntrySubmission(item,
                (IDesktopTextEntrySubmissionPlugin)item.Plugin, target, token, attempt)))
        .ToArray();
    }
    public bool ConfirmTextEntrySubmissionPreparation(string requestId, DesktopConversationBinding binding, long revision)
    {
        dispatcher.VerifyAccess();
        if (disposed || submissionPreparation is not { } attempt || attempt.RequestId != requestId ||
            attempt.Binding != binding || revision < 0 || attempt.Successful.Count != attempt.Participants.Length ||
            attempt.Participants.Any(owner => !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner))) return false;
        var draft = host.GetDraft();
        if (draft.Binding != binding || draft.Revision != revision) return false;
        attempt.Revision = revision;
        return true;
    }
    public void AbandonTextEntrySubmissionPreparation(string requestId)
    {
        dispatcher.VerifyAccess();
        if (submissionPreparation?.RequestId == requestId) submissionPreparation = null;
    }
    public void NotifyTextEntrySubmissionAcknowledged(DesktopTextEntrySubmissionAcknowledgment acknowledgment)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(acknowledgment);
        var attempt = submissionPreparation;
        if (attempt is null || attempt.RequestId != acknowledgment.RequestId) return;
        submissionPreparation = null; // Consume before callbacks, including reentrant unload/reload.
        if (disposed || attempt.Binding != acknowledgment.Binding || attempt.Revision != acknowledgment.SubmittedDraftRevision ||
            acknowledgment.SubmittedDraftRevision == long.MaxValue ||
            acknowledgment.EmptyDraftRevision != acknowledgment.SubmittedDraftRevision + 1) return;
        var draft = host.GetDraft();
        if (draft.Binding != acknowledgment.Binding || draft.Revision != acknowledgment.EmptyDraftRevision || draft.Text.Length != 0) return;
        foreach (var owner in attempt.Participants)
        {
            if (disposed || host.GetConversation().Binding != acknowledgment.Binding) break;
            var currentDraft = host.GetDraft();
            if (currentDraft.Binding != acknowledgment.Binding || currentDraft.Revision != acknowledgment.EmptyDraftRevision || currentDraft.Text.Length != 0) break;
            if (!attempt.Successful.Contains(owner) || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner) ||
                owner.Plugin is not IDesktopTextEntrySubmissionLifecyclePlugin participant) continue;
            try
            {
                using var invocation = BeginInvocation(owner);
                participant.SubmissionAcknowledged(acknowledgment);
                owner.Invocations.RequireActive();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Report(owner.Id, "The message was accepted, but text-entry rearming failed: " + Error(error)); }
        }
    }
    public IReadOnlyList<DesktopHostedInputMode> InputModes => loaded.Values.SelectMany(item => item.InputModes).ToList();
    public IReadOnlyList<DesktopHostedComposerCommand> ComposerCommands => loaded.Values
        .SelectMany(item => item.ComposerCommands).OrderBy(item => item.PluginId, StringComparer.Ordinal)
        .ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
    public IReadOnlyList<DesktopHostedConversationWorkspace> ConversationWorkspaces => loaded.Values.SelectMany(item => item.ConversationWorkspaces).ToList();
    public IReadOnlyList<DesktopHostedConversationSettings> ConversationSettings => loaded.Values
        .SelectMany(item => item.ConversationSettings).OrderBy(item => item.PluginId, StringComparer.Ordinal)
        .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    public IReadOnlyList<DesktopHostedAgentTemplate> AgentTemplates => loaded.Values.SelectMany(item => item.AgentTemplates).ToList();
    public IReadOnlyList<DesktopPluginLoadStatus> Status => loaded.Keys.Select(id => new DesktopPluginLoadStatus(id, true, errors.GetValueOrDefault(id)))
        .Concat(retiring.Select(item => new DesktopPluginLoadStatus(item.Key, false,
            item.Value.RetirementError ?? ("Stopping the previous native instance; " + item.Value.Invocations.ActiveCount + " host-managed callbacks remain; dependent/editor cleanup may also be pending."))))
        .Concat(errors.Where(item => !retiring.ContainsKey(item.Key) && !loaded.ContainsKey(item.Key)).Select(item => new DesktopPluginLoadStatus(item.Key, false, item.Value))).OrderBy(item => item.Id).ToList();

    /// <summary>Read the identity of the package actually loaded by this runtime; never resolve it from a newer catalog entry.</summary>
    public (string Directory, string Version)? GetLoadedPackage(string pluginId)
    {
        dispatcher.VerifyAccess();
        return !disposed && loaded.TryGetValue(pluginId, out var item)
            ? (item.PackageDirectory, item.Version) : null;
    }

    /// <summary>Explicit refresh: retain active instances, dispose disabled ones, and attempt newly enabled or failed packages.</summary>
    public void LoadEnabled() => LoadEnabled(null);

    public void LoadEnabled(DesktopPluginLoadReservation? reservation)
    {
        dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        if (!listening) { host.PresentationRefreshRequested += OnPresentationRefresh; listening = true; }
        EnsureWatcher();
        if (changingContributions || (activationUpdateDepth != 0 && reservation is null)) return;
        using var observation = catalog.TryBeginObservation();
        if (observation is null)
        {
            SchedulePublicationRefresh();
            return; // Keep the last coherent contributions while a publisher owns the catalog.
        }
        publicationRefreshTimer?.Stop();
        var revoked = false;
        try
        {
            changingContributions = true;
            var packages = catalog.List();
            var blockedUpdates = packages.Where(item => item.Enabled && catalog.IsActivationBlocked(item.Id) &&
                    (activationUpdateDepth == 0 || item.Id != activationUpdateId))
                .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var enabled = packages.Where(item => item.Enabled && item.Error is null && !blockedUpdates.Contains(item.Id))
                .ToDictionary(item => item.Id, StringComparer.Ordinal);
            // Owner disablement/invalid admission revokes contributions even if no capacity remains
            // for an optional consumer's replacement. New-load quotas must never keep revoked UI live.
            foreach (var id in loaded.Keys.Where(id => !enabled.ContainsKey(id)).ToArray())
            { revoked = true; Disable(id); }
            var manifests = enabled.ToDictionary(item => item.Key, item => catalog.Inspect(item.Value.ManifestPath), StringComparer.Ordinal);

            var plan = CreateLoadPlan(enabled, manifests);
            using var admission = ReserveAdmission(plan, reservation);
            try { admission.Bind(catalog.RootDirectory, plan); }
            catch (Exception error)
            {
                foreach (var candidate in plan) errors[candidate.Id] = Error(error);
                throw;
            }
            // Preserve unchanged instances, including consumers. Resolve providers before comparing
            // the exact instances captured at construction; an optional provider that still failed
            // to load must not cause another needless consumer/WPF assembly load on every refresh.
            foreach (var id in loaded.Values.Reverse().Where(item => !enabled.TryGetValue(item.Id, out var package) ||
                         !item.PackageDirectory.Equals(package.Directory, StringComparison.OrdinalIgnoreCase) ||
                         !item.Version.Equals(package.Version, StringComparison.Ordinal))
                         .Select(item => item.Id).ToList()) Disable(id);
            errors.Clear(); startupErrors.Clear();
            foreach (var id in blockedUpdates) errors[id] = "Native activation recovery is interrupted or incomplete. Inspect the retained result; automatic native activation was not replayed.";
            foreach (var invalid in packages.Where(item => item.Error is not null)) errors[invalid.Id] = invalid.Error!;
            var resolving = new List<string>();
            bool LoadResolved(string id)
            {
                if (retiring.TryGetValue(id, out var stopping))
                {
                    stopping.ReloadAfterRetirement = true;
                    errors[id] = "The previous native instance is still stopping. Its replacement waits for callback and editor settlement.";
                    return false;
                }
                if (errors.ContainsKey(id)) return false;
                var cycleAt = resolving.IndexOf(id);
                if (cycleAt >= 0)
                {
                    var cycle = resolving.Skip(cycleAt).Append(id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    var message = "Native plugin dependency cycle among: " + string.Join(", ", cycle);
                    foreach (var member in cycle) errors[member] = message;
                    return false;
                }
                resolving.Add(id);
                var manifest = manifests[id];
                foreach (var dependency in manifest.Dependencies ?? [])
                {
                    var declaration = DesktopPluginCatalog.Declaration(dependency);
                    var available = enabled.TryGetValue(dependency.Id, out var target) &&
                        PluginDependencyPolicy.Compatible(declaration, target.Version);
                    if (!available)
                    {
                        if (!dependency.Optional)
                        {
                            errors[id] = "Required dependency '" + dependency.Id + "' is missing, disabled, invalid, or version-incompatible.";
                            Disable(id);
                            resolving.RemoveAt(resolving.Count - 1); return false;
                        }
                        continue;
                    }
                    if (!LoadResolved(dependency.Id) && !dependency.Optional)
                    {
                        errors[id] = "Required dependency '" + dependency.Id + "' did not load: " + errors.GetValueOrDefault(dependency.Id);
                        Disable(id);
                        resolving.RemoveAt(resolving.Count - 1); return false;
                    }
                }
                resolving.RemoveAt(resolving.Count - 1);
                if (loaded.TryGetValue(id, out var current))
                {
                    if (current.ResolvedProviders.All(binding =>
                        ReferenceEquals(binding.Value, ResolveProvider(manifest.Dependencies!.Single(item => item.Id == binding.Key)))))
                        return true;
                    Disable(id);
                }
                return !errors.ContainsKey(id) && TryLoad(enabled[id], manifest, admission);
            }
            foreach (var item in enabled.Values.OrderBy(item => item.Id)) _ = LoadResolved(item.Id);
        }
        finally
        {
            changingContributions = false;
            if (revoked)
            {
                // Notify the host after mandatory revocation even when subsequent admission fails.
                // An observer error must not hide the original admission/cleanup diagnostic.
                try { RaiseChanged(); }
                catch (Exception error) { host.ReportStatus("Native revocation views could not refresh: " + Error(error)); }
            }
        }
        if (!revoked) RaiseChanged();
    }


    /// <summary>Reserve this process's candidate/dependent load closure before changing catalog selection.
    /// The caller passes the exact reservation into LoadEnabled after successful publication.</summary>
    public DesktopPluginLoadReservation ReserveForPublication(string manifestPath, bool enabled)
    {
        dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        using var observation = catalog.TryBeginObservation() ?? throw DesktopPluginPublicationLease.Busy();
        var candidate = catalog.Inspect(manifestPath);
        var packages = catalog.List().Where(item => item.Enabled && item.Error is null)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var manifests = packages.ToDictionary(item => item.Key, item => catalog.Inspect(item.Value.ManifestPath), StringComparer.Ordinal);
        if (enabled)
        {
            packages[candidate.Id] = new(candidate.Id, candidate.Name, candidate.Version, candidate.Description, true,
                Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, Path.GetFullPath(manifestPath), null);
            manifests[candidate.Id] = candidate;
        }
        else { packages.Remove(candidate.Id); manifests.Remove(candidate.Id); }
        var plan = CreateLoadPlan(packages, manifests, enabled ? candidate.Id : null);
        return DesktopPluginLoadLedger.Shared.Reserve(catalog.RootDirectory, plan);
    }

    private DesktopPluginLoadReservation ReserveAdmission(IReadOnlyList<DesktopPluginLoadPlan> plan,
        DesktopPluginLoadReservation? existing)
    {
        try { return existing ?? DesktopPluginLoadLedger.Shared.Reserve(catalog.RootDirectory, plan); }
        catch (Exception error)
        {
            foreach (var candidate in plan) errors[candidate.Id] = Error(error);
            throw;
        }
    }

    private IReadOnlyList<DesktopPluginLoadPlan> CreateLoadPlan(
        IReadOnlyDictionary<string, DesktopPluginInfo> enabled, IReadOnlyDictionary<string, DesktopPluginManifest> manifests,
        string? publishedCandidate = null)
    {
        var replacing = enabled.Values.Where(package => package.Id == publishedCandidate ||
                !loaded.TryGetValue(package.Id, out var current) ||
                !current.PackageDirectory.Equals(package.Directory, StringComparison.OrdinalIgnoreCase) ||
                current.Version != package.Version)
            .Select(package => package.Id).ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var consumer in loaded.Values.Where(item => enabled.ContainsKey(item.Id) && !replacing.Contains(item.Id)))
            {
                foreach (var dependency in consumer.Dependencies)
                {
                    var prior = consumer.ResolvedProviders[dependency.Id];
                    var available = enabled.TryGetValue(dependency.Id, out var package) &&
                        PluginDependencyPolicy.Compatible(DesktopPluginCatalog.Declaration(dependency), package.Version);
                    var current = ResolveProvider(dependency);
                    if ((prior is not null && (!available || replacing.Contains(dependency.Id) || !ReferenceEquals(prior, current))) ||
                        (prior is null && available && (current is not null || replacing.Contains(dependency.Id))))
                    {
                        replacing.Add(consumer.Id); changed = true; break;
                    }
                }
            }
        } while (changed);
        return replacing.Order(StringComparer.Ordinal).Select(id =>
            LoadPlan(id, enabled[id].Directory, enabled[id].Version, id == publishedCandidate)).ToArray();
    }

    private static DesktopPluginLoadPlan LoadPlan(string id, string directory, string version, bool publicationPending = false) =>
        new(id, directory, version, NativeDesktopPluginPackage.ComputeFingerprint(Path.Combine(directory, DesktopPluginCatalog.ManifestFileName)), publicationPending);

    /// <summary>
    /// Validates a targeted instance recreation without changing any contribution. Loaded dependents
    /// are rejected because recreating their provider without recreating them would leave stale references.
    /// </summary>
    public void ValidateCanReopenExact(string pluginId, string packageDirectory, string version)
    {
        dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        using var observation = catalog.TryBeginObservation() ?? throw DesktopPluginPublicationLease.Busy();
        var candidate = ReopenCandidate(pluginId, packageDirectory, version);
        DesktopPluginLoadLedger.Shared.Validate(catalog.RootDirectory, [LoadPlan(candidate.Id, candidate.PackageDirectory, candidate.Version)]);
    }

    /// <summary>
    /// Disposes and recreates exactly one already-loaded immutable package. Call
    /// ValidateCanReopenExact immediately before this method when delivery state must distinguish a
    /// preflight rejection from a lifecycle operation that may have started.
    /// </summary>
    public DesktopPluginReopenResult ReopenExact(string pluginId, string packageDirectory, string version)
    {
        dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        using var observation = catalog.TryBeginObservation() ?? throw DesktopPluginPublicationLease.Busy();
        var previous = ReopenCandidate(pluginId, packageDirectory, version);
        var package = catalog.List().Single(item => item.Id == pluginId);
        var manifest = catalog.Inspect(package.ManifestPath);
        var plan = LoadPlan(package.Id, package.Directory, package.Version);
        using var admission = DesktopPluginLoadLedger.Shared.Reserve(catalog.RootDirectory, [plan]);
        admission.Bind(catalog.RootDirectory, [plan]);
        var changed = false;
        try
        {
            changingContributions = true;
            if (!loaded.Remove(pluginId, out var removed) || !ReferenceEquals(removed, previous))
                throw new InvalidOperationException("The loaded native plugin changed before its exact reopen began.");
            changed = true;
            Release(removed);
            errors.Remove(pluginId);
            if (!TryLoad(package, manifest, admission))
                return new(false, BoundError(errors.GetValueOrDefault(pluginId) ?? "The native plugin did not reload."));
            var current = loaded[pluginId];
            if (ReferenceEquals(current.Plugin, previous.Plugin) || ReferenceEquals(current.Context, previous.Context))
            {
                loaded.Remove(pluginId);
                Release(current);
                errors[pluginId] = "The native plugin runtime did not create a distinct instance.";
                return new(false, errors[pluginId]);
            }
            return new(true);
        }
        finally
        {
            changingContributions = false;
            if (changed) RaiseChanged();
        }
    }

    private LoadedPlugin ReopenCandidate(string pluginId, string packageDirectory, string version)
    {
        if (catalog.IsActivationBlocked(pluginId))
            throw new InvalidOperationException("Native activation recovery is interrupted or incomplete; an instance reopen cannot replay it.");
        if (changingContributions || presentationDepth != 0)
            throw new InvalidOperationException("Native plugin contributions are already changing; retry after the current presentation settles.");
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(packageDirectory) || string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("An exact loaded plugin identity, package directory, and version are required.");
        var expectedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
        if (!loaded.TryGetValue(pluginId, out var current) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.PackageDirectory)), expectedDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.Version, version, StringComparison.Ordinal))
            throw new InvalidOperationException("The exact reviewed native plugin generation is not currently loaded.");
        var package = catalog.List().SingleOrDefault(item => item.Id == pluginId);
        if (package is null || !package.Enabled || package.Error is not null ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.Directory)), expectedDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.Version, version, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed native plugin generation changed before reopen.");
        if (current.Invocations.ActiveCount != 0 || current.SettingsOpens.Count != 0 ||
            current.SettingsEditors.Any(editor => editor.HasPendingSave))
            throw new InvalidOperationException("The exact native plugin still has host-managed callbacks or settings work in progress. Finish or cancel that work before reopening; no instance was replaced.");
        var dependents = loaded.Values.Where(item => item.Id != pluginId &&
                item.Dependencies.Any(dependency => dependency.Id == pluginId))
            .Select(item => item.Id).Order(StringComparer.Ordinal).ToArray();
        if (dependents.Length != 0)
        {
            var shown = string.Join(", ", dependents.Take(8));
            if (dependents.Length > 8) shown += $", and {dependents.Length - 8} more";
            throw new InvalidOperationException("Cannot reopen this plugin while loaded dependents retain its instance: " +
                shown + ". Review or disable those dependents first; no plugin was reloaded.");
        }
        return current;
    }

    private static string BoundError(string value) => value[..Math.Min(value.Length, 2_000)];

    /// <summary>Detaches contributions and cancels host access immediately. Native Dispose waits
    /// for admitted host callbacks, settings work and dependents. Saved enablement is unchanged.</summary>
    public void Disable(string id)
    {
        dispatcher.VerifyAccess();
        foreach (var dependent in loaded.Values.Reverse().Where(item => item.ResolvedProviders.TryGetValue(id, out var provider) && provider is not null)
                     .Select(item => item.Id).ToList())
            Disable(dependent);
        if (!loaded.Remove(id, out var item)) return;
        Release(item);
        RaiseChanged();
    }

    public DesktopMessagePresentation? Present(DesktopMessageSnapshot message)
    {
        dispatcher.VerifyAccess();
        if (disposed) return null;
        try { presentationDepth++; return PresentCore(message); }
        finally { presentationDepth--; }
    }

    private DesktopMessagePresentation? PresentCore(DesktopMessageSnapshot message)
    {
        if (host.Surface != DesktopPluginSurface.Orchestrator) return null;
        if (message.Text.Length > DesktopPluginContract.MaxTextChars)
            message = message with { Text = message.Text[..DesktopPluginContract.MaxTextChars], Truncated = true };
        FrameworkElement? replacement = null;
        var decorations = new StackPanel();
        foreach (var mount in ActiveMounts.Where(item =>
                     item.Descriptor.Slot == DesktopMountSlots.MessageRenderers &&
                     DesktopMountRegistry.AppliesToMessage(item.Descriptor, message)))
        {
            try
            {
                var item = (DesktopMountMessageRenderer)mount.Value;
                using var invocation = BeginInvocation(mount.PluginId);
                var view = item.Render(message);
                if (view is null) continue;
                RequireCurrent(mount.PluginId); RequireUnparented(view); replacement = view;
            }
            catch (Exception ex) { Report(mount.PluginId, "Message renderer failed: " + Error(ex)); }
        }
        foreach (var mount in ActiveMounts.Where(item =>
                     item.Descriptor.Slot == DesktopMountSlots.MessageDecorations &&
                     DesktopMountRegistry.AppliesToMessage(item.Descriptor, message)))
        {
            try
            {
                var item = (DesktopMountMessageDecoration)mount.Value;
                using var invocation = BeginInvocation(mount.PluginId);
                var view = item.Render(message);
                if (view is null) continue;
                RequireCurrent(mount.PluginId); RequireUnparented(view); decorations.Children.Add(view);
            }
            catch (Exception ex) { Report(mount.PluginId, "Message decoration failed: " + Error(ex)); }
        }
        // Desktop API v1 presentation factories remain a compatibility lane. They are deterministic by package ID;
        // new descriptor-first renderers use explicit replacement validation above.
        foreach (var item in loaded.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList())
        {
            try
            {
                using var invocation = BeginInvocation(item);
                var presentation = item.Plugin.CreateMessagePresentation(message);
                item.Invocations.RequireActive();
                if (presentation is null) continue;
                if (presentation.Replacement is { } view && replacement is null)
                { RequireUnparented(view); replacement = view; }
                if (presentation.Decoration is { } decoration)
                { RequireUnparented(decoration); decorations.Children.Add(decoration); }
            }
            catch (Exception ex) { Report(item.Id, "Message presentation failed: " + Error(ex)); }
        }
        return replacement is null && decorations.Children.Count == 0 ? null :
            new(replacement, decorations.Children.Count == 0 ? null : decorations);
    }

    private void RequireCurrent(string pluginId)
    {
        if (disposed || !loaded.ContainsKey(pluginId))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
    }

    private LoadedPlugin? ResolveProvider(DesktopPluginDependency dependency) =>
        loaded.TryGetValue(dependency.Id, out var provider) &&
        PluginDependencyPolicy.Compatible(DesktopPluginCatalog.Declaration(dependency), provider.Version)
            ? provider : null;

    private bool TryLoad(DesktopPluginInfo info, DesktopPluginManifest? acceptedManifest, DesktopPluginLoadReservation admission)
    {
        if (retiring.TryGetValue(info.Id, out var stopping))
        {
            stopping.ReloadAfterRetirement = true;
            errors[info.Id] = "The previous native instance must finish stopping before a replacement can load.";
            return false;
        }
        var processBlock = ProcessRetirementBlock(info.Id);
        if (processBlock is not null) { errors[info.Id] = processBlock; return false; }
        PluginLoadContext? context = null; IDesktopPlugin? plugin = null; ScopedDesktopPluginHost? scope = null;
        LoadedPlugin? owned = null;
        if (loadingDepth++ == 0) constructionSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var manifest = acceptedManifest ?? catalog.Inspect(info.ManifestPath);
            if (manifest.Id != info.Id) throw new InvalidDataException("The plugin identity changed. Review its package again.");
            // Capture resolved instances before any progress handler or native callback can reenter Disable.
            var providers = (manifest.Dependencies ?? []).Select(dependency =>
                (Dependency: dependency, Instance: ResolveProvider(dependency))).ToArray();
            void RequireConstruction()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                foreach (var provider in providers)
                    if ((!provider.Dependency.Optional || provider.Instance is not null) &&
                        (provider.Instance is null || !loaded.TryGetValue(provider.Dependency.Id, out var current) ||
                         !ReferenceEquals(current, provider.Instance)))
                        throw new InvalidOperationException("Dependency '" + provider.Dependency.Id + "' changed during native plugin construction; the candidate remains unpublished.");
            }
            T ReadNative<T>(Func<T> read)
            {
                RequireConstruction();
                var result = read();
                RequireConstruction();
                return result;
            }
            RequireConstruction();
            RaiseLoadProgress(new(info.Id, "loading"));
            RequireConstruction();
            var assemblyPath = DesktopPluginCatalog.ContainedPath(info.Directory, manifest.Assembly);
            context = new PluginLoadContext(assemblyPath);
            admission.Track(info.Id, context);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(manifest.EntryType, true)!;
            if (!type.IsPublic || type.IsAbstract || !typeof(IDesktopPlugin).IsAssignableFrom(type))
                throw new InvalidDataException("The entry type must be a public, concrete IDesktopPlugin implementation.");
            scope = host.CreateScope(manifest.Id, manifest.Name, info.Directory);
            plugin = (IDesktopPlugin)(Activator.CreateInstance(type) ?? throw new InvalidDataException("The native plugin could not be constructed."));
            owned = new LoadedPlugin(manifest.Id, info.Directory, info.Version, context, plugin, scope, manifest.Dependencies ?? [],
                providers.ToDictionary(item => item.Dependency.Id, item => item.Instance, StringComparer.Ordinal));
            ObjectDisposedException.ThrowIf(disposed, this);
            plugin.Initialize(scope);
            RequireConstruction();
            var panels = host.Surface == DesktopPluginSurface.Configuration
                ? plugin is IDesktopConfigurationPlugin configuration
                    ? ReadNative(() => configuration.ConfigurationPanels) ?? throw new InvalidDataException("The plugin returned no Configuration panel collection.")
                    : []
                : ReadNative(() => plugin.Panels) ?? throw new InvalidDataException("The plugin returned no Cerebral panel collection.");
            var actions = host.Surface == DesktopPluginSurface.Orchestrator
                ? ReadNative(() => plugin.ComposerActions) ?? throw new InvalidDataException("The plugin returned no composer action collection.")
                : [];
            var textEntryActions = host.Surface == DesktopPluginSurface.Orchestrator && plugin is IDesktopTextEntryPlugin textEntry
                ? ReadNative(() => textEntry.TextEntryActions) ?? throw new InvalidDataException("The plugin returned no text-entry action collection.")
                : [];
            var inputModes = host.Surface == DesktopPluginSurface.Orchestrator && plugin is IDesktopInputModePlugin inputModePlugin
                ? ReadNative(() => inputModePlugin.InputModes) ?? throw new InvalidDataException("The plugin returned no input-mode collection.")
                : [];
            var composerCommands = host.Surface == DesktopPluginSurface.Orchestrator && plugin is IDesktopComposerCommandPlugin commandPlugin
                ? ReadNative(() => commandPlugin.ComposerCommands) ?? throw new InvalidDataException("The plugin returned no composer-command collection.")
                : [];
            var conversationWorkspaces = host.Surface == DesktopPluginSurface.Orchestrator && plugin is IDesktopConversationWorkspacePlugin workspacePlugin
                ? ReadNative(() => workspacePlugin.ConversationWorkspaces) ?? throw new InvalidDataException("The plugin returned no conversation-workspace collection.")
                : [];
            var conversationSettings = host.Surface == DesktopPluginSurface.Orchestrator && plugin is IDesktopConversationSettingsPlugin settingsPlugin
                ? ReadNative(() => settingsPlugin.ConversationSettings) ?? throw new InvalidDataException("The plugin returned no conversation-settings collection.")
                : [];
            conversationSettings = ValidateConversationSettings(conversationSettings);
            var agentTemplates = host.Surface == DesktopPluginSurface.Configuration && plugin is IDesktopAgentTemplatePlugin templatePlugin
                ? ReadNative(() => templatePlugin.AgentTemplates) ?? throw new InvalidDataException("The plugin returned no agent-template collection.")
                : [];
            var descriptorMounts = plugin is IDesktopMountPlugin mountPlugin
                ? ReadNative(() => mountPlugin.MountContributions) ?? throw new InvalidDataException("The plugin returned no mount contribution collection.")
                : [];
            if (panels.Count > 8 || inputModes.Count > 8 || conversationWorkspaces.Count > 8 || actions.Count > 16 ||
                textEntryActions.Count > 16 || agentTemplates.Count > 16 || composerCommands.Count > 16)
                throw new InvalidDataException("A native plugin can contribute at most eight panels, input modes, or conversation workspaces and sixteen entries in each action, command, or agent-template collection.");
            if (descriptorMounts.Count > DesktopMountContract.MaximumContributionsPerPlugin || descriptorMounts.Any(item => item is null))
                throw new InvalidDataException($"A native plugin can contribute at most {DesktopMountContract.MaximumContributionsPerPlugin} non-null descriptor-first mounts.");
            ValidateIds(panels.Select(item => item.Id)); ValidateIds(actions.Select(item => item.Id));
            ValidateIds(textEntryActions.Select(item => item.Id)); ValidateIds(inputModes.Select(item => item.Id)); ValidateIds(agentTemplates.Select(item => item.Id));
            ValidateIds(conversationWorkspaces.Select(item => item.Id));
            ValidateIds(composerCommands.Select(item => item.Id));
            var legacySurface = host.Surface == DesktopPluginSurface.Orchestrator
                ? DesktopMountSurfaces.Orchestrator : DesktopMountSurfaces.Configuration;
            DesktopMountDescriptor Legacy(string id, string slot, int order) => new(id, slot)
            {
                Order = order,
                Applicability = new() { Surfaces = legacySurface }
            };
            var legacyOrder = 0;
            foreach (var panel in panels)
            {
                if (string.IsNullOrWhiteSpace(panel.Title) || panel.Title.Length > 100) throw new InvalidDataException("A plugin panel needs a title of at most 100 characters.");
                var view = ReadNative(panel.CreateView) ?? throw new InvalidDataException("A plugin panel returned no WPF view.");
                RequireUnparented(view);
                var hosted = new DesktopHostedPanel(manifest.Id, panel.Id, panel.Title, view);
                owned.Panels.Add(hosted);
                owned.Mounts.Add(new(manifest.Id, Legacy(panel.Id,
                    host.Surface == DesktopPluginSurface.Orchestrator ? DesktopMountSlots.SidebarTabs : DesktopMountSlots.ConfigurationPages,
                    legacyOrder++), DesktopMountKind.View, hosted, true));
            }
            foreach (var action in actions)
            {
                if (string.IsNullOrWhiteSpace(action.Label) || action.Label.Length > 100 || action.ExecuteAsync is null)
                    throw new InvalidDataException("A plugin composer action needs a label and handler.");
                var hosted = new DesktopHostedAction(manifest.Id, action.Id, action.Label, action.ToolTip,
                    token => ExecuteAction(owned, action, token));
                owned.Actions.Add(hosted);
                owned.Mounts.Add(new(manifest.Id, Legacy(action.Id,
                    action.Id.StartsWith("workspace.", StringComparison.Ordinal) ? DesktopMountSlots.WorkspaceActions : DesktopMountSlots.ComposerActions,
                    legacyOrder++), DesktopMountKind.Action, hosted, true));
            }
            foreach (var action in textEntryActions)
            {
                if (string.IsNullOrWhiteSpace(action.Label) || action.Label.Length > 100 || action.ExecuteAsync is null)
                    throw new InvalidDataException("A plugin text-entry action needs a label and handler.");
                var hosted = new DesktopHostedTextEntryAction(manifest.Id, action.Id, action.Label, action.ToolTip,
                    (target, token) => ExecuteTextEntryAction(owned, action, target, token));
                owned.TextEntryActions.Add(hosted);
                owned.Mounts.Add(new(manifest.Id, Legacy(action.Id, DesktopMountSlots.ComposerActions, legacyOrder++),
                    DesktopMountKind.TextEntryAction, hosted, true));
                owned.Mounts.Add(new(manifest.Id, Legacy(action.Id, DesktopMountSlots.AnswerActions, legacyOrder++),
                    DesktopMountKind.TextEntryAction, hosted, true));
            }
            foreach (var mode in inputModes)
            {
                if (string.IsNullOrWhiteSpace(mode.Label) || mode.Label.Length > 100 || mode.InterceptAsync is null)
                    throw new InvalidDataException("A plugin input mode needs a label and handler.");
                owned.InputModes.Add(new(manifest.Id, mode.Id, mode.Label, mode.ToolTip, mode.QuickToggle,
                    mode.PrepareAsync is null ? null : token => ExecuteInputModePreparation(owned, mode, token),
                    (input, token) => ExecuteInputMode(owned, mode, input, token)));
            }
            foreach (var command in composerCommands)
            {
                if (command.ExecuteAsync is null)
                    throw new InvalidDataException("A plugin composer command needs a handler.");
                owned.ComposerCommands.Add(new(manifest.Id, command.Id,
                    (input, token) => ExecuteComposerCommand(owned, command, input, token)));
            }
            foreach (var workspace in conversationWorkspaces)
            {
                if (string.IsNullOrWhiteSpace(workspace.Title) || workspace.Title.Length > 100 || workspace.CreateViewAsync is null)
                    throw new InvalidDataException("A plugin conversation workspace needs a title and view factory.");
                owned.ConversationWorkspaces.Add(new(manifest.Id, workspace.Id, workspace.Title,
                    (context, token) => CreateConversationWorkspaceAsync(owned, workspace, context, token)));
            }
            foreach (var settings in conversationSettings)
                owned.ConversationSettings.Add(new(manifest.Id, settings.Id, settings.Title,
                    (target, token) => OpenConversationSettingsAsync(owned, settings, target, token)));
            foreach (var template in agentTemplates)
            {
                if (string.IsNullOrWhiteSpace(template.Name) || template.Name.Length > 100 ||
                    template.ExecutionRole is not ("lead" or "helper" or "compactor") ||
                    template.Purpose.Length > 2_000 || template.Instructions.Length > DesktopPluginContract.MaxTextChars ||
                    template.Purpose.Contains('\0') || template.Instructions.Contains('\0'))
                    throw new InvalidDataException("An agent template needs a short name, a valid execution role, and bounded text without null characters.");
                if (template.RequiredCapabilities.Count > 32 || template.RequiredTools.Count > 64 ||
                    template.MaxSteps is < 1 or > 10_000 || template.MaxSeconds is < 1 or > 86_400 ||
                    template.RequiredCapabilities.Concat(template.RequiredTools).Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Contains('\0')))
                    throw new InvalidDataException("An agent template returned invalid execution defaults or excessive capability/tool requirements.");
                if (template.PluginSettings.Count > 32 || template.PluginSettings.Any(item =>
                        string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 100 || item.Key.Contains('\0') ||
                        item.Value.ValueKind != JsonValueKind.Object || item.Value.GetRawText().Length > 65_536))
                    throw new InvalidDataException("An agent template returned invalid or excessive plugin settings.");
                owned.AgentTemplates.Add(new(manifest.Id, template.Id, template.Name, template.ExecutionRole,
                    template.Purpose, template.Instructions, template.AutomaticBudgets,
                    template.RequiredCapabilities.ToArray(), template.RequiredTools.ToArray(),
                    template.PluginSettings.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal),
                    template.MaxSteps, template.MaxSeconds));
            }
            foreach (var contribution in descriptorMounts)
            {
                var descriptor = DesktopMountRegistry.Freeze(contribution.Descriptor);
                object payload = contribution;
                var appliesHere = DesktopMountRegistry.AppliesToSurface(descriptor, host.Surface);
                switch (contribution)
                {
                    case DesktopMountView viewMount:
                        if (string.IsNullOrWhiteSpace(viewMount.Title) || viewMount.Title.Length > 100 || viewMount.CreateView is null)
                            throw new InvalidDataException("A descriptor-first view needs a title and view factory.");
                        if (appliesHere)
                        {
                            var view = ReadNative(viewMount.CreateView) ?? throw new InvalidDataException("A descriptor-first view returned no WPF element.");
                            RequireUnparented(view);
                            var hosted = new DesktopHostedPanel(manifest.Id, descriptor.Id, viewMount.Title, view);
                            owned.Panels.Add(hosted); payload = hosted;
                        }
                        break;
                    case DesktopMountAction actionMount:
                        if (string.IsNullOrWhiteSpace(actionMount.Label) || actionMount.Label.Length > 100 || actionMount.ExecuteAsync is null)
                            throw new InvalidDataException("A descriptor-first action needs a label and handler.");
                        if (appliesHere)
                        {
                            var hosted = new DesktopHostedAction(manifest.Id, descriptor.Id, actionMount.Label, actionMount.ToolTip,
                                token => ExecuteMountAction(owned, actionMount, token));
                            owned.Actions.Add(hosted); payload = hosted;
                        }
                        break;
                    case DesktopMountTextEntryAction textMount:
                        if (string.IsNullOrWhiteSpace(textMount.Label) || textMount.Label.Length > 100 || textMount.ExecuteAsync is null)
                            throw new InvalidDataException("A descriptor-first text-entry action needs a label and handler.");
                        if (appliesHere)
                        {
                            var hosted = new DesktopHostedTextEntryAction(manifest.Id, descriptor.Id, textMount.Label, textMount.ToolTip,
                                (target, token) => ExecuteMountTextEntryAction(owned, textMount, target, token));
                            owned.TextEntryActions.Add(hosted); payload = hosted;
                        }
                        break;
                    case DesktopMountMessageRenderer renderer when renderer.Render is null:
                        throw new InvalidDataException("A descriptor-first message renderer needs a handler.");
                    case DesktopMountMessageDecoration decoration when decoration.Render is null:
                        throw new InvalidDataException("A descriptor-first message decoration needs a handler.");
                    case DesktopMountMessageRenderer or DesktopMountMessageDecoration:
                        break;
                    default:
                        throw new InvalidDataException("The plugin returned an unknown descriptor-first contribution type.");
                }
                owned.Mounts.Add(new(manifest.Id, descriptor, contribution.Kind, payload));
            }
            if (owned.Mounts.GroupBy(item => item.Descriptor.Slot).Any(group => group.Count() > 16))
                throw new InvalidDataException("A native plugin can contribute at most sixteen mounts to one slot.");
            _ = DesktopMountRegistry.Resolve(loaded.Values.SelectMany(item => item.Mounts).Concat(owned.Mounts), host.Surface);
            RequireConstruction();
            if (ProcessRetirementBlock(manifest.Id) is { } blocked) throw new InvalidOperationException(blocked);
            loaded.Add(manifest.Id, owned);
            RaiseLoadProgress(new(info.Id, "loaded"));
            return !disposed && loaded.TryGetValue(manifest.Id, out var current) && ReferenceEquals(current, owned);
        }
        catch (Exception ex)
        {
            startupErrors[info.Id] = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
            errors[info.Id] = BoundError(Error(ex)); ReportSettingsFailure(info.Id, "Could not load native plugin: " + BoundError(Error(ex)));
            RaiseLoadProgress(new(info.Id, "failed", Error(ex)));
            if (owned is not null)
            {
                if (loaded.TryGetValue(owned.Id, out var current) && ReferenceEquals(current, owned)) loaded.Remove(owned.Id);
                Release(owned); // Partial initialization follows the same no-retry cleanup policy.
            }
            else
            {
                try { scope?.Dispose(); context?.Unload(); }
                catch (Exception cleanup) when (SettingsFailure(cleanup)) { ReportSettingsFailure(info.Id, "Unconstructed load cleanup failed: " + BoundError(Error(cleanup))); }
            }
            return false;
        }
        finally
        {
            loadingDepth--;
            if (loadingDepth == 0)
            {
                if (disposed) ReleaseLoaded();
                constructionSettlement!.TrySetResult();
                constructionSettlement = null;
            }
        }
    }

    private async Task ExecuteAction(LoadedPlugin owner, DesktopAction action, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        if (disposed || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
        using var invocation = BeginInvocation(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try { await action.ExecuteAsync(linked.Token); linked.Token.ThrowIfCancellationRequested(); owner.Invocations.RequireActive(); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report(owner.Id, "Action failed: " + Error(ex)); throw; }
    }

    private async Task ExecuteMountAction(LoadedPlugin owner, DesktopMountAction action, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        if (disposed || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
        using var invocation = BeginInvocation(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try { await action.ExecuteAsync(linked.Token); linked.Token.ThrowIfCancellationRequested(); owner.Invocations.RequireActive(); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report(owner.Id, "Mount action failed: " + Error(ex)); throw; }
    }

    private IDisposable BeginInvocation(LoadedPlugin owner)
    {
        dispatcher.VerifyAccess();
        if (disposed || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(owner, current))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
        var observation = DesktopPluginPublicationLease.TryAcquireIndependentReader(catalog.RootDirectory) ??
            throw DesktopPluginPublicationLease.Busy();
        try
        {
            RequireSelectedInstance(owner, new HashSet<LoadedPlugin>());
        }
        catch { observation.Dispose(); throw; }
        return owner.Invocations.Enter(observation);
    }

    private void RequireSelectedInstance(LoadedPlugin owner, HashSet<LoadedPlugin> visited)
    {
        if (!visited.Add(owner)) return;
        if (!loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(owner, current) ||
            !catalog.IsCurrentSelection(owner.Id, owner.PackageDirectory))
            throw new InvalidOperationException("The selected native plugin or dependency generation changed before this callback. Refresh its contributions first.");
        foreach (var dependency in owner.Dependencies)
        {
            var provider = owner.ResolvedProviders[dependency.Id];
            if (!ReferenceEquals(provider, ResolveProvider(dependency)))
                throw new InvalidOperationException("A resolved native dependency changed before this callback. Refresh its contributions first.");
            if (provider is not null) RequireSelectedInstance(provider, visited);
        }
    }

    private IDisposable BeginInvocation(string id)
    {
        if (!loaded.TryGetValue(id, out var owner)) throw new InvalidOperationException("The native plugin is no longer enabled.");
        return BeginInvocation(owner);
    }

    private async Task ExecuteTextEntryAction(LoadedPlugin owner, DesktopTextEntryAction action, IDesktopTextEntry target, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(target);
        if (disposed || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
        using var invocation = BeginInvocation(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try { await action.ExecuteAsync(target, linked.Token); linked.Token.ThrowIfCancellationRequested(); owner.Invocations.RequireActive(); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report(owner.Id, "Text-entry action failed: " + Error(ex)); throw; }
    }

    private async Task<DesktopOperationResult> PrepareTextEntrySubmission(LoadedPlugin owner,
        IDesktopTextEntrySubmissionPlugin participant, IDesktopTextEntry target, CancellationToken token,
        SubmissionPreparation? attempt = null)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(target);
        if (disposed || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
        using var invocation = BeginInvocation(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            var result = await (attempt is not null && participant is IDesktopTextEntrySubmissionLifecyclePlugin lifecycle
                ? lifecycle.PrepareSubmissionAsync(attempt.RequestId, target, linked.Token)
                : participant.PrepareSubmissionAsync(target, linked.Token)) ??
                throw new InvalidDataException("The text-entry submission participant returned no result.");
            linked.Token.ThrowIfCancellationRequested(); owner.Invocations.RequireActive();
            if (result.Message?.Length > 2_000 || result.Message?.Contains('\0') == true)
                throw new InvalidDataException("The text-entry submission status is invalid or too large.");
            if (attempt is not null && ReferenceEquals(submissionPreparation, attempt) &&
                host.GetConversation().Binding == attempt.Binding && result.State is DesktopOperationState.Completed or DesktopOperationState.Accepted)
                attempt.Successful.Add(owner);
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report(owner.Id, "Text-entry submission preparation failed: " + Error(ex)); throw; }
    }

    private async Task ExecuteMountTextEntryAction(LoadedPlugin owner, DesktopMountTextEntryAction action,
        IDesktopTextEntry target, CancellationToken token)
    {
        dispatcher.VerifyAccess(); ArgumentNullException.ThrowIfNull(target);
        if (disposed || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
        using var invocation = BeginInvocation(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try { await action.ExecuteAsync(target, linked.Token); linked.Token.ThrowIfCancellationRequested(); owner.Invocations.RequireActive(); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report(owner.Id, "Mount text-entry action failed: " + Error(ex)); throw; }
    }

    private async Task<DesktopInputInterception> ExecuteInputMode(LoadedPlugin owner, DesktopInputMode mode,
        DesktopInputRequest input, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(input);
        if (disposed || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
        if (input.Submission.Binding != input.Conversation.Binding || string.IsNullOrWhiteSpace(input.Submission.RequestId) ||
            string.IsNullOrWhiteSpace(input.Submission.Text) || input.Submission.Text.Length > DesktopPluginContract.MaxTextChars)
            throw new ArgumentException("The plugin input request is invalid or exceeds the desktop text limit.");
        using var invocation = BeginInvocation(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            var result = await mode.InterceptAsync(input, linked.Token) ?? throw new InvalidDataException("The input mode returned no decision.");
            linked.Token.ThrowIfCancellationRequested(); owner.Invocations.RequireActive();
            if (result.Message?.Length > 2_000 || result.Message?.Contains('\0') == true)
                throw new InvalidDataException("The input-mode status message is invalid or too large.");
            if (result.State == DesktopInputInterceptionState.Hold)
            {
                if (result.Guidance is null) throw new InvalidDataException("A held input requires a guidance view.");
                RequireUnparented(result.Guidance);
            }
            else if (result.Guidance is not null)
                throw new InvalidDataException("Only a held input may return a guidance view.");
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report(owner.Id, "Input mode failed: " + Error(ex)); throw; }
    }

    private async Task<DesktopComposerCommandResult> ExecuteComposerCommand(LoadedPlugin owner,
        DesktopComposerCommand command, DesktopInputRequest input, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(input);
        if (disposed || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
        if (input.Submission.Binding != input.Conversation.Binding || string.IsNullOrWhiteSpace(input.Submission.RequestId) ||
            string.IsNullOrWhiteSpace(input.Submission.Text) || input.Submission.Text.Length > DesktopPluginContract.MaxTextChars)
            throw new ArgumentException("The plugin composer-command request is invalid or exceeds the desktop text limit.");
        using var invocation = BeginInvocation(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            var result = await command.ExecuteAsync(input, linked.Token) ??
                throw new InvalidDataException("The composer command returned no decision.");
            linked.Token.ThrowIfCancellationRequested(); owner.Invocations.RequireActive();
            if (result.Message?.Length > 2_000 || result.Message?.Contains('\0') == true)
                throw new InvalidDataException("The composer-command status message is invalid or too large.");
            if (result.State == DesktopComposerCommandState.Pass && result.ConsumedPrefixCharacters != 0 ||
                result.State != DesktopComposerCommandState.Pass &&
                (result.ConsumedPrefixCharacters < 1 || result.ConsumedPrefixCharacters > input.Submission.Text.Length))
                throw new InvalidDataException("The composer command returned an invalid consumed prefix length.");
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report(owner.Id, "Composer command failed: " + Error(ex)); throw; }
    }

    private async Task<string?> ExecuteInputModePreparation(LoadedPlugin owner, DesktopInputMode mode, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        if (disposed || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
        using var invocation = BeginInvocation(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            var message = await mode.PrepareAsync!(linked.Token);
            linked.Token.ThrowIfCancellationRequested(); owner.Invocations.RequireActive();
            if (message?.Length > 500 || message?.Contains('\0') == true)
                throw new InvalidDataException("The input-mode preparation status is invalid or too large.");
            return message;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report(owner.Id, "Input preparation failed: " + Error(ex)); throw; }
    }

    private async Task<FrameworkElement> CreateConversationWorkspaceAsync(LoadedPlugin owner,
        DesktopConversationWorkspace workspace, DesktopConversationWorkspaceContext context, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(context);
        if (disposed || !loaded.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner))
            throw new InvalidOperationException("The native plugin is no longer enabled.");
        if (context.Conversation.Binding.SessionId is null || context.Conversation.Binding.SessionId != context.Binding.ConversationId ||
            context.Binding.PluginId != owner.Id || context.Binding.SurfaceId != workspace.Id)
            throw new ArgumentException("The conversation workspace context does not match this plugin contribution.");
        using var invocation = BeginInvocation(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            var view = await workspace.CreateViewAsync(context, linked.Token) ??
                throw new InvalidDataException("The plugin conversation workspace returned no WPF view.");
            linked.Token.ThrowIfCancellationRequested(); owner.Invocations.RequireActive();
            RequireUnparented(view);
            return view;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex) { Report(owner.Id, "Conversation workspace failed: " + Error(ex)); throw; }
    }

    private static void ValidateIds(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !seen.Add(id)) throw new InvalidDataException("Contribution IDs must be nonempty, unique within their slot, and at most 64 characters.");
    }

    private static void RequireUnparented(FrameworkElement view)
    {
        if (view.Parent is not null || VisualTreeHelper.GetParent(view) is not null)
            throw new InvalidDataException("Plugin presentation must return a new unparented WPF element for each mount.");
    }

    private void Release(LoadedPlugin item)
    {
        if (retiring.TryGetValue(item.Id, out var prior))
        {
            if (!ReferenceEquals(prior, item)) throw new InvalidOperationException("Another native instance is already stopping.");
            return;
        }
        retiring.Add(item.Id, item);
        RegisterRetirement(item);
        Exception? observationFailure = null;
        try { item.RetirementObservation = DesktopPluginPublicationLease.TryAcquireIndependentReader(catalog.RootDirectory); }
        catch (Exception error) when (SettingsFailure(error)) { observationFailure = error; }
        var invocationCleanup = item.Invocations.Retire();
        // Removing host access first prevents callbacks from submitting new work while plugin cleanup runs.
        try { item.Host.Dispose(); } catch (Exception ex) when (SettingsFailure(ex)) { ReportSettingsFailure(item.Id, "Host cleanup failed: " + BoundError(Error(ex))); }
        try { item.Lifetime.Cancel(); } catch (Exception ex) when (SettingsFailure(ex)) { ReportSettingsFailure(item.Id, "Action cancellation failed: " + BoundError(Error(ex))); }
        var settingsCleanup = RetireConversationSettings(item);
        var constructionCleanup = constructionSettlement?.Task ?? Task.CompletedTask;
        item.Panels.Clear(); item.Actions.Clear(); item.TextEntryActions.Clear(); item.Mounts.Clear(); item.InputModes.Clear(); item.ConversationWorkspaces.Clear();
        item.ComposerCommands.Clear(); item.ConversationSettings.Clear(); item.AgentTemplates.Clear();
        if (observationFailure is not null) { FailRetirement(item, observationFailure); return; }
        var cleanup = WaitForNativeCleanup(item, constructionCleanup, settingsCleanup, invocationCleanup);
        if (cleanup.IsCompletedSuccessfully && item.RetirementObservation is not null) FinishRelease(item);
        else _ = FinishReleaseAfterSettingsAsync(item, cleanup);
    }

    private string RetirementKey(string id) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(catalog.RootDirectory)) + "\0" + id;
    private void RegisterRetirement(LoadedPlugin item)
    {
        lock (retirementGate)
        {
            var key = RetirementKey(item.Id);
            if (!processRetirements.TryGetValue(key, out var owners)) processRetirements[key] = owners = [];
            owners.Add(item);
        }
    }
    private void UnregisterRetirement(LoadedPlugin item)
    {
        lock (retirementGate)
        {
            var key = RetirementKey(item.Id);
            if (!processRetirements.TryGetValue(key, out var owners)) return;
            owners.Remove(item);
            if (owners.Count == 0) processRetirements.Remove(key);
        }
    }
    private string? ProcessRetirementBlock(string id)
    {
        lock (retirementGate)
        {
            if (!processRetirements.TryGetValue(RetirementKey(id), out var owners)) return null;
            return owners.Select(item => item.RetirementError).FirstOrDefault(error => error is not null) ??
                "A previous native instance in this host process is still stopping; replacement must wait for its cleanup.";
        }
    }

    private async Task WaitForNativeCleanup(LoadedPlugin owner, Task construction, Task settings, Task invocations)
    {
        // An unpublished consumer can still retain its provider inside Initialize or a view factory.
        // Resolve retired dependents only after every current constructor has returned and registered cleanup.
        await construction;
        var dependents = retiring.Values.Where(other => !ReferenceEquals(other, owner) &&
            other.Dependencies.Any(dependency => dependency.Id == owner.Id)).Select(other => other.Retirement.Task).ToArray();
        await Task.WhenAll(dependents.Cast<Task>().Append(settings).Append(invocations));
        if (owner.SettingsCleanupError is { } editorFailure)
            throw new InvalidOperationException("Native settings editor cleanup failed.", editorFailure);
        var failed = dependents.Select(task => task.Result).FirstOrDefault(error => error is not null);
        if (failed is not null) throw new InvalidOperationException("Dependent native cleanup failed: " + failed);
    }

    private void EnsureWatcher()
    {
        if (watcher is not null) return;
        Directory.CreateDirectory(catalog.RootDirectory);
        watcher = new FileSystemWatcher(catalog.RootDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        watcher.Created += OnPackageChanged; watcher.Changed += OnPackageChanged;
        watcher.Deleted += OnPackageChanged; watcher.Renamed += OnPackageRenamed;
        watcher.Error += OnWatcherError;
    }

    private void SchedulePublicationRefresh()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        if (publicationRefreshTimer is null)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!disposed) QueueRefresh();
            };
            publicationRefreshTimer = timer;
        }
        if (!publicationRefreshTimer.IsEnabled) publicationRefreshTimer.Start();
    }

    private void OnPackageChanged(object sender, FileSystemEventArgs args) => QueueRefresh();
    private void OnPackageRenamed(object sender, RenamedEventArgs args) => QueueRefresh();
    private void OnWatcherError(object sender, ErrorEventArgs args) => QueueRefresh();
    private void QueueRefresh()
    {
        if (disposed || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            if (disposed) return;
            try { LoadEnabled(); }
            catch (Exception ex) { host.ReportStatus("Native plugin refresh failed: " + Error(ex)); }
        });
    }

    private void Report(string id, string error) => host.ReportStatus(id + ": " + error);
    private void RaiseLoadProgress(DesktopPluginLoadProgress progress)
    {
        try { LoadProgress?.Invoke(this, progress); }
        catch { /* Startup diagnostics are observational and cannot own plugin runtime stability. */ }
    }
    private static string Error(Exception error) => (error is TargetInvocationException { InnerException: { } inner } ? inner : error).Message;

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        publicationRefreshTimer?.Stop();
        publicationRefreshTimer = null;
        if (listening) host.PresentationRefreshRequested -= OnPresentationRefresh;
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnPackageChanged; watcher.Changed -= OnPackageChanged;
            watcher.Deleted -= OnPackageChanged; watcher.Renamed -= OnPackageRenamed; watcher.Error -= OnWatcherError;
            watcher.Dispose(); watcher = null;
        }
        if (loadingDepth == 0) ReleaseLoaded();
        Changed = null; LoadProgress = null;
    }

    /// <summary>
    /// Observes the exact native retirement owners after Dispose. The caller keeps this task
    /// across observation timeouts and awaits it while the UI dispatcher can finish cleanup.
    /// A failed native owner remains rooted for process-exit recovery.
    /// </summary>
    public async Task ObserveRetirementAsync()
    {
        dispatcher.VerifyAccess();
        if (!disposed) throw new InvalidOperationException("Desktop plugin retirement has not started.");
        var construction = constructionSettlement?.Task;
        if (loadingDepth != 0 && construction is null)
            throw new InvalidOperationException("Desktop plugin construction has no settlement owner.");
        if (construction is not null) await construction;
        dispatcher.VerifyAccess();
        if (loadingDepth != 0 || loaded.Count != 0)
            throw new InvalidOperationException("Desktop plugin construction or retirement is still unconfirmed.");

        // Include generations retired by earlier runtimes for this exact catalog root.
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(catalog.RootDirectory)) + "\0";
        LoadedPlugin[] pending;
        lock (retirementGate)
            pending = retiring.Values.Concat(processRetirements
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .SelectMany(entry => entry.Value)).Distinct().ToArray();
        var results = await Task.WhenAll(pending.Select(item => item.Retirement.Task));
        if (results.FirstOrDefault(error => error is not null) is { } failure)
            throw new InvalidOperationException("Desktop plugin native cleanup failed; the original owner and catalog lease remain held until process exit: " + failure);
    }

    private void ReleaseLoaded()
    {
        foreach (var item in loaded.Values.Reverse().ToList()) Release(item);
        loaded.Clear(); errors.Clear();
    }

    private sealed class LoadedPlugin(string id, string packageDirectory, string version, PluginLoadContext context, IDesktopPlugin plugin, ScopedDesktopPluginHost host,
        IReadOnlyList<DesktopPluginDependency> dependencies, IReadOnlyDictionary<string, LoadedPlugin?> resolvedProviders)
    {
        public string Id { get; } = id;
        public string PackageDirectory { get; } = packageDirectory;
        public string Version { get; } = version;
        public CancellationTokenSource Lifetime { get; } = new();
        public DesktopPluginInvocationLifetime Invocations { get; } = new();
        public TaskCompletionSource<string?> Retirement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? RetirementObservation { get; set; }
        public string? RetirementError { get; set; }
        public bool CleanupStarted { get; set; }
        public bool ReloadAfterRetirement { get; set; }
        public PluginLoadContext Context { get; } = context;
        public IDesktopPlugin Plugin { get; } = plugin;
        public ScopedDesktopPluginHost Host { get; } = host;
        public IReadOnlyList<DesktopPluginDependency> Dependencies { get; } = dependencies.ToArray();
        public IReadOnlyDictionary<string, LoadedPlugin?> ResolvedProviders { get; } = resolvedProviders;
        public List<DesktopHostedPanel> Panels { get; } = [];
        public List<DesktopHostedAction> Actions { get; } = [];
        public List<DesktopHostedTextEntryAction> TextEntryActions { get; } = [];
        public List<DesktopMountedContribution> Mounts { get; } = [];
        public List<DesktopHostedInputMode> InputModes { get; } = [];
        public List<DesktopHostedComposerCommand> ComposerCommands { get; } = [];
        public List<DesktopHostedConversationWorkspace> ConversationWorkspaces { get; } = [];
        public List<DesktopHostedConversationSettings> ConversationSettings { get; } = [];
        public HashSet<HostedConversationSettingsEditor> SettingsEditors { get; } = [];
        public HashSet<Task> SettingsOpens { get; } = [];
        public Exception? SettingsCleanupError { get; set; }
        public List<IDesktopConversationSettingsEditor> FailedSettingsEditors { get; } = [];
        public System.Runtime.CompilerServices.ConditionalWeakTable<IDesktopConversationSettingsEditor, object> SettingsEditorIdentities { get; } = new();
        public System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> SettingsViewIdentities { get; } = new();
        public List<DesktopHostedAgentTemplate> AgentTemplates { get; } = [];
    }

    private sealed class PluginLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var sdk = typeof(IDesktopPlugin).Assembly;
            if (assemblyName.Name == sdk.GetName().Name)
            {
                if (assemblyName.Version?.Major != sdk.GetName().Version?.Major)
                    throw new InvalidDataException("The plugin references an incompatible desktop SDK assembly.");
                return sdk;
            }
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
