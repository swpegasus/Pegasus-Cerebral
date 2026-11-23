using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Activation;
using Cerebral.ReviewPlugin;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Actual Worker process, secured owner pipe, scheduler and example feature plugin. No model,
/// network worker, installed service, PAO operation or generated acceptance plugin is invoked.</summary>
internal static class EngineHostTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-engine-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        LocalWorkerHostTests.ChildWorker? child = null;
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        try
        {
            var data = Path.Combine(root, "data"); var packages = Path.Combine(root, "engines");
            var features = Path.Combine(root, "plugins"); var feature = Path.Combine(features, ExampleReviewPlugin.ModuleId);
            Directory.CreateDirectory(feature);
            foreach (var name in new[] { "Cerebral.ReviewPlugin.dll", "Cerebral.Core.dll", "Cerebral.Contracts.dll", "Microsoft.Data.Sqlite.dll" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(feature, name));
            foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory, "SQLitePCLRaw*.dll"))
                File.Copy(source, Path.Combine(feature, Path.GetFileName(source)));
            foreach (var source in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "runtimes"), "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(feature, Path.GetRelativePath(AppContext.BaseDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
            }
            File.WriteAllText(Path.Combine(feature, "service-plugin.json"), """
                {"schemaVersion":1,"contract":"pegasus.cerebral.service","apiVersion":1,"id":"example.review",
                "name":"Review example","version":"1.0.0","description":"Engine acceptance feature",
                "assembly":"Cerebral.ReviewPlugin.dll","entryType":"Cerebral.ReviewPlugin.ExampleReviewPlugin"}
                """);
            Pack(packages, "A", true); Pack(packages, "B", false);
            LocalWorkerHostTests.Seed(data, root, 9); SqliteConnection.ClearAllPools();
            var seedStore = new StateStore(data);
            seedStore.Put("model", "idle-policy", new ModelRecord { Id = "idle-policy", Name = "Idle policy", IdleMinutes = 5 });
            child = await LocalWorkerHostTests.ChildWorker.StartAsync(data, features, deadline.Token, packages);
            using var client = new CerebralClient(child.Endpoint);
            async Task<CommandResult> Call(string op, object? value = null, string? profile = null) =>
                await client.CommandAsync(op, value, profileId: profile, cancellationToken: deadline.Token);
            async Task<T> Read<T>(string op, object? value = null) =>
                await client.GetAsync<T>(op, value, cancellationToken: deadline.Token);
            var identity = await Read<HostInfo>("health");
            var pluginDiagnostics = await Read<JsonElement>("diagnostics");
            var reviewStatus = pluginDiagnostics.GetProperty("servicePlugins").EnumerateArray().SingleOrDefault(item => item.GetProperty("id").GetString() == ExampleReviewPlugin.ModuleId);
            if (reviewStatus.ValueKind == JsonValueKind.Undefined || !reviewStatus.GetProperty("loaded").GetBoolean())
                throw new InvalidOperationException("Review feature did not load: " + pluginDiagnostics.GetProperty("servicePlugins").GetRawText());
            var initial = await Read<EngineStatus>("engine.status");
            Check(!(await Call("models.idle.set", new { id = "idle-policy", idleMinutes = 0 }, "pipe-admin")).Success,
                "Administration agents cannot change owner model residency policy.");
            Check(!(await Call("models.idle.set", new { id = "idle-policy", idleMinutes = 0, unexpected = true })).Success,
                "Idle policy command rejects extra fields before mutation.");
            Check((await Read<ModelRecord>("models.idle.set", new { id = "idle-policy", idleMinutes = 0 })).IdleMinutes == 0,
                "Owner can save residency policy through the actual secured pipe without loading a model.");
            Check(initial.Active?.Slot == "A" && !initial.Paused, "Real Worker must start with default engine A.");
            foreach (var op in new[] { "engine.status", "engine.pause", "engine.resume", "engine.activate", "engine.rollback", "engine.promote" })
                Check(!(await Call(op, new { slot = "B", expectedEpoch = initial.Epoch }, "pipe-admin")).Success, "An administration-capable agent cannot control engine lifecycle: " + op);
            Check(!(await Call("engine.pause", new { expectedEpoch = initial.Epoch, unexpected = true })).Success, "Owner command rejects undeclared arguments.");
            await Read<EngineStatus>("engine.pause", new { expectedEpoch = initial.Epoch });
            var submitted = await Read<WorkflowRun>("jobs.workflow.submit", new SubmitWorkflowRequest("engine-live-work", "engine-live-chat", "pipe-reviewer", ExampleReviewPlugin.DefinitionId, "1.0.0", "Measure this text.", null, CerebralJson.Element(new { text = "one two three" })));
            await Task.Delay(300, deadline.Token);
            Check((await Read<WorkflowRun>("jobs.workflow.get", new { id = submitted.Id })).Status == WorkflowStatus.Queued, "Real scheduler retains accepted work while engine is paused.");
            var candidate = await Read<EngineStatus>("engine.activate", new { slot = "B", expectedEpoch = initial.Epoch });
            Check(candidate.CandidatePending && candidate.Active?.Id == "fixture.engine", "Real Worker loads the independent candidate without changing its pipe.");
            Check(!(await Call("engine.promote", new { expectedEpoch = candidate.Epoch })).Success, "Startup alone cannot promote a candidate.");
            await Read<EngineStatus>("engine.resume", new { expectedEpoch = candidate.Epoch });
            var waiting = await WaitRun(client, submitted.Id, run => run.Questions is not null, deadline.Token);
            var paused = await Read<EngineStatus>("engine.pause", new { expectedEpoch = candidate.Epoch });
            var rollback = await Read<EngineStatus>("engine.rollback", new { expectedEpoch = paused.Epoch });
            Check(rollback.Active?.Slot == "A" && (await Read<WorkflowRun>("jobs.workflow.get", new { id = submitted.Id })).Revision == waiting.Revision, "Code rollback keeps the latest feature-owned questions and journal revision.");
            await Read<EngineStatus>("engine.resume", new { expectedEpoch = rollback.Epoch });
            await Read<WorkflowRun>("jobs.workflow.answer", new WorkflowAnswerRequest("engine-live-answer", waiting.Id, waiting.Questions!.Id, waiting.Questions.Revision, new Dictionary<string, string> { ["label"] = "Engine acceptance", ["style"] = "concise" }));
            var completed = await WaitRun(client, waiting.Id, run => run.Status == WorkflowStatus.Completed, deadline.Token);
            Check(completed.Result?.GetProperty("wordCount").GetInt32() == 3, "The same existing feature completes after the alternate engine and rollback.");
            Check((await Read<HostInfo>("health")).InstallationId == identity.InstallationId, "Host identity remains unchanged through engine switches.");
            var current = await Read<EngineStatus>("engine.status");
            await Read<EngineStatus>("engine.pause", new { expectedEpoch = current.Epoch });
            Pack(packages, "B", false, "stale-epoch");
            candidate = await Read<EngineStatus>("engine.activate", new { slot = "B", expectedEpoch = current.Epoch });
            await Read<EngineStatus>("engine.resume", new { expectedEpoch = candidate.Epoch });
            var failureWork = await Read<WorkflowRun>("jobs.workflow.submit", new SubmitWorkflowRequest("engine-failed-candidate", "engine-failed-chat", "pipe-reviewer", ExampleReviewPlugin.DefinitionId, "1.0.0", "Measure another text.", null, CerebralJson.Element(new { text = "four five" })));
            var recovered = await WaitRun(client, failureWork.Id, run => run.Questions is not null, deadline.Token);
            current = await Read<EngineStatus>("engine.status");
            Check(current.Active?.Slot == "A" && !current.Paused && !current.CandidatePending && current.Recovery is not null, "Actual scheduler automatically restores a failed candidate and advances the same accepted work.");
            await Read<WorkflowRun>("jobs.workflow.answer", new WorkflowAnswerRequest("engine-failed-answer", recovered.Id, recovered.Questions!.Id, recovered.Questions.Revision, new Dictionary<string, string> { ["label"] = "Recovered", ["style"] = "concise" }));
            await WaitRun(client, recovered.Id, run => run.Status == WorkflowStatus.Completed, deadline.Token);
            await Read<JsonElement>("service.stop-local"); await child.WaitForExitAsync(deadline.Token);
            Check(child.ExitCode == 0, "Engine-enabled Worker shuts down cleanly.");
            await child.DisposeAsync(); child = null;
            // Missing engine still allows independent diagnostics and owner configuration.
            child = await LocalWorkerHostTests.ChildWorker.StartAsync(data, features, deadline.Token, Path.Combine(root, "missing-engines"));
            using var missing = new CerebralClient(child.Endpoint);
            var status = await missing.GetAsync<EngineStatus>("engine.status", cancellationToken: deadline.Token);
            var settings = await missing.GetAsync<HostSettings>("settings.get", cancellationToken: deadline.Token);
            Check(status.Active is null && status.Paused && status.Error is not null && settings.InstallationId == identity.InstallationId, "Real Worker remains diagnosable and configurable with no healthy engine.");
            var stop = await missing.CommandAsync("service.stop-local", cancellationToken: deadline.Token);
            Check(stop.Success, "Owner can close the engine-unavailable Worker normally.");
            await child.WaitForExitAsync(deadline.Token);
            Console.WriteLine($"Engine host checks passed: {checks} (actual child Worker, secured pipe, scheduler, existing example feature and SQLite; no model inference or installed UI evidence).");
        }
        finally
        {
            if (child is not null) await child.DisposeAsync();
            SqliteConnection.ClearAllPools();
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("cerebral-engine-host-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid engine fixture cleanup path.");
            Directory.Delete(root, true);
        }
    }

    private static async Task<WorkflowRun> WaitRun(CerebralClient client, string id, Func<WorkflowRun, bool> ready, CancellationToken ct)
    {
        while (true)
        {
            var run = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id }, cancellationToken: ct);
            if (ready(run)) return run;
            if (run.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled) throw new InvalidOperationException("Live engine work failed: " + run.Error);
            await Task.Delay(50, ct);
        }
    }
    internal static void Pack(string root, string slot, bool defaultEngine, string mode = "echo")
    {
        var path = Path.Combine(root, slot); Directory.CreateDirectory(path);
        var dll = defaultEngine ? "Cerebral.Engine.Default.dll" : "Cerebral.EngineFixture.dll";
        File.Copy(Path.Combine(AppContext.BaseDirectory, "EngineFixtures", dll), Path.Combine(path, dll), true);
        var config = Encoding.UTF8.GetBytes("[settings]\nmode=" + mode + "\n");
        File.WriteAllBytes(Path.Combine(path, "engine-settings.ini"), config);
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(path, "engine.ini"), $"[engine]\nid={(defaultEngine ? "pegasus.default-engine" : "fixture.engine")}\nversion=1.0.0\nprotocol=pegasus.cerebral.engine/1\nassembly={dll}\nassembly-sha256={Hash(File.ReadAllBytes(Path.Combine(path, dll)))}\nentry-type={(defaultEngine ? "Cerebral.Engine.Default.DefaultEnginePlugin" : "Cerebral.EngineFixture.FixtureEngine")}\nconfiguration-sha256={Hash(config)}\n", new UTF8Encoding(false));
    }
}
