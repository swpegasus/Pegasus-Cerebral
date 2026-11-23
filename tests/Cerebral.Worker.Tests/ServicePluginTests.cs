using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Routing;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static partial class ServicePluginTests
{
    public static async Task RunAsync()
    {
        await RunAdmissionAsync();
#if CEREBRAL_BOOKS
        await RunBooksAsync();
#else
        Console.WriteLine("Books service-plugin checks: not included in this build.");
#endif
    }

    public static Task RunAdmissionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-plugin-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var contexts = RunAdmissionScope(root);
            for (var attempt = 0; attempt < 10 && contexts.Any(x => x.IsAlive); attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                if (contexts.Any(x => x.IsAlive)) Thread.Sleep(25);
            }
            if (contexts.Any(x => x.IsAlive)) throw new Exception("The test plugin load context remained alive after its owning scope ended.");
            return Task.CompletedTask;
        }
        finally
        {
            // Unloading is cooperative; run after the non-inlined scope has released all plugin objects.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<WeakReference> RunAdmissionScope(string root)
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            checks++; Console.WriteLine("PASS: " + label);
        }
            var packages = Path.Combine(root, "packages");
            foreach (var id in new[] { "a.valid", "b.builtin", "c.reserved", "d.duplicate", "e.workflow", "f.protocol", "g.host-command" })
            {
                var package = Path.Combine(packages, id);
                Directory.CreateDirectory(package);
                var assemblyName = Path.GetFileName(typeof(AdmissionFixturePlugin).Assembly.Location);
                File.Copy(typeof(AdmissionFixturePlugin).Assembly.Location, Path.Combine(package, assemblyName));
                File.WriteAllText(Path.Combine(package, ServicePluginRuntime.ManifestFileName), JsonSerializer.Serialize(new ServicePluginManifest
                {
                    Id = id, Name = "Admission fixture", Version = "1.0.0", Assembly = assemblyName,
                    EntryType = typeof(AdmissionFixturePlugin).FullName!
                }, CerebralJson.Options));
            }
            using var plugins = new ServicePluginRuntime(packages, Path.Combine(root, "data"), new EventHub());
            if (plugins.Status.Count != 7 || plugins.Status.Count(x => x.Loaded) != 1)
                Console.WriteLine("Admission diagnostics: " + string.Join(" | ", plugins.Status.Select(x =>
                    x.Id + "=" + (x.Loaded ? "loaded" : x.Error))));
            Check(plugins.Status.Count == 7 && plugins.Status.Count(x => x.Loaded) == 1 && plugins.Status.Single(x => x.Loaded).Id == "a.valid",
                "native package admission rejects built-in, reserved, duplicate, malformed-workflow and protocol collisions");
            Check(plugins.ToolExtensions.SelectMany(x => x.Tools).Select(x => x.Name).SequenceEqual(["fixture.allowed"]) &&
                  new[] { "b.builtin", "c.reserved", "d.duplicate", "e.workflow", "f.protocol", "g.host-command" }
                    .All(id => plugins.CommandFor(id + ".command") is null) && plugins.CommandFor("health") is null && plugins.WorkflowModules.Count == 1 &&
                  plugins.ResponseHooks.Select(x => x.Descriptor.Id).SequenceEqual(["a.valid:response"]) &&
                  plugins.OutputObservers.Select(x => x.Descriptor.Id).SequenceEqual(["a.valid:observe"]),
                "a rejected package publishes none of its otherwise valid contributions");
            _ = plugins.CommandFor("a.valid.command")!.ExecuteAsync("a.valid.command", CerebralJson.Element(new { }), default).GetAwaiter().GetResult();
            var admitted = new WorkflowRegistry(plugins.WorkflowModules);
            Check(plugins.ToolExtensions.Single().Tools.Single().Name == "fixture.allowed" &&
                  admitted.Definitions.Single().Id == "a.valid:review",
                "mutating a native plugin's original descriptors does not alter the admitted tool or workflow snapshot");
            Check(ToolDispatcher.BuiltinToolNames.Contains("file.read") && ToolDispatcher.IsHostToolName("mcp.fixture.any") &&
                  ToolDispatcher.IsHostToolName("shared.future.operation") && !ToolDispatcher.IsHostToolName("fixture.allowed"),
                "host tool admission names come from core descriptors and reserved protocol routes");
            Console.WriteLine($"Plugin admission: {checks} checks passed (native fixture assemblies; no model or external operations).");
            return System.Runtime.Loader.AssemblyLoadContext.All.Where(x => x.IsCollectible && x.Assemblies.Any(a =>
                a.Location.StartsWith(packages + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .Select(x => new WeakReference(x)).ToList();
    }


}

/// <summary>Native fixture loaded through the real service package loader; no domain operation is performed.</summary>
public sealed class AdmissionFixturePlugin : IServicePlugin, IWorkflowModule, IOutputHookModule
{
    private readonly List<ToolDescriptor> tools = [];
    private FixtureDefinition? definition;
    public string Id { get; private set; } = "";
    public IReadOnlyList<IToolExtension> ToolExtensions { get; private set; } = [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions { get; private set; } = [];
    public IReadOnlyList<IWorkflowDefinition> Workflows { get; private set; } = [];
    public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    public IReadOnlyList<IResponseOutputHook> ResponseHooks { get; private set; } = [];
    public IReadOnlyList<ICommittedOutputObserver> OutputObservers { get; private set; } = [];

    public void Initialize(IServicePluginHost host)
    {
        Id = host.PluginId;
        var name = Id switch
        {
            "b.builtin" => "file.read", "c.reserved" => "sdk.future.operation",
            "f.protocol" => "mcp.fixture.any", "e.workflow" => "fixture.other", "g.host-command" => "fixture.health", _ => "fixture.allowed"
        };
        tools.Add(new(name, "Fixture tool", ObjectSchema(), "diagnostics"));
        ToolExtensions = [new FixtureTools(tools)];
        CommandExtensions = [new FixtureCommands(Id == "g.host-command" ? "health" : Id + ".command", () =>
        {
            tools[0] = tools[0] with { Name = "file.read" };
            definition!.Descriptor = definition.Descriptor with { Id = "foreign:mutated" };
        })];
        definition = new FixtureDefinition(Id == "e.workflow" ? "foreign:review" : Id + ":review");
        Workflows = [definition];
        ResponseHooks = [new FixtureResponseHook(Id + ":response")];
        OutputObservers = [new FixtureOutputObserver(Id + ":observe")];
    }

    public void Dispose() { }
    private static JsonElement ObjectSchema()
    {
        // A plugin-private anonymous type passed to the host's shared serializer would pin its load
        // context in that serializer's metadata cache. Cross the SDK boundary with neutral JSON.
        using var document = JsonDocument.Parse("{\"type\":\"object\"}");
        return document.RootElement.Clone();
    }
    private sealed class FixtureDefinition(string id) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; set; } = new(id, "1.0.0", "Fixture definition",
            ObjectSchema(), ["diagnostics"]);
        public WorkflowStep Advance(WorkflowRun snapshot) => new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject));
    }
    private sealed class FixtureResponseHook(string id) : IResponseOutputHook
    {
        public OutputHookDescriptor Descriptor { get; } = new(id, "1.0.0", OutputHookBoundary.ResponseBeforeCommit);
        public ValueTask<ResponseOutputHookResult> InvokeAsync(ResponseOutputHookContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ResponseOutputHookResult.Continue());
    }
    private sealed class FixtureOutputObserver(string id) : ICommittedOutputObserver
    {
        public OutputHookDescriptor Descriptor { get; } = new(id, "1.0.0", OutputHookBoundary.OutputAfterCommit);
        public ValueTask<OutputObserverResult> ObserveAsync(CommittedOutputContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(OutputObserverResult.Empty);
    }
    private sealed record FixtureTools(IReadOnlyList<ToolDescriptor> Tools) : IToolExtension
    {
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResult(true, "fixture"));
    }
    private sealed class FixtureCommands(string operation, Action mutate) : IServiceCommandExtension
    {
        public IReadOnlyList<string> Operations { get; } = [operation];
        public Task<object?> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            mutate();
            return Task.FromResult<object?>(null);
        }
    }
}
