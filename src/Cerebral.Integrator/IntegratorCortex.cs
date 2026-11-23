using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly DataGrid knowledgeProjects = Ui.Grid(("Project", "Title", 2), ("Folder", "Path", 3), ("ID", "Id", 2));
    private readonly TextBox cortexDetails = Ui.Json();
    private UIElement CortexPage()
    {
        cortexDetails.IsReadOnly = true; cortexDetails.Height = 180;
        var page = new DockPanel();
        var details = Ui.Page("Cortex configuration and diagnostics", "The service connection defines the available Cortex tools and allowed knowledge spaces. Each project chooses a smaller knowledge scope and its own retrieval and approval behavior.", cortexDetails);
        DockPanel.SetDock(details, Dock.Bottom); page.Children.Add(details);
        page.Children.Add(Ui.Page("Cortex project knowledge", "Use your separately installed Pegasus Cortex as your signed-in Windows user. Service access and per-project knowledge behavior remain separate and visible.", knowledgeProjects,
            Ui.Button("Refresh", () => Run(RefreshKnowledgeProjects)),
            Ui.Button("Configure project…", () => Run(ConfigureProjectKnowledge), true),
            Ui.Button("Advanced project spaces…", () => Run(ConfigureProjectKnowledgeAdvanced)),
            Ui.Button("Connect installed Cortex…", () => Run(() => ConnectLocalCortexAutomatically())),
            Ui.Button("Set up native Cortex connection…", () => Run(AddInstalledCortex)),
            Ui.Button("Service access summary", () => Run(ShowCortexServiceAccess)),
            Ui.Button("Advanced service spaces…", () => Run(EditCortexServiceSpacesAdvanced)),
            Ui.Button("Inspect Cortex connection…", () => Run(InspectCortexTools))));
        knowledgeProjects.SelectionChanged += (_, _) => { if (knowledgeProjects.SelectedItem is NavigationNode) Run(InspectProjectKnowledge); };
        return page;
    }
    private async Task RefreshKnowledgeProjects()
    {
        var selected = (knowledgeProjects.SelectedItem as NavigationNode)?.Id;
        var navigation = await Get<NavigationState>("navigation.get");
        knowledgeProjects.ItemsSource = navigation.Nodes.Where(x => x.Kind == "project").ToList();
        knowledgeProjects.SelectedItem = ((IEnumerable<NavigationNode>)knowledgeProjects.ItemsSource).FirstOrDefault(x => x.Id == selected);
    }
    private async Task InspectProjectKnowledge()
    {
        var project = Selected<NavigationNode>(knowledgeProjects);
        var value = await Get<ProjectKnowledgeSettings>("projects.knowledge.get", new { projectId = project.Id });
        cortexDetails.Text = string.IsNullOrWhiteSpace(value.CortexServerId)
            ? "This project has no Cortex connection configured. Its conversations and files still work locally."
            : ProjectKnowledgeSummary(project, value);
    }
    private async Task ConfigureProjectKnowledge()
    {
        var project = Selected<NavigationNode>(knowledgeProjects);
        var host = await Get<HostSettings>("settings.get");
        if (NativeCortexConnections(host.McpServers).Count == 0) throw new InvalidOperationException("Set up the installed native Cortex connection first.");
        if (host.CortexNamespaces.Count == 0) throw new InvalidOperationException("Add at least one service-approved Cortex space under Advanced service spaces.");
        var value = await Get<ProjectKnowledgeSettings>("projects.knowledge.get", new { projectId = project.Id });
        var servers = FriendlyCortexConnections(host.McpServers);
        var selectedServer = servers.FirstOrDefault(x => x.Value == value.CortexServerId).Key ?? servers.Keys.First();
        var startingReads = value.ReadNamespaces.Count > 0 ? value.ReadNamespaces : host.CortexNamespaces;
        var writeChoices = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Do not update retained knowledge"] = null };
        foreach (var item in FriendlyCortexSpaces(host.CortexWriteNamespaces, project)) writeChoices[item.Key] = item.Value;
        var selectedWrite = writeChoices.FirstOrDefault(x => x.Value == value.WriteNamespace).Key ?? writeChoices.Keys.First();
        var form = new FieldsDialog(this, "Cortex · " + project.Title,
            "Choose how this project uses the spaces already allowed by the service. Retrieval adds relevant retained knowledge to new work. Knowledge updates can require your approval. Task continuity stores bounded checkpoints separately.")
            { Height = 720, Width = 790 };
        form.Choice("server", "Cortex connection", servers.Keys, selectedServer);
        var readable = FriendlyCortexSpaces(host.CortexNamespaces, project).ToList();
        for (var index = 0; index < readable.Count; index++)
            form.Check("read:" + index, "Read " + readable[index].Key, startingReads.Contains(readable[index].Value, StringComparer.Ordinal));
        form.Choice("write", "Retained knowledge updates", writeChoices.Keys, selectedWrite);
        form.Check("retrieve", "Retrieve relevant knowledge automatically for new work", value.AutomaticRetrieval);
        form.Check("continuity", "Write bounded task continuity checkpoints automatically", value.AutomaticContinuity);
        form.Check("approve", "Ask before applying proposed changes to retained knowledge", value.RequireKnowledgeApproval);
        if (form.ShowDialog() != true) return;
        value.CortexServerId = servers[form.Selection("server")];
        value.ReadNamespaces = readable.Where((_, index) => form.Checked("read:" + index)).Select(x => x.Value).ToList();
        if (value.ReadNamespaces.Count == 0) throw new ArgumentException("Choose at least one knowledge space to read.");
        value.WriteNamespace = writeChoices[form.Selection("write")];
        value.AutomaticRetrieval = form.Checked("retrieve"); value.AutomaticContinuity = form.Checked("continuity");
        value.RequireKnowledgeApproval = form.Checked("approve");
        if (value.AutomaticContinuity && value.WriteNamespace is null)
            throw new ArgumentException("Automatic task continuity needs a retained knowledge update space.");
        if (value.AutomaticContinuity && !value.ReadNamespaces.Contains(value.WriteNamespace!, StringComparer.Ordinal))
            throw new ArgumentException("The task continuity space must also be selected for reading.");
        value = await Get<ProjectKnowledgeSettings>("projects.knowledge.save", value);
        cortexDetails.Text = ProjectKnowledgeSummary(project, value); SetStatus("Project Cortex settings saved. Knowledge approval remains separate from task continuity.");
    }

    private async Task ConfigureProjectKnowledgeAdvanced()
    {
        var project = Selected<NavigationNode>(knowledgeProjects);
        var host = await Get<HostSettings>("settings.get");
        if (NativeCortexConnections(host.McpServers).Count == 0) throw new InvalidOperationException("Set up the installed native Cortex connection first.");
        var value = await Get<ProjectKnowledgeSettings>("projects.knowledge.get", new { projectId = project.Id });
        var connections = NativeCortexConnections(host.McpServers);
        var selected = connections.Any(item => item.Id == value.CortexServerId) ? value.CortexServerId : connections[0].Id;
        var form = new FieldsDialog(this, "Advanced Cortex spaces · " + project.Title,
            "Use exact namespace identifiers from the service allowlist. This technical view preserves custom Cortex layouts.") { Height = 650, Width = 780 };
        form.Choice("server", "Native Cortex connection ID", connections.Select(x => x.Id), selected)
            .Add("read", "Exact read namespaces, one per line", string.Join(Environment.NewLine, value.ReadNamespaces.Count > 0 ? value.ReadNamespaces : host.CortexNamespaces), true)
            .Add("write", "Exact write namespace (blank disables writes)", value.WriteNamespace ?? "")
            .Check("retrieve", "Retrieve relevant knowledge automatically", value.AutomaticRetrieval)
            .Check("continuity", "Write bounded task continuity checkpoints automatically", value.AutomaticContinuity)
            .Check("approve", "Ask before applying proposed retained-knowledge changes", value.RequireKnowledgeApproval);
        if (form.ShowDialog() != true) return;
        value.CortexServerId = form.Selection("server"); value.ReadNamespaces = form.Lines("read");
        value.WriteNamespace = string.IsNullOrWhiteSpace(form.Value("write")) ? null : form.Value("write");
        value.AutomaticRetrieval = form.Checked("retrieve"); value.AutomaticContinuity = form.Checked("continuity"); value.RequireKnowledgeApproval = form.Checked("approve");
        value = await Get<ProjectKnowledgeSettings>("projects.knowledge.save", value);
        cortexDetails.Text = ProjectKnowledgeSummary(project, value); SetStatus("Advanced project Cortex spaces saved.");
    }

    private async Task ShowCortexServiceAccess()
    {
        var host = await Get<HostSettings>("settings.get");
        var native = FriendlyCortexConnections(host.McpServers);
        var connections = native.Count == 0 ? "No native Cortex connection is configured. Standard MCP providers remain in Tools & MCP." :
            string.Join("\n", native.Keys.Select(x => "• " + x));
        var reads = FriendlyCortexSpaces(host.CortexNamespaces, null).Select(x => "• " + x.Key).DefaultIfEmpty("• No retained knowledge spaces are allowed");
        var writes = FriendlyCortexSpaces(host.CortexWriteNamespaces, null).Select(x => "• " + x.Key).DefaultIfEmpty("• No retained knowledge updates are allowed");
        cortexDetails.Text = $"Service Cortex connections\n{connections}\n\nSpaces projects may read\n{string.Join('\n', reads)}\n\nSpaces projects may update\n{string.Join('\n', writes)}\n\nEach project selects a subset here. Its automatic retrieval, continuity, and approval choices do not widen this service access.";
        SetStatus("Showing service-level Cortex access. Exact namespace identifiers are available under Advanced service spaces.");
    }

    private async Task EditCortexServiceSpacesAdvanced()
    {
        await RefreshSettings();
        var form = new FieldsDialog(this, "Advanced service Cortex spaces", "These exact namespaces are the maximum Cortex scope any project may use. A project can only narrow this list.")
            .Add("read", "Allowed read namespaces, one per line", string.Join(Environment.NewLine, settings.CortexNamespaces), true)
            .Add("write", "Allowed write namespaces, one per line", string.Join(Environment.NewLine, settings.CortexWriteNamespaces), true);
        if (form.ShowDialog() != true) return;
        settings.CortexNamespaces = form.Lines("read"); settings.CortexWriteNamespaces = form.Lines("write");
        await SaveSettings(); await ShowCortexServiceAccess();
    }

    // The legacy settings collection and mcp.* tool aliases also carry native bindings.
    // Route identity comes from the explicit binding, never an ID or executable-name guess.
    private static IReadOnlyList<McpRegistration> NativeCortexConnections(IEnumerable<McpRegistration> connections) =>
        connections.Where(IsNativeCortexConnection).ToArray();

    private static bool IsEligibleFirstRunCortex(McpRegistration connection)
    {
        if (!IsNativeCortexConnection(connection) || !connection.Enabled || connection.RequiresConfiguration ||
            connection.CortexNative is not { } native || !File.Exists(native.HostExecutable)) return false;
        var ownerSid = InstalledWorkerWindows.ReadCurrentProcessIdentity().AccountSid;
        var credential = CortexManagedCredentialLocator.Resolve(native, ownerSid);
        return File.Exists(credential.EffectivePath);
    }

    private static bool IsNativeCortexConnection(McpRegistration connection)
    {
        if (connection.CortexNative is not { } native || !string.IsNullOrEmpty(connection.Url) ||
            connection.Arguments is null || connection.Arguments.Count != 0 ||
            connection.ViaUserConnector != (native.Relay is not null)) return false;
        try { native.Validate(); return true; }
        catch (ArgumentException) { return false; }
    }

    private static Dictionary<string, string> FriendlyCortexConnections(IEnumerable<McpRegistration> connections)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var connection in NativeCortexConnections(connections))
        {
            var basic = "Pegasus Cortex · native pipe · " + connection.Id;
            if (!connection.Enabled) basic += " · disabled";
            if (connection.RequiresConfiguration) basic += " · needs setup";
            var label = basic;
            for (var suffix = 2; result.ContainsKey(label); suffix++) label = $"{basic} ({suffix})";
            result[label] = connection.Id;
        }
        return result;
    }

    private static IEnumerable<KeyValuePair<string, string>> FriendlyCortexSpaces(IEnumerable<string> spaces, NavigationNode? project)
    {
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var space in spaces)
        {
            var basic = space switch
            {
                "knowledge.shared" => "shared Pegasus knowledge",
                "knowledge.user" => "your preferences and working style",
                _ when project is not null && space.Contains(project.Id, StringComparison.Ordinal) => "this project's retained knowledge",
                _ when space.StartsWith("project.", StringComparison.Ordinal) => "project retained knowledge",
                _ => "an additional service-approved knowledge space"
            };
            labels.TryGetValue(basic, out var count); labels[basic] = ++count;
            yield return new(count == 1 ? basic : $"{basic} ({count})", space);
        }
    }

    private static string ProjectKnowledgeSummary(NavigationNode project, ProjectKnowledgeSettings value)
    {
        var reads = FriendlyCortexSpaces(value.ReadNamespaces, project).Select(x => x.Key).ToList();
        var write = value.WriteNamespace is null ? "Retained knowledge updates are disabled." : "Updates use " + FriendlyCortexSpaces([value.WriteNamespace], project).Single().Key + ".";
        return $"Project: {project.Title}\nConnection: configured\nReads: {(reads.Count == 0 ? "none" : string.Join(", ", reads))}\n{write}\n" +
               $"Automatic retrieval: {(value.AutomaticRetrieval ? "on" : "off")}\nTask continuity checkpoints: {(value.AutomaticContinuity ? "on" : "off")}\nKnowledge changes: {(value.RequireKnowledgeApproval ? "ask before applying" : "apply when the agent is granted access")}";
    }

    private async Task<bool> ConnectLocalCortexAutomatically(bool optionalFirstRun = false)
    {
        if (ExistingWorkerAttachment is not null)
            throw new InvalidOperationException("An attached Configuration window cannot enroll a new Cortex client. Use the installed owner Configuration window.");
        if (!IsConnected || ServiceConnection.Transport is not ("pipe" or "installed-pipe" or "installed-user-pipe"))
            throw new InvalidOperationException("Connect to this computer's current Cerebral Worker before setting up Cortex.");
        var ownerClient = Client;
        var ownerEpoch = queueConnectionEpoch;
        var ownerTransport = ServiceConnection.Transport;
        var ownerEndpoint = ServiceConnection.LocalEndpoint;
        var ownerData = ServiceConnection.LocalDataDirectory;
        bool Current() => IsConnected && ReferenceEquals(Client, ownerClient) && queueConnectionEpoch == ownerEpoch &&
            ServiceConnection.Transport == ownerTransport && ServiceConnection.LocalEndpoint == ownerEndpoint &&
            ServiceConnection.LocalDataDirectory == ownerData;
        void RequireCurrent()
        {
            if (!Current()) throw new InvalidOperationException("The Cerebral Worker connection changed during Cortex setup. Inspect the original enrollment attempt; no new backend was updated.");
        }
        async Task<T> ReadOwner<T>(string operation, object? arguments = null)
        {
            RequireCurrent();
            var value = await ownerClient.GetAsync<T>(operation, arguments, cancellationToken: Lifetime.Token);
            RequireCurrent();
            return value;
        }
        async Task SaveOwner(HostSettings value)
        {
            RequireCurrent();
            var result = await ownerClient.CommandAsync("settings.save", value, cancellationToken: Lifetime.Token);
            RequireCurrent();
            if (!result.Success) throw new InvalidOperationException(result.Error ?? "The original Worker refused the Cortex connection update.");
        }

        var host = await ReadOwner<HostSettings>("settings.get");
        var existing = host.McpServers.Where(connection =>
            connection.Id.Equals("cortex", StringComparison.OrdinalIgnoreCase) ||
            connection.CortexNative is not null || connection.CortexNativeRequired).ToArray();
        if (existing.Length != 0)
        {
            if (existing.Length == 1 && IsEligibleFirstRunCortex(existing[0]))
            {
                var report = await ReadOwner<CortexConnectionReport>("cortex.connection.inspect", new { serverId = existing[0].Id });
                if (report.State != "ready" || report.Transport != "cortex-native-pipe" || report.NativeIdentity is null)
                    throw new InvalidOperationException("The existing native Cortex binding did not pass live inspection. Review it explicitly; automatic enrollment will not replace or rotate it.");
                SetStatus("The existing native Cortex connection is ready and was preserved.");
                return true;
            }
            throw new InvalidOperationException("An existing Cortex route needs review. Automatic enrollment will not replace, repair, or reissue it.");
        }
        FirstRunSetupCoordinator.RequireDirectOwnerTransport(ownerTransport);
        var instance = DesktopInstancePaths.Current;
        var workerPath = ManagementProcess.WorkerPath;
        if (instance.Portable)
        {
            var expectedWorker = Path.Combine(instance.InstallDirectory, "Applications", "Worker", "Cerebral.Worker.exe");
            if (!PortablePackageAdmission.IsValidatedAt(instance.InstallDirectory) ||
                !workerPath.Equals(expectedWorker, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cortex enrollment requires the verified current portable package and its exact Worker executable.");
        }
        else if (!ManagementProcess.UsesRegisteredInstallation)
            throw new InvalidOperationException("Cortex enrollment requires the current installed or verified portable Cerebral Worker executable.");
        if (!Path.GetFullPath(workerPath).StartsWith(instance.InstallDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected Worker executable is outside the current Cerebral package root.");

        CortexFirstRunEnrollmentPlan plan;
        try { plan = CortexFirstRunEnrollment.Prepare(workerPath); }
        catch (Exception error) when (optionalFirstRun && error is
            (IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or PlatformNotSupportedException))
        {
            SetStatus("Installed Cortex automatic setup is unavailable: " + error.Message +
                " Cerebral setup can continue without Cortex; review the installation in Cortex configuration.");
            return false;
        }
        var pending = CortexFirstRunEnrollment.FindUnresolvedAttempt(plan);
        if (pending is not null)
        {
            var message = $"Cortex enrollment attempt {pending.AttemptId} for this Worker is {pending.State}. Review it before any new registration.";
            throw new InvalidOperationException(message);
        }
        if (File.Exists(plan.CredentialFile) || Directory.Exists(plan.CredentialFile))
        {
            if (optionalFirstRun)
            {
                SetStatus("Cortex already has a Cerebral credential file. Review its existing client and connection; no new enrollment was started.");
                return false;
            }
            throw new InvalidOperationException("Cortex already has a Cerebral credential file. Review its existing client and connection before explicit repair.");
        }

        if (host.McpServers.Any(connection =>
            connection.Executable.Equals(plan.CortexExecutablePath, StringComparison.OrdinalIgnoreCase)))
        {
            if (optionalFirstRun)
            {
                SetStatus("An existing Cortex executable route needs review. First-run agent setup will continue without adding a connection.");
                return false;
            }
            throw new InvalidOperationException("A Cortex executable route already exists under another connection ID. Review it before automatic enrollment.");
        }
        McpRegistration registration;
        try
        {
            if (ownerEndpoint is null || string.IsNullOrWhiteSpace(ownerData))
                throw new InvalidOperationException("The current local Worker pipe has no exact endpoint or data root.");
            await LocalDevelopment.RequireExactPipeWorkerAsync(ownerEndpoint, ownerData,
                plan.WorkerExecutablePath, Lifetime.Token);
            RequireCurrent();
            registration = new McpRegistration
            {
                Id = "cortex", Executable = plan.CortexExecutablePath, Arguments = [], Enabled = true,
                ViaUserConnector = false, CortexNativeRequired = true,
                CortexNative = new(plan.HostExecutablePath, plan.CredentialFile, plan.SessionId)
            };
        }
        catch (Exception error) when (optionalFirstRun && error is
            (IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException))
        {
            SetStatus("Cortex connection preflight needs review: " + error.Message +
                " First-run agent setup can continue without Cortex.");
            return false;
        }

        if (!Confirm($"Connect this Cerebral Worker to the installed Cortex?\n\n" +
            $"Cortex: {plan.ReleaseVersion}\nWorker: {plan.WorkerExecutablePath}\n\n" +
            "Cortex will create one protected, app-scoped credential for this exact Worker. " +
            "Cerebral will use this verified signed-in Worker directly; no connector credential or relay will be created. " +
            "Existing agent, project, namespace and PAO grants will not change. " +
            "Cortex routing defaults are empty suggestions, not a namespace permission limit. " +
            "A failed or uncertain enrollment requires explicit review; Cerebral will not retry or rotate it automatically."))
            return false;

        // No provider mutation precedes the user's explicit confirmation.
        var current = await ReadOwner<HostSettings>("settings.get");
        if (current.Revision != host.Revision)
            throw new InvalidOperationException("Cerebral settings changed during review. Open setup again before enrollment.");
        await LocalDevelopment.RequireExactPipeWorkerAsync(ownerEndpoint!, ownerData!,
            plan.WorkerExecutablePath, Lifetime.Token);
        RequireCurrent();
        if (instance.Portable) PortablePackageAdmission.VerifyAt(instance.InstallDirectory);
        RequireCurrent();
        var enrollment = await CortexFirstRunEnrollment.RegisterAsync(plan, async (launch, token) =>
        {
            await Dispatcher.InvokeAsync(RequireCurrent, System.Windows.Threading.DispatcherPriority.Send, token);
            await LocalDevelopment.RequireExactPipeWorkerAsync(ownerEndpoint!, ownerData!,
                plan.WorkerExecutablePath, token).ConfigureAwait(false);
            if (instance.Portable) PortablePackageAdmission.VerifyAt(instance.InstallDirectory);
            await Dispatcher.InvokeAsync(() =>
            {
                RequireCurrent();
                launch();
            }, System.Windows.Threading.DispatcherPriority.Send, token);
        }, Lifetime.Token);
        if (enrollment.ClientId != plan.ClientId ||
            !enrollment.CredentialFile.Equals(plan.CredentialFile, StringComparison.OrdinalIgnoreCase))
            throw new CortexEnrollmentUncertainException();
        var phase = "recheck the original Cerebral Worker";
        try
        {
            RequireCurrent();
            await LocalDevelopment.RequireExactPipeWorkerAsync(ownerEndpoint!, ownerData!,
                plan.WorkerExecutablePath, Lifetime.Token);
            RequireCurrent();
            registration.CortexNative = new(plan.HostExecutablePath, enrollment.CredentialFile, plan.SessionId);
            current.McpServers.Add(registration);
            phase = "save the native connection";
            await SaveOwner(current);
            phase = "inspect the native connection";
            RequireCurrent();
            await RefreshSettings();
            RequireCurrent();
            var report = await ReadOwner<CortexConnectionReport>("cortex.connection.inspect", new { serverId = "cortex" });
            if (report.State != "ready" || report.Transport != "cortex-native-pipe" ||
                report.NativeIdentity?.ClientId != plan.ClientId)
                throw new InvalidOperationException("The new native connection did not pass live identity inspection: " + report.Message);
            SetStatus("Installed Cortex connection is ready. Agent and project grants were preserved.");
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            throw new InvalidOperationException("Cortex enrollment was created, but Cerebral connection setup did not complete. " +
                "Inspect the existing Cortex client and Worker connection before explicit repair; do not automatically register or rotate again. " +
                "Stopped at: " + phase + ".");
        }
    }

    private async Task AddInstalledCortex()
    {
        if (!IsConnected || ServiceConnection.Transport != "pipe" && (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var origin) || !origin.IsLoopback))
            throw new InvalidOperationException("Set up the signed-in user's Cortex provider from Integrator on the coordinator computer.");
        var host = await Get<HostSettings>("settings.get");
        var known = InstalledCortexDiscovery.FindInstalled();
        var existing = host.McpServers.FirstOrDefault(x => x.Id == "cortex")
            ?? host.McpServers.FirstOrDefault(x => known.Contains(x.Executable, StringComparer.OrdinalIgnoreCase));
        var executable = existing?.Executable ?? known.FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(executable))
        {
            var picker = new OpenFileDialog { Title = "Choose your installed Pegasus Cortex executable", Filter = "Pegasus Cortex|PegasusCortex.exe" };
            if (picker.ShowDialog(this) != true) return;
            executable = picker.FileName;
        }
        var profiles = await Get<List<AgentProfile>>("agents.list");
        var connector = await UserConnectorProcess.Manager.StatusAsync(Lifetime.Token);
        var relay = UserConnectorProcess.Manager.CreateCortexRelayBinding(ManagementProcess.WorkerPath);
        var nativeHost = Path.Combine(Path.GetDirectoryName(executable)!, "bin", "host", "PegasusCortex.Host.exe");
        var existingCredential = existing?.CortexNative is { } savedNative
            ? CortexManagedCredentialLocator.Resolve(savedNative, InstalledWorkerWindows.ReadCurrentProcessIdentity().AccountSid)
            : null;
        var credentialLocationNote = existingCredential?.ManagedClientId is not null
            ? " The saved key reference currently resolves to " + existingCredential.EffectivePath + ". The saved reference is retained."
            : "";
        var form = new FieldsDialog(this, "Set up native Cortex connection",
            "Use Cortex's resident local pipe in your signed-in Windows session. Enroll Cerebral with Cortex first, then choose its protected client key file. This does not create credentials or grant root access. Applying setup replaces this Cortex connection's MCP route; existing agent and project permissions are preserved unless you select additional grants below." + credentialLocationNote,
            "Apply setup") { Height = 780, Width = 840 };
        form.Add("path", "Installed PegasusCortex.exe", executable);
        form.ActionButton("Choose installed executable…", dialog =>
        {
            var picker = new OpenFileDialog { Title = "Choose your installed Pegasus Cortex executable", Filter = "Pegasus Cortex|PegasusCortex.exe" };
            if (picker.ShowDialog(dialog) == true) dialog.SetValue("path", picker.FileName);
        });
        form.Add("id", "Connection ID", existing?.Id ?? "cortex");
        form.Add("host", "Installed Cortex resident host", existing?.CortexNative?.HostExecutable ?? nativeHost);
        form.Add("credential", "Cortex-enrolled protected client key file", existing?.CortexNative?.CredentialFile ?? "");
        form.ActionButton("Choose enrolled key file…", dialog =>
        {
            var picker = new OpenFileDialog { Title = "Choose the protected client key file returned by Cortex enrollment", Filter = "Cortex protected key|*.key" };
            if (picker.ShowDialog(dialog) == true) dialog.SetValue("credential", picker.FileName);
        });
        form.Choice("access", "Additive grants for checked agents", ["Keep existing grants", "Search and typed reads", "Knowledge and task updates"], "Search and typed reads");
        form.Check("pao", "Grant PAO typed execution (saved agent capabilities still apply; when Cerebral Full Access is enabled, execution is carried by the private Cortex system-service session)", true);
        foreach (var profile in profiles)
            form.Check("profile:" + profile.Id, profile.Name + " · " + FriendlyAgentRole(profile.Role), profile.Role == "lead");
        form.Check("connector", "Provision and start the independent connector; restart it if running", true);
        if (form.ShowDialog() != true) return;

        executable = InstalledCortexDiscovery.ValidateExplicitPath(form.Value("path"));
        var serverId = form.Value("id");
        if (serverId.Length is < 1 or > 64 || serverId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw new ArgumentException("Connection ID must use at most 64 letters, digits, hyphens or underscores.");
        var endpoint = ConnectorProtocol.ValidateUrl(connector.Url).AbsoluteUri; // Existing connector management only; Cortex calls use pipes.
        var native = new CortexNativeConnection(form.Value("host"), form.Value("credential"), relay.SessionId, relay);
        native.Validate();
        if (!native.HostExecutable.Equals(Path.Combine(Path.GetDirectoryName(executable)!, "bin", "host", "PegasusCortex.Host.exe"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the resident host from this exact Cortex installation's bin\\host folder.");
        var credentialLocation = CortexManagedCredentialLocator.Resolve(native, InstalledWorkerWindows.ReadCurrentProcessIdentity().AccountSid);
        if (!File.Exists(native.HostExecutable) || !File.Exists(credentialLocation.EffectivePath))
            throw new InvalidOperationException("Cortex's resident host or enrolled protected key is missing. Complete Cortex native setup first; no MCP fallback will be used.");
        // Re-read before applying so unrelated settings or grants changed during the review are preserved.
        host = await Get<HostSettings>("settings.get");
        var registration = host.McpServers.SingleOrDefault(x => x.Id == serverId);
        if (registration is not null && !string.Equals(Path.GetFullPath(registration.Executable), executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This connection ID already has a different executable or route. Existing registration was preserved. Review it in Tools & MCP or choose another ID.");
        var selectedIds = profiles.Where(x => form.Checked("profile:" + x.Id)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var currentProfiles = await Get<List<AgentProfile>>("agents.list");
        if (selectedIds.Any(id => currentProfiles.All(x => x.Id != id)))
            throw new InvalidOperationException("A selected agent was removed during review. Open setup again before applying grants.");
        var applyConnector = form.Checked("connector");
        if (applyConnector)
        {
            connector = await UserConnectorProcess.Manager.StatusAsync(Lifetime.Token);
            if (connector.State is not ("not-configured" or "stopped" or "running"))
                throw new InvalidOperationException("Resolve connector status before setup: " + connector.Message);

        }
        registration ??= new McpRegistration { Id = serverId, Executable = executable };
        registration.Enabled = true;
        registration.RequiresConfiguration = false;
        registration.CortexNativeRequired = true;
        registration.Arguments = []; registration.Url = null; registration.ViaUserConnector = true; registration.CortexNative = native;
        if (!host.McpServers.Contains(registration)) host.McpServers.Add(registration);
        var configuration = applyConnector ? UserConnectorManager.CreateConfiguration(host.McpServers, endpoint, connector.State == "not-configured" ? null : connector.Url) : null;
        if (configuration is not null)
        {
            if (string.IsNullOrEmpty(host.AccessKey)) host.AccessKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            UserConnectorManager.ValidateProvisioning(configuration, host.AccessKey);
        }
        await Command("settings.save", host);
        var grantMode = form.Selection("access");
        var grantPao = form.Checked("pao");
        if (grantMode != "Keep existing grants" || grantPao)
            foreach (var profile in currentProfiles.Where(x => selectedIds.Contains(x.Id)))
            {
                if (grantMode != "Keep existing grants")
                {
                    AddCortexGrant(profile.Capabilities, "mcp." + serverId);
                    if (grantMode == "Knowledge and task updates") AddCortexGrant(profile.Capabilities, "cortex.write");
                    IReadOnlyList<string> names = grantMode == "Knowledge and task updates" ? CortexConnectionContract.CoreTools : ["cortex_search", "cortex_read"];
                    foreach (var name in names)
                        if (!profile.AllowedTools.Contains("*", StringComparer.Ordinal)) AddCortexGrant(profile.AllowedTools, $"mcp.{serverId}.{name}");
                }
                if (grantPao)
                {
                    AddCortexGrant(profile.Capabilities, "mcp." + serverId);
                    if (!profile.AllowedTools.Contains("*", StringComparer.Ordinal)) AddCortexGrant(profile.AllowedTools, $"mcp.{serverId}.pao");
                }
                await Command("agents.save", profile);
            }
        if (configuration is not null)
        {
            try
            {
                if (connector.State == "running")
                {
                    var stopped = await UserConnectorProcess.Manager.StopAsync(Lifetime.Token);
                    if (stopped.State != "stopped") throw new InvalidOperationException(stopped.Message);
                }
                await UserConnectorProcess.Manager.ProvisionAsync(configuration, host.AccessKey, Lifetime.Token);
                await UserConnectorProcess.StartAsync(Lifetime.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RefreshSettings(); await RefreshAgents(); await RefreshConnectorAsync();
                throw new InvalidOperationException("The Cortex registration and selected grants were saved, but connector setup did not complete. Review Connector status, then rerun setup or provision/start there. " + ex.Message);
            }
        }
        await RefreshSettings(); await RefreshAgents(); await RefreshConnectorAsync();
        await InspectCortexConnection(serverId);
    }

    private static void AddCortexGrant(List<string> grants, string value)
    {
        if (!grants.Contains(value, StringComparer.Ordinal)) grants.Add(value);
    }

    private async Task InspectCortexTools()
    {
        var host = await Get<HostSettings>("settings.get");
        var connections = NativeCortexConnections(host.McpServers);
        if (connections.Count == 0) throw new InvalidOperationException("Set up the installed native Cortex connection first.");
        var project = knowledgeProjects.SelectedItem as NavigationNode;
        var bound = project is null ? null : (await Get<ProjectKnowledgeSettings>("projects.knowledge.get", new { projectId = project.Id })).CortexServerId;
        var selected = connections.Any(x => x.Id == bound) ? bound! : connections.FirstOrDefault(x => x.Id == "cortex")?.Id ?? connections[0].Id;
        var form = new FieldsDialog(this, "Inspect Cortex connection", "Read-only owner diagnostic: checks the selected transport, authenticated identity, operation catalog and cortex_status. A model or lead profile is not required.", "Inspect")
            { Height = 340, Width = 740 };
        form.Choice("server", "Native Cortex connection", connections.Select(x => x.Id), selected);
        if (form.ShowDialog() == true) await InspectCortexConnection(form.Selection("server"));
    }

    private async Task InspectCortexConnection(string serverId)
    {
        var host = await Get<HostSettings>("settings.get");
        if (!NativeCortexConnections(host.McpServers).Any(item => item.Id == serverId))
            throw new InvalidOperationException("This connection no longer has an explicit native Cortex pipe binding. Set it up under Cortex; no MCP fallback was used.");
        var report = await Get<CortexConnectionReport>("cortex.connection.inspect", new { serverId });
        cortexDetails.Text = Ui.Serialize(report);
        SetStatus(report.State + ": " + report.Message);
    }
}
