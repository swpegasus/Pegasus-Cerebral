using System.Globalization;
using System.Text;

namespace Cerebral.Core;

/// <summary>
/// A bounded, human-inspectable, one-time invitation emitted by Worker setup. It contains a bearer
/// bootstrap credential, so Configuration consumes it and replaces that credential during claim.
/// </summary>
public sealed record WorkerProvisioningPackage(string WorkerId, string Name, string Url,
    string WorkerInstallationId, string ProvisioningId, DateTimeOffset ExpiresAt, string BootstrapKey)
{
    public const string Extension = ".CerebralWorker";
    public const string Contract = "pegasus.cerebral.worker-provisioning/1";
    public const int MaximumBytes = 16 * 1024;
    private const string Section = "Pegasus Cerebral Worker";

    public WorkerProvisioningPackage Validate()
    {
        ValidateId(WorkerId, nameof(WorkerId));
        ValidateId(WorkerInstallationId, nameof(WorkerInstallationId));
        ValidateId(ProvisioningId, nameof(ProvisioningId));
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200 || Name.Any(char.IsControl))
            throw new InvalidDataException("Worker provisioning name is invalid.");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath.Trim('/').Length != 0 || endpoint.Port is < 1 or > 65535 ||
            endpoint.Host is "0.0.0.0" or "::" or "[::]")
            throw new InvalidDataException("Worker provisioning URL must be a concrete HTTP origin with an explicit port.");
        if (ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(-1) || ExpiresAt > DateTimeOffset.UtcNow.AddDays(8))
            throw new InvalidDataException("Worker provisioning invitation is expired or outside its bounded lifetime.");
        if (BootstrapKey.Length is < 24 or > 512 || BootstrapKey.Any(char.IsWhiteSpace) || BootstrapKey.Any(char.IsControl))
            throw new InvalidDataException("Worker provisioning credential is invalid.");
        return this;
    }

    public WorkerRegistration ToPendingRegistration(string replacementManagementKey) => new()
    {
        Id = Validate().WorkerId,
        Name = Name,
        Url = Url,
        AccessKey = ValidateReplacementKey(replacementManagementKey),
        RequiresConfiguration = true
    };

    public string Serialize()
    {
        Validate();
        static string Escape(string value) => value.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal)
            .Replace("=", "%3D", StringComparison.Ordinal);
        var text = $"[{Section}]\r\n" +
                   $"Contract={Contract}\r\n" +
                   $"WorkerId={Escape(WorkerId)}\r\n" +
                   $"Name={Escape(Name)}\r\n" +
                   $"Url={Escape(Url)}\r\n" +
                   $"WorkerInstallationId={Escape(WorkerInstallationId)}\r\n" +
                   $"ProvisioningId={Escape(ProvisioningId)}\r\n" +
                   $"ExpiresAt={ExpiresAt.UtcDateTime:O}\r\n" +
                   $"BootstrapKey={Escape(BootstrapKey)}\r\n" +
                   "DeleteAfterImport=true\r\n";
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("Worker provisioning invitation is too large.");
        return text;
    }

    public static WorkerProvisioningPackage Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new InvalidDataException("Worker provisioning invitation has an invalid size.");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') throw new InvalidDataException("Worker provisioning invitation must use UTF-8 without a byte-order mark.");
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2 || lines[0] != $"[{Section}]") throw new InvalidDataException("Worker provisioning invitation header is invalid.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var split = line.IndexOf('=');
            if (split < 1 || !values.TryAdd(line[..split], Decode(line[(split + 1)..])))
                throw new InvalidDataException("Worker provisioning invitation contains an invalid or duplicate field.");
        }
        var expected = new HashSet<string>(["Contract", "WorkerId", "Name", "Url", "WorkerInstallationId",
            "ProvisioningId", "ExpiresAt", "BootstrapKey", "DeleteAfterImport"], StringComparer.Ordinal);
        if (values.Keys.Any(key => !expected.Contains(key)) || expected.Any(key => !values.ContainsKey(key)) ||
            values["Contract"] != Contract || values["DeleteAfterImport"] != "true" ||
            !DateTimeOffset.TryParseExact(values["ExpiresAt"], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires))
            throw new InvalidDataException("Worker provisioning invitation contract is invalid.");
        return new WorkerProvisioningPackage(values["WorkerId"], values["Name"], values["Url"], values["WorkerInstallationId"],
            values["ProvisioningId"], expires, values["BootstrapKey"]).Validate();
    }

    private static string Decode(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') { result.Append(value[index]); continue; }
            if (index + 2 >= value.Length || !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var decoded))
                throw new InvalidDataException("Worker provisioning invitation contains an invalid escape.");
            result.Append((char)decoded); index += 2;
        }
        return result.ToString();
    }

    private static void ValidateId(string value, string label)
    {
        if (value.Length is < 1 or > 100 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidDataException(label + " is not a bounded identifier.");
    }

    private static string ValidateReplacementKey(string value) =>
        value.Length is >= 24 and <= 512 && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl)
            ? value : throw new ArgumentException("Replacement Worker management key is invalid.");
}
