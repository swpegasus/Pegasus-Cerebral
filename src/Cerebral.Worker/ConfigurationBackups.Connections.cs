using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class ConfigurationBackups
{
    private static ConfigurationBackupConnections ExportConnections(HostSettings host, List<ConfigurationBackupAttention> attention)
    {
        ConnectionBounds(host.Workers.Count, host.Executables.Count, host.McpServers.Count);
        var workers = host.Workers.Select(PortableRegistrationPolicy.ExportWorker).ToArray();
        var executables = host.Executables.Select(PortableRegistrationPolicy.ExportExecutable).ToArray();
        var providers = host.McpServers.Select(PortableRegistrationPolicy.ExportMcp).ToArray();
        foreach (var item in workers.Where(x => x.Url is null))
            attention.Add(new("unsafe-endpoint-omitted", "workers", item.Id, "A worker endpoint was omitted; reconnect it locally before use."));
        foreach (var item in executables.Where(x => x.ArgumentsOmitted))
            attention.Add(new("opaque-arguments-omitted", "executables", item.Id, "Local executable arguments are omitted. They can be retained only for the same configured local executable binding."));
        foreach (var item in providers)
        {
            if (item.ArgumentsOmitted)
                attention.Add(new("opaque-arguments-omitted", "mcpServers", item.Id, "Local provider arguments are omitted. Reconfigure an unavailable or changed binding before use."));
            if (item.NativeBindingOmitted)
                attention.Add(new("native-binding-omitted", "mcpServers", item.Id, "Cortex's machine-local native pipe and protected client key path are omitted. An existing reviewed local binding may be retained; another machine requires Cortex enrollment."));
            if (host.McpServers.Single(x => x.Id == item.Id).Url is not null && item.Url is null)
                attention.Add(new("unsafe-endpoint-omitted", "mcpServers", item.Id, "A provider endpoint was omitted; reconnect it locally before use."));
        }
        return new(workers, executables, providers, host.SharedFilesWorkerId,
            host.CortexNamespaces.ToArray(), host.CortexWriteNamespaces.ToArray());
    }

    private static void ApplyConnections(ConfigurationBackupDocument document, HostSettings host, List<ConfigurationBackupAttention> attention)
    {
        if (document.SchemaVersion < 3)
        {
            if (document.Connections is not null) throw new ArgumentException("This earlier backup version cannot carry connection declarations.");
            attention.Add(new("not-supported", "connections", null, "This backup version has no connection declarations. Existing connections and executable bindings are preserved."));
            return;
        }
        var section = document.Connections ?? throw new ArgumentException("Version 3 backups require connection declarations.");
        if (section.Workers is null || section.Executables is null || section.McpServers is null)
            throw new ArgumentException("Connection declaration lists cannot be null.");
        ConnectionBounds(section.Workers.Count, section.Executables.Count, section.McpServers.Count);
        Unique(section.Workers.Select(x => x?.Id), "workers");
        Unique(section.Executables.Select(x => x?.Id), "executables");
        Unique(section.McpServers.Select(x => x?.Id), "mcpServers");
        var workers = section.Workers.Select(x => PortableRegistrationPolicy.RestoreWorker(x, host.Workers.SingleOrDefault(old => old.Id == x.Id))).ToArray();
        var executables = section.Executables.Select(x => PortableRegistrationPolicy.RestoreExecutable(x, host.Executables.SingleOrDefault(old => old.Id == x.Id))).ToArray();
        var providers = section.McpServers.Select(x => PortableRegistrationPolicy.RestoreMcp(x, host.McpServers.SingleOrDefault(old => old.Id == x.Id))).ToArray();
        host.Workers = host.Workers.Where(x => !workers.Any(item => item.Id == x.Id)).Concat(workers).ToList();
        host.Executables = host.Executables.Where(x => !executables.Any(item => item.Id == x.Id)).Concat(executables).ToList();
        host.McpServers = host.McpServers.Where(x => !providers.Any(item => item.Id == x.Id)).Concat(providers).ToList();
        ConnectionBounds(host.Workers.Count, host.Executables.Count, host.McpServers.Count);
        Id(section.SharedFilesWorkerId);
        if (section.SharedFilesWorkerId != "local" && !host.Workers.Any(x => x.Id == section.SharedFilesWorkerId))
            throw new ArgumentException("The shared-files worker reference is unavailable.");
        host.SharedFilesWorkerId = section.SharedFilesWorkerId;
        host.CortexNamespaces = Namespaces(section.CortexNamespaces);
        host.CortexWriteNamespaces = Namespaces(section.CortexWriteNamespaces);
        foreach (var item in workers.Where(x => x.RequiresConfiguration))
            attention.Add(new("local-configuration-required", "workers", item.Id, "Restored worker needs local connection review before network requests or SSH recovery."));
        foreach (var item in executables.Where(x => x.RequiresConfiguration))
            attention.Add(new("local-configuration-required", "executables", item.Id, "Restored executable is unavailable until its path and local arguments are reviewed."));
        foreach (var item in providers.Where(x => x.RequiresConfiguration))
            attention.Add(new("local-configuration-required", "mcpServers", item.Id, "Restored provider is disabled until its endpoint, executable and local arguments are reviewed."));
        attention.Add(new("connection-bindings", "connections", null, "Only exact ready local bindings retain local credentials and launch arguments. Restore never reconnects, launches a provider, or enables Full Access."));
    }

    private static void ConnectionBounds(int workers, int executables, int providers)
    {
        if (workers > ConfigurationBackupContract.MaximumEntries || executables > ConfigurationBackupContract.MaximumEntries ||
            providers > ConfigurationBackupContract.MaximumEntries)
            throw new ArgumentException("A connection section exceeds the 128-entry limit.");
    }

    private static List<string> Namespaces(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count > 128 || values.Distinct(StringComparer.Ordinal).Count() != values.Count ||
            values.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256 || x.Any(char.IsControl)))
            throw new ArgumentException("Cortex namespace declarations are invalid or exceed their bounds.");
        return values.ToList();
    }
}
