using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.SkillsPlugin;

internal sealed record SkillImportReport(IReadOnlyList<string> Outcomes, string? FollowupWarning);
internal sealed record SkillsOwner(string InstallationId, string Generation, IReadOnlyList<SkillSummary> Skills);
internal sealed record SkillManagerRow(SkillFolderEntry Entry, SkillSummary? Catalog, string State, string Group, string Detail)
{
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Version => Entry.Version;
    public string Origin => Entry.Origin == "bundled" ? "Bundled" : "Editable / imported";
    public string Assignments => Catalog is null || Catalog.Bindings.Count == 0 ? "Unassigned" :
        string.Join(", ", Catalog.Bindings.Select(x => x.Scope == "global" ? "global" : x.Scope + ":" + x.Target));
}
internal sealed record SkillsManagerSnapshot(IReadOnlyList<SkillManagerRow> Rows, string EditableRoot,
    string BundledRoot, DateTimeOffset? LastScan, bool Watching, bool Busy, string Message, string? InstallationId,
    bool NeedsOwnerReview, IReadOnlyDictionary<string, string[]> Groups);

public sealed class SkillsPlugin : IDesktopPlugin, IDesktopConfigurationPlugin
{
    public const string PackageId = "pegasus.skills";
    private IDesktopPluginHost? host;
    private IDesktopServiceHost? service;
    private SkillFolderCatalog? folders;
    private Dispatcher? dispatcher;
    private DispatcherTimer? debounce;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly List<WeakReference<SkillsManager>> views = [];
    private SkillsIndex index = new();
    private SkillScan? scan;
    private SkillsOwner? owner;
    private Window? window;
    private bool busy, disposed, refreshPending;
    private bool watcherRequested = true;
    private int queued;
    private string message = "Scanning selected skill folders…";
    public IReadOnlyList<DesktopPanel> Panels => [];
    public IReadOnlyList<DesktopAction> ComposerActions => [new("skills-manager", "Skills", OpenWindowAsync, "Manage instruction skills")];
    public IReadOnlyList<DesktopPanel> ConfigurationPanels => [new("skills-manager", "Skills", CreateView)];
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot _) => null;

    public void Initialize(IDesktopPluginHost value)
    {
        if (host is not null || disposed) throw new InvalidOperationException("This Skills manager cannot be initialized twice.");
        if (value is not IDesktopPluginEnvironment environment || value is not IDesktopServiceHost commands)
            throw new InvalidOperationException("Skills requires the existing desktop environment and owner-command host.");
        host = value; service = commands; dispatcher = Dispatcher.CurrentDispatcher;
        folders = new(environment.PackageDirectory, environment.DataDirectory);
        debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        debounce.Tick += OnRefreshTimer;
        _ = RefreshSafelyAsync();
    }

    internal SkillsManagerSnapshot Snapshot()
    {
        var rows = (scan?.Entries ?? []).Select(entry =>
        {
            var id = entry.Id;
            var current = owner?.Skills.SingleOrDefault(x => x.Id == id);
            index.Skills.TryGetValue(id, out var owned);
            var activeSource = owned?.SourceKey == entry.Key;
            var excluded = SkillFolderCatalog.IsExcluded(index, entry);
            var state = entry.Incomplete ? "Scan incomplete — catalog unchanged" : entry.Error is not null ? "Invalid / missing" :
                activeSource && owned!.Pending ? "Unconfirmed outcome" :
                excluded ? "Excluded — explicit Install required" :
                current is null ? activeSource ? "Installed — Worker inspection unavailable" : "Discovered — not installed" :
                !activeSource || current.Source != SkillFolderCatalog.Source(entry.Key) ? "ID conflict / other catalog source" :
                current.Enabled ? current.Bindings.Count == 0 ? "Installed — enabled but unassigned" : "Installed — enabled for assigned scope" : "Installed — disabled";
            var group = index.Groups.FirstOrDefault(x => x.Value.Contains(id, StringComparer.Ordinal)).Key ??
                (entry.Key.StartsWith("bundled/CerebralBase/", StringComparison.Ordinal) ? "Cerebral base" : "Ungrouped");
            return new SkillManagerRow(entry, current, state, group,
                entry.Error ?? (activeSource ? owned?.Diagnostic : null) ?? entry.Document?.Manifest.Description ?? "");
        }).ToArray();
        return new(rows, folders?.EditableRoot ?? "", folders?.BundledRoot ?? "", scan?.At,
            watchers.Count != 0, busy, message, owner?.InstallationId,
            index.OwnerInstallationId is not null && index.OwnerInstallationId != owner?.InstallationId,
            index.Groups.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal));
    }

    private FrameworkElement CreateView()
    {
        RequireActive();
        var view = new SkillsManager(this);
        views.Add(new(view));
        view.Update(Snapshot());
        return view;
    }
    private Task OpenWindowAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RequireActive();
        if (window is not null) { window.Show(); window.Activate(); return Task.CompletedTask; }
        window = new Window
        {
            Title = "Skills · Pegasus Cerebral", Width = 1040, Height = 700, MinWidth = 760, MinHeight = 480,
            Content = CreateView(), FontSize = 13, FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
        };
        window.SetResourceReference(Window.BackgroundProperty, SystemColors.ControlBrushKey);
        window.SetResourceReference(Window.ForegroundProperty, SystemColors.ControlTextBrushKey);
        window.Closed += (_, _) => { window = null; StopWatching(); Publish(); };
        window.Show();
        return Task.CompletedTask;
    }

    internal void ViewLoaded() { if (!disposed) { watcherRequested = true; StartWatching(); ScheduleRefresh(); } }
    internal void ViewUnloaded()
    {
        if (!disposed && !views.Any(x => x.TryGetTarget(out var view) && view.IsLoaded)) { StopWatching(); Publish(); }
    }
    internal void SetMessage(string value) { message = SkillFolderCatalog.Bound(value); Publish(); }
    private void Publish()
    {
        if (disposed) return;
        var snapshot = Snapshot();
        foreach (var reference in views.ToArray())
        {
            if (reference.TryGetTarget(out var view)) view.Update(snapshot);
            else views.Remove(reference);
        }
    }
    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        dispatcher?.VerifyAccess();
    }

    private async Task<T> ReadAsync<T>(string operation, object args)
    {
        RequireActive(); lifetime.Token.ThrowIfCancellationRequested();
        var generation = host!.GetConversation().Binding.Generation;
        var reply = await service!.ExecuteServiceCommandAsync(operation, args, lifetime.Token);
        RequireActive(); lifetime.Token.ThrowIfCancellationRequested();
        if (generation != host.GetConversation().Binding.Generation)
            throw new InvalidOperationException("The owner connection changed. Refresh before another operation.");
        if (!reply.Success) throw new InvalidOperationException(SkillFolderCatalog.Bound(reply.Error ?? "The owner command failed."));
        if (reply.Data.GetRawText().Length > 2 * 1024 * 1024) throw new InvalidDataException("The owner reply exceeds its bound.");
        return JsonSerializer.Deserialize<T>(reply.Data, CerebralJson.Options)
            ?? throw new InvalidDataException("The owner command returned no result.");
    }

    private async Task<SkillsOwner> ReadOwnerAsync()
    {
        var generation = host!.GetConversation().Binding.Generation;
        var health = await ReadAsync<HostInfo>("health", new { });
        if (health.Product != "Pegasus Cerebral" || health.Protocol != 1 ||
            string.IsNullOrWhiteSpace(health.InstallationId) || health.InstallationId.Length > 128)
            throw new InvalidDataException("The current Worker does not provide the required installation identity.");
        var list = await ReadAsync<List<SkillSummary>>("skills.list", new { });
        if (list.Count > SkillDocument.MaximumSkills || list.Any(x => x is null) || list.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != list.Count ||
            generation != host.GetConversation().Binding.Generation)
            throw new InvalidDataException("The skills owner/catalog changed or exceeds its bound.");
        return new(health.InstallationId, generation, list);
    }

    private void RequireOwner(SkillsIndex state, SkillsOwner current, bool initialExplicitAction = false)
    {
        if (host!.GetConversation().Binding.Generation != current.Generation)
            throw new InvalidOperationException("The selected owner connection changed.");
        if (state.OwnerInstallationId is null && state.Skills.Count == 0 && initialExplicitAction)
        { state.OwnerInstallationId = current.InstallationId; folders!.SaveIndex(state); }
        if (state.OwnerInstallationId != current.InstallationId)
            throw new InvalidOperationException("Review the selected Worker connection before publishing or removing skills.");
    }

    private async Task WithWriterAsync(Func<SkillsIndex, SkillsOwner, Task> action, bool scanLocalFirst = false)
    {
        RequireActive();
        await operations.WaitAsync(lifetime.Token);
        try
        {
            RequireActive(); busy = true; Publish();
            using var lease = folders!.AcquireWriter();
            var state = folders.ReadIndex();
            index = state;
            if (scanLocalFirst)
            {
                scan = await Task.Run(() => folders.Scan(state, lifetime.Token), lifetime.Token);
                RequireActive(); StartWatching(); Publish();
            }
            owner = null;
            var current = await ReadOwnerAsync();
            owner = current;
            await action(state, current);
        }
        finally
        {
            busy = false; operations.Release(); Publish();
            if (refreshPending && !disposed && watcherRequested) { refreshPending = false; ScheduleRefresh(); }
        }
    }

    internal async Task RefreshAsync()
    {
        await WithWriterAsync(async (state, current) =>
        {
            RequireActive();
            var localScan = scan ?? throw new InvalidOperationException("Refresh requires a local scan result.");
            if (state.OwnerInstallationId == current.InstallationId)
            {
                foreach (var (id, owned) in state.Skills.ToArray())
                {
                    var summary = current.Skills.SingleOrDefault(x => x.Id == id);
                    if (owned.Pending)
                    {
                        if (summary is not null && owned.PendingKind != "remove")
                        {
                            var inspection = await ReadAsync<SkillInspection>("skills.inspect", new { id });
                            if (owned.PendingKind == "install" && inspection.Summary.Source == SkillFolderCatalog.Source(owned.PendingSourceKey!) &&
                                CoreHash(inspection) == owned.PendingHash && inspection.Summary.Revision > owned.Revision)
                                AcceptInstall(state, id, owned, inspection);
                            else if (owned.PendingKind == "configure" && SkillFolderCatalog.MatchesOwned(state, current.InstallationId, id, inspection) &&
                                inspection.Summary.Revision > owned.Revision && inspection.Summary.Enabled == owned.PendingEnabled &&
                                SameBindings(inspection.Summary.Bindings, owned.PendingBindings ?? []))
                            { owned.Revision = inspection.Summary.Revision; ClearPending(owned); folders!.SaveIndex(state); }
                        }
                        continue; // Never retry an unconfirmed mutation.
                    }
                    if (summary is null)
                    {
                        var missing = localScan.Entries.FirstOrDefault(x => x.Key == owned.SourceKey) ??
                            new SkillFolderEntry(owned.SourceKey, Origin(owned.SourceKey), folders!.DirectoryFor(owned.SourceKey), null, null, id);
                        SkillFolderCatalog.Suppress(state, missing, "Removed through the owner catalog; explicit Install is required.");
                        state.Skills.Remove(id); folders!.SaveIndex(state);
                        continue;
                    }
                    var existing = await ReadAsync<SkillInspection>("skills.inspect", new { id });
                    if (!SkillFolderCatalog.MatchesOwned(state, current.InstallationId, id, existing))
                    { owned.Diagnostic = "Catalog source/content changed outside this manager; automatic changes are stopped."; continue; }
                    owned.Revision = existing.Summary.Revision; // Preserve explicit owner changes to enablement/bindings.
                    var row = localScan.Entries.SingleOrDefault(x => x.Key == owned.SourceKey);
                    if ((row is null && localScan.Complete || row?.Error is not null && !row.Incomplete) && existing.Summary.Enabled)
                    {
                        await ConfigureOneAsync(state, current, id, owned, existing, false, existing.Summary.Bindings);
                        owned.Diagnostic = "Source unavailable. Fix it, then explicitly Enable.";
                    }
                    else if (row?.Document is { } document && !SkillFolderCatalog.IsExcluded(state, row) &&
                        document.Fingerprint != owned.DocumentHash)
                    {
                        if (SkillFolderCatalog.CatalogHash(document.Manifest, document.Content) != owned.CatalogHash)
                            await PublishOneAsync(state, current, row, existing, null);
                        else owned.DocumentHash = document.Fingerprint;
                    }
                }
                folders!.SaveIndex(state);
            }
            owner = await ReadOwnerAsync();
            message = localScan.Complete ? "Scan complete. New discoveries are not installed; Install then Enable for an explicit scope."
                : "Scan incomplete; missing-source removals were not inferred. " + localScan.Error;
            StartWatching();
        }, scanLocalFirst: true);
    }
    private async Task RefreshSafelyAsync()
    {
        if (busy) { refreshPending = true; return; }
        try { await RefreshAsync(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { if (!disposed) SetMessage("Refresh stopped: " + error.Message); }
    }

    internal async Task ReviewConnectionAsync(string expectedInstallationId)
    {
        await WithWriterAsync(async (state, current) =>
        {
            if (current.InstallationId != expectedInstallationId) throw new InvalidOperationException("The reviewed connection changed.");
            if (state.Skills.Values.Any(x => x.Pending)) throw new InvalidOperationException("Resolve unconfirmed outcomes on their original Worker before changing ownership.");
            foreach (var (id, record) in state.Skills)
            {
                var found = current.Skills.SingleOrDefault(x => x.Id == id);
                if (found is null) { record.Diagnostic = "No matching record on this Worker. Explicit Install is required."; continue; }
                var inspection = await ReadAsync<SkillInspection>("skills.inspect", new { id });
                var matching = inspection.Summary.Source == SkillFolderCatalog.Source(record.SourceKey) &&
                    CoreHash(inspection) == record.CatalogHash && inspection.Summary.Revision >= record.Revision;
                record.Diagnostic = matching ? null : "Different catalog content was preserved; no automatic update/removal is allowed.";
            }
            state.OwnerInstallationId = current.InstallationId;
            folders!.SaveIndex(state);
            message = "Connection reviewed. Each operation still checks current revision, source and content.";
        });
        await RefreshAsync();
    }

    internal async Task InstallAsync(IReadOnlyList<SkillManagerRow> rows)
    {
        await WithWriterAsync(async (state, current) =>
        {
            RequireOwner(state, current, initialExplicitAction: true);
            foreach (var row in rows)
            {
                var document = row.Entry.Document ?? throw new InvalidOperationException("Fix the document before installing.");
                var fresh = SkillDocument.Read(row.Entry.Directory, row.Entry.Key, document.Manifest);
                if (fresh.Fingerprint != document.Fingerprint) throw new IOException("The skill changed after preview.");
                var found = (await ReadOwnerAsync()).Skills.SingleOrDefault(x => x.Id == document.Manifest.Id);
                SkillInspection? inspection = found is null ? null :
                    await ReadAsync<SkillInspection>("skills.inspect", new { id = document.Manifest.Id });
                if (inspection is not null && !SkillFolderCatalog.MatchesOwned(state, current.InstallationId, document.Manifest.Id, inspection))
                    throw new InvalidOperationException("This ID belongs to other catalog content. Use reviewed group conflict choices or a separate ID.");
                await PublishOneAsync(state, current, row.Entry, inspection, null);
            }
            message = "Installation completed. New skills remain disabled and unassigned; existing assignments were preserved.";
        });
        await RefreshAsync();
    }

    internal async Task ConfigureAsync(IReadOnlyList<SkillManagerRow> rows, bool enabled, SkillBinding? selectedScope)
    {
        await WithWriterAsync(async (state, current) =>
        {
            RequireOwner(state, current);
            foreach (var row in rows)
            {
                if (enabled && (row.Entry.Document is null || SkillFolderCatalog.IsExcluded(state, row.Entry)))
                    throw new InvalidOperationException("Install a valid, non-excluded skill before enabling it.");
                var record = state.Skills.GetValueOrDefault(row.Id) ?? throw new InvalidOperationException("Install this skill first.");
                if (record.SourceKey != row.Entry.Key || record.Pending) throw new InvalidOperationException("This source is not the confirmed installed skill.");
                var inspection = await ReadAsync<SkillInspection>("skills.inspect", new { id = row.Id });
                if (!SkillFolderCatalog.MatchesOwned(state, current.InstallationId, row.Id, inspection))
                    throw new InvalidOperationException("Catalog ownership changed; no configuration was sent.");
                if (enabled)
                {
                    var fresh = SkillDocument.Read(row.Entry.Directory, row.Entry.Key, record.Identity);
                    if (fresh.Fingerprint != row.Entry.Document!.Fingerprint ||
                        SkillFolderCatalog.CatalogHash(fresh.Manifest, fresh.Content) != record.CatalogHash)
                        throw new IOException("The definition changed after preview. Install/update it before Enable.");
                }
                IReadOnlyList<SkillBinding> bindings = selectedScope is null || !enabled
                    ? inspection.Summary.Bindings : [ValidateBinding(selectedScope)];
                if (enabled && bindings.Count == 0) throw new InvalidOperationException("Select an explicit scope before enabling an unassigned skill.");
                await ConfigureOneAsync(state, current, row.Id, record, inspection, enabled, bindings);
            }
            message = enabled ? "Selected skills enabled for their explicit assignments." : "Selected skills disabled. Their definitions and assignments remain stored.";
        });
        await RefreshAsync();
    }

    internal async Task UninstallAsync(IReadOnlyList<SkillManagerRow> rows, bool removeUnchangedImportedFiles)
    {
        await WithWriterAsync(async (state, current) =>
        {
            RequireOwner(state, current);
            foreach (var row in rows)
            {
                var record = state.Skills.GetValueOrDefault(row.Id) ?? throw new InvalidOperationException("This skill is not owned by the current manager index.");
                if (record.SourceKey != row.Entry.Key || record.Pending) throw new InvalidOperationException("This source has no confirmed removable installation.");
                var inspection = await ReadAsync<SkillInspection>("skills.inspect", new { id = row.Id });
                if (!SkillFolderCatalog.MatchesOwned(state, current.InstallationId, row.Id, inspection))
                    throw new InvalidOperationException("Current catalog source/revision/content differs; it was preserved.");
                SkillFolderCatalog.Suppress(state, row.Entry, "Intentionally uninstalled. Explicit Install is required.");
                record.Pending = true; record.PendingKind = "remove"; folders!.SaveIndex(state);
                try
                {
                    var result = await ReadAsync<JsonElement>("skills.remove", new { id = row.Id, expectedRevision = inspection.Summary.Revision });
                    if (!result.TryGetProperty("id", out var id) || id.GetString() != row.Id ||
                        !result.TryGetProperty("removed", out var removed) || removed.ValueKind != JsonValueKind.True)
                        throw new InvalidDataException("Uninstall acknowledgment is invalid.");
                    state.Skills.Remove(row.Id); folders.SaveIndex(state);
                    if (removeUnchangedImportedFiles) folders.RemoveManagedFiles(record);
                }
                catch { message = "Uninstall outcome requires inspection on the original Worker. It will not be retried or reinstalled."; throw; }
            }
            message = "Uninstalled. Bundled, modified and unknown files remain; unchanged import-owned files were removed only if selected.";
        });
        await RefreshAsync();
    }

    private async Task PublishOneAsync(SkillsIndex state, SkillsOwner current, SkillFolderEntry entry,
        SkillInspection? existing, IReadOnlyDictionary<string, string>? managedFiles)
    {
        RequireOwner(state, current);
        var doc = entry.Document ?? throw new InvalidOperationException("A valid document is required.");
        var id = doc.Manifest.Id;
        var record = state.Skills.GetValueOrDefault(id);
        if (record?.Pending == true) throw new InvalidOperationException("An earlier operation is unconfirmed; inspect it without replay.");
        if (state.Skills.Any(x => x.Key != id && x.Value.SourceKey == entry.Key))
            throw new InvalidOperationException("This source already has another installed ID. Uninstall that original first.");
        record ??= new() { SourceKey = entry.Key };
        state.Skills[id] = record;
        record.Pending = true; record.PendingKind = "install";
        record.PendingSourceKey = entry.Key; record.PendingHash = SkillFolderCatalog.CatalogHash(doc.Manifest, doc.Content);
        record.PendingDocumentHash = doc.Fingerprint;
        record.PendingIdentity = doc.HasManifest ? null : SkillDocument.Copy(doc.Manifest);
        record.PendingFiles = managedFiles?.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase) ??
            (record.SourceKey == entry.Key ? record.ManagedFiles : new(StringComparer.OrdinalIgnoreCase));
        folders!.SaveIndex(state); // Admission intent survives a lost reply; refresh never resubmits it.
        try
        {
            var reply = await ReadAsync<SkillInspection>("skills.install", new
            {
                manifest = doc.Manifest, content = doc.Content, source = SkillFolderCatalog.Source(entry.Key),
                expectedRevision = existing?.Summary.Revision
            });
            if (reply.Summary.Id != id || reply.Summary.Source != SkillFolderCatalog.Source(entry.Key) ||
                CoreHash(reply) != record.PendingHash || reply.Summary.Revision <= (existing?.Summary.Revision ?? 0))
                throw new InvalidDataException("Installed content differs from the reviewed document.");
            AcceptInstall(state, id, record, reply);
        }
        catch { message = "Install/update outcome is unconfirmed. Inspect the original catalog; no automatic retry will occur."; throw; }
    }

    private void AcceptInstall(SkillsIndex state, string id, OwnedSkill record, SkillInspection reply)
    {
        if (record.SourceKey != record.PendingSourceKey)
            state.Exclusions[record.SourceKey] = "Replaced by an explicitly reviewed source; old files were preserved.";
        record.SourceKey = record.PendingSourceKey!;
        record.Revision = reply.Summary.Revision; record.CatalogHash = CoreHash(reply);
        record.DocumentHash = record.PendingDocumentHash!; record.Identity = record.PendingIdentity;
        record.ManagedFiles = record.PendingFiles ?? new(StringComparer.OrdinalIgnoreCase);
        if (record.Identity is not null) state.SourceIdentities[record.SourceKey] = SkillDocument.Copy(record.Identity);
        state.Exclusions.Remove(record.SourceKey);
        state.SuppressedIds.Remove(Origin(record.SourceKey) + ":" + id);
        ClearPending(record); folders!.SaveIndex(state);
    }

    private async Task ConfigureOneAsync(SkillsIndex state, SkillsOwner current, string id, OwnedSkill record,
        SkillInspection inspection, bool enabled, IReadOnlyList<SkillBinding> bindings)
    {
        RequireOwner(state, current);
        if (record.Pending) throw new InvalidOperationException("A previous outcome is unconfirmed.");
        record.Revision = inspection.Summary.Revision;
        record.Pending = true; record.PendingKind = "configure"; record.PendingEnabled = enabled;
        record.PendingBindings = bindings.ToList(); folders!.SaveIndex(state);
        var reply = await ReadAsync<SkillInspection>("skills.configure",
            new { id, enabled, bindings, expectedRevision = inspection.Summary.Revision });
        if (reply.Summary.Id != id || reply.Summary.Revision <= inspection.Summary.Revision ||
            reply.Summary.Source != inspection.Summary.Source || CoreHash(reply) != record.CatalogHash ||
            reply.Summary.Enabled != enabled || !SameBindings(reply.Summary.Bindings, bindings))
            throw new InvalidDataException("Skill configuration acknowledgment differs from the requested state.");
        record.Revision = reply.Summary.Revision; ClearPending(record); folders!.SaveIndex(state);
    }

    internal async Task SaveGroupAsync(string name, IReadOnlyList<SkillManagerRow> rows)
    {
        await WithWriterAsync((state, _) =>
        {
            SkillFolderCatalog.SetGroup(state, name, rows.Select(x => x.Id).Where(x => x.Length != 0));
            folders!.SaveIndex(state); message = "Named group saved locally."; return Task.CompletedTask;
        });
    }

    internal async Task<(SkillsOwner Owner, IReadOnlyDictionary<string, SkillInspection> Existing)> ReviewImportAsync(SkillGroupArchive group)
    {
        RequireActive();
        var current = await ReadOwnerAsync();
        var found = new Dictionary<string, SkillInspection>(StringComparer.Ordinal);
        foreach (var id in group.Members.Select(x => x.Document.Manifest.Id))
            if (current.Skills.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                var actual = current.Skills.Single(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                found.Add(actual.Id, await ReadAsync<SkillInspection>("skills.inspect", new { id = actual.Id }));
            }
        return (current, found);
    }

    internal async Task<SkillImportReport> ImportAsync(SkillGroupArchive group, SkillImportPlan plan,
        SkillsOwner reviewedOwner, IReadOnlyDictionary<string, SkillInspection> reviewedExisting)
    {
        var outcomes = new List<string>();
        string? warning = null;
        try
        {
        await WithWriterAsync(async (state, current) =>
        {
            RequireOwner(state, current, initialExplicitAction: true);
            if (reviewedOwner.InstallationId != current.InstallationId || reviewedOwner.Generation != current.Generation)
                throw new InvalidOperationException("The import owner changed after preview; no content was extracted.");
            if (plan.GroupName != group.Name || plan.Items.Count != group.Members.Count ||
                plan.Items.Where((item, i) => !ReferenceEquals(item.Member, group.Members[i])).Any())
                throw new InvalidDataException("The import plan differs from its retained archive preview.");
            group.Extract(folders!.DataDirectory);
            var installed = new List<string>();
            foreach (var item in plan.Items)
            {
                if (item.Skip) { outcomes.Add(item.Manifest.Id + ": kept existing"); continue; }
                try
                {
                    var catalog = await ReadOwnerAsync();
                    if (catalog.InstallationId != reviewedOwner.InstallationId || catalog.Generation != reviewedOwner.Generation)
                        throw new InvalidOperationException("Connection changed; remaining import stopped.");
                    SkillInspection? currentSkill = null;
                    var found = catalog.Skills.SingleOrDefault(x => x.Id.Equals(item.Manifest.Id, StringComparison.OrdinalIgnoreCase));
                    if (found is not null)
                    {
                        currentSkill = await ReadAsync<SkillInspection>("skills.inspect", new { id = found.Id });
                        if (item.Existing is null || !reviewedExisting.TryGetValue(found.Id, out var old) ||
                            old.Summary.Revision != currentSkill.Summary.Revision || old.Summary.Source != currentSkill.Summary.Source ||
                            CoreHash(old) != CoreHash(currentSkill) || currentSkill.Summary.BuiltIn)
                            throw new InvalidOperationException("Catalog collision changed after preview; it was preserved.");
                    }
                    else if (item.Existing is not null) throw new InvalidOperationException("The reviewed replacement disappeared.");
                    var materialized = group.Materialize(item, folders);
                    await PublishOneAsync(state, current, materialized.Entry, currentSkill, materialized.Files);
                    installed.Add(item.Manifest.Id);
                    outcomes.Add(item.Manifest.Id + ": installed; previous state/assignments preserved when replacing");
                }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
                {
                    var disposition = state.Skills.GetValueOrDefault(item.Manifest.Id)?.Pending == true ? "unconfirmed" : "stopped";
                    outcomes.Add(item.Manifest.Id + ": " + disposition + " — " + SkillFolderCatalog.Bound(error.Message));
                    outcomes.AddRange(plan.Items.SkipWhile(x => !ReferenceEquals(x, item)).Skip(1)
                        .Select(x => x.Manifest.Id + ": not attempted after the preceding failure"));
                    break;
                }
            }
            var completed = installed.ToArray();
            if (completed.Length != 0) { SkillFolderCatalog.SetGroup(state, plan.GroupName, completed); folders.SaveIndex(state); }
            message = string.Join(" | ", outcomes.Take(3));
        });
        }
        catch (Exception error) when (outcomes.Count != 0 && error is not (OutOfMemoryException or StackOverflowException))
        {
            warning = "Follow-up local summary/index update failed: " + SkillFolderCatalog.Bound(error.Message);
        }
        try { await RefreshAsync(); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            var refresh = "Follow-up catalog refresh failed: " + SkillFolderCatalog.Bound(error.Message);
            warning = warning is null ? refresh : warning + "\n" + refresh;
        }
        // Item outcomes belong to admitted work; an independent refresh cannot replace or replay them.
        if (warning is not null) { message = "Import outcomes retained. " + warning; Publish(); }
        return new(outcomes.ToArray(), warning);
    }

    internal void Export(string name, IReadOnlyList<SkillManagerRow> rows, string destination, string? expectedHash)
    {
        RequireActive();
        if (folders is null) throw new InvalidOperationException("The selected folder is unavailable.");
        if (ScopedFilePolicy.IsWithin(Path.GetFullPath(destination), Path.GetDirectoryName(folders.BundledRoot)!))
            throw new UnauthorizedAccessException("Export into the replaceable plugin code payload is not allowed.");
        SkillGroups.Export(name, rows.Select(x => x.Entry).ToArray(), destination, expectedHash);
        message = "Selected definitions and every declared asset exported, including mandatory license notices."; Publish();
    }

    private static string CoreHash(SkillInspection value) => SkillFolderCatalog.CatalogHash(new()
    {
        Id = value.Summary.Id, Name = value.Summary.Name, Version = value.Summary.Version,
        Description = value.Summary.Description, Triggers = value.Summary.Triggers.ToList()
    }, value.Content);
    private static bool SameBindings(IReadOnlyList<SkillBinding> left, IReadOnlyList<SkillBinding> right) =>
        left.Select(x => x.Scope + ":" + x.Target).Order(StringComparer.Ordinal)
            .SequenceEqual(right.Select(x => x.Scope + ":" + x.Target).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    private static void ClearPending(OwnedSkill record)
    {
        record.Pending = false; record.PendingKind = null; record.PendingHash = null; record.PendingSourceKey = null;
        record.PendingDocumentHash = null; record.PendingIdentity = null; record.PendingFiles = null;
        record.PendingEnabled = null; record.PendingBindings = null; record.Diagnostic = null;
    }
    private static string Origin(string source) => source.StartsWith("bundled/", StringComparison.Ordinal) ? "bundled" : "data";
    internal static SkillBinding ValidateBinding(SkillBinding value)
    {
        if (value.Scope == "global" && string.IsNullOrEmpty(value.Target)) return new("global");
        if (value.Scope == "role" && value.Target is "lead" or "helper" or "compactor") return value;
        if (value.Scope is "profile" or "project" && value.Target is not null) { SkillFiles.ValidateId(value.Target); return value; }
        throw new InvalidDataException("Choose global, an exact lead/helper/compactor role, or an exact profile/project ID.");
    }

    private void OnRefreshTimer(object? sender, EventArgs args) { debounce!.Stop(); _ = RefreshSafelyAsync(); }
    private void ScheduleRefresh()
    {
        if (disposed || dispatcher is null || dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
        _ = dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref queued, 0);
            if (disposed) return;
            debounce!.Stop(); debounce.Start();
        });
    }
    private void OnFileChanged(object sender, FileSystemEventArgs args) => ScheduleRefresh();
    private void OnFileRenamed(object sender, RenamedEventArgs args) => ScheduleRefresh();
    private void OnWatchError(object sender, ErrorEventArgs args)
    {
        if (disposed || dispatcher?.HasShutdownStarted != false) return;
        _ = dispatcher.BeginInvoke(() => { if (!disposed) { message = "The watcher lost events. Use Refresh to inspect the complete folders."; Publish(); } });
        ScheduleRefresh();
    }
    private void StartWatching()
    {
        if (disposed || !watcherRequested || folders is null || watchers.Count != 0) return;
        foreach (var root in new[] { folders.EditableRoot, folders.BundledRoot })
        {
            if (!Directory.Exists(root)) continue;
            ScopedFilePolicy.RejectLinks(root);
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += OnFileChanged; watcher.Created += OnFileChanged; watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileRenamed; watcher.Error += OnWatchError;
            watchers.Add(watcher); watcher.EnableRaisingEvents = true;
        }
    }
    private void StopWatching()
    {
        watcherRequested = false;
        debounce?.Stop();
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileChanged; watcher.Created -= OnFileChanged; watcher.Deleted -= OnFileChanged;
            watcher.Renamed -= OnFileRenamed; watcher.Error -= OnWatchError; watcher.Dispose();
        }
        watchers.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; StopWatching();
        if (debounce is not null) debounce.Tick -= OnRefreshTimer;
        lifetime.Cancel(); lifetime.Dispose();
        window?.Close(); window = null;
        views.Clear();
        // No UI-thread wait. Any admitted command's durable pending marker remains for outcome inspection.
        // Closing/disabling this manager never changes core skill enablement or assignments.
    }
}
