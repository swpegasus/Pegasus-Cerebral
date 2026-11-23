using System.IO;
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
    /// <summary>Deterministic preference integration on the WPF dispatcher; no service, Registry, filesystem writes, or shown window.</summary>
    public async Task<int> CheckNativeConversationPreferencesAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run native preference checks on the active WPF dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            { checks++; return; }
            throw new InvalidOperationException(label);
        }

        const string id = "preferences-chat"; const string profileId = "preferences-lead";
        var originalWorkspace = nativeChat; var originalSession = session; var originalId = durableConversationId;
        var originalTitle = durableConversationTitle; var originalGeneration = pluginGeneration;
        var originalProject = draftProjectId; var originalSelection = conversationSelectionVersion;
        var originalSource = knowledgeConnectionVersion; var originalShare = selectedResponseSharePercent;
        var originalPending = nativePreferencePending; var originalEditGeneration = nativePreferenceEditGeneration;
        var originalLoaded = loadedConversationProperties; var originalLoadedSource = loadedPropertiesSource;
        var originalUnsupported = unsupportedPropertiesSource; var originalUnsupportedConnection = unsupportedPropertiesConnection;
        var originalUnconfirmed = unconfirmedNativePreference; var originalCommand = nativePreferenceCommandOverride;
        var originalBinding = nativePreferenceBindingOverride; var originalCache = nativePreferenceCacheOverride;
        var originalProfiles = profiles; var originalLead = leadProfile; var originalNavigation = navigation;
        var originalDirectory = projectsDirectory; var originalSummary = durableConversations.GetValueOrDefault(id);
        var current = true;
        var calls = new List<(string Operation, JsonElement Arguments)>();
        var cache = new List<(string Id, int? Share)>();
        var lazyParent = Path.Combine(Path.GetTempPath(), "cerebral-preference-root-" + Guid.NewGuid().ToString("N"));
        var profile = new AgentProfile { Id = profileId, Name = "Preference Cerebral", Role = "lead", WorkerId = "local",
            AllowedRoots = [lazyParent] };
        var saved = new ConversationPropertiesSnapshot(id, "Saved preferences chat", profileId, profile.Name, profile.Role,
            null, "bound", Path.Combine(lazyParent, "original"), [lazyParent], ["filesystem.read"], ["file.read"], false,
            "saved", "conversation", 7, DateTimeOffset.UnixEpoch, 25);
        var defaults = saved with { PropertyState = "default", SettingsSource = "profile", Revision = 0,
            UpdatedAt = null, ResponseSharePercent = null };
        var summary = new ConversationSummary(id, saved.Title, profileId, null, "preferences-run", WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
        var run = new WorkflowRun { Id = summary.LatestRunId, Revision = 1, Status = WorkflowStatus.Completed,
            Submission = new(summary.LatestRunId, id, profileId, ConversationControlClient.ChatDefinitionId,
                ConversationControlClient.ChatDefinitionVersion, "Original preferences request", null,
                WorkflowContract.EmptyObject, WorkflowContract.EmptyObject) };
        var originalMessage = new CerebralEvent("preferences-message", id, run.Id, 1, CerebralEventTypes.Message,
            new(EventActorKind.User), [new TextContentPart(run.Submission.UserText)], new(ModelInclusion.Required),
            new(EventSourceKind.User), DateTimeOffset.UnixEpoch);
        using var isolated = new WorkflowWorkspace((request, _) => Task.FromResult(request.Operation == "jobs.workflow.events.previous"
            ? CommandResult.Ok(new { events = new[] { originalMessage }, snapshotSequence = 1L, nextBeforeSequence = (long?)null })
            : CommandResult.Ok(run)), () => new[] { profile }, (_, _) => { }, embedded: true);
        void Route(Func<string, JsonElement, CancellationToken, Task<CommandResult>> handler)
        {
            nativePreferenceCommandOverride = (operation, arguments, token) =>
            {
                var frozen = CerebralJson.Element(arguments).Clone();
                calls.Add((operation, frozen)); return handler(operation, frozen, token);
            };
        }
        void Reset()
        {
            current = true; calls.Clear(); cache.Clear(); session = null; durableConversationId = id;
            draftProjectId = null; nativePreferencePending = false; nativePreferenceEditGeneration++;
            selectedResponseSharePercent = 25; unsupportedPropertiesSource = -1; unsupportedPropertiesConnection = null;
            unconfirmedNativePreference = null; loadedConversationProperties = null; loadedPropertiesSource = -1;
            Route((operation, _, _) => Task.FromResult(operation == "conversations.properties.get"
                ? CommandResult.Ok(saved) : throw new InvalidOperationException("Unexpected fixture write.")));
        }
        try
        {
            nativeChat = isolated; session = null; durableConversationId = id; durableConversationTitle = saved.Title;
            draftProjectId = null; pluginGeneration = "preferences-fixture-generation";
            profiles = [profile]; leadProfile = profile; navigation = new(); projectsDirectory = lazyParent;
            knowledgeConnectionVersion++; nativePreferenceBindingOverride = () => current;
            nativePreferenceCacheOverride = (target, share) => cache.Add((target, share));
            durableConversations[id] = summary; isolated.BeginConversation(id);
            Reset();
            var draft = await PersistNativeResponsePreferenceAsync(PluginBinding(), 20, deadline.Token);
            Check(draft is null && calls.Count == 0 && cache.Count == 0, "A draft never invents or saves a Worker properties record.");
            session = new TaskSession { Id = "legacy-preferences", ProfileId = profileId };
            Check(await PersistNativeResponsePreferenceAsync(PluginBinding(), 20, deadline.Token) is null && calls.Count == 0,
                "Legacy preferences do not use native owner commands.");
            session = null; await isolated.OpenRunAsync(run.Id);
            Check(isolated.Snapshot?.RunId == run.Id && isolated.Snapshot.OriginalMessage?.Id == originalMessage.Id,
                "Saved-chat fixtures use the real workflow adapter and canonical original owner event.");

            Reset(); selectedResponseSharePercent = 39;
            Route((_, _, _) => Task.FromResult(CommandResult.Ok(defaults)));
            await LoadNativePropertiesAsync(summary, conversationSelectionVersion, deadline.Token);
            Check(selectedResponseSharePercent is null && !nativePreferencePending && loadedConversationProperties?.Revision == 0 &&
                cache.SequenceEqual([(id, (int?)null)]), "A supported revision-zero default supersedes stale local percentages.");

            var confirmed = loadedConversationProperties;
            Route((_, _, _) => Task.FromResult(CommandResult.Ok(saved with { ProfileId = "foreign-profile" })));
            await Reject(() => LoadNativePropertiesAsync(summary, conversationSelectionVersion, deadline.Token),
                "A foreign profile must reject the read.");
            Check(ReferenceEquals(confirmed, loadedConversationProperties) && cache.Count == 1,
                "Rejected owner identity cannot poison the cached working root or local preference.");

            Reset(); selectedResponseSharePercent = 29; nativePreferencePending = true;
            Route((_, _, _) => Task.FromResult(CommandResult.Ok(defaults)));
            await LoadNativePropertiesAsync(summary, conversationSelectionVersion, deadline.Token);
            Check(selectedResponseSharePercent == 29 && nativePreferencePending && cache.Count == 0,
                "Refreshing the same chat preserves an existing explicit pending edit.");

            Reset();
            var heldLoad = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Route((_, _, token) => heldLoad.Task.WaitAsync(token));
            var loading = LoadNativePropertiesAsync(summary, conversationSelectionVersion, deadline.Token);
            selectedResponseSharePercent = 29; nativePreferencePending = true; nativePreferenceEditGeneration++;
            heldLoad.SetResult(CommandResult.Ok(defaults)); await loading;
            Check(selectedResponseSharePercent == 29 && nativePreferencePending && cache.Count == 0,
                "A new explicit edit during loading keeps its local value and pending state.");

            Reset();
            var selectionReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Route((_, _, token) => selectionReply.Task.WaitAsync(token));
            loading = LoadNativePropertiesAsync(summary, conversationSelectionVersion, deadline.Token);
            conversationSelectionVersion++; selectionReply.SetResult(CommandResult.Ok(defaults)); await loading;
            Check(selectedResponseSharePercent == 25 && loadedConversationProperties is null && cache.Count == 0,
                "A stale selected-chat read cannot project preferences or roots.");

            Reset();
            var connectionReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Route((_, _, token) => connectionReply.Task.WaitAsync(token));
            loading = LoadNativePropertiesAsync(summary, conversationSelectionVersion, deadline.Token);
            current = false; connectionReply.SetResult(CommandResult.Ok(defaults)); await loading;
            Check(selectedResponseSharePercent == 25 && loadedConversationProperties is null && cache.Count == 0,
                "A read held through reconnect cannot publish local state through the replacement binding.");

            Reset();
            Route((_, _, _) => Task.FromResult(CommandResult.Fail("Unknown command: conversations.properties.get")));
            await LoadNativePropertiesAsync(summary, conversationSelectionVersion, deadline.Token);
            await LoadNativePropertiesAsync(summary, conversationSelectionVersion, deadline.Token);
            Check(calls.Count == 1 && selectedResponseSharePercent == 25 && cache.Count == 0,
                "Only the exact old-Worker command result enables scoped compatibility fallback.");
            knowledgeConnectionVersion++;
            Route((_, _, _) => Task.FromResult(CommandResult.Ok(defaults)));
            await LoadNativePropertiesAsync(summary, conversationSelectionVersion, deadline.Token);
            Check(calls.Count == 2 && selectedResponseSharePercent is null, "A changed connection source retries optional properties discovery.");
            Reset();
            Route((_, _, _) => Task.FromResult(CommandResult.Fail("Unknown command: conversations.properties.get extra")));
            await Reject(() => LoadNativePropertiesAsync(summary, conversationSelectionVersion, deadline.Token),
                "A similar-looking error is not an old-Worker compatibility receipt.");
            Check(unsupportedPropertiesSource == -1, "Other failures cannot disable supported owner settings.");

            Reset();
            var noop = await PersistNativeResponsePreferenceAsync(PluginBinding(), 25, deadline.Token);
            Check(noop?.Revision == 7 && calls.Select(x => x.Operation).SequenceEqual(["conversations.properties.get"]) &&
                loadedConversationProperties?.Revision == 7, "A no-op fresh read confirms current state without a write or revision bump.");
            await Reject(() => PersistNativeResponsePreferenceAsync(PluginBinding(), 51, deadline.Token),
                "Out-of-contract percentages reject before dispatch.");
            Check(calls.Count == 1, "Invalid percentages never reach an owner command.");

            Reset();
            Route((operation, arguments, _) =>
            {
                if (operation == "conversations.properties.get") return Task.FromResult(CommandResult.Ok(saved));
                Check(operation == "conversations.properties.save" && arguments.GetProperty("id").GetString() == id &&
                    arguments.GetProperty("expectedRevision").GetInt64() == 7 && arguments.GetProperty("responseSharePercent").GetInt32() == 20 &&
                    arguments.EnumerateObject().Count() == 3, "The save uses the exact owner chat, fresh revision and bounded preference fields.");
                return Task.FromResult(CommandResult.Ok(saved with { Revision = 8, ResponseSharePercent = 20,
                    UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) }));
            });
            var accepted = await PersistNativeResponsePreferenceAsync(PluginBinding(), 20, deadline.Token);
            Check(accepted?.Revision == 8 && loadedConversationProperties?.ResponseSharePercent == 20 && calls.Count == 2,
                "Only a matching immutable-identity receipt updates the saved preference cache.");

            Reset(); selectedResponseSharePercent = 28; nativePreferencePending = true;
            await nativePreferenceGate.WaitAsync(deadline.Token);
            Task<ConversationPropertiesSnapshot?> queued;
            try
            {
                queued = PersistNativeResponsePreferenceAsync(PluginBinding(), 28, deadline.Token, onlyIfStillSelected: true);
                selectedResponseSharePercent = 35; nativePreferenceEditGeneration++;
            }
            finally { nativePreferenceGate.Release(); }
            Check(await queued is null && calls.Count == 0 && nativePreferencePending,
                "An obsolete queued slider save skips before transport and preserves the newer pending edit.");

            Reset(); selectedResponseSharePercent = 28; nativePreferencePending = true;
            var readReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Route((_, _, token) => readReply.Task.WaitAsync(token));
            var saving = PersistNativeResponsePreferenceAsync(PluginBinding(), 28, deadline.Token, onlyIfStillSelected: true);
            selectedResponseSharePercent = 36; nativePreferenceEditGeneration++;
            readReply.SetResult(CommandResult.Ok(saved));
            Check(await saving is null && calls.Count == 1 && nativePreferencePending && loadedConversationProperties is null,
                "An edit during the fresh read stops before CAS and cannot clear or cache under the new generation.");

            Reset(); selectedResponseSharePercent = 28; nativePreferencePending = true;
            var saveReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Route((operation, _, token) => operation == "conversations.properties.get"
                ? Task.FromResult(CommandResult.Ok(saved)) : saveReply.Task.WaitAsync(token));
            saving = PersistNativeResponsePreferenceAsync(PluginBinding(), 28, deadline.Token, onlyIfStillSelected: true);
            Check(calls.Count == 2, "The held mutation reaches exactly one dispatch.");
            selectedResponseSharePercent = 37; nativePreferenceEditGeneration++;
            saveReply.SetResult(CommandResult.Ok(saved with { Revision = 8, ResponseSharePercent = 28 }));
            Check(await saving is null && selectedResponseSharePercent == 37 && nativePreferencePending &&
                loadedConversationProperties?.Revision == 7 && unconfirmedNativePreference is null,
                "A confirmed old edit never projects or clears a newer edit, and does not invent uncertainty.");

            Reset(); selectedResponseSharePercent = 31; nativePreferencePending = true;
            Route((operation, _, _) => operation == "conversations.properties.get"
                ? Task.FromResult(CommandResult.Ok(saved)) : Task.FromException<CommandResult>(new IOException("Fixture lost save reply")));
            await Reject(() => PersistSelectedNativeResponsePreferenceAsync(), "A lost save reply remains unconfirmed.");
            Check(unconfirmedNativePreference == (id, knowledgeConnectionVersion) && nativePreferencePending && calls.Count == 2,
                "The attempted mutation retains its chat/source marker and pending intent.");
            Route((operation, _, _) => Task.FromResult(operation == "conversations.properties.get"
                ? CommandResult.Ok(saved with { Revision = 9, ResponseSharePercent = 12 })
                : throw new InvalidOperationException("An uncertain intent must not replay.")));
            await PersistSelectedNativeResponsePreferenceAsync();
            Check(calls.Count == 3 && selectedResponseSharePercent == 12 && !nativePreferencePending &&
                unconfirmedNativePreference is null && cache.SequenceEqual([(id, (int?)12)]),
                "Automatic recovery reads and adopts current owner settings without replaying the uncertain old edit.");

            Reset(); selectedResponseSharePercent = 31; nativePreferencePending = true;
            Route((operation, _, _) => Task.FromResult(operation == "conversations.properties.get"
                ? CommandResult.Ok(saved) : CommandResult.Ok(saved with { Revision = 8, Root = Path.Combine(lazyParent, "foreign-root"), ResponseSharePercent = 31 })));
            await Reject(() => PersistSelectedNativeResponsePreferenceAsync(), "A changed root receipt must remain unconfirmed.");
            var attempted = calls.Count;
            Route((_, _, _) => Task.FromResult(CommandResult.Ok(saved with { Id = "foreign-chat" })));
            await Reject(() => PersistSelectedNativeResponsePreferenceAsync(), "A foreign recovery read cannot unlock uncertainty.");
            Check(calls.Count == attempted + 1 && unconfirmedNativePreference == (id, knowledgeConnectionVersion) && nativePreferencePending,
                "Malformed recovery preserves uncertainty and makes no second write.");
            Route((_, _, _) => Task.FromResult(CommandResult.Fail("Unknown command: conversations.properties.get")));
            await PersistSelectedNativeResponsePreferenceAsync();
            Check(unconfirmedNativePreference == (id, knowledgeConnectionVersion) && nativePreferencePending && calls.Count == attempted + 2,
                "An unsupported optional read cannot confirm or replay a previously attempted save.");

            Reset();
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Reject(() => PersistNativeResponsePreferenceAsync(PluginBinding(), 20, cancelled.Token),
                    "Pre-dispatch cancellation must reject without transport.");
            }
            Check(calls.Count == 0 && unconfirmedNativePreference is null, "Pre-dispatch cancellation does not invent an uncertain mutation.");
            Reset(); selectedResponseSharePercent = 31; nativePreferencePending = true;
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                Route(async (operation, _, token) =>
                {
                    if (operation == "conversations.properties.get") return CommandResult.Ok(saved);
                    await Task.Delay(Timeout.Infinite, token); return CommandResult.Ok(saved);
                });
                saving = PersistNativeResponsePreferenceAsync(PluginBinding(), 31, cancelled.Token, onlyIfStillSelected: true);
                Check(calls.Count == 2 && !saving.IsCompleted, "Cancellation fixture holds one dispatched save.");
                cancelled.Cancel();
                await Reject(async () => await saving, "Cancellation after dispatch remains unconfirmed.");
            }
            Route((_, _, _) => Task.FromResult(CommandResult.Ok(saved)));
            await PersistSelectedNativeResponsePreferenceAsync();
            Check(calls.Count == 3 && selectedResponseSharePercent == 25 && !nativePreferencePending && unconfirmedNativePreference is null,
                "A cancelled save releases serialization and permits read-only recovery without replay.");

            Reset(); selectedResponseSharePercent = 34;
            Route((_, _, _) => Task.FromResult(CommandResult.Ok(defaults)));
            var input = new DesktopSubmission(PluginBinding(), "preferences-next-turn", "Continue");
            var root = await NativeSubmissionRootAsync(profile, isolated.Snapshot!, input, deadline.Token);
            var request = CreateNativeSubmission(input, profileId, root, null, selectedResponseSharePercent, null);
            Check(root == defaults.Root && selectedResponseSharePercent is null && calls.Count == 1 &&
                !request.Input.TryGetProperty("responseSharePercent", out _),
                "An owner reset while the chat is open is authoritative before the next turn and never written back from old local state.");
            Reset(); selectedResponseSharePercent = 34; nativePreferencePending = true;
            Route((_, _, _) => Task.FromResult(CommandResult.Ok(defaults)));
            root = await NativeSubmissionRootAsync(profile, isolated.Snapshot!, input, deadline.Token);
            Check(root == defaults.Root && selectedResponseSharePercent == 34 && nativePreferencePending && calls.Count == 1,
                "A fresh owner read preserves a separate explicit pending edit until the save path resolves it.");

            Reset();
            Route((_, _, _) => Task.FromResult(CommandResult.Ok(saved with { RootState = "unbound", Root = null })));
            root = await NativeSubmissionRootAsync(profile, isolated.Snapshot!, input, deadline.Token);
            request = CreateNativeSubmission(input, profileId, root, null, selectedResponseSharePercent, null);
            Check(root is null && !request.Input.TryGetProperty("workingDirectory", out _) && calls.Count == 1,
                "A cleared owner working folder remains unbound on the next send and never falls back to a configured parent.");

            Reset();
            var movedRoot = Path.Combine(lazyParent, "user-selected-moved-folder");
            Route((_, _, _) => Task.FromResult(CommandResult.Ok(saved with { Root = movedRoot, Revision = 8 })));
            root = await NativeSubmissionRootAsync(profile, isolated.Snapshot!, input, deadline.Token);
            Check(root == movedRoot && LoadedNativeWorkingRoot(profile) == movedRoot,
                "A fresh owner path override is used for the next submission and the current granted file-browser root.");

            Reset(); profile.AllowedRoots = [Path.Combine(lazyParent, "elsewhere")];
            root = await NativeSubmissionRootAsync(profile, isolated.Snapshot!, input, deadline.Token);
            Check(root == saved.Root && LoadedNativeWorkingRoot(profile) is null,
                "A bound root keeps its original identity after grant revocation while the file browser refuses an ungranted cached root.");
            profile.AllowedRoots = [lazyParent];
            Reset();
            var heldRoot = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Route((_, _, token) => heldRoot.Task.WaitAsync(token));
            var resolving = NativeSubmissionRootAsync(profile, isolated.Snapshot!, input, deadline.Token);
            current = false; heldRoot.SetResult(CommandResult.Ok(saved));
            await Reject(async () => await resolving, "Reconnect while resolving a root must reject the pending submission.");
            Check(loadedConversationProperties is null && cache.Count == 0, "A stale root read never publishes its bound path or preference.");
            Reset();
            await Reject(() => NativeSubmissionRootAsync(profile, isolated.Snapshot!, input with { Binding = input.Binding with { SessionId = "foreign" } }, deadline.Token),
                "A foreign submission binding cannot resolve a root.");
            Check(calls.Count == 0, "Foreign root resolution rejects before any owner read.");

            Reset(); var freshId = Guid.NewGuid().ToString("N"); durableConversationId = freshId; isolated.BeginConversation(freshId);
            Check(!Directory.Exists(lazyParent), "The task-owned lazy root must be absent before its pure path check.");
            var draftInput = new DesktopSubmission(PluginBinding(), "preferences-draft", "Start");
            root = await NativeSubmissionRootAsync(profile, isolated.Snapshot!, draftInput, deadline.Token);
            request = CreateNativeSubmission(draftInput, profileId, root, null, selectedResponseSharePercent, null);
            Check(root is null && !request.Input.TryGetProperty("workingDirectory", out _) && !Directory.Exists(lazyParent) && calls.Count == 0,
                "A new general chat omits its working folder without deriving a name from its internal ID or creating files.");
            profile.AllowedRoots = [];
            root = await NativeSubmissionRootAsync(profile, isolated.Snapshot!, draftInput, deadline.Token);
            Check(root is null && !Directory.Exists(lazyParent) && profile.AllowedRoots.Count == 0 && calls.Count == 0,
                "Starting a chat requires no filesystem grant and never widens owner permissions or creates a directory.");
            profile.AllowedRoots = [lazyParent];
            draftProjectId = "preferences-project";
            navigation.Nodes.Add(new() { Id = draftProjectId, Kind = "project", Title = "Exact project", Path = Path.Combine(lazyParent, "declared") });
            root = await NativeSubmissionRootAsync(profile, isolated.Snapshot!, draftInput, deadline.Token);
            Check(root == Path.Combine(lazyParent, "declared") && !Directory.Exists(lazyParent),
                "An explicitly selected project keeps its declared path without a generated per-chat child directory.");

            Console.WriteLine($"NATIVE CONVERSATION PREFERENCES: PASS · {checks} deterministic WPF/command checks");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { Console.Error.WriteLine("NATIVE CONVERSATION PREFERENCES: FAIL · " + error); return 1; }
        finally
        {
            deadline.Cancel();
            nativeChat = originalWorkspace; session = originalSession; durableConversationId = originalId;
            durableConversationTitle = originalTitle; pluginGeneration = originalGeneration; draftProjectId = originalProject;
            conversationSelectionVersion = originalSelection; knowledgeConnectionVersion = originalSource;
            selectedResponseSharePercent = originalShare; nativePreferencePending = originalPending;
            nativePreferenceEditGeneration = originalEditGeneration; loadedConversationProperties = originalLoaded;
            loadedPropertiesSource = originalLoadedSource; unsupportedPropertiesSource = originalUnsupported;
            unsupportedPropertiesConnection = originalUnsupportedConnection; unconfirmedNativePreference = originalUnconfirmed;
            nativePreferenceCommandOverride = originalCommand; nativePreferenceBindingOverride = originalBinding;
            nativePreferenceCacheOverride = originalCache; profiles = originalProfiles; leadProfile = originalLead;
            navigation = originalNavigation; projectsDirectory = originalDirectory;
            if (originalSummary is null) durableConversations.Remove(id); else durableConversations[id] = originalSummary;
            RefreshConversationTokenBalance();
        }
    }
}
