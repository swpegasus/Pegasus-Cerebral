using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Core;

/// <summary>Nonsecret enrollment metadata. A document is not authority until its protected registry
/// origin and the observed Windows identities have been verified.</summary>
public sealed record InstalledWorkerBinding(int Version, string BindingId, string OwnerSid,
    string ServiceName, string ServiceAccountSid, string WorkerExecutable, string DataDirectory)
{
    public const int CurrentVersion = 1;
    public const int MaximumDocumentBytes = 16 * 1024;
    public const string ExpectedServiceName = "PegasusCerebral";
    public const string LocalServiceSid = "S-1-5-19";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 8
    };

    public static InstalledWorkerBinding Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is < 2 or > MaximumDocumentBytes)
            throw new InvalidDataException("Installed Worker binding must be a document of at most 16 KiB.");
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new() { MaxDepth = 8 });
            RejectDuplicateProperties(document.RootElement);
            var binding = document.RootElement.Deserialize<InstalledWorkerBinding>(Json)
                ?? throw new InvalidDataException("Installed Worker binding is empty.");
            binding.Validate();
            return binding;
        }
        catch (JsonException error)
        {
            // Parser messages can contain imported values. Keep them off the diagnostic boundary.
            throw new InvalidDataException("Installed Worker binding is not a valid versioned document.",
                new FormatException(error.GetType().Name));
        }
    }

    public void Validate()
    {
        if (Version != CurrentVersion) throw new InvalidDataException("Unsupported installed Worker binding version.");
        ValidateCommon(BindingId, OwnerSid, WorkerExecutable, DataDirectory);
        if (ServiceName != ExpectedServiceName || ServiceAccountSid != LocalServiceSid)
            throw new InvalidDataException("Version 1 binds the Pegasus Cerebral LocalService worker only.");
    }

    internal static void ValidateCommon(string bindingId, string ownerSid, string workerExecutable, string dataDirectory)
    {
        if (!Guid.TryParseExact(bindingId, "N", out var id) || id == Guid.Empty || bindingId != id.ToString("N"))
            throw new InvalidDataException("Installed Worker binding requires a canonical nonempty installation ID.");
        if (!IsOwnerAccountSid(ownerSid))
            throw new InvalidDataException("Installed Worker owner must have an explicit Windows account SID.");
        var executable = CanonicalLocalPath(workerExecutable, directory: false);
        var data = CanonicalLocalPath(dataDirectory, directory: true);
        if (!executable[(executable.LastIndexOf('\\') + 1)..].Equals("Cerebral.Worker.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installed Worker binding requires the Cerebral Worker executable.");
        var program = executable[..executable.LastIndexOf('\\')];
        var applicationParent = Path.GetDirectoryName(program);
        var installRoot = Path.GetFileName(program).Equals("Worker", StringComparison.OrdinalIgnoreCase) &&
            applicationParent is not null && Path.GetFileName(applicationParent).Equals("Applications", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(applicationParent)! : program;
        if (Inside(data, installRoot) || Inside(installRoot, data))
            throw new InvalidDataException("Installed Worker data must be separate from its installed code directory.");
    }

    public LocalWorkerEndpoint PipeEndpoint()
    {
        Validate();
        return new("Pegasus.Cerebral.Service." + BindingId, BindingId);
    }

    public string ToJson()
    {
        Validate();
        var value = JsonSerializer.Serialize(this, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(value) > MaximumDocumentBytes)
            throw new InvalidDataException("Installed Worker binding exceeds 16 KiB.");
        return value;
    }

    internal static string CanonicalLocalPath(string value, bool directory)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)
            || value.StartsWith("\\", StringComparison.Ordinal) || value.StartsWith("/", StringComparison.Ordinal)
            || value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\'
            || value.AsSpan(2).Contains(':') || value.Contains('/')
            || value.Split('\\').Skip(1).Any(part => part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw new InvalidDataException("Installed Worker paths must be bounded canonical local drive paths.");
        // Windows paths are validated lexically here so fixture validation does not need a filesystem.
        var normalized = directory ? value.TrimEnd('\\') : value;
        if (normalized.Length <= 3 || normalized.Contains("\\\\", StringComparison.Ordinal)
            || (!directory && normalized.EndsWith('\\')) || normalized.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            throw new InvalidDataException("Installed Worker paths must name a local product file or directory.");
        foreach (var part in normalized.Split('\\').Skip(1))
        {
            var name = part.Split('.')[0].ToUpperInvariant();
            if (name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4
                && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9')
                throw new InvalidDataException("Installed Worker paths cannot use Windows device names.");
        }
        return normalized;
    }

    public static bool PathEquals(string first, string second, bool directory) =>
        CanonicalLocalPath(first, directory).Equals(CanonicalLocalPath(second, directory), StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string path, string directory) => path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnerAccountSid(string? value)
    {
        if (value is null || value.Length > 184) return false;
        var parts = value.Split('-');
        if (parts.Length != 8 || parts[0] != "S" || parts[1] != "1") return false;
        if (!(parts[2] == "5" && parts[3] == "21") && !(parts[2] == "12" && parts[3] == "1")) return false;
        return parts.Skip(4).All(part => uint.TryParse(part, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && part == number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Installed Worker binding contains duplicate fields.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
