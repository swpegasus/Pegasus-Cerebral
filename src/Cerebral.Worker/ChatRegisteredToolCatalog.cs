using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker;

/// <summary>Bind extension descriptors to their current loaded service-package owner.</summary>
internal static class ChatRegisteredToolCatalog
{
    internal static RegisteredToolCatalog Bind(RegisteredToolCatalog catalog,
        ServicePluginRuntime? loadedOwner)
    {
        var entries = catalog.Tools.Select(entry =>
        {
            if (entry.Source != "extension") return entry;
            var owner = loadedOwner?.LoadedWorkflowToolOwner(entry.Descriptor.Name);
            var package = owner is null ? null : loadedOwner?.PackageBindings.SingleOrDefault(
                x => x.Id == owner && x.Loaded);
            if (owner is not null && package is null)
                throw new InvalidDataException("The registered extension has no loaded package binding.");
            var source = owner is null ? "extension:unowned" : "extension:" + owner;
            var fingerprint = Hash(new
            {
                entry.SourceFingerprint, owner,
                package?.Version, package?.Generation, package?.Dependencies
            });
            return entry with { Source = source, SourceFingerprint = fingerprint };
        }).OrderBy(entry => entry.Descriptor.Name, StringComparer.Ordinal).ToArray();
        var generation = Hash(entries.Select(entry => new
        { entry.Descriptor.Name, entry.Source, entry.SourceFingerprint }));
        return new(entries, catalog.UnavailableServers, generation);
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options))).ToLowerInvariant();
}
