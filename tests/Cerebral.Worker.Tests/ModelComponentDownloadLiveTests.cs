using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Explicit owner acceptance for a real publisher component download; retains only the selected component file.</summary>
public static class ModelComponentDownloadLiveTests
{
    public static async Task<string> RunAsync(string weightsPath, string componentUrl, string fileName,
        CancellationToken cancellationToken = default)
    {
        weightsPath = Path.GetFullPath(weightsPath);
        if (!File.Exists(weightsPath)) throw new FileNotFoundException("Model weights were not found.", weightsPath);
        var repository = Path.GetDirectoryName(weightsPath) ?? throw new InvalidDataException("Model repository is unavailable.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-component-download-live-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); var completed = false;
        try
        {
            var store = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(store); var settings = configuration.Get();
            settings.ModelDirectory = repository;
            settings.McpServers = [];
            configuration.Save(settings);
            const string id = "component-download-live";
            store.Put("model", id, new ModelRecord
            {
                Id = id, Name = "Component download acceptance", Path = weightsPath,
                SizeBytes = new FileInfo(weightsPath).Length, State = "unloaded", ContextTokens = 8192
            });
            var events = new EventHub();
            await using var runtime = new ModelRuntime(store, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get);
            using var scheduler = new JobScheduler(store, configuration, new NoExecutor(), runtime, events,
                NullLogger<JobScheduler>.Instance);
            var commands = new CommandDispatcher(store, configuration, runtime, scheduler, tools, events);
            var result = await commands.ExecuteAsync(new("models.component.download", CerebralJson.Element(new
            {
                id,
                url = componentUrl,
                fileName
            })), cancellationToken);
            if (!result.Success) throw new InvalidOperationException("Component download failed: " + result.Error);
            var model = CerebralJson.Read<ModelRecord>(result.Data);
            if (model.VisionProjectorPath is not { } path || !File.Exists(path))
                throw new InvalidDataException("Component download returned no retained projector.");
            Console.WriteLine("PROJECTOR: " + path);
            completed = true;
            return path;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cerebral-component-download-live-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid component-download cleanup root.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (!completed) Console.WriteLine("No component was retained by this failed acceptance.");
        }
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("Component download acceptance never runs an agent.");
    }
}
