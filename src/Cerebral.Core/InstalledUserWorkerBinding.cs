using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Core;

/// <summary>Protected, nonsecret enrollment for an ordinary signed-in-user desktop Worker.
/// It is independent of service registration and grants no process or data access by itself.</summary>
public sealed record InstalledUserWorkerBinding(int Version, string BindingId, string OwnerSid,
    string WorkerExecutable, string DataDirectory)
{
    public const int CurrentVersion = 1;
    public const int MaximumDocumentBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 8
    };

    public static InstalledUserWorkerBinding Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is < 2 or > MaximumDocumentBytes)
            throw new InvalidDataException("Installed user Worker enrollment is empty or oversized.");
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new() { MaxDepth = 8 });
            InstalledWorkerBinding.RejectDuplicateProperties(document.RootElement);
            var value = document.RootElement.Deserialize<InstalledUserWorkerBinding>(Json)
                ?? throw new InvalidDataException("Installed user Worker enrollment is empty.");
            value.Validate();
            return value;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Installed user Worker enrollment is not a valid versioned document.",
                new FormatException(error.GetType().Name));
        }
    }

    public void Validate()
    {
        if (Version != CurrentVersion)
            throw new InvalidDataException("Unsupported installed user Worker enrollment version.");
        InstalledWorkerBinding.ValidateCommon(BindingId, OwnerSid, WorkerExecutable, DataDirectory);
    }

    public string ToJson()
    {
        Validate();
        var value = JsonSerializer.Serialize(this, Json);
        if (Encoding.UTF8.GetByteCount(value) > MaximumDocumentBytes)
            throw new InvalidDataException("Installed user Worker enrollment exceeds 16 KiB.");
        return value;
    }
}
