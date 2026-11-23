using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Desktop.Plugins;
using Cerebral.PackageSettings;

namespace Cerebral.ResourcePausePlugin;

public sealed partial class ResourcePausePlugin
{
    private FrameworkElement CreateLocalModelPanel()
    {
        var root = PanelRoot();
        root.Children.Add(Heading("Local model preference"));
        root.Children.Add(Explanation("Save the desired local model here. Loading, preparing or restoring preferences does not activate a model. Activate saved local model is a separate explicit action using the Worker's resource and engine checks."));
        var selector = new ComboBox { DisplayMemberPath = nameof(LocalModelChoice.Display),
            SelectedValuePath = nameof(LocalModelChoice.Id), MinWidth = 320, MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(selector, "ResourcePause.LocalModelSelector");
        var activate = ActionButton("Activate saved local model", "ResourcePause.UseLocalModel");
        var refresh = ActionButton("Refresh models", "ResourcePause.RefreshModels");
        var status = StatusText("Loading local model registrations…", "ResourcePause.LocalModelStatus");
        var row = new WrapPanel(); row.Children.Add(activate); row.Children.Add(refresh);
        root.Children.Add(selector); root.Children.Add(row); root.Children.Add(status);
        IReadOnlyList<LocalModelChoice> models = [];
        ResourcePausePreferenceDocument? preparedLegacy = null;
        ResourcePausePreferences? preparedValue = null;
        var legacyNeedsReconciliation = false;
        void Select(string? id)
        {
            var choices = models.ToList();
            if (id is not null && !choices.Any(value => value.Id == id))
                choices.Add(new(id, "Unavailable saved local model", "unavailable", []));
            selector.ItemsSource = choices; selector.SelectedValue = id;
        }
        void Hydrate(ResourcePausePreferences value)
        {
            if (!ReferenceEquals(value, preparedValue)) { preparedLegacy = null; preparedValue = null; legacyNeedsReconciliation = false; }
            Select(value.PreferredLocalModelId);
        }
        ResourcePausePreferences PrepareLegacy()
        {
            if (preferenceStore is null) throw new NotSupportedException("The legacy plugin data directory is unavailable.");
            if (centralPreferences!.State is not { } state || state.Active is not null || state.Pending.Count != 0)
                throw new InvalidOperationException("Saved central preferences already exist; keep them or explicitly review a new choice.");
            preparedLegacy = preferenceStore.Load();
            legacyNeedsReconciliation = false;
            preparedValue = preparedLegacy.Preferences;
            return preparedValue;
        }
        ResourcePausePreferences Draft()
        {
            if (legacyNeedsReconciliation)
                throw new InvalidOperationException("Load or prepare preferences again before another save attempt.");
            if (preparedLegacy is not null)
            {
                var expected = preparedLegacy.Revision;
                preparedLegacy = null; preparedValue = null; legacyNeedsReconciliation = true; // Preparation is consumed before a save attempt.
                if (preferenceStore is null || preferenceStore.Load().Revision != expected)
                    throw new InvalidOperationException("The legacy preference file changed. Prepare it again before saving.");
            }
            return new(selector.SelectedValue as string);
        }
        async Task ReloadModelsAsync()
        {
            refresh.IsEnabled = false;
            try
            {
                models = await ReadLocalModelsAsync(lifetime.Token);
                if (CallbacksRetired) return;
                Select(selector.SelectedValue as string);
                status.Text = models.Count == 0 ? "No usable local text model is registered."
                    : "Model registrations loaded. Save preferences first; activation is a separate action.";
            }
            finally { if (!CallbacksRetired) refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await RunUiAsync(ReloadModelsAsync);
        activate.Click += async (_, _) => await RunUiAsync(async () =>
        {
            await ActivatePreferredLocalAsync(lifetime.Token);
            if (!CallbacksRetired) status.Text = "The saved local preference was explicitly activated.";
        });
        var editor = new PackagePreferenceEditor<ResourcePausePreferences>(centralPreferences!, Draft, Hydrate,
            PrepareLegacy, "ResourcePause", _ => { preparedLegacy = null; preparedValue = null; legacyNeedsReconciliation = false; },
            "Preferences saved. Model activation remains a separate action.");
        _ = RunUiAsync(ReloadModelsAsync);
        return new ScrollViewer { Content = editor.Create(root), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Task ActivatePreferredLocalAsync(CancellationToken cancellationToken) => ActivateLocalAsync(null, cancellationToken);

    private async Task ActivateLocalAsync(string? explicitModelId, CancellationToken cancellationToken)
    {
        await (centralPreferences ?? throw new InvalidOperationException("Shared local model preferences are unavailable.")).ReadAsync(cancellationToken);
        await ownerActionGate.WaitAsync(cancellationToken);
        BeginEngineTransition();
        BeginResourceTransition();
        try
        {
            SetPending("Preparing local GPU…", "Pending: holding engine admission while a capable local default and local GPU availability are verified.");
            var resource = await ServiceAsync<ResourceStatus>("resources.local.status", new { }, cancellationToken);
            ValidateResource(resource);
            if (resource.State is not ("active" or "paused") || resource.State == "paused" && resource.ActiveLocalAssignments != 0)
                throw new InvalidOperationException("Local GPU activation requires a settled active state or a paused state with zero active local assignments.");
            var settings = await ServiceAsync<HostDefaults>("settings.get", new { }, cancellationToken);
            var models = await ReadLocalModelsAsync(cancellationToken);
            var model = SelectLocalModel(explicitModelId, settings, models);
            var engine = await ReadEngineAsync(cancellationToken);
            EnsureHealthyEngine(engine);
            var restoreEngine = !engine.Paused;
            if (restoreEngine)
            {
                SetEnginePending("Holding engine admission for local activation…");
                engine = await ServiceAsync<EngineViewStatus>("engine.pause", new { expectedEpoch = engine.Epoch }, cancellationToken);
                EnsureHeldEngine(engine);
            }
            ApplyEngine(engine);

            if (resource.State == "paused")
            {
                resource = await ServiceAsync<ResourceStatus>("resources.local.resume", new { }, cancellationToken);
                ValidateResource(resource);
                if (resource.State != "active")
                    throw new InvalidOperationException("The Worker accepted local activation but did not confirm active local GPU admission.");
                Apply(resource);
            }

            _ = await ServiceRawAsync("models.default.switch",
                new { modelId = model.Id, modelWorkerId = "local", retainAcceptedBindings = true }, cancellationToken);
            var confirmed = await ServiceAsync<HostDefaults>("settings.get", new { }, cancellationToken);
            ValidateDefaults(confirmed);
            if (!string.Equals(confirmed.DefaultModelWorkerId, "local", StringComparison.Ordinal) ||
                !string.Equals(confirmed.DefaultModelId, model.Id, StringComparison.Ordinal))
                throw new InvalidOperationException("The default-model operation returned, but the saved local default does not match the requested model. Refresh before retrying.");

            currentDefaults = confirmed;
            defaultRouteError = null;
            var held = await ReadEngineAsync(cancellationToken);
            if (held.Epoch != engine.Epoch || !held.Paused)
                throw new InvalidOperationException("Engine state changed while the local default was being verified. It was not resumed automatically.");
            EnsureHealthyEngine(held);
            if (restoreEngine)
            {
                var resumed = await ServiceAsync<EngineViewStatus>("engine.resume", new { expectedEpoch = held.Epoch }, cancellationToken);
                EnsureHealthyEngine(resumed);
                if (resumed.Paused) throw new InvalidOperationException("The engine resume request did not confirm active admission.");
                ApplyEngine(resumed);
            }
            else ApplyEngine(held);

            var final = await ServiceAsync<ResourceStatus>("resources.local.status", new { }, cancellationToken);
            ValidateResource(final);
            pending = null;
            Apply(final);
            ReportHostStatus($"Local GPU is active with {model.Name}. Explicitly pinned profiles were left to the Worker's validated switch policy.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = Bound("The local-model transition was not fully confirmed. Engine admission remains held when the hold was established; no ambiguous model write was replayed. " + error.Message);
            SetAttention("Local activation incomplete", message);
            throw new ResourceTransitionException(message, error);
        }
        finally { EndResourceTransition(); EndEngineTransition(); ownerActionGate.Release(); }
    }

    private static bool HasLocalDefault(HostDefaults? settings) =>
        settings?.DefaultModelWorkerId == "local" && !string.IsNullOrEmpty(settings.DefaultModelId);

    private static string DefaultRouteDetail(HostDefaults? defaults, string? error)
    {
        if (defaults is null) return error ?? "The preferred model route has not been confirmed. Refresh status.";
        if (HasLocalDefault(defaults))
            return $"The preferred model route is local: {defaults.DefaultModelId}.";
        var route = string.IsNullOrEmpty(defaults.DefaultModelId)
            ? "No default model is selected."
            : $"The preferred model route is remote: {defaults.DefaultModelId} on {defaults.DefaultModelWorkerId}.";
        return route + " Activate the preferred local model for new default-following work. Explicit profile pins and accepted runs keep their routes.";
    }

    private async Task<IReadOnlyList<LocalModelChoice>> ReadLocalModelsAsync(CancellationToken cancellationToken)
    {
        var models = await ServiceAsync<LocalModelRecord[]>("models.list", new { }, cancellationToken);
        if (models.Length > 256) throw new InvalidDataException("The local model catalog exceeds the Resource Pause display bound.");
        var duplicateIds = models.GroupBy(model => model.Id, StringComparer.Ordinal).Where(group => group.Count() != 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var result = new List<LocalModelChoice>();
        foreach (var model in models)
        {
            if (duplicateIds.Contains(model.Id) || !ValidId(model.Id) || string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 160 ||
                model.State is not ("loaded" or "unloaded") || !string.IsNullOrWhiteSpace(model.Error) || model.CapabilityStatus is null)
                continue;
            var text = model.CapabilityStatus.Where(capability => capability.Id == "text-generation").ToArray();
            if (text.Length != 1 || !text[0].Usable) continue;
            var capabilities = model.CapabilityStatus.Where(capability => capability.Usable && ValidId(capability.Id))
                .Select(capability => capability.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            result.Add(new(model.Id, model.Name.Trim(), model.State, capabilities));
        }
        return result.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ThenBy(model => model.Id, StringComparer.Ordinal).ToArray();
    }

    private LocalModelChoice SelectLocalModel(string? explicitModelId, HostDefaults settings, IReadOnlyList<LocalModelChoice> models)
    {
        ValidateDefaults(settings);
        if (models.Count == 0) throw new InvalidOperationException("No local model has complete usable text-generation evidence.");
        var chosen = explicitModelId;
        if (chosen is null)
            chosen = (centralPreferences ?? throw new InvalidOperationException("Load saved local model preferences first.")).Current.PreferredLocalModelId;
        if (chosen is null && settings.DefaultModelWorkerId == "local" && models.Any(model => model.Id == settings.DefaultModelId))
            chosen = settings.DefaultModelId;
        if (chosen is null && models.Count == 1) chosen = models[0].Id;
        if (chosen is null)
            throw new InvalidOperationException("More than one capable local model is available. Choose the preferred model in Advanced configuration before activating the local GPU.");
        return models.SingleOrDefault(model => model.Id == chosen)
            ?? throw new InvalidOperationException("The saved or selected local model is no longer a usable text-generation candidate. Refresh Advanced configuration and choose again.");
    }

    private async Task<DesktopServiceCommandResult> ServiceRawAsync(string operation, object arguments, CancellationToken cancellationToken)
    {
        var target = service ?? throw new InvalidOperationException("The Worker service connection is unavailable.");
        var result = await target.ExecuteServiceCommandAsync(operation, arguments, cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? operation + " failed.");
        return result;
    }

    private async Task<T> ServiceAsync<T>(string operation, object arguments, CancellationToken cancellationToken)
    {
        var result = await ServiceRawAsync(operation, arguments, cancellationToken);
        try { return result.Data.Deserialize<T>(Json) ?? throw new InvalidDataException(operation + " returned no usable data."); }
        catch (JsonException error) { throw new InvalidDataException(operation + " returned malformed data.", error); }
    }

    private static void ValidateDefaults(HostDefaults settings)
    {
        if (!ValidId(settings.DefaultModelWorkerId) || settings.DefaultModelId.Length > 100 ||
            settings.DefaultModelId.Length > 0 && !ValidId(settings.DefaultModelId))
            throw new InvalidDataException("settings.get returned invalid default-model identity.");
    }

    private static bool ValidId(string? value) => value is { Length: >= 1 and <= 100 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static void ValidateResource(ResourceStatus status)
    {
        if (status.State is not ("active" or "draining" or "paused" or "resuming" or "faulted") ||
            status.ActiveLocalAssignments < 0 || status.QueuedLocalAssignments < 0 || status.QueuedLocalWorkflows < 0 ||
            status.Peers is null || status.ModelsToReload is null || status.Detail is null)
            throw new InvalidDataException("The Worker returned an incomplete local resource status.");
    }

    private static string Bound(string text) => string.IsNullOrWhiteSpace(text) ? "The operation failed without a diagnostic." :
        text.Length <= 700 ? text : text[..700] + "…";

    private sealed class HostDefaults
    {
        public string DefaultModelId { get; init; } = "";
        public string DefaultModelWorkerId { get; init; } = "";
    }

    private sealed class LocalModelRecord
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string State { get; init; } = "";
        public string? Error { get; init; }
        public IReadOnlyList<LocalCapability>? CapabilityStatus { get; init; }
    }

    private sealed class LocalCapability
    {
        public string Id { get; init; } = "";
        public bool Usable { get; init; }
    }

    private sealed record LocalModelChoice(string Id, string Name, string State, IReadOnlyList<string> Capabilities)
    {
        public string Display => $"{Name} · {Id} · {State} · {string.Join(", ", Capabilities)}";
    }
}
