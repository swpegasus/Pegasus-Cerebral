using Cerebral.Core;

namespace Cerebral.Client;

/// <summary>Resolves only Cerebral's exact old Cortex enrollment path after Cortex registers a separate data root.</summary>
public static class CortexManagedCredentialLocator
{
    public sealed record Resolution(string EffectivePath, string? ManagedClientId);

    private sealed record LegacyBinding(string InstallDirectory, string DataDirectory, string ClientId);

    public static Resolution Resolve(CortexNativeConnection binding, string ownerSid)
    {
        var legacy = LegacyCandidate(binding, ownerSid);
        return legacy is null
            ? new(binding.CredentialFile, null)
            : Resolve(binding, ownerSid, InstalledCortexDiscovery.ReadRegistered());
    }

    /// <summary>Pure location projection for callers with an already admitted Cortex registration.</summary>
    public static Resolution Resolve(CortexNativeConnection binding, string ownerSid, InstalledCortexLocation? registered)
    {
        var legacy = LegacyCandidate(binding, ownerSid);
        if (legacy is null || registered is null ||
            !string.Equals(Path.TrimEndingDirectorySeparator(registered.InstallDirectory), legacy.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(registered.NativeHostPath, binding.HostExecutable, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.TrimEndingDirectorySeparator(registered.DataDirectory), legacy.DataDirectory, StringComparison.OrdinalIgnoreCase))
            return new(binding.CredentialFile, null);

        // Cortex owns the physical copy and protected key. This changes only the reference;
        // the native client still authenticates the exact peer and the decrypted client ID.
        return new(Path.Combine(registered.DataDirectory, "users", ownerSid, "config", "connections", legacy.ClientId + ".key"),
            legacy.ClientId);
    }

    public static void RequireManagedClientId(Resolution resolution, string credentialClientId)
    {
        if (resolution.ManagedClientId is { } expected &&
            !string.Equals(credentialClientId, expected, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The relocated Cortex credential does not match its enrolled client ID. No hello was sent.");
    }

    private static LegacyBinding? LegacyCandidate(CortexNativeConnection binding, string ownerSid)
    {
        binding.Validate();
        if (string.IsNullOrWhiteSpace(ownerSid) || ownerSid.Length > 184 ||
            !ownerSid.StartsWith("S-1-", StringComparison.Ordinal) ||
            ownerSid[2..].Any(character => !char.IsAsciiDigit(character) && character != '-')) return null;

        var hostDirectory = Path.GetDirectoryName(binding.HostExecutable);
        if (!string.Equals(Path.GetFileName(hostDirectory), "host", StringComparison.OrdinalIgnoreCase)) return null;
        var binDirectory = Path.GetDirectoryName(hostDirectory);
        if (!string.Equals(Path.GetFileName(binDirectory), "bin", StringComparison.OrdinalIgnoreCase)) return null;
        var installDirectory = Path.GetDirectoryName(binDirectory);
        if (installDirectory is null ||
            !string.Equals(binding.HostExecutable,
                Path.Combine(installDirectory, "bin", "host", "PegasusCortex.Host.exe"), StringComparison.OrdinalIgnoreCase))
            return null;

        var file = Path.GetFileName(binding.CredentialFile);
        if (file.Length is < 5 or > 68 || !file.EndsWith(".key", StringComparison.Ordinal)) return null;
        var clientId = file[..^4];
        if (clientId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) return null;
        var legacyData = Path.Combine(installDirectory, "state");
        var expected = Path.Combine(legacyData, "users", ownerSid, "config", "connections", file);
        return string.Equals(binding.CredentialFile, expected, StringComparison.OrdinalIgnoreCase)
            ? new(installDirectory, legacyData, clientId) : null;
    }
}
