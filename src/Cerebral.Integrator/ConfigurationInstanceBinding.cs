using System.Security.Cryptography;
using System.Text;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

/// <summary>Keep one writer per Configuration UI root while authenticating its complete requested owner.</summary>
internal sealed record ConfigurationInstanceBinding(string ApplicationId, DesktopApplicationIdentity Identity)
{
    internal static ConfigurationInstanceBinding Create(ExistingWorkerDesktopOptions options, string? managedOwnerBindingId)
    {
        ArgumentNullException.ThrowIfNull(options);
        // The key must never depend on the managed catalog. A different owner must encounter the
        // existing UI-root owner and fail its identity check, rather than obtain a second writer.
        var key = options.GetApplicationId("Integrator");
        if (managedOwnerBindingId is null) return new(key, options.ApplicationIdentity);
        if (string.IsNullOrWhiteSpace(managedOwnerBindingId) || managedOwnerBindingId.Length > 256 ||
            managedOwnerBindingId.Any(char.IsControl))
            throw new ArgumentException("Supply the validated managed plugin owner identity.", nameof(managedOwnerBindingId));
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            options.ApplicationIdentity.Endpoint + "\n" + managedOwnerBindingId)));
        return new(key, DesktopApplicationIdentity.Application("configuration-managed-plugins-v1:" + identity));
    }
}
