using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Desktop.Tests;

/// <summary>
/// Real connection/client and offscreen workspace paths against a deterministic, same-user pipe
/// fixture. This does not start a Worker, load models, perform inference or establish Source acceptance.
/// </summary>
public static class ConnectedWindowReconnectTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ConversationId = "reconnect-chat";
    private const string RunId = "reconnect-run";
    private const string EffectId = "reconnect-effect";

    public static async Task<int> RunAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF dispatcher is required.");
        dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run reconnect checks from an active WPF dispatcher callback.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        await ReplacementFeedsAsync(Check, deadline.Token);
        await InvalidIdentityAsync(Check, deadline.Token);
        await RejectedSwitchAsync(Check, deadline.Token);
        await SubscriptionFailuresAsync(Check, deadline.Token);
        return checks;
    }

    private static async Task ReplacementFeedsAsync(Action<bool, string> check, CancellationToken token)
    {
        await using var first = new PipeFixture();
        await using var replacement = new PipeFixture();
        using var window = new FixtureWindow();
        await window.ConnectAsync(first, "first", watch: true).WaitAsync(token);
        check(window.ServiceConnected && window.ResetCalls == 1 && window.HydrationCalls == 1 &&
            first.Count("fixture.hydrate") == 1, "First verified connection must reset once and hydrate once.");
        check(first.Trace.SequenceEqual(new[] { "health", "appearance", "reset", "hydrate", "hydrate-read" }),
            "Host identity and appearance must precede reset, which must precede the one initial hydration.");
        using var source = Offscreen(window.Workspace);
        await FlushAsync(window.Workspace, token);
        StopWorkspaceTimers(window.Workspace);
        check(window.Workspace.IsLoaded && !window.IsVisible, "The real workspace must be loaded offscreen while its connection window remains hidden.");
        var firstStream = await first.SubscriptionAsync(0, token);
        await firstStream.SendAsync(Preview(100, "Original public draft", 100), token);
        await firstStream.SendAsync(Metric(101, 40, 100), token);
        await WaitUntilAsync(() => window.Workspace.GenerationPreviewSnapshot.EventSequence == 100 &&
            window.Workspace.GenerationRateSnapshot.OutputTokensPerSecond == 40, token);
        check(window.Workspace.GenerationPreviewSnapshot.Visible && window.Workspace.GenerationRateSnapshot.Visible,
            "The original actual pipe feed must reach the real preview and per-chat rate projection.");
        await firstStream.SendAsync(Preview(1, "Rejected lower sequence", 200), token);
        await firstStream.SendAsync(Metric(2, 999, 200), token);
        await WaitUntilAsync(() => window.ObservedEvents >= 4, token);
        check(window.Workspace.GenerationPreviewSnapshot.PublicText == "Original public draft" &&
            window.Workspace.GenerationRateSnapshot.OutputTokensPerSecond == 40,
            "Lower sequences must remain fenced while the verified client has not been replaced.");

        var retiredClient = window.RawClient;
        var retiredToken = window.StreamToken;
        var feed = WorkspaceField<long>(window.Workspace, "generationPreviewFeedGeneration");
        var observed = window.ObservedEvents;
        // Keep the dispatcher occupied until a real old-stream frame has been written. Its watcher
        // cannot publish before ConnectCoreAsync cancels that stream and replaces the client.
        using (var written = new ManualResetEventSlim())
        {
            var delivery = Task.Run(async () =>
            {
                try { await firstStream.SendAsync(Preview(1000, "Original public draft retired callback", 1000), token).ConfigureAwait(false); }
                finally { written.Set(); }
            }, token);
            check(written.Wait(TimeSpan.FromSeconds(3), token), "An old-stream frame must be written without pumping its UI callback.");
            var reconnect = window.ConnectAsync(first, "same-endpoint-replacement", watch: true);
            await reconnect.WaitAsync(token);
            await delivery.WaitAsync(token);
        }
        await FlushAsync(window.Workspace, token);
        check(!ReferenceEquals(window.RawClient, retiredClient) && retiredToken.IsCancellationRequested &&
            window.ObservedEvents == observed, "A queued retired watcher must not publish after client replacement.");
        check(window.ResetCalls == 2 && window.HydrationCalls == 2 && first.Count("fixture.hydrate") == 2 &&
            WorkspaceField<long>(window.Workspace, "generationPreviewFeedGeneration") == feed + 1,
            "Replacement at the same endpoint must reset the process-local feed once and hydrate once.");
        check(!window.Workspace.GenerationPreviewSnapshot.Visible && !window.Workspace.GenerationRateSnapshot.Visible,
            "A verified replacement must clear the retired public draft and rate sample before new events.");
        var secondStream = await first.SubscriptionAsync(1, token);
        await secondStream.SendAsync(Preview(1, "Replacement public draft", 1), token);
        await secondStream.SendAsync(Metric(2, 12, 1), token);
        await WaitUntilAsync(() => window.Workspace.GenerationPreviewSnapshot.EventSequence == 1 &&
            window.Workspace.GenerationRateSnapshot.OutputTokensPerSecond == 12, token);
        check(window.Workspace.GenerationPreviewSnapshot.PublicText == "Replacement public draft",
            "A verified same-endpoint client replacement must admit a lower process-local preview/rate sequence.");

        var status = window.ServiceStatusText;
        using (var retiredWatcher = new CancellationTokenSource())
            await InvokeAsync(window, "WatchAsync", retiredClient, retiredWatcher.Token).WaitAsync(token);
        check(window.ServiceConnected && window.ServiceStatusText == status,
            "A retired client's watcher failure must not mark the replacement client offline.");
        var oldConnection = window.ConnectionIdentity;
        await window.ConnectAsync(replacement, "new-instance", watch: true).WaitAsync(token);
        check(window.ConnectionIdentity != oldConnection && window.ConnectionIdentity == replacement.Endpoint.InstanceId &&
            window.ResetCalls == 3 && window.HydrationCalls == 3 && replacement.Count("fixture.hydrate") == 1,
            "A verified new instance must replace identity, reset once and hydrate once.");
        check(replacement.Trace.SequenceEqual(new[] { "health", "appearance", "reset", "hydrate", "hydrate-read" }),
            "A verified new-instance reconnect must use the same ordered single-hydration contract.");
        var newStream = await replacement.SubscriptionAsync(0, token);
        await newStream.SendAsync(Preview(1, "New instance public draft", 1), token);
        await newStream.SendAsync(Metric(2, 21, 1), token);
        await WaitUntilAsync(() => window.Workspace.GenerationPreviewSnapshot.PublicText == "New instance public draft" &&
            window.Workspace.GenerationRateSnapshot.OutputTokensPerSecond == 21, token);
        check(window.Workspace.GenerationPreviewSnapshot.EventSequence == 1 &&
            window.Workspace.Snapshot?.RunId == RunId, "New-instance low sequences must be admitted without replacing the selected saved run.");
        check(window.Reports.Count == 0 && first.Errors.IsEmpty && replacement.Errors.IsEmpty,
            "The accepted connection fixture must complete without hidden workspace or server failures.");
        source.RootVisual = null;
    }

    private static async Task InvalidIdentityAsync(Action<bool, string> check, CancellationToken token)
    {
        foreach (var failure in new[] { "hello-instance", "health-product", "health-protocol", "health-data-root" })
        {
            await using var fixture = new PipeFixture(failure);
            using var window = new FixtureWindow();
            var error = await CaptureFailureAsync(() => window.ConnectAsync(fixture, failure, watch: true), token);
            check(error is InvalidOperationException && !window.ServiceConnected &&
                window.ServiceStatusText.StartsWith("Service unavailable:", StringComparison.Ordinal),
                "Rejected " + failure + " identity must be diagnosed as an unavailable service.");
            check(window.ResetCalls == 0 && window.HydrationCalls == 0 && fixture.Count("interface.theme.get") == 0 &&
                fixture.Count("fixture.hydrate") == 0 && fixture.SubscriptionCount == 0,
                "Rejected " + failure + " identity must not reset, hydrate appearance/application data or subscribe.");
            check(failure != "hello-instance" || fixture.Commands.IsEmpty,
                "A mismatched hello instance must be rejected before any command frame.");
            check(fixture.Errors.IsEmpty, "Identity rejection must not hide an unrelated fixture failure.");
        }
    }

    private static async Task RejectedSwitchAsync(Action<bool, string> check, CancellationToken token)
    {
        await using var original = new PipeFixture();
        await using var rejected = new PipeFixture();
        using var window = new FixtureWindow(hydrateWorkspace: false);
        await window.ConnectAsync(original, "original-pending-owner", watch: true).WaitAsync(token);
        _ = await original.SubscriptionAsync(0, token);
        window.Workspace.BeginConversation(ConversationId);
        var submission = new WorkflowConversationSubmission("reconnect-uncertain", "lead", "fixture.reconnect",
            "1.0.0", "Deterministic public fixture request", WorkflowContract.EmptyObject);
        original.DropSubmissionReply = true;
        var dispatched = await CaptureFailureAsync(() => window.Workspace.SubmitConversationAsync(submission, token), token);
        check(dispatched is LocalWorkerCommandUncertainException &&
            window.Workspace.Snapshot is { RunId: "reconnect-uncertain", MutationUncertain: true },
            "A fixture disconnect after actual local command dispatch must retain adapter uncertainty and exact request ID.");
        var client = window.RawClient;
        var stream = window.RawStream;
        var streamToken = window.StreamToken;
        var identity = window.ConnectionIdentity;
        var revision = window.AppliedRevision;
        var pending = WorkspaceField<WorkflowConversationSubmission>(window.Workspace, "pendingSubmission");
        var resets = window.ResetCalls;
        var hydrations = window.HydrationCalls;
        var error = await CaptureFailureAsync(() => window.ConnectAsync(rejected, "must-not-apply", watch: true), token);
        check(error is InvalidOperationException && window.ServiceConnected &&
            window.ServiceStatusText.StartsWith("Connection change deferred:", StringComparison.Ordinal),
            "An unknown mutation must defer switching while preserving the usable original connection.");
        check(ReferenceEquals(client, window.RawClient) && ReferenceEquals(stream, window.RawStream) &&
            !streamToken.IsCancellationRequested && window.AppliedRevision == revision && window.ConnectionIdentity == identity,
            "Rejected switching must preserve the original client, stream, applied revision and endpoint identity.");
        check(ReferenceEquals(pending, WorkspaceField<WorkflowConversationSubmission>(window.Workspace, "pendingSubmission")) &&
            window.Workspace.Snapshot is { RunId: "reconnect-uncertain", MutationUncertain: true } &&
            window.ResetCalls == resets && window.HydrationCalls == hydrations && rejected.HelloCount == 0,
            "Rejected switching must keep the exact pending body and uncertainty without reset, hydration or probing the candidate.");
        var read = await window.ReadFixtureAsync(token);
        check(read.Success && original.Count("fixture.hydrate") == 2,
            "Read-only commands must still use the original guarded client after switching is rejected.");
        var feed = WorkspaceField<long>(window.Workspace, "generationPreviewFeedGeneration");
        window.HydrateWorkspace = true;
        await window.ConnectAsync(original, "same-binding-unknown-reconnect", watch: true).WaitAsync(token);
        _ = await original.SubscriptionAsync(1, token);
        check(window.ServiceConnected && !ReferenceEquals(client, window.RawClient) && streamToken.IsCancellationRequested &&
            window.ConnectionIdentity == identity && window.AppliedRevision == "same-binding-unknown-reconnect",
            "Same-binding reconnect must verify a replacement client without abandoning its original authenticated identity.");
        check(window.ResetCalls == resets + 1 && window.HydrationCalls == hydrations + 1 &&
            original.Count("fixture.hydrate") == 3 &&
            WorkspaceField<long>(window.Workspace, "generationPreviewFeedGeneration") == feed + 1,
            "Same-binding reconnect with an unconfirmed request must reset feed fences once and hydrate once.");
        check(ReferenceEquals(pending, WorkspaceField<WorkflowConversationSubmission>(window.Workspace, "pendingSubmission")) &&
            window.Workspace.Snapshot is { RunId: "reconnect-uncertain", MutationUncertain: true } &&
            pending.UserText == submission.UserText && pending.ProfileId == submission.ProfileId &&
            pending.Input.GetRawText() == submission.Input.GetRawText(),
            "Reset-only same-binding reconnect and real workspace refresh must preserve the exact unresolved submitted body and uncertainty.");
        check(original.UnconfirmedRunReads == 1 && original.Count("jobs.workflow.get") == 1 &&
            original.Count("jobs.workflow.submit") == 1 && window.Reports.SequenceEqual(new[] { PipeFixture.UnconfirmedOutcome }),
            "Workspace reconnect hydration must query the exact unconfirmed run once, report its unresolved outcome and never resend submission.");
        check(original.Errors.IsEmpty && rejected.Errors.IsEmpty, "Unknown-request preservation must not hide fixture failures.");
    }

    private static async Task SubscriptionFailuresAsync(Action<bool, string> check, CancellationToken token)
    {
        foreach (var failure in new[] { "subscription-refused", "subscription-disconnected" })
        {
            await using var fixture = new PipeFixture(failure);
            using (var direct = new CerebralClient(fixture.Endpoint))
            {
                var error = await CaptureFailureAsync(async () =>
                {
                    await foreach (var _ in direct.EventsAsync(token))
                        throw new InvalidOperationException("A failed fixture subscription produced a success event.");
                }, token);
                check(error is IOException && error.Message.Contains(failure == "subscription-refused" ?
                    "rejected the event subscription" : "event stream disconnected", StringComparison.Ordinal),
                    "The actual local event client must diagnose " + failure + " without a successful event.");
            }
            using var window = new FixtureWindow(hydrateWorkspace: false);
            await window.ConnectAsync(fixture, failure, watch: true).WaitAsync(token);
            await WaitUntilAsync(() => !window.ServiceConnected, token);
            check(window.ResetCalls == 1 && window.HydrationCalls == 1 &&
                window.ServiceStatusText.Contains("Service connection unavailable", StringComparison.Ordinal) &&
                window.ObservedEvents == 0 && !window.Workspace.GenerationPreviewSnapshot.Visible &&
                !window.Workspace.GenerationRateSnapshot.Visible && fixture.Commands.All(x => !x.StartsWith("inference.", StringComparison.Ordinal)),
                "Failed event readiness must end offline with a recovery diagnostic and no fabricated generation success.");
            check(fixture.Errors.IsEmpty, "The expected subscription failure must remain separate from fixture implementation failure.");
        }
    }

    private static HwndSource Offscreen(WorkflowWorkspace workspace) => new(new HwndSourceParameters("Cerebral reconnect fixture")
    {
        Width = 680, Height = 480, WindowStyle = unchecked((int)0x80000000),
        PositionX = -32000, PositionY = -32000
    }) { RootVisual = workspace };

    private static async Task FlushAsync(WorkflowWorkspace workspace, CancellationToken token)
    {
        await workspace.Dispatcher.InvokeAsync(() =>
        {
            workspace.Measure(new Size(680, 480));
            workspace.Arrange(new Rect(0, 0, 680, 480));
            workspace.UpdateLayout();
        }, DispatcherPriority.Loaded, token);
        await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, token);
    }

    private static void StopWorkspaceTimers(WorkflowWorkspace workspace)
    {
        WorkspaceField<DispatcherTimer>(workspace, "refreshTimer").Stop();
        WorkspaceField<DispatcherTimer>(workspace, "activityClockTimer").Stop();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(10, token);
    }

    private static async Task<Exception> CaptureFailureAsync(Func<Task> action, CancellationToken token)
    {
        try { await action().WaitAsync(token); }
        catch (Exception error) when (!token.IsCancellationRequested) { return error; }
        throw new InvalidOperationException("The fixture operation unexpectedly succeeded.");
    }

    private static Task InvokeAsync(ConnectedWindow window, string method, params object[] arguments) =>
        (Task)(typeof(ConnectedWindow).GetMethod(method, PrivateInstance)?.Invoke(window, arguments)
            ?? throw new InvalidOperationException("Missing connection fixture method " + method));

    private static T WindowField<T>(ConnectedWindow window, string name) =>
        (T)(typeof(ConnectedWindow).GetField(name, PrivateInstance)?.GetValue(window)
            ?? throw new InvalidOperationException("Missing connection fixture field " + name));

    private static T WorkspaceField<T>(WorkflowWorkspace workspace, string name) =>
        (T)(typeof(WorkflowWorkspace).GetField(name, PrivateInstance)?.GetValue(workspace)
            ?? throw new InvalidOperationException("Missing workspace fixture field " + name));

    private static ServiceEvent Preview(long sequence, string text, long elapsed) => new(sequence,
        "workflow.generation.preview", RunId, CerebralJson.Element(new
        {
            schema = "cerebral.generation-preview/1", conversationId = ConversationId, requestId = RunId,
            runId = RunId, effectId = EffectId, agentId = "lead", phase = "draft", kind = "final",
            text, operation = (string?)null, truncated = false, elapsedMilliseconds = elapsed, finish = (string?)null
        }), DateTimeOffset.UtcNow);

    private static ServiceEvent Metric(long sequence, double rate, long elapsed) => new(sequence,
        "workflow.generation.metrics", RunId, CerebralJson.Element(new
        {
            schema = "cerebral.generation-metrics/1", laneId = "reconnect-lane", instanceId = "fixture-instance",
            modelId = "fixture", adapterId = "fixture.local",
            binding = new { conversationId = ConversationId, requestId = RunId, runId = RunId, effectId = EffectId, agentId = "lead", workerId = "local" },
            state = "active", phase = "output", inputTokens = 10, outputTokens = 2,
            inputTokensPerSecond = (double?)null, outputTokensPerSecond = rate,
            elapsedMilliseconds = elapsed, at = DateTimeOffset.UtcNow
        }), DateTimeOffset.UtcNow);

    private sealed class FixtureWindow : ConnectedWindow, IDisposable
    {
        public bool HydrateWorkspace { get; set; }
        private PipeFixture? fixture;
        public WorkflowWorkspace Workspace { get; }
        public List<string> Reports { get; } = [];
        public int ResetCalls { get; private set; }
        public int HydrationCalls { get; private set; }
        public int ObservedEvents { get; private set; }
        public CerebralClient RawClient => WindowField<CerebralClient>(this, "client");
        public CancellationTokenSource RawStream => WindowField<CancellationTokenSource>(this, "stream");
        public CancellationToken StreamToken => RawStream.Token;
        public string AppliedRevision => WindowField<string>(this, "appliedRevision");
        public string ConnectionIdentity => ServiceConnectionIdentity;

        public FixtureWindow(bool hydrateWorkspace = true) : base("Orchestrator", "Reconnect fixture", persistWindowPreferences: false)
        {
            HydrateWorkspace = hydrateWorkspace;
            EnterInspectionMode();
            Workspace = new((request, token) => Client.CommandAsync(request.Operation, request.Arguments, request.ProfileId, token),
                () => [], (message, error) => { if (error) Reports.Add(message); }, embedded: true);
        }

        public Task ConnectAsync(PipeFixture candidate, string revision, bool watch)
        {
            fixture = candidate;
            return InvokeAsync(this, "ConnectCoreAsync", new ConnectionSnapshot(candidate.Preferences, revision, true, null), watch);
        }

        public Task<CommandResult> ReadFixtureAsync(CancellationToken token) =>
            Client.CommandAsync("fixture.hydrate", cancellationToken: token);

        protected override void ValidateConnectionChange()
        {
            if (Workspace.MutationPending || Workspace.Snapshot?.MutationUncertain == true)
                throw new InvalidOperationException("Refresh or retry the retained unconfirmed request before changing its connection.");
        }

        protected override Task RefreshAfterReconnectAsync()
        {
            ResetCalls++;
            fixture!.Trace.Enqueue("reset");
            Workspace.ResetGenerationPreviewFeed();
            return Task.CompletedTask;
        }

        protected override async Task ConnectedAsync(HostInfo info)
        {
            HydrationCalls++;
            fixture!.Trace.Enqueue("hydrate");
            await Client.GetAsync<object>("fixture.hydrate", cancellationToken: Lifetime.Token);
            if (!HydrateWorkspace) return;
            if (Workspace.Snapshot?.RunId is null)
            {
                Workspace.BeginConversation(ConversationId);
                await Workspace.OpenRunSnapshotAsync(RunId, Lifetime.Token);
            }
            else await Workspace.RefreshFromConnectionAsync();
        }

        protected override Task OnEventAsync(ServiceEvent item)
        {
            ObservedEvents++;
            Workspace.Notify(item);
            return Task.CompletedTask;
        }

        protected override Task<object> InspectLoadedStateAsync(string? sessionId) => Task.FromResult<object>(new { fixture = true });

        public void Dispose()
        {
            Lifetime.Cancel();
            typeof(ConnectedWindow).GetMethod("StopStream", PrivateInstance)!.Invoke(this, null);
            RawClient.Dispose();
            Workspace.Dispose();
            Close();
            Lifetime.Dispose();
        }
    }

    private sealed class PipeFixture : IAsyncDisposable
    {
        public const string UnconfirmedOutcome = "Fixture request outcome remains unconfirmed; no accepted run is supplied.";
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentBag<NamedPipeServerStream> pipes = [];
        private readonly ConcurrentBag<Task> handlers = [];
        private readonly List<EventSession> subscriptions = [];
        private readonly Task accepting;
        private readonly string? failure;
        public LocalWorkerEndpoint Endpoint { get; } = new("cerebral-reconnect-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "CerebralReconnectFixture", Guid.NewGuid().ToString("N"));
        public ConcurrentQueue<string> Trace { get; } = [];
        public ConcurrentQueue<string> Commands { get; } = [];
        public ConcurrentQueue<Exception> Errors { get; } = [];
        private int helloCount;
        private int unconfirmedRunReads;
        public int HelloCount => Volatile.Read(ref helloCount);
        public int UnconfirmedRunReads => Volatile.Read(ref unconfirmedRunReads);
        public int SubscriptionCount { get { lock (subscriptions) return subscriptions.Count; } }
        public bool DropSubmissionReply { get; set; }
        public ConnectionPreferences Preferences => ConnectionPreferences.ForLocalPipe(Endpoint, DataDirectory, autoStart: false);
        public int Count(string operation) => Commands.Count(x => x == operation);

        public PipeFixture(string? failure = null)
        {
            this.failure = failure;
            accepting = Task.Run(AcceptAsync);
        }

        public async Task<EventSession> SubscriptionAsync(int index, CancellationToken token)
        {
            while (true)
            {
                lock (subscriptions) if (subscriptions.Count > index) return subscriptions[index];
                await Task.Delay(10, token);
            }
        }

        private async Task AcceptAsync()
        {
            try
            {
                for (var accepted = 0; accepted < 64; accepted++)
                {
                    var pipe = new NamedPipeServerStream(Endpoint.PipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    pipes.Add(pipe);
                    try { await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false); }
                    catch { pipe.Dispose(); throw; }
                    handlers.Add(HandleAsync(pipe));
                }
                Errors.Enqueue(new InvalidOperationException("The bounded reconnect fixture connection limit was exceeded."));
            }
            catch (Exception error) when (lifetime.IsCancellationRequested &&
                error is OperationCanceledException or ObjectDisposedException or IOException) { }
            catch (Exception error) { Errors.Enqueue(error); }
        }

        private async Task HandleAsync(NamedPipeServerStream pipe)
        {
            try
            {
                using (pipe)
                {
                    var hello = await LocalWorkerProtocol.ReadAsync<LocalWorkerHello>(pipe, lifetime.Token).ConfigureAwait(false);
                    LocalWorkerProtocol.ValidateHello(hello, Endpoint);
                    Interlocked.Increment(ref helloCount);
                    var response = failure == "hello-instance" ?
                        new LocalWorkerHello(LocalWorkerProtocol.Product, LocalWorkerProtocol.Version, Guid.NewGuid().ToString("N")) :
                        LocalWorkerProtocol.CreateHello(Endpoint);
                    await LocalWorkerProtocol.WriteAsync(pipe, response, lifetime.Token).ConfigureAwait(false);
                    if (failure == "hello-instance") return;
                    var request = await LocalWorkerProtocol.ReadAsync<LocalWorkerRequest>(pipe, lifetime.Token).ConfigureAwait(false);
                    if (request.Kind == "events")
                    {
                        var session = new EventSession();
                        lock (subscriptions) subscriptions.Add(session);
                        if (failure == "subscription-refused")
                        {
                            await LocalWorkerProtocol.WriteAsync(pipe, CommandResult.Fail("Fixture event admission refused."), lifetime.Token).ConfigureAwait(false);
                            return;
                        }
                        await LocalWorkerProtocol.WriteAsync(pipe, CommandResult.Ok(), lifetime.Token).ConfigureAwait(false);
                        if (failure == "subscription-disconnected") return;
                        await session.RunAsync(pipe, lifetime.Token).ConfigureAwait(false);
                        return;
                    }
                    var operation = request.Command!.Operation;
                    Commands.Enqueue(operation);
                    if (operation == "jobs.workflow.submit" && DropSubmissionReply) return;
                    var result = Reply(request.Command);
                    await LocalWorkerProtocol.WriteAsync(pipe, result, lifetime.Token).ConfigureAwait(false);
                }
            }
            // Normal replacement cancels/disposes the peer. Unexpected request/fixture logic errors
            // remain observable; only expected pipe closure and task-owned cancellation are ignored.
            catch (Exception error) when (error is IOException or ObjectDisposedException ||
                lifetime.IsCancellationRequested && error is OperationCanceledException) { }
            catch (Exception error) { Errors.Enqueue(error); }
        }

        private CommandResult Reply(CommandRequest request)
        {
            var operation = request.Operation;
            var now = DateTimeOffset.UtcNow;
            var events = new[]
            {
                new CerebralEvent("reconnect-message", ConversationId, RunId, 1, CerebralEventTypes.Message,
                    new(EventActorKind.User), [new TextContentPart("Public reconnect fixture request")],
                    new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), now),
                new CerebralEvent("reconnect-call", ConversationId, RunId, 2, CerebralEventTypes.ToolCall,
                    new(EventActorKind.Agent, "lead"), [], new(ModelInclusion.Never, HumanVisibility.Activity),
                    new(EventSourceKind.Agent), now, CerebralJson.Element(new ToolCallData("reconnect-call-id",
                        "cerebral.host:generate", WorkflowContract.EmptyObject)))
            };
            switch (operation)
            {
                case "health":
                    Trace.Enqueue("health");
                    return CommandResult.Ok(new HostInfo(failure == "health-product" ? "Foreign product" : LocalWorkerProtocol.Product,
                        "fixture", failure == "health-protocol" ? 999 : LocalWorkerProtocol.Version,
                        "Reconnect fixture", "fixture", "Windows", true,
                        failure == "health-data-root" ? DataDirectory + "-foreign" : DataDirectory, LocalRun: true));
                case "interface.theme.get":
                    Trace.Enqueue("appearance");
                    return CommandResult.Fail("Unknown command: interface.theme.get");
                case "fixture.hydrate":
                    Trace.Enqueue("hydrate-read");
                    return CommandResult.Ok(new { fixture = true });
                case "jobs.workflow.get":
                    var requestedId = request.Arguments.GetProperty("id").GetString();
                    if (requestedId == "reconnect-uncertain")
                    {
                        Interlocked.Increment(ref unconfirmedRunReads);
                        // The fixture deliberately dropped the dispatched submission response.
                        // A read cannot manufacture an accepted run or clear client uncertainty.
                        return CommandResult.Fail(UnconfirmedOutcome);
                    }
                    if (requestedId != RunId)
                        throw new InvalidOperationException("Unexpected fixture run identity " + requestedId);
                    return CommandResult.Ok(new WorkflowRun
                    {
                        Id = RunId, Revision = 1, Status = WorkflowStatus.Queued,
                        Submission = new(RunId, ConversationId, "lead", "fixture.reconnect", "1.0.0",
                            "Public reconnect fixture request", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
                        CreatedAt = now, UpdatedAt = now
                    });
                case "jobs.workflow.events.previous":
                    return CommandResult.Ok(new { events, snapshotSequence = 2L, nextBeforeSequence = (long?)null });
                case "jobs.workflow.events":
                    return CommandResult.Ok(new { events = new[] { events[0] }, snapshotSequence = 2L, nextAfterSequence = (long?)null });
                default: throw new InvalidOperationException("Unexpected reconnect fixture operation " + operation);
            }
        }

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            foreach (var pipe in pipes) pipe.Dispose();
            await accepting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            lifetime.Dispose();
        }
    }

    private sealed class EventSession
    {
        private sealed record PendingEvent(ServiceEvent Item, TaskCompletionSource Written);
        private readonly Channel<PendingEvent> events = Channel.CreateBounded<PendingEvent>(
            new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        public async Task SendAsync(ServiceEvent item, CancellationToken token)
        {
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await events.Writer.WriteAsync(new(item, written), token).ConfigureAwait(false);
            await written.Task.WaitAsync(token).ConfigureAwait(false);
        }

        public async Task RunAsync(Stream pipe, CancellationToken token)
        {
            await foreach (var pending in events.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await LocalWorkerProtocol.WriteAsync(pipe, pending.Item, token).ConfigureAwait(false);
                    pending.Written.TrySetResult();
                }
                catch (Exception error) { pending.Written.TrySetException(error); throw; }
            }
        }
    }
}
