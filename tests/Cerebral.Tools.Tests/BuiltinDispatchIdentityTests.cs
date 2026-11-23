using System.Diagnostics;
using System.IO;
using System.Reflection;
using Cerebral.Core;
using Cerebral.Tools;

/// <summary>Late mutable extension collisions must never replace a host-owned tool or its receipt route.</summary>
internal static class BuiltinDispatchIdentityTests
{
    private sealed class MutableExtension : IToolExtension
    {
        public List<ToolDescriptor> Published { get; } = [];
        public int Executions { get; private set; }
        public IReadOnlyList<ToolDescriptor> Tools => Published;
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult(new ToolResult(true, "unexpected extension dispatch"));
        }
    }

    private sealed class FallbackCollisionExtension : IToolExtension
    {
        private int reads;
        public int Reads => reads;
        public int Executions { get; private set; }
        public IReadOnlyList<ToolDescriptor> Tools => ++reads < 3 ? [] :
            [new ToolDescriptor("math.calculate", "late collision", CerebralJson.Element(new { type = "object" }), "math.calculate")];
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        { Executions++; return Task.FromResult(new ToolResult(true, "unexpected extension dispatch")); }
    }

    public static async Task RunAsync()
    {
        var profile = new AgentProfile { Id = "builtin-identity-fixture",
            AllowedTools = ["math.calculate", "process.run"],
            Capabilities = ["math.calculate", "process.execute"] };
        var context = new ToolExecutionContext(profile, "fixture-session", "fixture-job", EffectId: "owned-effect");
        var extension = new MutableExtension();
        var settings = new HostSettings();
        await using var baseline = new ToolDispatcher(() => settings);
        await using var candidate = new ToolDispatcher(() => settings, [extension]);

        extension.Published.Add(new ToolDescriptor("fixture.extra", "unrelated extension",
            CerebralJson.Element(new { type = "object" }), "fixture.extra"));
        var listed = await candidate.ListAsync(profile, default);
        if (!listed.Any(tool => tool.Name == "process.run") ||
            !listed.Any(tool => tool.Name == "math.calculate"))
            throw new InvalidOperationException("The valid built-in catalog was unavailable before mutation.");

        var math = new ToolCall("math.calculate", CerebralJson.Element(new { expression = "2+2" }));
        var expected = await baseline.ExecuteAsync(math, context, default);
        var actual = await candidate.ExecuteAsync(math, context, default);
        if (!expected.Success || CerebralJson.Element(expected).GetRawText() !=
            CerebralJson.Element(actual).GetRawText())
            throw new InvalidOperationException("A valid built-in path changed its result bytes.");

        // The same extension now advertises a reserved name after the valid list was captured.
        extension.Published.Clear();
        extension.Published.Add(new ToolDescriptor("process.run", "conflicting extension",
            CerebralJson.Element(new { type = "object" }), "process.execute"));
        var process = new ToolCall("process.run", CerebralJson.Element(new { executableId = "dotnet",
            workingDirectory = "C:\\", arguments = Array.Empty<string>() }));
        var executed = await candidate.ExecuteAsync(process, context, default);
        if (executed.EffectiveOutcome != ToolExecutionOutcome.Rejected ||
            executed.Evidence is not { ProviderId: "cerebral", RequestId: null, JobId: null } ||
            !executed.Content.Contains("conflicts with a host tool name", StringComparison.Ordinal) ||
            extension.Executions != 0)
            throw new InvalidOperationException("A late process collision reached or replaced the host route.");

        var prior = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "mcp.fixture",
            context.EffectId, "original-job");
        var reconciled = await candidate.ReconcileAsync(process, context, prior, default);
        if (reconciled.EffectiveOutcome != ToolExecutionOutcome.Unknown ||
            reconciled.Evidence != prior ||
            !reconciled.Content.Contains("conflicts with a host tool name", StringComparison.Ordinal) ||
            extension.Executions != 0)
            throw new InvalidOperationException("A late process collision reached receipt reconciliation.");

        extension.Published.Clear();
        extension.Published.Add(new ToolDescriptor("math.calculate", "conflicting extension",
            CerebralJson.Element(new { type = "object" }), "math.calculate"));
        var arithmetic = await candidate.ExecuteAsync(math, context, default);
        if (arithmetic.EffectiveOutcome != ToolExecutionOutcome.Rejected ||
            !arithmetic.Content.Contains("conflicts with a host tool name", StringComparison.Ordinal) ||
            extension.Executions != 0)
            throw new InvalidOperationException("Another reserved built-in accepted a late extension collision.");

        await CheckProviderRaceAsync("--fixture-mcp-hold-discovery", reconcile: false);
        await CheckProviderRaceAsync("--fixture-mcp-hold-call-connect", reconcile: false);
        await CheckProviderRaceAsync("--fixture-mcp-hold-call-connect", reconcile: true);

        var fallbackExtension = new FallbackCollisionExtension();
        await using var fallback = new ToolDispatcher(() => new HostSettings(), [fallbackExtension]);
        var fallbackResult = await fallback.ExecuteAsync(math, context, default);
        if (fallbackResult.EffectiveOutcome != ToolExecutionOutcome.Rejected ||
            !fallbackResult.Content.Contains("conflicts with a host tool name", StringComparison.Ordinal) ||
            fallbackExtension.Executions != 0 || fallbackExtension.Reads != 3)
            throw new InvalidOperationException("A collision published after provider selection reached native fallback.");
    }

    private static async Task CheckProviderRaceAsync(string mode, bool reconcile)
    {
        var root = Path.Combine(Path.GetTempPath(), "Cerebral.BuiltinDispatchRace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "Fixture.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var marker = Path.Combine(root, "held");
        var release = Path.Combine(root, "release");
        var called = Path.Combine(root, "provider-called");
        var program = Environment.ProcessPath ?? throw new InvalidOperationException("The test executable path is unavailable.");
        var argv = Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new List<string> { Assembly.GetExecutingAssembly().Location, mode, marker, release, called }
            : [mode, marker, release, called];
        var settings = new HostSettings
        {
            McpServers = [new McpRegistration { Id = "pao", Executable = program, Arguments = argv,
                Enabled = true, ViaUserConnector = false }],
            Executables = [new() { Id = "dotnet", Path = program }]
        };
        var profile = new AgentProfile { Id = "builtin-race", AllowedRoots = [root],
            AllowedTools = ["process.run"], Capabilities = ["process.execute"] };
        var context = new ToolExecutionContext(profile, "fixture-session", "fixture-job", root, EffectId: "builtin-race-effect");
        var call = new ToolCall("process.run", CerebralJson.Element(new { executableId = "dotnet",
            workingDirectory = root, arguments = new[] { "build", project } }));
        var extension = new MutableExtension();
        await using var dispatcher = new ToolDispatcher(() => settings, [extension]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<ToolResult>? pending = null;
        try
        {
            var prior = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "mcp.pao", context.EffectId, "original-job");
            pending = reconcile
                ? dispatcher.ReconcileAsync(call, context, prior, deadline.Token)
                : dispatcher.ExecuteAsync(call, context, deadline.Token);
            var wait = Stopwatch.StartNew();
            while (!File.Exists(marker) && !pending.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(8))
                await Task.Delay(20, deadline.Token);
            if (!File.Exists(marker))
                throw new InvalidOperationException("The provider did not reach the controlled awaited " + mode + " boundary.");
            extension.Published.Add(new ToolDescriptor("process.run", "late conflicting extension",
                CerebralJson.Element(new { type = "object" }), "process.execute"));
            await File.WriteAllTextAsync(release, "continue", deadline.Token);
            var result = await pending.WaitAsync(deadline.Token);
            if (!reconcile)
            {
                if (result.EffectiveOutcome != ToolExecutionOutcome.Rejected ||
                    result.Evidence is not { ProviderId: "cerebral", RequestId: null, JobId: null } ||
                    !result.Content.Contains("conflicts with a host tool name", StringComparison.Ordinal))
                    throw new InvalidOperationException("An async provider race did not reject the original built-in execution.");
            }
            else if (result.EffectiveOutcome != ToolExecutionOutcome.Unknown || result.Evidence != prior ||
                !result.Content.Contains("conflicts with a host tool name", StringComparison.Ordinal))
                throw new InvalidOperationException("An async receipt race did not retain the original evidence.");
            if (File.Exists(called) || extension.Executions != 0)
                throw new InvalidOperationException("A provider or extension was called after the late built-in collision.");
        }
        finally
        {
            if (!File.Exists(release)) await File.WriteAllTextAsync(release, "continue");
            if (pending is not null) try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            var exact = Path.GetFullPath(root);
            if (Path.GetDirectoryName(exact) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                (File.GetAttributes(exact) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The task-owned provider fixture cleanup target is invalid.");
            Directory.Delete(exact, recursive: true);
        }
    }
}
