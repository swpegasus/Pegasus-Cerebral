using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
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
    /// <summary>Deterministic transport + active WPF dispatcher checks; no service, settings, or model calls.</summary>
    public async Task<int> CheckConversationControlsAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run conversation controls checks on the active WPF dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or InvalidDataException or OperationCanceledException)
            { checks++; return; }
            throw new InvalidOperationException(label);
        }

        var clientCalls = new List<CommandRequest>();
        var firstReceipt = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ConversationControlClient("controls", (request, token) =>
        {
            clientCalls.Add(request);
            return clientCalls.Count == 1 ? firstReceipt.Task.WaitAsync(token) :
                Task.FromResult(CommandResult.Ok(new WorkflowSteeringReceipt("steer-1", "run-1", 1, DateTimeOffset.UnixEpoch.AddSeconds(1))));
        });
        var steering = new WorkflowSteeringRequest("steer-1", "run-1", "Original instruction", "Private context");
        var first = client.SteerAsync(steering, deadline.Token);
        Check(client.Snapshot.DispatchPending && client.Snapshot.RequestId == steering.RequestId, "Pending steering must retain its ID before acceptance.");
        await Reject(() => client.SteerAsync(steering, deadline.Token), "Concurrent control calls must not dispatch twice.");
        firstReceipt.SetException(new IOException("Fixture lost receipt"));
        await Reject(async () => await first, "Lost receipt must remain uncertain.");
        Check(client.Snapshot.Uncertain && client.Snapshot.Steering == steering, "Uncertainty retains exact public and private steering input.");
        await Reject(() => client.SteerAsync(steering with { Text = "Changed" }, deadline.Token), "Changed uncertain payload must be rejected.");
        Check(clientCalls.Count == 1, "Neither concurrency nor changed retry may dispatch.");
        var retried = await client.RetryAsync(deadline.Token);
        Check(retried.Steering?.Sequence == 1 && !client.Snapshot.Uncertain &&
            clientCalls.Count == 2 && clientCalls[0].Arguments.GetRawText() == clientCalls[1].Arguments.GetRawText() &&
            clientCalls.All(x => x.ProfileId is null), "Explicit retry must use exact original payload and owner route.");

        var rejectedRetryCount = 0;
        var uncertainRename = new ConversationControlClient("controls", (_, _) =>
        {
            rejectedRetryCount++;
            return rejectedRetryCount switch
            {
                1 => Task.FromException<CommandResult>(new IOException("Receipt lost after dispatch")),
                2 => Task.FromResult(CommandResult.Fail("Owner access temporarily unavailable")),
                _ => Task.FromResult(CommandResult.Ok(new ConversationRenameResult("controls", "Retained", 6)))
            };
        });
        var retainedRename = new ConversationRenameRequest("retained-rename", ConversationControlClient.ChatDefinitionId, "controls", 5, "Retained");
        await Reject(() => uncertainRename.RenameAsync(retainedRename, deadline.Token), "Initial rename receipt loss must be uncertain.");
        await Reject(() => uncertainRename.RetryAsync(deadline.Token), "An explicit retry rejection must be surfaced.");
        Check(uncertainRename.Snapshot.Uncertain && uncertainRename.Snapshot.Rename == retainedRename,
            "Rejection of a retry cannot erase evidence of the initially ambiguous mutation.");
        var recoveredRename = await uncertainRename.RetryAsync(deadline.Token);
        Check(!uncertainRename.Snapshot.Uncertain && recoveredRename.Rename?.TitleRevision == 6 && rejectedRetryCount == 3,
            "Only the matching saved receipt clears a previously ambiguous rename.");

        var invalid = new ConversationControlClient("controls", (_, _) => Task.FromResult(CommandResult.Ok(
            new WorkflowSteeringReceipt("foreign", "run-1", 1, DateTimeOffset.UtcNow))));
        await Reject(() => invalid.SteerAsync(steering, deadline.Token), "Foreign receipt must be rejected.");
        Check(invalid.Snapshot.Uncertain, "Malformed acceptance cannot be treated as a confirmed rejection.");
        var rejected = new ConversationControlClient("controls", (_, _) => Task.FromResult(CommandResult.Fail("Title revision changed.")));
        await Reject(() => rejected.RenameAsync(new("rename", ConversationControlClient.ChatDefinitionId, "controls", 3, "New title"), deadline.Token), "Stale title must reject.");
        Check(!rejected.Snapshot.Uncertain && rejected.Snapshot.RequestId is null, "An explicit stale-title rejection permits a fresh reviewed rename.");
        var cancelledCalls = 0;
        var cancelled = new ConversationControlClient("controls", (_, _) => { cancelledCalls++; return Task.FromResult(CommandResult.Ok()); });
        using (var cancelledToken = new CancellationTokenSource())
        {
            cancelledToken.Cancel();
            await Reject(() => cancelled.SteerAsync(steering, cancelledToken.Token), "Pre-dispatch cancellation must stop before transport.");
        }
        Check(cancelledCalls == 0 && !cancelled.Snapshot.Uncertain, "Pre-dispatch cancellation must not invent uncertainty.");
        await Reject(() => cancelled.RenameAsync(new("rename", "other:workflow", "controls", 0, "Name"), deadline.Token), "Rename cannot target a foreign definition.");
        await Reject(() => cancelled.RenameAsync(new("rename", ConversationControlClient.ChatDefinitionId, "controls", 0, "Bad\nname"), deadline.Token), "Rename cannot contain control characters.");

        await Reject(() => cancelled.RenameAsync(new("rename", ConversationControlClient.ChatDefinitionId, "controls", 0, "Bad\u2028name"), deadline.Token), "Rename cannot contain Unicode line separators.");
        await Reject(() => cancelled.SteerAsync(steering with { Text = new string('x', 8193) }, deadline.Token), "Steering must respect the exact 8192-byte service bound.");
        Check(cancelledCalls == 0, "Invalid control input must be rejected before transport.");

        var originalWorkspace = nativeChat; var originalSession = session; var originalId = durableConversationId;
        var originalTitle = durableConversationTitle; var originalGeneration = pluginGeneration; var originalPrompt = prompt.Text;
        var originalNavigation = navigation; var originalSessions = navigationSessions;
        var originalFactory = conversationControlFactory; var originalEditor = conversationRenameEditor;
        var originalSummary = durableConversations.GetValueOrDefault("control-chat"); var originalPendingSteering = pendingSteering;
        var originalHeading = sessionHeading.Text;
        var uiCalls = new List<CommandRequest>();
        var uiReceipt = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DesktopOperationResult>? uiDispatch = null;
        var renameAttempts = 0; var disposedAfterRenameDispatch = false; var disposedRenameAttempts = 0;
        var ownerActor = new EventActor(EventActorKind.User, "owner", "You");
        var run = new WorkflowRun
        {
            Id = "control-run", Revision = 1, Status = WorkflowStatus.Running,
            Submission = new("control-run", "control-chat", "lead", ConversationControlClient.ChatDefinitionId,
                ConversationControlClient.ChatDefinitionVersion, "Original message", null,
                WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, Caller: ownerActor)
        };
        var messageEvent = new CerebralEvent("control-message", "control-chat", "control-run", 1,
            CerebralEventTypes.Message, ownerActor, [new TextContentPart("Original message")],
            new(ModelInclusion.Required), new(EventSourceKind.User, "owner"), DateTimeOffset.UnixEpoch);
        using var isolated = new WorkflowWorkspace((request, _) => Task.FromResult(request.Operation == "jobs.workflow.events.previous"
            ? CommandResult.Ok(new { events = new[] { messageEvent }, snapshotSequence = 1L, nextBeforeSequence = (long?)null })
            : CommandResult.Ok(run)), () => Array.Empty<AgentProfile>(), (_, _) => { }, embedded: true);
        try
        {
            session = null; durableConversationId = "control-chat"; durableConversationTitle = "Before";
            navigation = new(); navigationSessions = [new() { Id = "control-chat", Title = "Before", ProfileId = "lead" }];
            durableConversations["control-chat"] = new("control-chat", "Before", "lead", null, "control-run", WorkflowStatus.Running, DateTimeOffset.UnixEpoch) { TitleRevision = 3 };
            nativeChat = isolated; await isolated.OpenRunAsync("control-run");
            conversationControlFactory = id => new(id, (request, token) =>
            {
                uiCalls.Add(request);
                if (request.Operation == "conversations.rename")
                {
                    renameAttempts++;
                    if (disposedAfterRenameDispatch)
                    {
                        disposedRenameAttempts++;
                        return disposedRenameAttempts == 1
                            ? Task.FromException<CommandResult>(new ObjectDisposedException("fixture transport after rename dispatch"))
                            : Task.FromResult(CommandResult.Ok(new ConversationRenameResult("control-chat", "Recovered", 5)));
                    }
                    if (renameAttempts == 1) return Task.FromException<CommandResult>(new IOException("Fixture rename receipt lost"));
                    return Task.FromResult(CommandResult.Ok(new ConversationRenameResult("control-chat", "Renamed", 4)));
                }
                if (request.Operation == "conversations.compact")
                {
                    var value = request.Arguments.Deserialize<ConversationCompactionRequest>(CerebralJson.Options)!;
                    return Task.FromResult(CommandResult.Ok(new ConversationCompactionReceipt(value.RequestId,
                        value.ConversationId, value.ExpectedConversationRevision, DateTimeOffset.UtcNow)));
                }
                return uiCalls.Count == 1 ? uiReceipt.Task.WaitAsync(token) :
                    Task.FromResult(CommandResult.Ok(new WorkflowSteeringReceipt("ui-steer", "control-run", 2, DateTimeOffset.UtcNow)));
            });
            prompt.Text = "Original steering";
            var binding = PluginBinding();
            uiDispatch = desktopPluginHost!.SteerAsync(new(binding, "ui-steer", prompt.Text), deadline.Token);
            Check(NativeMutationPending && conversationControl?.Snapshot.Steering?.RunId == "control-run",
                "Plugin steering must enter durable control dispatch, not the legacy job route.");
            NewConversation();
            Check(PluginBinding() == binding && prompt.Text == "Original steering", "New chat must preserve a pending steering request.");
            await Reject(() => SelectSession("another", deadline.Token), "Selection cannot discard a pending steering receipt.");
            await Reject(() => { ConnectionChanged(); return Task.CompletedTask; }, "Changing service cannot discard a pending steering receipt.");
            prompt.Text = "New draft while waiting";
            uiReceipt.SetException(new IOException("Fixture steering receipt lost"));
            var unknown = await uiDispatch;
            Check(unknown.State == DesktopOperationState.Uncertain && ControlUncertain && !ControlPending, "Lost UI steering receipt must remain retained.");
            Check(ConversationActionsMenu().Items.OfType<MenuItem>().Any(x => AutomationProperties.GetAutomationId(x) == "RetryConversationAction"),
                "An uncertain action must expose an explicit retry command.");
            await Reject(() => SelectSession("another", deadline.Token), "Selection cannot discard uncertain steering.");
            await Steer();
            Check(uiCalls.Count == 2 && uiCalls[0].Arguments.GetRawText() == uiCalls[1].Arguments.GetRawText() &&
                prompt.Text == "New draft while waiting" && !ControlUncertain && conversationControl is null,
                "Steer retry must bypass input reinterpretation, preserve a newer draft, and release only after acceptance.");

            var stalePluginRename = await desktopPluginHost.RenameConversationAsync(
                binding with { Generation = "stale-generation" }, "Wrong service", deadline.Token);
            Check(stalePluginRename.State == DesktopOperationState.Rejected && renameAttempts == 0,
                "A stale plugin binding cannot dispatch a durable rename.");
            var pluginRename = await desktopPluginHost.RenameConversationAsync(binding, "Renamed", deadline.Token);
            Check(pluginRename.State == DesktopOperationState.Uncertain && pluginRename.RequestId is not null &&
                uiCalls[^1].Operation == "conversations.rename", "Plugin rename uses the guarded durable command and exposes an unconfirmed receipt.");
            Check(ControlUncertain && conversationControl?.Snapshot.Rename is { ExpectedTitleRevision: 3, Title: "Renamed" },
                "Rename must retain its exact title and CAS revision after uncertainty.");
            var beforeDuplicate = uiCalls.Count;
            var duplicate = await desktopPluginHost.RenameConversationAsync(binding, "Different", deadline.Token);
            Check(duplicate.State == DesktopOperationState.Uncertain && uiCalls.Count == beforeDuplicate &&
                conversationControl?.Snapshot.Rename?.Title == "Renamed", "Plugin retry cannot reinterpret or redispatch a retained uncertain rename.");
            NewConversation();
            Check(PluginBinding() == binding, "Uncertain rename must block navigation.");
            await RetryConversationControlAsync();
            Check(!ControlUncertain && renameAttempts == 2 &&
                uiCalls[^1].Arguments.GetRawText() == uiCalls[^2].Arguments.GetRawText() &&
                durableConversations["control-chat"].TitleRevision == 4 && durableConversationTitle == "Renamed" &&
                navigationSessions.Single().Title == "Renamed" && prompt.Text == "New draft while waiting",
                "Rename retry must update only title projections, keep composer contents, and retain exact payload.");
            ApplyConversationRename(new("control-chat", "Stale title", 3));
            Check(durableConversationTitle == "Renamed", "An older rename receipt cannot overwrite a newer title revision.");

            disposedAfterRenameDispatch = true;
            var disposedRename = await desktopPluginHost.RenameConversationAsync(binding, "Recovered", deadline.Token);
            Check(disposedRename.State == DesktopOperationState.Uncertain && disposedRename.RequestId is not null &&
                ControlUncertain && conversationControl?.Snapshot.Rename is { ExpectedTitleRevision: 4, Title: "Recovered" },
                "A disposed transport after dispatch retains the exact rename as uncertain, never pre-dispatch rejected.");
            var beforeDisposedDuplicate = uiCalls.Count;
            var disposedDuplicate = await desktopPluginHost.RenameConversationAsync(binding, "Different", deadline.Token);
            Check(disposedDuplicate.State == DesktopOperationState.Uncertain && uiCalls.Count == beforeDisposedDuplicate,
                "A second rename cannot replace the retained request after a disposed transport.");
            await RetryConversationControlAsync();
            Check(disposedRenameAttempts == 2 && !ControlUncertain &&
                uiCalls[^1].Arguments.GetRawText() == uiCalls[^2].Arguments.GetRawText() &&
                durableConversations["control-chat"].TitleRevision == 5 && durableConversationTitle == "Recovered",
                "The explicit retry uses the same request and projects only its matching receipt.");

            var beforeModalCalls = uiCalls.Count;
            conversationRenameEditor = _ => { pluginGeneration = Guid.NewGuid().ToString("N"); return "Stale editor"; };
            await Reject(() => RenameDurableConversationAsync("control-chat"), "Modal source changes must reject before rename dispatch.");
            Check(uiCalls.Count == beforeModalCalls && conversationControl is null, "Stale editor binding must not dispatch through its old service.");
            pluginGeneration = binding.Generation;
            run = run with { Status = WorkflowStatus.Completed, Revision = 3 };
            await CompactSession();
            var compactCall = uiCalls.Last();
            Check(compactCall.Operation == "conversations.compact" && compactCall.ProfileId is null &&
                compactCall.Arguments.GetProperty("conversationId").GetString() == "control-chat" &&
                compactCall.Arguments.GetProperty("expectedConversationRevision").GetInt64() == 1,
                "manual compaction is an owner-only one-time service request and never a fake user message");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, conversationControlChecks = checks,
                evidence = "deterministic captured transport and active WPF dispatcher", serviceCommandsExecuted = false, networkUsed = false, windowShown = false }));
            return checks;
        }
        finally
        {
            deadline.Cancel(); uiReceipt.TrySetCanceled();
            if (uiDispatch is not null) { try { await uiDispatch; } catch (Exception) { } }
            conversationControl = null; conversationControlPreparing = false; conversationControlBinding = null;
            conversationControlTransport?.Dispose(); conversationControlTransport = null;
            conversationControlFactory = originalFactory; conversationRenameEditor = originalEditor; pendingSteering = originalPendingSteering;
            nativeChat = originalWorkspace; session = originalSession; durableConversationId = originalId;
            durableConversationTitle = originalTitle; pluginGeneration = originalGeneration; prompt.Text = originalPrompt; sessionHeading.Text = originalHeading;
            navigation = originalNavigation; navigationSessions = originalSessions;
            if (originalSummary is null) durableConversations.Remove("control-chat"); else durableConversations["control-chat"] = originalSummary;
        }
    }
}
