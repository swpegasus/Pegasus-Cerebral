using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Read-only owner-coordinator diagnostics for the task's exact GB10 model; never changes or loads models.</summary>
internal static class SourceRemoteModelInspection
{
    public static async Task RunAsync(string dataDirectory)
    {
        var source = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pegasus", "Cerebral", "Source"));
        if (!OperatingSystem.IsWindows() || !Path.GetFullPath(dataDirectory).Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Inspection is restricted to the existing Source root.");
        var file = new FileInfo(Path.Combine(source, "Desktop", "connection.json"));
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length is <= 0 or > 16_384)
            throw new InvalidOperationException("Unexpected Source declaration.");
        using var declaration = JsonDocument.Parse(await File.ReadAllTextAsync(file.FullName));
        var root = declaration.RootElement;
        if (root.GetProperty("transport").GetString() != "pipe" ||
            !Path.GetFullPath(root.GetProperty("localDataDirectory").GetString()!).Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source is not its local owner pipe.");
        var endpoint = root.GetProperty("localEndpoint").Deserialize<LocalWorkerEndpoint>(CerebralJson.Options)
            ?? throw new InvalidOperationException("Source owner endpoint is absent.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var client = new CerebralClient(endpoint);
        var health = await client.GetAsync<HostInfo>("health", cancellationToken: deadline.Token);
        if (!health.LocalRun || !Path.GetFullPath(health.DataDirectory).Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The endpoint does not own Source.");
        // Owner-management operations are verified in current dispatcher source, not workflow-contribution SDK discovery.
        const string workerId = "worker-3f6ab53f3a5a";
        const string modelId = "source-qwen35-9b";
        var model = await client.GetAsync<ModelRecord>("workers.models.inspect", new { workerId, id = modelId }, cancellationToken: deadline.Token);
        var diagnostics = await client.GetAsync<WorkerModelDiagnostics>("workers.models.diagnostics", new { workerId }, cancellationToken: deadline.Token);
        if (model.Id != modelId || diagnostics.Models.Count > 256 || string.IsNullOrWhiteSpace(diagnostics.WorkerInstallationId))
            throw new InvalidOperationException("Remote identity or diagnostics are invalid.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            actualSource = true, readOnly = true, modelCalls = 0, route = "owner-pipe-to-paired-worker",
            workerId, modelId, model.State, model.Architecture, model.ContextTokens, model.ParallelRequests,
            projectorConfigured = !string.IsNullOrWhiteSpace(model.VisionProjectorPath),
            capabilities = model.CapabilityStatus.Select(c => new { c.Id, c.FamilyStatus, c.PayloadStatus, c.RuntimeStatus, c.Usable, c.EvidenceBasis }),
            runtime = diagnostics.Models.Where(m => m.Id == modelId),
            diagnostics.SampledAt
        }, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false }));
    }
}
