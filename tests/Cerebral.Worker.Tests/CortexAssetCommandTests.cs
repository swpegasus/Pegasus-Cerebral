using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

internal static class CortexAssetCommandTests
{
    internal static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CerebralCortexAssetCommandTests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException("FAIL: " + name);
            checks++;
        }
        try
        {
            var store = new StateStore(Path.Combine(root, "data"));
            var events = new EventHub();
            var configuration = new Configuration(store);
            await using var runtime = new ModelRuntime(store, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get, [], store.DirectoryPath);
            using var scheduler = new JobScheduler(store, configuration, new NoExecutor(), runtime, events, NullLogger<JobScheduler>.Instance);
            var commands = new CommandDispatcher(store, configuration, runtime, scheduler, tools, events);
            foreach (var id in new[] { "ordinary", "administrator" })
                store.Put("agent", id, new AgentProfile { Id = id, Capabilities = id == "administrator" ? ["administration"] : [] });

            var listing = await Call("cortex.assets.providers", new { });
            Check(listing.Success && listing.Data.GetProperty("providers").GetArrayLength() == 0 &&
                listing.Data.GetProperty("totalCount").GetInt32() == 0, "unconfigured owner sees an empty bounded provider list");
            Check(!((await Call("cortex.assets.providers", new { extra = true })).Success), "provider listing rejects extra arguments");
            foreach (var id in new[] { "ordinary", "administrator" })
            {
                foreach (var operation in new[] { "cortex.assets.providers", "cortex.assets.read", "cortex.assets.unknown" })
                {
                    var result = await Call(operation, new { }, id);
                    Check(!result.Success && result.Error?.Contains("owner interface", StringComparison.Ordinal) == true,
                        id + " cannot cross the owner asset boundary: " + operation);
                }
            }
            Check(!((await Call("cortex.assets.read", new { serverId = "missing", uri = "cortex://" + new string('a', 64) })).Success),
                "an owner cannot read through an unconfigured provider");
            Check(!((await Call("cortex.assets.read", new { serverId = "missing" })).Success), "read rejects missing selector");
            Check(!((await Call("cortex.assets.read", new { serverId = "missing", uri = "file:///private", extra = true })).Success),
                "read rejects expanded command arguments");
            Check(store.List<AgentJob>("job").Count == 0, "passive boundary tests do not submit inference or execution");
            Console.WriteLine("PASS: " + checks + " owner asset command checks (real dispatcher and SQLite; no provider or model)");
            Task<CommandResult> Call(string operation, object args, string? profile = null) =>
                commands.ExecuteAsync(new CommandRequest(operation, CerebralJson.Element(args), profile));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // The fixture owns only this unique temporary directory.
            if (Path.GetDirectoryName(root) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) &&
                Path.GetFileName(root).StartsWith("CerebralCortexAssetCommandTests-", StringComparison.Ordinal))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("Asset command tests never run an agent.");
    }
}
