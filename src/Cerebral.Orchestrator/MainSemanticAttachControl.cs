using System.IO;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed record SemanticSelection(string? ConversationId, string? ProjectId, long SelectionVersion,
        int ConnectionVersion, string? ConnectionIdentity);
    private sealed class SemanticInvocation(SemanticSelection expected, CerebralClient? client, object connection,
        CancellationToken token)
    {
        internal SemanticSelection Expected = expected;
        internal readonly CerebralClient? Client = client;
        internal readonly object Connection = connection;
        internal readonly CancellationToken Token = token;
    }
    // Execution-context scoped: a normal UI action during an awaited witness read is not fenced
    // by that witness. Its changed selection instead causes the witness to refuse its next effect.
    private readonly AsyncLocal<SemanticInvocation?> semanticAttachInvocation = new();
    private readonly SemaphoreSlim semanticAttachMutationGate = new(1, 1);
    private sealed record SemanticSubmission(string ConversationId, string RequestId, string? ProjectId,
        string? WorkingDirectory, string State);
    private sealed record MainOwnedSubmissionCoordinates(string ConversationId, string RequestId,
        string? ProjectId, string? WorkingDirectory);
    private SemanticSubmission? semanticSubmission;
    // Last exact request's Main coordinates also survive accepted-receipt cleanup while its
    // original awaiting sender finishes. Identity checks prevent reuse in another conversation.
    private MainOwnedSubmissionCoordinates? mainOwnedSubmissionCoordinates;
    private CerebralClient? semanticSubmissionClient;
    private object? semanticSubmissionConnection;
    private int semanticSubmissionSource;
    private string? semanticSubmissionIdentity;

    private WorkflowConversationSubmission? retainedNativeSubmission;
    private string? retainedNativeConversationId;
    private CerebralClient? retainedNativeClient;
    private object? retainedNativeConnection;
    private int retainedNativeSource;
    private string? retainedNativeIdentity;

    private CerebralClient? SemanticAttachClient() => IsConnected ? Client : null;

    private SemanticSelection SemanticAttachSelection() => new(CurrentConversationId, ConversationProjectId,
        conversationSelectionVersion, knowledgeConnectionVersion, ServiceConnectionIdentity);

    internal async Task<DesktopControlResponse> HandleSemanticAttachControlAsync(DesktopControlRequest request,
        CancellationToken cancellationToken)
        => await HandleSelectionFencedControlAsync(request, cancellationToken, normalSemantic: false);

    internal async Task<DesktopControlResponse> HandleNormalSemanticControlAsync(DesktopControlRequest request,
        CancellationToken cancellationToken)
        => await HandleSelectionFencedControlAsync(request, cancellationToken, normalSemantic: true);

    private async Task<DesktopControlResponse> HandleSelectionFencedControlAsync(DesktopControlRequest request,
        CancellationToken cancellationToken, bool normalSemantic)
    {
        Dispatcher.VerifyAccess();
        DesktopControlProtocol.Validate(request, request.InstanceId);
        var admitted = normalSemantic ? DesktopControlProtocol.NormalSemanticOperations : DesktopControlProtocol.SemanticAttachOperations;
        if (!admitted.Contains(request.Operation))
            throw new UnauthorizedAccessException("This operation is not admitted by the selected semantic control mode.");
        var mutation = DesktopControlProtocol.RequiresSemanticSelection(request.Operation);
        var allowed = request.Operation switch
        {
            "snapshot.inspect" => new[] { "includeLastResponse" },
            "conversation.new" => new[] { "projectId", "profileId", "profileRevision", "initialPathGrants" },
            "conversation.open" => new[] { "id" },
            "conversation.history.back" or "conversation.history.forward" => Array.Empty<string>(),
            "conversation.workflow.select" => new[] { "detailedToolFeedback", "definitionId", "definitionVersion" },
            "conversation.list" or "activity.inspect" => new[] { "limit" },
            "composer.set" => new[] { "text" },
            "message.send" => new[] { "text", "definitionInput" },
            "workflow.open" or "workflow.inspect" => new[] { "runId" },
            "workflow.answer" => new[] { "runId", "questionId", "questionRevision", "answers" },
            "activity.detail" => new[] { "id", "expanded" },
            "wait.settled" => new[] { "timeoutSeconds" },
            DesktopControlProtocol.ConfigurationOpen => Array.Empty<string>(),
            "plugin.action" => new[] { "id" },
            "interface.owner.inspect" => new[] { "id" },
            "interface.owner.review" => new[] { "id", "packageId", "packageVersion", "packageFingerprint",
                "requestSha256", "reviewSha256", "bindingGeneration", "observedControlTestsAllowed",
                "controlTestsAllowed", "confirmInstall" },
            _ => Array.Empty<string>()
        };
        EnsureSemanticArguments(request.Arguments, mutation ? [.. allowed, "expected"] : allowed);
        if (request.Operation == "interface.owner.inspect")
            return await InspectSemanticInterfaceReviewsAsync(request, cancellationToken);
        if (!mutation) return await HandleDesktopControlAsync(request, cancellationToken);
        if (!request.Arguments.TryGetProperty("expected", out var supplied))
            throw new ArgumentException("Mutating semantic attach actions require an exact expected selection.");
        var expected = ReadSemanticSelection(supplied);
        if (!await semanticAttachMutationGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another semantic action is awaiting its receipt.");
        var previous = semanticAttachInvocation.Value;
        try
        {
            semanticAttachInvocation.Value = new(expected, SemanticAttachClient(), ServiceConnection, cancellationToken);
            RequireSemanticAttachCurrent();
            if (normalSemantic && request.Operation == "workflow.answer")
                await RequireNormalLocalChildParentAnswerAsync(request, cancellationToken);
            if (request.Operation is "conversation.history.back" or "conversation.history.forward")
                return await NavigateSemanticConversationHistoryAsync(request,
                    request.Operation == "conversation.history.back");
            if (request.Operation == DesktopControlProtocol.ConfigurationOpen)
                return await OpenConfigurationFromSemanticControlAsync(request, cancellationToken);
            if (request.Operation == "plugin.action")
                return await InvokePluginActionFromSemanticControlAsync(request, cancellationToken);
            if (request.Operation == "interface.owner.review")
                return await ReviewSemanticInterfaceAsync(request, cancellationToken);
            if (request.Operation == "conversation.workflow.select")
            {
                var hasDetailed = request.Arguments.TryGetProperty("detailedToolFeedback", out var choice);
                var hasId = request.Arguments.TryGetProperty("definitionId", out var id);
                var hasVersion = request.Arguments.TryGetProperty("definitionVersion", out var version);
                if (hasDetailed == (hasId || hasVersion) || hasId != hasVersion)
                    throw new ArgumentException("Select either detailed tool feedback or one exact workflow ID and version.");
                if (hasDetailed)
                {
                    if (choice.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new ArgumentException("Choose whether detailed tool feedback is enabled.");
                    await SelectMainChatWorkflowAsync(choice.GetBoolean(), cancellationToken);
                }
                else
                    await SelectMainExactWorkflowAsync(
                        JsonString(id, "definitionId", 256, allowEmpty: false),
                        JsonString(version, "definitionVersion", 64, allowEmpty: false), cancellationToken);
                RequireSemanticAttachCurrent();
                return DesktopControlProtocol.Result(request, "completed", DesktopControlSnapshot(false));
            }
            var arguments = request.Arguments.EnumerateObject().Where(x => x.Name != "expected")
                .ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
            // The server caches the original request, including expected. Only handler dispatch
            // removes this private admission envelope; normal handlers keep their closed schemas.
            return await HandleDesktopControlAsync(request with { Arguments = CerebralJson.Element(arguments) }, cancellationToken);
        }
        finally { semanticAttachInvocation.Value = previous; semanticAttachMutationGate.Release(); }
    }

    // The Work snapshot is a presentation projection. Read the exact full run only through
    // Main's currently bound owner client, retain it in memory, then recheck selection and
    // the displayed form before the existing answer handler can submit a mutation.
    private async Task RequireNormalLocalChildParentAnswerAsync(DesktopControlRequest request,
        CancellationToken token)
    {
        var requestedRunId = RequiredString(request.Arguments, "runId", 100);
        var questionId = RequiredString(request.Arguments, "questionId", 128);
        var questionRevision = RequiredLong(request.Arguments, "questionRevision", 1);
        var displayed = workflowWorkspace?.Snapshot;
        if (displayed?.Run is not { } shown || shown.Id != requestedRunId ||
            shown.ConversationId != CurrentConversationId ||
            shown.Questions is not { } shownQuestions ||
            shownQuestions.Id != questionId || shownQuestions.Revision != questionRevision)
            throw new InvalidOperationException("Normal Main can answer only the current parent form for an exact local helper question.");
        var owner = SemanticAttachClient() ?? throw new InvalidOperationException("The bound owner connection is unavailable.");
        var saved = await owner.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = requestedRunId },
            cancellationToken: token);
        RequireSemanticAttachCurrent();
        if (!ReferenceEquals(displayed, workflowWorkspace?.Snapshot) ||
            !NormalLocalChildParentAnswerMatches(workflowWorkspace?.Snapshot?.Run, saved,
                CurrentConversationId, requestedRunId, questionId, questionRevision))
            throw new InvalidOperationException("The local helper question or Main selection changed before the owner answer.");
    }

    internal static bool NormalLocalChildParentAnswerMatches(WorkflowConversationRun? shown,
        WorkflowRun? saved, string? currentConversationId, string requestedRunId,
        string questionId, long questionRevision)
    {
        if (shown is null || saved is null || string.IsNullOrWhiteSpace(currentConversationId) ||
            shown.Id != requestedRunId || saved.Id != requestedRunId ||
            shown.ConversationId != currentConversationId ||
            saved.Submission.ConversationId != currentConversationId ||
            saved.Submission.ParentRunId is not null || shown.Revision != saved.Revision ||
            shown.Status != WorkflowStatus.Waiting || saved.Status != WorkflowStatus.Waiting ||
            shown.CancelRequested || saved.CancelRequested ||
            shown.EffectState == WorkflowEffectState.Unknown || saved.Effect?.State == WorkflowEffectState.Unknown ||
            shown.Questions is not { Fields.Count: > 0 } displayedQuestions ||
            saved.Questions is not { Fields.Count: > 0 } savedQuestions ||
            displayedQuestions.Id != questionId || displayedQuestions.Revision != questionRevision ||
            savedQuestions.Id != questionId || savedQuestions.Revision != questionRevision ||
            saved.LocalChildQuestionRelay is not { } relay ||
            relay.ParentRunId != saved.Id || relay.ParentQuestionSetId != questionId ||
            relay.ParentQuestionRevision != questionRevision ||
            !saved.WaitingForRunIds.Contains(relay.ChildRunId, StringComparer.Ordinal) ||
            relay.ChildCaller is not { Kind: EventActorKind.Service, Id: "cerebral.coordination" })
            return false;
        return true;
    }

    private async Task<DesktopControlResponse> NavigateSemanticConversationHistoryAsync(DesktopControlRequest request, bool back)
    {
        var before = SemanticAttachSelection();
        var priorVisit = conversationHistory.Current?.Sequence;
        ConversationHistoryNavigationResult outcome;
        try { outcome = await NavigateConversationHistoryCoreAsync(back); }
        catch (Exception error)
        {
            // An awaited owner read may have advanced selection before it failed. Retain the
            // original failure type and current coordinates without suggesting a safe replay.
            return DesktopControlProtocol.Result(request, "uncertain", new
            {
                code = "history_navigation_failed", failureType = error.GetType().Name,
                conversationId = CurrentConversationId, semanticSelection = SemanticAttachSelection(),
                history = new { index = conversationHistory.CursorIndex, currentConversationId = conversationHistory.Current?.ConversationId }
            }, "History navigation did not confirm its final selection. Inspect before another action.");
        }
        var after = SemanticAttachSelection();
        var sameOwner = !Lifetime.IsCancellationRequested &&
            before.ConnectionVersion == after.ConnectionVersion && before.ConnectionIdentity == after.ConnectionIdentity;
        try { RequireSemanticAttachOwner(); }
        catch (Exception) { sameOwner = false; }
        var sameVisit = priorVisit == conversationHistory.Current?.Sequence;
        if (outcome.State == ConversationHistoryNavigationState.Moved && sameOwner && !sameVisit &&
            after.SelectionVersion > before.SelectionVersion && after.ConversationId == conversationHistory.Current?.ConversationId &&
            navigationTree.SelectedItem is System.Windows.Controls.TreeViewItem { Tag: Cerebral.Core.NavigationNode rowNode } row &&
            row.IsSelected && rowNode.TargetId == after.ConversationId)
        {
            try { return DesktopControlProtocol.Result(request, "completed", DesktopControlSnapshot(false)); }
            catch (Exception error)
            {
                return DesktopControlProtocol.Result(request, "uncertain", new
                {
                    code = "history_readback_failed", failureType = error.GetType().Name,
                    conversationId = after.ConversationId, semanticSelection = after,
                    historyIndex = conversationHistory.CursorIndex
                }, "History moved, but its final readback failed. Inspect before another action.");
            }
        }

        var confirmedUnavailable = sameOwner && (priorVisit is null ?
                conversationHistory.Current?.ConversationId == after.ConversationId : sameVisit) &&
            before.ConversationId == after.ConversationId &&
            outcome.State is ConversationHistoryNavigationState.Boundary or
                ConversationHistoryNavigationState.Unavailable or ConversationHistoryNavigationState.ConnectionChanged;
        var code = outcome.State switch
        {
            ConversationHistoryNavigationState.Boundary => "history_boundary",
            ConversationHistoryNavigationState.Unavailable => "history_unavailable",
            ConversationHistoryNavigationState.ConnectionChanged => "history_connection_changed",
            _ => "history_navigation_unconfirmed"
        };
        return DesktopControlProtocol.Result(request, confirmedUnavailable ? "rejected" : "uncertain", new
        {
            code, skippedUnavailable = outcome.SkippedUnavailable,
            conversationId = after.ConversationId, semanticSelection = after,
            historyIndex = conversationHistory.CursorIndex,
            historyConversationId = conversationHistory.Current?.ConversationId
        }, confirmedUnavailable ? "No available chat remains in that direction." :
            "History navigation did not confirm its final selection. Inspect before another action.");
    }

    private static void EnsureSemanticArguments(JsonElement arguments, params string[] allowed)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Arguments must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in arguments.EnumerateObject())
            if (!names.Add(item.Name) || !allowed.Contains(item.Name, StringComparer.Ordinal))
                throw new ArgumentException("Semantic attach argument names must be unique and admitted.");
    }

    private static SemanticSelection ReadSemanticSelection(JsonElement value)
    {
        EnsureSemanticArguments(value, "conversationId", "projectId", "selectionVersion", "connectionVersion", "connectionIdentity");
        if (value.EnumerateObject().Count() != 5 ||
            !value.TryGetProperty("selectionVersion", out var selection) || selection.ValueKind != JsonValueKind.Number || !selection.TryGetInt64(out var version) || version < 0 ||
            !value.TryGetProperty("connectionVersion", out var connection) || connection.ValueKind != JsonValueKind.Number || !connection.TryGetInt32(out var source) || source < 0)
            throw new ArgumentException("Expected selection requires all five exact identity fields.");
        return new(NullableSemanticString(value, "conversationId", 128), NullableSemanticString(value, "projectId", 128),
            version, source, NullableSemanticString(value, "connectionIdentity", 4096));
    }

    private static string? NullableSemanticString(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var field)) throw new ArgumentException("Missing expected " + name + ".");
        if (field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } text ||
            string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw new ArgumentException("Invalid expected " + name + ".");
        return text;
    }

    private void RequireSemanticAttachOwner()
    {
        if (semanticAttachInvocation.Value is not { } invocation) return;
        invocation.Token.ThrowIfCancellationRequested();
        if (Lifetime.IsCancellationRequested || !ReferenceEquals(invocation.Client, SemanticAttachClient()) ||
            !ReferenceEquals(invocation.Connection, ServiceConnection) ||
            invocation.Expected.ConnectionVersion != knowledgeConnectionVersion ||
            invocation.Expected.ConnectionIdentity != ServiceConnectionIdentity)
            throw new InvalidOperationException("The semantic attach connection changed. Inspect the current selection before another action.");
    }

    private void RequireSemanticAttachCurrent()
    {
        if (semanticAttachInvocation.Value is { } invocation)
        {
            RequireSemanticAttachOwner();
            if (invocation.Expected != SemanticAttachSelection())
                throw new InvalidOperationException("The semantic attach selection changed. Inspect it before another action.");
        }
        RequireMainChatWorkflowIntentCurrent(mainChatWorkflowIntent.Value);
    }

    private void AdvanceSemanticAttachNavigation(long selection)
    {
        if (semanticAttachInvocation.Value is not { } invocation) return;
        RequireSemanticAttachOwner();
        if (selection != checked(invocation.Expected.SelectionVersion + 1) ||
            invocation.Expected.ConversationId != CurrentConversationId || invocation.Expected.ProjectId != ConversationProjectId)
            throw new InvalidOperationException("The semantic attach selection changed before navigation.");
        invocation.Expected = invocation.Expected with { SelectionVersion = selection };
    }

    // A preparation may queue a later UI operation without flowing the caller's execution
    // context. Bind its target to these exact coordinates, never to a later ambient invocation.
    private Func<bool>? CaptureSemanticAttachAuthority()
    {
        var workflowIntent = mainChatWorkflowIntent.Value;
        if (semanticAttachInvocation.Value is not { } invocation)
            return workflowIntent is null ? null : () => !Lifetime.IsCancellationRequested &&
                MainChatWorkflowIntentCurrent(workflowIntent);
        var expected = invocation.Expected;
        var client = invocation.Client;
        var connection = invocation.Connection;
        var token = invocation.Token;
        return () => !token.IsCancellationRequested && !Lifetime.IsCancellationRequested &&
            ReferenceEquals(client, SemanticAttachClient()) && ReferenceEquals(connection, ServiceConnection) &&
            expected == SemanticAttachSelection() && MainChatWorkflowIntentCurrent(workflowIntent);
    }

    private void EnsureSemanticAttachSubmissionAllowed()
    {
        if (semanticAttachInvocation.Value is null) return;
        RequireSemanticAttachCurrent();
        if (nativeChat?.Snapshot?.Run is { IsTerminal: false, EffectState: WorkflowEffectState.Unknown })
            throw new InvalidOperationException("The current run needs ordinary reconciliation before semantic attach can send.");
        if (nativePreferencePending && nativeChat?.Snapshot?.Run is not null)
            throw new InvalidOperationException("Confirm the pending chat preference through the ordinary interface before semantic attach can send.");
    }

    private void RequireSemanticAttachNavigationCompleted(string target)
    {
        if (semanticAttachInvocation.Value is not { } invocation) return;
        RequireSemanticAttachOwner();
        if (invocation.Expected.SelectionVersion != conversationSelectionVersion ||
            ChatReference.NormalizeTarget(target) != CurrentConversationId)
            throw new InvalidOperationException("The requested chat was not selected on the original connection. Inspect before retrying.");
    }

    private async Task NewSemanticConversationAsync(JsonElement arguments, CancellationToken token)
    {
        EnsureSemanticArguments(arguments, "projectId", "profileId", "profileRevision", "initialPathGrants");
        var choice = ReadFreshChatSelection(arguments);
        var hasProject = arguments.TryGetProperty("projectId", out _);
        var projectId = hasProject ? RequiredString(arguments, "projectId", 128) : ProjectForNewConversation();
        RequireSemanticAttachCurrent();
        EnsureNativeNavigationAllowed();
        if (!hasProject && choice.ProfileId is null)
        {
            NewConversationInProject(projectId, choice); SelectPage("chat"); return;
        }
        var client = SemanticAttachClient(); var connection = ServiceConnection; var identity = ServiceConnectionIdentity;
        var source = knowledgeConnectionVersion; var selection = conversationSelectionVersion;
        var conversation = CurrentConversationId; var project = ConversationProjectId;
        void Current()
        {
            token.ThrowIfCancellationRequested();
            RequireSemanticAttachCurrent();
            if (!ReferenceEquals(client, SemanticAttachClient()) || !ReferenceEquals(connection, ServiceConnection) ||
                source != knowledgeConnectionVersion || identity != ServiceConnectionIdentity ||
                selection != conversationSelectionVersion || conversation != CurrentConversationId || project != ConversationProjectId ||
                !NativePreferenceConnectionCurrent(connection, identity))
                throw new InvalidOperationException("The profile or project lookup belongs to an earlier conversation or connection.");
        }
        Current();
        using var captured = nativePreferenceCommandOverride is null ? CreateIndependentClient() : null;
        var command = NativePreferenceCommand(captured);
        List<AgentProfile>? currentProfiles = null;
        if (choice.ProfileId is not null)
        {
            var result = await command("agents.list", null, token);
            Current();
            if (!result.Success) throw new InvalidDataException("The current service could not confirm existing profiles.");
            currentProfiles = CerebralJson.Read<List<AgentProfile>>(result.Data);
            _ = ResolveFreshChatProfile(currentProfiles, choice);
        }
        NavigationState? registered = null;
        if (hasProject)
        {
            var result = await command("navigation.get", null, token);
            Current();
            if (!result.Success) throw new InvalidDataException("The current service could not confirm existing projects.");
            registered = CerebralJson.Read<NavigationState>(result.Data);
            var matches = registered.Nodes.Where(x => x.Id == projectId).Take(2).ToArray();
            if (matches.Length != 1 || matches[0].Kind != "project" ||
                string.IsNullOrWhiteSpace(matches[0].Path) || !Path.IsPathFullyQualified(matches[0].Path!))
                throw new ArgumentException("Select one existing registered project with a fully qualified directory.");
        }
        Current();
        EnsureNativeNavigationAllowed();
        if (currentProfiles is not null)
        {
            profiles = currentProfiles;
            var leads = profiles.Where(x => x.Role == "lead" && x.WorkerId == "local").Take(2).ToArray();
            leadProfile = leads.Length == 1 ? leads[0] : null;
        }
        if (registered is not null) navigation = registered;
        // No await separates admitted readback and the ordinary New handler's immutable draft installation.
        NewConversationInProject(projectId, choice); SelectPage("chat");
    }

    private object SemanticAttachCoordinates()
    {
        var saved = loadedPropertiesSource == knowledgeConnectionVersion &&
            loadedConversationProperties is { } value && value.Id == CurrentConversationId ? value : null;
        var submitted = SemanticSubmissionCurrent() && semanticSubmission?.ConversationId == CurrentConversationId
            ? semanticSubmission : null;
        var root = saved is { RootState: "bound" } && saved.ProjectId == ConversationProjectId ? saved.Root :
            submitted is { State: "accepted" } && submitted.ProjectId == ConversationProjectId ? submitted.WorkingDirectory : null;
        return new
        {
            current = new { conversationId = CurrentConversationId, projectId = ConversationProjectId, workingDirectory = root },
            saved = saved is null ? null : new { conversationId = saved.Id, projectId = saved.ProjectId,
                workingDirectory = saved.RootState == "bound" ? saved.Root : null },
            submitted
        };
    }

    private bool SemanticSubmissionCurrent() => ReferenceEquals(semanticSubmissionClient, SemanticAttachClient()) &&
        ReferenceEquals(semanticSubmissionConnection, ServiceConnection) && semanticSubmissionSource == knowledgeConnectionVersion &&
        semanticSubmissionIdentity == ServiceConnectionIdentity;

    private void CaptureSemanticSubmission(WorkflowConversationSubmission request)
    {
        RequireSemanticAttachCurrent();
        var coordinates = mainOwnedSubmissionCoordinates is { } captured &&
            captured.ConversationId == durableConversationId && captured.RequestId == request.RequestId
            ? captured : null;
        if (request.DefinitionId != ConversationControlClient.ChatDefinitionId && coordinates is null)
            throw new InvalidOperationException("The exact workflow request has no frozen Main coordinates.");
        semanticSubmission = new(durableConversationId!, request.RequestId,
            coordinates is null ? SubmissionCoordinate(request.Input, "projectId") : coordinates.ProjectId,
            coordinates is null ? SubmissionCoordinate(request.Input, "workingDirectory") : coordinates.WorkingDirectory,
            "submitting");
        semanticSubmissionClient = SemanticAttachClient(); semanticSubmissionConnection = ServiceConnection;
        semanticSubmissionSource = knowledgeConnectionVersion; semanticSubmissionIdentity = ServiceConnectionIdentity;
    }

    private static string? SubmissionCoordinate(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private void MarkSemanticSubmission(string requestId, string state)
    {
        if (semanticSubmission is { } captured && captured.RequestId == requestId)
            semanticSubmission = captured with { State = state };
    }

    private bool RetainedNativeOwnerCurrent() => retainedNativeSubmission is null ||
        retainedNativeConversationId == durableConversationId && ReferenceEquals(retainedNativeClient, SemanticAttachClient()) &&
        ReferenceEquals(retainedNativeConnection, ServiceConnection) && retainedNativeSource == knowledgeConnectionVersion &&
        retainedNativeIdentity == ServiceConnectionIdentity;

    private Task<CommandResult> CommandRetainedNativeChatAsync(CommandRequest request, CancellationToken token)
    {
        if (!RetainedNativeOwnerCurrent())
            throw new InvalidOperationException("The retained request belongs to its original connection. Reconnect there before retrying.");
        return Client.CommandAsync(request.Operation, request.Arguments, request.ProfileId, token);
    }

    private void ClearRetainedNativeSubmission()
    {
        retainedNativeSubmission = null; retainedNativeConversationId = null; retainedNativeClient = null;
        retainedNativeConnection = null; retainedNativeIdentity = null;
    }
}
