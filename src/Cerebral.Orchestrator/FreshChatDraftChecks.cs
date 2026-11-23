using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>Unshown WPF/normal-adapter fixtures. Only task-owned temporary composer state is written.
/// Worker commands are deterministic delegates; no loaded window, package, pipe, model or service.</summary>
public static class FreshChatDraftChecks
{
    public static int Run()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Fresh draft checks require an STA dispatcher.");
        var dispatcher = Dispatcher.CurrentDispatcher;
        var priorContext = SynchronizationContext.Current;
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var priorMain = application.MainWindow;
        var root = Path.Combine(Path.GetTempPath(), "cerebral-fresh-draft-" + Guid.NewGuid().ToString("N"));
        OrchestratorWindow? window = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        var loaded = 0;
        var result = 0;
        try
        {
            Directory.CreateDirectory(root);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            window = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(root, "Plugins")),
                Path.Combine(root, "PluginData"), composerDraftDirectory: Path.Combine(root, "Composer"));
            window.Loaded += (_, _) => loaded++;
            var task = window.CheckFreshChatDraftAsync(root);
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            result = task.GetAwaiter().GetResult();
            if (loaded != 0 || window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The fixture created a rendered Main window.");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            void Clean(Action action) { try { action(); } catch (Exception error) { cleanup.Add(error); } }
            if (window is not null) Clean(window.Close);
            Clean(() => application.MainWindow = priorMain);
            Clean(() => SynchronizationContext.SetSynchronizationContext(priorContext));
            // The sole created directory is the exact fresh task-owned root above.
            if (Directory.Exists(root)) Clean(() => Directory.Delete(root, recursive: true));
        }
        if (primary is not null && cleanup.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary is not null) cleanup.Insert(0, primary);
        if (cleanup.Count != 0) throw new AggregateException("Fresh draft fixture cleanup failed.", cleanup);
        return result;
    }
}

public sealed partial class OrchestratorWindow
{
    public async Task<int> CheckFreshChatDraftAsync(string fixtureRoot)
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext || IsConnected || IsLoaded || IsVisible ||
            new WindowInteropHelper(this).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Use the unshown disconnected fixture dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or
                JsonException or OperationCanceledException) { checks++; return; }
            throw new InvalidOperationException(label);
        }
        void RejectValue(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or JsonException)
            { checks++; return; }
            throw new InvalidOperationException(label);
        }
        void RejectExactOwnerMismatch(Action action, string label)
        {
            try { action(); }
            catch (InvalidDataException error) when (error.Message ==
                "The accepted workflow input no longer matches this conversation and owner.")
            { checks++; return; }
            throw new InvalidOperationException(label);
        }

        const string instance = "00112233445566778899aabbccddeeff";
        var lead = new AgentProfile { Id = "fresh-lead", Name = "Fixture lead", Role = "lead",
            WorkerId = "local", Revision = 3, AllowedRoots = [fixtureRoot], Capabilities = [] };
        var helper = new AgentProfile { Id = "fresh-helper", Name = "Fixture helper", Role = "helper",
            WorkerId = "local", Revision = 8, AllowedRoots = [fixtureRoot], Capabilities = [] };
        var sourceProfiles = new List<AgentProfile> { lead, helper };
        var pathCalls = new List<string>();
        var sdkCalls = new List<string>();
        var sdkPayloads = new List<JsonElement>();
        JsonElement? savedPaths = null;
        var saveThrows = false; var readMismatch = false;
        Func<Task<CommandResult>>? profileRead = null;
        var sdkBehavior = "refused";
        var binding = true;
        var source = knowledgeConnectionVersion;
        var originalWorkspace = nativeChat;
        var originalProfiles = profiles; var originalLead = leadProfile;
        var originalCommand = nativePreferenceCommandOverride; var originalBinding = nativePreferenceBindingOverride;
        var originalDirectory = projectsDirectory; var originalNavigation = navigation;
        var originalProject = draftProjectId; var originalSurface = conversationPrimarySurface;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        // Read the existing unconnected fixture client; the protected Client property correctly refuses offline use.
        var originalClient = typeof(ConnectedWindow).GetField("client", flags)?.GetValue(this) as CerebralClient
            ?? throw new InvalidOperationException("The existing unshown fixture client is absent.");
        var httpField = typeof(CerebralClient).GetField("http", flags)
            ?? throw new InvalidOperationException("The existing fixture HTTP seam is absent.");
        var localField = typeof(CerebralClient).GetField("local", flags)
            ?? throw new InvalidOperationException("The existing fixture local transport field is absent.");
        if (localField.GetValue(originalClient) is not null)
            throw new InvalidOperationException("The fixture must start without a local transport.");
        var originalHttp = (HttpClient)httpField.GetValue(originalClient)!;
        using var refusal = new FreshDraftRefusingHandler();
        using var inertHttp = new HttpClient(refusal) { BaseAddress = new Uri("http://fresh-draft.invalid/") };
        httpField.SetValue(originalClient, inertHttp);

        WorkflowRun? accepted = null;
        WorkflowRun Run(JsonElement value) => new()
        {
            Id = value.GetProperty("requestId").GetString()!, Revision = 1, Status = WorkflowStatus.Completed,
            Submission = new(value.GetProperty("requestId").GetString()!, value.GetProperty("conversationId").GetString()!,
                value.GetProperty("profileId").GetString()!, value.GetProperty("definitionId").GetString()!,
                value.GetProperty("definitionVersion").GetString()!, value.GetProperty("userText").GetString()!,
                value.GetProperty("assignmentText").GetString(), value.GetProperty("input").Clone(), WorkflowContract.EmptyObject,
                value.GetProperty("stepLimit").GetInt32())
                {
                    AcceptedContent = CerebralJson.Read<ContentPart[]>(value.GetProperty("acceptedContent")),
                    ProjectId = value.TryGetProperty("ownerProjectId", out var project) ? project.GetString() : null,
                    LocalGeneration = value.TryGetProperty("localGeneration", out var generation) &&
                        generation.ValueKind != JsonValueKind.Null ? CerebralJson.Read<WorkflowLocalGenerationChoice>(generation) : null
                },
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch
        };
        using var isolated = new WorkflowWorkspace((request, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            sdkCalls.Add(request.Operation);
            if (request.Operation == "jobs.workflow.submit")
            {
                sdkPayloads.Add(request.Arguments.Clone());
                if (sdkBehavior == "lost") throw new IOException("Fixture acknowledgement was lost.");
                if (sdkBehavior == "accepted")
                {
                    accepted = Run(request.Arguments);
                    // Prevent unrelated navigation/preference reads after this deliberately late acceptance.
                    knowledgeConnectionVersion++;
                    return Task.FromResult(CommandResult.Ok(accepted));
                }
                return Task.FromResult(CommandResult.Fail("Fixture admission refused."));
            }
            if (request.Operation == "jobs.workflow.events.previous" && accepted is not null)
            {
                var message = new CerebralEvent("fresh-fixture-user", accepted.Submission.ConversationId, accepted.Id, 1,
                    CerebralEventTypes.Message, new(EventActorKind.User), [new TextContentPart(accepted.Submission.UserText)],
                    new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), DateTimeOffset.UnixEpoch);
                return Task.FromResult(CommandResult.Ok(new { events = new[] { message }, snapshotSequence = 1L, nextBeforeSequence = (long?)null }));
            }
            throw new InvalidOperationException("Unexpected fixture adapter read.");
        }, () => sourceProfiles, (_, _) => { }, embedded: true);
        Func<string, object?, CancellationToken, Task<CommandResult>> command = (operation, args, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            var value = CerebralJson.Element(args);
            if (operation == "agents.list") return profileRead?.Invoke() ?? Task.FromResult(CommandResult.Ok(sourceProfiles));
            if (operation == "navigation.get") return Task.FromResult(CommandResult.Ok(new NavigationState()));
            pathCalls.Add(operation);
            if (operation == "conversations.paths.save")
            {
                var prepared = freshChatPreparation ?? throw new InvalidOperationException("The ordinary request was not frozen before save.");
                Check(ReferenceEquals(retainedNativeSubmission, prepared.Request) &&
                      value.GetProperty("expectedRevision").GetInt64() == 0 &&
                      value.GetProperty("initial").GetProperty("firstRequestId").GetString() == prepared.Request.RequestId &&
                      value.GetProperty("grants").GetArrayLength() == 1 &&
                      value.GetProperty("grants")[0].GetProperty("access").GetString() == "read",
                    "The actual save boundary sees the frozen ordinary first RID before dispatch.");
                if (saveThrows) throw new IOException("Fixture save acknowledgement was lost.");
                savedPaths = CerebralJson.Element(new
                {
                    id = prepared.Draft.ConversationId, profileId = prepared.ProfileId, projectId = prepared.ProjectId,
                    rootState = "prepared", root = prepared.WorkingDirectory, revision = 1L,
                    initialState = "prepared", initial = FreshChatInitialTuple(prepared), grants = prepared.Grants
                });
                return Task.FromResult(CommandResult.Ok(savedPaths.Value));
            }
            if (operation == "conversations.paths.get")
            {
                if (savedPaths is not { } paths) return Task.FromResult(CommandResult.Fail("Fixture initial paths unavailable."));
                if (readMismatch)
                {
                    var prepared = freshChatPreparation!;
                    return Task.FromResult(CommandResult.Ok(new
                    {
                        id = prepared.Draft.ConversationId, profileId = prepared.ProfileId, projectId = prepared.ProjectId,
                        rootState = "prepared", root = prepared.WorkingDirectory, revision = 1L, initialState = "prepared",
                        initial = new { firstRequestId = "different-original", profileId = prepared.ProfileId,
                            expectedProfileRevision = prepared.ProfileRevision, projectId = prepared.ProjectId,
                            workingDirectory = prepared.WorkingDirectory }, grants = prepared.Grants
                    }));
                }
                return Task.FromResult(CommandResult.Ok(paths));
            }
            throw new InvalidOperationException("Unexpected fresh draft owner operation.");
        };
        int sequence = 0;
        DesktopControlRequest Request(string op, object body) =>
            new(instance, "fresh-check-" + ++sequence, op, CerebralJson.Element(body));
        object Expected() => SemanticAttachSelection();
        DesktopSubmission Input(string rid, string text = "Controlled fixture prompt") => new(PluginBinding(), rid, text);
        async Task NewExplicit()
        {
            profileRead = null; savedPaths = null; saveThrows = readMismatch = false;
            var response = await HandleSemanticAttachControlAsync(Request("conversation.new",
                new { expected = Expected(), profileId = helper.Id, profileRevision = 8,
                    initialPathGrants = new[] { new { path = Path.Combine(fixtureRoot, "Reference"), access = "read" } } }), token);
            Check(response.State == "completed" && ConversationProfile().Id == helper.Id,
                "The actual semantic New selects an existing local helper at its persisted revision.");
        }
        try
        {
            nativeChat = isolated; nativePreferenceCommandOverride = command;
            nativePreferenceBindingOverride = () => binding;
            profiles = sourceProfiles; leadProfile = lead; session = null;
            projectsDirectory = fixtureRoot; navigation = new(); draftProjectId = null; conversationPrimarySurface = null;
            isolated.BeginConversation(durableConversationId!);
            foreach (var body in new object[]
            {
                new { profileId = helper.Id }, new { profileRevision = 8 },
                new { profileId = helper.Id, profileRevision = 0 },
                new { profileId = helper.Id, profileRevision = "8" },
                new { initialPathGrants = Array.Empty<object>() }
            }) RejectValue(() => ReadFreshChatSelection(CerebralJson.Element(body)), "Malformed explicit selection was admitted.");
            foreach (var role in new[] { "compactor", "remote" })
            {
                var invalid = new AgentProfile { Id = helper.Id, Revision = 8,
                    Role = role == "compactor" ? role : "helper", WorkerId = role == "remote" ? "peer" : "local" };
                RejectValue(() => ResolveFreshChatProfile([invalid], new(helper.Id, 8, [])),
                    "Only existing local lead/helper profiles are admitted.");
            }
            RejectValue(() => ResolveFreshChatProfile([helper, helper], new(helper.Id, 8, [])), "Duplicate profile identity was admitted.");
            RejectValue(() => ResolveFreshChatProfile(sourceProfiles, new(helper.Id, 7, [])), "Stale persisted revision was admitted.");
            foreach (var grants in new object[]
            {
                new[] { new { path = fixtureRoot, access = "readWrite" } },
                new[] { new { path = fixtureRoot, access = "read" }, new { path = fixtureRoot + Path.DirectorySeparatorChar, access = "read" } },
                new[] { new { path = "..", access = "write" } }
            }) RejectValue(() => ReadFreshChatSelection(CerebralJson.Element(new { profileId = helper.Id, profileRevision = 8, initialPathGrants = grants })),
                "Malformed or duplicate grants were admitted.");
            var previous = durableConversationId;
            var entered = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            profileRead = () => entered.Task;
            var stale = NewSemanticConversationAsync(CerebralJson.Element(new { profileId = helper.Id, profileRevision = 8 }), token);
            knowledgeConnectionVersion++;
            entered.SetResult(CommandResult.Ok(sourceProfiles));
            await Reject(() => stale, "A stale profile response changed the selected chat.");
            Check(previous == durableConversationId, "Stale profile read never installs a replacement draft.");
            knowledgeConnectionVersion = source;
            profileRead = null;
            var observedHelperAtBegin = false;
            EventHandler observe = (_, _) => observedHelperAtBegin |= freshChatDraft is { } observedDraft &&
                observedDraft.ConversationId == durableConversationId &&
                observedDraft.Selection.ProfileId == helper.Id && TryConversationProfile()?.Id == helper.Id;
            isolated.SnapshotChanged += observe;
            try { await NewExplicit(); }
            finally { isolated.SnapshotChanged -= observe; }
            Check(observedHelperAtBegin, "The explicit owner is installed before normal New publishes its first snapshot callback.");
            var exactSchema = JsonDocument.Parse(
                """{"type":"object","properties":{"projectId":{},"workingDirectory":{}},"required":["projectId","workingDirectory"],"additionalProperties":false}""")
                .RootElement.Clone();
            var pluginCoordinates = new[]
            {
                CerebralJson.Element(new { projectId = (object?)null, workingDirectory = (object?)null }),
                CerebralJson.Element(new { projectId = new { plugin = "project" }, workingDirectory = new { plugin = "root" } }),
                CerebralJson.Element(new { projectId = "plugin-project", workingDirectory = "plugin-relative-root" })
            };
            for (var variant = 0; variant < pluginCoordinates.Length; variant++)
            {
                if (variant != 0) await NewExplicit();
                var ownedRoot = Path.Combine(fixtureRoot, "MainOwnedRoot");
                var exactRequest = WorkflowConversationClient.FreezeSubmission(CreateNativeSubmission(
                    Input("exact-first-" + variant), helper.Id, ownedRoot, "main-owned-project", 25, null,
                    definitionVersion: "2.0.0", definitionInput: pluginCoordinates[variant],
                    definitionId: "fixture:develop", exactSchema: exactSchema));
                CaptureFreshChatPreparation(exactRequest, ConversationProfile(),
                    "main-owned-project", ownedRoot, exactDefinition: true);
                Check(freshChatPreparation is { ProjectId: "main-owned-project" } prepared &&
                      prepared.WorkingDirectory == ownedRoot &&
                      JsonElement.DeepEquals(exactRequest.Input, pluginCoordinates[variant]),
                    "Explicit-profile New keeps Main path authority separate from schema-owned plugin fields.");
                mainOwnedSubmissionCoordinates = new(durableConversationId!, exactRequest.RequestId,
                    "main-owned-project", ownedRoot);
                CaptureSemanticSubmission(exactRequest);
                Check(semanticSubmission is { ProjectId: "main-owned-project" } reported &&
                      reported.WorkingDirectory == ownedRoot,
                    "Exact semantic coordinates report independently resolved Main fields.");
                CaptureFreshChatPreparation(exactRequest, ConversationProfile(),
                    "main-owned-project", ownedRoot, exactDefinition: true);
                Check(ReferenceEquals(freshChatPreparation?.Request, exactRequest),
                    "The frozen first-request preparation survives an exact same-RID retry.");
                ClearRetainedNativeSubmission();
                CaptureSemanticSubmission(exactRequest);
                Check(semanticSubmission is { ProjectId: "main-owned-project" } afterCleanup &&
                      afterCleanup.WorkingDirectory == ownedRoot,
                    "Accepted-original receipt cleanup retains the exact Main reporting coordinates.");
            }
            await NewExplicit();
            // The normal agents.list readback is detached from the fixture's service-side profile objects.
            var selectedHelper = profiles.Single(x => x.Id == helper.Id);
            var selectedLead = profiles.Single(x => x.Id == lead.Id);
            Check(FreshChatProfileAdmissible(selectedHelper) && !FreshChatProfileAdmissible(selectedLead),
                "One selected-profile predicate admits the explicit helper without granting the default lead's role.");
            var publicSelection = CerebralJson.Element(FreshChatProfileSelectionSnapshot());
            Check(publicSelection.GetProperty("origin").GetString() == "explicit_draft" &&
                  publicSelection.GetProperty("expectedProfileRevision").GetInt32() == 8 &&
                  CerebralJson.Element(FreshChatPathAuthoritySnapshot()).GetProperty("state").GetString() == "intent",
                "Public readback distinguishes selected revision and unprepared authority intent.");
            var first = Input("fresh-first");
            saveThrows = true;
            var failed = await SubmitNativeDesktopInput(first, null, token);
            Check(failed.State == DesktopOperationState.Rejected && sdkCalls.Count == 0 &&
                  retainedNativeSubmission?.RequestId == first.RequestId && freshChatAuthorityState == "held",
                "An unconfirmed path save submits no inference and retains the original frozen first RID.");
            Check(pathCalls.SequenceEqual(new[] { "conversations.paths.save", "conversations.paths.get" }),
                "The failed preparation performs one bounded readback with no automatic save replay.");
            var frozen = retainedNativeSubmission;
            var priorCalls = pathCalls.Count;
            var changed = await SubmitNativeDesktopInput(first with { Text = "Changed text" }, null, token);
            Check(changed.State == DesktopOperationState.Rejected && ReferenceEquals(frozen, retainedNativeSubmission) && pathCalls.Count == priorCalls,
                "Changed first text cannot replace an immutable preparation.");
            var foreign = await SubmitNativeDesktopInput(first with { RequestId = "different-rid" }, null, token);
            Check(foreign.State == DesktopOperationState.Rejected && pathCalls.Count == priorCalls && sdkCalls.Count == 0,
                "A replacement RID requires a new chat without touching path or SDK state.");
            saveThrows = false; readMismatch = true;
            var mismatched = await SubmitNativeDesktopInput(first, null, token);
            Check(mismatched.State == DesktopOperationState.Rejected && sdkCalls.Count == 0 &&
                  ReferenceEquals(frozen, retainedNativeSubmission) && freshChatPreparedRevision is null,
                "A successful save acknowledgement cannot substitute for exact original-RID readback.");
            readMismatch = false;
            var refused = await SubmitNativeDesktopInput(first, null, token);
            Check(refused.State == DesktopOperationState.Rejected && sdkCalls.SequenceEqual(new[] { "jobs.workflow.submit" }) &&
                  ReferenceEquals(frozen, retainedNativeSubmission) && freshChatPreparedRevision == 1,
                "Explicit same-RID retry confirms preparation then reaches the normal SDK; known refusal keeps its first payload.");
            Check(sdkPayloads[0].GetProperty("requestId").GetString() == first.RequestId &&
                  sdkPayloads[0].GetProperty("profileId").GetString() == helper.Id &&
                  sdkPayloads[0].GetProperty("definitionVersion").GetString() == ConversationControlClient.ChatDefinitionVersion,
                "The normal SDK receives the original RID, helper and ordinary Chat edition.");
            var immutablePreparation = freshChatPreparation;
            var callsBeforeLost = pathCalls.Count;
            sdkBehavior = "lost";
            var lost = await SubmitNativeDesktopInput(first, null, token);
            Check(lost.State == DesktopOperationState.Uncertain && isolated.Snapshot?.Run is null &&
                  retainedNativeSubmission is not null && pathCalls.Count == callsBeforeLost,
                "An SDK acknowledgement loss keeps its exact request and does not reprepare.");
            helper.Revision = 9; selectedHelper.Revision = 9;
            sdkBehavior = "accepted";
            var recovered = await SubmitNativeDesktopInput(first, null, token);
            Check(recovered.State == DesktopOperationState.Accepted && selectedHelper.Revision == 9 && pathCalls.Count == callsBeforeLost &&
                  sdkPayloads.Count == 3 && sdkPayloads.All(x => x.GetRawText() == sdkPayloads[0].GetRawText()),
                "Accepted-RID lookup precedes stale fresh-profile admission even without a prior run snapshot.");
            Check(freshChatInitialAccepted && ReferenceEquals(immutablePreparation, freshChatPreparation) &&
                  InitialAcceptedProfileId is null,
                "Late acceptance retains initial provenance but cannot lend profile authority across the changed binding.");
            knowledgeConnectionVersion = source;
            // Adopt the already accepted receipt through the normal current-owner lifecycle before a later turn.
            // No pendingSubmission or canonical result event exists here, so this cannot write preferences or navigation.
            UpdateNativeChatState();
            Check(InitialAcceptedProfileId == helper.Id && retainedNativeSubmission is null,
                "Current-owner receipt adoption clears the retained request while preserving its accepted-profile fallback.");
            helper.Revision = 8; selectedHelper.Revision = 8; sdkBehavior = "refused";
            isolated.BeginConversation(durableConversationId!);
            var later = Input("fresh-later", "Later turn");
            var laterResult = await SubmitNativeDesktopInput(later, null, token);
            Check(laterResult.State == DesktopOperationState.Rejected && pathCalls.Count == callsBeforeLost &&
                  sdkPayloads[^1].GetProperty("requestId").GetString() == later.RequestId &&
                  ReferenceEquals(immutablePreparation, freshChatPreparation),
                "A later ordinary turn uses the accepted owner with a new RID and never overwrites initial preparation.");
            // Default New retains the previous behavior, including known-refusal cleanup.
            await HandleSemanticAttachControlAsync(Request("conversation.new", new { expected = Expected() }), token);
            pathCalls.Clear(); sdkCalls.Clear(); sdkPayloads.Clear();
            var defaultResult = await SubmitNativeDesktopInput(Input("default-first"), null, token);
            Check(defaultResult.State == DesktopOperationState.Rejected && pathCalls.Count == 0 &&
                  sdkCalls.SequenceEqual(new[] { "jobs.workflow.submit" }) && retainedNativeSubmission is null,
                "Default ordinary send uses no paths API and keeps its prior known-refusal cleanup.");
            Check(ConversationProfile().Id == lead.Id &&
                  CerebralJson.Element(FreshChatPathAuthoritySnapshot()).GetProperty("state").GetString() == "legacy_profile_authority",
                "Default New preserves the sole local lead and legacy profile authority.");
            await NewExplicit();
            var summary = new ConversationSummary(durableConversationId!, "Saved owner", lead.Id, null,
                "saved-owner-run", WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
            durableConversations[durableConversationId!] = summary;
            Check(TryConversationProfile()?.Id == lead.Id && !FreshChatProfileAdmissible(profiles.Single(x => x.Id == helper.Id)),
                "An existing saved owner takes precedence over explicit draft intent.");
            durableConversations.Remove(durableConversationId!);
            binding = false;
            Check(TryConversationProfile() is null, "An explicit draft does not cross a lost connection binding.");
            binding = true;
            await NewExplicit();
            var exactAcceptedInput = CerebralJson.Element(new { projectId = "plugin-project", workingDirectory = "plugin-relative-root" });
            var exactAcceptedRequest = WorkflowConversationClient.FreezeSubmission(CreateNativeSubmission(
                Input("exact-accepted"), helper.Id, fixtureRoot, "main-owned-project", 25, null,
                definitionVersion: "2.0.0", definitionInput: exactAcceptedInput,
                definitionId: "fixture:develop", exactSchema: exactSchema));
            sdkBehavior = "accepted";
            await isolated.SubmitConversationAsync(exactAcceptedRequest, token);
            var savedExactRun = accepted ?? throw new InvalidOperationException("The exact fixture run was not accepted.");
            Check(savedExactRun.Submission.ProjectId == exactAcceptedRequest.OwnerProjectId &&
                savedExactRun.Submission.LocalGeneration == exactAcceptedRequest.LocalGeneration &&
                JsonElement.DeepEquals(savedExactRun.Submission.Input, exactAcceptedRequest.Input) &&
                JsonElement.DeepEquals(CerebralJson.Element(savedExactRun.Submission.AcceptedContent),
                    CerebralJson.Element(exactAcceptedRequest.AcceptedContent)),
                "fake accepted run faithfully returns host project, typed input, accepted content and local-generation coordinates");
            var fixtureGeneration = new WorkflowLocalGenerationChoice(WorkflowLocalGenerationChoice.CurrentSchemaVersion,
                WorkflowLocalGenerationChoice.NativeTextRoute, 4096, 512);
            var projectedGeneration = Run(CerebralJson.Element(new
            {
                requestId = "generation-echo", conversationId = durableConversationId,
                profileId = helper.Id, definitionId = "fixture:develop", definitionVersion = "2.0.0",
                userText = "Typed fixture", assignmentText = (string?)null,
                acceptedContent = exactAcceptedRequest.AcceptedContent, input = exactAcceptedRequest.Input,
                stepLimit = exactAcceptedRequest.StepLimit, ownerProjectId = exactAcceptedRequest.OwnerProjectId,
                localGeneration = fixtureGeneration
            }));
            Check(projectedGeneration.Submission.ProjectId == exactAcceptedRequest.OwnerProjectId &&
                projectedGeneration.Submission.LocalGeneration == fixtureGeneration &&
                JsonElement.DeepEquals(projectedGeneration.Submission.Input, exactAcceptedRequest.Input),
                "fake run retains an explicit typed local-generation choice without changing exact workflow input");
            var foreignProjectCalls = 0;
            var foreignProjectClient = new WorkflowConversationClient(durableConversationId!, (request, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (request.Operation != "jobs.workflow.submit")
                    throw new InvalidOperationException("The foreign-project fixture permits only one submission reply.");
                foreignProjectCalls++;
                var echoed = Run(request.Arguments);
                return Task.FromResult(CommandResult.Ok(echoed with
                { Submission = echoed.Submission with { ProjectId = "foreign-project" } }));
            });
            var foreignProjectRefused = false;
            try { await foreignProjectClient.SubmitAsync(exactAcceptedRequest, token); }
            catch (InvalidDataException error) when (error.Message.Contains("exact submitted request", StringComparison.Ordinal))
            { foreignProjectRefused = true; }
            Check(foreignProjectRefused,
                "the real client refuses a fake accepted run with a different host project coordinate");
            Check(foreignProjectCalls == 1 && foreignProjectClient.Snapshot.MutationUncertain &&
                foreignProjectClient.Snapshot.RunId == exactAcceptedRequest.RequestId,
                "a mismatched fake reply retains its exact request ID as uncertain without accepting the foreign run");
            var exactPriorInput = AcceptedExactFollowupInput(savedExactRun, durableConversationId!,
                exactAcceptedRequest.RequestId, helper.Id, "fixture:develop", "2.0.0", exactSchema);
            var nextExact = CreateNativeSubmission(Input("exact-followup", "Correct the remaining paragraph"),
                helper.Id, fixtureRoot, "main-owned-project", 25, null,
                definitionVersion: "2.0.0", definitionInput: exactPriorInput,
                definitionId: "fixture:develop", exactSchema: exactSchema);
            Check(nextExact.RequestId != exactAcceptedRequest.RequestId &&
                nextExact.UserText == "Correct the remaining paragraph" &&
                JsonElement.DeepEquals(nextExact.Input, exactAcceptedRequest.Input) &&
                nextExact.DefinitionId == "fixture:develop" && nextExact.DefinitionVersion == "2.0.0",
                "ordinary exact continuation can copy the accepted input into a fresh request for the same edition");
            RejectExactOwnerMismatch(() => AcceptedExactFollowupInput(savedExactRun, durableConversationId!,
                exactAcceptedRequest.RequestId, lead.Id, "fixture:develop", "2.0.0", exactSchema),
                "a different profile cannot borrow accepted exact input");
            RejectExactOwnerMismatch(() => AcceptedExactFollowupInput(savedExactRun with { Status = WorkflowStatus.Queued }, durableConversationId!,
                exactAcceptedRequest.RequestId, helper.Id, "fixture:develop", "2.0.0", exactSchema),
                "an unfinished predecessor cannot seed a new exact request");
            RejectValue(() => AcceptedExactFollowupInput(savedExactRun, durableConversationId!,
                exactAcceptedRequest.RequestId, helper.Id, "fixture:develop", "2.0.0",
                CerebralJson.Element(new { type = "object", required = new[] { "missing" } })),
                "a changed schema cannot silently reuse accepted exact input");
            var leaseOwner = true;
            using (var followupLease = new MainWorkflowDefinitionInputLease("exact-followup", exactPriorInput,
                () => leaseOwner, exact: true))
            {
                RejectValue(() => followupLease.Consume("other-request"),
                    "another request ID cannot consume the reviewed exact input lease");
                leaseOwner = false;
                RejectValue(() => followupLease.Consume("exact-followup"),
                    "a changed owner cannot consume the reviewed exact input lease");
            }
            knowledgeConnectionVersion = source;
            sdkBehavior = "refused";
            var connectedProperty = typeof(ConnectedWindow).GetProperty("IsConnected", flags)
                ?? throw new InvalidOperationException("Fixture connection flag is unavailable.");
            var previousMetricsClient = systemWorkMetricsClient;
            var previousMetricsInstallation = systemWorkMetricsInstallationId;
            var previousExact = mainExactWorkflowAvailability;
            var previousGeneration = mainChatDiscoveryGeneration;
            var previousChoiceRevision = mainChatWorkflowChoiceRevision;
            connectedProperty.SetValue(this, true);
            systemWorkMetricsClient = originalClient;
            systemWorkMetricsInstallationId = "fixture-installation";
            try
            {
                mainChatDiscoveryGeneration++;
                var exactDescriptor = new WorkflowDefinitionDescriptor("fixture:develop", "2.0.0",
                    "Exact fixture", exactSchema, []);
                mainExactWorkflowAvailability = new(originalClient, ServiceConnection,
                    ServiceConnectionIdentity, systemWorkMetricsInstallationId, knowledgeConnectionVersion,
                    conversationSelectionVersion, durableConversationId!, helper.Id, helper.Revision,
                    helper.Capabilities.ToArray(), mainChatDiscoveryGeneration, exactDescriptor);
                Check(MainExactWorkflowAvailabilityCurrent(), "Accepted exact fixture starts from a current declaration.");
                var acceptedIntent = CaptureMainChatWorkflowIntent()!;
                Check(acceptedIntent.Origin == "accepted_run", "Subsequent exact turn captures accepted-run origin.");
                RequireMainChatWorkflowIntentCurrent(acceptedIntent);
                var capturedExact = mainExactWorkflowAvailability!;
                mainExactWorkflowAvailability = capturedExact with
                {
                    Definition = exactDescriptor with { InputSchema = CerebralJson.Element(new { type = "object" }) }
                };
                RejectValue(() => RequireMainChatWorkflowIntentCurrent(acceptedIntent),
                    "A changed exact declaration after composer await was admitted.");
                mainExactWorkflowAvailability = capturedExact;
                mainChatDiscoveryGeneration++;
                RejectValue(() => RequireMainChatWorkflowIntentCurrent(acceptedIntent),
                    "A changed discovery generation after composer await was admitted.");
                mainChatDiscoveryGeneration--;
                var currentHelper = profiles.Single(x => x.Id == helper.Id);
                currentHelper.Revision++;
                RejectValue(() => RequireMainChatWorkflowIntentCurrent(acceptedIntent),
                    "A changed selected profile revision after composer await was admitted.");
                currentHelper.Revision--;
                currentHelper.Capabilities.Add("fixture.changed");
                RejectValue(() => RequireMainChatWorkflowIntentCurrent(acceptedIntent),
                    "A changed selected profile capability after composer await was admitted.");
                currentHelper.Capabilities.Remove("fixture.changed");
                RequireMainChatWorkflowIntentCurrent(acceptedIntent);
                retainedNativeSubmission = exactAcceptedRequest;
                retainedNativeConversationId = durableConversationId;
                retainedNativeClient = originalClient; retainedNativeConnection = ServiceConnection;
                retainedNativeSource = knowledgeConnectionVersion; retainedNativeIdentity = ServiceConnectionIdentity;
                var retainedIntent = CaptureMainChatWorkflowIntent()!;
                Check(retainedIntent.Origin == "retained_request", "Exact retry captures original retained request.");
                mainChatDiscoveryGeneration++;
                RequireMainChatWorkflowIntentCurrent(retainedIntent);
                ClearRetainedNativeSubmission();
                RequireMainChatWorkflowIntentCurrent(retainedIntent);
                Check(true, "Same-RID retry and accepted-original receipt cleanup keep original authority.");
                mainChatDiscoveryGeneration--;
            }
            finally
            {
                ClearRetainedNativeSubmission();
                mainExactWorkflowAvailability = previousExact;
                mainChatDiscoveryGeneration = previousGeneration;
                mainChatWorkflowChoiceRevision = previousChoiceRevision;
                systemWorkMetricsClient = previousMetricsClient;
                systemWorkMetricsInstallationId = previousMetricsInstallation;
                connectedProperty.SetValue(this, false);
            }
            checks += await CheckFreshChatMarkedPathsAsync(token);
            Check(refusal.Calls == 0, "All fixture transport remained at the injected command/adapter boundaries.");
        }
        finally
        {
            nativePreferenceCommandOverride = originalCommand; nativePreferenceBindingOverride = originalBinding;
            ClearRetainedNativeSubmission(); freshChatPreparation = null; freshChatDraft = null;
            freshChatInitialAccepted = false; freshChatInitialDispatchAttempted = false;
            profiles = originalProfiles; leadProfile = originalLead;
            projectsDirectory = originalDirectory; navigation = originalNavigation; draftProjectId = originalProject;
            knowledgeConnectionVersion = source; conversationPrimarySurface = originalSurface;
            nativeChat = originalWorkspace;
            httpField.SetValue(originalClient, originalHttp);
        }
        return checks;
    }


    private async Task<int> CheckFreshChatMarkedPathsAsync(CancellationToken token)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        void Refuse(ConversationPathSnapshot value)
        {
            try { ConversationPathSnapshot.Read(CerebralJson.Element(value), value.Id); }
            catch (InvalidDataException) { checks++; return; }
            throw new InvalidOperationException("Invalid marked permission projection was admitted.");
        }
        const string id = "marked-editor"; const string root = @"C:\Fixture\Marked";
        const string external = @"C:\Fixture\External";
        var legacy = new ConversationPathSnapshot(id, "helper", null, "bound", root, 0, null, [],
            ConversationPathSnapshot.Effective(root, []));
        var legacyJson = CerebralJson.Element(legacy);
        Check(legacyJson.EnumerateObject().Count() == 9 && ConversationPathSnapshot.Read(legacyJson, id).Initial is null,
            "The unchanged legacy wire stays exactly nine fields without null extension members.");
        Refuse(legacy with { EffectiveGrants = [] });
        var marked = legacy with
        {
            Revision = 1, UpdatedAt = DateTimeOffset.UnixEpoch,
            Grants = [new(external, "read")],
            EffectiveGrants = [new(root + @"\Narrowed", "read")],
            Initial = new("marked-first", "helper", 8, null, root), InitialState = "accepted"
        };
        Check(ConversationPathSnapshot.Read(CerebralJson.Element(marked), id).Initial == marked.Initial,
            "Marked accepted provenance is admitted with an honestly clipped effective scope.");
        var driveRoot = marked with
        {
            Root = @"C:\", Grants = [], EffectiveGrants = [new(@"C:\Fixture\Narrowed", "read")],
            Initial = marked.Initial! with { WorkingDirectory = @"C:\" }
        };
        Check(ConversationPathSnapshot.Read(CerebralJson.Element(driveRoot), id).EffectiveGrants.Count == 1,
            "A canonical drive root contains descendant effective paths without a doubled-separator alias.");
        var changedFolder = marked with { Initial = marked.Initial! with { WorkingDirectory = external } };
        Check(ConversationPathSnapshot.Read(CerebralJson.Element(changedFolder), id).Initial?.WorkingDirectory == external,
            "The effective folder may differ from canonical immutable initial preparation after an explicit owner edit.");
        Check(ConversationPathSnapshot.Read(CerebralJson.Element(changedFolder with
            { RootState = "unbound", Root = null, Grants = [], EffectiveGrants = [] }), id).Root is null,
            "An explicitly cleared effective folder preserves original preparation provenance without inventing a new root.");
        Refuse(marked with { Initial = marked.Initial! with { WorkingDirectory = "relative-original" } });
        Refuse(marked with { InitialState = "prepared" });
        Refuse(marked with { EffectiveGrants = [new(external, "write")] });
        var nested = marked with
        {
            Grants = [new(root + @"\Private", "read")],
            EffectiveGrants = [new(root, "write")]
        };
        Refuse(nested);
        var fragmented = marked with
        {
            EffectiveGrants = Enumerable.Range(0, 40).Select(x => new ConversationPathGrant(root + @"\Part" + x.ToString("D2"), "read")).ToArray()
        };
        Check(ConversationPathSnapshot.Read(CerebralJson.Element(fragmented), id).EffectiveGrants.Count == 40,
            "Existing profile clipping may produce more than 33 canonical entries without raising the owner's 128 bound.");
        var reads = 0; var saves = 0;
        using (var editor = new ConversationPathGrantsEditor(id, marked.ProfileId, null, "bound", root, () => true,
            (_, _) => { reads++; return Task.FromResult(CerebralJson.Element(marked)); },
            (_, revision, rules, _) =>
            {
                saves++;
                Check(revision == 1 && rules.Count == 2, "Marked editor saves the current revision and reviewed external rules.");
                return Task.FromResult(CommandResult.Ok(marked with
                {
                    Revision = 2, UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
                    Grants = rules, EffectiveGrants = []
                }));
            }, token))
        {
            await editor.LoadAsync();
            Check(reads == 1 && editor.AddButton.IsEnabled && editor.EffectiveSummary.Text.Contains("limited by the current profile", StringComparison.Ordinal),
                "The actual editor displays clipped effective authority separately from requested entries.");
            editor.AddDraft(@"C:\Fixture\Additional", "read");
            await editor.SaveAsync();
            Check(saves == 1 && editor.Accepted?.Revision == 2 && editor.Accepted.Initial == marked.Initial &&
                  editor.Accepted.EffectiveGrants.Count == 0,
                "An exact marked save retains immutable initial provenance even when current profile policy clips all access.");
        }
        using (var editor = new ConversationPathGrantsEditor(id, marked.ProfileId, null, "bound", root, () => true,
            (_, _) => Task.FromResult(CerebralJson.Element(marked)),
            (_, _, rules, _) => Task.FromResult(CommandResult.Ok(marked with
            {
                Revision = 2, UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1), Grants = rules,
                Initial = marked.Initial! with { FirstRequestId = "different-original" }
            })), token))
        {
            await editor.LoadAsync();
            editor.AddDraft(@"C:\Fixture\Additional", "read");
            await editor.SaveAsync();
            Check(editor.Accepted is null && !editor.SaveButton.IsEnabled &&
                  editor.Feedback.Text.Contains("unconfirmed", StringComparison.Ordinal),
                "A save receipt that swaps initial lineage is held without adopting it or replaying the mutation.");
        }
        return checks;
    }

    private sealed class FreshDraftRefusingHandler : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<HttpResponseMessage>(new InvalidOperationException("Unexpected fixture transport."));
        }
    }
}
