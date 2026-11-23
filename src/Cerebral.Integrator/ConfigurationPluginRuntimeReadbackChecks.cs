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
using Cerebral.Desktop.Plugins;

namespace Cerebral.Integrator;

// Public only so the isolated test runner can call without private reflection.
// Normal application startup never calls this diagnostic entry.
public static class ConfigurationPluginRuntimeReadbackChecks
{
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = false };
    public static int Run(string mode) => mode switch
    {
        "protocol" => Protocol().GetAwaiter().GetResult(),
        "managed" => Window(managed: true),
        "self-owned" => Window(managed: false),
        "legacy-owner-protocol" => ConfigurationPluginOwnerReadbackChecks.VerifyProtocol().GetAwaiter().GetResult(),
        _ => throw new ArgumentException("Unknown private check mode.")
    };

    private static async Task<int> Protocol()
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS runtime protocol: " + label); }
        void Refuse(Action work, string label) { var yes = false; try { work(); } catch (InvalidOperationException) { yes = true; } Check(yes, label); }
        var path = Path.Combine(Path.GetTempPath(), "cerebral-runtime-protocol-" + Guid.NewGuid().ToString("N"));
        const string id = "pegasus.root866-recovery-panel";
        var attachment = new ReceiverAttachment("Pegasus.Cerebral.Fixture", new string('a', 32), Path.Combine(path, "worker"), "fixture");
        var expected = new ReceiverExpected(123, "456", attachment, Path.Combine(path, "ui"));
        var descriptor = new ReceiverDescriptor(ConfigurationReadbackProtocol.Product, 1, "fixture", new string('b', 32), expected);
        var request = new ReceiverRequest(descriptor.Product, 1, descriptor.InstanceId, "request1",
            ConfigurationReadbackProtocol.PluginRuntimeOperation, expected, PluginId: id);
        var owner = new ReceiverPluginOwnerSnapshot(ConfigurationReadbackProtocol.PluginOwnerSchema, DateTimeOffset.UtcNow,
            123, "456", attachment, expected.ConfigurationStateDirectory, "managed", new string('c', 64),
            Path.Combine(path, "catalog"), Path.Combine(path, "data"), new(path, path, path, path, false));
        var absent = ConfigurationReadbackProtocol.RuntimeSnapshot(owner, id, null, null);
        var active = ConfigurationReadbackProtocol.RuntimeSnapshot(owner, id, new(id, true, null), (Path.Combine(path, "actual-generation"), "1.0.0"));
        var stopping = ConfigurationReadbackProtocol.RuntimeSnapshot(owner, id, new(id, false, "Stopping the previous native instance"), null);
        var failed = ConfigurationReadbackProtocol.RuntimeSnapshot(owner, id, new(id, false, "An earlier load failed"), null);
        Check(absent.State == "absent" && absent.RuntimeAvailable && absent.Status is null && absent.LoadedPackage is null &&
            absent.Retiring == false && absent.Scope == "current-window-runtime", "actual absent projection has explicit available scope");
        Check(active.State == "active" && active.Status?.Loaded == true && active.LoadedPackage?.Version == "1.0.0" &&
            active.Retiring == false, "loaded package/status projection preserves actual package identity");
        Check(stopping.State == "inactive-with-diagnostic" && stopping.Retiring is null && stopping.Status?.Error == "Stopping the previous native instance",
            "unstructured SDK retiring diagnostic remains unknown and blocks absence");
        Check(failed.State == stopping.State && failed.Retiring is null, "error text is never normalized into a retirement claim");
        Refuse(() => ConfigurationReadbackProtocol.RuntimeSnapshot(owner, id, new(id, true, null), null), "loaded status without actual package is inconsistent");
        Refuse(() => ConfigurationReadbackProtocol.RuntimeSnapshot(owner, id, null, (path, "1.0.0")), "package without actual loaded status is inconsistent");
        Refuse(() => ConfigurationReadbackProtocol.RuntimeSnapshot(owner, id, new("other.plugin", false, null), null), "another target cannot be substituted");

        var snapshotCalls = 0; var pageCalls = 0; var openCalls = 0; var ownerCalls = 0; var runtimeCalls = 0;
        var snapshot = new ReceiverSnapshot("partial", owner.CapturedAtUtc, 123, "456", attachment, expected.ConfigurationStateDirectory,
            false, "pending", false, null, new("cached", false, null, false, null, false, null, null, null, false));
        var page = new ReceiverPageSnapshot("partial", owner.CapturedAtUtc, 123, "456", "ConfigurationTabDesktopPlugins",
            1, false, false, null, null, false, false, false, "panel_closed");
        Task<ReceiverResponse> Send(ReceiverRequest input, ReceiverPluginRuntimeSnapshot? data = null, bool missing = false, bool unavailable = false) =>
            ConfigurationReadbackProtocol.DispatchAsync(input, descriptor,
                _ => { snapshotCalls++; return Task.FromResult(snapshot); },
                _ => { pageCalls++; return Task.FromResult(page); },
                (_, _, _) => { openCalls++; return Task.FromResult(page); },
                _ => { ownerCalls++; return Task.FromResult(owner); },
                missing ? null : (target, _) =>
                {
                    runtimeCalls++;
                    if (unavailable) throw new InvalidOperationException("fixture unavailable");
                    if (target != id) throw new InvalidOperationException("wrong target");
                    return Task.FromResult(data ?? absent);
                }, CancellationToken.None);
        var result = await Send(request);
        Check(result.State == "completed" && result.PluginRuntimeData == absent && result.Data is null &&
            result.PageData is null && result.PluginOwnerData is null, "runtime family is separate from legacy payloads");
        Check(runtimeCalls == 1 && snapshotCalls == 0 && pageCalls == 0 && openCalls == 0 && ownerCalls == 0,
            "runtime dispatch invokes only target capture");
        Check((await Send(request, active)).PluginRuntimeData == active, "active family accepted");
        Check((await Send(request, stopping)).PluginRuntimeData?.Retiring is null, "ambiguous retirement remains explicit over wire");
        var caps = await Send(request with { Operation = "capabilities.inspect", PluginId = null });
        Check(caps.Capabilities?.Operations.Contains(ConfigurationReadbackProtocol.PluginRuntimeOperation) == true &&
            caps.Capabilities.Operations.Contains(ConfigurationReadbackProtocol.PluginOwnerOperation), "optional capability advertised with existing operations preserved");
        async Task Reject(string label, ReceiverRequest value, string code)
        {
            var before = runtimeCalls; var response = await Send(value);
            Check(response.State == "rejected" && response.ErrorCode == code && response.PluginRuntimeData is null && before == runtimeCalls, label);
        }
        await Reject("stale PID rejected", request with { Expected = expected with { ProcessId = 124 } }, "identity_mismatch");
        await Reject("stale start rejected", request with { Expected = expected with { ProcessStartUtcTicks = "457" } }, "identity_mismatch");
        await Reject("stale Worker rejected", request with { Expected = expected with { Attachment = attachment with { InstanceId = new string('d', 32) } } }, "identity_mismatch");
        await Reject("stale UI rejected", request with { Expected = expected with { ConfigurationStateDirectory = path } }, "identity_mismatch");
        await Reject("missing target rejected", request with { PluginId = null }, "plugin_runtime_target_invalid");
        await Reject("invalid target rejected", request with { PluginId = "../other" }, "plugin_runtime_target_invalid");
        await Reject("overlength target rejected", request with { PluginId = new string('x', 65) }, "plugin_runtime_target_invalid");
        await Reject("page command rejected", request with { Page = new("ConfigurationTabSettings", null, "ConfigurationTabSettings", 1) }, "page_arguments_invalid");
        await Reject("legacy operation target input rejected", request with { Operation = "snapshot.inspect" }, "plugin_runtime_arguments_invalid");
        await Reject("unsupported mutation rejected", request with { Operation = "plugin-runtime.reload" }, "operation_not_supported");
        await Reject("wrong protocol rejected", request with { Protocol = 2 }, "protocol_mismatch");
        await Reject("unknown request field rejected", request with { Extra = new() { ["other"] = JsonSerializer.SerializeToElement(true) } }, "request_fields_invalid");
        await Reject("request bound rejected", request with { RequestId = new string('x', 9000) }, "request_too_large");
        var noHandler = await Send(request, missing: true); var noRuntime = await Send(request, unavailable: true);
        Check(noHandler.State == "unavailable" && noRuntime.State == "unavailable" &&
            noRuntime.ErrorCode == "plugin_runtime_unavailable" && noRuntime.PluginRuntimeData is null, "unavailable never becomes absent");
        foreach (var invalid in new[] { absent with { Schema = "other" }, absent with { RuntimeAvailable = false },
            absent with { Owner = owner with { ProcessStartUtcTicks = "999" } }, absent with { Retiring = true },
            absent with { PluginId = "other.plugin" } })
            Check((await Send(request, invalid)).State == "unavailable", "incomplete or substituted runtime DTO rejected");
        var oversized = await Send(request, active with { LoadedPackage = active.LoadedPackage! with { Directory = new string('x', 40000) } });
        Check(oversized.State == "unavailable" && oversized.ErrorCode == "plugin_runtime_response_too_large" &&
            oversized.PluginRuntimeData is null, "oversized package path is rejected without truncation");
        var oversizedError = await Send(request, stopping with { Status = stopping.Status! with { Error = new string('x', 40000) } });
        Check(oversizedError.ErrorCode == "plugin_runtime_response_too_large", "oversized diagnostic has no partial payload");
        foreach (var op in new[] { "snapshot.inspect", "page.inspect", ConfigurationReadbackProtocol.PluginOwnerOperation })
        {
            var old = await Send(request with { Operation = op, PluginId = null });
            var golden = new PreviousResponse(old.Product, old.Protocol, old.InstanceId, old.RequestId, old.Operation,
                old.State, old.Data, old.ErrorCode, old.PageData, old.Capabilities, old.PluginOwnerData);
            Check(JsonSerializer.Serialize(old, Json) == JsonSerializer.Serialize(golden, Json) &&
                !JsonSerializer.Serialize(old, Json).Contains("pluginRuntimeData", StringComparison.Ordinal), op + " retains exact previous response wire");
        }
        var raw = JsonSerializer.Serialize(request, Json);
        Check(raw.Length <= 8192 && raw.Contains("\"pluginId\":\"" + id + "\"", StringComparison.Ordinal), "bounded target is explicit in request wire");
        var duplicate = raw.Replace("\"pluginId\":", "\"pluginId\":\"other.plugin\",\"pluginId\":", StringComparison.Ordinal);
        var duplicateRefused = false;
        try { _ = await ReadRaw<ReceiverRequest>(duplicate); } catch (InvalidDataException) { duplicateRefused = true; }
        Check(duplicateRefused, "strict framing rejects duplicate target");
        var unknown = JsonSerializer.Serialize(result, Json).Replace("\"scope\":", "\"unknown\":true,\"scope\":", StringComparison.Ordinal);
        var unknownRefused = false;
        try { _ = await ReadRaw<ReceiverResponse>(unknown); } catch (InvalidDataException) { unknownRefused = true; }
        Check(unknownRefused, "runtime DTO is closed under existing strict framing");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var cancellationPreserved = false;
            try
            {
                _ = await ConfigurationReadbackProtocol.DispatchAsync(request, descriptor, _ => Task.FromResult(snapshot),
                    null, null, null, (_, _) => Task.FromResult(absent), canceled.Token);
            }
            catch (OperationCanceledException) { cancellationPreserved = true; }
            Check(cancellationPreserved, "pre-dispatch cancellation preserved");
        }
        using (var canceled = new CancellationTokenSource())
        {
            var cancellationPreserved = false;
            try
            {
                _ = await ConfigurationReadbackProtocol.DispatchAsync(request, descriptor, _ => Task.FromResult(snapshot),
                    null, null, null, (_, _) => { canceled.Cancel(); return Task.FromResult(absent); }, canceled.Token);
            }
            catch (OperationCanceledException) { cancellationPreserved = true; }
            Check(cancellationPreserved, "capture cancellation preserved without completed data");
        }
        Check(!Directory.Exists(path), "protocol fixture creates no filesystem state");
        Console.WriteLine($"Runtime protocol: {count} assertions; deterministic supplied data/framing only.");
        return count;
    }

    private sealed record PreviousResponse(string Product, int Protocol, string InstanceId, string? RequestId, string Operation,
        string State, ReceiverSnapshot? Data, string? ErrorCode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReceiverPageSnapshot? PageData = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReceiverPageCapabilities? Capabilities = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReceiverPluginOwnerSnapshot? PluginOwnerData = null);
    private static async Task<T> ReadRaw<T>(string json)
    {
        using var stream = new MemoryStream(); var bytes = new UTF8Encoding(false, true).GetBytes(json);
        stream.Write(BitConverter.GetBytes(bytes.Length)); stream.Write(bytes); stream.Position = 0;
        return await LocalWorkerProtocol.ReadAsync<T>(stream);
    }

    private static int Window(bool managed)
    {
        if (Application.Current is not null) throw new InvalidOperationException("Fresh STA process required.");
        var root = Path.Combine(Path.GetTempPath(), "cerebral-runtime-window-" + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "worker"); var ui = Path.Combine(root, "configuration");
        Directory.CreateDirectory(worker);
        var endpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.Fixture." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var options = ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", endpoint.PipeName, endpoint.InstanceId, worker, ui, "fixture"])!;
        ConfigurationManagedPluginOwnerBinding? binding = null;
        if (managed)
        {
            var mainUi = Path.Combine(root, "main-ui"); var main = options with { DesktopDataDirectory = mainUi };
            binding = ConfigurationManagedPluginOwnerBinding.CreateForMain(main, options, Path.Combine(root, "catalog"),
                Path.Combine(root, "plugin-data"), DesktopInstancePaths.ForManagedPluginOwnerSnapshot(mainUi, root, root, false));
        }
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS runtime window: " + label); }
        Application? app = null; IntegratorWindow? window = null; ConfigurationReadbackServer? server = null;
        using var sentinelLife = new CancellationTokenSource();
        using var sentinel = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var workerConnection = sentinel.WaitForConnectionAsync(sentinelLife.Token);
        try
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            window = IntegratorAttachedWorkerStartup.CreateWindow(options, binding);
            Ui.ApplyTheme(app);
            using var process = Process.GetCurrentProcess();
            var expected = new ReceiverExpected(process.Id, process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                new(endpoint.PipeName, endpoint.InstanceId, worker, "fixture"), DesktopInstancePaths.Current.DataDirectory);
            const string id = "pegasus.root866-recovery-panel";
            var before = Fingerprint(root);
            var direct = window.CaptureReceiverPluginRuntime(expected, id);
            Check(direct.State == "absent" && direct.RuntimeAvailable && direct.Status is null && direct.LoadedPackage is null &&
                direct.Retiring == false, "actual adopted empty runtime is absent");
            Check(direct.Owner.ProcessId == expected.ProcessId && direct.Owner.Attachment == expected.Attachment &&
                direct.Owner.ConfigurationStateDirectory == ui && direct.Owner.Mode == (managed ? "managed" : "self-owned") &&
                direct.Owner.OwnerBindingId == binding?.OwnerBindingId, "actual owner/lifetime fields captured");
            Check(Fingerprint(root) == before && !workerConnection.IsCompleted, "direct observation writes nothing and contacts no Worker");
            foreach (var invalid in new[] { expected with { ProcessId = expected.ProcessId + 1 },
                expected with { ProcessStartUtcTicks = "1" }, expected with { Attachment = expected.Attachment with { InstanceId = Guid.NewGuid().ToString("N") } },
                expected with { ConfigurationStateDirectory = Path.Combine(root, "wrong-ui") } })
            {
                var rejected = false; try { _ = window.CaptureReceiverPluginRuntime(invalid, id); } catch (ConfigurationPageControlRefusal) { rejected = true; }
                Check(rejected, "actual capture refuses stale owner");
            }
            server = window.StartReceiverObservation(options) ?? throw new InvalidOperationException("Private receiver unavailable.");
            var descriptor = server.Descriptor; var baseline = Fingerprint(root);
            ReceiverResponse Call(string op, string? target) => Pump(Task.Run(async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var pipe = new NamedPipeClientStream(".", descriptor.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(deadline.Token);
                await LocalWorkerProtocol.WriteAsync(pipe, new ReceiverRequest(descriptor.Product, descriptor.Protocol, descriptor.InstanceId,
                    Guid.NewGuid().ToString("N"), op, descriptor.Expected, PluginId: target), deadline.Token);
                return await LocalWorkerProtocol.ReadAsync<ReceiverResponse>(pipe, deadline.Token);
            }));
            Check(Call("capabilities.inspect", null).Capabilities?.Operations.Contains(ConfigurationReadbackProtocol.PluginRuntimeOperation) == true,
                "normal private receiver advertises runtime observation");
            var response = Call(ConfigurationReadbackProtocol.PluginRuntimeOperation, id);
            Check(response.State == "completed" && response.PluginRuntimeData?.State == "absent" &&
                response.PluginRuntimeData.Owner == direct.Owner with { CapturedAtUtc = response.PluginRuntimeData.Owner.CapturedAtUtc },
                "normal private receiver returns actual same owner/runtime");
            Check(response.PluginOwnerData is null && response.Data is null && response.PageData is null &&
                ConfigurationReadbackProtocol.Size(response) <= 32768, "runtime wire is separate and bounded");
            Check(Call(ConfigurationReadbackProtocol.PluginOwnerOperation, null).State == "completed" &&
                Call("snapshot.inspect", null).State == "completed" && Call("page.inspect", null).State == "completed",
                "existing read operations still complete");
            Check(Fingerprint(root) == baseline && !workerConnection.IsCompleted, "all receiver reads leave tree/files/Worker untouched");
            Check(!window.IsVisible && !window.IsLoaded && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                "fixture remains unshown, unloaded and without HWND");
            server.DisposeAsync().AsTask().GetAwaiter().GetResult(); server = null;
            window.Close();
            var closed = false; try { _ = window.CaptureReceiverPluginRuntime(expected, id); } catch (ConfigurationPageControlRefusal) { closed = true; }
            Check(closed, "closed lifetime refuses observation"); window = null;
            Console.WriteLine($"Runtime window ({(managed ? "managed" : "self-owned")}): {count} assertions; private unshown WPF/IPC; no product Worker.");
            return count;
        }
        finally
        {
            sentinelLife.Cancel();
            try { server?.DisposeAsync().AsTask().GetAwaiter().GetResult(); window?.Close(); }
            finally
            {
                app?.Shutdown();
                var full = Path.GetFullPath(root);
                if (!ScopedFilePolicy.IsWithin(full, Path.GetFullPath(Path.GetTempPath())) ||
                    !Path.GetFileName(full).StartsWith("cerebral-runtime-window-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Fixture cleanup escaped owned root.");
                Directory.Delete(full, recursive: true);
            }
        }
    }
    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
        var until = DateTime.UtcNow.AddSeconds(10);
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= until) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!task.IsCompleted) throw new TimeoutException("Private fixture deadline.");
        return task.GetAwaiter().GetResult();
    }
    private static string Fingerprint(string root) => string.Join("\n", Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + (Directory.Exists(path) ? "/" : ":" + FileHash(path))));
    private static string FileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
