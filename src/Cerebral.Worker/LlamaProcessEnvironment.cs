using System.Diagnostics;

namespace Cerebral.Worker;

internal static class LlamaProcessEnvironment
{
    private const string LinuxLibraryPath = "LD_LIBRARY_PATH";

    public static IReadOnlyDictionary<string, string>? ForExecutable(string executable)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var full = Path.GetFullPath(executable);
        var directory = Path.GetDirectoryName(full) ?? throw new ArgumentException("The runtime executable needs a parent directory.", nameof(executable));
        var inherited = Environment.GetEnvironmentVariable(LinuxLibraryPath);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LinuxLibraryPath] = ComposeLibraryPath(directory, inherited, Path.PathSeparator)
        };
    }

    public static void Apply(ProcessStartInfo start, string executable)
    {
        var environment = ForExecutable(executable);
        if (environment is null) return;
        foreach (var item in environment) start.Environment[item.Key] = item.Value;
    }

    internal static string ComposeLibraryPath(string runtimeDirectory, string? inherited, char separator)
    {
        var directory = Path.GetFullPath(runtimeDirectory);
        if (string.IsNullOrWhiteSpace(inherited)) return directory;
        var entries = inherited.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !value.Equals(directory, StringComparison.Ordinal));
        return string.Join(separator, new[] { directory }.Concat(entries));
    }
}
