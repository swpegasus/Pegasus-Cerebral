namespace Cerebral.Core;

/// <summary>Observed process metadata. No account SID is inferred from an SCM configuration string.</summary>
public sealed record InstalledWorkerProcessObservation(int ProcessId, string AccountSid, string Executable, int SessionId,
    long CreationTimeFileTime);

public sealed record InstalledWorkerPipeAce(string Sid, uint AccessMask);

/// <summary>The actual pipe object's owner and fixed allow-only DACL, read through its open handle.</summary>
public sealed record InstalledWorkerPipeProtection(string OwnerSid, bool DaclPresent, bool DaclProtected,
    IReadOnlyList<InstalledWorkerPipeAce> Entries);

public sealed record InstalledWorkerPipePeer(InstalledWorkerProcessObservation Process,
    InstalledWorkerPipeProtection Protection);

/// <summary>The native pipe endpoint identity available to an unelevated owner even when
/// the LocalService process denies direct process-handle inspection.</summary>
public sealed record InstalledWorkerPipeEndpoint(int ProcessId, int SessionId,
    InstalledWorkerPipeProtection Protection);
