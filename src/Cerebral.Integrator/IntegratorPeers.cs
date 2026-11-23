using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly DataGrid peerGrants = Ui.Grid(("Peer", "Name", 2), ("Agents", "Agents", 3),
        ("Limit", "Limit", .7), ("State", "State", 1), ("Expires", "Expires", 1.4));

    private async Task RefreshPeerGrants()
    {
        var grants = await Get<List<PeerGrantSummary>>("peer.grants.list");
        var profiles = await Get<List<AgentProfile>>("agents.list");
        var names = profiles.ToDictionary(x => x.Id, x => x.Name, StringComparer.Ordinal);
        peerGrants.ItemsSource = grants.Select(grant => new PeerGrantRow(grant,
            string.Join(", ", grant.SharedProfileIds.Select(id => names.GetValueOrDefault(id, id))))).ToList();
    }

    private async Task ImportProvisionedWorker(string? invitationPath = null)
    {
        if (invitationPath is null)
        {
            var picker = new OpenFileDialog
            {
                Title = "Import a Pegasus Cerebral Worker invitation",
                Filter = $"Cerebral Worker invitation (*{WorkerProvisioningPackage.Extension})|*{WorkerProvisioningPackage.Extension}",
                CheckFileExists = true,
                Multiselect = false
            };
            if (picker.ShowDialog(this) != true) return;
            invitationPath = picker.FileName;
        }
        invitationPath = Path.GetFullPath(invitationPath);
        var package = WorkerProvisioningPackage.Parse(await File.ReadAllBytesAsync(invitationPath, Lifetime.Token));
        var session = await BeginRegistrationEditAsync();
        var existing = session.Settings.Workers.SingleOrDefault(item => item.Id == package.WorkerId);
        if (existing is not null && (!existing.RequiresConfiguration ||
            !string.Equals(existing.Url, package.Url, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A different active Worker already uses this invitation's ID. Remove or rename that connection before importing it.");

        var replacementKey = existing?.AccessKey;
        if (string.IsNullOrWhiteSpace(replacementKey))
            replacementKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pending = package.ToPendingRegistration(replacementKey);
        session.Settings.Workers.RemoveAll(item => item.Id == pending.Id);
        session.Settings.Workers.Add(pending);
        await CommitRegistrationEditAsync(session);

        var controllerId = session.Settings.InstallationId;
        var controllerName = session.Settings.Name;
        WorkerManagementStatus? remote = null;
        try
        {
            // This is the recovery path for a claim whose reply was lost: the replacement key was saved
            // locally before the remote mutation, so Configuration can finish without reissuing secrets.
            using var replacementClient = CerebralClient.ForOwner(Ready(pending));
            remote = await replacementClient.GetAsync<WorkerManagementStatus>("worker.management.status",
                cancellationToken: Lifetime.Token);
        }
        catch (HttpRequestException) { }

        if (remote is null || remote.State != "assigned")
        {
            var bootstrap = Ready(pending);
            bootstrap.AccessKey = package.BootstrapKey;
            using var bootstrapClient = CerebralClient.ForOwner(bootstrap);
            remote = await bootstrapClient.GetAsync<WorkerManagementStatus>("worker.management.claim", new
            {
                provisioningId = package.ProvisioningId,
                controllerInstallationId = controllerId,
                controllerName,
                replacementManagementKey = replacementKey
            }, cancellationToken: Lifetime.Token);
        }
        RequireClaim(remote, package, controllerId);

        using (var replacementClient = CerebralClient.ForOwner(Ready(pending)))
        {
            var confirmed = await replacementClient.GetAsync<WorkerManagementStatus>("worker.management.status",
                cancellationToken: Lifetime.Token);
            RequireClaim(confirmed, package, controllerId);
        }

        var activation = await BeginRegistrationEditAsync();
        var activated = package.ToPendingRegistration(replacementKey);
        activated.RequiresConfiguration = false;
        activation.Settings.Workers.RemoveAll(item => item.Id == activated.Id);
        activation.Settings.Workers.Add(activated);
        await CommitRegistrationEditAsync(activation);
        await RefreshSettings();

        var removed = false;
        try { File.Delete(invitationPath); removed = !File.Exists(invitationPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        SetStatus(removed
            ? $"Worker '{package.Name}' is assigned and its one-time invitation was consumed."
            : $"Worker '{package.Name}' is assigned. Delete the imported invitation because it contains the expired bootstrap credential.");
    }

    private async Task ConfigureRemoteWorker()
    {
        var registration = Selected<WorkerRegistration>(workers);
        if (registration.RequiresConfiguration) throw new InvalidOperationException("Finish importing or reviewing this Worker before configuring it.");
        if (string.IsNullOrWhiteSpace(registration.AccessKey))
            throw new InvalidOperationException("This connection exposes peer capacity only. Import its provisioning invitation to manage the Worker.");
        var owner = await BeginRegistrationEditAsync();
        using var client = CerebralClient.ForOwner(registration);
        var management = await client.GetAsync<WorkerManagementStatus>("worker.management.status", cancellationToken: Lifetime.Token);
        if (management.State != "assigned" || management.ControllerInstallationId != owner.Settings.InstallationId)
            throw new UnauthorizedAccessException("This Worker is not assigned to the current Cerebral installation.");
        var remote = await client.GetAsync<HostSettings>("settings.get", cancellationToken: Lifetime.Token);
        var form = new FieldsDialog(this, "Worker settings · " + registration.Name,
            "These settings are saved on the assigned Worker. Connection, service and firewall changes remain explicit provisioning or recovery actions.", "Save")
            { Height = 640 };
        form.Add("name", "Worker name", remote.Name)
            .Add("llamaServer", "llama.cpp executable path on this Worker", remote.LlamaServerPath)
            .Add("concurrency", "Simultaneous jobs", remote.MaxConcurrentJobs.ToString())
            .Add("peerCapacity", "Maximum unfinished peer jobs", remote.PeerQueueCapacity.ToString())
            .Add("ownerSlots", "Slots reserved for owner work", remote.ReservedOwnerSlots.ToString())
            .Add("processorMaximum", "Maximum logical processors (0 = no additional maximum)", remote.MaxLogicalProcessorCount.ToString())
            .Add("processors", "Allowed logical processors (blank = all except 0)", string.Join(", ", remote.AllowedLogicalProcessors.Order()))
            .Add("memory", "Memory to keep free for the system (MiB)", remote.ReservedMemoryMb.ToString())
            .Add("modelIdle", "Default model idle unload minutes (0 = never)", remote.DefaultModelIdleMinutes.ToString())
            .Add("history", "Maximum completed jobs shown", remote.JobHistoryLimit.ToString())
            .Add("days", "Days of completed jobs shown", remote.JobHistoryDays.ToString());
        if (form.ShowDialog() != true) return;
        remote.Name = form.Value("name");
        // The path is interpreted and validated on the selected Worker, never by Windows here.
        remote.LlamaServerPath = form.Value("llamaServer");
        remote.MaxConcurrentJobs = form.Number("concurrency", 1, 16);
        remote.PeerQueueCapacity = form.Number("peerCapacity", 1, 1000);
        remote.ReservedOwnerSlots = form.Number("ownerSlots", 0, remote.MaxConcurrentJobs);
        remote.MaxLogicalProcessorCount = form.Number("processorMaximum", 0, 1024);
        remote.AllowedLogicalProcessors = ParseLogicalProcessors(form.Value("processors"));
        remote.ReservedMemoryMb = form.Number("memory", 0, 1_048_576);
        remote.DefaultModelIdleMinutes = form.Number("modelIdle", 0, 10_080);
        remote.JobHistoryLimit = form.Number("history", 1, 10_000);
        remote.JobHistoryDays = form.Number("days", 1, 3650);
        var saved = await client.GetAsync<HostSettings>("settings.save", remote, cancellationToken: Lifetime.Token);

        var local = await BeginRegistrationEditAsync();
        var savedRegistration = local.Settings.Workers.Single(item => item.Id == registration.Id);
        savedRegistration.Name = saved.Name;
        await CommitRegistrationEditAsync(local);
        await RefreshSettings();
        SetStatus("Worker settings saved. Processor-affinity changes take effect after the Worker service restarts.");
    }

    private static WorkerRegistration Ready(WorkerRegistration value) => new()
    {
        Id = value.Id, Name = value.Name, Url = value.Url, AccessKey = value.AccessKey,
        PeerToken = value.PeerToken, RequiresConfiguration = false
    };

    private static void RequireClaim(WorkerManagementStatus status, WorkerProvisioningPackage package,
        string controllerInstallationId)
    {
        if (status.WorkerInstallationId != package.WorkerInstallationId || status.State != "assigned" ||
            status.ControllerInstallationId != controllerInstallationId)
            throw new InvalidDataException("The Worker claim response does not match this invitation and Cerebral installation.");
    }

    private async Task CreatePeerGrant()
    {
        var profiles = (await Get<List<AgentProfile>>("agents.list"))
            .Where(profile => profile.WorkerId == "local" && profile.ModelWorkerId == "local")
            .OrderBy(profile => profile.Role).ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (profiles.Count == 0) throw new InvalidOperationException("Configure at least one local agent before sharing this machine.");
        var choices = UniqueProfileChoices(profiles);
        var form = new FieldsDialog(this, "Share local agents", "Select receiver-owned agents and an upper concurrency limit. The generated token is shown once; send it directly to the other owner and paste it into that machine's Worker connection.", "Create & copy token")
            { Height = 560 };
        form.Add("name", "Peer name", "Paired Cerebral")
            .MultiChoice("profiles", "Agents available to this peer", choices.Keys, Array.Empty<string>())
            .Add("maximum", "Maximum simultaneous peer jobs", "1");
        if (form.ShowDialog() != true) return;
        var selected = form.Selections("profiles");
        if (selected.Count == 0) throw new ArgumentException("Select at least one agent to share.");
        var issued = await Get<PeerGrantToken>("peer.grants.create", new
        {
            name = form.Value("name"), profileIds = selected.Select(label => choices[label]).ToList(),
            maxConcurrentJobs = form.Number("maximum", 1, settings.MaxConcurrentJobs)
        });
        ShowIssuedPeerToken(issued, "Peer access created");
        await RefreshPeerGrants();
    }

    private async Task RotatePeerGrant()
    {
        var grant = SelectedPeerGrant();
        if (!Confirm($"Rotate the token for '{grant.Name}'? The previous token will stop working immediately.")) return;
        var issued = await Get<PeerGrantToken>("peer.grants.rotate", new { id = grant.Id });
        ShowIssuedPeerToken(issued, "Peer token rotated");
        await RefreshPeerGrants();
    }

    private async Task TogglePeerGrantDrain()
    {
        var grant = SelectedPeerGrant();
        await Command("peer.grants.drain", new { id = grant.Id, draining = !grant.Draining });
        await RefreshPeerGrants();
        SetStatus(grant.Draining ? "Peer grant resumed; new assignments are accepted." : "Peer grant paused; existing assignments remain available.");
    }

    private async Task RevokePeerGrant()
    {
        var grant = SelectedPeerGrant();
        if (!Confirm($"Revoke '{grant.Name}'? Its token will stop immediately and its active assignments will be cancelled.")) return;
        await Command("peer.grants.revoke", new { id = grant.Id });
        await RefreshPeerGrants();
        SetStatus("Peer grant revoked.");
    }

    private PeerGrantSummary SelectedPeerGrant() => peerGrants.SelectedItem is PeerGrantRow row
        ? row.Grant : throw new InvalidOperationException("Select a peer grant first.");

    private void ShowIssuedPeerToken(PeerGrantToken issued, string title)
    {
        Clipboard.SetText(issued.Token);
        Ui.ShowText(this, title, "The token was copied to the clipboard. It is shown only in this window and is not stored by the receiving service.\n\n" + issued.Token);
        SetStatus(title + ". Paste the copied token into the other Cerebral installation.");
    }

    private static Dictionary<string, string> UniqueProfileChoices(IEnumerable<AgentProfile> profiles)
    {
        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            var basic = $"{profile.Name} · {FriendlyAgentRole(profile.Role)}";
            var label = basic;
            for (var suffix = 2; choices.ContainsKey(label); suffix++) label = $"{basic} ({suffix})";
            choices[label] = profile.Id;
        }
        return choices;
    }

    private static string FormatWorkerProbe(WorkerProbeResult probe)
    {
        var metrics = probe.Metrics;
        var text = new StringBuilder()
            .AppendLine($"{probe.Host.Name} · {probe.Host.OperatingSystem} / {probe.Host.Architecture}")
            .AppendLine($"Pegasus Cerebral {probe.Host.Version} · protocol {probe.Host.Protocol}")
            .AppendLine($"Measured command round trip: {probe.RoundTripLatencyMilliseconds:N0} ms")
            .AppendLine()
            .AppendLine($"Worker process: {metrics.Resources.ProcessMemoryMb:N0} MiB")
            .AppendLine($"System available: {(metrics.Resources.SystemAvailableMb is { } available ? available.ToString("N0") + " MiB" : "unavailable")}")
            .AppendLine($"Queue: owner {metrics.Queue.OwnerRunning} running / {metrics.Queue.OwnerQueued} waiting · peer {metrics.Queue.PeerRunning} running / {metrics.Queue.PeerQueued} waiting")
            .AppendLine($"Execution slots: {metrics.Queue.MaximumSlots} total · {metrics.Queue.ReservedOwnerSlots} reserved while owner work waits");
        foreach (var gpu in metrics.Resources.Gpus)
            text.AppendLine().AppendLine(gpu.Name)
                .AppendLine($"  Memory: {gpu.FreeMb?.ToString("N0") ?? "unavailable"} / {gpu.TotalMb?.ToString("N0") ?? "unavailable"} MiB free")
                .AppendLine($"  Utilization: {(gpu.UtilizationPercent is { } utilization ? utilization + "%" : "unavailable")}{(gpu.Unified ? " · unified memory" : "")}");
        if (metrics.Resources.Gpus.Count == 0) text.AppendLine().AppendLine("GPU telemetry is unavailable on this Worker.");
        if (probe.Catalog is { } catalog)
        {
            text.AppendLine().AppendLine()
                .AppendLine($"Available through this pairing: {catalog.Agents.Count} agent(s) · {catalog.Models.Count} model(s)");
            foreach (var model in catalog.Models.OrderBy(model => model.Name, StringComparer.CurrentCultureIgnoreCase))
                text.AppendLine($"  {model.Name} · {model.State} · {model.Architecture} / {model.Quantization} · {model.ContextTokens:N0} context tokens");
            if (catalog.Models.Count == 0)
                text.AppendLine("  No model is currently exposed by the remote owner's grant.");
        }
        return text.ToString().TrimEnd();
    }

    private sealed record PeerGrantRow(PeerGrantSummary Grant, string Agents)
    {
        public string Id => Grant.Id;
        public string Name => Grant.Name;
        public int Limit => Grant.MaxConcurrentJobs;
        public string State => Grant.RevokedAt is not null ? "Revoked" : Grant.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow ? "Expired" : Grant.Draining ? "Paused" : "Active";
        public string Expires => Grant.ExpiresAt?.ToLocalTime().ToString("g") ?? "No expiry";
    }
}
