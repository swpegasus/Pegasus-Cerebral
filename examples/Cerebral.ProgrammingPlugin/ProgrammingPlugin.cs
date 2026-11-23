using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;
using Cerebral.Programming;
using Microsoft.Win32;

namespace Cerebral.ProgrammingPlugin;

public sealed class ProgrammingPlugin : IDesktopPlugin, IDesktopAgentTemplatePlugin, IDesktopConfigurationPlugin
{
    private const string DevelopmentWorkflowId = ProgrammingWorkflowIdentity.DevelopId;
    private const string DevelopmentWorkflowVersion = ProgrammingWorkflowIdentity.DevelopVersion;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private IDesktopPluginHost? host; private IDesktopServiceHost? service; private IDesktopWorkspaceHost? workspace;
    private readonly TreeView projects = new(); private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private Summary[] projectSummaries = [];
    private bool disposed;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? submissionCancellation;
    private int submitting;
    private long refreshGeneration;
    private Button? developButton;
    private Button? cancelSubmissionButton;
    private Button? openSubmissionButton;
    private TextBlock? submissionStatus;
    private TextBox? submissionRequestId;
    private TextBox? submissionRunId;
    private SubmissionReceipt? submissionReceipt;
    private readonly Func<string, CancellationToken, ProgrammingDevelopmentOptions?> chooseDevelopment;
    private int openingSubmission;

    public ProgrammingPlugin() : this(ProgrammingDevelopmentDialog.Ask) { }
    internal ProgrammingPlugin(Func<string, CancellationToken, ProgrammingDevelopmentOptions?> chooseDevelopment) =>
        this.chooseDevelopment = chooseDevelopment ?? throw new ArgumentNullException(nameof(chooseDevelopment));

    public IReadOnlyList<DesktopPanel> Panels => [new("programming.projects", "Programming", CreatePanel)];
    public IReadOnlyList<DesktopPanel> ConfigurationPanels => [new("programming.settings", "Programming", CreateConfigurationPanel)];
    public IReadOnlyList<DesktopAction> ComposerActions =>
    [
        new("programming.develop", "Develop", DevelopCurrentRequestAsync,
            "Run the current request through the durable Pegasus Programming workflow for the selected programming project.")
    ];
    public IReadOnlyList<DesktopAgentTemplate> AgentTemplates =>
    [
        new("programming-engineer", "Programming engineer", "helper", "Implement and verify bounded software work from a durable programming plan.",
            "Inspect the durable programming project before acting. Use ordinary Cerebral file, process, Git, HTTP, and program tools; never ask for or emit raw PAO provider arguments. Read before editing, keep changes cohesive, run the focused build or test needed for the change, and record only compact verified outcomes in the programming plan. Mark an item done only after its intended result exists. Return a concise receipt to the lead, including changed paths, verification level, failures, and remaining work." + "\n" + ProgrammingDevelopmentGuidance.FailedBehaviorDiagnosis)
        {
            RequiredCapabilities = ["program.read", "program.write", "program.check", "filesystem.read", "filesystem.write", "process.execute", "git.read", "git.write", "network.http"],
            RequiredTools = ["program.list", "program.inspect", "program.context", "program.plan", "program.item.update", "program.decision.record", "program.check.record", "file.read", "file.inspect", "file.list", "file.search", "file.write", "file.edit", "file.mkdir", "process.run", "plugin.verify", "plugin.present", "git.status", "git.diff"],
            MaxSteps = 32, MaxSeconds = 1200
        },
        new("programming-reviewer", "Programming reviewer", "helper", "Review a bounded implementation against its durable plan and real verification receipts.",
            "Inspect the project plan and exact changed files. Use diffs, build or test receipts, and runtime evidence at their actual level. Record concrete defects or missing checks in the appropriate work item; do not create duplicate work. Return compact findings to the lead." + "\n" + ProgrammingDevelopmentGuidance.FailedBehaviorDiagnosis)
        {
            RequiredCapabilities = ["program.read", "program.write", "program.check", "filesystem.read", "process.execute", "git.read"],
            RequiredTools = ["program.list", "program.inspect", "program.context", "program.item.update", "program.check.record", "file.read", "file.search", "process.run", "git.status", "git.diff"],
            MaxSteps = 20, MaxSeconds = 900
        }
    ];

    public void Initialize(IDesktopPluginHost host)
    {
        this.host = host; service = host as IDesktopServiceHost ?? throw new InvalidOperationException("Pegasus Programming requires structured service commands.");
        workspace = host as IDesktopWorkspaceHost; host.ConversationChanged += ConversationChanged;
    }
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

    private FrameworkElement CreatePanel()
    {
        var root = new DockPanel { Margin = new Thickness(8) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Programming projects", FontSize = 14, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Plans, work items, decisions, and checks stay in a small service-owned database. Source and build output stay in the project folder.", TextWrapping = TextWrapping.Wrap, Opacity = .72, Margin = new Thickness(0, 3, 0, 7) });
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("Start project", StartProjectAsync));
        developButton = ActionButton("Develop request", DevelopCurrentRequestAsync);
        AutomationProperties.SetAutomationId(developButton, "PegasusProgramming.DevelopRequest");
        actions.Children.Add(developButton);
        cancelSubmissionButton = ActionButton("Cancel submission", _ =>
        {
            submissionCancellation?.Cancel();
            return Task.CompletedTask;
        });
        cancelSubmissionButton.IsEnabled = false;
        AutomationProperties.SetAutomationId(cancelSubmissionButton, "PegasusProgramming.CancelSubmission");
        actions.Children.Add(cancelSubmissionButton);
        actions.Children.Add(ActionButton("Refresh", token => RefreshAsync(token)));
        header.Children.Add(actions);
        submissionStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) };
        AutomationProperties.SetAutomationId(submissionStatus, "PegasusProgramming.SubmissionState");
        header.Children.Add(submissionStatus);
        submissionRequestId = ReceiptField("PegasusProgramming.SubmissionRequestId");
        submissionRunId = ReceiptField("PegasusProgramming.SubmissionRunId");
        header.Children.Add(new TextBlock { Text = "Request ID", Margin = new Thickness(0, 3, 0, 0) });
        header.Children.Add(submissionRequestId);
        header.Children.Add(new TextBlock { Text = "Accepted workflow ID", Margin = new Thickness(0, 3, 0, 0) });
        header.Children.Add(submissionRunId);
        openSubmissionButton = ActionButton("Open accepted workflow", OpenAcceptedWorkflowAsync);
        AutomationProperties.SetAutomationId(openSubmissionButton, "PegasusProgramming.OpenSubmission");
        header.Children.Add(openSubmissionButton);
        RenderSubmissionReceipt();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var content = new Grid(); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 120 }); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 90 });
        projects.BorderThickness = new Thickness(0); projects.SelectedItemChanged += (_, _) => ShowDetails(); AutomationProperties.SetAutomationId(projects, "PegasusProgramming.ProjectTree"); Grid.SetRow(projects, 0); content.Children.Add(projects);
        var splitter = new GridSplitter { Height = 7, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Background = SystemColors.ControlLightBrush, ToolTip = "Drag to resize project items and details." }; Grid.SetRow(splitter, 1); content.Children.Add(splitter);
        var detailScroll = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(detailScroll, 2); content.Children.Add(detailScroll); root.Children.Add(content);
        root.Loaded += async (_, _) => await RunPanelActionAsync(token => RefreshAsync(token));
        return root;
    }
    private FrameworkElement CreateConfigurationPanel()
    {
        var root = new StackPanel { Margin = new Thickness(16) }; var status = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Text = "Checking module…" };
        root.Children.Add(new TextBlock { Text = "Pegasus Programming module", FontSize = 18, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "Programming domain state is modular. Removing this plugin leaves the core conversation, file, process, Git, and PAO-backed semantic tools intact.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 12) });
        root.Children.Add(status);
        root.Children.Add(ActionButton("Refresh module status", token => LoadStatusAsync(status, token)));
        root.Loaded += async (_, _) => await RunPanelActionAsync(token => LoadStatusAsync(status, token));
        return new ScrollViewer { Content = root };
    }
    private async Task LoadStatusAsync(TextBox status, CancellationToken cancellationToken)
    {
        var result = await CommandAsync<Status>("programming.status", new { }, cancellationToken);
        if (!disposed) status.Text = $"Programming projects: {result.ProjectCount}";
    }
    private async Task RefreshAsync(CancellationToken cancellationToken, string? selectId = null)
    {
        if (disposed || service is null) return;
        var generation = Interlocked.Increment(ref refreshGeneration);
        selectId ??= (projects.SelectedItem as TreeViewItem)?.Tag is Selection selection ? selection.Project.Id : null;
        try
        {
            var summaries = await CommandAsync<Summary[]>("programming.list", new { }, cancellationToken);
            if (disposed || generation != refreshGeneration) return;
            projectSummaries = summaries; projects.Items.Clear();
            foreach (var summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inspected = await CommandAsync<Details>("programming.inspect", new { projectId = summary.Id }, cancellationToken);
                if (disposed || generation != refreshGeneration) return;
                var projectNode = Node(summary.Name, new Selection("project", summary, summary)); projectNode.ToolTip = summary.RootPath;
                foreach (var milestone in inspected.Milestones)
                {
                    var milestoneNode = Node($"{milestone.Sequence}. {milestone.Title} [{milestone.Status}]", new Selection("milestone", summary, milestone));
                    foreach (var item in milestone.Items) milestoneNode.Items.Add(Node($"{StatusGlyph(item.Status)} {item.Title}", new Selection("item", summary, item)));
                    projectNode.Items.Add(milestoneNode);
                }
                if (inspected.Decisions.Length > 0) { var decisions = Node("Decisions", new Selection("group", summary, inspected.Decisions)); foreach (var decision in inspected.Decisions) decisions.Items.Add(Node(decision.Title, new Selection("decision", summary, decision))); projectNode.Items.Add(decisions); }
                if (inspected.Checks.Length > 0) { var checks = Node("Checks", new Selection("group", summary, inspected.Checks)); foreach (var check in inspected.Checks) checks.Items.Add(Node($"{check.Outcome}: {check.Kind}", new Selection("check", summary, check))); projectNode.Items.Add(checks); }
                projects.Items.Add(projectNode); if (summary.Id == selectId) { projectNode.IsExpanded = true; projectNode.IsSelected = true; }
            }
            details.Text = summaries.Length == 0 ? "No programming project has been created. Select Start project to bind a durable plan to a source root." : "Select a project, milestone, work item, decision, or check.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (disposed || generation != refreshGeneration) return;
            details.Text = "Programming projects could not be loaded: " + error.Message;
            Report(details.Text);
        }
    }

    private async Task DevelopCurrentRequestAsync(CancellationToken cancellationToken)
    {
        if (disposed || host is not { } currentHost) return;
        if (Volatile.Read(ref openingSubmission) != 0) { Report("Wait for workflow navigation to finish before submitting another request."); return; }
        if (Interlocked.CompareExchange(ref submitting, 1, 0) != 0)
        {
            Report("A Programming submission is already pending. Wait for its receipt or cancel preparation.");
            return;
        }
        CancellationTokenSource? linked = null;
        var dispatched = false;
        var accepted = false;
        string? requestId = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            submissionCancellation = linked;
            SetSubmissionReceipt("Preparing", "Choosing the project and checks; no workflow has been submitted.");
            UpdateSubmissionControls();
            if (currentHost is not IDesktopNativeWorkflowHost workflows)
            {
                SetSubmissionReceipt("Not submitted", "This Cerebral host does not support the durable Programming workflow."); return;
            }
            var draft = currentHost.GetDraft();
            if (string.IsNullOrWhiteSpace(draft.Text)) { SetSubmissionReceipt("Not submitted", "Enter the programming request in the message box first."); return; }
            Report("Choose the Programming build target and verification checks.");
            var availableProjects = await CommandAsync<Summary[]>("programming.list", new { }, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var selectedId = (projects.SelectedItem as TreeViewItem)?.Tag is Selection selection ? selection.Project.Id : null;
            var selected = availableProjects.FirstOrDefault(item => item.Id == selectedId) ??
                availableProjects.FirstOrDefault(item => SamePath(item.RootPath, currentHost.GetConversation().ProjectPath)) ??
                ProjectMentionedIn(draft.Text, availableProjects) ??
                (availableProjects.Length == 1 ? availableProjects[0] : null);
            if (selected is null) { SetSubmissionReceipt("Not submitted", "Select a Programming project, or start one for the current source root, before developing the request."); return; }
            var options = chooseDevelopment(selected.RootPath, linked.Token);
            if (options is null) { SetSubmissionReceipt("Cancelled", "Programming preparation cancelled; no workflow was submitted."); return; }
            linked.Token.ThrowIfCancellationRequested();
            var currentDraft = currentHost.GetDraft();
            if (currentDraft.Binding != draft.Binding || currentDraft.Revision != draft.Revision)
            { SetSubmissionReceipt("Not submitted", "The conversation or request changed during preparation. Review the current request before developing it."); return; }
            var selectedTools = new List<string> { "file.read", "file.list", "file.search", "file.write", "file.edit", "file.mkdir", "process.run" };
            var input = new Dictionary<string, object>
            {
                ["rootPath"] = selected.RootPath, ["projectFile"] = options.ProjectFile,
                ["configuration"] = options.Configuration, ["selectedTools"] = selectedTools.ToArray(),
                ["requiredChecks"] = options.RequiredChecks
            };
            if (options.PresentManifest is not null)
            {
                selectedTools.Add("plugin.contract"); input["selectedTools"] = selectedTools.ToArray();
                input["presentManifest"] = options.PresentManifest;
                input["contractTopic"] = ContractTopicFor(draft.Text);
            }
            requestId = Guid.NewGuid().ToString("N");
            var submission = new DesktopWorkflowSubmission(draft.Binding, requestId, draft.Text,
                "Use the explicitly accepted Programming target and configured checks. Read relevant source, make bounded cohesive changes, repair real failures, and report evidence only at its actual level. Retrieve the exact plugin.contract topic before SDK/WPF repairs when plugin presentation is configured." + "\n" + ProgrammingDevelopmentGuidance.FailedBehaviorDiagnosis)
            {
                Target = new DesktopWorkflowTarget(DevelopmentWorkflowId, DevelopmentWorkflowVersion,
                    JsonSerializer.SerializeToElement(input, Json), StepLimit: 256)
            };
            SetSubmissionReceipt("Submitting", "Waiting for the host acknowledgement. Acceptance is not yet confirmed.", requestId);
            dispatched = true;
            var result = await workflows.SubmitWorkflowAsync(submission, linked.Token);
            // An ACK must identify this exact request and a real run. A click or foreign/missing ID
            // cannot earn accepted state, enable navigation, or clear the user's draft.
            accepted = (result.State is DesktopOperationState.Accepted or DesktopOperationState.Completed) &&
                result.RequestId == requestId && ReceiptId(result.JobId);
            if (accepted)
            {
                SetSubmissionReceipt("Accepted", result.Message ?? "Programming work was accepted in Work.", requestId, result.JobId);
                if (disposed) return;
                var cleared = await currentHost.ReplaceDraftAsync(new(draft.Binding, draft.Revision, ""), linked.Token);
                if (cleared.State != DesktopOperationState.Completed)
                    SetSubmissionReceipt("Accepted", "Programming work was accepted, but the composer changed before it could be cleared.", requestId, result.JobId);
            }
            else if (result.State is DesktopOperationState.Rejected or DesktopOperationState.Failed)
                SetSubmissionReceipt(result.State.ToString(), result.Message ?? "Programming work was not accepted.", requestId);
            else
                SetSubmissionReceipt("Unconfirmed", "Check the retained request in Work before retrying. " +
                    (result.Message ?? "The host did not return a matching accepted workflow identity."), requestId);
            // Work owns uncertain request IDs and explicit retry/reconciliation. Never resend here.
        }
        catch (OperationCanceledException)
        {
            SetSubmissionReceipt(accepted ? "Accepted" : dispatched ? "Unconfirmed" : "Cancelled", accepted ? "Programming work was accepted; composer clearing was interrupted. Check Work before taking another action." :
                dispatched ? "Submission confirmation was interrupted. Check the retained request in Work before retrying." :
                "Programming preparation cancelled; no workflow was submitted.", requestId, accepted ? submissionReceipt?.RunId : null);
        }
        catch (Exception error)
        {
            SetSubmissionReceipt(accepted ? "Accepted" : dispatched ? "Unconfirmed" : "Failed", (accepted ? "Programming work was accepted, but its display or composer update failed. Check Work. " :
                dispatched ? "Programming submission could not be confirmed. Check Work before retrying. " :
                "Programming work could not be prepared: ") + error.Message, requestId, accepted ? submissionReceipt?.RunId : null);
        }
        finally
        {
            submissionCancellation = null;
            Interlocked.Exchange(ref submitting, 0);
            UpdateSubmissionControls();
            linked?.Dispose();
        }
    }


    private static Summary? ProjectMentionedIn(string request, IEnumerable<Summary> projects)
    {
        var matches = projects.Where(project => request.Contains(project.RootPath, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static string ContractTopicFor(string request)
    {
        if (request.Contains("conversation.start", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("new-conversation", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("new-chat", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("new chat", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("new conversation", StringComparison.OrdinalIgnoreCase)) return "new-conversation";
        if (request.Contains("composer command", StringComparison.OrdinalIgnoreCase)) return "composer-command";
        if (request.Contains("response balance", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("response setting", StringComparison.OrdinalIgnoreCase)) return "response-settings";
        if (request.Contains("conversation", StringComparison.OrdinalIgnoreCase)) return "conversation";
        if (request.Contains("mount", StringComparison.OrdinalIgnoreCase)) return "mount-view";
        return "desktop-base";
    }
    private static bool SamePath(string first, string? second) => second is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
    private void ShowDetails()
    {
        if (projects.SelectedItem is not TreeViewItem { Tag: Selection selection }) return;
        details.Text = selection.Kind switch
        {
            "project" when selection.Value is Summary value => $"{value.Name}\n\n{value.Purpose}\n\nRoot: {value.RootPath}\nMilestones: {value.MilestoneCount}\nOpen work: {value.OpenItemCount}",
            "milestone" when selection.Value is Milestone value => $"{value.Sequence}. {value.Title}\nStatus: {value.Status}\n\n{value.Objective}\n\n{value.Items.Length} work item(s)",
            "item" when selection.Value is WorkItem value => $"{value.Title}\nKind: {value.Kind}\nStatus: {value.Status}\n\n{value.Details}\n\n{value.Notes}\n\n{string.Join(Environment.NewLine, value.FilePaths)}",
            "decision" when selection.Value is Decision value => $"{value.Title}\n\nDecision\n{value.DecisionText}\n\nRationale\n{value.Rationale}",
            "check" when selection.Value is Check value => $"{value.Kind}: {value.Outcome}\n\n{value.Command}\n\n{value.Details}",
            _ => "Select a specific item to inspect it."
        };
    }
    private async Task StartProjectAsync(CancellationToken cancellationToken)
    {
        if (disposed || host is not { } currentHost || service is null) return;
        var conversation = currentHost.GetConversation();
        var fallbackRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegasus", "Cerebral", "Projects", SafeName(conversation.Title));
        var input = ProgrammingProjectDialog.Ask(conversation.Title == "New conversation" ? "New application" : conversation.Title, conversation.ProjectPath ?? fallbackRoot);
        if (input is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        var created = await CommandAsync<Summary>("programming.create", new { name = input.Value.Name, rootPath = input.Value.RootPath, purpose = input.Value.Purpose }, cancellationToken);
        await RefreshAsync(cancellationToken, created.Id);
        if (workspace is not null && workspace.WorkspaceAvailable)
        {
            var opened = await workspace.NewConversationAsync(conversation.Binding, conversation.ProjectId);
            cancellationToken.ThrowIfCancellationRequested();
            if (opened.State != DesktopOperationState.Completed) { Report("Programming project created, but a conversation could not be opened: " + opened.Message); return; }
        }
        var draft = currentHost.GetDraft();
        var starter = $"Start guided programming work for \"{created.Name}\". Durable programming project ID {created.Id} is already created for {created.RootPath}. Inspect it with program.inspect. If it has no plan, clarify only requirements that materially change architecture or user-visible behavior, then save a milestone plan with program.plan. Execute ordinary filesystem, build, test, Git, HTTP, and diagnostics through Cerebral's semantic tools; PAO may be used internally by the service and must not be called or described as a raw model tool. Keep source and build output in the project root, record compact decisions and checks in the programming database, and continue until the requested usable result is verified.";
        var staged = await currentHost.ReplaceDraftAsync(new(draft.Binding, draft.Revision, starter), cancellationToken);
        Report(staged.State == DesktopOperationState.Completed ? "Programming project created. Review the starter, then Send." : staged.Message ?? "The starter could not be prepared.");
    }
    private async void ConversationChanged(object? sender, DesktopConversationChangedEventArgs args) =>
        await RunPanelActionAsync(token => RefreshAsync(token));
    private async Task<T> CommandAsync<T>(string operation, object arguments, CancellationToken cancellationToken)
    {
        var currentService = service ?? throw new ObjectDisposedException(nameof(ProgrammingPlugin));
        var result = await currentService.ExecuteServiceCommandAsync(operation, arguments, cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? operation + " failed.");
        return result.Data.Deserialize<T>(Json) ?? throw new InvalidDataException(operation + " returned no usable data.");
    }
    private async Task RunPanelActionAsync(Func<CancellationToken, Task> action)
    {
        if (disposed) return;
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) { Report("Programming action cancelled. Refresh Work or the project list if an operation was already submitted."); }
        catch (Exception error) { Report("Programming action could not finish: " + error.Message); }
    }
    private Button ActionButton(string text, Func<CancellationToken, Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6), MinWidth = 82 };
        button.Click += async (_, _) =>
        {
            if (disposed || !button.IsEnabled) return;
            button.IsEnabled = false;
            try { await RunPanelActionAsync(action); }
            finally { if (!disposed) { button.IsEnabled = true; UpdateSubmissionControls(); } }
        };
        return button;
    }
    private void UpdateSubmissionControls()
    {
        if (disposed) return;
        if (developButton is not null) developButton.IsEnabled = Volatile.Read(ref submitting) == 0 && Volatile.Read(ref openingSubmission) == 0;
        if (cancelSubmissionButton is not null) cancelSubmissionButton.IsEnabled = submissionCancellation is not null;
        if (openSubmissionButton is not null) openSubmissionButton.IsEnabled = submissionReceipt is { State: "Accepted" } &&
            workspace?.WorkspaceAvailable == true && Volatile.Read(ref submitting) == 0 && Volatile.Read(ref openingSubmission) == 0;
    }

    private static TextBox ReceiptField(string id)
    {
        var field = new TextBox { IsReadOnly = true, MinHeight = 26, Margin = new Thickness(0, 2, 0, 3) };
        AutomationProperties.SetAutomationId(field, id);
        return field;
    }

    private static bool ReceiptId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);

    private void SetSubmissionReceipt(string state, string message, string? requestId = null, string? runId = null)
    {
        if (disposed) return;
        submissionReceipt = new(state, message, requestId, runId);
        RenderSubmissionReceipt();
        Report(state + ": " + message + (requestId is null ? "" : " Request: " + requestId) + (runId is null ? "" : " Workflow: " + runId));
    }

    private void RenderSubmissionReceipt()
    {
        if (disposed) return;
        if (submissionStatus is not null) submissionStatus.Text = submissionReceipt is { } receipt
            ? receipt.State + ": " + receipt.Message : "No Programming request has been submitted.";
        if (submissionRequestId is not null) submissionRequestId.Text = submissionReceipt?.RequestId ?? "";
        if (submissionRunId is not null) submissionRunId.Text = submissionReceipt?.RunId ?? "";
        UpdateSubmissionControls();
    }

    private async Task OpenAcceptedWorkflowAsync(CancellationToken token)
    {
        if (disposed || host is not { } currentHost || workspace is not { WorkspaceAvailable: true } currentWorkspace ||
            submissionReceipt is not { State: "Accepted", RequestId: { } requestId, RunId: { } runId } receipt ||
            Volatile.Read(ref submitting) != 0 || Interlocked.CompareExchange(ref openingSubmission, 1, 0) != 0) return;
        try
        {
            UpdateSubmissionControls();
            var binding = currentHost.GetConversation().Binding;
            // Run IDs are not conversation IDs in the SDK contract. Resolve the saved coordinate
            // through the existing owner-bound read, then use normal draft-preserving navigation.
            var saved = await CommandAsync<SavedWorkflow>("jobs.workflow.get", new { id = runId }, token);
            token.ThrowIfCancellationRequested();
            if (disposed || !ReferenceEquals(receipt, submissionReceipt) || currentHost.GetConversation().Binding != binding) return;
            if (saved.Id != runId || saved.Submission is not { } savedSubmission || savedSubmission.RequestId != requestId || !ReceiptId(savedSubmission.ConversationId) ||
                savedSubmission.DefinitionId != DevelopmentWorkflowId || savedSubmission.DefinitionVersion != DevelopmentWorkflowVersion)
                throw new InvalidDataException("The saved workflow does not match this accepted Programming receipt.");
            var opened = await currentWorkspace.SelectConversationAsync(binding, savedSubmission.ConversationId, token);
            if (disposed || !ReferenceEquals(receipt, submissionReceipt)) return;
            SetSubmissionReceipt("Accepted", opened.State == DesktopOperationState.Completed
                ? "Opened the accepted workflow conversation. This does not prove completion."
                : "Programming work remains accepted. Navigation was not confirmed: " + (opened.Message ?? opened.State.ToString()), requestId, runId);
        }
        catch (OperationCanceledException) { if (!disposed && ReferenceEquals(receipt, submissionReceipt)) SetSubmissionReceipt("Accepted", "Opening the workflow was interrupted. The accepted IDs are retained.", requestId, runId); }
        catch (Exception error) { if (!disposed && ReferenceEquals(receipt, submissionReceipt)) SetSubmissionReceipt("Accepted", "Programming work remains accepted. Could not open its conversation: " + error.Message, requestId, runId); }
        finally { Interlocked.Exchange(ref openingSubmission, 0); UpdateSubmissionControls(); }
    }

    private sealed record SubmissionReceipt(string State, string Message, string? RequestId, string? RunId);
    private sealed record SavedWorkflow(string Id, SavedWorkflowSubmission Submission);
    private sealed record SavedWorkflowSubmission(string RequestId, string ConversationId, string DefinitionId, string DefinitionVersion);
    private void Report(string text)
    {
        if (disposed) return;
        try { host?.ReportStatus(text); }
        catch (ObjectDisposedException) { /* The host may finish its lifetime before deferred UI callbacks. */ }
    }

    private static TreeViewItem Node(string text, Selection selection) => new() { Header = text, Tag = selection, IsExpanded = selection.Kind == "project" };
    private static string StatusGlyph(string status) => status switch { "done" => "✓", "active" => "▶", "blocked" => "!", "cancelled" => "×", _ => "○" };
    private static string SafeName(string value) { var invalid = Path.GetInvalidFileNameChars(); var cleaned = new string(value.Where(x => !invalid.Contains(x)).ToArray()).Trim(); return string.IsNullOrWhiteSpace(cleaned) ? "NewProject" : cleaned; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (host is not null) host.ConversationChanged -= ConversationChanged;
        try { lifetime.Cancel(); }
        finally { lifetime.Dispose(); host = null; service = null; workspace = null; }
    }

    private sealed record Status(string Directory, int ProjectCount);
    private sealed record Summary(string Id, string Name, string RootPath, string Purpose, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MilestoneCount, int OpenItemCount);
    private sealed record Details(Summary Project, Milestone[] Milestones, Decision[] Decisions, Check[] Checks);
    private sealed record Milestone(long Id, int Sequence, string Title, string Objective, string Status, WorkItem[] Items);
    private sealed record WorkItem(long Id, long MilestoneId, string Title, string Details, string Kind, string Status, string Notes, string[] FilePaths, DateTimeOffset UpdatedAt);
    private sealed record Decision(long Id, string Title, [property: System.Text.Json.Serialization.JsonPropertyName("decision")] string DecisionText, string Rationale, DateTimeOffset CreatedAt);
    private sealed record Check(long Id, string Kind, string Command, string Outcome, string Details, DateTimeOffset CreatedAt);
    private sealed record Selection(string Kind, Summary Project, object? Value);
}

internal static class ProgrammingProjectDialog
{
    public static (string Name, string RootPath, string Purpose)? Ask(string initialName, string initialPath)
    {
        var window = new Window { Title = "Start programming project", Width = 620, Height = 430, MinWidth = 480, MinHeight = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize };
        if (Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) is { } owner) window.Owner = owner;
        var root = new Grid { Margin = new Thickness(16) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "Create a durable programming plan", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var name = Labeled(root, 1, "Name", initialName); var path = Labeled(root, 2, "Project root", initialPath);
        var purposePanel = new DockPanel(); purposePanel.Children.Add(new TextBlock { Text = "Purpose", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) }); var purpose = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; DockPanel.SetDock(purpose, Dock.Bottom); purposePanel.Children.Add(purpose); Grid.SetRow(purposePanel, 3); root.Children.Add(purposePanel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; var create = new Button { Content = "Create", IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 5, 10, 5) }; var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) }; create.Click += (_, _) => { if (string.IsNullOrWhiteSpace(name.Text) || !Path.IsPathFullyQualified(path.Text)) { MessageBox.Show(window, "Enter a name and an absolute project root.", "Programming project", MessageBoxButton.OK, MessageBoxImage.Information); return; } window.DialogResult = true; }; buttons.Children.Add(create); buttons.Children.Add(cancel); Grid.SetRow(buttons, 4); root.Children.Add(buttons); window.Content = root;
        return window.ShowDialog() == true ? (name.Text.Trim(), Path.GetFullPath(path.Text.Trim()), purpose.Text.Trim()) : null;
    }
    private static TextBox Labeled(Grid root, int row, string label, string text) { var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(new TextBlock { Text = label, Width = 100, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); var box = new TextBox { Text = text, MinHeight = 28 }; panel.Children.Add(box); Grid.SetRow(panel, row); root.Children.Add(panel); return box; }
}

internal sealed record ProgrammingDevelopmentOptions(string ProjectFile, string Configuration, string[] RequiredChecks, string? PresentManifest);

internal static class ProgrammingDevelopmentDialog
{
    internal static ProgrammingDevelopmentOptions? Ask(string rootPath, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(root)) throw new IOException("The selected Programming source root is unavailable.");
        cancellationToken.ThrowIfCancellationRequested();
        var window = new Window { Title = "Develop programming request", Width = 660, Height = 480, MinWidth = 520, MinHeight = 410,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize };
        if (Application.Current?.Windows.OfType<Window>().FirstOrDefault(item => item.IsActive) is { } owner) window.Owner = owner;
        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock { Text = "Choose the build target and checks", FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = root, TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new Thickness(0, 6, 0, 10) });
        body.Children.Add(new TextBlock { Text = "Project or solution", FontWeight = FontWeights.SemiBold });
        var targetRow = new DockPanel();
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right); targetRow.Children.Add(browse);
        var target = new TextBox { MinHeight = 28 }; targetRow.Children.Add(target); body.Children.Add(targetRow);
        var configuration = new ComboBox { ItemsSource = new[] { "Release", "Debug" }, SelectedIndex = 0, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) };
        body.Children.Add(new TextBlock { Text = "Configuration", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) }); body.Children.Add(configuration);
        body.Children.Add(new TextBlock { Text = "Required verification", FontWeight = FontWeights.SemiBold });
        var checks = new WrapPanel();
        var build = new CheckBox { Content = "Build", IsChecked = true, Margin = new Thickness(0, 5, 18, 5) };
        var test = new CheckBox { Content = "Test", Margin = new Thickness(0, 5, 18, 5) };
        var run = new CheckBox { Content = "Run", Margin = new Thickness(0, 5, 18, 5) };
        checks.Children.Add(build); checks.Children.Add(test); checks.Children.Add(run); body.Children.Add(checks);
        body.Children.Add(new TextBlock { Text = "These checks define what the workflow will prove. A build alone does not verify application behavior.", TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        var present = new CheckBox { Content = "Verify and present this desktop plugin for review", Margin = new Thickness(0, 10, 0, 6), IsEnabled = false };
        var manifest = new TextBox { MinHeight = 28 };
        var advanced = new StackPanel(); advanced.Children.Add(new TextBlock { Text = "Built desktop-plugin.json (relative to this source root)", TextWrapping = TextWrapping.Wrap }); advanced.Children.Add(manifest);
        var output = new Expander { Header = "Plugin output (advanced)", Content = advanced, Visibility = Visibility.Collapsed };
        body.Children.Add(present); body.Children.Add(output);
        string? configuredTarget = null;
        void TargetChanged()
        {
            var project = Path.GetExtension(target.Text).Equals(".csproj", StringComparison.OrdinalIgnoreCase);
            run.IsEnabled = project; if (!project) run.IsChecked = false;
            var full = TryFullTarget(root, target.Text);
            var plugin = project && full is not null && File.Exists(Path.Combine(Path.GetDirectoryName(full)!, "desktop-plugin.json"));
            present.IsEnabled = plugin;
            output.Visibility = plugin ? Visibility.Visible : Visibility.Collapsed;
            if (!plugin) present.IsChecked = false;
            else if (configuredTarget != full)
            {
                configuredTarget = full;
                manifest.Text = Path.GetRelativePath(root, Path.Combine(Path.GetDirectoryName(full)!, "bin", (string)configuration.SelectedItem, "net10.0-windows", "desktop-plugin.json"));
                present.IsChecked = true;
            }
        }
        target.TextChanged += (_, _) => TargetChanged();
        present.Checked += (_, _) => { build.IsChecked = true; build.IsEnabled = false; };
        present.Unchecked += (_, _) => build.IsEnabled = true;
        configuration.SelectionChanged += (_, _) => { configuredTarget = null; TargetChanged(); };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "Choose a project or solution inside this source root", InitialDirectory = root,
                Filter = "Projects and solutions (*.csproj;*.sln;*.slnx)|*.csproj;*.sln;*.slnx", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(window) == true)
            {
                if (!Within(picker.FileName, root)) { MessageBox.Show(window, "Choose a target inside the selected source root.", "Programming target", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                target.Text = Path.GetRelativePath(root, picker.FileName);
            }
        };
        target.ToolTip = "Browse or enter a relative project or solution path inside this source root.";
        TargetChanged();
        ProgrammingDevelopmentOptions? selection = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var develop = new Button { Content = "Develop", IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 5, 10, 5) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
        develop.Click += (_, _) =>
        {
            try
            {
                var full = TryFullTarget(root, target.Text) ?? throw new ArgumentException("Select an existing .csproj, .sln or .slnx inside the source root.");
                if (!File.Exists(full)) throw new ArgumentException("The selected build target is unavailable.");
                var required = new List<string>(); if (build.IsChecked == true) required.Add("build"); if (test.IsChecked == true) required.Add("test"); if (run.IsChecked == true) required.Add("run");
                if (required.Count == 0) throw new ArgumentException("Select at least one verification check.");
                if (required.Contains("run") && !Path.GetExtension(full).Equals(".csproj", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Run requires a project; use build/test for a solution.");
                string? presentation = null;
                if (present.IsChecked == true)
                {
                    var value = manifest.Text.Trim();
                    if (value.Length is < 1 or > 4096 || value.Contains('\0') || Path.IsPathRooted(value)) throw new ArgumentException("Choose a bounded relative output manifest path.");
                    var fullManifest = Path.GetFullPath(value, root);
                    if (!Within(fullManifest, root) || !Path.GetFileName(fullManifest).Equals("desktop-plugin.json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The output must be desktop-plugin.json inside the source root.");
                    presentation = Path.GetRelativePath(root, fullManifest);
                }
                selection = new(Path.GetRelativePath(root, full), (string)configuration.SelectedItem, required.ToArray(), presentation);
                window.DialogResult = true;
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { MessageBox.Show(window, error.Message, "Programming target", MessageBoxButton.OK, MessageBoxImage.Information); }
        };
        buttons.Children.Add(develop); buttons.Children.Add(cancel); body.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        using var registration = cancellationToken.Register(() => window.Dispatcher.BeginInvoke(new Action(() => { if (window.IsVisible) window.Close(); })));
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = window.ShowDialog() == true;
        cancellationToken.ThrowIfCancellationRequested();
        return accepted ? selection : null;
    }
    internal static string? TryFullTarget(string root, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\0') || Path.IsPathRooted(value) ||
            Path.GetExtension(value).ToLowerInvariant() is not (".csproj" or ".sln" or ".slnx")) return null;
        try { var full = Path.GetFullPath(value, root); return Within(full, root) ? full : null; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return null; }
    }
    private static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
