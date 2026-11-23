using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Context;
using Cerebral.Engine.Workflows;
using Cerebral.PersonalityServicePlugin;

// The retained 1.0.2 harness stays historical; successor verification has an explicit entry.
if (args.Length == 2 && args[0] == "--transfer")
{
    await SuccessorTests.RunAsync(args[1]);
    await PendingTransferTests.RunAsync(args[1]);
    return;
}
if (args.Length == 2 && args[0] is "--successor" or "--chat")
{
    await SuccessorTests.RunAsync(args[1]);
    return;
}
if (args.Length != 1) throw new ArgumentException("Pass the task-owned fixture directory under this workspace's .tmp.");
var workspace = Path.GetFullPath(Environment.CurrentDirectory);
var allowed = Path.GetFullPath(args[0]);
if (!allowed.StartsWith(Path.Combine(workspace, ".tmp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Fixtures must stay under this workspace's .tmp.");
var fixture = Path.Combine(allowed, "fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    checks++; Console.WriteLine("PASS: " + label);
}
void Reject(Action action, string label)
{
    var rejected = false;
    try { action(); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or JsonException)
    { rejected = true; }
    Check(rejected, label);
}
async Task RejectAsync(Func<Task> action, string label)
{
    var rejected = false;
    try { await action(); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or JsonException)
    { rejected = true; }
    Check(rejected, label);
}
JsonElement Json(object value) => PersonalityContract.Element(value);
async Task<JsonElement> Command(PersonalityServicePlugin plugin, string operation, object arguments) =>
    Json((await plugin.ExecuteAsync(operation, Json(arguments), default))!);
async Task<JsonElement> Settings(PersonalityServicePlugin plugin) => await Command(plugin, "personality.settings", new { });
async Task<JsonElement> Select(PersonalityServicePlugin plugin, JsonElement settings, bool enabled, string id) =>
    await Command(plugin, "personality.select", new
    {
        expectedRevision = settings.GetProperty("revision").GetInt64(),
        expectedSha256 = settings.GetProperty("sha256").GetString(), enabled, presetId = id
    });
async Task<JsonElement> Save(PersonalityServicePlugin plugin, JsonElement settings, string text, string id = "fixture") =>
    await Command(plugin, "personality.save", new
    {
        expectedRevision = settings.GetProperty("revision").GetInt64(),
        expectedSha256 = settings.GetProperty("sha256").GetString(), id, name = "Fixture style", text
    });
ContextProviderRequest Request(JsonElement selection, int bytes = PersonalityContract.MaximumUtf8Bytes) =>
    new(selection.GetProperty("providerId").GetString()!, selection.GetProperty("providerVersion").GetInt32(),
        [new(selection.GetProperty("contributionId").GetString()!, selection.GetProperty("referenceKind").GetString()!,
            selection.GetProperty("sourceId").GetString()!, selection.GetProperty("sourceRevision").GetInt64(),
            ContextRequirement.Optional, 0, BeforeCurrentInput: true)], bytes, ContextRevision: 4, ExclusionRevision: 2);
WorkflowSubmission Owner() => new("request-fixture", "conversation-fixture", "profile-fixture",
    ConversationWorkflowContract.DefinitionId, ConversationWorkflowContract.DefinitionVersion, "Visible fixture request",
    null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, Caller: new(EventActorKind.User, "owner"));
WorkflowCommandContext Context(ContextProviderRequest request, WorkflowSubmission? submission = null) =>
    new("effect-fixture", submission ?? Owner(), Json(request)) { RunId = "run-fixture" };
string SourceText(WorkflowCommandResult result) =>
    ((TextContentPart)result.Data!.Value.Deserialize<ContextProviderResolution>(PersonalityContract.Json)!
        .Sources.Single().Messages.Single().Content.Single()).Text;

try
{
    var data = Path.Combine(fixture, "plugin-data");
    using var plugin = new PersonalityServicePlugin();
    Reject(() => plugin.Initialize(new FixtureHost(Path.Combine(fixture, "bad-id"), "another.package")),
        "initialization rejects a mismatched service package identity");
    plugin.Initialize(new FixtureHost(data));
    Reject(() => plugin.Initialize(new FixtureHost(data)), "a second initialization is rejected");
    Check(plugin.ToolExtensions.Count == 0 && plugin.Workflows.Count == 0 && plugin.CommandExtensions.Single() == plugin,
        "package contributes owner commands and context only, with no model tools or workflow entrypoints");
    var registry = new WorkflowRegistry([plugin]);
    var provider = registry.GetContextProvider(PersonalityContract.ProviderId, 1);
    Check(registry.ContextProviders.Count == 1 && provider.Provider.MaxReferences == 1 &&
        provider.Descriptor.ReplayPolicy == WorkflowReplayPolicy.Idempotent && provider.Descriptor.RequiredCapabilities.Count == 0,
        "actual WorkflowRegistry admits the unchanged context SPI with bounded idempotent descriptor");
    var settings = await Settings(plugin);
    Check(!settings.GetProperty("enabled").GetBoolean() && settings.GetProperty("selection").ValueKind == JsonValueKind.Null &&
        settings.GetProperty("selectedPresetId").GetString() == "cerebral" &&
        settings.GetProperty("revision").GetInt64() == 0 && !File.Exists(Path.Combine(data, PersonalityStore.FileName)),
        "new package defaults to disabled, selected Cerebral, and no persisted settings file");
    var list = await Command(plugin, "personality.list", new { });
    Check(list.GetProperty("presets").GetArrayLength() == 2 &&
        list.GetProperty("presets").EnumerateArray().All(item => item.GetProperty("readOnly").GetBoolean() &&
            item.GetProperty("text").ValueKind == JsonValueKind.Null),
        "owner list supplies two immutable generic presets without source bodies");
    var get = await Command(plugin, "personality.get", new { id = "cerebral" });
    var text = get.GetProperty("text").GetString()!;
    Check(get.GetProperty("sha256").GetString() == PersonalityContract.Digest(Encoding.UTF8.GetBytes(text)) &&
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 120 &&
        text.Contains("latest user correction", StringComparison.Ordinal),
        "redistributable Cerebral preset is compact and content-hashed without a private profile copy");
    settings = await Select(plugin, settings, true, "cerebral");
    var selection = settings.GetProperty("selection").Clone();
    var request = Request(selection);
    _ = ContextProviderNormalizer.ReadRequest(provider.Provider, Json(request));
    var context = Context(request);
    var resolved = await provider.ExecuteAsync(context, default);
    Check(resolved.State == WorkflowEffectState.Succeeded && resolved.ProviderReceiptId == context.EffectId,
        "provider resolves the exact accepted source with a durable effect receipt");
    var normalized = ContextProviderNormalizer.Normalize(provider.Provider, request,
        ContextProviderNormalizer.ReadResolution(resolved.Data!.Value), context.EffectId, resolved.ProviderReceiptId!);
    var source = normalized.Window.Contributions.Single();
    Check(source.Id == PersonalityContract.ContributionId && source.Requirement == ContextRequirement.Optional &&
        source.BeforeCurrentInput && source.Complete && source.Messages.Single().Role == "user" &&
        source.ExactReadHandle is not null && ContextValidation.GetCoveredManifest(normalized.Window).Count == 0,
        "actual normalizer produces one optional non-system source excluded from compaction summaries");
    Check(JsonSerializer.SerializeToUtf8Bytes(resolved.Data.Value).Length <= PersonalityContract.MaximumUtf8Bytes &&
        SourceText(resolved).EndsWith(text, StringComparison.Ordinal),
        "provider result stays within its serialized bound and retains the complete exact source");
    var disabled = await Select(plugin, settings, false, "succinct");
    var reconciled = await provider.ReconcileAsync(context, default);
    Check(disabled.GetProperty("selection").ValueKind == JsonValueKind.Null &&
        reconciled.State == WorkflowEffectState.Succeeded && reconciled.Data!.Value.GetRawText() == resolved.Data.Value.GetRawText(),
        "switching or disabling affects new selection while reconciliation retains the accepted source");
    await RejectAsync(async () => { await Select(plugin, settings, true, "cerebral"); },
        "stale settings revision and SHA reject lost updates");
    await RejectAsync(async () => { await Save(plugin, disabled, "Fixture text", "cerebral"); },
        "owner save cannot overwrite a built-in preset");
    await RejectAsync(async () => { await Save(plugin, disabled, new string('x', 1025)); },
        "oversized custom text is rejected before publication");
    await RejectAsync(async () => { await Save(plugin, disabled, string.Join(' ', Enumerable.Repeat("x", 121))); },
        "custom text exceeding the word limit is rejected before publication");
    var saved1 = await Save(plugin, disabled, "Use short, clear fixture answers.");
    settings = saved1.GetProperty("settings");
    settings = await Select(plugin, settings, true, "fixture");
    var customRequest = Request(settings.GetProperty("selection"));
    var oldCustom = await provider.ExecuteAsync(Context(customRequest), default);
    var saved2 = await Save(plugin, settings, "Use clear fixture answers with essential detail.");
    settings = saved2.GetProperty("settings");
    Check(settings.GetProperty("selection").GetProperty("sourceRevision").GetInt64() == 2 &&
        settings.GetProperty("selection").GetProperty("sourceId").GetString() != customRequest.References.Single().SourceId,
        "custom save appends a new source revision and future selection identity");
    var oldReconciled = await provider.ReconcileAsync(Context(customRequest), default);
    Check(oldReconciled.Data!.Value.GetRawText() == oldCustom.Data!.Value.GetRawText(),
        "a custom edit preserves the exact earlier revision for accepted work and retries");
    var exactGet = await Command(plugin, "personality.get", new { id = "fixture", revision = 1 });
    Check(exactGet.GetProperty("sha256").GetString() == saved1.GetProperty("preset").GetProperty("sha256").GetString(),
        "owner exact get retrieves an earlier retained revision by identity");
    using (var reloaded = new PersonalityServicePlugin())
    {
        reloaded.Initialize(new FixtureHost(data));
        var reloadedSettings = await Settings(reloaded);
        Check(reloadedSettings.GetProperty("sha256").GetString() == settings.GetProperty("sha256").GetString() &&
            reloadedSettings.GetProperty("selection").GetProperty("sourceRevision").GetInt64() == 2,
            "atomic durable configuration and source versions survive a fresh plugin instance");
        var reloadProvider = (IContextProviderCommandHandler)reloaded.Commands.Single();
        Check((await reloadProvider.ReconcileAsync(Context(customRequest), default)).Data!.Value.GetRawText() ==
            oldCustom.Data.Value.GetRawText(), "accepted source reconciliation survives plugin reload");
    }
    var invalidReferences = new[]
    {
        customRequest.References[0] with { Requirement = ContextRequirement.Required },
        customRequest.References[0] with { Requirement = ContextRequirement.Reducible },
        customRequest.References[0] with { BeforeCurrentInput = false },
        customRequest.References[0] with { Priority = 1 },
        customRequest.References[0] with { Kind = "another.kind" },
        customRequest.References[0] with { ExpectedRevision = null },
        customRequest.References[0] with { SourceId = customRequest.References[0].SourceId[..^1] + (customRequest.References[0].SourceId[^1] == '0' ? '1' : '0') },
        customRequest.References[0] with { SourceId = customRequest.References[0].SourceId.Replace("/1/", "/01/", StringComparison.Ordinal) }
    };
    foreach (var reference in invalidReferences)
        Check((await provider.ExecuteAsync(Context(customRequest with { References = [reference] }), default)).State ==
            WorkflowEffectState.Failed, "provider rejects a changed exact identity, requirement, or placement");
    var invalidSubmissions = new[]
    {
        Owner() with { Caller = new(EventActorKind.Agent, "owner") },
        Owner() with { Caller = new(EventActorKind.Service, "owner") },
        Owner() with { Caller = new(EventActorKind.User, "another-user") },
        Owner() with { Caller = null },
        Owner() with { ParentRunId = "helper-parent" },
        Owner() with { DefinitionId = "fixture:helper" }
    };
    foreach (var submission in invalidSubmissions)
        Check((await provider.ExecuteAsync(Context(customRequest, submission), default)).State == WorkflowEffectState.Failed,
            "provider refuses helper, service, missing, or non-owner entrypoint lineage");
    Check((await provider.ExecuteAsync(Context(customRequest with { References = [] }), default)).State == WorkflowEffectState.Failed &&
        (await provider.ExecuteAsync(Context(customRequest with { MaximumUtf8Bytes = 1 }), default)).State == WorkflowEffectState.Failed,
        "disabled admission must omit invocation, and too-small requested bounds fail explicitly");
    await RejectAsync(async () =>
    {
        await plugin.ExecuteAsync("personality.settings", PersonalityContract.Parse("{\"extra\":true}"), default);
    }, "owner command rejects unknown arguments");
    await RejectAsync(async () =>
    {
        await plugin.ExecuteAsync("personality.get", PersonalityContract.Parse("{\"id\":\"cerebral\",\"id\":\"succinct\"}"), default);
    }, "owner command rejects duplicate JSON fields");
    var unchanged = await Select(plugin, settings, true, "fixture");
    Check(unchanged.GetProperty("sha256").GetString() == settings.GetProperty("sha256").GetString(),
        "same selection is an exact no-op against the current CAS");
    var sameSave = await Save(plugin, settings, "Use clear fixture answers with essential detail.");
    Check(sameSave.GetProperty("settings").GetProperty("revision").GetInt64() == settings.GetProperty("revision").GetInt64(),
        "same custom source save is an exact no-op against the current CAS");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        var caught = false;
        try { await provider.ExecuteAsync(context, canceled.Token); } catch (OperationCanceledException) { caught = true; }
        Check(caught, "provider preserves cancellation");
        caught = false;
        try { await plugin.ExecuteAsync("personality.select", Json(new { }), canceled.Token); }
        catch (OperationCanceledException) { caught = true; }
        Check(caught && (await Settings(plugin)).GetProperty("sha256").GetString() == settings.GetProperty("sha256").GetString(),
            "canceled owner operations preserve durable configuration");
    }
    for (var revision = 3; revision <= 32; revision++)
    {
        var saved = await Save(plugin, settings, "Fixture version " + revision + ".");
        settings = saved.GetProperty("settings");
    }
    await RejectAsync(async () => { await Save(plugin, settings, "Fixture version beyond the retained quota."); },
        "full retained-version store rejects another save without automatic eviction");
    var full = await Settings(plugin);
    Check(full.GetProperty("retention").GetProperty("retainedCustomVersions").GetInt32() == 32 &&
        !full.GetProperty("retention").GetProperty("automaticallyEvicts").GetBoolean() &&
        (await provider.ReconcileAsync(Context(customRequest), default)).Data!.Value.GetRawText() == oldCustom.Data.Value.GetRawText(),
        "full-state diagnostics are actionable and earliest accepted custom sources remain available");
    using (var limited = new PersonalityServicePlugin())
    {
        limited.Initialize(new FixtureHost(Path.Combine(fixture, "preset-limit")));
        var limitedSettings = await Settings(limited);
        for (var index = 0; index < 8; index++)
            limitedSettings = (await Save(limited, limitedSettings, "Fixture style.", "fixture-" + index)).GetProperty("settings");
        await RejectAsync(async () => { await Save(limited, limitedSettings, "Another fixture style.", "fixture-nine"); },
            "custom preset count is bounded independently of retained revisions");
    }
    plugin.Dispose();
    Check((await provider.ExecuteAsync(context, default)).State == WorkflowEffectState.Failed,
        "disposed plugin supplies no source through an old provider handle");
    await RejectAsync(async () => { await Settings(plugin); }, "disposed plugin rejects owner commands");

    var damaged = Path.Combine(fixture, "damaged");
    Directory.CreateDirectory(damaged);
    File.WriteAllText(Path.Combine(damaged, PersonalityStore.FileName),
        "{\"schemaVersion\":1,\"revision\":0,\"enabled\":false,\"selectedPresetId\":\"cerebral\",\"versions\":[],\"unexpected\":1}");
    using var corrupt = new PersonalityServicePlugin();
    Reject(() => corrupt.Initialize(new FixtureHost(damaged)), "unknown durable fields fail closed without resetting user configuration");
    var oversized = Path.Combine(fixture, "oversized");
    Directory.CreateDirectory(oversized);
    File.WriteAllBytes(Path.Combine(oversized, PersonalityStore.FileName), new byte[PersonalityStore.MaximumFileBytes + 1]);
    using var huge = new PersonalityServicePlugin();
    Reject(() => huge.Initialize(new FixtureHost(oversized)), "oversized durable storage fails before an unbounded read");
    await AdmissionTests.RunAsync(fixture, Check);
    await AdmissionSeedTests.RunAsync(fixture, Check);
    var commandStart = checks;
    await AdmissionCommandTests.RunAsync(fixture, (condition, label) =>
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++;
    });
    Console.WriteLine($"PASS: {checks - commandStart} focused admission command checks (closed JSON, scope, preserved state, cancellation and lifecycle).");
    Console.WriteLine($"PASS: {checks} focused personality configuration/provider checks (private fixtures; no live host or model).");
}
finally
{
    var resolved = Path.GetFullPath(fixture);
    if (resolved.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        Directory.Delete(resolved, recursive: true);
}

sealed class FixtureHost(string directory, string id = PersonalityContract.PluginId) : IServicePluginHost
{
    public string PluginId => id;
    public string PackageDirectory => directory;
    public string DataDirectory => directory;
    public void Publish(string eventType, string? jobId, object? data) { }
}
