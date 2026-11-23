using System.IO;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly SemaphoreSlim nativePreferenceGate = new(1, 1);
    private bool nativePreferencePending;
    private long nativePreferenceEditGeneration;
    private ConversationPropertiesSnapshot? loadedConversationProperties;
    private long loadedPropertiesSource = -1;
    private long unsupportedPropertiesSource = -1;
    private object? unsupportedPropertiesConnection;
    private (string Id, long Source)? unconfirmedNativePreference;

    // Private fixture seams never expose a replacement endpoint or change the production owner route.
    private Func<string, object?, CancellationToken, Task<CommandResult>>? nativePreferenceCommandOverride;
    private Func<bool>? nativePreferenceBindingOverride;
    private Action<string, int?>? nativePreferenceCacheOverride;

    private bool NativePreferenceConnectionCurrent(object connection, string identity) =>
        nativePreferenceBindingOverride?.Invoke() ??
        (IsConnected && ReferenceEquals(connection, ServiceConnection) && identity == ServiceConnectionIdentity);

    private Func<string, object?, CancellationToken, Task<CommandResult>> NativePreferenceCommand(CerebralClient? captured) =>
        nativePreferenceCommandOverride ?? ((operation, arguments, token) =>
            captured!.CommandAsync(operation, arguments, cancellationToken: token));

    private void CacheNativeResponsePreference(string id, int? share)
    {
        if (nativePreferenceCacheOverride is { } sink) sink(id, share);
        else SaveChatResponseBalance();
    }

    private void AcceptNativePreferenceSnapshot(ConversationPropertiesSnapshot saved, long source)
    {
        loadedConversationProperties = saved; loadedPropertiesSource = source;
        if (unconfirmedNativePreference == (saved.Id, source)) unconfirmedNativePreference = null;
    }

    private async Task<ConversationPropertiesSnapshot?> ReadNativePropertiesAsync(
        Func<string, object?, CancellationToken, Task<CommandResult>> command, string id, long source,
        Func<bool> current, CancellationToken token)
    {
        if (unsupportedPropertiesSource == source && ReferenceEquals(unsupportedPropertiesConnection, ServiceConnection))
            return null;
        token.ThrowIfCancellationRequested();
        var result = await command("conversations.properties.get", new { id }, token);
        if (!result.Success && result.Error == "Unknown command: conversations.properties.get")
        {
            // Only the exact old-Worker command response permits compatibility fallback.
            if (current()) { unsupportedPropertiesSource = source; unsupportedPropertiesConnection = ServiceConnection; }
            return null;
        }
        if (!result.Success) throw new InvalidOperationException(result.Error);
        var saved = CerebralJson.Read<ConversationPropertiesSnapshot>(result.Data);
        ConversationPropertiesSnapshot.Validate(saved, id);
        token.ThrowIfCancellationRequested();
        return saved;
    }

    private async Task LoadNativePropertiesAsync(ConversationSummary summary, long selection, CancellationToken token)
    {
        var binding = PluginBinding(); var source = knowledgeConnectionVersion;
        var editGeneration = nativePreferenceEditGeneration;
        var connection = ServiceConnection; var identity = ServiceConnectionIdentity;
        bool Current() => !Lifetime.IsCancellationRequested && NativePreferenceConnectionCurrent(connection, identity) &&
            selection == conversationSelectionVersion && binding == PluginBinding() && source == knowledgeConnectionVersion;
        if (!Current()) return;
        using var captured = nativePreferenceCommandOverride is null ? CreateIndependentClient() : null;
        var saved = await ReadNativePropertiesAsync(NativePreferenceCommand(captured), summary.Id, source, Current, token);
        if (!Current()) return;
        if (saved is null) { loadedConversationProperties = null; loadedPropertiesSource = source; return; }
        if (saved.ProfileId != summary.ProfileId || saved.ProjectId != summary.ProjectId)
            throw new InvalidDataException("The chat Properties do not match the saved conversation.");
        var recovering = unconfirmedNativePreference == (saved.Id, source);
        AcceptNativePreferenceSnapshot(saved, source);
        // A same-chat explicit edit owns its local value until saved; selection changes reset it before loading.
        if (nativePreferenceEditGeneration != editGeneration || nativePreferencePending && !recovering) return;
        selectedResponseSharePercent = saved.ResponseSharePercent;
        nativePreferencePending = false; nativePreferenceEditGeneration++;
        CacheNativeResponsePreference(saved.Id, saved.ResponseSharePercent);
    }

    private async Task<ConversationPropertiesSnapshot?> PersistNativeResponsePreferenceAsync(
        DesktopConversationBinding binding, int? share, CancellationToken token, bool onlyIfStillSelected = false)
    {
        if (durableConversationId is { } exactId && exactWorkflowConversations.ContainsKey(exactId))
            throw new InvalidOperationException("Response-balance preferences are available only for ordinary chats.");
        AgentTokenLimits.ValidateResponseShare(share, nameof(share));
        if (!IsNativeChat || nativeChat?.Snapshot?.RunId is null) return null; // A draft has no saved Worker chat yet.
        var id = durableConversationId!; var source = knowledgeConnectionVersion;
        var profileId = ConversationProfile().Id; var projectId = ConversationProjectId;
        var editGeneration = nativePreferenceEditGeneration;
        var connection = ServiceConnection; var identity = ServiceConnectionIdentity;
        bool Current() => !Lifetime.IsCancellationRequested && NativePreferenceConnectionCurrent(connection, identity) &&
            binding == PluginBinding() && source == knowledgeConnectionVersion &&
            nativePreferenceEditGeneration == editGeneration &&
            (!onlyIfStillSelected || selectedResponseSharePercent == share);
        await nativePreferenceGate.WaitAsync(token);
        try
        {
            if (!Current())
            {
                if (onlyIfStillSelected) return null;
                throw new InvalidOperationException("The chat, preference or connection changed before saving.");
            }
            using var captured = nativePreferenceCommandOverride is null ? CreateIndependentClient() : null;
            var command = NativePreferenceCommand(captured);
            var saved = await ReadNativePropertiesAsync(command, id, source, Current, token);
            if (!Current())
            {
                if (onlyIfStillSelected) return null;
                throw new InvalidOperationException("The chat, preference or connection changed while reading settings.");
            }
            if (saved is null) return null;
            if (saved.ProfileId != profileId || saved.ProjectId != projectId)
                throw new InvalidDataException("The saved chat identity changed. Reopen the conversation before saving.");
            var recovering = unconfirmedNativePreference == (id, source);
            AcceptNativePreferenceSnapshot(saved, source);
            if (recovering && onlyIfStillSelected)
            {
                // A fresh read recovers current settings, not proof that the previous write succeeded.
                // Automatic submission must never resend the old uncertain slider value.
                selectedResponseSharePercent = saved.ResponseSharePercent;
                nativePreferencePending = false; nativePreferenceEditGeneration++;
                CacheNativeResponsePreference(id, saved.ResponseSharePercent); RefreshConversationTokenBalance();
                return saved;
            }
            if (saved.ResponseSharePercent == share) return saved;
            token.ThrowIfCancellationRequested();
            var dispatched = false;
            try
            {
                dispatched = true;
                var result = await command("conversations.properties.save",
                    new { id, expectedRevision = saved.Revision, responseSharePercent = share }, token);
                if (!result.Success) { dispatched = false; throw new InvalidOperationException(result.Error); }
                var accepted = CerebralJson.Read<ConversationPropertiesSnapshot>(result.Data);
                ConversationPropertiesSnapshot.Validate(accepted, id);
                if (accepted.ProfileId != saved.ProfileId || accepted.ProjectId != saved.ProjectId ||
                    accepted.Root != saved.Root || accepted.RootState != saved.RootState ||
                    accepted.ResponseSharePercent != share || accepted.Revision != checked(saved.Revision + 1))
                    throw new InvalidDataException("Chat preference acceptance is unconfirmed. Reopen Properties to reload the saved settings.");
                dispatched = false; // A validated matching receipt confirms this exact mutation, even if its UI binding became stale.
                if (Current()) { AcceptNativePreferenceSnapshot(accepted, source); return accepted; }
                if (onlyIfStillSelected) return null;
                throw new InvalidOperationException("The preference was saved through the previous binding. Reopen this chat before another change.");
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                if (dispatched) unconfirmedNativePreference = (id, source);
                throw;
            }
        }
        finally { nativePreferenceGate.Release(); }
    }

    private async Task PersistSelectedNativeResponsePreferenceAsync()
    {
        var binding = PluginBinding(); var share = selectedResponseSharePercent;
        var generation = nativePreferenceEditGeneration;
        var accepted = await PersistNativeResponsePreferenceAsync(binding, share, Lifetime.Token, onlyIfStillSelected: true);
        if (accepted is not null && binding == PluginBinding() && generation == nativePreferenceEditGeneration)
            nativePreferencePending = false;
    }

    private async Task<string?> NativeSubmissionRootAsync(AgentProfile profile,
        WorkflowConversationSnapshot snapshot, DesktopSubmission input, CancellationToken token)
    {
        if (input.Binding != PluginBinding() || snapshot.ConversationId != durableConversationId)
            throw new InvalidOperationException("The conversation changed while resolving its workspace.");
        if (snapshot.Run is { } exactRun && exactRun.DefinitionId != ConversationControlClient.ChatDefinitionId)
        {
            if (!exactWorkflowConversations.TryGetValue(snapshot.ConversationId, out var exact) ||
                exact.DefinitionId != exactRun.DefinitionId || exact.DefinitionVersion != exactRun.DefinitionVersion ||
                exact.Summary.ProfileId != profile.Id || exact.Summary.ProjectId != ConversationProjectId)
                throw new InvalidDataException("Reopen the saved exact workflow before preparing another turn.");
            if (ConversationProjectId is not { } exactProjectId) return null;
            var projects = EffectiveNavigation().Where(node => node.Id == exactProjectId && node.Kind == "project").Take(2).ToArray();
            if (projects.Length != 1 || projects[0].Path is not { } exactRoot || !Path.IsPathFullyQualified(exactRoot))
                throw new InvalidOperationException("The saved exact workflow project is no longer registered. Review its project before continuing.");
            return exactRoot;
        }
        if (snapshot.Run is not null)
        {
            var source = knowledgeConnectionVersion; var generation = nativePreferenceEditGeneration;
            var connection = ServiceConnection; var identity = ServiceConnectionIdentity;
            bool Current() => !Lifetime.IsCancellationRequested && NativePreferenceConnectionCurrent(connection, identity) &&
                input.Binding == PluginBinding() && source == knowledgeConnectionVersion &&
                snapshot.ConversationId == durableConversationId;
            if (!Current()) throw new InvalidOperationException("Reconnect before resolving this conversation's workspace.");
            using var captured = nativePreferenceCommandOverride is null ? CreateIndependentClient() : null;
            var saved = await ReadNativePropertiesAsync(NativePreferenceCommand(captured), snapshot.ConversationId, source, Current, token);
            if (!Current()) throw new InvalidOperationException("The conversation or connection changed while resolving its workspace.");
            if (saved is not null)
            {
                if (saved.ProfileId != profile.Id || saved.ProjectId != ConversationProjectId)
                    throw new InvalidDataException("The saved chat identity changed. Reopen the conversation before sending.");
                var recovering = unconfirmedNativePreference == (saved.Id, source);
                AcceptNativePreferenceSnapshot(saved, source);
                if (generation == nativePreferenceEditGeneration && (!nativePreferencePending || recovering))
                {
                    selectedResponseSharePercent = saved.ResponseSharePercent;
                    if (recovering) { nativePreferencePending = false; nativePreferenceEditGeneration++; }
                    CacheNativeResponsePreference(saved.Id, saved.ResponseSharePercent); RefreshConversationTokenBalance();
                }
                // An explicit cleared override is authoritative too; never replace it with a
                // sidebar/default path just because the effective working folder is unbound.
                return saved.Root;
            }
            return null; // An older owner route supplies its own accepted legacy binding.
        }
        var projectRoot = EffectiveNavigation().FirstOrDefault(x => x.Id == ConversationProjectId && x.Kind == "project")?.Path;
        if (string.IsNullOrWhiteSpace(projectRoot)) return null;
        // Only an explicitly selected project supplies an initial folder. Starting a general
        // conversation never derives a filesystem name from its internal identity.
        var root = ConversationPathSnapshot.CanonicalPath(projectRoot);
        if (!profile.AllowedRoots.Any(allowed => NavigationPathInside(root, allowed)))
            throw new InvalidOperationException("The selected project's working folder is outside this profile's granted paths. Review its owner Settings before using that folder.");
        return root;
    }

    private string? LoadedNativeWorkingRoot(AgentProfile profile) =>
        IsNativeChat && loadedPropertiesSource == knowledgeConnectionVersion &&
        loadedConversationProperties is { RootState: "bound", Root: { } root } saved &&
        saved.Id == durableConversationId && saved.ProfileId == profile.Id &&
        profile.AllowedRoots.Any(allowed => NavigationPathInside(root, allowed)) ? root : null;
}
