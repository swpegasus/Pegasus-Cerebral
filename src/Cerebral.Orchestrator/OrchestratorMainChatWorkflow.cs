using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Fresh Main consumes ordinary optional context without opting into detailed tool receipts.
    private const string NormalMainChatDefinitionVersion = "1.0.2-generic-context.1";
    private const string DetailedToolFeedbackDefinitionVersion = "1.0.3-tool-context.1";
    private readonly CheckBox detailedToolFeedback = new()
    {
        Content = "Detailed tool feedback", Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
    };
    private CancellationTokenSource? mainChatDiscoveryCancellation;
    private long mainChatDiscoveryGeneration;
    private long detailedToolFeedbackChoiceGeneration = -1;
    private long mainChatWorkflowChoiceRevision;
    private MainChatWorkflowAvailability? mainChatWorkflowAvailability;
    private MainChatWorkflowAvailability? mainNormalChatWorkflowAvailability;
    private const string NormalMainChatUnavailable =
        "Chat support is not confirmed yet. Wait for connection checks, then try again or reconnect. Your draft is preserved.";
    private MainExactWorkflowAvailability? mainExactWorkflowAvailability;
    private string mainChatWorkflowUnavailable = "Reconnect to check whether detailed tool feedback is available.";
    private readonly AsyncLocal<MainChatWorkflowIntent?> mainChatWorkflowIntent = new();

    private sealed record MainChatWorkflowAvailability(CerebralClient Client, object? Connection,
        string ConnectionIdentity, string InstallationId, int ConnectionVersion, long SelectionVersion,
        string ConversationId, string ProfileId, int ProfileRevision, string[] Capabilities,
        long Generation, WorkflowDefinitionDescriptor Definition);

    private sealed record MainExactWorkflowAvailability(CerebralClient Client, object? Connection,
        string ConnectionIdentity, string InstallationId, int ConnectionVersion, long SelectionVersion,
        string ConversationId, string ProfileId, int ProfileRevision, string[] Capabilities,
        long Generation, WorkflowDefinitionDescriptor Definition);

    private sealed record MainWorkflowIdentity(string Id, string Version);

    private sealed record MainChatWorkflowIntent(string Origin, string DefinitionVersion, string ConversationId,
        long SelectionVersion, int ConnectionVersion, CerebralClient? Client, object? Connection,
        string ConnectionIdentity, string? InstallationId, string ProfileId, int ProfileRevision,
        string[] Capabilities, bool DetailedRequested, long ChoiceRevision, long DiscoveryGeneration,
        long ChoiceGeneration, MainChatWorkflowAvailability? Availability, string? DefinitionInputJson,
        string[] RequiredCapabilities, WorkflowConversationSubmission? RetainedSubmission, string? AcceptedRunId)
    {
        public string DefinitionId { get; init; } = ConversationControlClient.ChatDefinitionId;
        public MainExactWorkflowAvailability? ExactAvailability { get; init; }
    }

    private void InitializeMainChatWorkflowChoice()
    {
        AutomationProperties.SetAutomationId(detailedToolFeedback, "DetailedToolFeedback");
        AutomationProperties.SetName(detailedToolFeedback, "Detailed tool feedback");
        detailedToolFeedback.Checked += (_, _) => MainChatWorkflowChoiceChanged();
        detailedToolFeedback.Unchecked += (_, _) => MainChatWorkflowChoiceChanged();
        composerOptionsPanel?.Children.Insert(0, detailedToolFeedback);
        ServiceStatusChanged += MainChatWorkflowConnectionChanged;
        Closed += (_, _) =>
        {
            ServiceStatusChanged -= MainChatWorkflowConnectionChanged;
            InvalidateMainChatWorkflowDiscovery();
        };
        UpdateMainChatWorkflowChoice();
    }

    private void MainChatWorkflowChoiceChanged()
    {
        mainExactWorkflowAvailability = null;
        // A changed-back checkbox is still a new decision made after a send was captured.
        mainChatWorkflowChoiceRevision = checked(mainChatWorkflowChoiceRevision + 1);
        detailedToolFeedbackChoiceGeneration = detailedToolFeedback.IsChecked == true && MainChatWorkflowAvailabilityCurrent()
            ? mainChatWorkflowAvailability!.Generation : -1;
        UpdateMainChatWorkflowChoice();
    }

    private void MainChatWorkflowConnectionChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => MainChatWorkflowConnectionChanged(sender, args)));
            return;
        }
        if (!IsConnected || mainChatWorkflowAvailability is { } available &&
            (!ReferenceEquals(available.Client, Client) || !ReferenceEquals(available.Connection, ServiceConnection) ||
              available.ConnectionIdentity != ServiceConnectionIdentity) ||
            mainExactWorkflowAvailability is { } exact &&
            (!ReferenceEquals(exact.Client, Client) || !ReferenceEquals(exact.Connection, ServiceConnection) ||
             exact.ConnectionIdentity != ServiceConnectionIdentity))
            InvalidateMainChatWorkflowDiscovery();
        UpdateMainChatWorkflowChoice();
    }

    private void InvalidateMainChatWorkflowDiscovery()
    {
        mainChatDiscoveryGeneration = checked(mainChatDiscoveryGeneration + 1);
        mainChatDiscoveryCancellation?.Cancel();
        mainChatDiscoveryCancellation?.Dispose();
        mainChatDiscoveryCancellation = null;
        mainChatWorkflowAvailability = null;
        mainNormalChatWorkflowAvailability = null;
        mainExactWorkflowAvailability = null;
        UpdateMainChatWorkflowChoice();
    }

    private void ResetMainChatWorkflowChoice()
    {
        ClearRetainedNativeSubmission();
        detailedToolFeedback.IsChecked = false;
        detailedToolFeedbackChoiceGeneration = -1;
        InvalidateMainChatWorkflowDiscovery();
    }

    private async Task RefreshFreshNormalChatWorkflowAfterProfilesAsync(CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        // Profile notifications may replace the cached revision after discovery has completed.
        // Inspect every fresh-normal boundary before the discovery method invalidates anything.
        if (!IsConnected || !IsNativeChat || nativeChat?.Snapshot is not { Run: null, RunId: null } snapshot ||
            snapshot.ConversationId != durableConversationId || retainedNativeSubmission is not null ||
            NativeMutationPending || NativeMutationUncertain || detailedToolFeedback.IsChecked != false ||
            mainExactWorkflowAvailability is not null || TryConversationProfile() is not { } profile ||
            !FreshChatProfileAdmissible(profile) || MainNormalChatWorkflowAvailabilityCurrent())
            return;
        await RefreshMainChatWorkflowAvailabilityAsync(token);
    }

    private async Task RefreshMainChatWorkflowAvailabilityAsync(CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        InvalidateMainChatWorkflowDiscovery();
        if (!IsConnected || !IsNativeChat || nativeChat?.Snapshot?.Run is not null ||
            retainedNativeSubmission is not null || TryConversationProfile() is not { } profile || !FreshChatProfileAdmissible(profile))
            return;
        var client = Client;
        var connection = ServiceConnection;
        var identity = ServiceConnectionIdentity;
        var installation = systemWorkMetricsInstallationId;
        if (installation is null || !SystemWorkMetricsBindingCurrent(client, installation)) return;
        var source = knowledgeConnectionVersion;
        var selection = conversationSelectionVersion;
        var conversationId = durableConversationId!;
        var generation = mainChatDiscoveryGeneration;
        var profileId = profile.Id;
        var profileRevision = profile.Revision;
        var capabilities = profile.Capabilities.ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token, token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        mainChatDiscoveryCancellation = cancellation;
        bool Current() => !cancellation.IsCancellationRequested && IsConnected &&
            ReferenceEquals(client, Client) && ReferenceEquals(connection, ServiceConnection) &&
            identity == ServiceConnectionIdentity && SystemWorkMetricsBindingCurrent(client, installation) &&
            source == knowledgeConnectionVersion && selection == conversationSelectionVersion &&
            generation == mainChatDiscoveryGeneration && conversationId == durableConversationId &&
            retainedNativeSubmission is null && nativeChat?.Snapshot?.Run is null &&
            TryConversationProfile() is { } current && FreshChatProfileAdmissible(current) &&
            current.Id == profileId && current.Revision == profileRevision &&
            current.Capabilities.SequenceEqual(capabilities, StringComparer.Ordinal);
        try
        {
            // Both editions share one bounded catalog/description read in this exact binding.
            CommandResult? catalog = null, description = null;
            async Task<CommandResult> Query(CommandRequest request, CancellationToken cancellationToken)
            {
                if (request.Operation == "sdk.discover")
                    return catalog ??= await client.CommandAsync(request.Operation, request.Arguments, request.ProfileId, cancellationToken);
                if (request.Operation == "sdk.describe")
                    return description ??= await client.CommandAsync(request.Operation, request.Arguments, request.ProfileId, cancellationToken);
                throw new InvalidOperationException("Unexpected Main workflow discovery operation.");
            }
            try
            {
                var normal = await DiscoverMainExactWorkflowAsync(Query, ConversationControlClient.ChatDefinitionId,
                    NormalMainChatDefinitionVersion, capabilities, Current, cancellation.Token);
                if (!Current()) return;
                mainNormalChatWorkflowAvailability = new(client, connection, identity, installation, source, selection,
                    conversationId, profileId, profileRevision, capabilities.ToArray(), generation, normal);
                // Only this exact fresh binding may consume the confirmed ordinary edition.
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException or
                HttpRequestException or ArgumentException or JsonException or TimeoutException or OperationCanceledException or
                KeyNotFoundException or FormatException or OverflowException)
            {
                // Leave ordinary availability absent; detailed availability is independent.
                // Selecting normal Chat fails actionably rather than dropping personality support.
            }
            if (!Current()) return;
            var definition = await DiscoverMainChatWorkflowAsync(Query, capabilities, Current, cancellation.Token);
            if (!Current()) return;
            mainChatWorkflowAvailability = new(client, connection, identity, installation, source, selection,
                conversationId, profileId, profileRevision, capabilities.ToArray(), generation,
                definition with { InputSchema = definition.InputSchema.Clone(), RequiredCapabilities = definition.RequiredCapabilities.ToArray() });
            mainChatWorkflowUnavailable = "";
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException or
            HttpRequestException or ArgumentException or JsonException or TimeoutException or OperationCanceledException or
            KeyNotFoundException or FormatException or OverflowException)
        {
            // A cancelled/foreign reply never updates availability or the current chat's diagnostics.
            if (Current())
                mainChatWorkflowUnavailable = error is InvalidDataException
                    ? "Detailed tool feedback is unavailable or its current declaration is invalid."
                    : "Detailed tool feedback could not be confirmed. Reconnect or start a new chat.";
        }
        finally
        {
            if (ReferenceEquals(mainChatDiscoveryCancellation, cancellation)) mainChatDiscoveryCancellation = null;
            if (!Dispatcher.HasShutdownStarted) UpdateMainChatWorkflowChoice();
        }
    }

    private static async Task<WorkflowDefinitionDescriptor> DiscoverMainChatWorkflowAsync(
        Func<CommandRequest, CancellationToken, Task<CommandResult>> command, string[] capabilities,
        Func<bool> current, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(current);
        var acceptedCapabilities = capabilities.ToArray();
        void RequireCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (!current()) throw new InvalidOperationException("The chat or service changed while checking detailed tool feedback.");
        }
        RequireCurrent();
        var discovery = await command(new("sdk.discover", CerebralJson.Element(new { })), token);
        RequireCurrent();
        if (!discovery.Success) throw new InvalidDataException("The service could not check detailed tool feedback.");
        if (!ValidateMainChatWorkflowCatalog(discovery.Data))
            throw new InvalidDataException("Detailed tool feedback is unavailable on this service.");
        RequireCurrent();
        var described = await command(new("sdk.describe", CerebralJson.Element(new { id = ConversationControlClient.ChatDefinitionId })), token);
        RequireCurrent();
        if (!described.Success) throw new InvalidDataException("The service could not confirm detailed tool feedback.");
        var definition = ValidateMainChatWorkflowDescription(described.Data);
        if (definition.RequiredCapabilities.Except(acceptedCapabilities, StringComparer.Ordinal).Any())
            throw new InvalidDataException("The selected chat profile does not have the permissions needed for detailed tool feedback.");
        RequireCurrent();
        return definition with { InputSchema = definition.InputSchema.Clone(), RequiredCapabilities = definition.RequiredCapabilities.ToArray() };
    }

    private async Task SelectMainExactWorkflowAsync(string id, string version, CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        RequireSemanticAttachCurrent();
        var accepted = nativeChat?.Snapshot?.Run;
        var acceptedRecheck = accepted is { IsTerminal: true } && accepted.DefinitionId == id &&
            accepted.DefinitionVersion == version && retainedNativeSubmission is null &&
            !NativeMutationPending && !NativeMutationUncertain;
        if (!acceptedRecheck) RequireFreshMainChatWorkflowChoice();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(version) || version.Length > 64 || version.Any(char.IsControl))
            throw new ArgumentException("Select a bounded exact workflow ID and version.");
        InvalidateMainChatWorkflowDiscovery();
        var client = Client;
        var connection = ServiceConnection;
        var connectionIdentity = ServiceConnectionIdentity;
        var installation = systemWorkMetricsInstallationId;
        if (installation is null || !SystemWorkMetricsBindingCurrent(client, installation))
            throw new InvalidOperationException("The current service identity is unavailable.");
        var source = knowledgeConnectionVersion;
        var selection = conversationSelectionVersion;
        var conversationId = durableConversationId!;
        var generation = mainChatDiscoveryGeneration;
        var profile = ConversationProfile();
        if (acceptedRecheck && profile.Id != accepted!.ProfileId)
            throw new InvalidOperationException("The accepted workflow belongs to a different profile.");
        var profileId = profile.Id;
        var profileRevision = profile.Revision;
        var capabilities = profile.Capabilities.ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token, token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        mainChatDiscoveryCancellation = cancellation;
        bool Current() => !cancellation.IsCancellationRequested && IsConnected &&
            ReferenceEquals(client, Client) && ReferenceEquals(connection, ServiceConnection) &&
            connectionIdentity == ServiceConnectionIdentity && SystemWorkMetricsBindingCurrent(client, installation) &&
            source == knowledgeConnectionVersion && selection == conversationSelectionVersion &&
            generation == mainChatDiscoveryGeneration && conversationId == durableConversationId &&
            (acceptedRecheck ? nativeChat?.Snapshot?.Run is { IsTerminal: true } run &&
                run.Id == accepted!.Id && run.DefinitionId == id && run.DefinitionVersion == version &&
                run.ProfileId == profileId : nativeChat?.Snapshot?.Run is null) && retainedNativeSubmission is null &&
            TryConversationProfile() is { } current && (acceptedRecheck || FreshChatProfileAdmissible(current)) &&
            current.Id == profileId && current.Revision == profileRevision &&
            current.Capabilities.SequenceEqual(capabilities, StringComparer.Ordinal);
        try
        {
            var definition = await DiscoverMainExactWorkflowAsync(
                (request, cancellationToken) => client.CommandAsync(request.Operation, request.Arguments,
                    request.ProfileId, cancellationToken), id, version, capabilities, Current, cancellation.Token);
            if (!Current()) throw new InvalidOperationException("The chat or service changed while selecting the workflow.");
            if (detailedToolFeedback.IsChecked == true) detailedToolFeedback.IsChecked = false;
            detailedToolFeedbackChoiceGeneration = -1;
            mainExactWorkflowAvailability = new(client, connection, connectionIdentity, installation, source, selection,
                conversationId, profileId, profileRevision, capabilities, generation, definition);
            mainChatWorkflowChoiceRevision = checked(mainChatWorkflowChoiceRevision + 1);
            UpdateMainChatWorkflowChoice();
        }
        finally
        {
            if (ReferenceEquals(mainChatDiscoveryCancellation, cancellation)) mainChatDiscoveryCancellation = null;
        }
    }

    internal static async Task<WorkflowDefinitionDescriptor> DiscoverMainExactWorkflowAsync(
        Func<CommandRequest, CancellationToken, Task<CommandResult>> command, string id, string version,
        string[] capabilities, Func<bool> current, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(current);
        void RequireCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (!current()) throw new InvalidOperationException("The chat or service changed while checking the workflow.");
        }
        RequireCurrent();
        var catalog = await command(new("sdk.discover", CerebralJson.Element(new { })), token);
        RequireCurrent();
        if (!catalog.Success || !ValidateMainExactWorkflowCatalog(catalog.Data, id, version))
            throw new InvalidDataException("The exact workflow is unavailable on this service.");
        var described = await command(new("sdk.describe", CerebralJson.Element(new { id })), token);
        RequireCurrent();
        if (!described.Success) throw new InvalidDataException("The service could not describe the exact workflow.");
        var definition = ValidateMainExactWorkflowDescription(described.Data, id, version);
        if (definition.RequiredCapabilities.Except(capabilities, StringComparer.Ordinal).Any())
            throw new InvalidDataException("The selected profile lacks the workflow's required capabilities.");
        RequireCurrent();
        return definition;
    }

    private bool MainChatWorkflowAvailabilityCurrent() =>
        MainChatWorkflowAvailabilityCurrent(mainChatWorkflowAvailability) &&
        mainChatWorkflowAvailability!.Definition.Id == ConversationControlClient.ChatDefinitionId &&
        mainChatWorkflowAvailability.Definition.Version == DetailedToolFeedbackDefinitionVersion;

    private bool MainNormalChatWorkflowAvailabilityCurrent() =>
        MainChatWorkflowAvailabilityCurrent(mainNormalChatWorkflowAvailability) &&
        mainNormalChatWorkflowAvailability!.Definition.Id == ConversationControlClient.ChatDefinitionId &&
        mainNormalChatWorkflowAvailability.Definition.Version == NormalMainChatDefinitionVersion;

    private bool MainChatWorkflowAvailabilityCurrent(MainChatWorkflowAvailability? available)
    {
        if (available is null || !IsConnected ||
            !ReferenceEquals(available.Client, Client) || !ReferenceEquals(available.Connection, ServiceConnection) ||
            available.ConnectionIdentity != ServiceConnectionIdentity ||
            !SystemWorkMetricsBindingCurrent(available.Client, available.InstallationId) ||
            available.ConnectionVersion != knowledgeConnectionVersion ||
            available.SelectionVersion != conversationSelectionVersion || available.ConversationId != durableConversationId ||
            available.Generation != mainChatDiscoveryGeneration ||
            TryConversationProfile() is not { } profile || !FreshChatProfileAdmissible(profile) ||
            profile.Id != available.ProfileId || profile.Revision != available.ProfileRevision ||
            !profile.Capabilities.SequenceEqual(available.Capabilities, StringComparer.Ordinal))
            return false;
        return true;
    }

    private bool MainExactWorkflowAvailabilityCurrent()
    {
        if (mainExactWorkflowAvailability is not { } available || !IsConnected ||
            !ReferenceEquals(available.Client, Client) || !ReferenceEquals(available.Connection, ServiceConnection) ||
            available.ConnectionIdentity != ServiceConnectionIdentity ||
            !SystemWorkMetricsBindingCurrent(available.Client, available.InstallationId) ||
            available.ConnectionVersion != knowledgeConnectionVersion ||
            available.SelectionVersion != conversationSelectionVersion || available.ConversationId != durableConversationId ||
            available.Generation != mainChatDiscoveryGeneration ||
            nativeChat?.Snapshot?.Run is { } selectedRun &&
                (selectedRun.DefinitionId != available.Definition.Id ||
                 selectedRun.DefinitionVersion != available.Definition.Version) ||
            TryConversationProfile() is not { } profile ||
            (nativeChat?.Snapshot?.Run is { } accepted ? accepted.ProfileId != profile.Id :
                !FreshChatProfileAdmissible(profile)) ||
            profile.Id != available.ProfileId || profile.Revision != available.ProfileRevision ||
            !profile.Capabilities.SequenceEqual(available.Capabilities, StringComparer.Ordinal))
            return false;
        return true;
    }

    private MainWorkflowIdentity SelectedMainWorkflowIdentity(WorkflowConversationSnapshot snapshot)
    {
        if (retainedNativeSubmission is { } pending)
        {
            if (string.IsNullOrWhiteSpace(pending.DefinitionId) || string.IsNullOrWhiteSpace(pending.DefinitionVersion))
                throw new InvalidOperationException("The retained request has no exact workflow identity.");
            return new(pending.DefinitionId, pending.DefinitionVersion);
        }
        if (snapshot.Run is { } accepted)
        {
            if (string.IsNullOrWhiteSpace(accepted.DefinitionId) || string.IsNullOrWhiteSpace(accepted.DefinitionVersion))
                throw new InvalidOperationException("The accepted run has no exact workflow identity.");
            return new(accepted.DefinitionId, accepted.DefinitionVersion);
        }
        if (mainExactWorkflowAvailability is not null)
        {
            if (!MainExactWorkflowAvailabilityCurrent())
                throw new InvalidOperationException("The exact workflow choice is no longer confirmed for this chat and service.");
            return new(mainExactWorkflowAvailability.Definition.Id, mainExactWorkflowAvailability.Definition.Version);
        }
        return new(ConversationControlClient.ChatDefinitionId, SelectedMainChatDefinitionVersion(snapshot));
    }

    private void UpdateMainChatWorkflowChoice()
    {
        var fresh = IsNativeChat && nativeChat?.Snapshot?.Run is null && retainedNativeSubmission is null;
        var current = MainChatWorkflowAvailabilityCurrent();
        var chosen = detailedToolFeedback.IsChecked == true;
        detailedToolFeedback.Visibility = fresh && mainExactWorkflowAvailability is null && (current || chosen) ? Visibility.Visible : Visibility.Collapsed;
        detailedToolFeedback.IsEnabled = fresh && mainExactWorkflowAvailability is null && !NativeMutationPending && !NativeMutationUncertain && (current || chosen);
        detailedToolFeedback.ToolTip = current && (!chosen || detailedToolFeedbackChoiceGeneration == mainChatWorkflowAvailability!.Generation)
            ? "Keep detailed tool feedback in this chat. Choose before sending its first message."
            : "This choice must be checked again for the current connection. Turn it off to use a normal chat, or start a new chat to check availability. " + mainChatWorkflowUnavailable;
    }

    private string SelectedMainChatDefinitionVersion(WorkflowConversationSnapshot snapshot)
    {
        var detailed = detailedToolFeedback.IsChecked == true;
        var available = detailed
            ? MainChatWorkflowAvailabilityCurrent() &&
                detailedToolFeedbackChoiceGeneration == mainChatWorkflowAvailability!.Generation
            : MainNormalChatWorkflowAvailabilityCurrent();
        if (retainedNativeSubmission is null && snapshot.Run is null && !detailed && !available)
            throw new InvalidOperationException(NormalMainChatUnavailable);
        return ResolveMainChatDefinitionVersion(retainedNativeSubmission, snapshot.Run, detailed, available);
    }

    private void RequireFreshMainChatWorkflowChoice()
    {
        if (!IsNativeChat || nativeChat?.Snapshot is not { } snapshot || snapshot.Run is not null ||
            snapshot.RunId is not null || retainedNativeSubmission is not null || NativeMutationPending || NativeMutationUncertain)
            throw new InvalidOperationException("Choose the workflow for a fresh chat before any request is pending or accepted.");
    }

    private async Task SelectMainChatWorkflowAsync(bool detailed, CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        RequireSemanticAttachCurrent();
        RequireFreshMainChatWorkflowChoice();
        if (detailed ? !MainChatWorkflowAvailabilityCurrent() : !MainNormalChatWorkflowAvailabilityCurrent())
            await RefreshMainChatWorkflowAvailabilityAsync(token);
        token.ThrowIfCancellationRequested();
        RequireSemanticAttachCurrent();
        RequireFreshMainChatWorkflowChoice();
        if (detailed && !MainChatWorkflowAvailabilityCurrent())
            throw new InvalidOperationException("Detailed tool feedback is not confirmed for this chat and service.");
        if (!detailed && !MainNormalChatWorkflowAvailabilityCurrent())
            throw new InvalidOperationException(NormalMainChatUnavailable);
        // Use the ordinary UI decision handler, including an explicit selection of its current value.
        if (detailedToolFeedback.IsChecked == detailed) MainChatWorkflowChoiceChanged();
        else detailedToolFeedback.IsChecked = detailed;
        if (detailed && detailedToolFeedbackChoiceGeneration != mainChatWorkflowAvailability!.Generation)
            throw new InvalidOperationException("The chat option changed before it could be selected.");
    }

    private MainChatWorkflowIntent? CaptureMainChatWorkflowIntent()
    {
        Dispatcher.VerifyAccess();
        if (!IsNativeChat) return null;
        var snapshot = nativeChat?.Snapshot ?? throw new InvalidOperationException("The current chat is unavailable.");
        if (snapshot.ConversationId != durableConversationId)
            throw new InvalidOperationException("The selected chat snapshot changed.");
        var retained = retainedNativeSubmission;
        var accepted = snapshot.Run;
        var selected = SelectedMainWorkflowIdentity(snapshot);
        var profile = TryConversationProfile();
        var origin = retained is not null ? "retained_request" : accepted is not null ? "accepted_run" : "fresh_choice";
        if (retained is not null && !RetainedNativeOwnerCurrent())
            throw new InvalidOperationException("The retained request belongs to its original service.");
        if (origin == "fresh_choice" && (profile is null || NativeMutationUncertain))
            throw new InvalidOperationException("The current chat profile or request state is unavailable.");
        var availability = detailedToolFeedback.IsChecked == true
            ? mainChatWorkflowAvailability : mainNormalChatWorkflowAvailability;
        var exact = mainExactWorkflowAvailability;
        return new(origin, selected.Version, durableConversationId!, conversationSelectionVersion, knowledgeConnectionVersion,
            SemanticAttachClient(), ServiceConnection, ServiceConnectionIdentity, systemWorkMetricsInstallationId,
            retained?.ProfileId ?? accepted?.ProfileId ?? profile!.Id, profile?.Revision ?? 0,
            profile?.Capabilities.ToArray() ?? [], detailedToolFeedback.IsChecked == true,
            mainChatWorkflowChoiceRevision, mainChatDiscoveryGeneration, detailedToolFeedbackChoiceGeneration,
             availability, (exact?.Definition ?? availability?.Definition)?.InputSchema.GetRawText(),
             (exact?.Definition ?? availability?.Definition)?.RequiredCapabilities.ToArray() ?? [], retained, accepted?.Id)
        {
            DefinitionId = selected.Id,
            ExactAvailability = exact
        };
    }

    private void RequireMainChatWorkflowIntentCurrent(MainChatWorkflowIntent? intent)
    {
        if (intent is null) return;
        Dispatcher.VerifyAccess();
        if (Lifetime.IsCancellationRequested || !IsNativeChat || durableConversationId != intent.ConversationId ||
            conversationSelectionVersion != intent.SelectionVersion || knowledgeConnectionVersion != intent.ConnectionVersion ||
            !ReferenceEquals(intent.Client, SemanticAttachClient()) || !ReferenceEquals(intent.Connection, ServiceConnection) ||
            intent.ConnectionIdentity != ServiceConnectionIdentity || intent.InstallationId != systemWorkMetricsInstallationId ||
            intent.Client is not null && intent.InstallationId is not null &&
                !SystemWorkMetricsBindingCurrent(intent.Client, intent.InstallationId))
            throw new InvalidOperationException("The chat or service changed after the workflow choice was captured.");
        var snapshot = nativeChat?.Snapshot ?? throw new InvalidOperationException("The current chat is unavailable.");
        if (snapshot.ConversationId != intent.ConversationId)
            throw new InvalidOperationException("The captured chat snapshot changed.");
        if (intent.Origin == "retained_request")
        {
            var original = intent.RetainedSubmission;
            var stillRetained = original is not null && ReferenceEquals(retainedNativeSubmission, original) &&
                RetainedNativeOwnerCurrent() && original.DefinitionId == intent.DefinitionId &&
                original.DefinitionVersion == intent.DefinitionVersion &&
                original.ProfileId == intent.ProfileId;
            // Receipt cleanup may clear the payload before this awaiting caller resumes. The same
            // accepted request/edition is authority; a foreign or uncertain snapshot cannot borrow it.
            var acceptedOriginal = retainedNativeSubmission is null && !snapshot.MutationUncertain &&
                original is not null && snapshot.Run is { } receipt && snapshot.RunId == original.RequestId &&
                receipt.Id == original.RequestId && receipt.DefinitionId == intent.DefinitionId &&
                receipt.DefinitionVersion == intent.DefinitionVersion && receipt.ProfileId == intent.ProfileId;
            if (!stillRetained && !acceptedOriginal)
                throw new InvalidOperationException("The original retained workflow request changed.");
            return;
        }
        if (intent.Origin == "accepted_run")
        {
            if (snapshot.Run is not { } accepted || accepted.Id != intent.AcceptedRunId ||
                accepted.DefinitionId != intent.DefinitionId ||
                accepted.DefinitionVersion != intent.DefinitionVersion || accepted.ProfileId != intent.ProfileId)
                throw new InvalidOperationException("The original accepted workflow edition changed.");
            if (intent.DefinitionId != ConversationControlClient.ChatDefinitionId)
            {
                // A new plugin turn is a fresh admission, even when its accepted predecessor pins
                // the definition ID. The retained-request branch above alone owns uncertain retries.
                if (!accepted.IsTerminal || snapshot.RunId != accepted.Id ||
                    retainedNativeSubmission is not null || NativeMutationUncertain ||
                    detailedToolFeedback.IsChecked != intent.DetailedRequested ||
                    mainChatWorkflowChoiceRevision != intent.ChoiceRevision ||
                    TryConversationProfile() is not { } acceptedProfile || acceptedProfile.Id != intent.ProfileId ||
                    acceptedProfile.Revision != intent.ProfileRevision ||
                    !acceptedProfile.Capabilities.SequenceEqual(intent.Capabilities, StringComparer.Ordinal) ||
                    mainChatDiscoveryGeneration != intent.DiscoveryGeneration ||
                    intent.ExactAvailability is not { } acceptedExact ||
                    !ReferenceEquals(mainExactWorkflowAvailability, acceptedExact) ||
                    !MainExactWorkflowAvailabilityCurrent() ||
                    acceptedExact.Definition.Id != intent.DefinitionId ||
                    acceptedExact.Definition.Version != intent.DefinitionVersion ||
                    acceptedExact.Definition.InputSchema.GetRawText() != intent.DefinitionInputJson ||
                    !acceptedExact.Definition.RequiredCapabilities.SequenceEqual(intent.RequiredCapabilities, StringComparer.Ordinal))
                    throw new InvalidOperationException("The exact workflow declaration or profile changed during message preparation.");
            }
            return;
        }
        if (intent.Origin != "fresh_choice" || snapshot.Run is not null || retainedNativeSubmission is not null || NativeMutationUncertain ||
            detailedToolFeedback.IsChecked != intent.DetailedRequested || mainChatWorkflowChoiceRevision != intent.ChoiceRevision ||
            TryConversationProfile() is not { } profile || profile.Id != intent.ProfileId || profile.Revision != intent.ProfileRevision ||
            !profile.Capabilities.SequenceEqual(intent.Capabilities, StringComparer.Ordinal))
            throw new InvalidOperationException("The fresh chat workflow choice changed during message preparation.");
        // Every fresh Main choice depends on its captured exact declaration and current binding.
        // Accepted/retained requests above keep their original edition without reselecting it.
        if (intent.ExactAvailability is { } selectedExact)
        {
            if (mainChatDiscoveryGeneration != intent.DiscoveryGeneration ||
                !ReferenceEquals(mainExactWorkflowAvailability, selectedExact) || !MainExactWorkflowAvailabilityCurrent() ||
                selectedExact.Definition.Id != intent.DefinitionId || selectedExact.Definition.Version != intent.DefinitionVersion ||
                selectedExact.Definition.InputSchema.GetRawText() != intent.DefinitionInputJson ||
                !selectedExact.Definition.RequiredCapabilities.SequenceEqual(intent.RequiredCapabilities, StringComparer.Ordinal))
                throw new InvalidOperationException("The captured exact workflow declaration changed.");
        }
        else
        {
            var currentAvailability = intent.DetailedRequested
                ? mainChatWorkflowAvailability : mainNormalChatWorkflowAvailability;
            if (mainChatDiscoveryGeneration != intent.DiscoveryGeneration ||
                intent.DetailedRequested && detailedToolFeedbackChoiceGeneration != intent.ChoiceGeneration ||
                !ReferenceEquals(currentAvailability, intent.Availability) ||
                intent.Availability is not { } availability ||
                !(intent.DetailedRequested ? MainChatWorkflowAvailabilityCurrent() : MainNormalChatWorkflowAvailabilityCurrent()))
                throw new InvalidOperationException("The selected chat workflow discovery changed during message preparation.");
            if (availability.Definition.InputSchema.GetRawText() != intent.DefinitionInputJson ||
                !availability.Definition.RequiredCapabilities.SequenceEqual(intent.RequiredCapabilities, StringComparer.Ordinal))
                throw new InvalidOperationException("The captured workflow declaration changed.");
        }
        if (SelectedMainWorkflowIdentity(snapshot) is not { } selected ||
            selected.Id != intent.DefinitionId || selected.Version != intent.DefinitionVersion)
            throw new InvalidOperationException("The fresh chat workflow is no longer confirmed.");
    }

    private bool MainChatWorkflowIntentCurrent(MainChatWorkflowIntent? intent)
    {
        try { RequireMainChatWorkflowIntentCurrent(intent); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private object MainChatWorkflowSelectionSnapshot()
    {
        var snapshot = IsNativeChat ? nativeChat?.Snapshot : null;
        var retained = IsNativeChat ? retainedNativeSubmission : null;
        var accepted = snapshot?.Run;
        var fresh = IsNativeChat && snapshot is not null && accepted is null && retained is null;
        var current = detailedToolFeedback.IsChecked == true
            ? MainChatWorkflowAvailabilityCurrent() : MainNormalChatWorkflowAvailabilityCurrent();
        string? version = null;
        string? definitionId = null;
        string? reason = null;
        var origin = retained is not null ? "retained_request" : accepted is not null ? "accepted_run" : fresh ? "fresh_choice" : "unavailable";
        if (snapshot is not null)
        {
            try { var selected = SelectedMainWorkflowIdentity(snapshot); definitionId = selected.Id; version = selected.Version; }
            catch (InvalidOperationException) { reason = "The selected workflow is not confirmed for this chat and service."; }
        }
        var availability = detailedToolFeedback.IsChecked == true
            ? mainChatWorkflowAvailability : mainNormalChatWorkflowAvailability;
        return new
        {
            definitionId,
            definitionVersion = version, origin, fresh,
            selectionAllowed = fresh && !NativeMutationPending && !NativeMutationUncertain,
            detailedToolFeedback = detailedToolFeedback.IsChecked == true,
            choiceRevision = mainChatWorkflowChoiceRevision,
            availability = new { current, exactCurrent = MainExactWorkflowAvailabilityCurrent(), generation = mainChatDiscoveryGeneration,
                choiceGeneration = detailedToolFeedbackChoiceGeneration,
                installationId = availability?.InstallationId, profileId = availability?.ProfileId,
                profileRevision = availability?.ProfileRevision, definitionVersion = availability?.Definition.Version,
                exactDefinitionId = mainExactWorkflowAvailability?.Definition.Id,
                exactDefinitionVersion = mainExactWorkflowAvailability?.Definition.Version },
            reason
        };
    }

    private static string ResolveMainChatDefinitionVersion(WorkflowConversationSubmission? pending,
        WorkflowConversationRun? accepted, bool detailedRequested, bool selectedAvailable)
    {
        if (pending is not null)
        {
            if (pending.DefinitionId != ConversationControlClient.ChatDefinitionId || string.IsNullOrWhiteSpace(pending.DefinitionVersion))
                throw new InvalidOperationException("The retained request has no confirmed chat version. Restore its original connection and refresh its receipt before continuing.");
            return pending.DefinitionVersion;
        }
        if (accepted is not null)
        {
            if (accepted.DefinitionId != ConversationControlClient.ChatDefinitionId || string.IsNullOrWhiteSpace(accepted.DefinitionVersion))
                throw new InvalidOperationException("This saved conversation cannot be continued in Main Chat. Open its saved work or start a new chat.");
            return accepted.DefinitionVersion;
        }
        if (!selectedAvailable)
            throw new InvalidOperationException(detailedRequested
                ? "Detailed tool feedback is no longer confirmed. Reconnect before choosing it for a new chat."
                : NormalMainChatUnavailable);
        return detailedRequested ? DetailedToolFeedbackDefinitionVersion : NormalMainChatDefinitionVersion;
    }

    private static bool ValidateMainChatWorkflowCatalog(JsonElement catalog)
    {
        ValidateMainChatWorkflowContract(catalog);
        if (!catalog.TryGetProperty("workflows", out var entries) ||
            entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 256)
            throw new InvalidDataException("The installed workflow catalog is invalid or too large.");
        var identities = new HashSet<(string Id, string Version)>();
        var found = false;
        foreach (var entry in entries.EnumerateArray())
        {
            var id = MainChatWorkflowText(entry, "id");
            var version = MainChatWorkflowText(entry, "version");
            _ = MainChatWorkflowText(entry, "description", allowEmpty: true);
            if (!identities.Add((id, version)))
                throw new InvalidDataException("The installed workflow catalog contains duplicate versions.");
            found |= id == ConversationControlClient.ChatDefinitionId && version == DetailedToolFeedbackDefinitionVersion;
        }
        return found;
    }

    private static WorkflowDefinitionDescriptor ValidateMainChatWorkflowDescription(JsonElement description)
    {
        ValidateMainChatWorkflowContract(description);
        if (!description.TryGetProperty("definitions", out var entries) ||
            entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 256)
            throw new InvalidDataException("The workflow description is invalid or too large.");
        var definitions = entries.Deserialize<WorkflowDefinitionDescriptor[]>(CerebralJson.Options)
            ?? throw new InvalidDataException("The service returned no workflow description.");
        var matches = definitions.Where(x => x is not null && x.Id == ConversationControlClient.ChatDefinitionId &&
            x.Version == DetailedToolFeedbackDefinitionVersion).Take(2).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("Detailed tool feedback is unavailable or ambiguous. Reconnect after the service configuration is corrected.");
        var definition = matches[0];
        if (definition.RequiredCapabilities is null || definition.RequiredCapabilities.Count > 128 ||
            definition.RequiredCapabilities.Any(string.IsNullOrWhiteSpace) ||
            definition.InputSchema.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The detailed tool feedback declaration is invalid.");
        return definition with { InputSchema = definition.InputSchema.Clone(), RequiredCapabilities = definition.RequiredCapabilities.ToArray() };
    }

    internal static bool ValidateMainExactWorkflowCatalog(JsonElement catalog, string id, string version)
    {
        ValidateMainChatWorkflowContract(catalog);
        if (!catalog.TryGetProperty("workflows", out var entries) ||
            entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 256)
            throw new InvalidDataException("The installed workflow catalog is invalid or too large.");
        var identities = new HashSet<(string Id, string Version)>();
        var found = false;
        foreach (var entry in entries.EnumerateArray())
        {
            var entryId = MainChatWorkflowText(entry, "id");
            var entryVersion = MainChatWorkflowText(entry, "version");
            _ = MainChatWorkflowText(entry, "description", allowEmpty: true);
            if (!identities.Add((entryId, entryVersion)))
                throw new InvalidDataException("The installed workflow catalog contains duplicate versions.");
            found |= entryId == id && entryVersion == version;
        }
        return found;
    }

    internal static WorkflowDefinitionDescriptor ValidateMainExactWorkflowDescription(JsonElement description,
        string id, string version)
    {
        ValidateMainChatWorkflowContract(description);
        if (!description.TryGetProperty("definitions", out var entries) ||
            entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 256)
            throw new InvalidDataException("The workflow description is invalid or too large.");
        var definitions = entries.Deserialize<WorkflowDefinitionDescriptor[]>(CerebralJson.Options)
            ?? throw new InvalidDataException("The service returned no workflow description.");
        var matches = definitions.Where(x => x is not null && x.Id == id && x.Version == version).Take(2).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("The exact workflow description is unavailable or ambiguous.");
        var definition = matches[0];
        if (definition.RequiredCapabilities is null || definition.RequiredCapabilities.Count > 128 ||
            definition.RequiredCapabilities.Any(string.IsNullOrWhiteSpace) ||
            definition.InputSchema.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The exact workflow declaration is invalid.");
        WorkflowSchema.ValidateSchema(definition.InputSchema);
        return definition with { InputSchema = definition.InputSchema.Clone(),
            RequiredCapabilities = definition.RequiredCapabilities.ToArray() };
    }

    private static void ValidateMainChatWorkflowContract(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.String ||
            contract.GetString() != WorkflowContract.Id ||
            !data.TryGetProperty("version", out var version) || !version.TryGetInt32(out var value) ||
            value != WorkflowContract.Version)
            throw new InvalidDataException("The service returned an incompatible workflow catalog.");
    }

    private static string MainChatWorkflowText(JsonElement value, string name, bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field) ||
            field.ValueKind != JsonValueKind.String || field.GetString() is not { } text ||
            !allowEmpty && string.IsNullOrWhiteSpace(text) || text.Length > 4096)
            throw new InvalidDataException("The workflow catalog contains an invalid " + name + ".");
        return text;
    }
}
