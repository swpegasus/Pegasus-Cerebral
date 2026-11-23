using System.IO;
using System.Windows;
using System.Windows.Threading;
using Cerebral.Core;

namespace Cerebral.Desktop.Plugins;

/// <summary>Adapts native plugins to the application's existing conversation flow without exposing service credentials.</summary>
public sealed partial class DesktopPluginHostAdapter(
    Dispatcher dispatcher,
    Func<DesktopConversationSnapshot> conversation,
    Func<DesktopDraftSnapshot> draft,
    Func<DesktopDraftUpdate, CancellationToken, Task<DesktopOperationResult>> replaceDraft,
    Func<DesktopSubmission, CancellationToken, Task<DesktopOperationResult>> submit,
    Func<DesktopSubmission, CancellationToken, Task<DesktopOperationResult>> steer,
    Func<DesktopConversationBinding, CancellationToken, Task<DesktopOperationResult>> stop,
    Action<string> reportStatus,
    DesktopPluginSurface surface = DesktopPluginSurface.Orchestrator,
    string? pluginDataRoot = null,
    Func<string, object, CancellationToken, Task<DesktopServiceCommandResult>>? serviceCommand = null,
    Action? dismissInputGuidance = null,
    Func<string, DesktopWorkflowSubmission, CancellationToken, Task<DesktopOperationResult>>? submitWorkflow = null,
    Func<DesktopSubmission, bool>? hasSubmissionContent = null,
    Func<DesktopResponseBalanceSnapshot>? responseBalance = null,
    Func<DesktopConversationBinding, int?, CancellationToken, Task<DesktopOperationResult>>? setResponseBalance = null,
    Func<DesktopProductPaths>? productPaths = null,
    Func<string, CancellationToken, Task<DesktopOperationResult>>? setClipboardText = null) : IDesktopPluginHost, IDesktopWorkspaceHost, IDesktopMountHost, IDesktopTextEntryFocusHost, IDesktopDefaultTextEntryHost, IDesktopClipboardHost
{
    private Dispatcher DesktopDispatcher => dispatcher;
    internal DesktopPluginSurface Surface { get; } = surface;
    private string PluginDataRoot { get; } = Path.GetFullPath(pluginDataRoot ?? DesktopPluginCatalog.DefaultDataDirectory);
    private Func<string, string, string, DesktopSettingsNavigationAvailability>? settingsNavigationAvailability;
    private Func<string, string, string, CancellationToken, Task<DesktopOperationResult>>? openSettings;
    private Func<IDesktopTextEntry?>? defaultTextEntry;
    private bool supportsDraftPresentation;

    /// <summary>Bind the Main composer resolver once; the resolver must return only its current owner target.</summary>
    public void ConfigureDefaultTextEntry(Func<IDesktopTextEntry?> current)
        => ConfigureDefaultTextEntry(current, supportsDraftPresentation: false);

    /// <summary>Opt in only after the owner implements atomic draft/selection checks and caret-preserving edits.</summary>
    public void ConfigureDefaultTextEntry(Func<IDesktopTextEntry?> current, bool supportsDraftPresentation)
    {
        dispatcher.VerifyAccess();
        if (Surface != DesktopPluginSurface.Orchestrator)
            throw new InvalidOperationException("Configuration does not expose a default text entry.");
        if (defaultTextEntry is not null)
            throw new InvalidOperationException("The default text entry is already configured.");
        defaultTextEntry = current ?? throw new ArgumentNullException(nameof(current));
        this.supportsDraftPresentation = supportsDraftPresentation;
    }

    /// <summary>The Orchestrator may bind this optional route once after constructing its host.</summary>
    public void ConfigureSettingsNavigation(
        Func<string, string, string, DesktopSettingsNavigationAvailability> availability,
        Func<string, string, string, CancellationToken, Task<DesktopOperationResult>> open)
    {
        dispatcher.VerifyAccess();
        if (settingsNavigationAvailability is not null || openSettings is not null)
            throw new InvalidOperationException("Settings navigation is already configured.");
        settingsNavigationAvailability = availability ?? throw new ArgumentNullException(nameof(availability));
        openSettings = open ?? throw new ArgumentNullException(nameof(open));
    }

    public DesktopPluginHostAdapter(Dispatcher dispatcher, Action<string> reportStatus, string? pluginDataRoot = null,
        Func<string, object, CancellationToken, Task<DesktopServiceCommandResult>>? serviceCommand = null,
        Func<DesktopProductPaths>? productPaths = null,
        Func<string, CancellationToken, Task<DesktopOperationResult>>? setClipboardText = null)
        : this(dispatcher, ConfigurationConversation, ConfigurationDraft,
            (_, _) => ConfigurationOnly(), (_, _) => ConfigurationOnly(), (_, _) => ConfigurationOnly(), (_, _) => ConfigurationOnly(),
            reportStatus, DesktopPluginSurface.Configuration, pluginDataRoot, serviceCommand,
            productPaths: productPaths, setClipboardText: setClipboardText) { }

    private static DesktopConversationSnapshot ConfigurationConversation() =>
        new(new("configuration", null), "Pegasus Cerebral Configuration", null, null, false);
    private static DesktopDraftSnapshot ConfigurationDraft() => new(new("configuration", null), 0, "");
    private static Task<DesktopOperationResult> ConfigurationOnly() => Task.FromResult(
        new DesktopOperationResult(DesktopOperationState.Rejected, "Conversation operations are unavailable in Pegasus Cerebral Configuration."));
    public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged;
    public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted;
    public event EventHandler<DesktopTextEntryFocusChangedEventArgs>? FocusedTextEntryChanged;
    public event EventHandler? PresentationRefreshRequested;
    public IDesktopTextEntry? FocusedTextEntry { get; private set; }
    public IDesktopTextEntry? DefaultTextEntry => Surface != DesktopPluginSurface.Orchestrator ||
        defaultTextEntry is null || dispatcher.HasShutdownStarted
        ? null : dispatcher.CheckAccess() ? defaultTextEntry() : dispatcher.Invoke(defaultTextEntry);
    public bool SupportsDraftPresentation => supportsDraftPresentation && DefaultTextEntry is not null;

    public void RefreshPresentation()
    {
        if (dispatcher.CheckAccess()) PresentationRefreshRequested?.Invoke(this, EventArgs.Empty);
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => PresentationRefreshRequested?.Invoke(this, EventArgs.Empty)));
    }

    public IReadOnlyList<DesktopMountCapability> GetMountCapabilities() => DesktopMountRegistry.Capabilities(Surface);

    public DesktopConversationSnapshot GetConversation() => dispatcher.CheckAccess() ? conversation() : dispatcher.Invoke(conversation);
    public DesktopDraftSnapshot GetDraft()
    {
        var value = dispatcher.CheckAccess() ? draft() : dispatcher.Invoke(draft);
        if (value.Text.Length > DesktopPluginContract.MaxTextChars)
            throw new InvalidOperationException("The draft exceeds the desktop plugin text limit; edit it in Cerebral.");
        return value;
    }

    public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update, CancellationToken cancellationToken = default) =>
        OnDispatcher(async () =>
        {
            if (CheckBinding(update.Binding) is { } invalid) return invalid;
            var current = draft();
            if (current.Binding != update.Binding || current.Revision != update.ExpectedRevision)
                return Rejected("The draft changed. Read the current draft before editing.");
            if (update.Text is null || update.Text.Length > DesktopPluginContract.MaxTextChars)
                return Rejected("The proposed draft exceeds the desktop plugin text limit.");
            if (update.Presentation is { } presentation &&
                (!SupportsDraftPresentation || presentation.ExpectedSelection?.IsWithin(current.Text.Length) != true ||
                 presentation.Selection?.IsWithin(update.Text.Length) != true))
                return Rejected("The current composer does not support this draft presentation or selection.");
            return await replaceDraft(update, cancellationToken);
        }, cancellationToken);

    public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) =>
        SubmitOrSteer(submission, submit, cancellationToken, allowAttachments: true);
    public Task<DesktopOperationResult> SteerAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) =>
        SubmitOrSteer(submission, steer, cancellationToken);
    internal Task<DesktopOperationResult> SubmitWorkflowAsync(string pluginId, DesktopWorkflowSubmission submission, CancellationToken cancellationToken = default) =>
        OnDispatcher(async () =>
        {
            if (CheckBinding(submission.Binding) is { } invalid) return invalid with { RequestId = submission.RequestId };
            if (submitWorkflow is null) return Rejected("The current host does not support private plugin workflows.", submission.RequestId);
            if (string.IsNullOrWhiteSpace(submission.RequestId) || submission.RequestId.Length > 128)
                return Rejected("A stable request ID of at most 128 characters is required.", submission.RequestId);
            if (string.IsNullOrWhiteSpace(submission.VisibleText) || submission.VisibleText.Length > DesktopPluginContract.MaxTextChars ||
                string.IsNullOrWhiteSpace(submission.Context) || submission.Context.Length > DesktopPluginContract.MaxTextChars)
                return Rejected("A plugin workflow requires bounded visible text and backend context.", submission.RequestId);
            try { submission = DesktopWorkflowAdmission.Freeze(pluginId, submission); }
            catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException or ObjectDisposedException)
            { return Rejected(error.Message, submission.RequestId); }
            return await submitWorkflow(pluginId, submission, cancellationToken);
        }, cancellationToken);
    public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding binding, CancellationToken cancellationToken = default) =>
        OnDispatcher(async () => CheckBinding(binding) ?? await stop(binding, cancellationToken), cancellationToken);
    public Task<DesktopOperationResult> SetClipboardTextAsync(string text, CancellationToken cancellationToken = default) =>
        OnDispatcher(async () =>
        {
            if (text is null || text.Length > DesktopPluginContract.MaxTextChars)
                return Rejected("Clipboard text exceeds the desktop plugin text limit.");
            if (setClipboardText is not null) return await setClipboardText(text, cancellationToken);
            try
            {
                Clipboard.SetText(text);
                return new(DesktopOperationState.Completed);
            }
            catch (Exception error)
            {
                return new(DesktopOperationState.Failed, "The clipboard is unavailable: " + error.Message);
            }
        }, cancellationToken);
    public void DismissInputGuidance()
    {
        if (dismissInputGuidance is null) return;
        if (dispatcher.CheckAccess()) dismissInputGuidance();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(dismissInputGuidance);
    }

    private Task<DesktopOperationResult> SubmitOrSteer(DesktopSubmission value,
        Func<DesktopSubmission, CancellationToken, Task<DesktopOperationResult>> action, CancellationToken token, bool allowAttachments = false) =>
        OnDispatcher(async () =>
        {
            if (CheckBinding(value.Binding) is { } invalid) return invalid with { RequestId = value.RequestId };
            if (string.IsNullOrWhiteSpace(value.RequestId) || value.RequestId.Length > 128)
                return Rejected("A stable request ID of at most 128 characters is required.", value.RequestId);
            if (value.Text is null || value.Text.Length > DesktopPluginContract.MaxTextChars ||
                string.IsNullOrWhiteSpace(value.Text) && !(allowAttachments && hasSubmissionContent?.Invoke(value) == true))
                return Rejected("Enter nonempty content within the desktop plugin text limit.", value.RequestId);
            return await action(value, token);
        }, token);

    private DesktopOperationResult? CheckBinding(DesktopConversationBinding binding)
    {
        var current = conversation().Binding;
        return string.IsNullOrWhiteSpace(binding.Generation) || binding != current
            ? Rejected("The connection or conversation changed. Review the current conversation before trying again.") : null;
    }

    private Task<DesktopOperationResult> OnDispatcher(Func<Task<DesktopOperationResult>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return dispatcher.CheckAccess() ? action() : dispatcher.InvokeAsync(action, DispatcherPriority.Normal, token).Task.Unwrap();
    }

    public void ReportStatus(string text)
    {
        var bounded = text.Length > 1000 ? text[..1000] + "…" : text;
        if (dispatcher.CheckAccess()) reportStatus(bounded);
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => reportStatus(bounded)));
    }

    private bool conversationNotificationPending;
    private bool conversationNotificationForced;
    private DesktopConversationSnapshot? lastNotifiedConversation;
    public void PublishConversationChanged(bool force = false)
    {
        dispatcher.VerifyAccess();
        if (dispatcher.HasShutdownStarted) return;
        if (force) conversationNotificationForced = true;
        if (conversationNotificationPending) return;
        conversationNotificationPending = true;
        // Application selection/rendering must finish before plugin callbacks can navigate or redraw.
        dispatcher.InvokeAsync(() =>
        {
            conversationNotificationPending = false;
            var forced = conversationNotificationForced;
            conversationNotificationForced = false;
            var current = conversation();
            if (!forced && current == lastNotifiedConversation) return;
            lastNotifiedConversation = current;
            var args = new DesktopConversationChangedEventArgs(current);
            foreach (var listener in ConversationChanged?.GetInvocationList() ?? [])
            {
                if (current.Binding != conversation().Binding) break;
                try { ((EventHandler<DesktopConversationChangedEventArgs>)listener)(this, args); }
                catch (Exception ex) { ReportStatus("Desktop plugin conversation event failed: " + ex.Message); }
            }
        }, DispatcherPriority.Background);
    }

    public void PublishLeadMessageCommitted(DesktopMessageSnapshot message)
    {
        dispatcher.VerifyAccess();
        if (message.Role != "assistant" || message.Binding != conversation().Binding || dispatcher.HasShutdownStarted) return;
        if (message.Text.Length > DesktopPluginContract.MaxTextChars)
            message = message with { Text = message.Text[..DesktopPluginContract.MaxTextChars], Truncated = true };
        dispatcher.InvokeAsync(() =>
        {
            if (message.Binding != conversation().Binding) return;
            var args = new DesktopLeadMessageEventArgs(message);
            foreach (var listener in LeadMessageCommitted?.GetInvocationList() ?? [])
            {
                if (message.Binding != conversation().Binding) break;
                try { ((EventHandler<DesktopLeadMessageEventArgs>)listener)(this, args); }
                catch (Exception ex) { ReportStatus("Desktop plugin message event failed: " + ex.Message); }
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Host-only focus publication. Plugins receive the scoped read/event facade and cannot forge focus.</summary>
    public void PublishFocusedTextEntry(IDesktopTextEntry? entry)
    {
        dispatcher.VerifyAccess();
        if (dispatcher.HasShutdownStarted || ReferenceEquals(FocusedTextEntry, entry)) return;
        FocusedTextEntry = entry;
        var args = new DesktopTextEntryFocusChangedEventArgs(entry);
        foreach (var listener in FocusedTextEntryChanged?.GetInvocationList() ?? [])
        {
            try { ((EventHandler<DesktopTextEntryFocusChangedEventArgs>)listener)(this, args); }
            catch (Exception ex) { ReportStatus("Desktop plugin text focus event failed: " + ex.Message); }
        }
    }

    internal ScopedDesktopPluginHost CreateScope(string id, string name, string packageDirectory)
    {
        var conversationSettings = responseBalance is not null && setResponseBalance is not null;
        var settingsNavigation = settingsNavigationAvailability is not null && openSettings is not null;
        return (conversationSettings, settingsNavigation) switch
        {
            (true, true) => new ScopedConversationSettingsNavigationDesktopPluginHost(this, id, name, packageDirectory),
            (true, false) => new ScopedConversationSettingsDesktopPluginHost(this, id, name, packageDirectory),
            (false, true) => new ScopedSettingsNavigationDesktopPluginHost(this, id, name, packageDirectory),
            _ => new ScopedDesktopPluginHost(this, id, name, packageDirectory)
        };
    }
    internal DesktopSettingsNavigationAvailability GetSettingsNavigationAvailability(string id, string packageDirectory, string panelId)
    {
        if (settingsNavigationAvailability is null) return new(false, "Settings navigation is unavailable in this host.");
        if (!ValidSettingsPanelId(panelId)) return new(false, "The settings panel ID is invalid.");
        return dispatcher.CheckAccess()
            ? settingsNavigationAvailability(id, packageDirectory, panelId)
            : dispatcher.Invoke(() => settingsNavigationAvailability(id, packageDirectory, panelId));
    }
    internal Task<DesktopOperationResult> OpenSettingsAsync(string id, string packageDirectory, string panelId, CancellationToken token) =>
        OnDispatcher(async () =>
        {
            var available = GetSettingsNavigationAvailability(id, packageDirectory, panelId);
            if (!available.Available) return new(DesktopOperationState.Rejected, available.Message);
            return await openSettings!(id, packageDirectory, panelId, token);
        }, token);
    private static bool ValidSettingsPanelId(string? id) => id is { Length: > 0 and <= 128 } &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    internal string PluginDataDirectory(string id) => Path.Combine(PluginDataRoot, id);
    internal Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments, CancellationToken token) =>
        serviceCommand is null
            ? Task.FromResult(new DesktopServiceCommandResult(false, default, "The current host has no Worker command connection."))
            : serviceCommand(operation, arguments, token);
    internal DesktopResponseBalanceSnapshot GetResponseBalance() =>
        responseBalance?.Invoke() ?? throw new InvalidOperationException("The current host does not expose conversation response balance.");
    internal DesktopProductPaths GetProductPaths()
    {
        if (productPaths?.Invoke() is { } configured) return configured;
        return DesktopInstancePaths.Current.ProductPaths();
    }
    internal Task<DesktopOperationResult> SetResponseBalanceAsync(DesktopConversationBinding binding, int? share, CancellationToken token) =>
        setResponseBalance is null
            ? Task.FromResult(Rejected("The current host does not expose conversation response balance."))
            : OnDispatcher(() => setResponseBalance(binding, share, token), token);
    private static DesktopOperationResult Rejected(string message, string? requestId = null) =>
        new(DesktopOperationState.Rejected, message, requestId);
}

/// <summary>Releases host subscriptions and cancels host operations when an enabled plugin is disabled.</summary>
internal partial class ScopedDesktopPluginHost : IDesktopPluginHost, IDesktopWorkspaceHost, IDesktopPluginEnvironment, IDesktopProductEnvironment, IDesktopServiceHost, IDesktopNativeWorkflowHost, IDesktopMountHost, IDesktopTextEntryFocusHost, IDesktopDefaultTextEntryHost, IDesktopClipboardHost, IDisposable
{
    private readonly DesktopPluginHostAdapter source;
    private readonly string id;
    private readonly string name;
    private readonly string packageDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged;
    public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted;
    public event EventHandler<DesktopTextEntryFocusChangedEventArgs>? FocusedTextEntryChanged;

    public ScopedDesktopPluginHost(DesktopPluginHostAdapter source, string id, string name, string packageDirectory)
    {
        this.source = source; this.id = id; this.name = name; this.packageDirectory = Path.GetFullPath(packageDirectory);
        source.ConversationChanged += OnConversation;
        source.LeadMessageCommitted += OnMessage;
        source.FocusedTextEntryChanged += OnFocusedTextEntryChanged;
    }
    public DesktopPluginSurface Surface { get { Check(); return source.Surface; } }
    public string DataDirectory { get { Check(); return source.PluginDataDirectory(id); } }
    public string PackageDirectory { get { Check(); return packageDirectory; } }
    public DesktopProductPaths ProductPaths { get { Check(); return source.GetProductPaths(); } }
    public IDesktopTextEntry? FocusedTextEntry { get { Check(); return source.FocusedTextEntry; } }
    public IDesktopTextEntry? DefaultTextEntry { get { Check(); return source.DefaultTextEntry; } }
    public bool SupportsDraftPresentation { get { Check(); return source.SupportsDraftPresentation; } }
    public IReadOnlyList<DesktopMountCapability> GetMountCapabilities() { Check(); return source.GetMountCapabilities(); }
    private void OnConversation(object? sender, DesktopConversationChangedEventArgs args) => ConversationChanged?.Invoke(this, args);
    private void OnMessage(object? sender, DesktopLeadMessageEventArgs args) => LeadMessageCommitted?.Invoke(this, args);
    private void OnFocusedTextEntryChanged(object? sender, DesktopTextEntryFocusChangedEventArgs args) => FocusedTextEntryChanged?.Invoke(this, args);
    private void Check() => ObjectDisposedException.ThrowIf(disposed, this);
    public DesktopConversationSnapshot GetConversation() { Check(); return source.GetConversation(); }
    public DesktopDraftSnapshot GetDraft() { Check(); return source.GetDraft(); }
    public void ReportStatus(string text) { Check(); source.ReportStatus(name + ": " + text); }
    public void RefreshPresentation() { Check(); source.RefreshPresentation(); }
    private async Task<DesktopOperationResult> Run(Func<CancellationToken, Task<DesktopOperationResult>> action, CancellationToken token)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        return await action(linked.Token);
    }
    public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update, CancellationToken cancellationToken = default) =>
        Run(token => source.ReplaceDraftAsync(update, token), cancellationToken);
    public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) =>
        Run(token => source.SubmitAsync(submission, token), cancellationToken);
    public Task<DesktopOperationResult> SteerAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) =>
        Run(token => source.SteerAsync(submission, token), cancellationToken);
    public Task<DesktopOperationResult> SubmitWorkflowAsync(DesktopWorkflowSubmission submission, CancellationToken cancellationToken = default) =>
        Run(token => source.SubmitWorkflowAsync(id, submission, token), cancellationToken);
    public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding binding, CancellationToken cancellationToken = default) =>
        Run(token => source.StopAsync(binding, token), cancellationToken);
    public Task<DesktopOperationResult> SetClipboardTextAsync(string text, CancellationToken cancellationToken = default) =>
        Run(token => source.SetClipboardTextAsync(text, token), cancellationToken);
    public DesktopSettingsNavigationAvailability GetSettingsNavigationAvailability(string panelId)
    {
        Check();
        return source.GetSettingsNavigationAvailability(id, packageDirectory, panelId);
    }
    public async Task<DesktopOperationResult> OpenSettingsAsync(string panelId, CancellationToken cancellationToken = default)
    {
        var result = await Run(token => source.OpenSettingsAsync(id, packageDirectory, panelId, token), cancellationToken);
        // The receiver may complete after this plugin generation has been retired. Never publish
        // that late completion as a confirmed action for the new generation.
        return disposed ? new(DesktopOperationState.Uncertain,
            "The plugin changed while Configuration navigation was in progress. Inspect the current panel.") : result;
    }
    public void DismissInputGuidance() { Check(); source.DismissInputGuidance(); }
    public async Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments,
        CancellationToken cancellationToken = default)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        return await source.ExecuteServiceCommandAsync(operation, arguments, linked.Token);
    }
    public DesktopResponseBalanceSnapshot GetResponseBalance() { Check(); return source.GetResponseBalance(); }
    public Task<DesktopOperationResult> SetResponseBalanceAsync(DesktopConversationBinding expectedBinding,
        int? responseSharePercent, CancellationToken cancellationToken = default) =>
        Run(token => source.SetResponseBalanceAsync(expectedBinding, responseSharePercent, token), cancellationToken);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        source.ConversationChanged -= OnConversation;
        source.LeadMessageCommitted -= OnMessage;
        source.FocusedTextEntryChanged -= OnFocusedTextEntryChanged;
        ConversationChanged = null; LeadMessageCommitted = null; FocusedTextEntryChanged = null;
        try { lifetime.Cancel(); } finally { lifetime.Dispose(); }
    }
}

/// <summary>Advertises the optional conversation-settings contract only when both host callbacks are available.</summary>
internal class ScopedConversationSettingsDesktopPluginHost(
    DesktopPluginHostAdapter source, string id, string name, string packageDirectory)
    : ScopedDesktopPluginHost(source, id, name, packageDirectory), IDesktopConversationSettingsHost
{
}

internal sealed class ScopedSettingsNavigationDesktopPluginHost(
    DesktopPluginHostAdapter source, string id, string name, string packageDirectory)
    : ScopedDesktopPluginHost(source, id, name, packageDirectory), IDesktopPluginSettingsNavigationHost
{
}

internal sealed class ScopedConversationSettingsNavigationDesktopPluginHost(
    DesktopPluginHostAdapter source, string id, string name, string packageDirectory)
    : ScopedConversationSettingsDesktopPluginHost(source, id, name, packageDirectory), IDesktopPluginSettingsNavigationHost
{
}
