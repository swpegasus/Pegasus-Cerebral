using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Real scoped file I/O and hidden WPF controls with deterministic transport delegates.</summary>
    internal static async Task CheckComposerDraftsAsync()
    {
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Composer checks require the WPF dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var root = Path.Combine(Path.GetTempPath(), "PegasusCerebral.ComposerDraftTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var windows = new List<OrchestratorWindow>();
        var checks = 0;
        const string unicode = "Draft persistence verified — café.\nSecond line.\r\n日本語 🎙";
        void Check(bool value, string label)
        { if (!value) throw new InvalidOperationException(label); checks++; }
        void Reject(Action operation, string label)
        {
            var failed = false;
            try { operation(); }
            catch (Exception error) when (ComposerDraftStorageFailure(error)) { failed = true; }
            Check(failed, label);
        }
        ComposerDraftStore Store(string name, string owner = "fixture-owner") => new(Path.Combine(root, name), owner);
        OrchestratorWindow Window(string name)
        {
            var area = Path.Combine(root, name);
            var window = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(area, "plugins")),
                Path.Combine(area, "plugin-data"), Path.Combine(area, "drafts"));
            windows.Add(window); return window;
        }
        static WorkflowRun Run(string id, string conversation, string text) => new()
        {
            Id = id, Revision = 1, Status = WorkflowStatus.Completed,
            Submission = new(id, conversation, "fixture-lead", ConversationControlClient.ChatDefinitionId,
                ConversationControlClient.ChatDefinitionVersion, text, null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
        };
        static CommandResult Events() => CommandResult.Ok(new { events = Array.Empty<CerebralEvent>(), snapshotSequence = 0, nextBeforeSequence = (long?)null });
        try
        {
            // A dedicated check process selects a temporary normal binding before any window.
            // Default diagnostic construction must never adopt its seeded ordinary draft state.
            var processPaths = DesktopInstancePaths.UseSource(Path.Combine(root, "process-source-data"),
                Path.Combine(root, "process-source-program"));
            var store = Store("files");
            Check(store.Read().Drafts.Count == 0 && !Directory.Exists(Path.GetDirectoryName(store.FilePath)),
                "A missing snapshot is an empty read without a write.");
            store.Write(new Dictionary<string, string> { ["chat-a"] = unicode, ["chat-b"] = "Other draft" }, "chat-a", true, "project-a");
            var retained = File.ReadAllBytes(store.FilePath);
            var saved = Store("files").Read();
            Check(saved.Drafts["chat-a"] == unicode && saved.Drafts["chat-b"] == "Other draft" &&
                saved.SelectedConversationId == "chat-a" && saved.SelectedProjectId == "project-a" && saved.SelectedIsNew,
                "Unicode, exact newlines, every retained draft and selection must survive a real reload.");
            Check(Encoding.UTF8.GetString(retained).Contains("café", StringComparison.Ordinal) &&
                !retained.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }),
                "The retained UTF-8 text preserves Unicode without a BOM.");
            var foreign = Store("files", "other-user-or-instance");
            Reject(() => foreign.Read(), "Another owner must not adopt this snapshot.");
            Reject(() => foreign.Write(new Dictionary<string, string> { ["foreign"] = "Text" }, "foreign", true, null),
                "An unread foreign snapshot must not be replaced.");
            Check(retained.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Foreign admission failure retains the file.");
            var separate = Store("different-instance");
            Check(separate.Read().Drafts.Count == 0, "Explicit instance roots cannot adopt each other's drafts.");
            Reject(() => store.Write(new Dictionary<string, string> { ["too-large"] = new('x', ComposerDraftStore.MaximumDraftCharacters + 1) }, null, true, null),
                "An oversized draft must fail without truncation.");
            Reject(() => store.Write(Enumerable.Range(0, ComposerDraftStore.MaximumDrafts + 1).ToDictionary(i => "chat-" + i, _ => "x"), null, true, null),
                "Too many drafts must fail without eviction.");
            Reject(() => store.Write(Enumerable.Range(0, 10).ToDictionary(i => "chat-" + i, _ => new string('x', 900_000)), null, true, null),
                "An oversized serialized snapshot must fail before replacing the last saved file.");
            Check(retained.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Every rejected write keeps the last-good snapshot.");
            using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Reject(() => store.Write(new Dictionary<string, string> { ["chat-a"] = "New" }, "chat-a", true, null),
                    "An actual sharing violation must be reported as a failed save.");
            Check(retained.SequenceEqual(File.ReadAllBytes(store.FilePath)) &&
                Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!).Length == 1,
                "A failed atomic replacement preserves the file and cleans its temporary output.");
            store.Write(new Dictionary<string, string> { ["chat-a"] = "Recovered" }, "chat-a", true, null);
            Check(Store("files").Read().Drafts["chat-a"] == "Recovered", "Saving can recover after the sharing lock is released.");

            var malformed = Store("malformed"); Directory.CreateDirectory(Path.GetDirectoryName(malformed.FilePath)!);
            var goodText = Encoding.UTF8.GetString(retained);
            foreach (var text in new[] { "{", goodText.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
                goodText.Replace("\"drafts\":{", "\"drafts\":{\"chat-a\":\"duplicate\",", StringComparison.Ordinal),
                goodText.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
                goodText[..^1] + ",\"unknown\":true}" })
            {
                File.WriteAllText(malformed.FilePath, text, new UTF8Encoding(false));
                var failedStore = Store("malformed");
                Reject(() => failedStore.Read(), "Malformed, repeated, unknown or unsupported data must be rejected.");
                Reject(() => failedStore.Write(new Dictionary<string, string> { ["new"] = "New" }, null, true, null),
                    "A load failure must prevent replacement of retained evidence.");
                Check(File.ReadAllText(malformed.FilePath) == text, "A rejected snapshot is unchanged.");
            }
            File.WriteAllBytes(malformed.FilePath, [0xc0, 0xaf]);
            Reject(() => Store("malformed").Read(), "Invalid UTF-8 must be rejected.");
            using (var oversized = File.Create(malformed.FilePath)) oversized.SetLength(ComposerDraftStore.MaximumBytes + 1L);
            Reject(() => Store("malformed").Read(), "An oversized file must be rejected by its bounded read.");

            // Match RunSourceLocal's real construction: injected catalog/data plus an explicit
            // normal instance binding.
            var sourcePaths = DesktopInstancePaths.ForSource(Path.Combine(root, "normal-source-data"),
                Path.Combine(root, "normal-source-program"));
            OrchestratorWindow SourceWindow(DesktopInstancePaths paths)
            {
                var window = new OrchestratorWindow(new DesktopPluginCatalog(paths.PluginDirectory),
                    paths.PluginDataDirectory, composerDraftPaths: paths);
                windows.Add(window); return window;
            }
            // A retained ordinary draft exists under the process default. A parameterless
            // diagnostic window must ignore it and leave its bytes unchanged after edits/close.
            // Match installed startup's null catalog/data constructor with an explicit normal binding.
            var ordinary = new OrchestratorWindow(null, null, composerDraftPaths: processPaths); windows.Add(ordinary);
            ordinary.prompt.Text = "Ordinary state must remain unchanged";
            Check(CerebralJson.Element(ordinary.ComposerDraftControlSnapshot()).GetProperty("enabled").GetBoolean() &&
                ordinary.FlushComposerDrafts(), "The installed-startup constructor pattern must enable and seed its explicit retained draft file.");
            var ordinaryPath = ordinary.composerDraftStore!.FilePath;
            ordinary.Close();
            var ordinaryBytes = File.ReadAllBytes(ordinaryPath);
            var diagnostic = new OrchestratorWindow(); windows.Add(diagnostic);
            Check(diagnostic.composerDraftStore is null && diagnostic.prompt.Text.Length == 0 &&
                diagnostic.drafts.Count == 0 && !CerebralJson.Element(diagnostic.ComposerDraftControlSnapshot()).GetProperty("enabled").GetBoolean(),
                "Parameterless diagnostic construction cannot adopt the normal instance's retained composer state.");
            var diagnosticSet = await diagnostic.HandleDesktopControlAsync(new(Guid.NewGuid().ToString("N"), "diagnostic-set", "composer.set",
                CerebralJson.Element(new { text = "Diagnostic fixture text" })), deadline.Token);
            Check(diagnosticSet.Success && !diagnosticSet.Data.GetProperty("durable").GetBoolean(),
                "Parameterless diagnostic assignment must not claim persistence.");
            diagnostic.Close();
            Check(ordinaryBytes.SequenceEqual(File.ReadAllBytes(ordinaryPath)),
                "Editing and closing a default diagnostic window cannot change ordinary draft bytes.");

            var source = SourceWindow(sourcePaths);
            var sourceId = source.CurrentConversationId!;
            var sourceMetadata = CerebralJson.Element(source.ComposerDraftControlSnapshot());
            Check(sourceMetadata.GetProperty("enabled").GetBoolean() && source.composerDraftStore is not null &&
                source.composerDraftStore.FilePath.StartsWith(sourcePaths.UserDataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Normal Source catalog injection must enable draft storage inside its explicit instance root.");
            var sourceRestore = await source.HandleDesktopControlAsync(new(Guid.NewGuid().ToString("N"), "normal-source-restore", "composer.drafts.restore",
                CerebralJson.Element(new { items = new[] { new { id = sourceId, text = unicode } } })), deadline.Token);
            Check(sourceRestore.Success && sourceRestore.Data.GetProperty("durable").GetBoolean() &&
                source.composerDraftStore!.Read().Drafts[sourceId] == unicode,
                "Normal Source restoration must report durability only after the actual file write.");
            source.Close();
            var sourceReopened = SourceWindow(sourcePaths);
            Check(sourceReopened.CurrentConversationId == sourceId && sourceReopened.prompt.Text == unicode &&
                sourceReopened.composerDraftStore!.FilePath == source.composerDraftStore!.FilePath,
                "A normal Source constructor must reopen the saved text and same storage binding.");
            void FixtureConnection(OrchestratorWindow window, DesktopInstancePaths paths)
            {
                // Simulate the already validated post-connect assignment. No pipe is opened.
                typeof(ConnectedWindow).GetField("activeConnection", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, ConnectionPreferences.ForLocalPipe(new("fixture-draft-pipe", Guid.NewGuid().ToString("N")), paths.DataDirectory));
            }
            CerebralClient FixtureClient(OrchestratorWindow window, Func<CommandRequest, CommandResult> dispatch)
            {
                var fixture = new CerebralClient();
                var http = typeof(CerebralClient).GetField("http", BindingFlags.Instance | BindingFlags.NonPublic)!;
                ((HttpClient)http.GetValue(fixture)!).Dispose();
                http.SetValue(fixture, new HttpClient(new ComposerDraftFixtureHttpHandler(dispatch))
                    { BaseAddress = new Uri("http://fixture.invalid/") });
                var field = typeof(ConnectedWindow).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!;
                ((CerebralClient)field.GetValue(window)!).Dispose();
                field.SetValue(window, fixture);
                return fixture;
            }
            var startupDraftBytes = File.ReadAllBytes(sourceReopened.composerDraftStore!.FilePath);
            sourceReopened.ConnectionChanged();
            FixtureConnection(sourceReopened, sourcePaths);
            await sourceReopened.RestoreComposerDraftSelectionAsync();
            Check(sourceReopened.CurrentConversationId == sourceId && sourceReopened.nativeChat!.Snapshot?.ConversationId == sourceId &&
                sourceReopened.prompt.Text == unicode && sourceReopened.drafts.Count == 1 &&
                sourceReopened.drafts[sourceId] == unicode,
                "The first matching Source connection must preserve a restored new draft's GUID, exact Unicode and sole key.");
            Check(sourceReopened.FlushComposerDrafts() && startupDraftBytes.SequenceEqual(File.ReadAllBytes(sourceReopened.composerDraftStore.FilePath)),
                "Matching initial Source connection must not rewrite the restored draft snapshot.");
            sourceReopened.ConnectionChanged();
            var switchedId = sourceReopened.CurrentConversationId;
            FixtureConnection(sourceReopened, sourcePaths);
            await sourceReopened.RestoreComposerDraftSelectionAsync();
            Check(switchedId != sourceId && sourceReopened.CurrentConversationId == switchedId &&
                sourceReopened.composerDraftStartupSelection is null && sourceReopened.composerDraftSelectionToRestore is null &&
                sourceReopened.drafts[sourceId] == unicode,
                "A later genuine connection replacement keeps its ordinary new identity and cannot replay startup restoration.");

            var blankPaths = DesktopInstancePaths.ForSource(Path.Combine(root, "blank-source-data"), sourcePaths.InstallDirectory);
            var blankSeed = SourceWindow(blankPaths); var blankStore = blankSeed.composerDraftStore!; blankSeed.Close();
            var blankId = Guid.NewGuid().ToString("N");
            var otherDrafts = new Dictionary<string, string>
            {
                ["other-one"] = unicode, ["other-two"] = "Retained other draft", ["other-three"] = "Third retained draft"
            };
            blankStore.Write(otherDrafts, blankId, false, "fixture-project");
            var blankBytes = File.ReadAllBytes(blankStore.FilePath);
            var blank = SourceWindow(blankPaths);
            Check(blank.composerDraftSelectionToRestore?.SelectedConversationId == blankId && !blank.drafts.ContainsKey(blankId),
                "A saved blank composer must retain its selected existing-chat metadata without manufacturing a text entry.");
            blank.ConnectionChanged(); FixtureConnection(blank, blankPaths);
            var blankRun = Run("blank-saved-run", blankId, "Fixture saved request");
            var blankSummary = new ConversationSummary(blankId, "Saved blank composer", "fixture-lead", "fixture-project",
                blankRun.Id, WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
            using var blankWorkspace = new WorkflowWorkspace((input, _) => Task.FromResult(input.Operation switch
            {
                "jobs.workflow.get" => CommandResult.Ok(blankRun),
                "jobs.workflow.events.previous" => Events(),
                _ => throw new InvalidOperationException("Unexpected blank startup fixture operation: " + input.Operation)
            }), () => Array.Empty<AgentProfile>(), (_, _) => { }, embedded: true);
            blank.nativeChat = blankWorkspace;
            blank.nativePreferenceBindingOverride = () => false; // No optional owner Properties read in this fixture.
            blank.durableConversations[blankId] = blankSummary;
            using var blankClient = new CerebralClient();
            var httpField = typeof(CerebralClient).GetField("http", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((HttpClient)httpField.GetValue(blankClient)!).Dispose();
            httpField.SetValue(blankClient, new HttpClient(new ComposerDraftFixtureHttpHandler(input =>
                input.Operation == "interface.list" ? CommandResult.Ok(Array.Empty<InterfaceRequest>()) :
                    throw new InvalidOperationException("Unexpected blank owner fixture operation: " + input.Operation)))
                { BaseAddress = new Uri("http://fixture.invalid/") });
            var clientField = typeof(ConnectedWindow).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((CerebralClient)clientField.GetValue(blank)!).Dispose(); clientField.SetValue(blank, blankClient);
            await blank.RestoreComposerDraftSelectionAsync();
            Check(blank.CurrentConversationId == blankId && blank.nativeChat.Snapshot?.RunId == blankRun.Id &&
                blank.prompt.Text.Length == 0 && blank.drafts.Count == otherDrafts.Count && !blank.drafts.ContainsKey(blankId),
                "The matching first Source connection actually reopens the saved blank chat and preserves other draft keys.");
            Check(blank.FlushComposerDrafts() && blankBytes.SequenceEqual(File.ReadAllBytes(blankStore.FilePath)),
                "Blank-chat restoration retains the exact previous draft file bytes and selected existing-chat metadata.");
            var newBlank = await blank.HandleDesktopControlAsync(new(Guid.NewGuid().ToString("N"), "new-blank-fixture",
                "conversation.new", CerebralJson.Element(new { })), deadline.Token);
            var newBlankId = blank.CurrentConversationId!;
            var newBlankSaved = blankStore.Read();
            Check(newBlank.Success && newBlank.Data.GetProperty("conversationId").GetString() == newBlankId &&
                newBlankId != blankId && blank.prompt.Text.Length == 0 &&
                newBlankSaved.SelectedConversationId == newBlankId && newBlankSaved.SelectedIsNew &&
                newBlankSaved.SelectedProjectId == "fixture-project" && !blank.composerDraftDirty,
                "Normal New on an already blank existing chat persists the acknowledged new selection and project immediately.");
            Check(newBlankSaved.Drafts.Count == otherDrafts.Count && !newBlankSaved.Drafts.ContainsKey(newBlankId) &&
                otherDrafts.All(pair => newBlankSaved.Drafts.TryGetValue(pair.Key, out var text) && text == pair.Value),
                "Normal blank New preserves all three unrelated draft bodies without making an empty draft entry.");

            OrchestratorWindow SeedStartupSource(string name, out DesktopInstancePaths paths, out string id)
            {
                paths = DesktopInstancePaths.ForSource(Path.Combine(root, name), sourcePaths.InstallDirectory);
                var seed = SourceWindow(paths); id = seed.CurrentConversationId!;
                seed.draftProjectId = "startup-project"; seed.prompt.Text = unicode; seed.Close();
                return SourceWindow(paths);
            }
            var historySource = SeedStartupSource("history-restored-source", out var historyPaths, out var historyId);
            historySource.ConnectionChanged(); FixtureConnection(historySource, historyPaths);
            var historyCalls = new ConcurrentQueue<string>();
            using var historyClient = FixtureClient(historySource, input =>
            {
                historyCalls.Enqueue(input.Operation);
                return input.Operation switch
                {
                    "conversations.workspace.get" => CommandResult.Ok(),
                    "projects.knowledge.get" => CommandResult.Ok(new ProjectKnowledgeSettings { ProjectId = "startup-project" }),
                    "interface.list" => CommandResult.Ok(Array.Empty<InterfaceRequest>()),
                    _ => throw new InvalidOperationException("Unexpected history fixture operation: " + input.Operation)
                };
            });
            // The ordinary post-connect path has already marked the validated owner connected.
            typeof(ConnectedWindow).GetProperty("IsConnected", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(historySource, true);
            await historySource.RestoreComposerDraftSelectionAsync();
            Check(historySource.CurrentConversationId == historyId && historySource.IsUnsentLocalConversation &&
                historySource.freshChatDraft is { } restoredDraft && restoredDraft.Selection.ProfileId is null,
                "The validated first connection rebinds a restored unsent no-lead draft to its original ID.");
            historySource.NewConversationInProject(null);
            var nextHistoryId = historySource.CurrentConversationId;
            Check(nextHistoryId != historyId && historySource.conversationHistory.CanBack,
                "Leaving the restored draft records it through the ordinary navigation path.");
            var historyBack = await historySource.NavigateConversationHistoryCoreAsync(back: true);
            Check(historyBack.State == ConversationHistoryNavigationState.Moved &&
                historySource.CurrentConversationId == historyId && historySource.prompt.Text == unicode &&
                historySource.conversationHistory.CanForward,
                "Back restores the exact saved draft ID and text after a real startup restore.");
            var historyForward = await historySource.NavigateConversationHistoryCoreAsync(back: false);
            Check(historyForward.State == ConversationHistoryNavigationState.Moved &&
                historySource.CurrentConversationId == nextHistoryId && historySource.conversationHistory.CanBack,
                "Forward returns to the next new chat after restoring the startup draft.");
            Check(historyCalls.All(operation => operation is "conversations.workspace.get" or
                "projects.knowledge.get" or "interface.list"),
                "Restored-draft navigation permits only bounded in-memory read fixtures: " +
                string.Join(",", historyCalls.Distinct(StringComparer.Ordinal)));

            var selectedSource = SeedStartupSource("selected-restored-source", out var selectedPaths, out var selectedId);
            selectedSource.ConnectionChanged(); FixtureConnection(selectedSource, selectedPaths);
            using var selectedClient = FixtureClient(selectedSource, input =>
                throw new InvalidOperationException("Unexpected selected-draft fixture operation: " + input.Operation));
            typeof(ConnectedWindow).GetProperty("IsConnected", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(selectedSource, true);
            selectedSource.durableConversationId = selectedId;
            selectedSource.InstallFreshChatDraft(new("retained-lead", 3, []));
            var retainedSelection = selectedSource.freshChatDraft;
            await selectedSource.RestoreComposerDraftSelectionAsync();
            Check(ReferenceEquals(selectedSource.freshChatDraft, retainedSelection) &&
                selectedSource.IsUnsentLocalConversation &&
                selectedSource.freshChatDraft!.Selection.ProfileId == "retained-lead",
                "Startup restoration must preserve a current same-ID explicit profile selection.");

            var pendingClose = SeedStartupSource("pending-close-source", out _, out var pendingId);
            var pendingStore = pendingClose.composerDraftStore!;
            var pendingBytes = File.ReadAllBytes(pendingStore.FilePath);
            pendingClose.ConnectionChanged();
            Check(pendingClose.CurrentConversationId != pendingId && pendingClose.CurrentStartupComposer()?.Id == pendingId &&
                !pendingClose.composerDraftTimer!.IsEnabled,
                "A pending first connection retains only the old startup identity and stops delayed draft autosave.");
            pendingClose.Close();
            Check(pendingBytes.SequenceEqual(File.ReadAllBytes(pendingStore.FilePath)) &&
                pendingStore.Read().Drafts.Count == 1 && pendingStore.Read().Drafts[pendingId] == unicode,
                "Closing before initial connection validation keeps the exact saved snapshot without a duplicate reset-GUID draft.");

            var beforeConnectEdit = SeedStartupSource("preconnect-edited-source", out var beforeConnectPaths, out var beforeConnectId);
            const string startupEdit = "Owner edit before connection — café.\nStill the same new draft.";
            beforeConnectEdit.prompt.Text = startupEdit;
            Check(beforeConnectEdit.composerDraftDirty && beforeConnectEdit.composerDraftTimer!.IsEnabled,
                "A real owner edit before first connection starts the ordinary delayed save.");
            beforeConnectEdit.ConnectionChanged();
            Check(!beforeConnectEdit.composerDraftTimer!.IsEnabled && beforeConnectEdit.FlushComposerDrafts(),
                "The connection reset stops delayed autosave and a pending flush uses the retained original identity.");
            var pendingEditedSnapshot = beforeConnectEdit.composerDraftStore!.Read();
            Check(pendingEditedSnapshot.SelectedConversationId == beforeConnectId && pendingEditedSnapshot.SelectedIsNew &&
                pendingEditedSnapshot.SelectedProjectId == "startup-project" && pendingEditedSnapshot.Drafts.Count == 1 &&
                pendingEditedSnapshot.Drafts[beforeConnectId] == startupEdit,
                "A pre-connect edit saves under its original GUID and project even while connection validation is pending.");
            FixtureConnection(beforeConnectEdit, beforeConnectPaths);
            await beforeConnectEdit.RestoreComposerDraftSelectionAsync();
            Check(beforeConnectEdit.CurrentConversationId == beforeConnectId && beforeConnectEdit.prompt.Text == startupEdit &&
                beforeConnectEdit.ConversationProjectId == "startup-project" && beforeConnectEdit.drafts.Count == 1 &&
                beforeConnectEdit.composerDraftStore.Read().Drafts[beforeConnectId] == startupEdit,
                "First validated Source connection retains the original GUID, current owner text and project after a pending save.");

            var mismatch = SeedStartupSource("mismatched-source", out var mismatchPaths, out var mismatchId);
            mismatch.ConnectionChanged(); var mismatchFresh = mismatch.CurrentConversationId;
            FixtureConnection(mismatch, sourcePaths); await mismatch.RestoreComposerDraftSelectionAsync();
            Check(mismatch.CurrentConversationId == mismatchFresh && mismatchFresh != mismatchId &&
                mismatch.composerDraftSelectionToRestore is null && mismatch.drafts[mismatchId] == unicode,
                "A first connection to another Source root cannot adopt the retained draft's identity or saved-chat selection.");
            var edited = SeedStartupSource("edited-source", out var editedPaths, out var editedId);
            edited.ConnectionChanged(); var editedFresh = edited.CurrentConversationId!;
            edited.prompt.Text = "New owner text after reset";
            FixtureConnection(edited, editedPaths); await edited.RestoreComposerDraftSelectionAsync();
            Check(edited.CurrentConversationId == editedFresh && edited.prompt.Text == "New owner text after reset" &&
                edited.drafts[editedId] == unicode && edited.drafts[editedFresh] == "New owner text after reset",
                "Typing after connection reset wins over the queued restored text and preserves the original draft.");
            var navigated = SeedStartupSource("navigated-source", out var navigatedPaths, out var navigatedId);
            navigated.ConnectionChanged(); navigated.NewConversationInProject(null);
            var navigatedFresh = navigated.CurrentConversationId;
            FixtureConnection(navigated, navigatedPaths); await navigated.RestoreComposerDraftSelectionAsync();
            Check(navigated.CurrentConversationId == navigatedFresh && navigatedFresh != navigatedId &&
                navigated.prompt.Text.Length == 0 && navigated.drafts[navigatedId] == unicode,
                "Owner navigation during startup wins over the retained initial composer identity.");

            var otherSourcePaths = DesktopInstancePaths.ForSource(Path.Combine(root, "other-normal-source-data"),
                Path.Combine(root, "normal-source-program"));
            var otherSource = SourceWindow(otherSourcePaths);
            Check(otherSource.composerDraftStore!.Read().Drafts.Count == 0 && otherSource.prompt.Text.Length == 0,
                "Two normal Source bindings cannot adopt one another's draft data.");
            var memoryArea = Path.Combine(root, "memory-only");
            var memoryOnly = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(memoryArea, "plugins")),
                Path.Combine(memoryArea, "plugin-data"));
            windows.Add(memoryOnly);
            var memoryMetadata = CerebralJson.Element(memoryOnly.ComposerDraftControlSnapshot());
            Check(!memoryMetadata.GetProperty("enabled").GetBoolean() && memoryOnly.composerDraftStore is null,
                "An explicitly injected diagnostic fixture without a durable binding remains memory-only.");
            var memoryRestore = await memoryOnly.HandleDesktopControlAsync(new(Guid.NewGuid().ToString("N"), "memory-restore", "composer.drafts.restore",
                CerebralJson.Element(new { items = new[] { new { id = memoryOnly.CurrentConversationId!, text = "In memory" } } })), deadline.Token);
            Check(memoryRestore.Success && !memoryRestore.Data.GetProperty("durable").GetBoolean() &&
                memoryOnly.prompt.Text == "In memory", "A successful memory-only restore must not claim persistence.");
            var memorySet = await memoryOnly.HandleDesktopControlAsync(new(Guid.NewGuid().ToString("N"), "memory-set", "composer.set",
                CerebralJson.Element(new { text = "Changed in memory" })), deadline.Token);
            Check(memorySet.Success && !memorySet.Data.GetProperty("durable").GetBoolean() &&
                memoryOnly.prompt.Text == "Changed in memory", "A successful memory-only set must not claim persistence.");

            var first = Window("window");
            var firstId = first.CurrentConversationId!;
            first.draftProjectId = "fixture-project";
            first.prompt.Text = unicode;
            Check(first.composerDraftDirty && first.drafts[firstId] == unicode, "Real TextChanged records the current unsent text.");
            var firstClosed = false;
            first.Closed += (_, _) => firstClosed = true;
            first.Close();
            Check(firstClosed && first.composerDraftStore!.Read().Drafts[firstId] == unicode,
                "A successful close completes only after the current draft is saved.");
            var reopened = Window("window");
            Check(reopened.CurrentConversationId == firstId && reopened.prompt.Text == unicode &&
                reopened.draftProjectId == "fixture-project",
                "Closing and constructing a fresh hidden window restores its selected new-chat identity, project and text.");
            var lockedClose = Window("locked-close");
            var lockedId = lockedClose.CurrentConversationId!;
            lockedClose.prompt.Text = "Last saved text";
            Check(lockedClose.FlushComposerDrafts(), "The lock fixture starts with a saved draft.");
            var lockedPath = lockedClose.composerDraftStore!.FilePath;
            var lockedBytes = File.ReadAllBytes(lockedPath);
            lockedClose.prompt.Text = "Unsaved text during file lock";
            var lockedClosed = false;
            lockedClose.Closed += (_, _) => lockedClosed = true;
            using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                lockedClose.Close();
                Check(!lockedClosed && lockedClose.ComposerDraftCloseBlocked && lockedClose.composerDraftDirty &&
                    lockedClose.prompt.Text == "Unsaved text during file lock",
                    "An actual file-sharing failure keeps the window and unsaved text.");
            }
            Check(lockedBytes.SequenceEqual(File.ReadAllBytes(lockedPath)),
                "A failed close leaves the last saved draft bytes unchanged.");
            lockedClose.Close();
            Check(lockedClosed && lockedClose.composerDraftStore.Read().Drafts[lockedId] == "Unsaved text during file lock",
                "Closing again after storage recovers saves the retained text.");
            reopened.NewConversationInProject(null);
            var secondId = reopened.CurrentConversationId!;
            var secondSaved = reopened.composerDraftStore!.Read();
            Check(secondSaved.SelectedConversationId == secondId && secondSaved.SelectedIsNew &&
                secondSaved.SelectedProjectId is null && secondSaved.Drafts[firstId] == unicode &&
                !secondSaved.Drafts.ContainsKey(secondId) && !reopened.composerDraftDirty &&
                !reopened.composerDraftTimer!.IsEnabled,
                "Normal New after clearing nonblank text saves the new selection once before the delayed timer runs.");
            reopened.prompt.Text = "Second draft";
            Check(secondId != firstId && reopened.FlushComposerDrafts(), "Starting another chat preserves the previous draft.");
            reopened.durableConversationId = firstId; reopened.nativeChat!.BeginConversation(firstId);
            reopened.RestoreComposerDraftText(firstId);
            Check(reopened.prompt.Text == unicode && reopened.drafts[secondId] == "Second draft",
                "Switch restoration preserves both chat drafts.");
            reopened.prompt.Clear(); reopened.FlushComposerDrafts();
            var reloaded = reopened.composerDraftStore!.Read();
            Check(!reloaded.Drafts.ContainsKey(firstId) && reloaded.Drafts[secondId] == "Second draft",
                "Explicitly clearing one draft removes only that draft.");

            var control = Window("control");
            var controlId = control.CurrentConversationId!;
            var request = new DesktopControlRequest(Guid.NewGuid().ToString("N"), "restore-fixture", "composer.drafts.restore",
                CerebralJson.Element(new { items = new[] { new { id = controlId, text = unicode }, new { id = "other-retained", text = "Other" } } }));
            var restored = await control.HandleDesktopControlAsync(request, deadline.Token);
            Check(restored.Success && restored.Data.GetProperty("durable").GetBoolean() && control.composerDraftStore!.Read().Drafts.Count == 2,
                "The existing restore control must atomically persist its admitted batch.");
            control.prompt.Text = "Newer edits";
            var conflict = false;
            try { await control.HandleDesktopControlAsync(request with { RequestId = "restore-conflict" }, deadline.Token); }
            catch (InvalidOperationException) { conflict = true; }
            Check(conflict && control.prompt.Text == "Newer edits" && control.drafts["other-retained"] == "Other",
                "A retained-draft restore must not overwrite newer edits.");
            control.FlushComposerDrafts();
            var metadata = CerebralJson.Element(control.ComposerDraftControlSnapshot());
            Check(metadata.GetProperty("retained").GetInt32() == 2 && metadata.GetProperty("persisted").GetInt32() == 2 &&
                metadata.GetProperty("selectedSha256").GetString() == ComposerDraftStore.Hash("Newer edits") &&
                !metadata.GetRawText().Contains("Newer edits", StringComparison.Ordinal) && !metadata.GetRawText().Contains(unicode, StringComparison.Ordinal),
                "Owner diagnostics contain bounded counts and hashes, never draft text.");
            control.prompt.Text = new string('x', ComposerDraftStore.MaximumDraftCharacters + 1);
            Check(!control.FlushComposerDrafts() && control.composerDraftDirty &&
                control.composerDraftStore!.Read().Drafts[controlId] == "Newer edits" &&
                CerebralJson.Element(control.ComposerDraftControlSnapshot()).GetProperty("selectedSha256").ValueKind == JsonValueKind.Null,
                "Oversized unsaved text remains available while the last saved file and bounded diagnostics remain intact.");
            var controlClosed = false;
            control.Closed += (_, _) => controlClosed = true;
            control.Close();
            Check(!controlClosed && control.ComposerDraftCloseBlocked && control.composerDraftDirty &&
                control.prompt.Text.Length == ComposerDraftStore.MaximumDraftCharacters + 1 &&
                control.composerDraftStore.Read().Drafts[controlId] == "Newer edits",
                "A failed close save retains the live unsaved text and the last good file.");
            control.prompt.Text = "Recovered after failed close";
            control.Close();
            Check(controlClosed && !control.ComposerDraftCloseBlocked &&
                control.composerDraftStore.Read().Drafts[controlId] == "Recovered after failed close",
                "A corrected draft can be saved by an explicit second close.");

            var corruptArea = Path.Combine(root, "corrupt-window", "drafts"); Directory.CreateDirectory(corruptArea);
            var corruptPath = Path.Combine(corruptArea, "composer-drafts.txt"); File.WriteAllText(corruptPath, "UNSUPPORTED RETAINED DATA");
            var corrupt = Window("corrupt-window"); corrupt.prompt.Text = "Keep current unsaved text";
            Check(!corrupt.FlushComposerDrafts() && corrupt.prompt.Text == "Keep current unsaved text" &&
                corrupt.composerDraftStorageError is not null && File.ReadAllText(corruptPath) == "UNSUPPORTED RETAINED DATA",
                "A startup read failure retains the existing file and the user's newly entered text.");
            var corruptClosed = false;
            corrupt.Closed += (_, _) => corruptClosed = true;
            corrupt.Close();
            Check(!corruptClosed && corrupt.ComposerDraftCloseBlocked &&
                corrupt.prompt.Text == "Keep current unsaved text" &&
                File.ReadAllText(corruptPath) == "UNSUPPORTED RETAINED DATA",
                "A failed startup read also blocks close without replacing the unreadable retained file.");

            // Hold the actual selection's asynchronous workflow lookup. Typing during startup
            // must cancel that old selection before it can replace the current composer.
            var raceArea = Path.Combine(root, "race", "drafts");
            new ComposerDraftStore(raceArea, "isolated-drafts:" + Path.GetFullPath(raceArea)).Write(
                new Dictionary<string, string> { ["saved-chat"] = "Saved existing draft" }, "saved-chat", false, null);
            var race = Window("race"); var freshId = race.CurrentConversationId!;
            var pending = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var savedRun = Run("saved-run", "saved-chat", "Previous submitted text");
            using var raceWorkspace = new WorkflowWorkspace((input, token) =>
            {
                if (input.Operation == "jobs.workflow.get")
                { entered.TrySetResult(); return pending.Task.WaitAsync(token); }
                if (input.Operation == "jobs.workflow.events.previous") return Task.FromResult(Events());
                throw new InvalidOperationException("Unexpected fixture operation: " + input.Operation);
            }, () => Array.Empty<AgentProfile>(), (_, _) => { }, embedded: true);
            race.nativeChat = raceWorkspace; raceWorkspace.BeginConversation(freshId);
            race.durableConversations["saved-chat"] = new("saved-chat", "Saved chat", "fixture-lead", null, "saved-run", WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
            var startup = race.RestoreComposerDraftSelectionAsync();
            await entered.Task.WaitAsync(deadline.Token);
            Check(race.restoringComposerDraftSelection && !startup.IsCompleted, "The fixture exercises the actual pending startup selection.");
            race.prompt.Text = "Typed before startup finished";
            pending.SetResult(CommandResult.Ok(savedRun));
            await startup.WaitAsync(deadline.Token);
            Check(race.CurrentConversationId == freshId && race.nativeChat.Snapshot?.ConversationId == freshId &&
                race.nativeChat.Snapshot.RunId is null && race.prompt.Text == "Typed before startup finished" &&
                race.drafts["saved-chat"] == "Saved existing draft",
                "A delayed startup response cannot replace newly typed text or discard the old draft.");
            Check(race.FlushComposerDrafts() && race.composerDraftStore!.Read().Drafts[freshId] == "Typed before startup finished",
                "The newer startup text is durably retained under its own conversation.");

            // Exercise the real confirmed-submission hook with an accepted deterministic receipt.
            var send = Window("send"); var sendId = send.CurrentConversationId!;
            var accepted = Run("accepted-request", sendId, "Submitted text");
            using var sendWorkspace = new WorkflowWorkspace((input, _) => Task.FromResult(input.Operation switch
            {
                "jobs.workflow.submit" => CommandResult.Ok(accepted),
                "jobs.workflow.events.previous" => Events(),
                _ => throw new InvalidOperationException("Unexpected fixture operation: " + input.Operation)
            }), () => Array.Empty<AgentProfile>(), (_, _) => { }, embedded: true);
            send.nativeChat = sendWorkspace; sendWorkspace.BeginConversation(sendId);
            send.prompt.Text = "Submitted text";
            send.pendingSubmission = new SubmitJobRequest { RequestId = accepted.Id, SessionId = sendId, ProfileId = "fixture-lead", Prompt = "Submitted text" };
            await sendWorkspace.SubmitConversationAsync(new(accepted.Id, "fixture-lead", accepted.Submission.DefinitionId,
                accepted.Submission.DefinitionVersion, "Submitted text", WorkflowContract.EmptyObject), deadline.Token);
            send.prompt.Text = "New edits while completing";
            send.UpdateNativeChatState();
            Check(send.pendingSubmission is null && send.prompt.Text == "New edits while completing" &&
                send.composerDraftStore!.Read().Drafts[sendId] == "New edits while completing",
                "A confirmed send preserves any text edited after that submission.");
            send.pendingSubmission = new SubmitJobRequest { RequestId = accepted.Id, SessionId = sendId, ProfileId = "fixture-lead", Prompt = "New edits while completing" };
            send.UpdateNativeChatState();
            Check(send.prompt.Text.Length == 0 && !send.composerDraftStore!.Read().Drafts.ContainsKey(sendId),
                "A confirmed send clears only the exact submitted draft.");
            Check(windows.All(window => !window.IsVisible), "No fixture window was shown or brought forward.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, composerDraftChecks = checks,
                evidence = "real bounded file I/O and deterministic hidden WPF controls/async receipt fixtures",
                serviceCommandsExecuted = false, networkUsed = false, modelUsed = false, windowShown = false }));
        }
        finally
        {
            foreach (var window in windows) window.Close();
            ScopedFilePolicy.RejectLinks(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ComposerDraftFixtureHttpHandler(Func<CommandRequest, CommandResult> dispatch) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var input = await request.Content!.ReadFromJsonAsync<CommandRequest>(CerebralJson.Options, token)
                ?? throw new InvalidOperationException("The composer fixture received no command.");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(dispatch(input), options: CerebralJson.Options) };
        }
    }
}
