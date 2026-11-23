using System.Text.Json;
using System.IO;
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
    /// <summary>Pure submission/binding checks on the native window; no service or persistent setting writes.</summary>
    public int CheckDurableChatUi()
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var originalShare = selectedResponseSharePercent; var originalSession = session;
        var originalProfiles = profiles; var originalLead = leadProfile;
        var originalSummary = durableConversations.GetValueOrDefault(durableConversationId!);
        try
        {
            Check(IsNativeChat && nativeChat?.Snapshot?.ConversationId == CurrentConversationId,
                "A new Chat window must own a stable durable conversation identity before its first submission.");
            var binding = PluginBinding();
            Check(binding.SessionId == CurrentConversationId && !string.IsNullOrWhiteSpace(binding.Generation),
                "Plugins must receive the durable conversation identity and a generation boundary.");
            var input = new DesktopSubmission(binding, "fixture-request", "Continue the previous request.");
            selectedResponseSharePercent = 26;
            var request = CreateNativeSubmission(input, "lead", @"C:\Projects\short-folder", "project-id", selectedResponseSharePercent, "PRIVATE PLUGIN ASSIGNMENT");
            Check(request.RequestId == input.RequestId && request.DefinitionId == ConversationControlClient.ChatDefinitionId && request.DefinitionVersion == ConversationControlClient.ChatDefinitionVersion && request.ProfileId == "lead",
                "The explicit legacy factory fallback must preserve its version and request ID; fresh Main selects its confirmed edition before calling it.");
            Check(request.UserText == input.Text && request.AssignmentText == "PRIVATE PLUGIN ASSIGNMENT",
                "The visible user message and private plugin assignment must remain separate and exact.");
            Check(request.StepLimit == WorkflowContract.MaximumSteps,
                "Native Chat must admit the full bounded workflow transition budget; the selected profile still limits agent actions.");
            Check(request.Input.EnumerateObject().Count() == 3 && request.Input.GetProperty("workingDirectory").GetString() == @"C:\Projects\short-folder" &&
                request.Input.GetProperty("projectId").GetString() == "project-id" && request.Input.GetProperty("responseSharePercent").GetInt32() == 26,
                "Project path, project identity and response balance must be present in the workflow input.");
            var defaultRequest = CreateNativeSubmission(input, "lead", null, null, null, null);
            Check(!defaultRequest.Input.EnumerateObject().Any() && defaultRequest.AssignmentText is null &&
                  defaultRequest.StepLimit == WorkflowContract.MaximumSteps,
                "Unset optional workflow settings must be omitted, not emitted as invalid null values.");
            var rejected = false;
            try { CreateNativeSubmission(input, "lead", null, null, 51, null); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "The native route must not bypass the configured response-share bounds.");
            session = new TaskSession { Id = "previous-session", ProfileId = "lead", Title = "Previous chat" };
            Check(!IsNativeChat && CurrentConversationId == "previous-session" && PluginBinding().SessionId == "previous-session",
                "The explicit previous-chat view must retain its own identity and cannot masquerade as the pending native conversation.");
            session = originalSession;
            var rejectedLegacy = SubmitNativeDesktopInput(input with { Binding = binding with { SessionId = "foreign" } }, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(rejectedLegacy.State == DesktopOperationState.Rejected,
                "A stale or foreign plugin conversation binding must be rejected before any service command.");
            profiles = [new AgentProfile { Id = "saved-profile", Role = "helper" }, new AgentProfile { Id = "new-lead", Role = "lead" }];
            leadProfile = profiles[1];
            durableConversations[durableConversationId!] = new(durableConversationId!, "Saved chat", "saved-profile", null, "old-run", WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
            Check(ConversationProfile().Id == "saved-profile", "An existing chat must use its bound profile even after the current lead changes.");
            profiles.RemoveAt(0);
            var unavailable = false;
            try { ConversationProfile(); }
            catch (InvalidOperationException error) { unavailable = error.Message.Contains("saved-profile", StringComparison.Ordinal); }
            Check(unavailable, "A missing bound profile must fail actionably rather than silently changing the chat owner.");
            durableConversations.Remove(durableConversationId!);
            Check(ConversationProfile().Id == "new-lead", "A fresh chat must use the current lead profile.");
            var run = new WorkflowConversationRun("receipt", "chat", "saved-profile", "Visible", ConversationControlClient.ChatDefinitionId, ConversationControlClient.ChatDefinitionVersion, 1,
                WorkflowStatus.Running, null, null, null, null, false, null, null, 0);
            CheckMainChatWorkflowContracts(input, run, Check);
            var receipt = new WorkflowConversationSnapshot("chat", "receipt", run, Array.Empty<CerebralEvent>(), 0, false, false, false, "receipt");
            Check(MainCompletionProgress(receipt, true) == "Submitting request · waiting for receipt" &&
                MainCompletionProgress(receipt, false) == "Engine running · completion pending",
                "Submission acceptance and ongoing engine work have distinct completion labels.");
            Check(MainCompletionProgress(receipt with { Run = run with { Status = WorkflowStatus.Completed } }, false) ==
                "Completed · ready for your next message" &&
                MainCompletionProgress(receipt with { Run = run with { Status = WorkflowStatus.Failed } }, false) == "Failed" &&
                MainCompletionProgress(receipt with { MutationUncertain = true }, false).StartsWith("Outcome unconfirmed", StringComparison.Ordinal),
                "Completed, failed and unconfirmed work cannot collapse into a submit-reply status.");
            Check(ConfirmedNativeSubmission(receipt, "receipt") && !ConfirmedNativeSubmission(receipt, "foreign") &&
                !ConfirmedNativeSubmission(receipt with { MutationUncertain = true }, "receipt"),
                "A projection failure after acceptance must retain a confirmed matching receipt, while foreign or uncertain mutations remain unconfirmed.");
            Check(!ConfirmedNativeStop(receipt, "receipt") && ConfirmedNativeStop(receipt with { Run = run with { CancelRequested = true } }, "receipt") &&
                ConfirmedNativeStop(receipt with { Run = run with { Status = WorkflowStatus.Cancelled } }, "receipt"),
                "Stop can be reported accepted only after the matching receipt confirms cancellation or its pending cancellation flag.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, durableChatChecks = checks,
                evidence = "deterministic native window binding and submission contract checks", serviceCommandsExecuted = false, networkUsed = false, windowShown = false }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, durableChatChecks = checks, error = error.ToString() })); return 1; }
        finally
        {
            session = originalSession; selectedResponseSharePercent = originalShare; profiles = originalProfiles; leadProfile = originalLead;
            if (originalSummary is not null) durableConversations[originalSummary.Id] = originalSummary;
            else if (durableConversationId is not null) durableConversations.Remove(durableConversationId);
            RefreshConversationTokenBalance();
        }
    }

    /// <summary>Active-dispatcher regression for direct navigation while a plugin-style request awaits acceptance.</summary>
    public async Task<int> CheckDurableChatNavigationAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run this check on the active WPF dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var originalWorkspace = nativeChat; var originalSession = session; var originalId = durableConversationId;
        var originalGeneration = pluginGeneration; var originalPrompt = prompt.Text;
        var calls = 0; var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        var pending = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WorkflowConversationSnapshot>? dispatch = null;
        using var isolated = new WorkflowWorkspace((_, token) => { calls++; return pending.Task.WaitAsync(token); },
            () => Array.Empty<AgentProfile>(), (_, _) => { }, embedded: true);
        try
        {
            nativeChat = isolated; session = null; durableConversationId = "pending-native-chat";
            isolated.BeginConversation(durableConversationId); prompt.Text = "Keep this draft";
            var binding = PluginBinding();
            dispatch = isolated.SubmitConversationAsync(new("pending-plugin-request", "lead", ConversationControlClient.ChatDefinitionId, ConversationControlClient.ChatDefinitionVersion,
                "VISIBLE", WorkflowContract.EmptyObject, "PRIVATE"), deadline.Token);
            Check(NativeMutationPending && !NativeMutationUncertain && isolated.Snapshot?.Run is null,
                "A plugin-origin dispatch must block navigation before the first receipt exists.");
            NewConversation();
            Check(PluginBinding() == binding && prompt.Text == "Keep this draft" && Status.Text.Contains("acceptance", StringComparison.Ordinal),
                "The direct New chat button must report pending acceptance safely and preserve its binding and draft.");
            var selectionDenied = false;
            try { await SelectSession("another-chat", deadline.Token); }
            catch (InvalidOperationException) { selectionDenied = true; }
            Check(selectionDenied && PluginBinding() == binding && calls == 1,
                "Selection must reject before even reading another chat while a plugin submission is pending.");
            var pluginNew = await desktopPluginHost!.NewConversationAsync(binding, cancellationToken: deadline.Token);
            var pluginSelect = await desktopPluginHost.SelectConversationAsync(binding, "another-chat", deadline.Token);
            Check(pluginNew.State == DesktopOperationState.Rejected && pluginSelect.State == DesktopOperationState.Rejected && PluginBinding() == binding,
                "Plugin New and Select callbacks must share the pending-dispatch guard independently of the window's operation gate.");
            pending.SetException(new IOException("Fixture disconnect after dispatch."));
            try { await dispatch; throw new InvalidDataException("Expected uncertain dispatch."); }
            catch (IOException) { }
            Check(!NativeMutationPending && NativeMutationUncertain && isolated.Snapshot?.RunId == "pending-plugin-request",
                "Dispatch failure must retain the same request as uncertain after the pending guard clears.");
            NewConversation();
            Check(PluginBinding() == binding && Status.Text.Contains("unconfirmed", StringComparison.Ordinal) && calls == 1,
                "Direct New chat after an uncertain result must report safely without an unhandled event exception or a replacement request.");
            checks += await CheckDetailedMainChatRetryAsync(deadline.Token);
            var historyWindow = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(Path.GetTempPath(),
                "Cerebral.NavigationHistory-" + Guid.NewGuid().ToString("N"))), null);
            try { checks += await historyWindow.CheckConversationHistoryFailureAsync(deadline.Token); }
            finally { historyWindow.Close(); }
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, nativeNavigationChecks = checks,
                evidence = "deterministic active WPF dispatcher and pending command delegate", serviceCommandsExecuted = false, networkUsed = false, windowShown = false }));
            return checks;
        }
        finally
        {
            deadline.Cancel();
            if (dispatch is not null) { try { await dispatch; } catch (Exception) { } }
            nativeChat = originalWorkspace; session = originalSession; durableConversationId = originalId;
            pluginGeneration = originalGeneration; prompt.Text = originalPrompt;
        }
    }

    private async Task<int> CheckConversationHistoryFailureAsync(CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var workspace = new WorkflowWorkspace((command, cancellation) =>
        {
            if (command.Operation != "jobs.workflow.get")
                throw new InvalidOperationException("Unexpected history fixture command: " + command.Operation);
            return command.Arguments.GetProperty("id").GetString() switch
            {
                "held-run" => Hold(),
                "missing-run" => Task.FromResult(CommandResult.Fail("Conversation was not found.")),
                "transient-run" => Task.FromResult(CommandResult.Fail("Service unavailable.")),
                _ => throw new InvalidOperationException("Unexpected history fixture run")
            };
            Task<CommandResult> Hold() { entered.TrySetResult(true); return held.Task.WaitAsync(cancellation); }
        }, () => Array.Empty<AgentProfile>(), (_, _) => { }, embedded: true);
        nativeChat = workspace;
        session = null; durableConversationId = Guid.NewGuid().ToString("N");
        workspace.BeginConversation(durableConversationId);
        var original = durableConversationId;
        showingLegacyNavigation = true;
        durableConversations["fixture-held"] = new("fixture-held", "Held saved chat", "lead", null,
            "held-run", WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
        conversationHistory.Clear();
        conversationHistory.Record("fixture-held", null, ServiceConnectionIdentity, false, false, null);
        conversationHistory.Record(original, null, ServiceConnectionIdentity, false, false, null);
        var delayed = HandleSemanticAttachControlAsync(new DesktopControlRequest(
            Guid.NewGuid().ToString("N"), "history-delayed-fixture", "conversation.history.back",
            CerebralJson.Element(new { expected = SemanticAttachSelection() })), token);
        await entered.Task.WaitAsync(token);
        Check(showingLegacyNavigation && CurrentConversationId == original,
            "An awaited history read must not change the visible navigation mode before selection commits.");
        NewConversationInProject(null);
        var newer = CurrentConversationId;
        prompt.Text = "Newer unsent selection";
        ConnectionChanged();
        var switched = CurrentConversationId;
        prompt.Text = "Post-switch draft";
        Check(newer != original && switched != newer && !showingLegacyNavigation,
            "A new selection and connection transition supersede the held history read.");
        held.SetException(new IOException("Fixture delayed read failed."));
        var staleResponse = await delayed;
        Check(staleResponse.State == "uncertain" &&
            staleResponse.Data.GetProperty("code").GetString() == "history_navigation_failed" &&
            !showingLegacyNavigation && CurrentConversationId == switched && prompt.Text == "Post-switch draft" &&
            conversationHistory.Count == 0,
            "A stale semantic read must remain unconfirmed and cannot restore an older mode, selection, history or composer after connection change.");

        var current = CurrentConversationId!;
        durableConversations["fixture-missing"] = new("fixture-missing", "Missing chat", "lead", null,
            "missing-run", WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
        conversationHistory.Record("fixture-missing", null, ServiceConnectionIdentity, false, false, null);
        conversationHistory.Record(current, null, ServiceConnectionIdentity, false, false, null);
        var missingNavigation = await HandleSemanticAttachControlAsync(new DesktopControlRequest(
            Guid.NewGuid().ToString("N"), "history-missing-fixture", "conversation.history.back",
            CerebralJson.Element(new { expected = SemanticAttachSelection() })), token);
        Check(missingNavigation.State == "rejected" &&
            missingNavigation.Data.GetProperty("code").GetString() == "history_unavailable" &&
            missingNavigation.Data.GetProperty("skippedUnavailable").GetInt32() == 1 &&
            conversationHistory.Count == 1 && conversationHistory.Current?.ConversationId == current &&
            CurrentConversationId == current,
            "Only the exact confirmed not-found visit is skipped without changing the current chat.");

        NewConversationInProject(null);
        current = CurrentConversationId!;
        prompt.Text = "Retryable draft";
        durableConversations["fixture-transient"] = new("fixture-transient", "Temporarily unavailable", "lead", null,
            "transient-run", WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
        conversationHistory.Clear();
        conversationHistory.Record("fixture-transient", null, ServiceConnectionIdentity, false, false, null);
        conversationHistory.Record(current, null, ServiceConnectionIdentity, false, false, null);
        var transientResponse = await HandleSemanticAttachControlAsync(new DesktopControlRequest(
            Guid.NewGuid().ToString("N"), "history-transient-fixture", "conversation.history.back",
            CerebralJson.Element(new { expected = SemanticAttachSelection() })), token);
        Check(transientResponse.State == "uncertain" &&
            transientResponse.Data.GetProperty("code").GetString() == "history_navigation_failed" &&
            conversationHistory.Count == 2 && conversationHistory.Current?.ConversationId == current &&
            CurrentConversationId == current && prompt.Text == "Retryable draft",
            "A transient semantic read failure must retain its history target and current unsent text without claiming a completed move.");
        return checks;
    }

    private static void CheckMainChatWorkflowContracts(DesktopSubmission input, WorkflowConversationRun run,
        Action<bool, string> check)
    {
        bool Refused(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or JsonException) { return true; }
        }
        JsonElement Catalog(object[] workflows, string contract = WorkflowContract.Id, int version = WorkflowContract.Version) =>
            CerebralJson.Element(new { contract, version, workflows });
        object Entry(string id, string version) => new { id, version, description = "Fixture workflow" };
        JsonElement Description(params WorkflowDefinitionDescriptor[] definitions) =>
            CerebralJson.Element(new { contract = WorkflowContract.Id, version = WorkflowContract.Version, definitions });
        var detailed = CreateNativeSubmission(input, "captured-owner", @"C:\Projects\frozen-root", "frozen-project", 31,
            "PRIVATE FROZEN ASSIGNMENT", definitionVersion: DetailedToolFeedbackDefinitionVersion);
        check(detailed.DefinitionVersion == DetailedToolFeedbackDefinitionVersion && detailed.StepLimit == 512 &&
            detailed.ProfileId == "captured-owner" && detailed.RequestId == input.RequestId &&
            detailed.Input.GetProperty("responseSharePercent").GetInt32() == 31,
            "The explicit detailed route must change only the exact edition, retaining normal Chat's owner, input and 512-step budget.");
        check(Refused(() => ResolveMainChatDefinitionVersion(null, null, false, false)) &&
            ResolveMainChatDefinitionVersion(null, null, false, true) == NormalMainChatDefinitionVersion,
            "Fresh normal Chat must require its confirmed context-only edition and never silently drop personality support to legacy.");
        check(ResolveMainChatDefinitionVersion(null, null, true, true) == DetailedToolFeedbackDefinitionVersion,
            "An explicit fresh choice may select only the confirmed detailed edition.");
        check(Refused(() => ResolveMainChatDefinitionVersion(null, null, true, false)),
            "An explicit unavailable or stale detailed choice must refuse instead of silently falling back.");
        check(ResolveMainChatDefinitionVersion(detailed with { DefinitionVersion = "saved-uninstalled-version" }, run, false, false) == "saved-uninstalled-version",
            "A retained pending edition must win over accepted history, the checkbox and discovery, including unknown saved editions.");
        check(ResolveMainChatDefinitionVersion(null, run, true, true) == run.DefinitionVersion &&
            ResolveMainChatDefinitionVersion(null, run with { DefinitionVersion = DetailedToolFeedbackDefinitionVersion }, false, false) == DetailedToolFeedbackDefinitionVersion,
            "An accepted run must retain its exact legacy or detailed edition independently of the current fresh choice.");
        check(Refused(() => ResolveMainChatDefinitionVersion(null, run with { DefinitionVersion = null! }, false, false)) &&
            Refused(() => ResolveMainChatDefinitionVersion(null, run with { DefinitionVersion = " " }, true, true)) &&
            Refused(() => ResolveMainChatDefinitionVersion(detailed with { DefinitionVersion = "" }, run, true, true)) &&
            Refused(() => ResolveMainChatDefinitionVersion(detailed with { DefinitionId = "other:workflow" }, run, true, true)),
            "Malformed saved or pending identities must refuse instead of allowing the factory's legacy default to replace their edition.");
        check(ResolveMainChatDefinitionVersion(null, run with { DefinitionVersion = "saved-uninstalled-version" }, false, false) == "saved-uninstalled-version" &&
            Refused(() => ResolveMainChatDefinitionVersion(null, run with { DefinitionId = "other:workflow" }, true, true)),
            "Saved editions must not be upgraded implicitly, and a foreign saved definition must be refused.");

        var metadata = new Dictionary<string, string> { ["fixture"] = "original" };
        var attachments = new List<ContentPart> { new FileContentPart(new("fixture-attachment", 1, "text/plain", "notes.txt", 7, metadata)) };
        WorkflowConversationSubmission frozen;
        using (var document = JsonDocument.Parse("{\"workingDirectory\":\"frozen\",\"responseSharePercent\":31,\"opaque\":{\"number\":1.00,\"text\":\"Ω\"}}"))
            frozen = WorkflowConversationClient.FreezeSubmission(detailed with { Input = document.RootElement, AcceptedContent = attachments });
        metadata["fixture"] = "changed"; attachments.Clear();
        check(frozen.DefinitionVersion == detailed.DefinitionVersion && frozen.Input.GetProperty("opaque").GetProperty("number").GetRawText() == "1.00" &&
            frozen.AcceptedContent.Single() is FileContentPart file && file.Attachment.Metadata!["fixture"] == "original" &&
            frozen.AssignmentText == "PRIVATE FROZEN ASSIGNMENT" && frozen.StepLimit == 512,
            "The real submission freezer must isolate input-document lifetime and mutable attachment metadata along with the full route.");

        var normal = Entry(ConversationControlClient.ChatDefinitionId, NormalMainChatDefinitionVersion);
        var normalDefinition = new WorkflowDefinitionDescriptor(ConversationControlClient.ChatDefinitionId,
            NormalMainChatDefinitionVersion, "Ordinary fixture", CerebralJson.Element(new { type = "object" }), []);
        check(ValidateMainExactWorkflowCatalog(Catalog([normal]), ConversationControlClient.ChatDefinitionId, NormalMainChatDefinitionVersion) &&
            !ValidateMainExactWorkflowCatalog(Catalog([Entry(ConversationControlClient.ChatDefinitionId, ConversationControlClient.ChatDefinitionVersion)]),
                ConversationControlClient.ChatDefinitionId, NormalMainChatDefinitionVersion),
            "Ordinary discovery must find the exact context-only edition rather than treating legacy support as personality consumption.");
        check(Refused(() => ValidateMainExactWorkflowCatalog(Catalog([normal, normal]),
                ConversationControlClient.ChatDefinitionId, NormalMainChatDefinitionVersion)) &&
            Refused(() => ValidateMainExactWorkflowDescription(Description(normalDefinition, normalDefinition),
                ConversationControlClient.ChatDefinitionId, NormalMainChatDefinitionVersion)) &&
            Refused(() => ValidateMainExactWorkflowDescription(Description(normalDefinition with { Version = ConversationControlClient.ChatDefinitionVersion }),
                ConversationControlClient.ChatDefinitionId, NormalMainChatDefinitionVersion)),
            "Missing, duplicate or wrong-version ordinary declarations must refuse without selecting a fallback.");
        check(ValidateMainExactWorkflowDescription(Description(normalDefinition),
                ConversationControlClient.ChatDefinitionId, NormalMainChatDefinitionVersion).Version == NormalMainChatDefinitionVersion,
            "One exact ordinary description must retain the context-only version.");

        var target = Entry(ConversationControlClient.ChatDefinitionId, DetailedToolFeedbackDefinitionVersion);
        var legacy = Entry(ConversationControlClient.ChatDefinitionId, ConversationControlClient.ChatDefinitionVersion);
        check(ValidateMainChatWorkflowCatalog(Catalog([legacy, target])) &&
            !ValidateMainChatWorkflowCatalog(Catalog([legacy])),
            "Catalog discovery must distinguish the exact detailed edition from ordinary legacy-only support.");
        check(Refused(() => ValidateMainChatWorkflowCatalog(Catalog([target, target]))) &&
            Refused(() => ValidateMainChatWorkflowCatalog(Catalog([target, Entry("other:workflow", "1"), Entry("other:workflow", "1")]))),
            "Duplicate version pairs anywhere in the current catalog must refuse ambiguous discovery.");
        check(Refused(() => ValidateMainChatWorkflowCatalog(Catalog([target], "foreign-contract"))) &&
            Refused(() => ValidateMainChatWorkflowCatalog(Catalog([target], version: WorkflowContract.Version + 1))) &&
            Refused(() => ValidateMainChatWorkflowCatalog(Catalog([new { id = "missing-version", description = "" }]))),
            "Wrong contracts, versions and malformed catalog entries must refuse.");
        var maximumCatalog = Enumerable.Range(0, 255).Select(i => Entry("fixture:workflow-" + i, "1")).Append(target).ToArray();
        check(ValidateMainChatWorkflowCatalog(Catalog(maximumCatalog)) &&
            Refused(() => ValidateMainChatWorkflowCatalog(Catalog(maximumCatalog.Append(legacy).ToArray()))),
            "The actual catalog parser must admit 256 distinct rows and refuse a 257th.");

        var capabilities = Enumerable.Range(0, 128).Select(i => "fixture.capability-" + i).ToArray();
        var definition = new WorkflowDefinitionDescriptor(ConversationControlClient.ChatDefinitionId, DetailedToolFeedbackDefinitionVersion,
            "Detailed fixture", CerebralJson.Element(new { type = "object" }), capabilities);
        var accepted = ValidateMainChatWorkflowDescription(Description(definition));
        check(accepted.Id == definition.Id && accepted.Version == definition.Version &&
            accepted.RequiredCapabilities.SequenceEqual(capabilities, StringComparer.Ordinal) &&
            JsonElement.DeepEquals(accepted.InputSchema, definition.InputSchema),
            "One exact described edition must retain its frozen schema and all 128 required capabilities.");
        check(Refused(() => ValidateMainChatWorkflowDescription(Description(definition, definition))) &&
            Refused(() => ValidateMainChatWorkflowDescription(Description(definition with { Version = ConversationControlClient.ChatDefinitionVersion }))),
            "An absent or ambiguous exact description must never authorize the option.");
        check(Refused(() => ValidateMainChatWorkflowDescription(Description(definition with { RequiredCapabilities = capabilities.Append("fixture.extra").ToArray() }))) &&
            Refused(() => ValidateMainChatWorkflowDescription(Description(definition with { RequiredCapabilities = null! }))) &&
            Refused(() => ValidateMainChatWorkflowDescription(Description(definition with { RequiredCapabilities = [" "] }))) &&
            Refused(() => ValidateMainChatWorkflowDescription(Description(definition with { InputSchema = CerebralJson.Element(new[] { "invalid" }) }))),
            "Oversized, null or blank capability declarations and a nonobject input schema must refuse before submission.");
    }

    /// <summary>Injected command receipts exercise the real client freezer, uncertainty, retry and reopen; no service commands execute.</summary>
    private async Task<int> CheckDetailedMainChatRetryAsync(CancellationToken token)
    {
        if (IsConnected || mainChatDiscoveryCancellation is not null)
            throw new InvalidOperationException("This injected fixture requires an offline window without an active discovery read.");
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        var originalWorkspace = nativeChat; var originalSession = session; var originalId = durableConversationId;
        var originalPending = retainedNativeSubmission; var originalPendingId = retainedNativeConversationId;
        var originalPendingClient = retainedNativeClient; var originalPendingConnection = retainedNativeIdentity;
        var originalPendingConnectionObject = retainedNativeConnection; var originalPendingSource = retainedNativeSource;
        var originalConnectionVersion = knowledgeConnectionVersion;
        var originalAvailability = mainChatWorkflowAvailability; var originalUnavailable = mainChatWorkflowUnavailable;
        var originalNormalAvailability = mainNormalChatWorkflowAvailability;
        var originalDiscoveryGeneration = mainChatDiscoveryGeneration;
        var originalChoice = detailedToolFeedback.IsChecked; var originalChoiceGeneration = detailedToolFeedbackChoiceGeneration;
        var originalProfiles = profiles; var originalLead = leadProfile; var originalShare = selectedResponseSharePercent;
        var originalGeneration = pluginGeneration; var originalSelection = conversationSelectionVersion;
        var originalMetricsClient = systemWorkMetricsClient; var originalInstallation = systemWorkMetricsInstallationId;
        var connectedField = typeof(ConnectedWindow).GetField("<IsConnected>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The established offline connection fixture field is unavailable.");
        var originalConnected = IsConnected;
        var clientField = typeof(ConnectedWindow).GetField("client",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The established offline client fixture field is unavailable.");
        var originalClient = clientField.GetValue(this);
        var originalExactAvailability = mainExactWorkflowAvailability;
        var sdkReads = new List<string>(); var sdkReadGate = new object();
        string[] SdkReads() { lock (sdkReadGate) return sdkReads.ToArray(); }
        var holdCatalog = false; var wrongNormalDescriptor = false;
        var catalogEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldCatalog = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? priorDiscovery = null;
        CommandResult Catalog() => CommandResult.Ok(new
        {
            contract = WorkflowContract.Id, version = WorkflowContract.Version,
            workflows = new[]
            {
                new { id = ConversationControlClient.ChatDefinitionId, version = NormalMainChatDefinitionVersion, description = "Ordinary fixture" },
                new { id = ConversationControlClient.ChatDefinitionId, version = DetailedToolFeedbackDefinitionVersion, description = "Detailed fixture" }
            }
        });
        using var readinessHandler = new AgentSidebarMetricsHandler((command, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (command.ProfileId is not null || command.Arguments.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Unexpected profile-readiness command shape.");
            lock (sdkReadGate) sdkReads.Add(command.Operation);
            if (command.Operation == "sdk.discover" && !command.Arguments.EnumerateObject().Any())
            {
                if (holdCatalog)
                {
                    holdCatalog = false; catalogEntered.TrySetResult();
                    return heldCatalog.Task.WaitAsync(cancellation);
                }
                return Task.FromResult(Catalog());
            }
            if (command.Operation == "sdk.describe" && command.Arguments.EnumerateObject().Count() == 1 &&
                command.Arguments.TryGetProperty("id", out var id) && id.GetString() == ConversationControlClient.ChatDefinitionId)
                return Task.FromResult(CommandResult.Ok(new
                {
                    contract = WorkflowContract.Id, version = WorkflowContract.Version,
                    definitions = new[]
                    {
                        new WorkflowDefinitionDescriptor(ConversationControlClient.ChatDefinitionId,
                            wrongNormalDescriptor ? ConversationControlClient.ChatDefinitionVersion : NormalMainChatDefinitionVersion,
                            "Ordinary fixture", WorkflowContract.EmptyObject, []),
                        new WorkflowDefinitionDescriptor(ConversationControlClient.ChatDefinitionId,
                            DetailedToolFeedbackDefinitionVersion, "Detailed fixture", WorkflowContract.EmptyObject, [])
                    }
                }));
            throw new InvalidDataException("Unexpected profile-readiness operation or arguments.");
        });
        using var readinessClient = new Cerebral.Client.CerebralClient();
        var readinessHttpField = typeof(Cerebral.Client.CerebralClient).GetField("http",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The established offline HTTP fixture field is unavailable.");
        ((System.Net.Http.HttpClient)readinessHttpField.GetValue(readinessClient)!).Dispose();
        readinessHttpField.SetValue(readinessClient, new System.Net.Http.HttpClient(readinessHandler, disposeHandler: false)
            { BaseAddress = new Uri("http://profile-readiness-fixture.invalid/"), Timeout = Timeout.InfiniteTimeSpan });
        var pending = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedReply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptanceEntered = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DesktopOperationResult>? accepting = null;
        var submitted = new List<JsonElement>(); var accept = false; var rejectRetry = false;
        WorkflowRun? saved = null; CerebralEvent? originalMessage = null;
        Task<CommandResult> Dispatch(CommandRequest command, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (command.Operation == "jobs.workflow.submit")
            {
                var value = command.Arguments; submitted.Add(value.Clone());
                if (rejectRetry) return Task.FromResult(CommandResult.Fail("Fixture explicit retry refusal after prior uncertainty."));
                if (!accept) return pending.Task.WaitAsync(cancellation);
                var submission = new WorkflowSubmission(value.GetProperty("requestId").GetString()!, value.GetProperty("conversationId").GetString()!,
                    value.GetProperty("profileId").GetString()!, value.GetProperty("definitionId").GetString()!, value.GetProperty("definitionVersion").GetString()!,
                    value.GetProperty("userText").GetString()!, value.GetProperty("assignmentText").GetString(), value.GetProperty("input").Clone(),
                    WorkflowContract.EmptyObject, value.GetProperty("stepLimit").GetInt32())
                    { AcceptedContent = value.GetProperty("acceptedContent").Deserialize<ContentPart[]>(CerebralJson.Options)! };
                saved = new() { Id = submission.RequestId, Submission = submission, Revision = 1, Status = WorkflowStatus.Completed,
                    CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch };
                originalMessage = new("detailed-original-message", submission.ConversationId, submission.RequestId, 1, CerebralEventTypes.Message,
                    new(EventActorKind.User), [new TextContentPart(submission.UserText), .. submission.AcceptedContent],
                    new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), DateTimeOffset.UnixEpoch);
                acceptanceEntered.TrySetResult(CommandResult.Ok(saved));
                return acceptedReply.Task.WaitAsync(cancellation);
            }
            if (command.Operation == "jobs.workflow.get") return Task.FromResult(CommandResult.Ok(saved ?? throw new InvalidOperationException("No saved fixture run.")));
            if (command.Operation == "jobs.workflow.events.previous")
                return Task.FromResult(CommandResult.Ok(new { events = new[] { originalMessage ?? throw new InvalidOperationException("No saved fixture message.") },
                    snapshotSequence = 1L, nextBeforeSequence = (long?)null }));
            throw new InvalidOperationException("Unexpected detailed fixture command: " + command.Operation);
        }
        using var workspace = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(), (_, _) => { }, embedded: true);
        Task<WorkflowConversationSnapshot>? dispatch = null;
        try
        {
            var discoveryCalls = new List<string>();
            Task<CommandResult> DiscoverOrdinary(CommandRequest command, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                discoveryCalls.Add(command.Operation);
                if (command.ProfileId is not null) throw new InvalidDataException("Discovery unexpectedly selected a profile.");
                return Task.FromResult(command.Operation switch
                {
                    "sdk.discover" => CommandResult.Ok(new { contract = WorkflowContract.Id, version = WorkflowContract.Version,
                        workflows = new[] { new { id = ConversationControlClient.ChatDefinitionId, version = NormalMainChatDefinitionVersion, description = "Ordinary fixture" } } }),
                    "sdk.describe" => CommandResult.Ok(new { contract = WorkflowContract.Id, version = WorkflowContract.Version,
                        definitions = new[] { new WorkflowDefinitionDescriptor(ConversationControlClient.ChatDefinitionId,
                            NormalMainChatDefinitionVersion, "Ordinary fixture", WorkflowContract.EmptyObject, []) } }),
                    _ => throw new InvalidDataException("Unexpected ordinary discovery operation.")
                });
            }
            var normalDescription = await DiscoverMainExactWorkflowAsync(DiscoverOrdinary, ConversationControlClient.ChatDefinitionId,
                NormalMainChatDefinitionVersion, [], () => true, token);
            Check(normalDescription.Version == NormalMainChatDefinitionVersion &&
                discoveryCalls.SequenceEqual(new[] { "sdk.discover", "sdk.describe" }),
                "Fresh ordinary discovery must use the bounded owner catalog and exact descriptor without a detailed-feedback choice.");
            var discoveryCurrent = true; var staleDiscoveryRefused = false; var staleDiscoveryCalls = 0;
            try
            {
                await DiscoverMainExactWorkflowAsync((command, cancellation) =>
                {
                    staleDiscoveryCalls++; discoveryCurrent = false;
                    return DiscoverOrdinary(command, cancellation);
                }, ConversationControlClient.ChatDefinitionId, NormalMainChatDefinitionVersion, [], () => discoveryCurrent, token);
            }
            catch (InvalidOperationException) { staleDiscoveryRefused = true; }
            Check(staleDiscoveryRefused && staleDiscoveryCalls == 1,
                "Ordinary discovery must stop after an epoch change before describing or freezing a submission.");

            nativeChat = workspace; session = null; durableConversationId = "detailed-pending-chat"; workspace.BeginConversation(durableConversationId);
            profiles = [new AgentProfile { Id = "captured-owner", Role = "lead" }, new AgentProfile { Id = "later-lead", Role = "lead" }];
            leadProfile = profiles[0];
            clientField.SetValue(this, readinessClient);
            connectedField.SetValue(this, true);
            systemWorkMetricsClient = Client; systemWorkMetricsInstallationId = "fixture-installed-owner";
            mainChatWorkflowAvailability = new(Client, ServiceConnection, ServiceConnectionIdentity, "fixture-installed-owner",
                knowledgeConnectionVersion, conversationSelectionVersion, durableConversationId, leadProfile.Id,
                leadProfile.Revision, leadProfile.Capabilities.ToArray(), mainChatDiscoveryGeneration,
                new(ConversationControlClient.ChatDefinitionId, DetailedToolFeedbackDefinitionVersion, "Fixture discovered edition",
                    WorkflowContract.EmptyObject, []));
            detailedToolFeedback.IsChecked = false;
            mainNormalChatWorkflowAvailability = mainChatWorkflowAvailability! with { Definition = normalDescription };
            Check(MainNormalChatWorkflowAvailabilityCurrent() &&
                SelectedMainChatDefinitionVersion(workspace.Snapshot!) == NormalMainChatDefinitionVersion,
                "An unchecked fresh Chat must select its confirmed context-only edition with detailed receipts still off.");
            var normalIntent = CaptureMainChatWorkflowIntent() ?? throw new InvalidDataException("Missing ordinary intent.");
            var normalRequest = CreateNativeSubmission(new(PluginBinding(), "ordinary-frozen-request", "Ordinary visible message"),
                ConversationProfile().Id, null, null, null, null, definitionVersion: normalIntent.DefinitionVersion);
            Check(normalIntent.DefinitionVersion == NormalMainChatDefinitionVersion &&
                normalRequest.DefinitionVersion == NormalMainChatDefinitionVersion && submitted.Count == 0,
                "The ordinary Main intent must carry the exact edition into the existing submission freezer before dispatch.");
            var readyProfiles = profiles; var readyLead = leadProfile;
            var readyNormal = mainNormalChatWorkflowAvailability; var readyDetailed = mainChatWorkflowAvailability;
            var readyExact = mainExactWorkflowAvailability; var readyGeneration = mainChatDiscoveryGeneration;
            var readyChoice = detailedToolFeedback.IsChecked;
            var readyChoiceRevision = mainChatWorkflowChoiceRevision; var readyChoiceGeneration = detailedToolFeedbackChoiceGeneration;
            var readyText = prompt.Text; var readyStart = prompt.SelectionStart; var readyLength = prompt.SelectionLength;
            var readyPluginRevision = pluginDraftRevision; var readyEditGeneration = composerDraftEditGeneration;
            var readyChangingDraft = changingComposerDraft;
            try
            {
                changingComposerDraft = true;
                try { prompt.Text = "Synthetic profile refresh draft"; prompt.Select(3, 7); }
                finally { changingComposerDraft = readyChangingDraft; }
                var draftText = prompt.Text; var draftRevision = pluginDraftRevision; var editGeneration = composerDraftEditGeneration;
                void ProfileRevision(int revision)
                {
                    profiles = [new AgentProfile { Id = readyLead!.Id, Role = "lead", Revision = revision }];
                    leadProfile = profiles[0];
                }
                bool DraftPreserved() => prompt.Text == draftText && prompt.SelectionStart == 3 && prompt.SelectionLength == 7 &&
                    pluginDraftRevision == draftRevision && composerDraftEditGeneration == editGeneration &&
                    workspace.Snapshot is { Run: null, RunId: null, LastMutationRequestId: null } &&
                    retainedNativeSubmission is null && submitted.Count == 0;
                ProfileRevision(57);
                mainNormalChatWorkflowAvailability = readyNormal! with { ProfileRevision = 57 };
                Check(MainNormalChatWorkflowAvailabilityCurrent(), "The profile-refresh fixture begins with an exact revision-57 ordinary declaration.");
                ProfileRevision(58);
                Check(!MainNormalChatWorkflowAvailabilityCurrent(), "Replacing cached profile 57 with 58 makes the original declaration stale.");
                await RefreshFreshNormalChatWorkflowAfterProfilesAsync(token);
                Check(MainNormalChatWorkflowAvailabilityCurrent() && mainNormalChatWorkflowAvailability!.ProfileRevision == 58 &&
                    mainNormalChatWorkflowAvailability.Definition.Version == NormalMainChatDefinitionVersion &&
                    SdkReads().SequenceEqual(new[] { "sdk.discover", "sdk.describe" }) && DraftPreserved(),
                    "A normal profile refresh rechecks revision 58 with only bounded owner discovery, preserving draft/selection and creating no request.");
                var unchangedGeneration = mainChatDiscoveryGeneration; var unchangedNormal = mainNormalChatWorkflowAvailability;
                await RefreshFreshNormalChatWorkflowAfterProfilesAsync(token);
                Check(mainChatDiscoveryGeneration == unchangedGeneration && ReferenceEquals(mainNormalChatWorkflowAvailability, unchangedNormal) &&
                    SdkReads().Length == 2 && DraftPreserved(), "An already-current ordinary binding requires no extra discovery or invalidation.");

                ProfileRevision(59); holdCatalog = true;
                priorDiscovery = RefreshMainChatWorkflowAvailabilityAsync(token);
                await catalogEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                ProfileRevision(60);
                await RefreshFreshNormalChatWorkflowAfterProfilesAsync(token);
                var newestNormal = mainNormalChatWorkflowAvailability; var newestGeneration = mainChatDiscoveryGeneration;
                heldCatalog.TrySetResult(Catalog()); await priorDiscovery; priorDiscovery = null;
                Check(MainNormalChatWorkflowAvailabilityCurrent() && newestNormal!.ProfileRevision == 60 &&
                    ReferenceEquals(mainNormalChatWorkflowAvailability, newestNormal) && mainChatDiscoveryGeneration == newestGeneration &&
                    SdkReads().SequenceEqual(new[] { "sdk.discover", "sdk.describe", "sdk.discover", "sdk.discover", "sdk.describe" }) && DraftPreserved(),
                    "The latest cached profile cancels older in-flight discovery; a late revision-59 result cannot replace revision 60.");

                ProfileRevision(61); wrongNormalDescriptor = true;
                await RefreshFreshNormalChatWorkflowAfterProfilesAsync(token);
                var mismatchedRefused = false;
                try { _ = SelectedMainChatDefinitionVersion(workspace.Snapshot!); }
                catch (InvalidOperationException) { mismatchedRefused = true; }
                Check(mismatchedRefused && !MainNormalChatWorkflowAvailabilityCurrent() &&
                    mainNormalChatWorkflowAvailability is null && DraftPreserved(),
                    "A mismatched ordinary descriptor remains unavailable without a legacy fallback, frozen request or draft change.");
                wrongNormalDescriptor = false;

                mainNormalChatWorkflowAvailability = null;
                detailedToolFeedback.IsChecked = true;
                var skipGeneration = mainChatDiscoveryGeneration; var skipReads = SdkReads().Length;
                await RefreshFreshNormalChatWorkflowAfterProfilesAsync(token);
                Check(mainChatDiscoveryGeneration == skipGeneration && SdkReads().Length == skipReads &&
                    detailedToolFeedback.IsChecked == true && mainNormalChatWorkflowAvailability is null && DraftPreserved(),
                    "A profile notification leaves an explicit detailed choice untouched, even when ordinary availability is absent.");
                detailedToolFeedback.IsChecked = false;
                var exactChoice = new MainExactWorkflowAvailability(Client, ServiceConnection, ServiceConnectionIdentity,
                    "fixture-installed-owner", knowledgeConnectionVersion, conversationSelectionVersion, durableConversationId!,
                    leadProfile!.Id, leadProfile.Revision, leadProfile.Capabilities.ToArray(), mainChatDiscoveryGeneration, normalDescription);
                mainExactWorkflowAvailability = exactChoice;
                await RefreshFreshNormalChatWorkflowAfterProfilesAsync(token);
                Check(mainChatDiscoveryGeneration == skipGeneration && SdkReads().Length == skipReads &&
                    ReferenceEquals(mainExactWorkflowAvailability, exactChoice) && DraftPreserved(),
                    "A profile notification never invalidates or replaces an explicit exact-workflow choice.");
                mainExactWorkflowAvailability = null; retainedNativeSubmission = normalRequest;
                await RefreshFreshNormalChatWorkflowAfterProfilesAsync(token);
                Check(mainChatDiscoveryGeneration == skipGeneration && SdkReads().Length == skipReads &&
                    ReferenceEquals(retainedNativeSubmission, normalRequest) && prompt.Text == draftText && submitted.Count == 0,
                    "A retained request is skipped before discovery invalidation and keeps its exact frozen identity.");
                retainedNativeSubmission = null;
            }
            finally
            {
                heldCatalog.TrySetCanceled(token);
                if (priorDiscovery is not null) { try { await priorDiscovery; } catch (OperationCanceledException) { } priorDiscovery = null; }
                profiles = readyProfiles; leadProfile = readyLead;
                detailedToolFeedback.IsChecked = readyChoice;
                mainNormalChatWorkflowAvailability = readyNormal; mainChatWorkflowAvailability = readyDetailed;
                mainExactWorkflowAvailability = readyExact; mainChatDiscoveryGeneration = readyGeneration;
                mainChatWorkflowChoiceRevision = readyChoiceRevision; detailedToolFeedbackChoiceGeneration = readyChoiceGeneration;
                changingComposerDraft = true;
                try { prompt.Text = readyText; prompt.Select(readyStart, readyLength); }
                finally { changingComposerDraft = readyChangingDraft; }
                pluginDraftRevision = readyPluginRevision; composerDraftEditGeneration = readyEditGeneration;
                UpdateMainChatWorkflowChoice();
            }
            var normalBinding = mainNormalChatWorkflowAvailability;
            mainNormalChatWorkflowAvailability = null;
            var absentNormalRefused = false;
            try { _ = CaptureMainChatWorkflowIntent(); }
            catch (InvalidOperationException) { absentNormalRefused = true; }
            Check(absentNormalRefused && submitted.Count == 0,
                "Missing ordinary availability must refuse before capture or a service mutation.");
            mainNormalChatWorkflowAvailability = normalBinding! with { };
            var replacedNormalRefused = false;
            try { RequireMainChatWorkflowIntentCurrent(normalIntent); }
            catch (InvalidOperationException) { replacedNormalRefused = true; }
            Check(replacedNormalRefused && submitted.Count == 0,
                "Replacing an ordinary declaration must invalidate an already captured intent before dispatch.");
            mainNormalChatWorkflowAvailability = normalBinding;
            RequireMainChatWorkflowIntentCurrent(normalIntent);
            detailedToolFeedback.IsChecked = true;
            Check(MainChatWorkflowAvailabilityCurrent() && SelectedMainChatDefinitionVersion(workspace.Snapshot!) == DetailedToolFeedbackDefinitionVersion,
                "The actual availability binding must admit an explicit choice only for its captured client, installation, selection, profile and discovery generation.");
            systemWorkMetricsInstallationId = "fixture-foreign-installed-owner";
            var staleInstallationRefused = false;
            try { _ = SelectedMainChatDefinitionVersion(workspace.Snapshot!); }
            catch (InvalidOperationException) { staleInstallationRefused = true; }
            Check(!MainChatWorkflowAvailabilityCurrent() && staleInstallationRefused,
                "A changed Worker installation must invalidate the actual cached availability and refuse a checked detailed choice.");
            detailedToolFeedback.IsChecked = false;
            var staleNormalRefused = false;
            try { _ = SelectedMainChatDefinitionVersion(workspace.Snapshot!); }
            catch (InvalidOperationException) { staleNormalRefused = true; }
            Check(!MainNormalChatWorkflowAvailabilityCurrent() && staleNormalRefused && submitted.Count == 0,
                "An unchecked normal Chat must refuse stale installation discovery rather than silently switching to legacy.");
            systemWorkMetricsInstallationId = "fixture-installed-owner";
            detailedToolFeedback.IsChecked = true;
            conversationSelectionVersion++;
            var staleSelectionRefused = false;
            try { _ = SelectedMainChatDefinitionVersion(workspace.Snapshot!); }
            catch (InvalidOperationException) { staleSelectionRefused = true; }
            Check(!MainChatWorkflowAvailabilityCurrent() && staleSelectionRefused,
                "A changed conversation selection must invalidate the actual availability and refuse the previously checked detailed choice.");
            conversationSelectionVersion = originalSelection;
            mainChatWorkflowAvailability = null;
            connectedField.SetValue(this, false);
            systemWorkMetricsClient = originalMetricsClient; systemWorkMetricsInstallationId = originalInstallation;
            var request = WorkflowConversationClient.FreezeSubmission(CreateNativeSubmission(
                new(PluginBinding(), "detailed-frozen-request", "Exact visible Ω message"), "captured-owner", @"C:\Projects\frozen-root",
                "frozen-project", 31, "Exact private assignment", [new TextContentPart("Accepted attachment text")], DetailedToolFeedbackDefinitionVersion));
            // Seed the window's already-captured payload; the actual workspace/client below owns transport freezing and receipt validation.
            retainedNativeSubmission = request; retainedNativeConversationId = durableConversationId;
            retainedNativeConnection = ServiceConnection; retainedNativeSource = knowledgeConnectionVersion;
            retainedNativeClient = SemanticAttachClient(); retainedNativeIdentity = ServiceConnectionIdentity;
            dispatch = workspace.SubmitConversationAsync(request, token);
            NewConversation();
            Check(NativeMutationPending && ReferenceEquals(retainedNativeSubmission, request) && detailedToolFeedback.IsChecked == true &&
                durableConversationId == "detailed-pending-chat",
                "Blocked New Chat must retain the selected option and the full captured detailed request before acceptance.");
            pending.SetException(new IOException("Detailed fixture disconnect after dispatch."));
            try { await dispatch; throw new InvalidDataException("Expected detailed uncertainty."); }
            catch (IOException) { }
            detailedToolFeedback.IsChecked = false; selectedResponseSharePercent = 49; leadProfile = profiles[1];
            NewConversation();
            Check(NativeMutationUncertain && ReferenceEquals(retainedNativeSubmission, request) &&
                ConversationProfile().Id == "captured-owner" && SelectedMainChatDefinitionVersion(workspace.Snapshot!) == DetailedToolFeedbackDefinitionVersion,
                "An uncertain request must retain its captured owner and edition despite checkbox, balance and lead changes.");
            var changedRejected = false;
            try { await workspace.SubmitConversationAsync(request with { DefinitionVersion = ConversationControlClient.ChatDefinitionVersion }, token); }
            catch (InvalidOperationException) { changedRejected = true; }
            Check(changedRejected && submitted.Count == 1,
                "Changing the edition on an uncertain retry must refuse before a second command is sent.");
            // Exercise the real window's retained branch: a later explicit refusal cannot erase an earlier uncertain dispatch.
            connectedField.SetValue(this, true);
            systemWorkMetricsClient = Client; systemWorkMetricsInstallationId = "fixture-installed-owner";
            retainedNativeClient = Client; retainedNativeIdentity = ServiceConnectionIdentity;
            knowledgeConnectionVersion++;
            var foreignInstallation = await SubmitNativeDesktopInput(new(PluginBinding(), request.RequestId, request.UserText), null, token);
            Check(foreignInstallation.State == DesktopOperationState.Rejected && submitted.Count == 1 &&
                ReferenceEquals(retainedNativeSubmission, request) && retainedNativeSource == originalConnectionVersion && ReferenceEquals(retainedNativeConnection, ServiceConnection) &&
                !RetainedNativeOwnerCurrent() && NativeMutationUncertain,
                "A retained request must refuse a changed connection epoch before dispatch while keeping its original full uncertain payload.");
            knowledgeConnectionVersion = originalConnectionVersion;
            rejectRetry = true;
            var refusedRetry = await SubmitNativeDesktopInput(new(PluginBinding(), request.RequestId, request.UserText),
                "Changed assignment must not rebuild the retained request.", token);
            Check(refusedRetry.State == DesktopOperationState.Uncertain && NativeMutationUncertain &&
                ReferenceEquals(retainedNativeSubmission, request) && retainedNativeConversationId == "detailed-pending-chat" &&
                ReferenceEquals(retainedNativeClient, Client) && retainedNativeIdentity == ServiceConnectionIdentity &&
                retainedNativeSource == originalConnectionVersion && ReferenceEquals(retainedNativeConnection, ServiceConnection) && submitted.Count == 2 &&
                JsonElement.DeepEquals(submitted[0], submitted[1]),
                "A real window retry refused after an earlier uncertain dispatch must retain its exact full request and original connection ownership.");
            NewConversation();
            Check(ReferenceEquals(retainedNativeSubmission, request) && durableConversationId == "detailed-pending-chat" &&
                Status.Text.Contains("unconfirmed", StringComparison.Ordinal) && submitted.Count == 2,
                "A refused explicit retry must continue blocking New Chat without dropping or replacing uncertain work.");
            rejectRetry = false; accept = true;
            accepting = SubmitNativeDesktopInput(new(PluginBinding(), request.RequestId, request.UserText), null, token);
            var heldSuccess = await acceptanceEntered.Task.WaitAsync(token);
            knowledgeConnectionVersion++;
            acceptedReply.SetResult(heldSuccess);
            var acceptedOperation = await accepting;
            var accepted = workspace.Snapshot ?? throw new InvalidDataException("The original client did not retain its accepted snapshot.");
            Check(submitted.Count == 3 && JsonElement.DeepEquals(submitted[0], submitted[1]) && JsonElement.DeepEquals(submitted[0], submitted[2]) &&
                accepted.Run is { DefinitionVersion: DetailedToolFeedbackDefinitionVersion, ProfileId: "captured-owner", Status: WorkflowStatus.Completed } &&
                saved!.Submission.StepLimit == 512,
                "The same-ID retry must send the exact captured profile, edition, text, assignment, attachments, input and 512-step budget.");
            Check(acceptedOperation.State == DesktopOperationState.Accepted && submitted.Count == 3 &&
                !accepted.MutationUncertain && accepted.RunId == request.RequestId &&
                accepted.LastMutationRequestId == request.RequestId &&
                ReferenceEquals(retainedNativeSubmission, request) && retainedNativeConversationId == "detailed-pending-chat" &&
                ReferenceEquals(retainedNativeClient, Client) && retainedNativeIdentity == ServiceConnectionIdentity &&
                retainedNativeSource == originalConnectionVersion && ReferenceEquals(retainedNativeConnection, ServiceConnection) && !RetainedNativeOwnerCurrent() && !NativeMutationUncertain,
                "A late original-client acceptance after connection epoch drift must retain the full captured owner and payload without a replacement submission.");
            knowledgeConnectionVersion = originalConnectionVersion;
            UpdateNativeChatState();
            Check(retainedNativeSubmission is null && retainedNativeConversationId is null && retainedNativeClient is null && retainedNativeIdentity is null && retainedNativeConnection is null && retainedNativeIdentity is null &&
                SelectedMainChatDefinitionVersion(workspace.Snapshot!) == DetailedToolFeedbackDefinitionVersion,
                "A confirmed matching receipt must clear pending ownership while the accepted detailed edition remains authoritative.");
            var acceptedGeneration = mainChatDiscoveryGeneration; var acceptedReads = SdkReads().Length;
            var acceptedNormal = mainNormalChatWorkflowAvailability; var acceptedSnapshotBeforeRefresh = workspace.Snapshot;
            mainNormalChatWorkflowAvailability = null;
            await RefreshFreshNormalChatWorkflowAfterProfilesAsync(token);
            Check(mainChatDiscoveryGeneration == acceptedGeneration && SdkReads().Length == acceptedReads &&
                mainNormalChatWorkflowAvailability is null && retainedNativeSubmission is null &&
                ReferenceEquals(workspace.Snapshot, acceptedSnapshotBeforeRefresh) && submitted.Count == 3,
                "An accepted run is skipped before normal discovery invalidation and keeps its accepted owner, edition and request.");
            mainNormalChatWorkflowAvailability = acceptedNormal;
            connectedField.SetValue(this, false);
            systemWorkMetricsClient = originalMetricsClient; systemWorkMetricsInstallationId = originalInstallation;
            workspace.Reset(); await workspace.OpenRunSnapshotAsync(saved!.Id, token);
            Check(workspace.Snapshot?.Run is { DefinitionVersion: DetailedToolFeedbackDefinitionVersion } &&
                SelectedMainChatDefinitionVersion(workspace.Snapshot!) == DetailedToolFeedbackDefinitionVersion,
                "Reopening a real saved-run response must recover its detailed edition without depending on the checkbox or discovery.");
            var next = CreateNativeSubmission(new(PluginBinding(), "new-turn-request", "Next message"), ConversationProfile().Id,
                null, null, null, null, definitionVersion: SelectedMainChatDefinitionVersion(workspace.Snapshot!));
            Check(next.RequestId != request.RequestId && next.DefinitionVersion == request.DefinitionVersion && next.ProfileId == request.ProfileId && next.StepLimit == 512,
                "A new turn after reopening must use a fresh request ID while preserving the accepted owner, edition and normal budget.");
            detailedToolFeedback.IsChecked = true;
            BeginNativeChat();
            Check(durableConversationId != "detailed-pending-chat" && workspace.Snapshot?.Run is null && detailedToolFeedback.IsChecked == false &&
                retainedNativeSubmission is null && retainedNativeConversationId is null && retainedNativeClient is null && retainedNativeIdentity is null && retainedNativeConnection is null && retainedNativeIdentity is null,
                "An allowed New Chat transition after acceptance must reset the fresh option and retain cleared pending ownership.");
        }
        finally
        {
            pending.TrySetCanceled(token); acceptedReply.TrySetCanceled(token);
            if (dispatch is not null) { try { await dispatch; } catch (Exception) { } }
            if (accepting is not null) { try { await accepting; } catch (Exception) { } }
            connectedField.SetValue(this, originalConnected);
            clientField.SetValue(this, originalClient);
            mainExactWorkflowAvailability = originalExactAvailability;
            systemWorkMetricsClient = originalMetricsClient; systemWorkMetricsInstallationId = originalInstallation;
            nativeChat = originalWorkspace; session = originalSession; durableConversationId = originalId;
            retainedNativeSubmission = originalPending; retainedNativeConversationId = originalPendingId;
            retainedNativeClient = originalPendingClient; retainedNativeIdentity = originalPendingConnection;
            retainedNativeConnection = originalPendingConnectionObject; retainedNativeSource = originalPendingSource;
            knowledgeConnectionVersion = originalConnectionVersion;
            mainChatWorkflowAvailability = originalAvailability; mainChatWorkflowUnavailable = originalUnavailable;
            mainNormalChatWorkflowAvailability = originalNormalAvailability;
            mainChatDiscoveryGeneration = originalDiscoveryGeneration;
            profiles = originalProfiles; leadProfile = originalLead; selectedResponseSharePercent = originalShare; pluginGeneration = originalGeneration;
            conversationSelectionVersion = originalSelection;
            detailedToolFeedback.IsChecked = originalChoice; detailedToolFeedbackChoiceGeneration = originalChoiceGeneration;
        }
        return checks;
    }
}
