using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cerebral.Auditing;
using Cerebral.Auditing.Laya;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

var checks = 0;
void Check(bool passed, string description)
{ if (!passed) throw new InvalidOperationException("Laya fixture: " + description); checks++; }
async Task Reject(Func<Task> operation, string description)
{
    var rejected = false;
    try { await operation(); }
    catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OperationCanceledException or JsonException or InvalidOperationException) { rejected = true; }
    Check(rejected, description);
}

// Captured by the independent stock CPU smoke; replay validates native public shape only.
// It does not start Python or claim that the captured readiness is current live state.
using (var captured = LayaProtocol.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "native-ready.json"))))
{
    var root = captured.RootElement;
    var config = Fixture.Config() with
    {
        OriginalInputsSha256 = "9d8d37fdb87d87fc56ce6be0388ef67bd625ce3d1622cd659022f8ae5a0eae99",
        EffectiveInputsSha256 = "9d8d37fdb87d87fc56ce6be0388ef67bd625ce3d1622cd659022f8ae5a0eae99",
        RunnerSha256 = "1bdae19163f375800115d1ffe26a8d1a1923870f16c75f00ab283c9e9be68009",
        RendererSha256 = "29cc722224f202eef4e130dee05131ddc62a18ee1120919e5ae65918d07aec41",
        DependencyLockSha256 = "2c9bb1c1d91a43e947d0ea22d3ebc49ff5d9890ae55687dfdd8b50589cb8590a"
    };
    var ready = LayaProtocol.Ready(root, config);
    Check(ready.Device == "cpu" && ready.ResidentMemoryBytes > 0 && ready.ReadinessIdentity == root.GetProperty("readinessIdentity").GetString(),
        "captured actual stock CPU Ready packet satisfies exact public shape, admission digests and canonical identity");
}

foreach (var choice in LayaProtocol.Options)
{
    await using var fixture = await Fixture.StartAsync(); fixture.Child.Choice = choice;
    var result = await fixture.Adapter.ReviewAsync(Fixture.Request(), default);
    Check(result.Disposition == (choice == "met" ? "accepted" : choice == "gap" ? "gap" : "insufficient_evidence") &&
        result.AdvisoryOnly && !result.DirectActionAllowed, "only typed native criterion is projected, with no authority from confidence");
    Check(fixture.Child.Predictions == 1 && fixture.Module.CompletedRequests == 1 && fixture.Child.Preparations == 1,
        "one preflight and one prediction use one resident deterministic fixture");
    using var nativeRequest = JsonDocument.Parse(fixture.Child.LastPrediction!);
    var state = nativeRequest.RootElement.GetProperty("state").GetString()!;
    var hostContext = nativeRequest.RootElement.GetProperty("hostContext").GetString()!;
    Check(!state.Contains("conversation_id", StringComparison.Ordinal) && hostContext.Contains("conversation_id", StringComparison.Ordinal) &&
        state.Contains("omitted_evidence", StringComparison.Ordinal), "native classifier sees exact sealed criterion, not host bookkeeping or full chats");
    Check(fixture.Child.NativeInputTokens < fixture.Child.HostContextTokens && result.RequestId == Fixture.Id,
        "separate actual fixture token measurements preserve the explicit projection invariant and host binding");
    var saved = JsonSerializer.Serialize(fixture.Module.LastResult);
    Check(!saved.Contains("Explicit requirement", StringComparison.Ordinal) && saved.Contains("unvalidated", StringComparison.Ordinal),
        "owner diagnostics retain hashes and unvalidated status rather than prompts");
}

await using (var fixture = await Fixture.StartAsync())
{
    var request = Fixture.Request();
    using var context = JsonDocument.Parse("{\"observation\":\"No explicit criterion\"}");
    var result = await fixture.Adapter.ReviewAsync(request with { Context = context.RootElement }, default);
    Check(result.Disposition == "insufficient_evidence" && fixture.Child.Preparations == 0 && fixture.Child.Predictions == 0,
        "missing criterion is not invented and dispatches no model request");
}
foreach (var setting in new[] { "reject", "host-overflow", "unmeasured-reject", "predict-reject", "changed-state", "changed-count", "unknown-field" })
{
    await using var fixture = await Fixture.StartAsync(); fixture.Child.Fault = setting;
    var result = await fixture.Adapter.ReviewAsync(Fixture.Request(), default);
    Check(result.Disposition is "unavailable" or "rejected" && fixture.Module.CompletedRequests == 0,
        $"{setting}: oversized, rejected or unbound native packets never produce accepted advisory results ({result.Disposition}, completed={fixture.Module.CompletedRequests})");
    if (setting is "reject" or "host-overflow" or "unmeasured-reject" or "predict-reject")
    {
        Check(fixture.Child.Predictions == (setting == "predict-reject" ? 1 : 0) && fixture.Client.Ready,
            "provider limits are honest unavailability and do not invent truncation");
        var diagnostic = fixture.Module.LastRejection!;
        Check(diagnostic.RequestId == Fixture.Id && result.Summary.Contains(diagnostic.Code, StringComparison.Ordinal) && result.Summary.Length <= 256 &&
            !JsonSerializer.Serialize(diagnostic).Contains("Explicit requirement", StringComparison.Ordinal),
            "bounded rejection summary and owner diagnostic bind this request without retaining raw criterion text");
        Check(setting == "unmeasured-reject"
            ? diagnostic.ContextTokens is null && diagnostic.HostContextTokens is null && diagnostic.InputTokens is null &&
                result.Summary.Contains("inputTokens=unavailable", StringComparison.Ordinal)
            : setting == "host-overflow"
                ? diagnostic is { ContextTokens: 80, HostContextTokens: 700, InputTokens: 140 } && result.Summary.Contains("hostContextTokens=700", StringComparison.Ordinal)
                : diagnostic is { ContextTokens: 500, HostContextTokens: 190, InputTokens: 560 },
            "measured packet, host and input counts remain distinct; absent measurements are never zero estimates");
        var samePid = fixture.Client.ProcessId; fixture.Child.Fault = "";
        var fresh = await fixture.Adapter.ReviewAsync(Fixture.Request("after-overflow"), default);
        Check(fresh.Disposition == "accepted" && fixture.Client.ProcessId == samePid && fixture.Child.Starts == 1 && fixture.Child.Stops == 0,
            "overflow leaves the same healthy resident child available for a fresh small packet");
        Check(!fresh.Summary.Contains(diagnostic.Code, StringComparison.Ordinal), "a later successful invocation does not inherit the prior rejection summary");
    }
    else Check(!fixture.Client.Ready && fixture.Child.Stops == 1, "invalid result terminates exact owned fixture before returning capacity");
}
await using (var fixture = await Fixture.StartAsync())
{
    var workflow = Fixture.Request();
    var prepared = await fixture.Module.PrepareAsync(workflow, LayaProtocol.Hash(workflow.Context.GetRawText()), default);
    var state = new AdvisorHostState(prepared.Request.InputRevision + 1, prepared.Request.ContextSha256, prepared.Request.Sequence, LayaStockAdvisorModule.Now);
    var stale = await fixture.Module.InvokeAsync(prepared.Request, state, default);
    Check(stale.Disposition == "stale" && fixture.Child.Predictions == 0, "fresh native host-state check refuses stale revision before prediction");
}

await using (var fixture = await Fixture.StartAsync())
{
    fixture.Child.HoldPredictions = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cancel = new CancellationTokenSource();
    var running = fixture.Adapter.ReviewAsync(Fixture.Request(), cancel.Token);
    await fixture.Child.PredictionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
    await Task.Delay(20);
    Check(!running.IsCompleted && fixture.Client.ActiveRequests == 1 && fixture.Child.Alive,
        "cancelled native prediction retains its occupied resource until actual deterministic completion");
    fixture.Child.HoldPredictions.SetResult();
    var result = await running;
    Check(result.Disposition == "cancelled" && fixture.Module.CompletedRequests == 0 && fixture.Client.ActiveRequests == 0 && fixture.Client.Ready,
        "late result is discarded without killing or prematurely freeing a completed resident child");
}
await using (var fixture = await Fixture.StartAsync(config => config with { QueueCapacity = 0 }))
{
    fixture.Child.HoldPredictions = new(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Module.RecordRejection("other-request", new("previous-rejection", 10, 20, 30));
    var running = fixture.Adapter.ReviewAsync(Fixture.Request(), default);
    await fixture.Child.PredictionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var competing = await fixture.Adapter.ReviewAsync(Fixture.Request("competing"), default);
    Check(competing.Disposition == "unavailable" && fixture.Child.Predictions == 1, "bounded queue rejects excess requests without hidden parallel inference");
    Check(!competing.Summary.Contains("previous-rejection", StringComparison.Ordinal), "queue unavailability cannot use a different request's retained rejection as evidence");
    fixture.Child.HoldPredictions.SetResult(); await running;
}
foreach (var invalidCount in new[] { "-1", "1048577", "\"700\"", "true" })
{
    using var packet = LayaProtocol.Parse($"{{\"protocol\":\"{LayaProtocol.Version}\",\"kind\":\"rejected\",\"id\":\"count-fixture\",\"errorCode\":\"host-context-token-limit\",\"hostContextTokens\":{invalidCount}}}");
    await Reject(() => { LayaProtocol.Rejection(packet.RootElement, "count-fixture"); return Task.CompletedTask; },
        "malformed optional rejection counts fail validation rather than appearing as reported numeric evidence");
}
using (var packet = LayaProtocol.Parse($"{{\"protocol\":\"{LayaProtocol.Version}\",\"kind\":\"rejected\",\"id\":\"null-count-fixture\",\"errorCode\":\"state-byte-limit\",\"contextTokens\":null,\"hostContextTokens\":null,\"inputTokens\":null}}"))
{
    LayaRejectionDiagnostic? diagnostic = null;
    try { LayaProtocol.Rejection(packet.RootElement, "null-count-fixture"); }
    catch (LayaRejectedException rejection) { diagnostic = rejection.Diagnostic; }
    Check(diagnostic is { ContextTokens: null, HostContextTokens: null, InputTokens: null } &&
        diagnostic.Summary().Contains("contextTokens=unavailable", StringComparison.Ordinal),
        "explicit null native measurements remain unavailable without inventing zeros");
}
foreach (var confirmed in new[] { true, false })
{
    await using var fixture = await Fixture.StartAsync(config => config with { RequestTimeoutMilliseconds = 50 });
    fixture.Child.StopConfirmed = confirmed;
    fixture.Child.HoldPredictions = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var result = await fixture.Adapter.ReviewAsync(Fixture.Request(), default);
    Check(result.Disposition is "timed_out" or "unavailable" && fixture.Child.Stops == 1 && fixture.Module.CompletedRequests == 0,
        $"transport deadline requires owned process termination rather than assuming native cancellation ({result.Disposition}, stops={fixture.Child.Stops}, completed={fixture.Module.CompletedRequests})");
    Check(!fixture.Client.Ready && fixture.Client.ActiveRequests == (confirmed ? 0 : 1),
        "unconfirmed termination quarantines occupied capacity, confirmed termination releases it");
    fixture.Child.HoldPredictions.TrySetResult();
}
await using (var fixture = await Fixture.StartAsync())
{
    await Reject(fixture.Client.StartAsync, "repeated startup is refused without replacing the resident child");
    Check(fixture.Client.Ready && fixture.Child.Starts == 1 && fixture.Child.Stops == 0, "startup replay does not terminate a valid resident child");
    fixture.Child.HoldPredictions = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var active = fixture.Adapter.ReviewAsync(Fixture.Request("dispose-active"), default);
    await fixture.Child.PredictionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var queued = fixture.Adapter.ReviewAsync(Fixture.Request("dispose-queued"), default);
    Check(fixture.Client.ActiveRequests == 1 && fixture.Client.QueuedRequests == 1, "shutdown fixture owns one native call and one queued waiter");
    var firstDispose = fixture.Client.DisposeAsync().AsTask(); var repeatDispose = fixture.Client.DisposeAsync().AsTask();
    await Task.WhenAll(firstDispose, repeatDispose, active, queued).WaitAsync(TimeSpan.FromSeconds(2));
    Check(!fixture.Child.Alive && !fixture.Client.Ready && fixture.Child.Stops == 1 && fixture.Client.ActiveRequests == 0 &&
        fixture.Client.QueuedRequests == 0 && fixture.Module.CompletedRequests == 0 && fixture.Child.Predictions == 1,
        "idempotent shutdown drains active and queued callers before disposing synchronization handles without accepting late results");
    await Reject(() => fixture.Client.ExchangeAsync(new { }, _ => true, default), "disposed client refuses new admission");
}
foreach (var fault in new[] { "gpu", "bad-identity", "wrong-options", "wrong-renderer", "duplicate", "startup-unavailable" })
{
    var child = new FakeChild { Fault = fault };
    await using var client = new LayaProcessClient(Fixture.Config(), child);
    await Reject(client.StartAsync, "stock CPU readiness rejects unverified identity, placement, options or malformed protocol");
    Check(!client.Ready && child.Stops == 1, "invalid CPU startup exposes no readiness and stops only its owned fixture");
}
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var allowStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var child = new FakeChild();
    await using var client = new LayaProcessClient(Fixture.Config(), child, async () => { entered.SetResult(); await allowStart.Task; });
    var startup = client.StartAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var disposing = client.DisposeAsync().AsTask();
    Check(!disposing.IsCompleted && child.Starts == 0 && child.Stops == 1,
        "dispose holds cleanup until a pre-creation startup participant leaves");
    allowStart.SetResult(); await Reject(() => startup, "startup cancelled by disposal cannot create a child after a cached no-child stop");
    await disposing.WaitAsync(TimeSpan.FromSeconds(2));
    Check(!child.Alive && child.Starts == 0 && child.Stops == 1 && !client.Ready,
        "creation/stop serialization prevents an orphan child in the startup-dispose race");
}

var temp = Path.Combine(Path.GetTempPath(), "Cerebral.LayaFixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var ini = Path.Combine(temp, LayaConfiguration.FileName);
    File.WriteAllText(ini, "[host]\nenabled=false\n", new UTF8Encoding(false));
    using var plugin = new LayaStockPlugin(() => throw new InvalidOperationException("disabled plugin started a child"));
    plugin.Initialize(new Host(temp));
    Check(plugin.WorkflowAuditors.Count == 0 && plugin.ToolExtensions.Count == 0 && !plugin.Status().Ready,
        "disabled optional package publishes no auditor, agent tools or readiness");
    using var empty = JsonDocument.Parse("{}");
    var command = plugin.CommandExtensions.Single();
    var status = (LayaPluginStatus)(await command.ExecuteAsync("laya.status", empty.RootElement, default))!;
    Check(!status.Ready && status.AdapterId == "laya.stock.criterion" && status.Error is { Length: > 0 and <= 256 },
        "owner status distinguishes disabled admission with exact package and adapter IDs");
    using var injection = JsonDocument.Parse("{\"command\":\"anything\"}");
    await Reject(() => command.ExecuteAsync("laya.status", injection.RootElement, default), "status does not accept command injection");
    File.WriteAllText(ini, "[host]\nenabled=false\nunknown=true\n", new UTF8Encoding(false));
    await Reject(() => Task.FromResult(LayaConfiguration.Read(ini)), "closed INI rejects unknown host keys");
    File.WriteAllText(ini, "[host]\nenabled=false\nenabled=true\n", new UTF8Encoding(false));
    await Reject(() => Task.FromResult(LayaConfiguration.Read(ini)), "closed INI rejects duplicated settings");
    var python = Path.Combine(temp, "python.exe"); var runner = Path.Combine(temp, "runner.py");
    var renderer = Path.Combine(temp, "preflight.py"); var dependencyLock = Path.Combine(temp, "requirements-lock.txt");
    foreach (var path in new[] { python, runner, renderer, dependencyLock }) File.WriteAllText(path, "fixture immutable launch bytes", new UTF8Encoding(false));
    var digest = LayaProtocol.Hash("fixture immutable launch bytes");
    var launch = Fixture.Config() with
    {
        PythonPath = python, PythonSha256 = digest, RunnerPath = runner, RunnerSha256 = digest,
        RendererPath = renderer, RendererSha256 = digest, DependencyLockPath = dependencyLock, DependencyLockSha256 = digest
    };
    var unrelatedName = "CEREBRAL_LAYA_FIXTURE_UNRELATED";
    var previousUnrelated = Environment.GetEnvironmentVariable(unrelatedName);
    try
    {
        Environment.SetEnvironmentVariable(unrelatedName, "must-not-be-forwarded");
        var start = LayaOwnedChild.CreateStartInfo(launch with { RootDirectory = temp, ConfigurationPath = ini });
        Check(start.Environment["USERNAME"] == Environment.UserName && !start.Environment.ContainsKey(unrelatedName),
            "sanitized child retains exactly actual Windows user identity without unrelated environment forwarding");
        Check(start.Environment["CUDA_VISIBLE_DEVICES"] == "" && start.Environment["HF_HUB_OFFLINE"] == "1" &&
            start.Environment["TRANSFORMERS_OFFLINE"] == "1" && start.Environment["HF_HUB_DISABLE_TELEMETRY"] == "1" &&
            start.Environment.Keys.All(name => new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "LOCALAPPDATA", "USERNAME", "PATH",
                "CUDA_VISIBLE_DEVICES", "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_HUB_DISABLE_TELEMETRY" }.Contains(name)),
            "CPU/offline isolation uses a closed nonsecret environment allowlist");
        Check(start.ArgumentList.SequenceEqual(new[] { "-I", "-S", "-u", runner, "--config", ini }) &&
            !start.UseShellExecute && start.CreateNoWindow && start.WorkingDirectory == temp,
            "verified runner uses exact isolated/no-site argument list and ordinary-user hidden child");
    }
    finally { Environment.SetEnvironmentVariable(unrelatedName, previousUnrelated); }
    launch.VerifyLaunchFiles(); Check(true, "all four admitted launch input digests are verified before child start");
    File.WriteAllText(renderer, "modified renderer", new UTF8Encoding(false));
    await Reject(() => { launch.VerifyLaunchFiles(); return Task.CompletedTask; }, "changed renderer bytes fail before process creation");
    File.WriteAllText(renderer, "fixture immutable launch bytes", new UTF8Encoding(false));
    await Reject(() => { (launch with { RendererPath = dependencyLock }).VerifyLaunchFiles(); return Task.CompletedTask; },
        "hashing an unrelated file cannot stand in for the exact imported sibling renderer");
    File.WriteAllText(ini, $"""
        [host]
        enabled=true
        python_path={python}
        python_sha256={digest}
        runner_path={runner}
        runner_sha256={digest}
        renderer_path={renderer}
        renderer_sha256={digest}
        dependency_lock_path={dependencyLock}
        dependency_lock_sha256={digest}
        estimated_p95_ms=100
        generation=7
        purposes=component_evidence_check
        [laya]
        root_directory={temp}
        dependency_lock_sha256={digest}
        expected_weights_sha256={LayaConfiguration.WeightsSha256}
        expected_original_inputs_sha256={new string('a', 64)}
        expected_effective_inputs_sha256={new string('b', 64)}
        checkpoint_revision={LayaConfiguration.CheckpointRevision}
        package_version={LayaConfiguration.PackageVersion}
        torch_threads=2
        interop_threads=1
        """, new UTF8Encoding(false));
    var admittedChild = new FakeChild();
    using var admittedPlugin = new LayaStockPlugin(() => admittedChild);
    admittedPlugin.Initialize(new Host(temp));
    Check(admittedPlugin.Status().Ready && admittedPlugin.WorkflowAuditors.Count == 1, "explicit enabled INI publishes only actually ready deterministic CPU auditor");
    admittedChild.Fault = "host-overflow";
    var unavailable = await admittedPlugin.WorkflowAuditors.Single().ReviewAsync(Fixture.Request("status-rejection"), default);
    var reported = (LayaPluginStatus)(await admittedPlugin.CommandExtensions.Single().ExecuteAsync("laya.status", empty.RootElement, default))!;
    Check(unavailable.Disposition == "unavailable" && reported.LastRejection is
        { Code: "host-context-token-limit", ContextTokens: 80, HostContextTokens: 700, InputTokens: 140, RequestId: "status-rejection" } &&
        reported.Ready && reported.CompletedRequests == 0,
        "owner status exposes validated rejection counts while truthful readiness and completed native inference remain separate");
}
finally
{
    var target = Path.GetFullPath(temp);
    if (!target.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Fixture cleanup escaped its assigned temporary root.");
    Directory.Delete(target, true);
}
Console.WriteLine($"Laya C# adapter: {checks} deterministic protocol, binding, optional admission and owned-resource checks passed. No stock model, GPU, network or hardware inference was exercised.");

sealed class Host(string directory) : IServicePluginHost
{
    public string PluginId => "pegasus.laya-auditor";
    public string PackageDirectory => directory;
    public string DataDirectory => directory;
    public void Publish(string eventType, string? jobId, object? data) => throw new InvalidOperationException("No events required by fixture.");
}
sealed class Fixture : IAsyncDisposable
{
    public const string Id = "fixture-audit";
    public FakeChild Child { get; }
    public LayaProcessClient Client { get; }
    public LayaStockAdvisorModule Module { get; }
    public LayaWorkflowAuditAdapter Adapter { get; }
    private Fixture(FakeChild child, LayaProcessClient client, LayaStockAdvisorModule module)
    { Child = child; Client = client; Module = module; Adapter = new(module); }
    public static LayaConfiguration Config() => new()
    {
        Enabled = true, Generation = 7, EstimatedP95Milliseconds = 100,
        OriginalInputsSha256 = new('a', 64), EffectiveInputsSha256 = new('b', 64),
        RunnerSha256 = new('c', 64), RendererSha256 = new('1', 64), DependencyLockSha256 = new('d', 64),
        Purposes = Array.AsReadOnly(new[] { "coordinator_decision_check", "component_evidence_check" })
    };
    public static async Task<Fixture> StartAsync(Func<LayaConfiguration, LayaConfiguration>? change = null)
    {
        var config = change?.Invoke(Config()) ?? Config();
        var child = new FakeChild(); var client = new LayaProcessClient(config, child);
        await client.StartAsync(); return new(child, client, new(config, client));
    }
    public static WorkflowAuditRequest Request(string id = Id)
    {
        var now = LayaStockAdvisorModule.Now;
        var context = JsonSerializer.SerializeToElement(new
        {
            acceptanceCriterion = "Recorded observation supports the explicit requirement.", requirement = "Explicit requirement",
            proposedAction = "Completed work", observation = "Recorded deterministic observation", evidenceRunId = "fixture-run",
            evidenceRevision = 1, profileId = "fixture-profile", knownMissingEvidence = "Omitted receipts are not inferred."
        });
        return new(id, "fixture-run", "fixture-chat", 1, 2, 3, "component_evidence_check", new('e', 64), new('f', 64), context, now, now + 5000);
    }
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
sealed class FakeChild : ILayaChild
{
    private readonly Channel<string> responses = Channel.CreateUnbounded<string>();
    private JsonObject? ready;
    public bool Alive { get; private set; }
    public int? ProcessId => 12345;
    public int Stops, Starts, Predictions, Preparations;
    public string Choice = "met", Fault = "";
    public bool StopConfirmed = true;
    public int NativeInputTokens = 140, HostContextTokens = 190;
    public TaskCompletionSource? HoldPredictions;
    public TaskCompletionSource PredictionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? LastPrediction;
    public void Start(LayaConfiguration config)
    {
        Alive = true;
        Starts++;
        ready = JsonNode.Parse(LayaProtocol.Serialize(new
        {
            protocol = LayaProtocol.Version, kind = "ready", packageVersion = LayaConfiguration.PackageVersion,
            packageWheelSha256 = LayaConfiguration.WheelSha256, checkpointRevision = LayaConfiguration.CheckpointRevision,
            weightsSha256 = LayaConfiguration.WeightsSha256, originalInputsSha256 = config.OriginalInputsSha256,
            effectiveInputsSha256 = config.EffectiveInputsSha256, dependencyLockSha256 = config.DependencyLockSha256,
            runnerSha256 = config.RunnerSha256, rendererSha256 = config.RendererSha256, runtimeIdentity = new string('2', 64),
            device = "cpu", cudaVisible = false, torchThreads = config.TorchThreads, interopThreads = config.InteropThreads,
            temperature = 3, fixedoptionOrder = LayaProtocol.Options, maximumInputTokens = 512, headMaximumTokens = 256,
            questionDefinition = LayaProtocol.Question, questionSha256 = LayaProtocol.Hash(LayaProtocol.Question)
        }))!.AsObject();
        ready["readinessIdentity"] = LayaProtocol.Hash(LayaProtocol.Canonical(ready));
        ready["residentMemoryBytes"] = 100_000_000; ready["peakMemoryBytes"] = 120_000_000;
        ready["privateMemoryBytes"] = 100_000_000; ready["peakPrivateBytes"] = 120_000_000; ready["loadMilliseconds"] = 15;
        if (Fault == "gpu") ready["device"] = "cuda";
        if (Fault == "bad-identity") ready["readinessIdentity"] = new string('0', 64);
        if (Fault == "wrong-options") ready["fixedoptionOrder"] = new JsonArray("gap", "met", "insufficient_evidence");
        if (Fault == "wrong-renderer") ready["rendererSha256"] = new string('0', 64);
        var line = ready.ToJsonString();
        if (Fault == "duplicate") line = line.Replace("\"kind\":\"ready\"", "\"kind\":\"ready\",\"kind\":\"ready\"");
        if (Fault == "startup-unavailable") line = LayaProtocol.Serialize(new { protocol = LayaProtocol.Version, kind = "unavailable", errorCode = "native-load-failed" });
        responses.Writer.TryWrite(line);
    }
    public async Task<string> ReadLineAsync(CancellationToken token) => await responses.Reader.ReadAsync(token);
    public Task WriteLineAsync(string line, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var parsed = LayaProtocol.Parse(line); var request = parsed.RootElement;
        var predict = request.GetProperty("kind").GetString() == "predict";
        if (predict) { Predictions++; LastPrediction = line; PredictionStarted.TrySetResult(); } else Preparations++;
        var response = new JsonObject
        {
            ["protocol"] = LayaProtocol.Version, ["kind"] = predict ? "result" : "prepared", ["id"] = request.GetProperty("id").GetString(),
            ["readinessIdentity"] = ready!["readinessIdentity"]!.GetValue<string>(),
            ["stateSha256"] = LayaProtocol.Hash(request.GetProperty("state").GetString()!),
            ["hostContextSha256"] = LayaProtocol.Hash(request.GetProperty("hostContext").GetString()!),
            ["questionSha256"] = LayaProtocol.Hash(LayaProtocol.Question), ["encodedIdsSha256"] = new string('3', 64),
            ["contextTokens"] = 80, ["hostContextTokens"] = HostContextTokens,
            ["inputTokens"] = NativeInputTokens, ["headTokens"] = 20, ["markerPositions"] = new JsonArray(22, 30, 38)
        };
        if (Fault == "reject" || Fault == "predict-reject" && predict) response = new() { ["protocol"] = LayaProtocol.Version, ["kind"] = "rejected",
            ["id"] = request.GetProperty("id").GetString(), ["errorCode"] = "input-token-limit", ["contextTokens"] = 500,
            ["hostContextTokens"] = HostContextTokens, ["inputTokens"] = 560 };
        else if (Fault == "unmeasured-reject") response = new() { ["protocol"] = LayaProtocol.Version, ["kind"] = "rejected",
            ["id"] = request.GetProperty("id").GetString(), ["errorCode"] = "literal-mask-marker" };
        else if (Fault == "host-overflow") response = new() { ["protocol"] = LayaProtocol.Version, ["kind"] = "rejected",
            ["id"] = request.GetProperty("id").GetString(), ["errorCode"] = "host-context-token-limit", ["contextTokens"] = 80,
            ["hostContextTokens"] = 700, ["inputTokens"] = NativeInputTokens };
        else if (predict)
        {
            response["nativeAnswer"] = new JsonObject { ["choice"] = Choice, ["probabilities"] = new JsonArray(.7, .2, .1),
                ["confidence"] = .7, ["answerConfidence"] = .7, ["actionProbability"] = .8 };
            response["elapsedMilliseconds"] = 10; response["temperature"] = 3; response["outputTokens"] = 0;
            foreach (var name in new[] { "residentMemoryBytes", "peakMemoryBytes", "privateMemoryBytes", "peakPrivateBytes" })
                response[name] = ready![name]!.DeepClone();
            if (Fault == "changed-state") response["stateSha256"] = new string('0', 64);
            if (Fault == "changed-count") response["inputTokens"] = 141;
            if (Fault == "unknown-field") response["execute"] = "unrequested";
        }
        if (predict && HoldPredictions is { } hold)
        {
            _ = DeliverAsync(hold.Task, response.ToJsonString());
            return Task.CompletedTask;
        }
        responses.Writer.TryWrite(response.ToJsonString()); return Task.CompletedTask;
    }
    private async Task DeliverAsync(Task hold, string response)
    { await hold; if (Alive) responses.Writer.TryWrite(response); }
    public Task<bool> StopAsync(TimeSpan limit)
    { Stops++; if (StopConfirmed) { Alive = false; responses.Writer.TryComplete(); HoldPredictions?.TrySetResult(); } return Task.FromResult(StopConfirmed); }
}
