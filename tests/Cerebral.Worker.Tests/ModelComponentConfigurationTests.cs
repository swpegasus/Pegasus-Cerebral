using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Real temporary SQLite/file/ZIP commands, with header-only GGUF fixtures; never model inference or installed-state changes.</summary>
public static class ModelComponentConfigurationTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-model-components-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; }
        bool Reject(Action action) { try { action(); return false; } catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException) { return true; } }
        try
        {
            await using var fixture = new Fixture(Path.Combine(root, "state"));
            var repository = fixture.Configuration.Get().ModelDirectory;
            Directory.CreateDirectory(repository);
            var modelPath = Path.Combine(repository, "base.gguf");
            var projectorPath = Path.Combine(repository, "selected-projector.gguf");
            var otherProjector = Path.Combine(repository, "other-projector.gguf");
            byte[] weights = Gguf("weights"), projector = Gguf("selected component"), second = Gguf("other component");
            await File.WriteAllBytesAsync(modelPath, weights);
            await File.WriteAllBytesAsync(projectorPath, projector);
            await File.WriteAllBytesAsync(otherProjector, second);
            var initial = new ModelRecord { Id = "model", Name = "Fixture model", Path = modelPath, License = "test-license",
                ContextTokens = 4096, AutomaticContextSizing = false, SizeBytes = weights.Length, State = "unloaded" };
            fixture.Store.Put("model", initial.Id, initial);
            Task<CommandResult> Call(string op, object value, CancellationToken ct = default) =>
                fixture.Commands.ExecuteAsync(new(op, CerebralJson.Element(value)), ct);
            ModelRecord Current() => fixture.Store.Get<ModelRecord>("model", "model")!;
            string Saved() => JsonSerializer.Serialize(Current(), CerebralJson.Options);

            Check((await Call("models.configure", new ConfigureModelComponentRequest("model", projectorPath))).Success,
                "explicit component selection succeeds through shared command");
            Check(Current().VisionProjectorPath == projectorPath && Current().Path == modelPath && Current().License == "test-license",
                "component patch preserves unrelated model settings and weight identity");
            Check(!Current().CapabilityStatus.Any(x => x.Id == "image-input" && x.Usable),
                "component configuration does not assert usable image capability");
            var legacy = JsonSerializer.SerializeToNode(Current(), CerebralJson.Options)!.AsObject();
            legacy.Remove("visionProjectorPath"); legacy["name"] = "Older editor update";
            Check((await Call("models.save", legacy)).Success && Current().VisionProjectorPath == projectorPath,
                "older full-model saves without the component field preserve its binding");
            Check((await Call("models.configure", new ConfigureModelComponentRequest("model", null))).Success && Current().VisionProjectorPath is null,
                "explicit null clears only the selected component");
            Check((await Call("models.configure", new ConfigureModelComponentRequest("model", projectorPath))).Success,
                "component can be selected again without any load");

            var beforeInvalid = Saved();
            var badHeader = Path.Combine(repository, "bad.gguf"); await File.WriteAllTextAsync(badHeader, "NOT-GGUF");
            foreach (var invalid in new[] { "relative.gguf", "", Path.Combine(repository, "..", "escape.gguf"),
                Path.Combine(repository, "wrong.txt"), Path.Combine(repository, "missing.gguf"), badHeader, projectorPath + "\0" })
                Check(!(await Call("models.configure", new ConfigureModelComponentRequest("model", invalid))).Success && Saved() == beforeInvalid,
                    "invalid or unavailable component preserves the accepted binding");
            Check(!(await Call("models.configure", new { id = "model" })).Success &&
                !(await Call("models.configure", new { id = "model", visionProjectorPath = projectorPath, unexpected = true })).Success,
                "typed patch requires explicit presence and rejects unknown fields");
            var actor = new AgentProfile { Id = "ordinary", ModelId = "model" }; fixture.Store.Put("agent", actor.Id, actor);
            Check(!(await fixture.Commands.ExecuteAsync(new("models.configure",
                CerebralJson.Element(new ConfigureModelComponentRequest("model", null)), actor.Id))).Success,
                "ordinary model input cannot mutate component configuration");
            fixture.Store.Delete("agent", actor.Id);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Check(!(await Call("models.configure", new ConfigureModelComponentRequest("model", otherProjector), cancelled.Token)).Success && Saved() == beforeInvalid,
                    "cancelled component update preserves state");
            }
            var loaded = Current(); loaded.State = "loaded"; fixture.Store.Put("model", loaded.Id, loaded);
            Check(!(await Call("models.configure", new ConfigureModelComponentRequest("model", otherProjector))).Success &&
                Current().VisionProjectorPath == projectorPath, "declared loaded-state fixture refuses component replacement");
            loaded.State = "unloaded"; fixture.Store.Put("model", loaded.Id, loaded);
            var assignment = new AgentJob { Id = "accepted", ProfileId = "old", Profile = new AgentProfile { Id = "old", ModelId = "model" }, Status = "queued" };
            fixture.Store.Put("job", assignment.Id, assignment);
            Check(!(await Call("models.configure", new ConfigureModelComponentRequest("model", otherProjector))).Success &&
                Current().VisionProjectorPath == projectorPath, "accepted assignment blocks a component change");
            fixture.Store.Delete("job", assignment.Id);
            var acceptedProfile = new AgentProfile { Id = "durable-owner", ModelId = "model", ModelWorkerId = "local" };
            var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, acceptedProfile, "", "",
                WorkerExecutionSnapshot.Bind(Current()), [], [], [], [], false, "fixture", [], []);
            var durable = fixture.Workflows.Store.Submit(new WorkflowSubmission("durable-work", "durable-chat", acceptedProfile.Id,
                "fixture:waiting", "1.0.0", "Awaiting a decision", null, WorkflowContract.EmptyObject, CerebralJson.Element(snapshot)));
            fixture.Store.Put("agent", acceptedProfile.Id, new AgentProfile { Id = acceptedProfile.Id, ModelId = "other" });
            Check(!(await Call("models.configure", new ConfigureModelComponentRequest("model", otherProjector))).Success &&
                !(await Call("models.save", Current())).Success && Current().VisionProjectorPath == projectorPath,
                "rebuilt workflow blocks changes using accepted model binding after the current profile changes");
            fixture.Workflows.Store.Cancel(durable.Id);
            var compactorSnapshot = snapshot with
            {
                Model = WorkerExecutionSnapshot.Bind(new ModelRecord { Id = "other", Path = modelPath }),
                Profile = new AgentProfile { Id = acceptedProfile.Id, ModelId = "other", ModelWorkerId = "local" },
                Compactor = new(acceptedProfile, "", WorkerExecutionSnapshot.Bind(Current()))
            };
            var compacting = fixture.Workflows.Store.Submit(new WorkflowSubmission("durable-compactor", "compactor-chat", acceptedProfile.Id,
                "fixture:waiting", "1.0.0", "Future compaction", null, WorkflowContract.EmptyObject, CerebralJson.Element(compactorSnapshot)));
            Check(!(await Call("models.configure", new ConfigureModelComponentRequest("model", otherProjector))).Success,
                "accepted compactor binding also prevents component changes");
            fixture.Workflows.Store.Cancel(compacting.Id);
            var uncertain = fixture.Workflows.Store.Submit(new WorkflowSubmission("durable-uncertain", "uncertain-chat", acceptedProfile.Id,
                "fixture:waiting", "1.0.0", "Uncertain completed effect", null, WorkflowContract.EmptyObject, CerebralJson.Element(snapshot)));
            uncertain = fixture.Workflows.Store.TryClaim(uncertain.Id)!;
            var descriptor = new WorkflowCommandDescriptor("fixture:uncertain", 1, "Fixture uncertain effect",
                WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, []);
            uncertain = fixture.Workflows.Store.ApplyStep(uncertain.Id, uncertain.Revision,
                new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, WorkflowContract.EmptyObject)), descriptor);
            uncertain = fixture.Workflows.Store.DispatchEffect(uncertain.Id, uncertain.Revision);
            uncertain = fixture.Workflows.Store.CompleteEffect(uncertain.Id, uncertain.Effect!.Id,
                new(WorkflowEffectState.Unknown, Error: "Fixture lost the receipt."));
            uncertain = fixture.Workflows.Store.Cancel(uncertain.Id);
            Check(uncertain is { Status: WorkflowStatus.Waiting, CancelRequested: true, Effect.State: WorkflowEffectState.Unknown },
                "cancelled uncertain workflow retains audit and reconciliation state");
            fixture.Store.Delete("agent", acceptedProfile.Id);
            Check((await Call("models.configure", new ConfigureModelComponentRequest("model", projectorPath))).Success,
                "cancelled rebuilt and uncertain workflows no longer pin component settings");
            var assessedImage = ModelCapabilityPolicy.Assess(Current()).Single(x => x.Id == ModelCapabilityIds.ImageInput);
            Check(assessedImage.PayloadStatus == ModelCapabilityStates.Available && !assessedImage.Usable &&
                assessedImage.Evidence.Contains("native image path is implemented", StringComparison.Ordinal),
                "readable component is distinguished from unavailable retained runtime verification");

            var backups = new ConfigurationBackups(fixture.Store, () => false);
            var exported = backups.Export(true); var document = exported.Document;
            Check(document.SchemaVersion == ConfigurationBackupContract.SchemaVersion && document.Models.Single().VisionProjectorPath == projectorPath,
                "current typed backup includes the explicit component reference");
            await using var destination = new Fixture(Path.Combine(root, "restore"));
            var targetBackups = new ConfigurationBackups(destination.Store, () => false);
            var relocated = Path.Combine(root, "relocated-models");
            var preview = targetBackups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document,
                new ConfigurationRestorePathOverrides(ModelDirectory: relocated))), true);
            Check(preview.Attention.Any(x => x.Code == "model-component-missing") &&
                preview.Attention.Any(x => x.Code == "model-component-unverified"), "missing component remains explicit and unverified during preview");
            targetBackups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(preview.PreviewId, preview.ExpectedHostRevision, true)), true);
            var restored = destination.Store.Get<ModelRecord>("model", "model")!;
            Check(restored.Path == Path.Combine(relocated, "base.gguf") && restored.VisionProjectorPath == Path.Combine(relocated, "selected-projector.gguf") &&
                restored.State == "unloaded" && !Directory.Exists(relocated), "restore remaps both paths without creating files or loading a model");
            var explicitPath = Path.Combine(root, "separate-component", "projector.gguf");
            var exact = targetBackups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document,
                new ConfigurationRestorePathOverrides(ModelProjectorFiles: new Dictionary<string, string> { ["model"] = explicitPath }))), true);
            targetBackups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(exact.PreviewId, exact.ExpectedHostRevision, true)), true);
            Check(destination.Store.Get<ModelRecord>("model", "model")!.VisionProjectorPath == explicitPath,
                "per-model component override is independent of model weights");
            Check(Reject(() => targetBackups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document,
                new ConfigurationRestorePathOverrides(ModelProjectorFiles: new Dictionary<string, string> { ["unknown"] = explicitPath }))), true)),
                "unknown component override cannot inject a binding");

            var legacyDocument = document with { SchemaVersion = 3, InterfacePreferences = null, Skills = null,
                Models = document.Models.Select(x => x with { VisionProjectorPath = null }).ToArray() };
            var same = backups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(legacyDocument)), true);
            backups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(same.PreviewId, same.ExpectedHostRevision, true)), true);
            Check(Current().VisionProjectorPath == projectorPath, "v3 restore preserves a same-model local component");
            var moved = backups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(legacyDocument,
                new ConfigurationRestorePathOverrides(ModelFiles: new Dictionary<string, string> { ["model"] = Path.Combine(root, "different.gguf") }))), true);
            Check(moved.Attention.Any(x => x.Code == "model-component-reconfigure"), "old backup reports changed model/component association");
            backups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(moved.PreviewId, moved.ExpectedHostRevision, true)), true);
            Check(Current().VisionProjectorPath is null, "v3 cannot forward an old projector to different weights");
            fixture.Store.Put("model", "model", loaded);
            Check(Reject(() => backups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document with { SchemaVersion = 3 })), true)),
                "old backup version rejects a new non-null component declaration");
            var savedBeforeBadRestore = Saved();
            var badDocument = document with { Models = [document.Models.Single() with { VisionProjectorPath = "../outside.gguf" }] };
            Check(Reject(() => backups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(badDocument)), true)) && Saved() == savedBeforeBadRestore,
                "malformed component restore cannot partially write settings");
            var stale = backups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document)), true);
            var changed = Current(); changed.VisionProjectorPath = otherProjector; fixture.Store.Put("model", changed.Id, changed);
            Check(Reject(() => backups.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(stale.PreviewId, stale.ExpectedHostRevision, true)), true)) &&
                Current().VisionProjectorPath == otherProjector, "concurrent component change invalidates a preview");
            fixture.Store.Put("model", "model", loaded);

            var packagePath = Path.Combine(root, "vision.cerebral-model.zip");
            var sourceWithSecret = Current(); sourceWithSecret.SourceUrl = "https://example.invalid/model?token=private-component-token";
            fixture.Store.Put("model", "model", sourceWithSecret);
            Check((await Call("models.package.export", new { id = "model", path = packagePath })).Success, "component package exports through actual command");
            ModelPackage package;
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
                var json = await reader.ReadToEndAsync();
                package = JsonSerializer.Deserialize<ModelPackage>(json, CerebralJson.Options)!;
                Check(package.Version == 2 && package.Model.VisionProjectorPath == "projector.gguf" &&
                    archive.Entries.Select(x => x.FullName).ToHashSet().SetEquals(["manifest.json", "model.gguf", "projector.gguf"]),
                    "v2 package uses exactly the fixed component entries");
                Check(!json.Contains(projectorPath, StringComparison.Ordinal) && !json.Contains("private-component-token", StringComparison.Ordinal),
                    "portable metadata omits absolute component paths and credential-like URL queries");
                using var component = archive.GetEntry("projector.gguf")!.Open(); using var bytes = new MemoryStream(); await component.CopyToAsync(bytes);
                Check(bytes.ToArray().SequenceEqual(projector), "export retains exact selected component bytes");
            }
            var importedResult = await Call("models.package.import", new { path = packagePath });
            Check(importedResult.Success, "v2 package imports through shared protected file operation");
            var imported = CerebralJson.Read<ModelRecord>(importedResult.Data);
            Check(imported.Id != "model" && imported.VisionProjectorPath == Path.Combine(repository, imported.Id + ".projector.gguf") &&
                File.ReadAllBytes(imported.Path).SequenceEqual(weights) && File.ReadAllBytes(imported.VisionProjectorPath!).SequenceEqual(projector),
                "package creates fresh confined local paths for both exact originals");
            Check(!imported.CapabilityStatus.Any(x => x.Id == "image-input" && x.Usable) && imported.State == "unloaded",
                "package metadata does not certify runtime image capability or load a model");
            Check(!(await Call("models.distribute", new { id = "model", workerId = "unconfigured" })).Success,
                "single-file distribution cannot silently omit a projector");
            Check(!(await Call("models.import", new { path = modelPath, name = "No opaque component", visionProjectorPath = projectorPath })).Success,
                "single-file import rejects a component it cannot carry");

            string Package(string name, ModelPackage manifest, byte[] modelBytes, byte[]? componentBytes, string entryName = "projector.gguf")
            {
                var path = Path.Combine(root, name + ".zip");
                using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
                using (var output = new StreamWriter(zip.CreateEntry("manifest.json").Open())) output.Write(JsonSerializer.Serialize(manifest, CerebralJson.Options));
                using (var output = zip.CreateEntry("model.gguf").Open()) output.Write(modelBytes);
                if (componentBytes is not null) using (var output = zip.CreateEntry(entryName).Open()) output.Write(componentBytes);
                return path;
            }
            ModelPackage CopyPackage() => CerebralJson.Read<ModelPackage>(CerebralJson.Element(package));
            var v1WithComponent = CopyPackage(); v1WithComponent.Version = 1;
            var absolute = CopyPackage(); absolute.Model.VisionProjectorPath = projectorPath;
            var escaped = CopyPackage(); escaped.Model.VisionProjectorPath = "../projector.gguf";
            var count = fixture.Store.List<ModelRecord>("model").Count;
            var filesBefore = Directory.GetFiles(repository).Order(StringComparer.Ordinal).ToArray();
            foreach (var invalidPackage in new[]
            {
                Package("v1-unrepresentable", v1WithComponent, weights, null),
                Package("absolute-component", absolute, weights, projector),
                Package("parent-component", escaped, weights, projector, "../projector.gguf"),
                Package("missing-component", CopyPackage(), weights, null),
                Package("invalid-component", CopyPackage(), weights, Encoding.UTF8.GetBytes("BAD!")),
                Package("component-then-bad-weights", CopyPackage(), Encoding.UTF8.GetBytes("BAD!"), projector)
            })
                Check(!(await Call("models.package.import", new { path = invalidPackage })).Success,
                    "invalid package cannot escape its fixed component contract");
            Check(fixture.Store.List<ModelRecord>("model").Count == count &&
                Directory.GetFiles(repository).Order(StringComparer.Ordinal).SequenceEqual(filesBefore),
                "failed package writes remove only fresh import files and preserve existing registrations");
            Check(File.ReadAllBytes(projectorPath).SequenceEqual(projector) && File.ReadAllBytes(modelPath).SequenceEqual(weights),
                "all configuration and rejection paths preserve original input bytes");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected component fixture root.");
            Directory.Delete(root, true);
        }
        Console.WriteLine($"Model components: {checks} temporary SQLite/file/ZIP command checks passed; no inference, downloads or installed changes.");
    }

    private static byte[] Gguf(string marker)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write("GGUF"u8); writer.Write((uint)3); writer.Write((ulong)0); writer.Write((ulong)0);
        writer.Write(Encoding.UTF8.GetBytes(marker)); writer.Flush(); return stream.ToArray();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public ModelRuntime Runtime { get; }
        public CommandDispatcher Commands { get; }
        public WorkerWorkflows Workflows { get; }
        private readonly ToolDispatcher tools;
        private readonly JobScheduler scheduler;
        public Fixture(string path)
        {
            Store = new(path); Configuration = new(Store);
            var settings = Configuration.Get();
            settings.ModelDirectory = Path.Combine(path, "models");
            settings.ProjectsDirectory = Path.Combine(path, "projects");
            settings.SharedDirectory = Path.Combine(path, "shared");
            Configuration.Save(settings);
            var events = new EventHub();
            Runtime = new(Store, Configuration.Get, events); tools = new(Configuration.Get);
            scheduler = new(Store, Configuration, new NoExecutor(), Runtime, events, NullLogger<JobScheduler>.Instance);
            Workflows = new(Store, Configuration, events, [], []);
            Commands = new(Store, Configuration, Runtime, scheduler, tools, events, workflows: Workflows);
        }
        public async ValueTask DisposeAsync() { scheduler.Dispose(); await tools.DisposeAsync(); await Runtime.DisposeAsync(); }
    }
    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Component tests never run agents.");
    }
}
