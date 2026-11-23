using Cerebral.Core;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class SkillCatalogTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-skills-tests-" + Guid.NewGuid().ToString("N"));
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            assertions++; Console.WriteLine("PASS: " + name);
        }
        bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException) { return true; }
        }
        try
        {
            var store = new StateStore(root); var events = new EventHub(); var catalog = new SkillCatalog(store, events);
            catalog.EnsureBuiltIns();
            var builtIn = catalog.List().Single();
            Check(builtIn.Id == "response-balance" && builtIn.Enabled && builtIn.BuiltIn &&
                  builtIn.Bindings.SequenceEqual([new SkillBinding("global")]),
                "response-balance is an enabled global built-in skill");
            var lead = new AgentProfile { Id = "lead", Role = "lead", Name = "Cerebral", ModelId = "fixture" };
            var helper = new AgentProfile { Id = "helper", Role = "helper", Name = "Helper", ModelId = "fixture" };
            var global = catalog.Resolve(lead, null);
            Check(global.Count == 1 && global[0].Content.Contains("conversation.response-balance", StringComparison.Ordinal),
                "built-in skill resolves as bounded instruction content");
            var unrelated = SkillCatalog.ModelGuidance(global, "Summarize this ordinary note.");
            Check(unrelated.Length == 0,
                "unrelated work receives no skill catalog or guidance layer");
            var relevant = SkillCatalog.ModelGuidance(global, "The generation hit its response limit.");
            Check(relevant.Contains("Selected instruction skills", StringComparison.Ordinal) &&
                  relevant.Contains("Relevant skill response-balance@1.0.0", StringComparison.Ordinal) &&
                  relevant.Contains("Exact prepared", StringComparison.OrdinalIgnoreCase),
                "matching work receives only the selected skill metadata and exact guidance");
            var explicitlyNamed = SkillCatalog.ModelGuidance(global, "Use response-balance for the next run.");
            Check(explicitlyNamed.Contains("Relevant skill response-balance@1.0.0", StringComparison.Ordinal),
                "an explicitly named enabled skill loads without projecting the full enabled catalog");
            var acceptedChoice = new WorkflowLocalGenerationChoice(1, "local.native.text", 262_144, 131_073);
            var fixedGuidance = SkillCatalog.ModelGuidance(global, "The generation hit its response limit.", acceptedChoice);
            Check(fixedGuidance.Contains("smaller complete action", StringComparison.Ordinal) &&
                  fixedGuidance.Contains("new explicit owner-admitted choice", StringComparison.Ordinal) &&
                  !fixedGuidance.Contains("conversation.response-balance", StringComparison.Ordinal) &&
                  !fixedGuidance.Contains("Relevant skill response-balance", StringComparison.Ordinal),
                "accepted local choice replaces only incompatible balance skill guidance");

            var manifest = new SkillManifest
            {
                Id = "fixture-review", Name = "Fixture review", Version = "1.0.0",
                Description = "Review fixture output before completion.", Triggers = ["fixture review", "audit fixture"]
            };
            var installed = catalog.Install(manifest, "# Fixture review\n\nCheck the saved receipt.", "fixture package", null);
            Check(!installed.Summary.Enabled && installed.Summary.Bindings.Count == 0 && installed.Summary.Revision == 1,
                "new skills install disabled and unassigned");
            var configured = catalog.Configure(manifest.Id, true,
                [new("profile", lead.Id), new("project", "special-project")], installed.Summary.Revision);
            Check(configured.Summary.Enabled && catalog.Resolve(lead, null).Any(x => x.Id == manifest.Id) &&
                  !catalog.Resolve(helper, null).Any(x => x.Id == manifest.Id) &&
                  catalog.Resolve(helper, "special-project").Any(x => x.Id == manifest.Id),
                "profile and project bindings resolve without changing agent grants");
            var joinedGuidance = SkillCatalog.ModelGuidance(catalog.Resolve(lead, null),
                "Use response-balance while you audit fixture output.", acceptedChoice);
            Check(joinedGuidance.Contains("Relevant skill fixture-review@1.0.0", StringComparison.Ordinal) &&
                  joinedGuidance.Contains("Check the saved receipt", StringComparison.Ordinal) &&
                  !joinedGuidance.Contains("conversation.response-balance", StringComparison.Ordinal),
                "accepted local choice retains other owner-enabled matching skills");
            var reopenedCatalog = new SkillCatalog(new StateStore(root), new EventHub());
            var reopened = reopenedCatalog.Inspect(manifest.Id);
            Check(reopened.Summary.Enabled && reopened.Summary.Revision == configured.Summary.Revision &&
                  reopened.Summary.Bindings.SequenceEqual(configured.Summary.Bindings) &&
                  reopened.Content == "# Fixture review\n\nCheck the saved receipt.",
                "installed skill content and assignments survive a new store/catalog generation");
            Check(Rejects(() => catalog.Configure(manifest.Id, false, [], installed.Summary.Revision)),
                "stale skill revisions cannot overwrite current configuration");

            store.Put("session", "lead-chat", new TaskSession { Id = "lead-chat", ProfileId = lead.Id });
            var tools = new SkillToolExtension(store, catalog);
            var context = new ToolExecutionContext(lead, "lead-chat", "skill-test") { EnforceProfileScope = true };
            var listed = await tools.ExecuteAsync(new(SkillToolExtension.ListToolName, CerebralJson.Element(new { })), context, default);
            var read = await tools.ExecuteAsync(new(SkillToolExtension.ReadToolName, CerebralJson.Element(new { id = manifest.Id })), context, default);
            Check(listed.Success && listed.Content.Contains(manifest.Id, StringComparison.Ordinal) &&
                  read.Success && read.Content.Contains("Check the saved receipt", StringComparison.Ordinal),
                "agent tools list and read only assigned skill guidance");
            var helperContext = new ToolExecutionContext(helper, "helper-chat", "skill-test") { EnforceProfileScope = true };
            Check(Rejects(() => tools.ExecuteAsync(new(SkillToolExtension.ReadToolName,
                    CerebralJson.Element(new { id = manifest.Id })), helperContext, default).GetAwaiter().GetResult()),
                "an unassigned agent cannot read the skill through the tool");

            var accepted = catalog.Resolve(lead, null);
            var acceptedJson = CerebralJson.Element(accepted).GetRawText();
            var projection = catalog.CaptureModelProjection(accepted, lead, null, "audit fixture; response limit");
            catalog.RequireProjectionCurrent(projection, lead, null);
            var disabled = catalog.Configure(manifest.Id, false, configured.Summary.Bindings, configured.Summary.Revision);
            var withoutDisabled = catalog.CaptureModelProjection(accepted, lead, null, "audit fixture; response limit");
            Check(Rejects(() => catalog.RequireProjectionCurrent(projection, lead, null)) &&
                  !withoutDisabled.Guidance.Contains("Check the saved receipt", StringComparison.Ordinal) &&
                  withoutDisabled.Guidance.Contains("Relevant skill response-balance", StringComparison.Ordinal),
                "future inputs omit disabled skills while independent built-in guidance remains available");
            var enabled = catalog.Configure(manifest.Id, true, configured.Summary.Bindings, disabled.Summary.Revision);
            Check(Rejects(() => catalog.RequireProjectionCurrent(projection, lead, null)),
                "disable and re-enable cannot revive an earlier preparation stamp");
            var changed = catalog.Install(manifest, "# Fixture review\n\nUPDATED-INSTRUCTIONS", "fixture update", enabled.Summary.Revision);
            var frozen = catalog.CaptureModelProjection(accepted, lead, null, "audit fixture");
            var fresh = catalog.CaptureModelProjection(catalog.Resolve(lead, null), lead, null, "audit fixture");
            Check(frozen.Guidance.Length == 0 && fresh.Guidance.Contains("UPDATED-INSTRUCTIONS", StringComparison.Ordinal),
                "frozen inputs never substitute updated bodies while fresh legacy resolution sees the current body");
            var scopeChanged = catalog.Configure(manifest.Id, true, [new("profile", helper.Id)], changed.Summary.Revision);
            Check(Rejects(() => catalog.RequireProjectionCurrent(fresh, lead, null)) &&
                  catalog.CaptureModelProjection(catalog.Resolve(helper, null), lead, null, "audit fixture").Guidance.Length == 0,
                "scope changes invalidate prepared inputs and exclude unassigned skill bodies");
            var restored = catalog.Configure(manifest.Id, true, configured.Summary.Bindings, scopeChanged.Summary.Revision);
            var later = catalog.Install(new SkillManifest { Id = "later-skill", Name = "Later skill", Version = "1.0.0",
                Description = "Added after admission.", Triggers = ["audit fixture"] }, "LATER-SKILL-CANARY", "fixture", null);
            later = catalog.Configure(later.Summary.Id, true, [new("global")], later.Summary.Revision);
            Check(!catalog.CaptureModelProjection(accepted, lead, null, "audit fixture").Guidance.Contains("LATER-SKILL-CANARY", StringComparison.Ordinal) &&
                  CerebralJson.Element(accepted).GetRawText() == acceptedJson,
                "later catalog additions never enter a frozen input or rewrite its accepted snapshot");
            catalog.Remove(later.Summary.Id, later.Summary.Revision);

            var outerCalls = 0; var innerCalls = 0;
            using (SkillDispatchFence.Enter(() => outerCalls++))
            {
                using (SkillDispatchFence.Enter(() => innerCalls++))
                { await Task.Yield(); SkillDispatchFence.RequireCurrent(); }
                SkillDispatchFence.RequireCurrent();
            }
            SkillDispatchFence.RequireCurrent();
            Check(outerCalls == 1 && innerCalls == 1, "request-local dispatch scopes flow through await and restore their previous scope");

            var legacyProjection = catalog.CaptureModelProjection(catalog.Resolve(lead, null), lead, null, "audit fixture");
            store.Put("model", "fixture", new ModelRecord { Id = "fixture", Path = Path.Combine(root, "unused.gguf"), ContextTokens = 8192 });
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Preparation? preparation = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using (var runtime = new ModelRuntime(store, new Configuration(store).Get, events, null,
                async (request, token) =>
                {
                    entered.TrySetResult(true);
                    await release.Task.WaitAsync(token);
                    return preparation = new Preparation(request);
                }))
            using (SkillDispatchFence.Enter(() => catalog.RequireProjectionCurrent(legacyProjection, lead, null)))
            {
                var pending = runtime.GenerateAcceptedTextAsync("fixture", [new("user", "audit fixture")], 8192,
                    512, 0.2, false, null, deadline.Token);
                try
                {
                    await entered.Task.WaitAsync(deadline.Token);
                    catalog.Configure(manifest.Id, false, restored.Summary.Bindings, restored.Summary.Revision);
                }
                finally { release.TrySetResult(true); }
                var rejected = false;
                try { await pending; }
                catch (UnauthorizedAccessException) { rejected = true; }
                Check(rejected && preparation is { Generated: 0, Disposed: 1 },
                    "legacy text rechecks after asynchronous preparation and releases the unused lease without generation");
            }
            SkillDispatchFence.RequireCurrent();
            catalog.Remove(manifest.Id, catalog.Inspect(manifest.Id).Summary.Revision);
            Check(catalog.CaptureModelProjection(accepted, lead, null, "audit fixture").Guidance.Length == 0,
                "removed guidance stays absent from future frozen inputs");
            Check(catalog.List().Count == 1 && Rejects(() => catalog.Remove(builtIn.Id, builtIn.Revision)),
                "owner removal retains protected built-in skills");
            Check(Rejects(() => catalog.Install(new SkillManifest
                {
                    Id = "bad", Name = "Bad", Version = "1", Description = "Bad version", Triggers = ["bad"]
                }, "# Bad", "fixture", null)), "invalid package versions are rejected before persistence");

            Console.WriteLine($"Skill catalog tests passed: {assertions}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Preparation(ModelGenerationRequest request) : IPreparedModelGeneration
    {
        public int Generated;
        public int Disposed;
        public PreparedModelMetadata Metadata { get; } = new("fixture", "skill-fixture", "1.0.0", request.ModelId,
            1, request.ContextLimit, request.ResponseLimit, "deterministic fixture");
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
        { Generated++; return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, "fixture")); }
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
    }
}
