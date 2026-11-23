using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Tools;

public sealed partial class ToolDispatcher
{
    private static RegisteredToolEntry RegisteredEntry(ToolDescriptor descriptor, string source, string sourceIdentity)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Name) || descriptor.Name.Length > 256 ||
            descriptor.Name.Any(c => c is < '!' or > '~' or '*') ||
            string.IsNullOrWhiteSpace(descriptor.Capability) || descriptor.Capability.Length > 256 ||
            descriptor.Capability.Any(c => c is < '!' or > '~' or '*') ||
            descriptor.Description is null || descriptor.InputSchema.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A registered tool descriptor has an invalid exact identity or schema.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { source, sourceIdentity, descriptor }, CerebralJson.Options))).ToLowerInvariant();
        return new(descriptor, source, fingerprint);
    }
    /// <summary>Bind broker cleanup to the existing dispatcher shutdown; no process or service is added.</summary>
    public void OwnManagedInternetLifetime(IDisposable broker)
    {
        _lifetime.Token.Register(static value => ((IDisposable)value!).Dispose(), broker);
    }
    /// <summary>Offline catalog of only this managed web contract. Never discovers MCP or contacts providers.</summary>
    public RegisteredToolCatalog ListManagedInternet()
    {
        var entries = new List<RegisteredToolEntry>
        { RegisteredEntry(Builtins.Single(x => x.Name == "http.fetch"), "builtin", "cerebral.tools") };
        foreach (var extension in _extensions.OfType<IManagedInternetToolProvider>())
        {
            if (extension.ManagedInternetVersion != InternetAuthorityContract.Version || extension is not IToolExtension tools) continue;
            var assembly = extension.GetType().Assembly;
            var identity = assembly.FullName + ":" + assembly.ManifestModule.ModuleVersionId.ToString("N");
            foreach (var descriptor in tools.Tools.Where(x => InternetAuthorityContract.ManagedToolIds.Contains(x.Name, StringComparer.Ordinal)))
            {
                if (descriptor.Name == "http.fetch") throw new InvalidDataException("An extension conflicts with the managed builtin.");
                entries.Add(RegisteredEntry(descriptor, "extension", identity));
            }
        }
        if (entries.GroupBy(x => x.Descriptor.Name, StringComparer.Ordinal).Any(x => x.Count() != 1))
            throw new InvalidDataException("Managed web tools have conflicting owners.");
        var ordered = entries.OrderBy(x => x.Descriptor.Name, StringComparer.Ordinal).ToArray();
        var generation = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            ordered.Select(x => new { x.Descriptor.Name, x.Source, x.SourceFingerprint }), CerebralJson.Options))).ToLowerInvariant();
        return new(ordered, [], generation);
    }
}
