namespace Cerebral.PluginLifetime;

// These helpers apply only at an owned callback's host notification/revalidation
// boundary. Other failures propagate normally; callers retire owned work on false.
internal static class PluginHostAccess
{
    internal static bool TryRead<T>(Func<T> read, out T value)
    {
        try { value = read(); return true; }
        catch (ObjectDisposedException) { value = default!; return false; }
    }
    internal static bool TryNotify(Action notification) => TryRead(() => { notification(); return true; }, out _);
}

// Cleanup is best effort across resources, with every ordinary failure retained
// and reported after the owning plugin has completed its teardown.
internal sealed class PluginCleanup
{
    private readonly List<Exception> failures = [];
    internal void Attempt(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) when (error is not OutOfMemoryException) { failures.Add(error); }
    }
    internal void Complete()
    {
        if (failures.Count != 0) throw new AggregateException("Plugin cleanup completed with failures.", failures);
    }
}
