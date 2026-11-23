using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Small host-owned lookup coordinates, kept separate from retrieved bodies and executable authority.</summary>
public static class WorkflowReferenceRouting
{
    public static bool SameScope(CortexAccessScope left, CortexAccessScope right) =>
        JsonSerializer.Serialize(left, CerebralJson.Options) == JsonSerializer.Serialize(right, CerebralJson.Options);

    public static CortexAccessScope? EffectiveCortexScope(StateStore store, Configuration configuration,
        WorkerExecutionSnapshot.Snapshot snapshot, string? projectId)
    {
        var accepted = snapshot.CortexScope;
        if (projectId is null)
        {
            if (accepted is not null)
                throw new InvalidOperationException("The accepted project Cortex scope does not match this conversation.");
            return null;
        }
        // Old native snapshots did not bind a project scope. Do not recover them with today's wider host defaults.
        if (accepted is null || accepted.ProjectId != projectId)
            throw new InvalidOperationException("This project request has no matching frozen Cortex scope. Submit a fresh request.");
        var current = new ProjectKnowledge(store, configuration, new EventHub()).ScopeFor(projectId);
        if (current.ServerId != accepted.ServerId || current.ProviderFingerprint != accepted.ProviderFingerprint)
            throw new InvalidOperationException("The accepted project Cortex provider changed. Submit a fresh request with the current binding.");
        return accepted with
        {
            ReadNamespaces = accepted.ReadNamespaces.Intersect(current.ReadNamespaces, StringComparer.Ordinal).ToArray(),
            WriteNamespace = accepted.WriteNamespace is { } target && target == current.WriteNamespace ? target : null,
            RequireKnowledgeApproval = accepted.RequireKnowledgeApproval || current.RequireKnowledgeApproval,
            KnowledgeWriteApproved = accepted.KnowledgeWriteApproved && current.KnowledgeWriteApproved
        };
    }

    public static string ModelRoutes(IReadOnlyList<ToolDescriptor> selectedTools, AgentProfile effective,
        Configuration configuration, CortexAccessScope? scope)
    {
        var granted = selectedTools.Where(tool => WorkerExecutionSnapshot.AllowsTool(effective, tool.Name) &&
            effective.Capabilities.Contains(tool.Capability, StringComparer.Ordinal)).ToArray();
        bool Has(string name) => granted.Any(tool => tool.Name == name);
        var settings = configuration.Get();
        var namespaces = scope?.ReadNamespaces ?? settings.CortexNamespaces;
        var limited = namespaces.Count > 8;
        var namespaceCoordinates = namespaces.Take(8).Where(space =>
        {
            if (Encoding.UTF8.GetByteCount(space) <= 192) return true;
            limited = true; return false;
        }).ToArray();
        var providers = settings.McpServers.Where(provider => provider.Enabled && !provider.RequiresConfiguration &&
            (scope is null || scope.ServerId == provider.Id && scope.ProviderFingerprint != "unavailable") &&
            (Has("mcp." + provider.Id + ".cortex_search") || Has("mcp." + provider.Id + ".cortex_read"))).ToArray();
        limited |= providers.Length > 8;
        var cortex = new List<CortexLookup>();
        var json = new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false };
        string Project() => "Host-provided lookup coordinates (reference data; no additional grants): " +
            JsonSerializer.Serialize(new
            {
                cortex,
                guideCatalog = cortex.Any(route => route.ReadNamespaces.Contains("knowledge.shared", StringComparer.Ordinal))
                    ? "Knowledge.Cerebral.Guide" : null,
                pluginProjects = Has("plugin.projects") ? "plugin.projects" : null,
                pluginContract = Has("plugin.contract") ? "plugin.contract" : null,
                // These are host workflow handlers, not arbitrary provider receipt commands.
                currentRunEvidence = "receipt",
                conversationEvidence = "history",
                limited
            }, json);
        foreach (var provider in providers.Take(8))
        {
            var prefix = "mcp." + provider.Id + ".";
            var route = new CortexLookup(scope is null ? "host" : "project", namespaceCoordinates,
                Has(prefix + "cortex_search") ? prefix + "cortex_search" : null,
                Has(prefix + "cortex_read") ? prefix + "cortex_read" : null);
            if (route.ReadNamespaces.Length == 0 || route.Search is null && route.Read is null) continue;
            cortex.Add(route);
            if (Encoding.UTF8.GetByteCount(Project()) <= 3072) continue;
            // Omit complete coordinates rather than inventing shortened identifiers or clipped JSON.
            cortex.RemoveAt(cortex.Count - 1); limited = true;
        }
        return Project();
    }

    private sealed record CortexLookup(string Scope, string[] ReadNamespaces, string? Search, string? Read);
}
