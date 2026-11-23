using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private ComposerDraftStore? composerDraftStore;
    private DispatcherTimer? composerDraftTimer;
    private ComposerDraftSnapshot? composerDraftSelectionToRestore;
    private bool changingComposerDraft;
    private bool restoringComposerDraftSelection;
    private bool composerDraftDirty;
    private bool composerDraftReadFailed;
    internal bool ComposerDraftCloseBlocked { get; private set; }
    private string? composerDraftStorageError;
    private string? composerDraftSelectionError;
    private string? composerDraftInitialSourceRoot;
    private ExistingWorkerDesktopOptions? composerDraftInitialAttachment;
    private ComposerDraftSnapshot? composerDraftProtectedSelection;
    private bool composerDraftInitialSourceConnectionPending;
    private long composerDraftEditGeneration;
    private ComposerDraftStartupSelection? composerDraftStartupSelection;

    private sealed record ComposerDraftStartupSelection(string? Id, string Text, string? ProjectId,
        bool IsNew, long SelectionVersion, long EditGeneration);

    private void InitializeComposerDrafts(string? isolatedDirectory, DesktopInstancePaths? paths,
        ExistingWorkerDesktopOptions? attachedStartup)
    {
        composerDraftInitialSourceRoot = paths?.SourceLocal == true ? paths.DataDirectory : null;
        if (isolatedDirectory is not null && paths is not null)
            throw new ArgumentException("Composer drafts require one explicit isolated or desktop instance binding, not both.");
        if (attachedStartup is not null &&
            (isolatedDirectory is not null || paths is null || paths.SourceLocal ||
                !ComposerDraftSameDirectory(paths.UserDataDirectory, attachedStartup.DesktopDataDirectory)))
            throw new ArgumentException("Attached composer restoration requires its exact private desktop state binding.");
        composerDraftInitialAttachment = attachedStartup;
        composerDraftInitialSourceConnectionPending = composerDraftInitialSourceRoot is not null || attachedStartup is not null;
        if (isolatedDirectory is not null)
            composerDraftStore = new(isolatedDirectory, "isolated-drafts:" + Path.GetFullPath(isolatedDirectory));
        else if (paths is not null)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User?.Value
                ?? throw new InvalidOperationException("The signed-in user identity is unavailable for composer drafts.");
            var userFolder = ComposerDraftStore.Hash(user)[..24];
            composerDraftStore = new(Path.Combine(paths.UserDataDirectory, "Desktop", "Composer", userFolder),
                paths.ApplicationIdentity.Mode + "\n" + paths.UserDataDirectory.ToUpperInvariant() + "\n" + user);
        }
        // Startup supplies its explicit instance binding even when it injects a plugin catalog.
        // Diagnostic/plugin fixtures remain memory-only unless they opt into their own binding.
        if (composerDraftStore is not null)
        {
            try
            {
                var saved = composerDraftStore.Read();
                foreach (var pair in saved.Drafts) drafts.Add(pair.Key, pair.Value);
                if (saved.SelectedConversationId is { } id &&
                    (paths?.SourceLocal == true || attachedStartup is not null || drafts.ContainsKey(id)))
                {
                    composerDraftSelectionToRestore = saved;
                    if (attachedStartup is not null && !saved.SelectedIsNew)
                        composerDraftProtectedSelection = saved;
                    if (saved.SelectedIsNew)
                    {
                        durableConversationId = id; draftProjectId = saved.SelectedProjectId;
                        nativeChat?.BeginConversation(id);
                        changingComposerDraft = true;
                        try { prompt.Text = drafts.GetValueOrDefault(id, ""); }
                        finally { changingComposerDraft = false; }
                    }
                }
            }
            catch (Exception error) when (ComposerDraftStorageFailure(error))
            {
                composerDraftReadFailed = true;
                composerDraftStorageError = "Composer drafts could not be loaded. The retained file is unchanged; repair it and reopen Cerebral. " + error.Message;
                SetStatus(composerDraftStorageError, true);
            }
        }
        composerDraftTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        composerDraftTimer.Tick += (_, _) => { composerDraftTimer.Stop(); FlushComposerDrafts(); };
        prompt.TextChanged += (_, _) =>
        {
            if (changingComposerDraft) return;
            composerDraftEditGeneration++;
            composerDraftProtectedSelection = null;
            composerDraftSelectionError = null;
            if (restoringComposerDraftSelection)
            {
                conversationSelectionVersion++;
                // Cancel the workspace read generation too: a retained composer must stay
                // bound to its own new conversation rather than an asynchronously loaded run.
                if (CurrentConversationId is { } current) nativeChat?.BeginConversation(current);
            }
            composerDraftSelectionToRestore = null;
            if (CurrentConversationId is { } id) RememberComposerDraft(id, prompt.Text, flush: false);
            composerDraftTimer.Stop(); composerDraftTimer.Start();
        };
        Closing += (_, args) =>
        {
            ComposerDraftCloseBlocked = false;
            // A failed/pending initial connection must not save the temporary reset GUID as
            // the owner of unchanged restored text. The explicit storage binding remains local.
            if (CurrentStartupComposer() is { } pending)
            {
                if (composerDraftDirty && pending.Id is { } retainedId)
                    RememberComposerDraft(retainedId, prompt.Text, flush: false);
            }
            else if (CurrentConversationId is { } id) RememberComposerDraft(id, prompt.Text, flush: false);
            if (FlushComposerDrafts()) return;
            // Closing the only live copy after a failed write would discard the current text.
            // Keep it editable so the owner can resolve the reported problem and retry.
            args.Cancel = true;
            ComposerDraftCloseBlocked = true;
            SetStatus("Draft was not saved, so Cerebral stayed open with your text. Resolve the draft problem and try closing again. " +
                (composerDraftStorageError ?? "Check the draft storage location."), true);
        };
        Closed += (_, _) => composerDraftTimer.Stop();
    }

    private void RememberComposerDraft(string id, string text, bool flush = true)
    {
        if (text.Length == 0) drafts.Remove(id);
        else drafts[id] = text;
        composerDraftDirty = true;
        if (flush) FlushComposerDrafts();
    }

    private void RestoreComposerDraftText(string id)
    {
        // The target conversation has passed its identity checks before this point.
        composerDraftProtectedSelection = null;
        composerDraftSelectionError = null;
        changingComposerDraft = true;
        try { prompt.Text = drafts.GetValueOrDefault(id, ""); }
        finally { changingComposerDraft = false; }
        RememberComposerDraft(id, prompt.Text);
    }

    private bool FlushComposerDrafts()
    {
        composerDraftTimer?.Stop();
        if (composerDraftStore is null) return true;
        if (composerDraftReadFailed) return false;
        if (!composerDraftDirty) return composerDraftStorageError is null;
        try
        {
            var startup = CurrentStartupComposer();
            var protectedSelection = composerDraftProtectedSelection;
            composerDraftStore.Write(drafts, protectedSelection?.SelectedConversationId ?? startup?.Id ?? CurrentConversationId,
                protectedSelection?.SelectedIsNew ?? startup?.IsNew ?? (session is null && nativeChat?.Snapshot?.RunId is null &&
                    !durableConversations.ContainsKey(CurrentConversationId ?? "")),
                protectedSelection is null ? (startup is null ? ConversationProjectId : startup.ProjectId)
                    : protectedSelection.SelectedProjectId);
            composerDraftDirty = false; composerDraftStorageError = null;
            return true;
        }
        catch (Exception error) when (ComposerDraftStorageFailure(error))
        {
            var message = "Composer draft could not be saved; the last saved file and current text are retained. " + error.Message;
            if (composerDraftStorageError != message) SetStatus(message, true);
            composerDraftStorageError = message;
            return false;
        }
    }

    // ConnectionChanged runs before the new endpoint is assigned and authenticated. Retain only
    // the first explicitly bound composer until Source or attached-owner validation completes.
    private bool RetainInitialSourceComposerForConnection()
    {
        if (composerDraftInitialSourceRoot is null && composerDraftInitialAttachment is null) return false;
        if (!composerDraftInitialSourceConnectionPending)
        {
            composerDraftStartupSelection = null;
            composerDraftSelectionToRestore = null;
            return false;
        }
        composerDraftInitialSourceConnectionPending = false;
        composerDraftTimer?.Stop();
        composerDraftStartupSelection = new(CurrentConversationId, prompt.Text, ConversationProjectId,
            session is null && nativeChat?.Snapshot?.RunId is null && !durableConversations.ContainsKey(CurrentConversationId ?? ""),
            conversationSelectionVersion, composerDraftEditGeneration);
        return true;
    }

    private ComposerDraftStartupSelection? CurrentStartupComposer() =>
        composerDraftStartupSelection is { } saved && saved.SelectionVersion == conversationSelectionVersion &&
        saved.EditGeneration == composerDraftEditGeneration ? saved : null;

    private bool RestoreInitialSourceComposer()
    {
        if (composerDraftStartupSelection is not { } saved) return true;
        composerDraftStartupSelection = null;
        var connection = ServiceConnection; // ConnectedAsync has already validated this exact owner route.
        var sourceMatches = ComposerDraftSameDirectory(connection.LocalDataDirectory, composerDraftInitialSourceRoot);
        var attachment = ExistingWorkerAttachment;
        var attachedMatches = composerDraftInitialAttachment is { } expected && attachment is not null &&
            attachment.Endpoint == expected.Endpoint && connection.LocalEndpoint == expected.Endpoint &&
            attachment.InstallationId == expected.InstallationId &&
            ComposerDraftSameDirectory(attachment.WorkerDataDirectory, expected.WorkerDataDirectory) &&
            ComposerDraftSameDirectory(connection.LocalDataDirectory, expected.WorkerDataDirectory);
        var bindingMatches = connection.Transport == "pipe" && (sourceMatches || attachedMatches);
        if (!bindingMatches ||
            saved.SelectionVersion != conversationSelectionVersion || saved.EditGeneration != composerDraftEditGeneration)
        {
            composerDraftSelectionToRestore = null;
            if (!bindingMatches && composerDraftInitialAttachment is not null && composerDraftProtectedSelection is not null)
                ReportComposerDraftSelectionError("The saved conversation selection remains on disk, but this attached Worker did not match its startup binding.");
            return false;
        }
        if (saved.Id is null) return true;
        session = null; durableConversationId = saved.Id; draftProjectId = saved.ProjectId;
        // The saved new selection belongs to this validated owner, but the connection reset
        // invalidated its original draft binding. Rebind only an unsent local selection.
        // A same-ID explicit profile or path choice is held in memory, outside the composer file.
        var retainedSelection = freshChatDraft is { } retained && retained.ConversationId == saved.Id &&
            (retained.Selection.ProfileId is not null || retained.Selection.ProfileRevision is not null ||
                retained.Selection.Grants.Length != 0);
        if (saved.IsNew && !durableConversations.ContainsKey(saved.Id) && !retainedSelection &&
            !freshChatInitialAccepted && freshChatPreparation is null && !freshChatInitialDispatchAttempted &&
            !NativeMutationPending && !NativeMutationUncertain &&
            (freshChatDraft is not { } current || !FreshChatDraftBindingCurrent(current)))
            InstallFreshChatDraft(new(null, null, []));
        nativeChat?.BeginConversation(saved.Id);
        changingComposerDraft = true;
        try { prompt.Text = saved.Text; }
        finally { changingComposerDraft = false; }
        FlushComposerDrafts();
        return true;
    }

    private static bool ComposerDraftSameDirectory(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private async Task RestoreComposerDraftSelectionAsync()
    {
        if (!RestoreInitialSourceComposer()) return;
        var saved = composerDraftSelectionToRestore;
        composerDraftSelectionToRestore = null;
        if (saved?.SelectedConversationId is not { } id) return;
        if (saved.SelectedIsNew && !durableConversations.ContainsKey(id)) return;
        restoringComposerDraftSelection = true;
        var selection = conversationSelectionVersion;
        try
        {
            var reopening = SelectSession(id, Lifetime.Token);
            selection = conversationSelectionVersion;
            await reopening;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (selection == conversationSelectionVersion)
                ReportComposerDraftSelectionError("The saved conversation selection and drafts are retained, but its conversation could not reopen: " + error.Message);
        }
        finally { restoringComposerDraftSelection = false; }
        if (composerDraftStorageError is { } persistenceError) SetStatus(persistenceError, true);
    }

    private void ReportComposerDraftSelectionError(string message)
    {
        composerDraftSelectionError = message[..Math.Min(message.Length, 512)];
        sessionState.Text = composerDraftSelectionError;
        SetStatus(composerDraftSelectionError, true);
    }

    private object ComposerDraftControlSnapshot()
    {
        const int limit = 20;
        var persisted = composerDraftStore?.Persisted;
        return new
        {
            enabled = composerDraftStore is not null,
            path = composerDraftStore?.FilePath,
            dirty = composerDraftDirty,
            error = composerDraftStorageError,
            selectionError = composerDraftSelectionError,
            retained = drafts.Count,
            persisted = persisted?.Drafts.Count ?? 0,
            persistedBytes = composerDraftStore?.PersistedBytes ?? 0,
            selectedConversationId = CurrentConversationId,
            selectedCharacters = prompt.Text.Length,
            selectedSha256 = BoundedDraftHash(prompt.Text),
            persistedSelectedConversationId = persisted?.SelectedConversationId,
            persistedSelectedSha256 = persisted?.SelectedConversationId is { } selected
                ? ComposerDraftStore.Hash(persisted.Drafts.GetValueOrDefault(selected, "")) : null,
            items = drafts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(limit)
                .Select(pair => new { id = pair.Key, characters = pair.Value.Length, sha256 = BoundedDraftHash(pair.Value) }).ToArray(),
            truncated = drafts.Count > limit
        };
    }

    // Oversized unsaved text is retained in the composer, but diagnostics never hash unbounded input.
    private static string? BoundedDraftHash(string text) => text.Length <= ComposerDraftStore.MaximumDraftCharacters ? ComposerDraftStore.Hash(text) : null;

    private static bool ComposerDraftStorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        JsonException or ArgumentException or InvalidDataException or InvalidOperationException;
}
