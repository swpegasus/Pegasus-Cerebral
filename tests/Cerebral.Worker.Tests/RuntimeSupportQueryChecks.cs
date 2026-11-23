using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Cerebral.Worker.ModelAdapters;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

// Deterministic metadata fixtures only. No listener, HTTP call, process, GGUF, model or GPU.
public static class RuntimeSupportQueryChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Runtime-support query: " + name); checks++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (InvalidDataException) { checks++; return; }
            throw new InvalidOperationException("Runtime-support query accepted " + name);
        }
        async Task RejectAsync<T>(Func<Task<T>> action, string name)
        {
            try { await action(); } catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException)
            { checks++; return; }
            throw new InvalidOperationException("Runtime-support query accepted " + name);
        }
        var request = new ModelRuntimeSupportQueryRequest(1, "fixture.model", "fixture.worker");
        var descriptor = Descriptor();
        var response = Available(request, descriptor);
        var encoded = ModelRuntimeSupportQueryProtocol.WriteResponse(response, request);
        var read = ModelRuntimeSupportQueryProtocol.ReadResponse(encoded, request);
        Check(read.Support!.Capacity.AggregateContextTokens.Maximum is null &&
            read.Support.Capacity.CombinedGenerationTokens.Provenance.Basis == RuntimeLimitBasis.Unreported &&
            !read.ExpandedPathSupported && read.Support.Capabilities.Count == 0,
            "unknown independent maxima survive the closed codec without a usable path");
        Check(ModelRuntimeSupportQueryProtocol.RequestCodec.IsReadOnly &&
            ModelRuntimeSupportQueryProtocol.RequestCodec.Options.IsReadOnly &&
            ModelRuntimeSupportQueryProtocol.ResponseCodec.IsReadOnly &&
            ModelRuntimeSupportQueryProtocol.RemoteRequestCodec.Options.IsReadOnly,
            "actual shared request/response metadata and options are immutable");

        var requestJson = ModelRuntimeSupportQueryProtocol.WriteRequest(request).GetRawText();
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadRequest(Parse(requestJson.Replace("\"version\":1", "\"version\":1,\"version\":1"))),
            "duplicate request field");
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadRequest(Parse(requestJson[..^1] + ",\"load\":true}")),
            "unknown request field");
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadRequest(Parse(requestJson.Replace("\"version\":1", "\"version\":2"))),
            "unknown query version");
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadRequest(Parse(requestJson.Replace("\"version\":1", "\"version\":\"1\""))),
            "coerced query version");
        var boundRequest = request with { ExpectedBinding = descriptor.Binding };
        var boundJson = ModelRuntimeSupportQueryProtocol.WriteRequest(boundRequest).GetRawText();
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadRequest(Parse(boundJson.Replace(
            "\"configurationScopeId\":\"" + descriptor.Binding.ConfigurationScopeId + "\"", "\"configurationScopeId\":null"))),
            "digest without its exact scope");
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadRequest(Parse(boundJson.Replace(
            "\"configurationSha256\":\"" + descriptor.Binding.ConfigurationSha256 + "\"", "\"configurationSha256\":null"))),
            "scope without its digest");
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadResponse(encoded,
            boundRequest with { ExpectedBinding = descriptor.Binding with { ConfigurationSha256 = new string('b', 64) } }),
            "stale expected scoped binding");
        var responseJson = encoded.GetRawText();
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadResponse(Parse(responseJson[..^1] + ",\"usable\":true}"), request),
            "unknown response field");
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadResponse(Parse(responseJson.Replace(
            "\"schemaVersion\":1", "\"schemaVersion\":99")), request), "unknown descriptor schema");
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadResponse(Parse(responseJson.Replace(
            "\"basis\":\"unreported\"", "\"basis\":\"futureBasis\"")), request), "unknown basis enum");
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadResponse(Parse(responseJson.Replace(
            "\"basis\":\"unreported\"", "\"basis\":4")), request), "numeric basis enum");
        Reject(() => ModelRuntimeSupportQueryProtocol.ReadResponse(Parse(responseJson.Replace(
            "\"minimum\":0", "\"minimum\":0,\"minimum\":0")), request), "nested duplicate field");
        Reject(() => ModelRuntimeSupportQueryProtocol.WriteResponse(response with { ExpandedPathSupported = true }, request),
            "expanded path claim");
        Reject(() => ModelRuntimeSupportQueryProtocol.WriteResponse(response with { WorkerInstallationId = "other.worker" }, request),
            "wrong executing installation");
        var evolvedDescriptor = descriptor with {
            Binding = descriptor.Binding with { BackendId = "fixture.evolved-backend", AdapterVersion = "2",
                ConfigurationScopeId = "Fixture.Configuration.canonical/2", ConfigurationSha256 = new string('c', 64) },
            Capacity = descriptor.Capacity with {
                InferenceSlots = descriptor.Capacity.InferenceSlots with { Maximum = 32,
                    Provenance = new(RuntimeLimitBasis.OperatorPolicy, "fixture.evolved-slots", "2") },
                ContextPerSlotTokens = new(1024, null, 16, new(RuntimeLimitBasis.ModelNative, "fixture.evolved-context", "3")) } };
        var evolvedWire = ModelRuntimeSupportQueryProtocol.WriteResponse(response with { Support = evolvedDescriptor }, request);
        var evolved = ModelRuntimeSupportQueryProtocol.ReadResponse(evolvedWire, request);
        ModelRuntimeSupportQueryProtocol.RequirePassiveQuerySemantics(evolved);
        Check(evolved.Support!.Binding == evolvedDescriptor.Binding && evolved.Support.Capacity == evolvedDescriptor.Capacity &&
            evolved.Support.Capabilities.Count == 0 && !evolved.ExpandedPathSupported,
            "legal backend, scoped binding, bounds and provenance evolution remains passive metadata");

        var unavailable = ModelRuntimeSupportQueryProtocol.Unavailable(request, "not_reported", "fixture.worker", DateTimeOffset.UtcNow);
        var unavailableJson = ModelRuntimeSupportQueryProtocol.WriteResponse(unavailable, request);
        Check(ModelRuntimeSupportQueryProtocol.ReadResponse(unavailableJson, request).Support is null &&
            !unavailableJson.GetRawText().Contains("unloaded", StringComparison.Ordinal) &&
            !unavailableJson.GetRawText().Contains("capacity", StringComparison.Ordinal),
            "unavailable carries neither a model-state conclusion nor zero capacity");
        Reject(() => ModelRuntimeSupportQueryProtocol.WriteResponse(response with
            { Support = descriptor with { Binding = descriptor.Binding with { InstanceId = "replacement.instance" } } }, boundRequest),
            "replacement instance cannot satisfy the old expected binding");
        Reject(() => ModelRuntimeSupportQueryProtocol.WriteResponse(unavailable with { State = "unloaded" }, request),
            "false unloaded state");

        // Base descriptor collections retain their legal bounds even though this native query
        // separately refuses executable capability claims. This catches an accidental tiny byte cap.
        var maximumKinds = Enumerable.Range(0, ModelRuntimeSupportContract.MaximumKinds)
            .Select(i => i.ToString("D3") + new string('k', 125)).ToArray();
        var maximumCapabilities = Enumerable.Range(0, ModelRuntimeSupportContract.MaximumCapabilities)
            .Select(i => new ModelRuntimeCapability(i.ToString("D3") + new string('c', 125),
                new string('d', 128), int.MaxValue, maximumKinds, maximumKinds)).ToArray();
        var full = response with { Support = descriptor with { Capabilities = maximumCapabilities } };
        var fullJson = ModelRuntimeSupportQueryProtocol.WriteResponse(full, request);
        Check(fullJson.GetRawText().Length > 524288 &&
            ModelRuntimeSupportQueryProtocol.ReadResponse(fullJson, request).Support!.Capabilities.Count == 64,
            "legal maximum descriptor collections fit the declared payload bound");
        Reject(() => ModelRuntimeSupportQueryProtocol.RequirePassiveQuerySemantics(full),
            "metadata codec mistaken for native execution witnesses");
        Reject(() => ModelRuntimeSupportQueryProtocol.WriteResponse(full with
        { Support = descriptor with { Capabilities = maximumCapabilities.Append(maximumCapabilities[0]).ToArray() } }, request),
            "capability collection beyond base bound");

        var calls = new List<string>();
        var client = new ModelRuntimeSupportClient((operation, arguments, token) =>
        {
            token.ThrowIfCancellationRequested(); calls.Add(operation);
            return Task.FromResult(operation == "sdk.discover" ? Catalog(ModelRuntimeSupportQueryProtocol.ReadOperation) : encoded);
        });
        Check((await client.ReadAsync(boundRequest)).State == "available" &&
            calls.SequenceEqual(["sdk.discover", ModelRuntimeSupportQueryProtocol.ReadOperation]),
            "Client discovers the exact local operation before one query");
        var evolvedClient = new ModelRuntimeSupportClient((operation, arguments, token) => Task.FromResult(
            operation == "sdk.discover" ? Catalog(ModelRuntimeSupportQueryProtocol.ReadOperation) : evolvedWire));
        var evolvedRead = await evolvedClient.ReadAsync(request);
        Check(evolvedRead.Support!.Binding == evolvedDescriptor.Binding && !evolvedRead.ExpandedPathSupported &&
            evolvedRead.Support.Capabilities.Count == 0,
            "Client has no executing-Worker backend or range literals to update");
        foreach (var version in new[] { 0, 2 })
        {
            calls.Clear();
            var absent = new ModelRuntimeSupportClient((operation, arguments, token) =>
            { calls.Add(operation); return Task.FromResult(Catalog(ModelRuntimeSupportQueryProtocol.ReadOperation, version)); });
            var result = await absent.ReadAsync(request);
            Check(result.State == "unavailable" && result.Reason == "operation_unavailable" &&
                result.WorkerInstallationId is null && result.ObservedAt is null && result.Support is null &&
                calls.SequenceEqual(["sdk.discover"]), "missing or unsupported operation dispatches no query or fallback");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); calls.Clear();
            try { await client.ReadAsync(request, cancelled.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { Check(calls.Count == 0, "pre-cancelled Client performs no discovery or query"); }
        }
        calls.Clear();
        var missingProxy = new ModelRuntimeSupportClient((operation, arguments, token) =>
        {
            calls.Add(operation);
            return Task.FromResult(Catalog(ModelRuntimeSupportQueryProtocol.ReadOperation));
        }, "fixture-remote");
        Check((await missingProxy.ReadAsync(request)).Reason == "operation_unavailable" &&
            calls.SequenceEqual(["sdk.discover"]),
            "target-named discovery cannot stand in for missing coordinator proxy support");
        calls.Clear();
        var interrupted = new ModelRuntimeSupportClient((operation, arguments, token) =>
        {
            calls.Add(operation);
            if (operation == "sdk.discover") return Task.FromResult(Catalog(ModelRuntimeSupportQueryProtocol.ReadOperation));
            throw new IOException("fixture transport interruption");
        });
        try { await interrupted.ReadAsync(request); throw new Exception("Interrupted transport accepted."); }
        catch (IOException) { Check(calls.Count == 2, "interrupted read is neither retried nor redirected"); }

        await using (var h = new Harness())
        {
            var localRequest = request with { ExpectedWorkerInstallationId = h.Configuration.Get().InstallationId };
            h.Store.Put("model", request.ModelId, new ModelRecord { Id = request.ModelId, State = "loaded", Path = "fixture-do-not-open.gguf" });
            var before = h.Runtime.Accounting.Snapshot();
            var local = new ModelRuntimeSupportClient(h.ReadCommandAsync);
            var localResult = await local.ReadAsync(localRequest);
            Check(localResult.State == "unavailable" && localResult.Reason == "not_reported" &&
                !h.Runtime.IsLoaded(request.ModelId) && h.Runtime.ResourceProcessIds().Count == 0 &&
                h.Runtime.Accounting.Snapshot().Revision == before.Revision && h.Remote.Calls.Count == 0,
                "real local handler performs no load, registration, process or inference ownership");
            var mismatch = await local.ReadAsync(localRequest with { ExpectedWorkerInstallationId = "different.worker" });
            Check(mismatch.Reason == "identity_changed" && mismatch.WorkerInstallationId is null && mismatch.Support is null,
                "wrong local installation exposes no support");
            h.Store.Put("agent", "query-admin", new AgentProfile { Id = "query-admin", Capabilities = ["administration"] });
            foreach (var operation in new[] { ModelRuntimeSupportQueryProtocol.ReadOperation, ModelRuntimeSupportQueryProtocol.RemoteReadOperation })
                Check(!(await h.Commands.ExecuteAsync(new(operation, ModelRuntimeSupportQueryProtocol.WriteRequest(localRequest), "query-admin"))).Success,
                    "administration profile cannot invoke owner-only metadata query");
            var actorCatalog = await h.Commands.ExecuteAsync(new("sdk.discover", Parse("{}"), "query-admin"));
            Check(!ModelRuntimeSupportQueryProtocol.SupportsOperation(actorCatalog.Data, ModelRuntimeSupportQueryProtocol.ReadOperation) &&
                !ModelRuntimeSupportQueryProtocol.SupportsOperation(actorCatalog.Data, ModelRuntimeSupportQueryProtocol.RemoteReadOperation),
                "owner-only query discovery is hidden from profile actors");
        }

        await using (var h = new Harness())
        {
            var proxy = new ModelRuntimeSupportClient(h.ReadCommandAsync, "fixture-remote");
            var remoteRequest = request with { ExpectedWorkerInstallationId = "fixture.worker" };
            var before = h.Runtime.Accounting.Snapshot();
            var result = await proxy.ReadAsync(remoteRequest);
            Check(result.State == "available" && result.Support!.Binding.WorkerInstallationId == "fixture.worker" &&
                h.CommandCalls.SequenceEqual(["sdk.discover", ModelRuntimeSupportQueryProtocol.RemoteReadOperation]) &&
                h.Remote.Calls.SequenceEqual(["health", "worker.management.status", "sdk.discover",
                    ModelRuntimeSupportQueryProtocol.ReadOperation, "health", "worker.management.status"]) &&
                h.Runtime.Accounting.Snapshot().Revision == before.Revision,
                "both discovery layers and verified post-read session rejoin precede a returned remote observation");
            h.Remote.Calls.Clear(); h.Remote.OperationVersion = 2;
            Check((await proxy.ReadAsync(remoteRequest)).Reason == "operation_unavailable" &&
                !h.Remote.Calls.Contains(ModelRuntimeSupportQueryProtocol.ReadOperation),
                "coordinator proxy advertisement cannot open unsupported target version");
            h.Remote.OperationVersion = 1;
            h.Remote.AfterRead = () => h.Remote.ChangedInstallation = true;
            await RejectAsync(() => proxy.ReadAsync(remoteRequest), "remote installation changed after read");
        }
        await using (var h = new Harness())
        {
            var proxy = new ModelRuntimeSupportClient(h.ReadCommandAsync, "fixture-remote");
            h.Remote.AfterRead = () =>
            {
                var settings = h.Configuration.Get(); settings.Workers[0].Url = "http://127.0.0.1:2668"; h.Configuration.Save(settings);
            };
            await RejectAsync(() => proxy.ReadAsync(request), "saved owner route changed after read");
        }
        await using (var h = new Harness())
        {
            var proxy = new ModelRuntimeSupportClient(h.ReadCommandAsync, "fixture-remote");
            var mismatch = await proxy.ReadAsync(request with { ExpectedWorkerInstallationId = "different.worker" });
            Check(mismatch.Reason == "identity_changed" && !h.Remote.Calls.Contains("sdk.discover"),
                "wrong accepted remote identity blocks target discovery/query");
            h.Remote.Calls.Clear(); h.Remote.WaitForCancellation = true;
            using var cancellation = new CancellationTokenSource();
            var pending = proxy.ReadAsync(request, cancellation.Token);
            await h.Remote.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { await pending; throw new Exception("Remote cancellation ignored."); }
            catch (OperationCanceledException)
            { Check(h.Remote.Calls.Count(x => x == ModelRuntimeSupportQueryProtocol.ReadOperation) == 1,
                "in-flight remote cancellation propagates without retry or inference work"); }
        }
        return checks;
    }

    private static JsonElement Parse(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static JsonElement Catalog(string operation, int version = 1) => CerebralJson.Element(new
    {
        contract = WorkflowContract.Id, version = WorkflowContract.Version,
        operations = version == 0 ? Array.Empty<object>() : new object[] { new { id = operation, version } }
    });
    private static ModelRuntimeSupportDescriptor Descriptor()
    {
        var model = new ModelRecord { Id = "fixture.model", Path = "fixture.gguf",
            ContextTokens = 2048, MaximumContextTokens = 2048, ParallelRequests = 1 };
        var launch = OwnerRuntimeLaunchCapture.Capture(model, "fixture-not-executed", "fixture.worker").SuccessfulStartup(null);
        var native = LlamaPreparedGeneration.Adapter;
        var facade = new ModelAdapterDescriptor(native.AdapterId, native.Version, ["text"], SupportsReasoning: true);
        var actual = new AdapterBinding("worker.local.llama.cpp", model.Id, "fixture.instance", facade.AdapterId, facade.Version);
        // Exercise the actual canonical declaration producer, without constructing/loading an instance.
        return NativeInstanceRuntimeSupport.TryCreate(launch, actual, facade)
            ?? throw new InvalidOperationException("Canonical native metadata fixture was unavailable.");
    }
    private static ModelRuntimeSupportQueryResponse Available(ModelRuntimeSupportQueryRequest request,
        ModelRuntimeSupportDescriptor descriptor) => new(1, request.ModelId, request.ExpectedWorkerInstallationId,
            DateTimeOffset.UtcNow, "available", null, descriptor, false);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cerebral-support-query-" + Guid.NewGuid().ToString("N"));
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public ModelRuntime Runtime { get; }
        public CommandDispatcher Commands { get; }
        public RemoteOwner Remote { get; }
        public List<string> CommandCalls { get; } = [];
        private readonly ToolDispatcher tools;
        private readonly JobScheduler scheduler;
        public Harness()
        {
            Directory.CreateDirectory(root); Store = new(root); Configuration = new(Store);
            var settings = Configuration.Get(); settings.Workers = [new() { Id = "fixture-remote", Name = "Fixture",
                Url = "http://127.0.0.1:2667", AccessKey = new string('a', 48) }]; Configuration.Save(settings);
            Remote = new(settings.InstallationId);
            var events = new EventHub(); Runtime = new(Store, Configuration.Get, events); tools = new(Configuration.Get);
            var workflows = new WorkerWorkflows(Store, Configuration, events, [], [], tools);
            scheduler = new(Store, Configuration, new NoExecutor(), Runtime, events, NullLogger<JobScheduler>.Instance,
                workflows, resourceModels: new NoModels(), resourcePeers: new NoPeers());
            Commands = new(Store, Configuration, Runtime, scheduler, tools, events, workflows: workflows,
                remoteWorkerClientFactory: _ => Remote);
        }
        public async Task<JsonElement> ReadCommandAsync(string operation, JsonElement args, CancellationToken token)
        {
            CommandCalls.Add(operation);
            var result = await Commands.ExecuteAsync(new(operation, args), token);
            token.ThrowIfCancellationRequested();
            if (!result.Success) throw new InvalidDataException(result.Error);
            return result.Data;
        }
        public async ValueTask DisposeAsync()
        {
            scheduler.Dispose(); await tools.DisposeAsync(); await Runtime.DisposeAsync(); SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
    private sealed class RemoteOwner(string controller) : IRemoteWorkerOwnerClient
    {
        public List<string> Calls { get; } = [];
        public int OperationVersion = 1;
        public bool ChangedInstallation;
        public Action? AfterRead;
        public bool WaitForCancellation;
        public TaskCompletionSource<bool> QueryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T> GetAsync<T>(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add(operation);
            object value;
            switch (operation)
            {
                case "health": value = new HostInfo("Pegasus Cerebral", "fixture", 1, "Fixture", "X64", "fixture", false,
                    "fixture-root", InstallationId: ChangedInstallation ? "changed.worker" : "fixture.worker"); break;
                case "worker.management.status": value = new WorkerManagementStatus(
                    ChangedInstallation ? "changed.worker" : "fixture.worker", "assigned", controller, "Fixture", null); break;
                case "sdk.discover": value = Catalog(ModelRuntimeSupportQueryProtocol.ReadOperation, OperationVersion); break;
                case ModelRuntimeSupportQueryProtocol.ReadOperation:
                    var request = ModelRuntimeSupportQueryProtocol.ReadRequest((JsonElement)arguments!);
                    QueryEntered.TrySetResult(true);
                    if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    value = ModelRuntimeSupportQueryProtocol.WriteResponse(Available(request, Descriptor()), request);
                    AfterRead?.Invoke(); break;
                default: throw new InvalidOperationException("Unexpected metadata fixture operation: " + operation);
            }
            return CerebralJson.Read<T>(CerebralJson.Element(value));
        }
        public void Dispose() { }
    }
    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("No fixture execution.");
    }
    private sealed class NoModels : IResourcePauseModelControl
    {
        public Task<IReadOnlyList<string>> BeginPauseAsync(CancellationToken token) => throw new InvalidOperationException("No model work.");
        public Task UnloadPausedAsync(CancellationToken token) => throw new InvalidOperationException("No model work.");
        public Task ResumePausedAsync(IReadOnlyList<string> ids, CancellationToken token) => throw new InvalidOperationException("No model work.");
        public Task ReopenPausedAdmissionAsync(Action commit, CancellationToken token) => throw new InvalidOperationException("No model work.");
    }
    private sealed class NoPeers : IResourcePausePeerProbe
    {
        public Task<IReadOnlyList<ResourcePausePeerStatus>> ProbeAsync(IReadOnlyList<WorkerRegistration> workers,
            CancellationToken token) => throw new InvalidOperationException("No network probe.");
    }
}
