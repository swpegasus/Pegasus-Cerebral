using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class Configuration
{
    private Func<HostSettings?>? sourceRecoverySnapshot;

    // Recovery reads must not discover defaults or migrate profiles after release either.
    // Current owner-saved settings remain visible, so this never retains a stale grant or key.
    internal void AttachSourceRecoverySnapshot(Func<HostSettings?> read)
    {
        if (sourceRecoverySnapshot is not null) throw new InvalidOperationException("source_recovery_configuration_already_bound");
        var installationId = read()?.InstallationId ??
            throw new InvalidOperationException("source_recovery_configuration_unavailable");
        sourceRecoverySnapshot = () =>
        {
            if (read() is { } fenced) return fenced;
            var saved = store.Get<HostSettings>("settings", "host");
            if (saved is null || saved.InstallationId != installationId)
                throw new InvalidOperationException("source_recovery_configuration_identity_changed");
            return saved;
        };
    }
}
