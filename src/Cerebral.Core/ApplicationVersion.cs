using System.Reflection;

namespace Cerebral.Core;

public static class ApplicationVersion
{
    public static string Current
    {
        get
        {
            var assembly = typeof(ApplicationVersion).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "unknown";
        }
    }

    // Admission probes must exit before any GUI, connection, provider or service state is created.
    public static bool TryWrite(string[] args, string application)
    {
        if (!args.Contains("--version", StringComparer.Ordinal)) return false;
        Console.WriteLine($"Pegasus Cerebral {application} {Current}");
        return true;
    }
}
