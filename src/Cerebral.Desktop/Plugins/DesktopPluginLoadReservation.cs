using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Cerebral.Core;

namespace Cerebral.Desktop.Plugins;

/// <summary>Host-owned admission credits, not a plugin SDK capability or a memory-size guarantee.</summary>
public sealed class DesktopPluginLoadReservation : IDisposable
{
    internal readonly DesktopPluginLoadLedger Ledger;
    internal readonly string Root;
    internal readonly Dictionary<string, DesktopPluginLoadPlan> Pending;
    internal bool Closed;

    internal DesktopPluginLoadReservation(DesktopPluginLoadLedger ledger, string root, IEnumerable<DesktopPluginLoadPlan> plans)
    { Ledger = ledger; Root = root; Pending = plans.ToDictionary(plan => plan.Id, StringComparer.Ordinal); }

    internal void Bind(string root, IReadOnlyList<DesktopPluginLoadPlan> plans) => Ledger.Bind(this, root, plans);
    internal void Track(string id, AssemblyLoadContext context) => Ledger.Track(this, id, context);
    /// <summary>Exact admitted candidate bytes for publication's copy-time verification.</summary>
    public string CandidateFingerprint(string pluginId) => Ledger.Fingerprint(this, pluginId);
    public void Dispose() => Ledger.Release(this);
}

internal sealed record DesktopPluginLoadPlan(string Id, string Directory, string Version, string Fingerprint,
    bool PublishedDirectoryPending = false);

/// <summary>
/// Counts reservations plus native generations until both their context and assemblies are observed dead.
/// WPF can retain an ordinary Assembly after an Unload request; successful Dispose is not a capacity refund.
/// </summary>
internal sealed class DesktopPluginLoadLedger
{
    internal const int MaximumPerPlugin = 8;
    internal const int MaximumPerProcess = 256;
    private const int MaximumObservedAssemblies = 128;
    internal static readonly DesktopPluginLoadLedger Shared = new();
    private readonly object gate = new();
    private readonly List<Generation> generations = [];
    private readonly HashSet<DesktopPluginLoadReservation> reservations = [];
    private readonly ConditionalWeakTable<AssemblyLoadContext, Generation> contexts = new();

    private DesktopPluginLoadLedger() => AppDomain.CurrentDomain.AssemblyLoad += ObserveAssembly;

    internal DesktopPluginLoadReservation Reserve(string root, IReadOnlyList<DesktopPluginLoadPlan> plans)
    {
        root = Normalize(root);
        lock (gate)
        {
            Prune();
            if (plans.Select(plan => plan.Id).Distinct(StringComparer.Ordinal).Count() != plans.Count)
                throw new InvalidOperationException("A native admission plan contains duplicate plugin identities.");
            CheckCapacity(root, plans);
            var reservation = new DesktopPluginLoadReservation(this, root, plans);
            reservations.Add(reservation);
            return reservation;
        }
    }

    internal void Validate(string root, IReadOnlyList<DesktopPluginLoadPlan> plans)
    { lock (gate) { Prune(); CheckCapacity(Normalize(root), plans); } }

    private void CheckCapacity(string root, IReadOnlyList<DesktopPluginLoadPlan> plans)
    {
        var reserved = reservations.Sum(item => item.Pending.Count);
        if (generations.Count + reserved + plans.Count > MaximumPerProcess)
            throw Limit("this desktop process", generations.Count + reserved, MaximumPerProcess);
        foreach (var plan in plans)
        {
            NativeDesktopPluginPackage.ValidateId(plan.Id);
            var count = generations.Count(item => item.Root == root && item.Id == plan.Id) +
                reservations.Where(item => item.Root == root).Count(item => item.Pending.ContainsKey(plan.Id));
            if (count >= MaximumPerPlugin) throw Limit("plugin '" + plan.Id + "'", count, MaximumPerPlugin);
        }
    }

    private static InvalidOperationException Limit(string target, int count, int maximum) =>
        new($"Native loading is bounded for {target}: {count} unreclaimed or reserved loads, limit {maximum}. " +
            "Exit and restart this desktop host before another load. Closing its window, changing engines, or pausing a worker does not release WPF assembly roots. Existing instances were not replaced.");

    internal void Bind(DesktopPluginLoadReservation reservation, string root, IReadOnlyList<DesktopPluginLoadPlan> plans)
    {
        lock (gate)
        {
            RequireOpen(reservation);
            if (Normalize(root) != reservation.Root)
                throw new InvalidOperationException("The native admission reservation belongs to a different catalog root.");
            // Validate the whole plan before changing any credit. A publication ticket may bind
            // once to the resulting immutable directory only if the copied file-set fingerprint agrees.
            foreach (var plan in plans)
            {
                if (!reservation.Pending.TryGetValue(plan.Id, out var expected) ||
                    expected.Version != plan.Version || expected.Fingerprint != plan.Fingerprint ||
                    (!expected.PublishedDirectoryPending && Normalize(expected.Directory) != Normalize(plan.Directory)))
                    throw new InvalidOperationException("The exact native load plan changed before admission; no active instance was replaced.");
            }
            foreach (var plan in plans) reservation.Pending[plan.Id] = plan with { PublishedDirectoryPending = false };
        }
    }

    internal string Fingerprint(DesktopPluginLoadReservation reservation, string id)
    {
        lock (gate)
        {
            RequireOpen(reservation);
            return reservation.Pending.TryGetValue(id, out var plan) ? plan.Fingerprint :
                throw new InvalidOperationException("The candidate has no unconsumed native admission credit.");
        }
    }

    internal void Track(DesktopPluginLoadReservation reservation, string id, AssemblyLoadContext context)
    {
        lock (gate)
        {
            RequireOpen(reservation);
            if (!reservation.Pending.TryGetValue(id, out var plan) || plan.PublishedDirectoryPending)
                throw new InvalidOperationException("A bound one-use native admission credit is required before assembly loading.");
            if (!context.IsCollectible || context.Assemblies.Any())
                throw new InvalidOperationException("Native admission must observe a fresh collectible context before its first assembly.");
            var generation = new Generation(reservation.Root, id, context);
            contexts.Add(context, generation);
            generations.Add(generation);
            reservation.Pending.Remove(id); // Atomic reserved-to-observed conversion; never double count.
        }
    }

    private void ObserveAssembly(object? sender, AssemblyLoadEventArgs args)
    {
        var context = AssemblyLoadContext.GetLoadContext(args.LoadedAssembly);
        if (context is null) return;
        lock (gate)
        {
            if (!contexts.TryGetValue(context, out var generation)) return;
            if (generation.Assemblies.Count >= MaximumObservedAssemblies)
                generation.ObservationIncomplete = true; // Conservatively charged until process exit.
            else generation.Assemblies.Add(new(args.LoadedAssembly));
        }
    }

    internal void Release(DesktopPluginLoadReservation reservation)
    {
        lock (gate)
        {
            if (reservation.Closed) return;
            reservation.Closed = true;
            reservation.Pending.Clear(); // Only never-entered credits are returned.
            reservations.Remove(reservation);
        }
    }

    private void RequireOpen(DesktopPluginLoadReservation reservation)
    {
        if (reservation.Closed || !ReferenceEquals(reservation.Ledger, this) || !reservations.Contains(reservation))
            throw new InvalidOperationException("The native admission reservation has ended.");
    }
    private void Prune() => generations.RemoveAll(item => !item.ObservationIncomplete &&
        !item.Context.TryGetTarget(out _) && item.Assemblies.All(assembly => !assembly.TryGetTarget(out _)));
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();

    internal (int Generations, int Reserved) Counts(string? root = null, string? id = null)
    {
        lock (gate)
        {
            Prune();
            var key = root is null ? null : Normalize(root);
            return (generations.Count(item => (key is null || item.Root == key) && (id is null || item.Id == id)),
                reservations.Where(item => key is null || item.Root == key).Sum(item =>
                    id is null ? item.Pending.Count : item.Pending.ContainsKey(id) ? 1 : 0));
        }
    }

    private sealed class Generation(string root, string id, AssemblyLoadContext context)
    {
        internal string Root { get; } = root;
        internal string Id { get; } = id;
        internal WeakReference<AssemblyLoadContext> Context { get; } = new(context);
        internal List<WeakReference<Assembly>> Assemblies { get; } = [];
        internal bool ObservationIncomplete;
    }
}
