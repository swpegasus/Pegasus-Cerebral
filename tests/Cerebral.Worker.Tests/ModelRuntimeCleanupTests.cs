using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class ModelRuntimeCleanupTests
{
    internal const string FixtureEnvironment = "CEREBRAL_MODEL_RUNTIME_EXIT_FIXTURE";

    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-model-runtime-cleanup-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var assertions = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            assertions++;
            Console.WriteLine("PASS: " + label);
        }

        var store = new StateStore(Path.Combine(root, "data"));
        var configuration = new Configuration(store);
        var events = new EventHub();
        var runtime = new ModelRuntime(store, configuration.Get, events);
        IDisposable? busyLease = null;
        var previousFixtureEnvironment = Environment.GetEnvironmentVariable(FixtureEnvironment);
        try
        {
            var host = AppHost();
            var settings = configuration.Get();
            settings.LlamaServerPath = host;
            settings.ReservedMemoryMb = 0;
            settings.DefaultModelIdleMinutes = 0;
            configuration.Save(settings);

            var modelPath = Path.Combine(root, "fixture.gguf");
            await File.WriteAllBytesAsync(modelPath, "GGUF"u8.ToArray());
            var configured = new ModelRecord
            {
                Id = "dead-fixture",
                Name = "Owner configured dead-process fixture",
                Path = modelPath,
                SizeBytes = 4,
                State = "unloaded",
                Error = null,
                ContextTokens = 2048,
                MaximumContextTokens = 2048,
                ParallelRequests = 1,
                GpuLayers = 0,
                IdleMinutes = 0,
                AutomaticContextSizing = false,
                Purpose = "Preserve this owner field",
                Tags = ["fixture-owner-tag"],
                DeclaredType = "model",
                MetadataSource = "fixture"
            };
            store.Put("model", configured.Id, configured);

            var signal = Path.Combine(root, "exit.signal");
            Environment.SetEnvironmentVariable(FixtureEnvironment, signal);
            try { _ = await runtime.LoadAsync(configured.Id, CancellationToken.None); }
            finally { Environment.SetEnvironmentVariable(FixtureEnvironment, previousFixtureEnvironment); }
            Check(runtime.IsLoaded(configured.Id) && store.Get<ModelRecord>("model", configured.Id)!.State == "loaded",
                "fixture starts one real managed executable health-server child and records the runtime loaded without performing inference");

            var instance = CurrentInstance(runtime, configured.Id);
            var busy = instance.GetType().GetField("Busy", BindingFlags.Public | BindingFlags.Instance)!;
            var slots = (LlamaSlotPool)instance.GetType().GetField("Slots", BindingFlags.Public | BindingFlags.Instance)!.GetValue(instance)!;
            busy.SetValue(instance, 1);
            busyLease = await slots.AcquireAsync(() => busy.SetValue(instance, (int)busy.GetValue(instance)! - 1), CancellationToken.None);
            store.Put("job", "accepted-work", new AgentJob
            {
                Id = "accepted-work",
                SessionId = "fixture-session",
                ProfileId = "fixture-profile",
                Status = "running",
                Profile = new AgentProfile { Id = "fixture-profile", ModelId = configured.Id, WorkerId = "local", ModelWorkerId = "local" }
            });

            await File.WriteAllTextAsync(signal, "exit");
            await UntilAsync(() => !runtime.IsLoaded(configured.Id), "fixture llama child exits");
            await runtime.UnloadIdleAsync(CancellationToken.None);

            var failed = store.Get<ModelRecord>("model", configured.Id)!;
            Check(failed.State == "failed" && failed.Error is { } error &&
                error.Contains("out of memory", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("exit code 17", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("reduce context", StringComparison.OrdinalIgnoreCase),
                "dead child becomes a failed model with bounded actionable OOM and exit evidence");
            Check(failed.Error!.Length <= 4096 && failed.Error.Contains("fixture terminal log marker", StringComparison.Ordinal),
                "failure retains only a bounded recent server-log diagnostic");
            Check(failed.Name == configured.Name && failed.Path == configured.Path && failed.ContextTokens == configured.ContextTokens &&
                failed.IdleMinutes == configured.IdleMinutes && failed.Purpose == configured.Purpose && failed.Tags.SequenceEqual(configured.Tags),
                "dead cleanup updates only runtime state and preserves owner configuration");
            Check(!RuntimeInstances(runtime).Cast<object>().Any(),
                "dead cleanup removes and disposes the exact stale runtime despite busy, accepted-work, and disabled-idle gates");

            busyLease.Dispose();
            busyLease = null;
            Check((int)busy.GetValue(instance)! == 0,
                "a busy slot lease can release safely after its dead runtime was disposed");

            var replacementSignal = Path.Combine(root, "replacement-exit.signal");
            Environment.SetEnvironmentVariable(FixtureEnvironment, replacementSignal);
            try { _ = await runtime.LoadAsync(configured.Id, CancellationToken.None); }
            finally { Environment.SetEnvironmentVariable(FixtureEnvironment, previousFixtureEnvironment); }
            var replacement = CurrentInstance(runtime, configured.Id);
            await InvokeDeadCleanupAsync(runtime, configured.Id, instance);
            Check(runtime.IsLoaded(configured.Id) && ReferenceEquals(replacement, CurrentInstance(runtime, configured.Id)),
                "a stale observed instance cannot remove or fail a newer current runtime");

            // Simulate an already-committed deliberate state transition while the runtime child
            // becomes terminal. Cleanup still reaps its owned resources but must not overwrite it.
            var ownerTransition = store.Get<ModelRecord>("model", configured.Id)!;
            ownerTransition.State = "unloaded";
            ownerTransition.Error = null;
            store.Put("model", ownerTransition.Id, ownerTransition);
            await File.WriteAllTextAsync(replacementSignal, "exit");
            await UntilAsync(() => !runtime.IsLoaded(configured.Id), "replacement fixture child exits");
            await runtime.UnloadIdleAsync(CancellationToken.None);
            var explicitlyUnloaded = store.Get<ModelRecord>("model", configured.Id)!;
            Check(explicitlyUnloaded.State == "unloaded" && explicitlyUnloaded.Error is null && !RuntimeInstances(runtime).Cast<object>().Any(),
                "a preexisting deliberate unloaded state wins while the dead owned runtime is still reaped");

            Console.WriteLine($"Model runtime cleanup: {assertions} checks passed (two real managed health-server children; no inference; SQLite state; reflection limited to private runtime revalidation and a genuine busy slot lease).");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FixtureEnvironment, previousFixtureEnvironment);
            busyLease?.Dispose();
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
            Directory.Delete(root, true);
        }
    }

    internal static async Task RunServerAsync(string[] args, string signal)
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        signal = Path.GetFullPath(signal);
        if (!signal.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture signal must be temporary.");
        var port = Argument(args, "--port");
        if (!int.TryParse(port, out var parsedPort) || parsedPort is < 1024 or > 65535) throw new ArgumentException("Fixture server received an invalid port.");

        using var listener = new TcpListener(IPAddress.Loopback, parsedPort);
        using var cancellation = new CancellationTokenSource();
        listener.Start();
        var server = ServeHealthAsync(listener, cancellation.Token);
        try
        {
            while (!File.Exists(signal)) await Task.Delay(20);
            Console.Error.WriteLine("CUDA error: out of memory; fixture terminal log marker");
            for (var index = 0; index < 20; index++) Console.Error.WriteLine("bounded terminal cleanup line " + index);
            await Console.Error.FlushAsync();
            Environment.ExitCode = 17;
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
            try { await server; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }

    private static async Task ServeHealthAsync(TcpListener listener, CancellationToken token)
    {
        var response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray();
        while (!token.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            await using var stream = client.GetStream();
            var request = new byte[4096];
            _ = await stream.ReadAsync(request, token);
            await stream.WriteAsync(response, token);
        }
    }

    private static string Argument(string[] args, string name)
    {
        var matches = args.Select((value, index) => (value, index)).Where(x => x.value == name).ToArray();
        if (matches.Length != 1 || matches[0].index + 1 >= args.Length) throw new ArgumentException("Fixture server is missing " + name + ".");
        return args[matches[0].index + 1];
    }

    internal static string AppHost()
    {
        var assembly = typeof(ModelRuntimeCleanupTests).Assembly.Location;
        var candidate = OperatingSystem.IsWindows() ? Path.ChangeExtension(assembly, ".exe") : Path.Combine(Path.GetDirectoryName(assembly)!, Path.GetFileNameWithoutExtension(assembly));
        if (!File.Exists(candidate)) throw new FileNotFoundException("Build the Worker test apphost before running the cleanup fixture.", candidate);
        return candidate;
    }

    private static IEnumerable RuntimeInstances(ModelRuntime runtime) =>
        (IEnumerable)typeof(ModelRuntime).GetField("instances", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;

    internal static object CurrentInstance(ModelRuntime runtime, string id)
    {
        foreach (var entry in RuntimeInstances(runtime))
        {
            var type = entry!.GetType();
            if ((string)type.GetProperty("Key")!.GetValue(entry)! == id) return type.GetProperty("Value")!.GetValue(entry)!;
        }
        throw new InvalidOperationException("Fixture runtime instance was not found.");
    }

    private static async Task InvokeDeadCleanupAsync(ModelRuntime runtime, string id, object observed)
    {
        var method = typeof(ModelRuntime).GetMethod("CleanupExitedInstanceAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(ModelRuntime), "CleanupExitedInstanceAsync");
        await (Task)(method.Invoke(runtime, [id, observed, CancellationToken.None])
            ?? throw new InvalidOperationException("Cleanup invocation returned no task."));
    }

    private static async Task UntilAsync(Func<bool> condition, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Timed out waiting for " + label + ".");
            await Task.Delay(20);
        }
    }
}
