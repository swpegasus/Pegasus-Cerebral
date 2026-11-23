using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

internal static class LiveImageModelAcceptanceTests
{
    public static async Task RunAsync(string discoveryPath, string modelPath, string projectorPath)
    {
        var exactModel = ExistingFile(modelPath, "model");
        var exactProjector = ExistingFile(projectorPath, "projector");
        using var discovery = JsonDocument.Parse(await File.ReadAllTextAsync(Path.GetFullPath(discoveryPath)));
        var endpoint = discovery.RootElement.GetProperty("endpoint");
        using var client = new CerebralClient(new LocalWorkerEndpoint(
            endpoint.GetProperty("pipeName").GetString()!, endpoint.GetProperty("instanceId").GetString()!));

        var models = await client.GetAsync<List<ModelRecord>>("models.list");
        var model = models.SingleOrDefault(item => PathEquals(item.Path, exactModel))
            ?? throw new InvalidOperationException("The source Worker does not have the selected image-capable model registered. Current models: " +
                string.Join("; ", models.Take(16).Select(item => item.Id + "=" + item.Path)));
        if (!PathEquals(model.VisionProjectorPath, exactProjector))
        {
            var configured = await client.CommandAsync("models.configure", new { id = model.Id, visionProjectorPath = exactProjector });
            if (!configured.Success)
                throw new InvalidOperationException("The source Worker could not bind the matching image projector: " + configured.Error);
        }

        var verification = await client.GetAsync<ModelImageVerification>("models.verify-image", new { id = model.Id });
        var inspected = await client.GetAsync<ModelRecord>("models.inspect", new { id = model.Id });
        var image = inspected.CapabilityStatus.SingleOrDefault(item => item.Id == ModelCapabilityIds.ImageInput);
        if (verification.ModelId != model.Id || verification.InputTokens <= 0 || image is not { Usable: true })
            throw new InvalidOperationException("The loaded source model did not retain native image-input readiness.");

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            suite = "image-source-live",
            evidence = "actual source Worker, llama.cpp model/projector load and native image-token generation",
            model = model.Id,
            verification.InstanceId,
            verification.AdapterId,
            verification.AdapterVersion,
            verification.InputTokens,
            verifiedAt = verification.VerifiedAt,
            projectorConfigured = PathEquals(inspected.VisionProjectorPath, exactProjector),
            imageUsable = image.Usable
        }, CerebralJson.Options));
    }

    private static string ExistingFile(string value, string label)
    {
        if (!Path.IsPathFullyQualified(value) || !File.Exists(value))
            throw new FileNotFoundException("Live image acceptance requires an existing absolute " + label + " file.", value);
        return Path.GetFullPath(value);
    }

    private static bool PathEquals(string? left, string right) => left is not null &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ?
            StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
