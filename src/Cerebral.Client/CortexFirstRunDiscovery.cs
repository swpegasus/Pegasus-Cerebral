using Cerebral.Core;
using System.Text.Json;

namespace Cerebral.Client;

public sealed record CortexFirstRunCandidate(string State, string Message, string? ExecutablePath)
{
    /// <summary>Persist only an observation. MCP registrations remain explicit enrolled bindings.</summary>
    public bool SaveInitialTo(HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.InitialCortexDiscovery is not null) return false;
        settings.InitialCortexDiscovery = new(State, State == "compatible" ? ExecutablePath : null);
        return true;
    }
}

/// <summary>Read-only installed-product discovery; never opens the Cortex database or enrolls a client.</summary>
public static class CortexFirstRunDiscovery
{
    public static CortexFirstRunCandidate InspectRegistered()
    {
        try { return Inspect(InstalledCortexDiscovery.ReadRegistered()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
            System.Security.SecurityException or ArgumentException)
        {
            return new("incompatible", "Cortex registration could not be validated. Repair Cortex, then finish native connection setup in Configuration.", null);
        }
    }

    // The explicit location is a deterministic test seam; it never replaces machine registration.
    public static CortexFirstRunCandidate Inspect(InstalledCortexLocation? location)
    {
        if (location is null)
            return new("unavailable", "No same-machine Cortex installation is registered. Install Cortex separately or continue without it.", null);
        try
        {
            var compatible = CortexPrerequisite.Inspect(location);
            return new("compatible", "Compatible Cortex detected. Cerebral setup can offer an owner-approved native connection for the current Worker.",
                compatible.ExecutablePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
            System.Security.SecurityException or ArgumentException)
        {
            return new("incompatible", "The registered Cortex installation is incomplete or incompatible. Repair or update Cortex, then retry native setup.", null);
        }
    }
}
