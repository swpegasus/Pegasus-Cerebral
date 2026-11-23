using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private sealed record RegisteredCatalogItem(string Kind, string Id, string? Capability,
        string? Description, string? Source, string? DescriptorFingerprint,
        IReadOnlyList<string> SecondaryRequirements, bool? ModelVisible);
    private sealed record RegisteredCatalogPage(string WorkerId, string WorkerInstallationId,
        string Generation, int Offset, IReadOnlyList<RegisteredCatalogItem> Items, int? NextOffset,
        IReadOnlyList<string> UnavailableServers);

    private async Task<object> RegisteredToolsCatalogAsync(JsonElement args, CancellationToken token)
    {
        RegisteredCatalogFields(args, "offset", "limit", "expectedGeneration");
        var offset = RegisteredCatalogNumber(args, "offset", 0, 0, 4096);
        var limit = RegisteredCatalogNumber(args, "limit", 50, 1, 100);
        string? expected = null;
        if (args.TryGetProperty("expectedGeneration", out var generationValue))
        {
            if (generationValue.ValueKind != JsonValueKind.String ||
                generationValue.GetString() is not { Length: 64 } value ||
                !value.All(Uri.IsHexDigit))
                throw new ArgumentException("Expected registered generation must be an exact SHA-256 value.");
            expected = value;
        }
        if (offset > 0 && expected is null)
            throw new ArgumentException("Later catalog pages require the exact registered generation.");

        token.ThrowIfCancellationRequested();
        var installationId = configuration.Get().InstallationId;
        var catalog = ChatRegisteredToolCatalog.Bind(await tools.ListRegisteredAsync(token)
            .ConfigureAwait(false), servicePlugins);
        token.ThrowIfCancellationRequested();
        if (configuration.Get().InstallationId != installationId)
            throw new InvalidOperationException("The registered Worker identity changed during catalog discovery.");
        if (expected is not null && expected != catalog.Generation)
            throw new InvalidOperationException("Registered tools changed between catalog pages.");
        var rows = catalog.Tools.Select(entry => new RegisteredCatalogItem("tool",
            entry.Descriptor.Name, entry.Descriptor.Capability,
            entry.Descriptor.Description[..Math.Min(entry.Descriptor.Description.Length, 1000)],
            entry.Source, entry.SourceFingerprint,
            entry.Descriptor.Name == "file.copy" ? ["file.read", "filesystem.read"] :
            entry.Source.StartsWith("mcp:", StringComparison.Ordinal) &&
            entry.Descriptor.Name.Split('.').Last().StartsWith("cortex_", StringComparison.Ordinal)
                ? ["cortex.write for mutations"] : [], entry.Descriptor.ModelVisible))
            .Concat(catalog.Tools.Select(entry => entry.Descriptor.Capability)
                .Concat(catalog.Tools.Any(entry => entry.Source.StartsWith("mcp:", StringComparison.Ordinal) &&
                    entry.Descriptor.Name.Split('.').Last().StartsWith("cortex_", StringComparison.Ordinal))
                        ? ["cortex.write"] : [])
                .Distinct(StringComparer.Ordinal)
                .Select(capability => new RegisteredCatalogItem("capability", capability, null,
                    null, null, null, [], null)))
            .OrderBy(entry => entry.Kind, StringComparer.Ordinal).ThenBy(entry => entry.Id,
                StringComparer.Ordinal).ToArray();
        if (rows.Length > 4096)
            throw new InvalidDataException("The registered catalog contains more than 4096 rows.");
        if (offset > rows.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "The requested page starts beyond the registered catalog.");
        token.ThrowIfCancellationRequested();
        return new RegisteredCatalogPage("local", installationId, catalog.Generation, offset,
            rows.Skip(offset).Take(limit).ToArray(),
            offset + limit < rows.Length ? offset + limit : null, catalog.UnavailableServers);
    }

    private static int RegisteredCatalogNumber(JsonElement args, string name, int fallback, int minimum, int maximum)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) ||
            number < minimum || number > maximum)
            throw new ArgumentException("Invalid registered catalog page field: " + name);
        return number;
    }

    private static void RegisteredCatalogFields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Registered catalog arguments must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException("Registered catalog arguments contain a duplicate or unsupported field.");
    }
}
