using System.IO;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;
using Cerebral.ProgrammingPlugin;

public interface IProgrammingPanelTestHost : IDesktopPluginHost, IDesktopServiceHost, IDesktopNativeWorkflowHost, IDesktopWorkspaceHost { }

public class ProgrammingPanelTestHostProxy : DispatchProxy
{
    internal readonly ConcurrentQueue<string> Reports = new();
    internal readonly TaskCompletionSource<DesktopServiceCommandResult> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int ListCalls, SubmitCalls;
    internal bool FailList;
    internal bool Prepared, NavigationAvailable = true, ForeignSavedRun, ForeignSavedRequest;
    internal int ClearCalls, ReadCalls, NavigationCalls;
    internal string? SelectedConversation;
    internal DesktopWorkflowSubmission? Submitted;
    internal Func<DesktopWorkflowSubmission, CancellationToken, Task<DesktopOperationResult>>? Submit;
    internal Func<CancellationToken, Task<DesktopOperationResult>>? Clear;
    internal DesktopOperationState NavigationState = DesktopOperationState.Completed;
    internal static readonly DesktopConversationBinding Binding = new("fixture-generation", "fixture-session");
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        args ??= [];
        switch (method?.Name)
        {
            case "GetDraft": return new DesktopDraftSnapshot(new("fixture-generation", "fixture-session"), 1, "Repair this bounded fixture.");
            case "GetConversation": return new DesktopConversationSnapshot(Binding, "Fixture", "fixture-project", "C:\\ProgrammingPanelFixture", false);
            case "get_WorkspaceAvailable": return NavigationAvailable;
            case "ReportStatus": Reports.Enqueue((string)args[0]!); return null;
            case "ExecuteServiceCommandAsync":
                var token = (CancellationToken)args[2]!; token.ThrowIfCancellationRequested();
                if ((string)args[0]! == "jobs.workflow.get")
                {
                    Interlocked.Increment(ref ReadCalls);
                    return Task.FromResult(new DesktopServiceCommandResult(true, JsonSerializer.SerializeToElement(new
                    {
                        id = ForeignSavedRun ? "foreign-run" : "fixture-run",
                        submission = new { requestId = ForeignSavedRequest ? "foreign-request" : Submitted!.RequestId, conversationId = "accepted-conversation", definitionId = "pegasus.programming:develop", definitionVersion = "1.3.21" }
                    })));
                }
                if ((string)args[0]! != "programming.list") throw new InvalidOperationException("Unexpected fixture command.");
                Interlocked.Increment(ref ListCalls);
                if (Prepared) return Task.FromResult(new DesktopServiceCommandResult(true, JsonSerializer.SerializeToElement(new[]
                {
                    new { id = "fixture-project", name = "Fixture", rootPath = "C:\\ProgrammingPanelFixture", purpose = "Receipt checks", createdAt = DateTimeOffset.UnixEpoch, updatedAt = DateTimeOffset.UnixEpoch, milestoneCount = 0, openItemCount = 0 }
                })));
                return FailList ? Task.FromException<DesktopServiceCommandResult>(new IOException("synthetic storage failure")) : Pending.Task.WaitAsync(token);
            case "SubmitWorkflowAsync":
                Interlocked.Increment(ref SubmitCalls);
                Submitted = (DesktopWorkflowSubmission)args[0]!;
                return Submit?.Invoke(Submitted, (CancellationToken)args[1]!) ?? Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "The fixture must not submit work."));
            case "ReplaceDraftAsync":
                Interlocked.Increment(ref ClearCalls);
                return Clear?.Invoke((CancellationToken)args[1]!) ?? Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
            case "SelectConversationAsync":
                ((CancellationToken)args[2]!).ThrowIfCancellationRequested();
                Interlocked.Increment(ref NavigationCalls); SelectedConversation = (string)args[1]!;
                return Task.FromResult(new DesktopOperationResult(NavigationState, "fixture navigation"));
        }
        if (method?.Name.StartsWith("add_", StringComparison.Ordinal) == true || method?.Name.StartsWith("remove_", StringComparison.Ordinal) == true) return null;
        throw new NotSupportedException("Unexpected fixture callback: " + method?.Name);
    }
}

internal static class ProgrammingPanelSubmissionChecks
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        Task Develop(ProgrammingPlugin plugin, CancellationToken token) => (Task)(typeof(ProgrammingPlugin)
            .GetMethod("DevelopCurrentRequestAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(plugin, [token]) ?? throw new InvalidOperationException("Submission returned no task."));
        var host = DispatchProxy.Create<IProgrammingPanelTestHost, ProgrammingPanelTestHostProxy>();
        var state = (ProgrammingPanelTestHostProxy)(object)host;
        var plugin = new ProgrammingPlugin(); plugin.Initialize(host);
        Check(plugin.AgentTemplates.All(template =>
            template.Instructions.Contains("complete handler, including later assignments", StringComparison.Ordinal) &&
            template.Instructions.Contains("current built artifacts", StringComparison.Ordinal) &&
            template.Instructions.Contains("admitted host checker", StringComparison.Ordinal) &&
            template.Instructions.Contains("Do not add Main or force OutputType=Exe", StringComparison.Ordinal)),
            "both actual programming templates carry the shared causal-diagnosis and current-evidence guidance");
        try
        {
            using var request = new CancellationTokenSource();
            var first = Develop(plugin, request.Token);
            Check(!first.IsCompleted && state.ListCalls == 1, "one preparation remains pending at its actual fixture callback");
            Develop(plugin, default).GetAwaiter().GetResult();
            Check(state.ListCalls == 1 && state.Reports.Any(text => text.Contains("already pending", StringComparison.Ordinal)), "a second local submission is refused while one is in flight");
            request.Cancel(); first.GetAwaiter().GetResult();
            Check(first.IsCompletedSuccessfully && state.SubmitCalls == 0, "pre-dispatch cancellation completes without workflow submission");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Develop(plugin, cancelled.Token).GetAwaiter().GetResult();
            Check(state.ListCalls == 1 && state.Reports.Last().Contains("cancelled", StringComparison.Ordinal), "cancelled preparation releases the in-flight guard for a later action");
            var closing = Develop(plugin, default);
            Check(!closing.IsCompleted && state.ListCalls == 2, "a new preparation can start after cancellation");
            plugin.Dispose(); closing.GetAwaiter().GetResult();
            Check(closing.IsCompletedSuccessfully && state.SubmitCalls == 0, "plugin disposal cancels pending preparation and settles its awaited task");
        }
        finally { plugin.Dispose(); state.Pending.TrySetCanceled(); }
        var failingHost = DispatchProxy.Create<IProgrammingPanelTestHost, ProgrammingPanelTestHostProxy>();
        var failing = (ProgrammingPanelTestHostProxy)(object)failingHost; failing.FailList = true;
        using var errorPlugin = new ProgrammingPlugin(); errorPlugin.Initialize(failingHost);
        Develop(errorPlugin, default).GetAwaiter().GetResult();
        Check(failing.ListCalls == 1 && failing.SubmitCalls == 0 && failing.Reports.Any(text => text.Contains("synthetic storage failure", StringComparison.Ordinal)), "service preparation errors are awaited and reported without submission");
        Develop(errorPlugin, default).GetAwaiter().GetResult();
        Check(failing.ListCalls == 2 && failing.SubmitCalls == 0, "error cleanup releases the guard instead of leaving the panel permanently busy");
        failing.Pending.TrySetCanceled();
        return count + ReceiptChecks();
    }

    private static int ReceiptChecks()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        void Await(Task task)
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                var deadline = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Send, (_, _) => frame.Continue = false, dispatcher);
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                try { Dispatcher.PushFrame(frame); }
                finally { deadline.Stop(); }
                if (!task.IsCompleted) throw new TimeoutException("The fake-host panel callback did not settle.");
            }
            task.GetAwaiter().GetResult();
        }
        Task Call(ProgrammingPlugin plugin, string method, CancellationToken token = default) =>
            (Task)typeof(ProgrammingPlugin).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [token])!;
        static IEnumerable<FrameworkElement> Elements(DependencyObject root)
        {
            if (root is FrameworkElement element) yield return element;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var descendant in Elements(child)) yield return descendant;
        }
        static T Control<T>(FrameworkElement view, string suffix) where T : FrameworkElement => Elements(view).OfType<T>()
            .Single(element => AutomationProperties.GetAutomationId(element) == "PegasusProgramming." + suffix);
        (ProgrammingPlugin Plugin, ProgrammingPanelTestHostProxy Host, FrameworkElement View) Fixture(
            Func<DesktopWorkflowSubmission, CancellationToken, Task<DesktopOperationResult>> submit,
            bool cancelDialog = false)
        {
            var host = DispatchProxy.Create<IProgrammingPanelTestHost, ProgrammingPanelTestHostProxy>();
            var state = (ProgrammingPanelTestHostProxy)(object)host; state.Prepared = true; state.Submit = submit;
            var plugin = new ProgrammingPlugin((_, _) => cancelDialog ? null : new("Fixture.csproj", "Release", ["build", "run"], null));
            plugin.Initialize(host);
            return (plugin, state, plugin.Panels[0].CreateView());
        }
        string State(FrameworkElement view) => Control<TextBlock>(view, "SubmissionState").Text;
        string Request(FrameworkElement view) => Control<TextBox>(view, "SubmissionRequestId").Text;
        string Run(FrameworkElement view) => Control<TextBox>(view, "SubmissionRunId").Text;
        bool CanOpen(FrameworkElement view) => Control<Button>(view, "OpenSubmission").IsEnabled;
        Task<DesktopOperationResult> Ack(DesktopWorkflowSubmission input, CancellationToken _) =>
            Task.FromResult(new DesktopOperationResult(DesktopOperationState.Accepted, "Saved", input.RequestId, "fixture-run"));
        try
        {
            var pending = new TaskCompletionSource<DesktopOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var f = Fixture((_, token) => pending.Task.WaitAsync(token));
            using (f.Plugin)
            {
                var task = Call(f.Plugin, "DevelopCurrentRequestAsync");
                Check(!task.IsCompleted && f.Host.SubmitCalls == 1 && f.Host.ClearCalls == 0, "one actual fake-host submission awaits its ACK without clearing the draft");
                Check(State(f.View).StartsWith("Submitting:", StringComparison.Ordinal) && Run(f.View) == "" && !CanOpen(f.View), "pending UI never reports acceptance or a run identity");
                Check(Request(f.View) == f.Host.Submitted!.RequestId, "the submitted request ID is visible before ACK");
                Await(Call(f.Plugin, "DevelopCurrentRequestAsync"));
                Check(f.Host.SubmitCalls == 1, "duplicate local invocation while pending cannot submit another workflow");
                pending.SetResult(new(DesktopOperationState.Accepted, "Saved", f.Host.Submitted!.RequestId, "fixture-run")); Await(task);
                Check(State(f.View).StartsWith("Accepted:", StringComparison.Ordinal) && Run(f.View) == "fixture-run" && Request(f.View) == f.Host.Submitted.RequestId, "matching ACK publishes both confirmed identities");
                Check(f.Host.ClearCalls == 1 && CanOpen(f.View), "only matching ACK clears the old draft and enables available navigation");
                Check(Control<TextBox>(f.View, "SubmissionRequestId").IsReadOnly && Control<TextBox>(f.View, "SubmissionRunId").IsReadOnly, "receipt fields are selectable read-only native controls with stable automation IDs");
                Check(f.Host.Submitted.Target is { DefinitionId: "pegasus.programming:develop", DefinitionVersion: "1.3.21" } &&
                    f.Host.Submitted.Target.Input.GetProperty("requiredChecks").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "build", "run" }), "panel still submits the exact Develop edition and chosen checks");
                Check(f.Host.Submitted.Context.Contains("complete handler, including later assignments", StringComparison.Ordinal) &&
                    f.Host.Submitted.Context.Contains("successful build proves compilation", StringComparison.Ordinal) &&
                    f.Host.Submitted.Context.Contains("user's purpose", StringComparison.Ordinal),
                    "actual submitted context carries causal diagnosis rather than only a generic repair instruction");
                Check(f.Host.Submitted.Context.Contains("Changing run flags", StringComparison.Ordinal) &&
                    f.Host.Submitted.Context.Contains("report the mismatch for owner correction", StringComparison.Ordinal),
                    "submitted context explains wrong library-run validation without bypassing accepted checks");
                Check(f.Host.Submitted.VisibleText == "Repair this bounded fixture." &&
                    f.Host.Submitted.Target!.Input.GetProperty("selectedTools").GetArrayLength() == 7,
                    "guidance preserves the exact owner request and selected-tool authority");
                Await(Call(f.Plugin, "OpenAcceptedWorkflowAsync"));
                Check(f.Host.ReadCalls == 1 && f.Host.NavigationCalls == 1 && f.Host.SelectedConversation == "accepted-conversation", "navigation resolves the saved conversation instead of guessing it from the run ID");
                Check(f.Host.SubmitCalls == 1 && Run(f.View) == "fixture-run" && State(f.View).Contains("does not prove completion", StringComparison.Ordinal), "opening accepted work never resubmits or claims workflow completion");
            }
            foreach (var outcome in new[] { DesktopOperationState.Rejected, DesktopOperationState.Failed, DesktopOperationState.Uncertain })
            {
                var rejected = Fixture((input, _) => Task.FromResult(new DesktopOperationResult(outcome, "Synthetic outcome", input.RequestId, "fixture-run")));
                using (rejected.Plugin)
                {
                    Await(Call(rejected.Plugin, "DevelopCurrentRequestAsync"));
                    Check(State(rejected.View).StartsWith(outcome == DesktopOperationState.Uncertain ? "Unconfirmed:" : outcome + ":", StringComparison.Ordinal), "non-ACK state remains truthful: " + outcome);
                    Check(rejected.Host.SubmitCalls == 1 && rejected.Host.ClearCalls == 0 && Run(rejected.View) == "" && !CanOpen(rejected.View), "failure/unknown retains only request identity without replay, draft clearing or navigation: " + outcome);
                    Check(Request(rejected.View) == rejected.Host.Submitted!.RequestId, "non-ACK preserves exact request ID: " + outcome);
                }
            }
            foreach (var invalid in new[] { "missing-run", "foreign-request" })
            {
                var bad = Fixture((input, _) => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Accepted, "Bad ACK", invalid == "foreign-request" ? "foreign" : input.RequestId, invalid == "missing-run" ? null : "fixture-run")));
                using (bad.Plugin)
                {
                    Await(Call(bad.Plugin, "DevelopCurrentRequestAsync"));
                    Check(State(bad.View).StartsWith("Unconfirmed:", StringComparison.Ordinal) && Run(bad.View) == "" && !CanOpen(bad.View) && bad.Host.ClearCalls == 0, "malformed or foreign ACK cannot earn accepted state: " + invalid);
                }
            }
            foreach (var failure in new[] { "cancel", "io" })
            {
                var interrupted = Fixture((_, _) => failure == "io" ? Task.FromException<DesktopOperationResult>(new IOException("synthetic transport EOF")) : Task.FromCanceled<DesktopOperationResult>(new CancellationToken(true)));
                using (interrupted.Plugin)
                {
                    Await(Call(interrupted.Plugin, "DevelopCurrentRequestAsync"));
                    Check(State(interrupted.View).StartsWith("Unconfirmed:", StringComparison.Ordinal) && Request(interrupted.View) == interrupted.Host.Submitted!.RequestId && Run(interrupted.View) == "", "post-dispatch interruption retains uncertain request identity: " + failure);
                    Check(interrupted.Host.SubmitCalls == 1 && interrupted.Host.ClearCalls == 0 && !CanOpen(interrupted.View), "post-dispatch interruption never replays or clears: " + failure);
                }
            }
            var cancelled = Fixture(Ack, cancelDialog: true);
            using (cancelled.Plugin)
            {
                Await(Call(cancelled.Plugin, "DevelopCurrentRequestAsync"));
                Check(State(cancelled.View).StartsWith("Cancelled:", StringComparison.Ordinal) && Request(cancelled.View) == "" && Run(cancelled.View) == "" && cancelled.Host.SubmitCalls == 0, "dialog cancellation is pre-dispatch, not accepted or uncertain");
            }
            var completedAck = Fixture((input, _) => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed, "Submission acknowledged", input.RequestId, "fixture-run")));
            using (completedAck.Plugin)
            {
                Await(Call(completedAck.Plugin, "DevelopCurrentRequestAsync"));
                Check(State(completedAck.View).StartsWith("Accepted:", StringComparison.Ordinal) && Run(completedAck.View) == "fixture-run", "Completed submission ACK is accepted without claiming workflow completion");
            }
            var disposalAck = new TaskCompletionSource<DesktopOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closing = Fixture((_, token) => disposalAck.Task.WaitAsync(token));
            var closingTask = Call(closing.Plugin, "DevelopCurrentRequestAsync"); closing.Plugin.Dispose(); Await(closingTask);
            Check(closing.Host.SubmitCalls == 1 && closing.Host.ClearCalls == 0 && closingTask.IsCompletedSuccessfully, "disposal after dispatch settles uncertainty without replay or draft clearing");
            foreach (var clearFailure in new[] { "cancel", "io", "rejected" })
            {
                var accepted = Fixture(Ack);
                accepted.Host.Clear = _ => clearFailure switch
                {
                    "cancel" => Task.FromCanceled<DesktopOperationResult>(new CancellationToken(true)),
                    "io" => Task.FromException<DesktopOperationResult>(new IOException("synthetic draft failure")),
                    _ => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "Changed draft"))
                };
                using (accepted.Plugin)
                {
                    Await(Call(accepted.Plugin, "DevelopCurrentRequestAsync"));
                    Check(State(accepted.View).StartsWith("Accepted:", StringComparison.Ordinal) && Run(accepted.View) == "fixture-run" && Request(accepted.View) == accepted.Host.Submitted!.RequestId, "ACK survives draft-clear interruption: " + clearFailure);
                    Check(accepted.Host.SubmitCalls == 1 && CanOpen(accepted.View), "draft-clear failure leaves accepted navigation available without resubmission: " + clearFailure);
                }
            }
            var noNavigation = Fixture(Ack); noNavigation.Host.NavigationAvailable = false;
            using (noNavigation.Plugin)
            {
                Await(Call(noNavigation.Plugin, "DevelopCurrentRequestAsync"));
                Check(Run(noNavigation.View) == "fixture-run" && !CanOpen(noNavigation.View), "hosts without navigation still show the confirmed receipt and disable the action");
            }
            var foreign = Fixture(Ack); foreign.Host.ForeignSavedRun = true;
            using (foreign.Plugin)
            {
                Await(Call(foreign.Plugin, "DevelopCurrentRequestAsync")); Await(Call(foreign.Plugin, "OpenAcceptedWorkflowAsync"));
                Check(foreign.Host.NavigationCalls == 0 && Run(foreign.View) == "fixture-run" && State(foreign.View).Contains("does not match", StringComparison.Ordinal), "foreign saved-run identity cannot redirect accepted workflow navigation");
            }
            var foreignRequest = Fixture(Ack); foreignRequest.Host.ForeignSavedRequest = true;
            using (foreignRequest.Plugin)
            {
                Await(Call(foreignRequest.Plugin, "DevelopCurrentRequestAsync")); Await(Call(foreignRequest.Plugin, "OpenAcceptedWorkflowAsync"));
                Check(foreignRequest.Host.NavigationCalls == 0 && Run(foreignRequest.View) == "fixture-run", "matching run with a foreign submission request cannot redirect navigation");
            }
            var navigationFailure = Fixture(Ack); navigationFailure.Host.NavigationState = DesktopOperationState.Rejected;
            using (navigationFailure.Plugin)
            {
                Await(Call(navigationFailure.Plugin, "DevelopCurrentRequestAsync")); Await(Call(navigationFailure.Plugin, "OpenAcceptedWorkflowAsync"));
                Check(State(navigationFailure.View).StartsWith("Accepted:", StringComparison.Ordinal) && Run(navigationFailure.View) == "fixture-run" && navigationFailure.Host.SubmitCalls == 1, "navigation rejection preserves accepted IDs without replay");
            }
            return checks;
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
}
