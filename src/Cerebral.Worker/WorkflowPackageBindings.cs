using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Accepted work keeps its package graph, including unavailable optional features, across restart.</summary>
internal sealed class WorkflowPackageBindings
{
    private readonly IReadOnlyDictionary<string, ServicePluginPackageBinding> packages;

    public WorkflowPackageBindings(IReadOnlyList<ServicePluginPackageBinding> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        this.packages = packages.ToDictionary(x => x.Id, x => x with
        {
            Dependencies = Array.AsReadOnly(x.Dependencies.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray())
        }, StringComparer.Ordinal);
    }

    public IReadOnlyList<ServicePluginPackageBinding> Capture(string definitionId) => Capture(definitionId, []);

    // Explicit optional roots become required members of this accepted graph. The caller decides
    // whether a feature is ready; a ready feature cannot degrade to omission during capture.
    public IReadOnlyList<ServicePluginPackageBinding> Capture(string definitionId,
        IReadOnlyList<string> additionalContributions)
    {
        var roots = AdditionalRoots(additionalContributions);
        var graph = new SortedDictionary<string, ServicePluginPackageBinding>(StringComparer.Ordinal);
        var strictGraph = new HashSet<string>(StringComparer.Ordinal);
        Include(Owner(definitionId), false);
        if (packages.ContainsKey("cerebral.host")) Include("cerebral.host", false);
        foreach (var root in roots) Include(root, true);
        return Array.AsReadOnly(graph.Values.ToArray());

        void Include(string id, bool strict)
        {
            if (strict ? !strictGraph.Add(id) : graph.ContainsKey(id)) return;
            if (!packages.TryGetValue(id, out var package) || !package.Loaded)
                throw new InvalidOperationException($"Workflow package '{id}' is unavailable.");
            if (strict && (package.Dependencies is null || package.Dependencies.Count > ServicePluginContract.MaxDependencies ||
                package.Dependencies.Any(dependency => !dependency.Optional && !dependency.Available)))
                throw new InvalidOperationException($"Workflow package '{id}' has an unavailable required dependency graph.");
            if (!graph.ContainsKey(id)) graph.Add(id, package);
            if (graph.Count > 1024) throw new InvalidOperationException("The accepted workflow package graph is too large.");
            foreach (var dependency in package.Dependencies.Where(x => x.Available))
            {
                if (strict && (!packages.TryGetValue(dependency.Id, out var target) ||
                    dependency.ResolvedVersion != target.Version))
                    throw new InvalidOperationException($"Workflow package '{id}' has a mismatched dependency graph.");
                Include(dependency.Id, strict);
            }
        }
    }

    internal static IReadOnlyList<string> AdditionalRoots(IReadOnlyList<string> additionalContributions)
    {
        ArgumentNullException.ThrowIfNull(additionalContributions);
        if (additionalContributions.Count > 16)
            throw new ArgumentException("At most 16 additional workflow package roots may be accepted.", nameof(additionalContributions));
        var roots = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var contribution in additionalContributions)
        {
            if (string.IsNullOrWhiteSpace(contribution) || contribution.Length > 200 ||
                contribution.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
                contribution.Count(character => character == ':') > 1 || contribution.EndsWith(':'))
                throw new ArgumentException("Additional workflow contributions require bounded package or contribution identities.", nameof(additionalContributions));
            var root = Owner(contribution);
            ServicePluginRuntime.ValidatePackageId(root);
            roots.Add(root);
        }
        return Array.AsReadOnly(roots.ToArray());
    }

    public string? Check(WorkflowSubmission submission, string contributionId)
    {
        if (!submission.SettingsSnapshot.TryGetProperty("packageBindings", out var element) || element.ValueKind != JsonValueKind.Array)
            return "This workflow has no saved package binding. Start a new run to accept the current plugin versions.";
        ServicePluginPackageBinding[]? saved;
        try { saved = element.Deserialize<ServicePluginPackageBinding[]>(CerebralJson.Options); }
        catch (JsonException) { return "The workflow's saved package binding is invalid."; }
        if (saved is null || saved.Length is 0 or > 1024 || saved.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) ||
            string.IsNullOrWhiteSpace(x.Version) || !x.Loaded || x.Dependencies is null ||
            x.Generation is { Length: > 160 } || x.Generation?.Contains('\0') == true ||
            x.Dependencies.Count > ServicePluginContract.MaxDependencies || x.Dependencies.Any(d => d is null || string.IsNullOrWhiteSpace(d.Id)) ||
            x.Dependencies.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != x.Dependencies.Count) ||
            saved.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != saved.Length)
            return "The workflow's saved package binding is invalid.";
        if (!saved.Any(x => x.Id == Owner(contributionId) && x.Loaded))
            return "The requested command's plugin was not a resolved dependency when this workflow was accepted.";
        foreach (var prior in saved)
        {
            if (!packages.TryGetValue(prior.Id, out var current) || !current.Loaded || prior.Version != current.Version ||
                NormalizeGeneration(prior.Generation) != NormalizeGeneration(current.Generation) ||
                !SameDependencies(prior.Dependencies, current.Dependencies))
                return $"Workflow package '{prior.Id}' or its dependencies changed or became unavailable. Restore the accepted versions before resuming, or start new work with the current versions.";
        }
        return null;
    }

    private static bool SameDependencies(IReadOnlyList<ServicePluginDependencyBinding> saved,
        IReadOnlyList<ServicePluginDependencyBinding> current) => saved.Count == current.Count &&
        saved.OrderBy(x => x.Id, StringComparer.Ordinal).Zip(current.OrderBy(x => x.Id, StringComparer.Ordinal))
            .All(pair => pair.First with { Reason = null } == pair.Second with { Reason = null });

    private static string Owner(string contributionId) => contributionId.Split(':', 2)[0];
    private static string NormalizeGeneration(string? generation) => string.IsNullOrWhiteSpace(generation) ? "legacy" : generation;
}
