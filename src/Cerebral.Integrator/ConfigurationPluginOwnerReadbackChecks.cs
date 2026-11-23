using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

internal static class ConfigurationPluginOwnerReadbackChecks
{
    private static readonly JsonSerializerOptions Compact = new(CerebralJson.Options) { WriteIndented = false };

    internal static async Task<int> VerifyProtocol()
    {
        var checks = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("Owner readback protocol: " + label);
            checks++; Console.WriteLine("PASS owner readback protocol: " + label);
        }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-owner-protocol-" + Guid.NewGuid().ToString("N"));
        var attachment = new ReceiverAttachment("Pegasus.Cerebral.Fixture", new string('a', 32), Path.Combine(root, "worker"), "fixture");
        var expected = new ReceiverExpected(123, "456", attachment, Path.Combine(root, "ui"));
        var descriptor = new ReceiverDescriptor(ConfigurationReadbackProtocol.Product, 1, "fixture", new string('b', 32), expected);
        var request = new ReceiverRequest(descriptor.Product, 1, descriptor.InstanceId, "request1",
            ConfigurationReadbackProtocol.PluginOwnerOperation, expected);
        var owner = new ReceiverPluginOwnerSnapshot(ConfigurationReadbackProtocol.PluginOwnerSchema,
            DateTimeOffset.Parse("2026-10-27T00:00:00Z"), expected.ProcessId, expected.ProcessStartUtcTicks,
            attachment, expected.ConfigurationStateDirectory, "managed", new string('c', 64),
            Path.Combine(root, "catalog"), Path.Combine(root, "data"),
            new(Path.Combine(root, "main"), Path.Combine(root, "main"), Path.Combine(root, "install"), root, true));
        var snapshot = new ReceiverSnapshot("partial", owner.CapturedAtUtc, 123, "456", attachment,
            expected.ConfigurationStateDirectory, false, "pending", false, null,
            new("cached", false, null, false, null, false, null, null, null, false));
        var page = new ReceiverPageSnapshot("partial", owner.CapturedAtUtc, 123, "456", "ConfigurationTabSettings",
            1, false, false, null, null, false, false, false, "panel_closed");
        var ownerCalls = 0; var snapshotCalls = 0; var pageCalls = 0; var openCalls = 0;
        Task<ReceiverResponse> Send(ReceiverRequest supplied, ReceiverPluginOwnerSnapshot? suppliedOwner = null,
            bool unavailable = false, bool missingHandler = false) =>
            ConfigurationReadbackProtocol.DispatchAsync(supplied, descriptor,
                _ => { snapshotCalls++; return Task.FromResult(snapshot); },
                _ => { pageCalls++; return Task.FromResult(page); },
                (_, _, _) => { openCalls++; return Task.FromResult(page); },
                missingHandler ? null : _ =>
                {
                    ownerCalls++;
                    if (unavailable) throw new InvalidOperationException("fixture unavailable");
                    return Task.FromResult(suppliedOwner ?? owner);
                }, CancellationToken.None);

        var result = await Send(request);
        Check(result.State == "completed" && result.PluginOwnerData == owner && result.Data is null &&
            result.PageData is null && result.Capabilities is null, "separate versioned owner DTO is returned");
        Check(ownerCalls == 1 && snapshotCalls == 0 && pageCalls == 0 && openCalls == 0,
            "owner read dispatch invokes only its capture");
        var json = JsonSerializer.Serialize(result, Compact);
        Check(!json.Contains("projectsDirectory", StringComparison.Ordinal) &&
            !json.Contains("modelsDirectory", StringComparison.Ordinal) && !json.Contains("sharedDirectory", StringComparison.Ordinal),
            "dynamic paths and settings content are excluded");
        var capabilities = await Send(request with { Operation = "capabilities.inspect" });
        Check(capabilities.Capabilities?.Operations.Contains(ConfigurationReadbackProtocol.PluginOwnerOperation) == true,
            "operation is separately advertised");

        async Task Refuse(string label, ReceiverRequest invalid, string error)
        {
            var before = ownerCalls;
            var rejected = await Send(invalid);
            Check(rejected.State == "rejected" && rejected.ErrorCode == error && rejected.PluginOwnerData is null &&
                before == ownerCalls, label + " is refused before capture");
        }
        await Refuse("stale PID", request with { Expected = expected with { ProcessId = 124 } }, "identity_mismatch");
        await Refuse("stale start", request with { Expected = expected with { ProcessStartUtcTicks = "457" } }, "identity_mismatch");
        await Refuse("stale Worker", request with { Expected = expected with { Attachment = attachment with { InstanceId = new string('d', 32) } } }, "identity_mismatch");
        await Refuse("stale UI root", request with { Expected = expected with { ConfigurationStateDirectory = Path.Combine(root, "other") } }, "identity_mismatch");
        await Refuse("wrong schema version", request with { Protocol = 2 }, "protocol_mismatch");
        await Refuse("wrong product", request with { Product = "other" }, "protocol_mismatch");
        await Refuse("stale receiver", request with { InstanceId = new string('e', 32) }, "generation_mismatch");
        await Refuse("unsupported operation", request with { Operation = "plugin-owner.remove" }, "operation_not_supported");
        await Refuse("page input on owner read", request with { Page = new("ConfigurationTabSettings", null, "ConfigurationTabSettings", 1) }, "page_arguments_invalid");
        await Refuse("unknown field", request with { Extra = new() { ["extra"] = JsonSerializer.SerializeToElement(true) } }, "request_fields_invalid");
        await Refuse("oversized request", request with { Extra = new() { ["extra"] = JsonSerializer.SerializeToElement(new string('x', 9000)) } }, "request_too_large");
        var unavailable = await Send(request, unavailable: true);
        Check(unavailable.State == "unavailable" && unavailable.ErrorCode == "plugin_owner_unavailable" &&
            unavailable.PluginOwnerData is null && unavailable.Data is null, "unavailable capture has no echo fallback");
        var absent = await Send(request, missingHandler: true);
        Check(absent.State == "unavailable" && absent.ErrorCode == "plugin_owner_unavailable", "missing capture is explicit");
        var invalidOwner = await Send(request, owner with { Schema = "unknown" });
        Check(invalidOwner.State == "unavailable" && invalidOwner.PluginOwnerData is null, "unknown owner DTO schema is refused");
        var missing = await Send(request, owner with { PluginDataRoot = "" });
        Check(missing.State == "unavailable" && missing.PluginOwnerData is null, "missing actual storage is not substituted");
        var wrongCapture = await Send(request, owner with { ProcessStartUtcTicks = "999" });
        Check(wrongCapture.State == "unavailable" && wrongCapture.PluginOwnerData is null, "capture cannot substitute another lifetime");
        var tooLarge = await Send(request, owner with { CatalogRoot = Path.Combine(root, new string('x', 40000)) });
        Check(tooLarge.State == "unavailable" && tooLarge.ErrorCode == "plugin_owner_response_too_large" &&
            tooLarge.PluginOwnerData is null, "oversized paths produce an error without truncation");
        Check(ConfigurationReadbackProtocol.Size(tooLarge) <= ConfigurationReadbackProtocol.MaximumResponseBytes,
            "oversize error remains bounded");

        foreach (var op in new[] { "snapshot.inspect", "page.inspect" })
        {
            var legacy = await Send(request with { Operation = op });
            var golden = new LegacyReceiverResponse(legacy.Product, legacy.Protocol, legacy.InstanceId,
                legacy.RequestId, legacy.Operation, legacy.State, legacy.Data, legacy.ErrorCode, legacy.PageData, legacy.Capabilities);
            Check(JsonSerializer.Serialize(legacy, Compact) == JsonSerializer.Serialize(golden, Compact) &&
                !JsonSerializer.Serialize(legacy, Compact).Contains("pluginOwnerData", StringComparison.Ordinal),
                op + " retains the exact old response wire shape");
        }
        var raw = JsonSerializer.Serialize(request, Compact);
        raw = raw.Replace("\"operation\":\"plugin-owner.inspect\"", "\"operation\":\"plugin-owner.inspect\",\"operation\":\"snapshot.inspect\"", StringComparison.Ordinal);
        var duplicateRefused = false;
        try { _ = await ReadRaw<ReceiverRequest>(raw); }
        catch (InvalidDataException) { duplicateRefused = true; }
        Check(duplicateRefused, "framing rejects duplicate request fields");
        var extraDto = json.Replace("\"schema\":\"pegasus.configuration-plugin-owner/1\"",
            "\"schema\":\"pegasus.configuration-plugin-owner/1\",\"unknown\":true", StringComparison.Ordinal);
        var closedDto = false;
        try { _ = await ReadRaw<ReceiverResponse>(extraDto); }
        catch (InvalidDataException) { closedDto = true; }
        Check(closedDto, "owner DTO is closed under normal strict framing");
        Check(!Directory.Exists(root), "protocol checks create no filesystem state");
        Console.WriteLine($"Owner readback protocol: {checks} assertions passed; deterministic delegates/framing only.");
        return checks;
    }

    private sealed record LegacyReceiverResponse(string Product, int Protocol, string InstanceId, string? RequestId,
        string Operation, string State, ReceiverSnapshot? Data, string? ErrorCode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReceiverPageSnapshot? PageData = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReceiverPageCapabilities? Capabilities = null);

    private static async Task<T> ReadRaw<T>(string json)
    {
        using var stream = new MemoryStream();
        var bytes = new UTF8Encoding(false, true).GetBytes(json);
        stream.Write(BitConverter.GetBytes(bytes.Length)); stream.Write(bytes); stream.Position = 0;
        return await LocalWorkerProtocol.ReadAsync<T>(stream);
    }

    internal static int VerifyManaged() => VerifyCapture(managed: true);
    internal static int VerifySelfOwned() => VerifyCapture(managed: false);

    private static int VerifyCapture(bool managed)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Use a fresh private STA fixture process.");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-owner-capture-" + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "worker"); var ui = Path.Combine(root, "configuration");
        var mainUi = Path.Combine(root, "main");
        Directory.CreateDirectory(worker);
        var endpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.Fixture." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var options = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", endpoint.PipeName, endpoint.InstanceId,
            worker, ui, "fixture-installation"])!;
        ConfigurationManagedPluginOwnerBinding? binding = null;
        if (managed)
        {
            var main = options with { DesktopDataDirectory = mainUi };
            var paths = DesktopInstancePaths.ForManagedPluginOwnerSnapshot(mainUi, Path.Combine(root, "main-install"), root, true);
            binding = ConfigurationManagedPluginOwnerBinding.CreateForMain(main, options, Path.Combine(root, "actual-catalog"),
                Path.Combine(root, "actual-plugin-data"), paths);
        }
        var checks = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("Owner capture: " + label);
            checks++; Console.WriteLine("PASS owner capture: " + label);
        }
        Application? app = null; IntegratorWindow? window = null; ConfigurationReadbackServer? server = null;
        using var workerLife = new CancellationTokenSource();
        using var unexpectedWorker = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var workerConnection = unexpectedWorker.WaitForConnectionAsync(workerLife.Token);
        try
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            window = IntegratorAttachedWorkerStartup.CreateWindow(options, binding);
            Ui.ApplyTheme(app);
            using var process = Process.GetCurrentProcess();
            var attachment = window.ExistingWorkerAttachment!;
            var expected = new ReceiverExpected(process.Id, process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                new(attachment.Endpoint.PipeName, attachment.Endpoint.InstanceId, attachment.WorkerDataDirectory, attachment.InstallationId),
                DesktopInstancePaths.Current.DataDirectory);
            var before = Fingerprint(root);
            var actual = window.CaptureReceiverPluginOwner(expected);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var appliedCatalog = (Cerebral.Desktop.Plugins.DesktopPluginCatalog)typeof(IntegratorWindow).GetField("desktopCatalog", flags)!.GetValue(window)!;
            var appliedData = (string)typeof(IntegratorWindow).GetField("desktopPluginDataRoot", flags)!.GetValue(window)!;
            Check(actual.CatalogRoot == appliedCatalog.RootDirectory && actual.PluginDataRoot == appliedData,
                "capture reads actual window storage fields");
            Check(actual.ProcessId == process.Id && actual.ProcessStartUtcTicks == expected.ProcessStartUtcTicks &&
                actual.CapturedAtUtc != default && actual.Attachment == expected.Attachment,
                "capture reports actual lifetime and fixed Worker");
            Check(actual.ConfigurationStateDirectory == ui && actual.Mode == (managed ? "managed" : "self-owned") &&
                actual.OwnerBindingId == binding?.OwnerBindingId, "actual UI root and owner mode are separate from managed roots");
            var projection = (Cerebral.Desktop.Plugins.DesktopProductPaths)typeof(IntegratorWindow).GetMethod("ConfigurationProductPaths", flags)!.Invoke(window, null)!;
            Check(actual.ProductPaths == new ReceiverPluginOwnerBasePaths(projection.DataDirectory, projection.UserDataDirectory,
                projection.InstallDirectory, projection.BaseDirectory, projection.Portable), "capture reports actual base product projection");
            Check(actual.ProductPaths.UserDataDirectory == (managed ? mainUi : ui), "mode-specific product owner is applied");
            Check(Fingerprint(root) == before && !workerConnection.IsCompleted, "direct capture changes no files and opens no Worker pipe");

            foreach (var invalid in new[]
            {
                expected with { ProcessId = expected.ProcessId + 1 },
                expected with { ProcessStartUtcTicks = "1" },
                expected with { Attachment = expected.Attachment with { InstanceId = Guid.NewGuid().ToString("N") } },
                expected with { ConfigurationStateDirectory = Path.Combine(root, "other-ui") }
            })
            {
                var refused = false;
                try { _ = window.CaptureReceiverPluginOwner(invalid); }
                catch (ConfigurationPageControlRefusal) { refused = true; }
                Check(refused, "direct capture refuses stale lifetime/Worker/UI identity");
            }

            server = window.StartReceiverObservation(options) ?? throw new InvalidOperationException("Private receiver unavailable.");
            var descriptor = server.Descriptor;
            var baseline = Fingerprint(root);
            ReceiverResponse Call(string operation, ReceiverExpected? supplied = null) => Pump(Task.Run(async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var pipe = new NamedPipeClientStream(".", descriptor.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(deadline.Token);
                await LocalWorkerProtocol.WriteAsync(pipe, new ReceiverRequest(descriptor.Product, descriptor.Protocol,
                    descriptor.InstanceId, Guid.NewGuid().ToString("N"), operation, supplied ?? descriptor.Expected), deadline.Token);
                return await LocalWorkerProtocol.ReadAsync<ReceiverResponse>(pipe, deadline.Token);
            }));
            var caps = Call("capabilities.inspect");
            Check(caps.Capabilities?.Operations.Contains(ConfigurationReadbackProtocol.PluginOwnerOperation) == true,
                "normal receiver advertises owner inspection");
            var response = Call(ConfigurationReadbackProtocol.PluginOwnerOperation);
            Check(response.State == "completed" && response.PluginOwnerData is { } captured &&
                captured.CatalogRoot == actual.CatalogRoot && captured.PluginDataRoot == actual.PluginDataRoot &&
                captured.ProductPaths == actual.ProductPaths && captured.Attachment == actual.Attachment &&
                captured.ConfigurationStateDirectory == ui, "normal private pipe returns actual applied fields");
            Check(response.Data is null && response.PageData is null &&
                ConfigurationReadbackProtocol.Size(response) <= ConfigurationReadbackProtocol.MaximumResponseBytes,
                "owner response is separate and bounded");
            var stale = Call(ConfigurationReadbackProtocol.PluginOwnerOperation,
                descriptor.Expected with { ProcessStartUtcTicks = "1" });
            Check(stale.State == "rejected" && stale.ErrorCode == "identity_mismatch" && stale.PluginOwnerData is null,
                "normal receiver refuses a stale process start");
            var oldSnapshot = Call("snapshot.inspect"); var oldPage = Call("page.inspect");
            Check(oldSnapshot.State == "completed" && oldSnapshot.PluginOwnerData is null &&
                oldPage.State == "completed" && oldPage.PluginOwnerData is null,
                "normal legacy reads retain their original payload families");
            Check(Fingerprint(root) == baseline && !workerConnection.IsCompleted,
                "receiver reads create no files, refresh no Worker and leave fixture state intact");
            Check(!window.IsVisible && !window.IsLoaded && new WindowInteropHelper(window).Handle == IntPtr.Zero &&
                !window.ServiceConnected, "fixture remains unshown with no normal Worker startup");
            server.DisposeAsync().AsTask().GetAwaiter().GetResult(); server = null;
            window.Close();
            var closed = false;
            try { _ = window.CaptureReceiverPluginOwner(expected); }
            catch (ConfigurationPageControlRefusal) { closed = true; }
            Check(closed, "closed lifetime cannot return an owner projection");
            window = null;
            Console.WriteLine($"Owner capture ({(managed ? "managed" : "self-owned")}): {checks} assertions passed; private unshown WPF/pipe, no product Worker.");
            return checks;
        }
        finally
        {
            workerLife.Cancel();
            try { server?.DisposeAsync().AsTask().GetAwaiter().GetResult(); window?.Close(); }
            finally
            {
                app?.Shutdown();
                var full = Path.GetFullPath(root);
                if (!ScopedFilePolicy.IsWithin(full, Path.GetFullPath(Path.GetTempPath())) ||
                    !Path.GetFileName(full).StartsWith("cerebral-owner-capture-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Capture fixture cleanup escaped its owned root.");
                Directory.Delete(full, recursive: true);
            }
        }
    }

    private static T Pump<T>(Task<T> task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
        var until = DateTime.UtcNow.AddSeconds(10);
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= until) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!task.IsCompleted) throw new TimeoutException("Private receiver fixture timed out.");
        return task.GetAwaiter().GetResult();
    }

    private static string FileHash(string path)
    {
        // The receiver descriptor deliberately retains its publication lease. Permit
        // that existing writer while taking a read-only content fingerprint.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Fingerprint(string root)
    {
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) +
                (Directory.Exists(path) ? "/" : ":" + FileHash(path)));
        return string.Join("\n", entries);
    }
}
