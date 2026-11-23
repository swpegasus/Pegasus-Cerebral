using System.Text.Json;
using System.Net;
using System.Windows.Automation;
using Cerebral.Core;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private int CheckModelWorkerLibrary()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        var previousSettings = settings; var previousWorker = loadedModelWorkerId;
        var privateEndpoint = WorkerNetworkRoute.ParseEndpoint("http://user:hidden@10.255.255.15:2668/api?token=hidden");
        Check(privateEndpoint.Display == "http://10.255.255.15:2668" &&
              privateEndpoint.Address?.ToString() == "10.255.255.15",
            "Network selectors must show the configured authority without leaking URL credentials, paths or queries.");
        Check(WorkerNetworkRoute.ParseEndpoint("not a URL").Diagnostic is not null &&
              WorkerNetworkRoute.ParseEndpoint("file:///tmp/worker").Diagnostic is not null,
            "Malformed or unsupported endpoints must remain explicit and must not invoke route inspection.");
        Check(WorkerNetworkRoute.ParseEndpoint("https://worker.example.test").Address is null &&
              WorkerNetworkRoute.ParseEndpoint("https://worker.example.test").Diagnostic!.Contains("Hostname", StringComparison.Ordinal),
            "Hostname endpoints must not trigger DNS or pretend to identify a numeric subnet.");
        Check(WorkerNetworkRoute.ParseEndpoint("http://127.0.0.1:2667").Diagnostic!.Contains("Loopback", StringComparison.Ordinal) &&
              WorkerNetworkRoute.ParseEndpoint("http://[::1]:2667").Diagnostic!.Contains("Loopback", StringComparison.Ordinal),
            "Loopback endpoints must remain distinct from a remote subnet.");
        Check(WorkerNetworkRoute.Subnet(IPAddress.Parse("10.255.255.25"), 24) == "10.255.255.0/24" &&
              WorkerNetworkRoute.Subnet(IPAddress.Parse("10.255.255.25"), 16) == "10.255.0.0/16",
            "Local subnet display must use the actual prefix rather than assume /24.");
        Check(WorkerNetworkRoute.Subnet(IPAddress.Parse("10.255.255.25"), 0) == "0.0.0.0/0" &&
              WorkerNetworkRoute.Subnet(IPAddress.Parse("10.255.255.25"), 32) == "10.255.255.25/32",
            "Zero and host-only prefixes must remain correct.");
        Check(WorkerNetworkRoute.Subnet(IPAddress.Parse("fd00:1234:5678::25"), 64) == "fd00:1234:5678::/64" &&
              WorkerNetworkRoute.Contains(IPAddress.Parse("fd00:1234:5678::25"), 64, IPAddress.Parse("fd00:1234:5678::15")),
            "IPv6 subnet selection must use its own address family and prefix.");
        Check(WorkerNetworkRoute.Contains(IPAddress.Parse("10.255.255.25"), 24, IPAddress.Parse("10.255.255.15")) &&
              !WorkerNetworkRoute.Contains(IPAddress.Parse("10.10.10.25"), 24, IPAddress.Parse("10.255.255.15")) &&
              !WorkerNetworkRoute.Contains(IPAddress.Parse("10.255.255.25"), 24, IPAddress.Parse("fd00::15")),
            "On-link display must not confuse Wi-Fi, Ethernet or different address families.");
        try { WorkerNetworkRoute.Subnet(IPAddress.Parse("10.255.255.25"), 33); throw new InvalidOperationException("Invalid prefix accepted."); }
        catch (ArgumentOutOfRangeException) { count++; }
        var socket4 = WorkerNetworkRoute.SocketAddressBytes(IPAddress.Parse("10.255.255.15"));
        Check(socket4.Length == 16 && BitConverter.ToUInt16(socket4) == 2 &&
              socket4.Skip(4).Take(4).SequenceEqual(new byte[] { 10, 255, 255, 15 }),
            "IPv4 route lookup must pass the native sockaddr family and network-order address.");
        var socket6 = WorkerNetworkRoute.SocketAddressBytes(IPAddress.Parse("fe80::15%7"));
        Check(socket6.Length == 28 && BitConverter.ToUInt16(socket6) == 23 &&
              socket6.Skip(8).Take(16).SequenceEqual(IPAddress.Parse("fe80::15").GetAddressBytes()) &&
              BitConverter.ToUInt32(socket6, 24) == 7,
            "IPv6 route lookup must retain the scope and sockaddr layout.");
        var wiredRoute = WorkerNetworkRoute.Format(new("Ethernet", "10.255.255.0/24", 2_500_000_000, true));
        Check(wiredRoute.Contains("10.255.255.0/24", StringComparison.Ordinal) &&
              wiredRoute.Contains("adapter link", StringComparison.Ordinal) &&
              wiredRoute.Contains("on-link", StringComparison.Ordinal) &&
              wiredRoute.Contains("reachability", StringComparison.Ordinal),
            "Link rate must remain adapter evidence, not worker availability or measured throughput.");
        Check(WorkerNetworkRoute.Format(new("Wi-Fi", "10.10.10.0/24", 0, false))
                .Contains("worker may be on another subnet", StringComparison.Ordinal),
            "A routed destination must not be labeled as belonging to the local interface subnet.");
        var previousModels = configuredModels; var previousRows = models.ItemsSource;
        try
        {
            settings = new HostSettings
            {
                DefaultModelId = "same-id", DefaultModelWorkerId = "managed",
                Workers =
                [
                    new() { Id = "managed", Name = "Managed", AccessKey = "owner-fixture", PeerToken = "peer-fixture" },
                    new() { Id = "peer", Name = "Peer only", PeerToken = "peer-fixture" },
                    new() { Id = "pending", Name = "Pending", AccessKey = "owner-fixture", RequiresConfiguration = true }
                ]
            };
            RefreshModelWorkerChoices();
            var choices = (IEnumerable<ModelWorkerChoice>)modelWorkerPicker.ItemsSource;
            Check(choices.Select(x => x.Id).SequenceEqual(["local", "managed"]),
                "The model library must list only local and ready owner-management connections, never a peer-only or pending connection.");
            Check(AutomationProperties.GetAutomationId(modelWorkerPicker) == "ModelLibraryWorker" &&
                SelectedModelWorkerId == "local", "The machine picker needs a stable ID and an explicit local default.");
            Check(AutomationProperties.GetAutomationId(modelWorkerNetwork) == "ModelLibraryNetwork" &&
                  modelWorkerNetwork.Text.Contains("local worker pipe", StringComparison.Ordinal),
                "The selected machine must expose its network evidence without using a network connection.");
            changingModelWorkerChoices = true;
            try { modelWorkerPicker.SelectedValue = "managed"; } finally { changingModelWorkerChoices = false; }
            Check(modelWorkerNetwork.Text.Contains("Worker Managed", StringComparison.Ordinal) &&
                  modelWorkerNetwork.Text.Contains("Edit the worker", StringComparison.Ordinal),
                "Changing the selected worker must update its network diagnostic even while service refresh is suppressed.");
            settings.Workers[0].Url = "http://user:hidden@10.255.255.15:2668/path?token=hidden";
            RefreshModelWorkerChoices();
            Check(SelectedModelWorkerId == "managed" &&
                  ((ModelWorkerChoice)modelWorkerPicker.SelectedItem).Name.Contains("10.255.255.15:2668", StringComparison.Ordinal) &&
                  !((ModelWorkerChoice)modelWorkerPicker.SelectedItem).Name.Contains("hidden", StringComparison.Ordinal) &&
                  !modelWorkerNetwork.Text.Contains("hidden", StringComparison.Ordinal),
                "A changed endpoint must keep the stable worker selection and omit all credential-bearing URL fields.");
            var machineChoices = AgentModelMachineChoices(settings, "managed");
            Check(machineChoices.Single(x => x.Value == "managed").Key.StartsWith("Managed · http://10.255.255.15:2668", StringComparison.Ordinal) &&
                  !machineChoices.Keys.Any(x => x.Contains("hidden", StringComparison.Ordinal)),
                "Agent machine choices must use display names and sanitized endpoints, without leaking credentials.");
            var defaultProfile = new AgentProfile { FollowsDefaultModel = true, ModelId = "old-default", ModelWorkerId = "local" };
            var defaultRow = CreateAgentRow(defaultProfile);
            Check(defaultRow.Model == "same-id" && defaultRow.Binding == "Follows default" &&
                  defaultRow.InferenceMachine == "Managed" && defaultProfile.ModelId == "old-default" &&
                  defaultProfile.ModelWorkerId == "local",
                "The list must resolve the current default inference machine without mutating a stale saved profile.");
            var pinnedRow = CreateAgentRow(new AgentProfile { ModelId = "other-model", ModelWorkerId = "managed" });
            Check(pinnedRow.Model == "other-model" && pinnedRow.Binding == "Pinned" && pinnedRow.InferenceMachine == "Managed",
                "Pinned bindings must show the registered inference machine separately from the exact model.");
            settings.Workers.Add(new() { Id = "managed-copy", Name = "Managed", Url = settings.Workers[0].Url, AccessKey = "owner-fixture" });
            var duplicateChoices = AgentModelMachineChoices(settings, "managed");
            Check(duplicateChoices.Count == 3 && duplicateChoices.Values.Distinct(StringComparer.Ordinal).Count() == 3 &&
                  duplicateChoices.Any(x => x.Value == "managed-copy" && x.Key.EndsWith("(2)", StringComparison.Ordinal)),
                "Duplicate display names and endpoints must remain selectable by distinct, stable worker IDs.");
            settings.Workers.RemoveAll(x => x.Id == "managed-copy");
            var model = new ModelRecord { Id = "same-id", Name = "Model" };
            var profiles = new[]
            {
                new AgentProfile { Id = "default", FollowsDefaultModel = true, ModelId = "same-id", ModelWorkerId = "managed" },
                new AgentProfile { Id = "local", FollowsDefaultModel = false, ModelId = "same-id", ModelWorkerId = "local" },
                new AgentProfile { Id = "remote", FollowsDefaultModel = false, ModelId = "same-id", ModelWorkerId = "managed" }
            };
            Check(ProfilesUsingModel(model, profiles, settings.DefaultModelId, "local").Select(x => x.Id).SequenceEqual(["local"]),
                "A same-ID remote model must not be counted as a local model assignment.");
            Check(ProfilesUsingModel(model, profiles, settings.DefaultModelId, "managed").Count() == 2,
                "Remote default and pinned assignments must count on their actual worker.");
            loadedModelWorkerId = "local"; configuredModels = [model];
            RefreshDefaultModelPicker();
            Check(defaultModelSummary.Text.Contains("Managed", StringComparison.Ordinal) &&
                !defaultModelSummary.Text.Contains("Context per request", StringComparison.Ordinal),
                "The default summary must not show a same-ID local model's capacity for a remote default.");
            var args = CerebralJson.Element(WorkerModelArguments("managed", new { id = "same-id" }));
            Check(args.GetProperty("workerId").GetString() == "managed" && args.GetProperty("id").GetString() == "same-id" &&
                args.EnumerateObject().Count() == 2, "Coordinator model arguments must contain only the selected IDs.");
            try { WorkerModelArguments("managed", new { workerId = "other" }); throw new InvalidOperationException("Duplicate worker was accepted."); }
            catch (ArgumentException) { count++; }
            models.ItemsSource = new[] { new ModelRow(model, false) }; models.SelectedIndex = 0;
            changingModelWorkerChoices = true;
            try { modelWorkerPicker.SelectedValue = "managed"; } finally { changingModelWorkerChoices = false; }
            try { SelectedModel(); throw new InvalidOperationException("A stale machine selection was accepted."); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Wait for the selected machine", StringComparison.Ordinal)) { count++; }
        }
        finally
        {
            settings = previousSettings; loadedModelWorkerId = previousWorker; configuredModels = previousModels;
            models.ItemsSource = previousRows; RefreshModelWorkerChoices(); RefreshDefaultModelPicker();
        }
        return count;
    }
}
