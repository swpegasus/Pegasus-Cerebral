using System.Security.Cryptography;
using System.Text;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Real temporary model files/SQLite with a deterministic owner transport; no model process or LAN use.</summary>
public static class ModelDistributionTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); checks++; }
        await using (var fixture = new Fixture())
        {
            var sourceSettings = CerebralJson.Element(fixture.Configuration.Get()).GetRawText();
            var targetSettings = CerebralJson.Element(fixture.Remote.Model).GetRawText();
            var description = await fixture.Call("models.content.describe", new { id = "source" });
            var content = CerebralJson.Read<ModelContentDescription>(description.Data);
            Check(description.Success && content.WorkerInstallationId == fixture.Configuration.Get().InstallationId &&
                  content.ModelId == "source" && content.Version == 1 &&
                  content.Weights.Sha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Source.Path))) &&
                  content.Projector?.Sha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Source.VisionProjectorPath!))),
                "registered-model owner description measures exact installed weights and projector hashes");
            Check(!description.Data.GetRawText().Contains(fixture.Directory, StringComparison.Ordinal),
                "content description does not expose absolute paths, settings or component bytes");
            var result = await fixture.Distribute();
            var receipt = CerebralJson.Read<ModelDistributionReceipt>(result.Data);
            Check(result.Success && receipt.State == "ExistingVerified" && receipt.TargetModelId == "target" &&
                  receipt.SourceModelId == "source" && receipt.WorkerInstallationId == "target-installation" &&
                  receipt.BytesTransferred == 0 && !receipt.TargetSettingsChanged && receipt.Projector is not null,
                "matching pair reuses the exact remote registration with truthful zero-transfer receipt");
            Check(CerebralJson.Element(fixture.Configuration.Get()).GetRawText() == sourceSettings &&
                  CerebralJson.Element(fixture.Remote.Model).GetRawText() == targetSettings &&
                  fixture.Remote.Calls.All(operation => operation is "health" or "worker.management.status" or "models.list" or "models.content.describe"),
                "existing-content verification leaves tuning, default placement, loaded state and all target settings unchanged");
            Check(!result.Data.GetRawText().Contains("TransferredCommitted", StringComparison.Ordinal),
                "existing-content proof never claims a full transfer completed");
            var repeated = await fixture.Distribute();
            Check(repeated.Success && CerebralJson.Read<ModelDistributionReceipt>(repeated.Data).TargetModelId == receipt.TargetModelId &&
                  Directory.GetFiles(fixture.Directory, "*", SearchOption.AllDirectories).All(path => !path.EndsWith(".partial", StringComparison.Ordinal)),
                "retry verifies the same target without copies, partial staging or a new model registration");

            var arbitrary = await fixture.Call("models.content.describe", new { id = "source", path = fixture.Remote.Model.Path });
            var missing = await fixture.Call("models.content.describe", new { id = "missing" });
            fixture.Store.Put("agent", "admin", new AgentProfile { Id = "admin", Name = "Administrator", ModelId = "source", Capabilities = ["administration"] });
            var agentDescription = await fixture.Commands.ExecuteAsync(new("models.content.describe", CerebralJson.Element(new { id = "source" }), "admin"));
            var agentDistribution = await fixture.Commands.ExecuteAsync(new("models.distribute", CerebralJson.Element(new { id = "source", workerId = "target-worker" }), "admin"));
            Check(!arbitrary.Success && !missing.Success && !agentDescription.Success && !agentDistribution.Success,
                "closed description schema and owner-only distribution reject arbitrary paths, unknown registrations and administration agents");
        }

        await using (var fixture = new Fixture())
        {
            var foreign = CerebralJson.Read<ModelRecord>(CerebralJson.Element(fixture.Source));
            foreign.Id = "foreign"; fixture.Store.Put("model", "source", foreign);
            var description = await fixture.Call("models.content.describe", new { id = "source" });
            var distribution = await fixture.Distribute();
            Check(!description.Success && !distribution.Success && fixture.FactoryCalls == 0 &&
                  description.Error!.Contains("storage key", StringComparison.Ordinal) &&
                  distribution.Error!.Contains("storage key", StringComparison.Ordinal),
                "corrupt local registration identity fails before hashing or worker connection");
        }

        foreach (var defect in new[] { "installation", "model", "version", "weights-hash", "projector-hash", "negative-size", "bad-hash", "no-projector", "no-weights" })
        {
            await using var fixture = new Fixture();
            fixture.Remote.Defect = defect;
            var result = await fixture.Distribute();
            Check(!result.Success && fixture.Remote.Calls.All(operation => operation is "health" or "worker.management.status" or "models.list" or "models.content.describe"),
                "unverified " + defect + " evidence fails without any target mutation or upload");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Remote.Unsupported = true;
            var result = await fixture.Distribute();
            Check(!result.Success && result.Error!.Contains("Update the standalone Worker", StringComparison.Ordinal) &&
                  result.Error.Contains("No upload", StringComparison.Ordinal),
                "old worker fails with an actionable upgrade diagnostic before blind upload");
        }
        foreach (var defect in new[] { "controller", "product", "protocol", "coordinator", "identity", "unassigned" })
        {
            await using var fixture = new Fixture(); fixture.Remote.OwnerDefect = defect;
            var result = await fixture.Distribute();
            Check(!result.Success && !fixture.Remote.Calls.Contains("models.list") && !fixture.Remote.Calls.Contains("models.content.describe"),
                "owner session rejects " + defect + " mismatch before catalog/content requests");
        }
        await using (var fixture = new Fixture())
        {
            var settings = fixture.Configuration.Get(); settings.Workers[0].AccessKey = ""; fixture.Configuration.Save(settings);
            Check(!(await fixture.Distribute()).Success && fixture.FactoryCalls == 0,
                "a peer token cannot substitute for worker model owner authorization");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Source.VisionProjectorPath = null; fixture.Store.Put("model", "source", fixture.Source);
            Check(!(await fixture.Distribute()).Success, "a remote extra projector is not silently treated as a matching text-only package");
            fixture.Remote.Model.VisionProjectorPath = null;
            var result = await fixture.Distribute();
            Check(result.Success && CerebralJson.Read<ModelDistributionReceipt>(result.Data).Projector is null,
                "weights-only existing verification requires both selected package projector slots to be absent");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Remote.NoCandidates = true;
            var result = await fixture.Distribute();
            Check(!result.Success && result.Error!.Contains("binary v1/v2 model package transfer is required", StringComparison.Ordinal) &&
                  !fixture.Remote.Calls.Contains("models.content.describe"),
                "missing content reports the separate transfer requirement without inventing success");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Remote.ManyCandidates = true;
            Check(!(await fixture.Distribute()).Success && !fixture.Remote.Calls.Contains("models.content.describe"),
                "candidate hashing is bounded at eight models rather than sweeping an arbitrary catalog");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Remote.BeforeDescribe = () => File.AppendAllText(fixture.Source.Path, "source changed");
            Check(!(await fixture.Distribute()).Success,
                "source content changed while awaiting target verification cannot produce an existing-content receipt");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Remote.BeforeDescribe = () =>
            {
                var settings = fixture.Configuration.Get(); settings.Workers.Clear(); fixture.Configuration.Save(settings);
            };
            Check(!(await fixture.Distribute()).Success,
                "removed owner registration aborts confirmation after remote hash verification");
        }
        await using (var fixture = new Fixture())
        {
            using var cancellation = new CancellationTokenSource();
            fixture.Remote.HoldDescription = true;
            var running = fixture.Commands.ExecuteAsync(new("models.distribute",
                CerebralJson.Element(new { id = "source", workerId = "target-worker" })), cancellation.Token);
            await fixture.Remote.DescriptionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var result = await running;
            Check(!result.Success && result.Error!.Contains("cancelled", StringComparison.OrdinalIgnoreCase) &&
                  fixture.Remote.Calls.Count(operation => operation == "models.content.describe") == 1,
                "cancellation during remote content verification cannot return a success receipt or retry upload");
        }
        await using (var fixture = new Fixture())
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Check(!(await fixture.Commands.ExecuteAsync(new("models.distribute",
                CerebralJson.Element(new { id = "source", workerId = "target-worker" })), cancellation.Token)).Success &&
                  fixture.FactoryCalls == 0,
                "pre-cancelled request opens no worker connection");
        }
        Console.WriteLine($"Model distribution: {checks} temporary file/SQLite and deterministic owner-transport checks passed; no LAN, inference, uploads or installed changes.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "cerebral-model-distribution-" + Guid.NewGuid().ToString("N"));
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public CommandDispatcher Commands { get; }
        public ModelRecord Source { get; }
        public FakeOwner Remote { get; }
        public int FactoryCalls;
        private readonly ModelRuntime runtime;
        private readonly ToolDispatcher tools;
        private readonly JobScheduler scheduler;
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Store = new(Directory); Configuration = new(Store);
            var settings = Configuration.Get();
            settings.ModelDirectory = Path.Combine(Directory, "models");
            settings.ProjectsDirectory = Path.Combine(Directory, "projects");
            settings.SharedDirectory = Path.Combine(Directory, "shared");
            settings.ToolsDirectory = Path.Combine(Directory, "tools");
            settings.DefaultModelId = "source";
            settings.Workers = [new() { Id = "target-worker", Name = "Target fixture", Url = "http://127.0.0.1:2667",
                AccessKey = new string('a', 48), PeerToken = "unusable-fixture-peer" }];
            Configuration.Save(settings);
            var weights = Gguf("same exact weights"); var projector = Gguf("same exact projector");
            Source = new() { Id = "source", Name = "Source", Path = Path.Combine(Directory, "source.gguf"),
                VisionProjectorPath = Path.Combine(Directory, "source.projector.gguf"), SizeBytes = weights.Length,
                ContextTokens = 8192, ParallelRequests = 2, GpuLayers = -1 };
            File.WriteAllBytes(Source.Path, weights); File.WriteAllBytes(Source.VisionProjectorPath, projector);
            Store.Put("model", Source.Id, Source);
            var target = new ModelRecord { Id = "target", Name = "Existing target",
                Path = Path.Combine(Directory, "target.gguf"), VisionProjectorPath = Path.Combine(Directory, "target.projector.gguf"),
                SizeBytes = weights.Length, ContextTokens = 32768, ParallelRequests = 3, GpuLayers = 0, State = "loaded" };
            File.WriteAllBytes(target.Path, weights); File.WriteAllBytes(target.VisionProjectorPath, projector);
            Remote = new(settings.InstallationId, target);
            var events = new EventHub(); runtime = new(Store, Configuration.Get, events); tools = new(Configuration.Get);
            scheduler = new(Store, Configuration, new NoExecutor(), runtime, events, NullLogger<JobScheduler>.Instance);
            Commands = new(Store, Configuration, runtime, scheduler, tools, events,
                remoteWorkerClientFactory: _ => { FactoryCalls++; return Remote; });
        }
        public Task<CommandResult> Call(string operation, object args) => Commands.ExecuteAsync(new(operation, CerebralJson.Element(args)));
        public Task<CommandResult> Distribute() => Call("models.distribute", new { id = "source", workerId = "target-worker" });
        public async ValueTask DisposeAsync()
        {
            scheduler.Dispose(); await tools.DisposeAsync(); await runtime.DisposeAsync(); SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(Directory); var temporary = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected distribution fixture root.");
            System.IO.Directory.Delete(full, true);
        }
    }

    private sealed class FakeOwner(string controller, ModelRecord model) : IRemoteWorkerOwnerClient
    {
        public ModelRecord Model = model;
        public string? Defect, OwnerDefect;
        public bool Unsupported, NoCandidates, ManyCandidates, HoldDescription;
        public Action? BeforeDescribe;
        public TaskCompletionSource DescriptionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Calls { get; } = [];
        public async Task<T> GetAsync<T>(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add(operation);
            object result;
            if (operation == "health")
                result = new HostInfo(OwnerDefect == "product" ? "Other" : "Pegasus Cerebral", "fixture", OwnerDefect == "protocol" ? 99 : 1,
                    "Target", "Arm64", "Linux", OwnerDefect == "coordinator", "/var/lib/cerebral", InstallationId: "target-installation");
            else if (operation == "worker.management.status")
                result = new WorkerManagementStatus(OwnerDefect == "identity" ? "foreign" : "target-installation",
                    OwnerDefect == "unassigned" ? "awaiting-claim" : "assigned", OwnerDefect == "controller" ? "foreign" : controller, "Fixture", null);
            else if (operation == "models.list")
                result = NoCandidates ? new List<ModelRecord>() : ManyCandidates
                    ? Enumerable.Range(1, 9).Select(index => new ModelRecord { Id = "candidate-" + index, SizeBytes = Model.SizeBytes }).ToList()
                    : new List<ModelRecord> { Model };
            else if (operation == "models.content.describe")
            {
                if (Unsupported) throw new InvalidOperationException("Unknown command: models.content.describe");
                BeforeDescribe?.Invoke(); DescriptionEntered.TrySetResult();
                if (HoldDescription) await Task.Delay(Timeout.Infinite, cancellationToken);
                var content = (await ModelDistributionContent.DescribeAsync("target-installation", Model, cancellationToken)).Description;
                result = Defect switch
                {
                    "installation" => content with { WorkerInstallationId = "foreign" },
                    "model" => content with { ModelId = "foreign" },
                    "version" => content with { Version = 99 },
                    "weights-hash" => content with { Weights = content.Weights with { Sha256 = new string('0', 64) } },
                    "projector-hash" => content with { Projector = content.Projector! with { Sha256 = new string('0', 64) } },
                    "negative-size" => content with { Weights = content.Weights with { Bytes = -1 } },
                    "bad-hash" => content with { Weights = content.Weights with { Sha256 = "invalid" } },
                    "no-projector" => content with { Projector = null },
                    "no-weights" => content with { Weights = null! },
                    _ => content
                };
            }
            else throw new InvalidOperationException("Unexpected distribution owner command: " + operation);
            return CerebralJson.Read<T>(CerebralJson.Element(result));
        }
        public void Dispose() { }
    }

    private static byte[] Gguf(string marker)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write("GGUF"u8); writer.Write((uint)3); writer.Write((ulong)0); writer.Write((ulong)0);
        writer.Write(Encoding.UTF8.GetBytes(marker)); writer.Flush(); return stream.ToArray();
    }
    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Distribution fixture never runs an agent.");
    }
}
