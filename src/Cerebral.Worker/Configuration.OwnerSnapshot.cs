using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerebral.Worker;

/// <summary>Private owner evidence. Credential bytes/raw config digest are never response fields or record ToString output.</summary>
internal sealed class OwnerSnapshotConfiguration
{
    private readonly string key;
    private readonly byte[] rawDigest;
    internal string InstallationId { get; }
    internal string? ControllerId { get; }
    internal int Revision { get; }
    internal bool AllowLan { get; }
    internal bool Standalone { get; }
    internal bool Coordinator { get; }

    private OwnerSnapshotConfiguration(string id, string? controller, int revision, bool allowLan,
        bool standalone, bool coordinator, string key, byte[] digest)
    {
        InstallationId = id; ControllerId = controller; Revision = revision; AllowLan = allowLan;
        Standalone = standalone; Coordinator = coordinator; this.key = key; rawDigest = digest;
    }

    internal static OwnerSnapshotConfiguration Parse(byte[]? body)
    {
        try { return ParseCurrent(body); }
        catch (OwnerSnapshotUnavailableException error) when (!error.Code.StartsWith("owner_", StringComparison.Ordinal))
        { throw new OwnerSnapshotUnavailableException("owner_configuration_invalid"); }
    }

    private static OwnerSnapshotConfiguration ParseCurrent(byte[]? body)
    {
        if (body is null) throw new OwnerSnapshotUnavailableException("owner_configuration_missing");
        using var document = OwnerSnapshotJson.Parse(body, StateStore.MaximumSnapshotConfigurationBytes);
        var fields = OwnerSnapshotJson.Fields(document.RootElement);
        var id = OwnerSnapshotJson.Text(fields, "installationId", 128, false, out _);
        RequireInstallationId(id);
        var controller = OwnerSnapshotJson.Text(fields, "managementControllerId", 128, true, out var controllerNull);
        if (!controllerNull) RequireInstallationId(controller);
        var key = OwnerSnapshotJson.Text(fields, "accessKey", 512, false, out _);
        if (key.Length < 24 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
            throw new OwnerSnapshotUnavailableException("owner_credential_state_invalid");
        var revision = OwnerSnapshotJson.Integer(fields, "revision");
        if (revision < 0) throw new OwnerSnapshotUnavailableException("owner_revision_invalid");
        var standalone = OwnerSnapshotJson.Boolean(fields, "standaloneWorker");
        var coordinator = OwnerSnapshotJson.Boolean(fields, "coordinator");
        var lan = OwnerSnapshotJson.Boolean(fields, "allowLan");
        if (standalone && (coordinator || controllerNull))
            throw new OwnerSnapshotUnavailableException("owner_assignment_unavailable");
        if (!standalone && !controllerNull)
            throw new OwnerSnapshotUnavailableException("owner_role_inconsistent");
        return new(id, controllerNull ? null : controller, revision, lan, standalone, coordinator, key, SHA256.HashData(body));
    }

    internal bool Matches(OwnerSnapshotConfiguration current) =>
        CryptographicOperations.FixedTimeEquals(rawDigest, current.rawDigest);

    internal bool Authenticates(string supplied)
    {
        if (supplied.Length is < 24 or > 512 || supplied.Any(char.IsControl) || supplied.Any(char.IsWhiteSpace)) return false;
        var strict = new UTF8Encoding(false, true);
        try { return CryptographicOperations.FixedTimeEquals(strict.GetBytes(key), strict.GetBytes(supplied)); }
        catch (EncoderFallbackException) { return false; }
    }

    internal static void RequireInstallationId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty || value != id.ToString("N"))
            throw new OwnerSnapshotUnavailableException("installation_identity_invalid");
    }
}

public sealed partial class Configuration
{
    // Internal diagnostics let request-path fixtures prove that inspection never enters normalization.
    private long getInvocationCount;
    internal long GetInvocationCount => Interlocked.Read(ref getInvocationCount);

    internal OwnerSnapshotConfiguration ReadOwnerSnapshotConfiguration(CancellationToken token) =>
        store.WithOwnerSnapshotState(false, state => OwnerSnapshotConfiguration.Parse(state.ConfigurationBody), token);
}
