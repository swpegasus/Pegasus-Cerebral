using System.Text.Json;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class ContextProviderDiscoveryTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception("FAIL: " + message);
            count++; Console.WriteLine("PASS: " + message);
        }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-provider-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Binding("fixture.provider", "1.0.0", Dependency("fixture.base", "1.0.0"));
            var basis = Binding("fixture.base", "1.0.0");
            var store = new StateStore(Path.Combine(root, "data"));
            var workflows = new WorkerWorkflows(store,
                new Configuration(store), new EventHub(),
                [new ProviderModule()], [package, basis]);
            var discovery = CerebralJson.Element(workflows.Discover(null));
            Check(discovery.GetProperty("commands").EnumerateArray().Any(item =>
                    item.GetProperty("id").GetString() == ProviderHandler.Id),
                "provider remains discoverable through the existing concise workflow-command catalog");
            var description = CerebralJson.Element(workflows.Describe(ProviderHandler.Id, null));
            var provider = description.GetProperty("contextProviders").EnumerateArray().Single();
            Check(provider.GetProperty("id").GetString() == ProviderHandler.Id &&
                provider.GetProperty("supportedReferenceKinds").EnumerateArray().Single().GetString() == "fixture.record" &&
                provider.GetProperty("maxReferences").GetInt32() == 4,
                "exact command description exposes the frozen context-provider kinds and bounds");

            var bindings = new WorkflowPackageBindings([package, basis]);
            var captured = bindings.Capture(ProviderHandler.Id);
            Check(captured.Select(item => item.Id).SequenceEqual(["fixture.base", "fixture.provider"]),
                "provider capture reuses deterministic transitive workflow package bindings");
            var submission = Submission(captured);
            Check(bindings.Check(submission, ProviderHandler.Id) is null,
                "unchanged provider and dependency versions satisfy the accepted package graph");
            var changed = new WorkflowPackageBindings([
                Binding("fixture.provider", "1.0.0", Dependency("fixture.base", "2.0.0")),
                Binding("fixture.base", "2.0.0")]);
            Check(changed.Check(submission, ProviderHandler.Id)?.Contains("changed", StringComparison.Ordinal) == true,
                "provider execution is held when its accepted dependency version changes");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        Console.WriteLine($"Context provider Worker discovery: {count} deterministic assertions passed.");
    }

    private static ServicePluginDependencyBinding Dependency(string id, string version) =>
        new(id, version, null, null, false, version, true);
    private static ServicePluginPackageBinding Binding(string id, string version,
        params ServicePluginDependencyBinding[] dependencies) => new(id, version, true, dependencies);
    private static WorkflowSubmission Submission(IReadOnlyList<ServicePluginPackageBinding> bindings) =>
        new("provider-binding", "conversation", "profile", "fixture.provider:workflow", "1.0.0", "Resolve.", null,
            WorkflowContract.EmptyObject, CerebralJson.Element(new { packageBindings = bindings }));

    private sealed class ProviderModule : IWorkflowModule
    {
        public string Id => "fixture.provider";
        public IReadOnlyList<IWorkflowDefinition> Workflows => [];
        public IReadOnlyList<IWorkflowCommandHandler> Commands { get; } = [new ProviderHandler()];
    }

    private sealed class ProviderHandler : IContextProviderCommandHandler
    {
        public const string Id = "fixture.provider:resolve";
        public WorkflowCommandDescriptor Descriptor { get; } = new(Id, 1, "Resolve fixture context.",
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, [], WorkflowReplayPolicy.Idempotent);
        public ContextProviderDescriptor Provider { get; } = new(Id, 1, "Resolve fixture context.",
            ["fixture.record"], 4, 16 * 1024);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Discovery fixture is not executed.");
    }
}
