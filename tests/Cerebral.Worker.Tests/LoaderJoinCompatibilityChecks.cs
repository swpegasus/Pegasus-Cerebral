using System.Text.Json;
using Cerebral.Conversation;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

internal static class LoaderJoinCompatibilityChecks
{
    private sealed record Module(string Id, IReadOnlyList<IWorkflowDefinition> Workflows,
        IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;

    private sealed class MutableDefinition(IWorkflowDefinition inner) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; set; } = inner.Descriptor;
        public WorkflowStep Advance(WorkflowRun snapshot) => inner.Advance(snapshot);
    }

    // A/B/A reproduces the previous helper's first/constructor/last ID reads.
    private sealed class ChangingIdModule(string firstId, string secondId,
        IReadOnlyList<IWorkflowDefinition> workflows,
        IReadOnlyList<IWorkflowCommandHandler> commands) : IWorkflowModule
    {
        public int IdReads { get; private set; }
        public int WorkflowReads { get; private set; }
        public int CommandReads { get; private set; }
        public string Id => ++IdReads % 2 == 1 ? firstId : secondId;
        public IReadOnlyList<IWorkflowDefinition> Workflows
        {
            get { WorkflowReads++; return workflows; }
        }
        public IReadOnlyList<IWorkflowCommandHandler> Commands
        {
            get { CommandReads++; return commands; }
        }
    }

    private sealed class OversizedCountingList<T>(T item) : IReadOnlyList<T>
    {
        private const int MaximumObservedReads = 1025; // Registry's existing 1024 + one sentinel.
        public int Reads { get; private set; }
        public int Count => int.MaxValue;
        public T this[int index] => index >= 0 ? Read() : throw new ArgumentOutOfRangeException(nameof(index));
        private T Read()
        {
            if (++Reads > MaximumObservedReads)
                throw new InvalidOperationException("Candidate capture read past the registry's existing bound.");
            return item;
        }
        public IEnumerator<T> GetEnumerator()
        {
            while (true) yield return Read();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void Refuses(Action action, string label)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected strict registry refusal: " + label);
    }

    /// <summary>Use one fresh, caller-owned absolute scratch root. The parent runner removes it
    /// only after this process exits, when the collectible package DLL is no longer locked.</summary>
    public static async Task<int> VerifyAsync(string root, string? reviewedManifestPath = null,
        string? expectedPackageVersion = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("Supply a fresh absolute loader fixture root.", nameof(root));
        root = Path.GetFullPath(root);
        var parent = Path.GetDirectoryName(root)
            ?? throw new ArgumentException("The loader fixture root must have an owning parent.", nameof(root));
        var parentInfo = new DirectoryInfo(parent);
        if (!parentInfo.Exists || (parentInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
            Directory.Exists(root) || File.Exists(root))
            throw new ArgumentException("The loader fixture root must be fresh under a real existing parent.", nameof(root));
        string[]? reviewedPackageFiles = null;
        if (reviewedManifestPath is not null || expectedPackageVersion is not null)
        {
            if (expectedPackageVersion is not ("1.0.7" or "1.0.8" or "1.0.9" or "1.0.10" or "1.0.11") || string.IsNullOrWhiteSpace(reviewedManifestPath) ||
                !Path.IsPathFullyQualified(reviewedManifestPath) ||
                Path.GetFileName(reviewedManifestPath) != ServicePluginRuntime.ManifestFileName)
                throw new ArgumentException("Supply the exact clean reviewed manifest and explicit expected version 1.0.7 through 1.0.11.");
            reviewedManifestPath = Path.GetFullPath(reviewedManifestPath);
            var inputDirectory = Path.GetDirectoryName(reviewedManifestPath)!;
            var inputInfo = new DirectoryInfo(inputDirectory);
            if (!inputInfo.Exists || (inputInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("The reviewed package must be a real existing directory.");
            string[] leaves = ["Cerebral.Conversation.dll", "Cerebral.Conversation.deps.json", ServicePluginRuntime.ManifestFileName];
            var entries = Directory.EnumerateFileSystemEntries(inputDirectory).Take(leaves.Length + 1).ToArray();
            if (entries.Length != leaves.Length || entries.Any(path =>
                !leaves.Contains(Path.GetFileName(path), StringComparer.Ordinal) || !File.Exists(path) ||
                (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
                throw new ArgumentException("The reviewed package must contain exactly its three regular DLL, deps and manifest files.");
            if (new FileInfo(reviewedManifestPath).Length is <= 0 or > ServicePluginContract.MaxManifestBytes)
                throw new ArgumentException("The reviewed manifest exceeds its existing byte bound.");
            using var reviewedManifest = JsonDocument.Parse(File.ReadAllText(reviewedManifestPath));
            LocalGenerationConversationExportChecks.CheckManifest(reviewedManifest.RootElement, expectedPackageVersion);
            reviewedPackageFiles = leaves.Select(leaf => Path.Combine(inputDirectory, leaf)).ToArray();
        }
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            checks++;
        }
        static string FileDigest(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        // Retain the root on success and failure. The caller owns bounded post-exit cleanup,
        // so cleanup cannot mask the primary loader or registry assertion.
        {
            var repository = Path.Combine(root, "packages");
            var package = Path.Combine(repository, ConversationServicePlugin.ModuleId);
            Directory.CreateDirectory(package);
            var assembly = typeof(LocalGenerationConversationServicePlugin).Assembly.Location;
            if (reviewedPackageFiles is null)
            {
                // Preserve the historical synthetic 1.0.5 loader lane exactly.
                File.Copy(assembly, Path.Combine(package, Path.GetFileName(assembly)));
                var manifest = new ServicePluginManifest
                {
                    Id = ConversationServicePlugin.ModuleId,
                    Name = "Isolated Conversation loader fixture",
                    Version = "1.0.5",
                    Assembly = Path.GetFileName(assembly),
                    EntryType = typeof(LocalGenerationConversationServicePlugin).FullName!
                };
                File.WriteAllText(Path.Combine(package, ServicePluginRuntime.ManifestFileName),
                    JsonSerializer.Serialize(manifest, CerebralJson.Options));
            }
            else
            {
                // Exercise the actual reviewed package, without synthesizing a replacement manifest.
                foreach (var source in reviewedPackageFiles)
                    File.Copy(source, Path.Combine(package, Path.GetFileName(source)));
                Check(reviewedPackageFiles.All(source =>
                {
                    var destination = Path.Combine(package, Path.GetFileName(source));
                    return new FileInfo(source).Length == new FileInfo(destination).Length &&
                        FileDigest(source) == FileDigest(destination);
                }), "The isolated package preserves all three exact reviewed file preimages.");
                Check(new FileInfo(reviewedPackageFiles[0]).Length == new FileInfo(assembly).Length &&
                    FileDigest(reviewedPackageFiles[0]) == FileDigest(assembly),
                    "The reviewed package contains the same compiled Conversation assembly as this probe.");
            }
            using var runtime = new ServicePluginRuntime(repository, Path.Combine(root, "plugin-data"), new EventHub());
            Check(runtime.Status.Count == 1 && runtime.Status[0].Loaded &&
                runtime.WorkflowModules.Count == 1, "Actual service package loader admits the declared consumer.");
            if (reviewedPackageFiles is not null)
                Check(runtime.Status[0].Version == expectedPackageVersion &&
                    runtime.PackageBindings.Single().Id == ConversationServicePlugin.ModuleId &&
                    runtime.PackageBindings.Single().Version == expectedPackageVersion,
                    "The actual loader publishes the explicit reviewed package version and identity.");
            var module = runtime.WorkflowModules.Single();
            var local = module.Workflows.Single(x => x.Descriptor.LocalGenerationConsumer is not null);
            var consumer = local.Descriptor.LocalGenerationConsumer!;
            Check(consumer == new WorkflowLocalGenerationConsumer(1,
                WorkflowLocalGenerationConsumer.NativeGenerateCommandId, 2,
                WorkflowLocalGenerationChoice.NativeTextRoute) &&
                module.Workflows.Any(x => x.Descriptor.LocalGenerationConsumer is null),
                "The loader freezes the exact opt-in while old definitions remain without a consumer.");
            var existing = new Module("fixture.existing",
                [new MutableDefinition(local) { Descriptor = local.Descriptor with
                    { Id = "fixture.existing:review", Version = "1.0.0", LocalGenerationConsumer = null } }],
                []);
            var changingToCandidate = new ChangingIdModule(existing.Id, module.Id, [local], []);
            Refuses(() => _ = WorkflowRegistry.FreezeCandidateShape([existing], changingToCandidate),
                "Captured existing-owner ID must refuse rather than return the existing owner's contribution.");
            Check(changingToCandidate.IdReads == 1 && changingToCandidate.WorkflowReads == 1 &&
                changingToCandidate.CommandReads == 1,
                "Candidate identity and contribution lists are each captured once before registry validation.");
            var changingToExisting = new ChangingIdModule(module.Id, existing.Id, [local], []);
            var capturedCandidate = WorkflowRegistry.FreezeCandidateShape([existing], changingToExisting);
            Check(changingToExisting.IdReads == 1 && capturedCandidate.Id == module.Id &&
                capturedCandidate.Workflows.Single().Descriptor.Id == local.Descriptor.Id &&
                capturedCandidate.Workflows.All(x => x.Descriptor.Id != "fixture.existing:review"),
                "A changing ID cannot replace candidate content with an existing owner's definition.");
            var ordinary = new Module(module.Id,
                module.Workflows.Where(x => x.Descriptor.LocalGenerationConsumer is null).ToArray(),
                module.Commands);
            Check(new WorkflowRegistry([ordinary]).Definitions.All(x => x.LocalGenerationConsumer is null),
                "The ordinary public registry still admits prior definitions without a host command.");
            Refuses(() => _ = WorkflowRegistry.FreezeCandidateShape([module], module),
                "Candidate shape validation retains duplicate module refusal.");
            checks++;

            Refuses(() => _ = new WorkflowRegistry(runtime.WorkflowModules),
                "Default registry cannot publish a consumer without its exact host command.");
            checks++;
            var state = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(state);
            var events = new EventHub();
            await using var models = new ModelRuntime(state, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get);
            var host = new WorkerWorkflowEffects(state, configuration, models, tools);
            var versionOneOnly = new Module(host.Id, host.Workflows,
                host.Commands.Where(x => x.Descriptor.Id != consumer.CommandId ||
                    x.Descriptor.Version != consumer.CommandVersion).ToArray());
            Refuses(() => _ = new WorkflowRegistry(runtime.WorkflowModules.Append(versionOneOnly)),
                "An actual host with only generate v1 cannot satisfy generate v2.");
            checks++;
            var admitted = new WorkflowRegistry(runtime.WorkflowModules.Append(host));
            Check(admitted.TryGetDefinition(local.Descriptor.Id, local.Descriptor.Version, out var exact) &&
                exact.Descriptor.LocalGenerationConsumer == consumer &&
                admitted.TryGetCommand(consumer.CommandId, consumer.CommandVersion, out var handler) &&
                ReferenceEquals(handler.Descriptor, admitted.GetCommand(consumer.CommandId, consumer.CommandVersion).Descriptor),
                "The final strict registry joins the actual host generate v2 command.");
            static void Overflow(Action action)
            {
                try { action(); }
                catch (ArgumentException error) when (error.Message.Contains("Too many workflow contributions.", StringComparison.Ordinal))
                { return; }
                throw new InvalidOperationException("Oversized capture did not retain the registry's contribution refusal.");
            }
            var oversizedWorkflows = new OversizedCountingList<IWorkflowDefinition>(local);
            Overflow(() => _ = WorkflowRegistry.FreezeCandidateShape([],
                new Module(module.Id, oversizedWorkflows, [])));
            Check(oversizedWorkflows.Reads is > 0 and <= 1025,
                "Workflow capture stops at the existing contribution ceiling plus one.");
            var oversizedCommands = new OversizedCountingList<IWorkflowCommandHandler>(host.Commands[0]);
            Overflow(() => _ = WorkflowRegistry.FreezeCandidateShape([],
                new Module(module.Id, [], oversizedCommands)));
            Check(oversizedCommands.Reads is > 0 and <= 1025,
                "Command capture stops at the existing contribution ceiling plus one.");
            Overflow(() => _ = WorkflowRegistry.FreezeCandidateShape([],
                new Module(module.Id, Enumerable.Repeat(local, 513).ToArray(),
                    Enumerable.Repeat(host.Commands[0], 513).ToArray())));
            checks++;

            var mutable = new MutableDefinition(local);
            var candidate = new Module(module.Id, [mutable], []);
            var frozen = WorkflowRegistry.FreezeCandidateShape([], candidate);
            Check(frozen.Workflows.Single().Descriptor.LocalGenerationConsumer == consumer &&
                !ReferenceEquals(frozen.Workflows.Single().Descriptor, mutable.Descriptor),
                "Candidate validation freezes the generic descriptor without a host command.");
            mutable.Descriptor = mutable.Descriptor with
            { LocalGenerationConsumer = new(1, "cerebral.host:other", 2, consumer.Route) };
            Check(frozen.Workflows.Single().Descriptor.LocalGenerationConsumer == consumer &&
                new WorkflowRegistry([frozen, host]).Definitions.Any(x =>
                    x.Id == local.Descriptor.Id && x.LocalGenerationConsumer == consumer),
                "A later source mutation cannot change the frozen candidate or final join.");
            Refuses(() => _ = WorkflowRegistry.FreezeCandidateShape([], candidate),
                "Changed consumer command is rejected on a new candidate validation.");
            checks++;

            foreach (var invalid in new[]
            {
                consumer with { SchemaVersion = 2 },
                consumer with { CommandId = "cerebral.host:other" },
                consumer with { CommandVersion = 1 },
                consumer with { Route = "local.other" }
            })
            {
                var malformed = new MutableDefinition(local)
                { Descriptor = local.Descriptor with { LocalGenerationConsumer = invalid } };
                Refuses(() => _ = WorkflowRegistry.FreezeCandidateShape([],
                    new Module(module.Id, [malformed], [])), "Unsupported consumer declaration.");
                checks++;
            }
            return checks;
        }
    }
}
