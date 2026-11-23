using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Bounded private wire codec. Frames are observations; only the existing terminal DTO proves outcome.</summary>
internal static class RemotePreparedStreamingCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options) { WriteIndented = false };
    private static readonly JsonSerializerOptions StrictJson = new(Json)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 32
    };
    private static readonly string[] Common = ["schema", "kind", "sequence", "streamId",
        "workerInstallationId", "controllerInstallationId", "protocolVersion", "leaseId", "metadataSha256"];

    internal static RemotePreparedStreamOwnerIdentity ParseCapabilitiesRequest(ReadOnlySpan<byte> bytes) =>
        Parse(bytes, RemotePreparedStreamingLimits.RequestBytes, root =>
        {
            var fields = Fields(root, ["schemaVersion", "expectedWorkerInstallationId", "expectedControllerInstallationId"]);
            Require(Integer(fields, "schemaVersion") == 1, "Unsupported prepared stream discovery schema.");
            var value = new RemotePreparedStreamOwnerIdentity(Text(fields, "expectedWorkerInstallationId", 128),
                Text(fields, "expectedControllerInstallationId", 128));
            ValidateOwner(value.ExpectedWorkerInstallationId, value.ExpectedControllerInstallationId);
            return value;
        });

    internal static RemotePreparedStreamRequest ParseRequest(ReadOnlySpan<byte> bytes) =>
        Parse(bytes, RemotePreparedStreamingLimits.RequestBytes, root =>
        {
            var f = Fields(root, ["schemaVersion", "streamId", "expectedWorkerInstallationId",
                "expectedControllerInstallationId", "protocolVersion", "leaseId", "expectedMetadataSha256"]);
            var value = new RemotePreparedStreamRequest(Integer(f, "schemaVersion"), Text(f, "streamId", 32),
                Text(f, "expectedWorkerInstallationId", 128), Text(f, "expectedControllerInstallationId", 128),
                Integer(f, "protocolVersion"), Text(f, "leaseId", 32), Text(f, "expectedMetadataSha256", 64));
            ValidateRequest(value);
            return value;
        });

    internal static void ValidateRequest(RemotePreparedStreamRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Require(value.SchemaVersion == 1, "Unsupported prepared stream schema.");
        ValidateOwner(value.ExpectedWorkerInstallationId, value.ExpectedControllerInstallationId);
        Require(Uuid(value.StreamId) && Uuid(value.LeaseId), "Prepared stream requires exact bounded one-use identities.");
        RemotePreparedProtocol.Validate(new RemoteLeaseRequest(value.ProtocolVersion, value.LeaseId));
        Require(Hash(value.ExpectedMetadataSha256), "Prepared metadata fingerprint is invalid.");
    }

    internal static RemotePreparedStreamCapabilities ParseCapabilities(ReadOnlySpan<byte> bytes,
        RemotePreparedStreamOwnerIdentity owner) =>
        Parse(bytes, RemotePreparedStreamingLimits.RequestBytes, root =>
        {
            ValidateOwner(owner.ExpectedWorkerInstallationId, owner.ExpectedControllerInstallationId);
            var names = new[] { "schemaVersion", "workerInstallationId", "controllerInstallationId", "generatePath",
                "contentType", "preparedProtocolVersions", "maximumRequestBytes", "maximumStartFrameBytes",
                "maximumTextFrameBytes", "maximumTerminalFrameBytes", "maximumStreamBytes", "maximumFrames",
                "maximumTextFragmentCharacters" };
            var f = Fields(root, names);
            var protocols = Required(f, "preparedProtocolVersions");
            Require(protocols.ValueKind == JsonValueKind.Array && protocols.GetArrayLength() == 2 &&
                protocols[0].ValueKind == JsonValueKind.Number && protocols[0].TryGetInt32(out var one) && one == 1 &&
                protocols[1].ValueKind == JsonValueKind.Number && protocols[1].TryGetInt32(out var two) && two == 2,
                "Prepared stream protocol support is incompatible.");
            var value = new RemotePreparedStreamCapabilities(Integer(f, "schemaVersion"),
                Text(f, "workerInstallationId", 128), Text(f, "controllerInstallationId", 128),
                Text(f, "generatePath", 128), Text(f, "contentType", 128), [1, 2],
                Integer(f, "maximumRequestBytes"), Integer(f, "maximumStartFrameBytes"),
                Integer(f, "maximumTextFrameBytes"), Integer(f, "maximumTerminalFrameBytes"),
                Integer(f, "maximumStreamBytes"), Integer(f, "maximumFrames"), Integer(f, "maximumTextFragmentCharacters"));
            Require(owner.SchemaVersion == 1 && value.SchemaVersion == 1 &&
                value.WorkerInstallationId == owner.ExpectedWorkerInstallationId &&
                value.ControllerInstallationId == owner.ExpectedControllerInstallationId &&
                value.GeneratePath == "/api/inference/prepared/generate-stream" && value.ContentType == "application/x-ndjson" &&
                value.MaximumRequestBytes == RemotePreparedStreamingLimits.RequestBytes &&
                value.MaximumStartFrameBytes == RemotePreparedStreamingLimits.StartBytes &&
                value.MaximumTextFrameBytes == RemotePreparedStreamingLimits.TextBytes &&
                value.MaximumTerminalFrameBytes == RemotePreparedStreamingLimits.TerminalBytes &&
                value.MaximumStreamBytes == RemotePreparedStreamingLimits.StreamBytes &&
                value.MaximumFrames == RemotePreparedStreamingLimits.Frames &&
                value.MaximumTextFragmentCharacters == RemotePreparedStreamingLimits.FragmentCharacters,
                "Prepared stream capabilities do not match this exact owner and bounded codec.");
            return value;
        });

    internal static string MetadataSha256(PreparedModelMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
        Require(bytes.Length <= RemotePreparedStreamingLimits.StartBytes, "Prepared metadata exceeded its frame bound.");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal static RemotePreparedStreamFrame Start(RemotePreparedStreamRequest request, PreparedModelMetadata metadata)
    {
        ValidateRequest(request);
        Require(MetadataSha256(metadata) == request.ExpectedMetadataSha256, "Prepared stream metadata changed.");
        return Frame(request, "start", 0) with { Metadata = metadata };
    }
    internal static RemotePreparedStreamFrame TextFrame(RemotePreparedStreamRequest request, int sequence, string text) =>
        Frame(request, "text", sequence) with { Text = text };
    internal static RemotePreparedStreamFrame Terminal(RemotePreparedStreamRequest request, int sequence, RemoteGeneratedResponse response) =>
        Frame(request, "terminal", sequence) with { Response = response };
    private static RemotePreparedStreamFrame Frame(RemotePreparedStreamRequest r, string kind, int sequence) =>
        new(RemotePreparedStreamingLimits.Schema, kind, sequence, r.StreamId, r.ExpectedWorkerInstallationId,
            r.ExpectedControllerInstallationId, r.ProtocolVersion, r.LeaseId, r.ExpectedMetadataSha256);

    internal static async Task WriteFrameAsync(Stream output, RemotePreparedStreamFrame frame, CancellationToken token)
    {
        ValidateFrame(frame);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        Require(bytes.Length <= Limit(frame.Kind), "Prepared stream frame exceeded its byte bound.");
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        await output.WriteAsync(new byte[] { 10 }, token).ConfigureAwait(false);
    }

    internal static async Task<RemoteGeneratedResponse> ReadAsync(Stream input, RemotePreparedStreamRequest request,
        Action<string>? onText, CancellationToken token)
    {
        ValidateRequest(request);
        var reader = new Lines(input);
        var sequence = 0; long characters = 0;
        PreparedModelMetadata? prepared = null;
        RemoteGeneratedResponse? terminal = null;
        while (await reader.ReadAsync(token).ConfigureAwait(false) is { } bytes)
        {
            Require(sequence < RemotePreparedStreamingLimits.Frames, "Prepared stream exceeded its frame-count bound.");
            Require(terminal is null, "Prepared stream emitted data after its terminal receipt.");
            var frame = ParseFrame(bytes);
            Require(frame.Sequence == sequence++ && frame.StreamId == request.StreamId &&
                frame.WorkerInstallationId == request.ExpectedWorkerInstallationId &&
                frame.ControllerInstallationId == request.ExpectedControllerInstallationId &&
                frame.ProtocolVersion == request.ProtocolVersion && frame.LeaseId == request.LeaseId &&
                frame.MetadataSha256 == request.ExpectedMetadataSha256, "Prepared stream changed identity or sequence.");
            if (prepared is null)
            {
                Require(frame.Kind == "start" && frame.Metadata is not null &&
                    MetadataSha256(frame.Metadata) == request.ExpectedMetadataSha256, "Prepared stream did not acknowledge its exact metadata.");
                prepared = frame.Metadata;
            }
            else if (frame.Kind == "text")
            {
                characters += frame.Text!.Length;
                Require(characters <= RemotePreparedProtocol.MaximumOutputCharacters, "Prepared callback text exceeded its total bound.");
                onText?.Invoke(frame.Text!);
            }
            else if (frame.Kind == "terminal")
            {
                terminal = frame.Response!;
                Require(terminal.ProtocolVersion == request.ProtocolVersion && terminal.LeaseId == request.LeaseId &&
                    terminal.Metadata is not null && MetadataSha256(terminal.Metadata) == request.ExpectedMetadataSha256 &&
                    JsonSerializer.Serialize(terminal.Metadata, Json) == JsonSerializer.Serialize(prepared, Json),
                    "Prepared terminal did not match the acknowledged lease and complete metadata.");
                ValidateTerminal(terminal);
                // EOF is a framing boundary, never a substitute for this verified terminal DTO.
                using var finish = CancellationTokenSource.CreateLinkedTokenSource(token);
                finish.CancelAfter(TimeSpan.FromSeconds(5));
                Require(await reader.ReadAsync(finish.Token).ConfigureAwait(false) is null,
                    "Prepared stream emitted data after its terminal receipt.");
                return terminal;
            }
            else throw new RemoteModelProtocolException("Prepared stream repeated its start frame.");
        }
        throw new RemoteModelProtocolException("Prepared stream ended without verified terminal evidence.");
    }

    private static RemotePreparedStreamFrame ParseFrame(byte[] bytes)
    {
        return Parse(bytes, RemotePreparedStreamingLimits.TerminalBytes, root =>
        {
            var kind = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() : null;
            Require(kind is "start" or "text" or "terminal", "Prepared stream frame kind is invalid.");
            var payload = kind switch { "start" => "metadata", "text" => "text", _ => "response" };
            var f = Fields(root, Common.Append(payload));
            var frame = new RemotePreparedStreamFrame(Text(f, "schema", 64), kind!, Integer(f, "sequence"),
                Text(f, "streamId", 32), Text(f, "workerInstallationId", 128), Text(f, "controllerInstallationId", 128),
                Integer(f, "protocolVersion"), Text(f, "leaseId", 32), Text(f, "metadataSha256", 64));
            var value = Required(f, payload);
            if (kind == "text") frame = frame with { Text = Text(f, payload, RemotePreparedStreamingLimits.FragmentCharacters) };
            else
            {
                UniqueTree(value);
                frame = kind == "start" ? frame with { Metadata = ReadMetadata(value) }
                    : frame with { Response = ReadResponse(value) };
            }
            ValidateFrame(frame);
            Require(bytes.Length <= Limit(frame.Kind), "Prepared frame exceeded its declared byte bound.");
            return frame;
        });
    }

    private static PreparedModelMetadata ReadMetadata(JsonElement value)
    {
        var metadata = value.Deserialize<PreparedModelMetadata>(StrictJson);
        Require(metadata is not null && JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(metadata, Json)),
            "Prepared metadata fields were incomplete or contradicted their frozen values.");
        return metadata;
    }

    private static RemoteGeneratedResponse ReadResponse(JsonElement value)
    {
        var fields = Fields(value, ["protocolVersion", "leaseId", "metadata", "result"]);
        var metadata = ReadMetadata(Required(fields, "metadata"));
        var result = Required(fields, "result");
        var resultFields = Fields(result, ["finish", "text", "generatedTokens", "evaluatedTokens",
            "cancellationConfirmed", "error", "promptCache"]);
        Require(Required(resultFields, "finish").ValueKind == JsonValueKind.String,
            "Prepared terminal finish requires its canonical typed value.");
        var response = value.Deserialize<RemoteGeneratedResponse>(StrictJson);
        Require(response is not null && response.Metadata == metadata, "Prepared terminal metadata could not be read exactly.");
        return response;
    }

    private static void ValidateFrame(RemotePreparedStreamFrame f)
    {
        Require(f.Schema == RemotePreparedStreamingLimits.Schema && f.Sequence is >= 0 and < RemotePreparedStreamingLimits.Frames &&
            Uuid(f.StreamId) && Uuid(f.LeaseId) && Hash(f.MetadataSha256), "Prepared frame has invalid identity or sequence.");
        ValidateOwner(f.WorkerInstallationId, f.ControllerInstallationId);
        RemotePreparedProtocol.Validate(new RemoteLeaseRequest(f.ProtocolVersion, f.LeaseId));
        Require(f.Kind switch
        {
            "start" => f.Sequence == 0 && f.Metadata is not null && f.Text is null && f.Response is null,
            "text" => f.Sequence > 0 && f.Metadata is null && f.Response is null &&
                f.Text is { Length: > 0 and <= RemotePreparedStreamingLimits.FragmentCharacters },
            "terminal" => f.Sequence > 0 && f.Metadata is null && f.Text is null && f.Response is not null,
            _ => false
        }, "Prepared frame has invalid payload fields.");
        if (f.Text is not null) _ = Utf8.GetByteCount(f.Text);
    }

    private static void ValidateTerminal(RemoteGeneratedResponse response)
    {
        var result = response.Result;
        Require(result is not null && result.Text is not null && result.Text.Length <= RemotePreparedProtocol.MaximumOutputCharacters &&
            Enum.IsDefined(result.Finish) && result.GeneratedTokens is not < 0 && result.EvaluatedTokens is not < 0 &&
            (!result.CancellationConfirmed || result.Finish == ModelGenerationFinish.Cancelled) &&
            JsonSerializer.SerializeToUtf8Bytes(response, CerebralJson.Options).Length <= RemotePreparedProtocol.MaximumResponseBytes,
            "Prepared terminal exceeded its existing result contract.");
        _ = Utf8.GetByteCount(result!.Text);
    }

    private static T Parse<T>(ReadOnlySpan<byte> bytes, int maximum, Func<JsonElement, T> read)
    {
        try { using var document = OwnerSnapshotJson.Parse(bytes.ToArray(), maximum); return read(document.RootElement); }
        catch (Exception error) when (error is OwnerSnapshotUnavailableException or JsonException or ArgumentException or OverflowException)
        { throw new RemoteModelProtocolException("Prepared stream returned invalid bounded JSON or scalar encoding."); }
    }
    private static Dictionary<string, JsonElement> Fields(JsonElement root, IEnumerable<string> names) =>
        OwnerSnapshotJson.Fields(root, new HashSet<string>(names, StringComparer.Ordinal));
    private static JsonElement Required(Dictionary<string, JsonElement> fields, string name) => OwnerSnapshotJson.Required(fields, name);
    private static int Integer(Dictionary<string, JsonElement> fields, string name) => OwnerSnapshotJson.Integer(fields, name);
    private static string Text(Dictionary<string, JsonElement> fields, string name, int maximum) =>
        OwnerSnapshotJson.Text(fields, name, maximum, false, out _);
    private static void UniqueTree(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) foreach (var child in OwnerSnapshotJson.Fields(value).Values) UniqueTree(child);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) UniqueTree(child);
    }
    private static void ValidateOwner(string worker, string controller) =>
        Require(!string.IsNullOrWhiteSpace(worker) && worker.Length <= 128 && !worker.Any(char.IsControl) &&
            !string.IsNullOrWhiteSpace(controller) && controller.Length <= 128 && !controller.Any(char.IsControl) && worker != controller,
            "Prepared stream owner comparison is invalid.");
    private static bool Uuid(string value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Hash(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static int Limit(string kind) => kind switch
    { "start" => RemotePreparedStreamingLimits.StartBytes, "text" => RemotePreparedStreamingLimits.TextBytes, "terminal" => RemotePreparedStreamingLimits.TerminalBytes, _ => 0 };
    private static void Require([DoesNotReturnIf(false)] bool ok, string message)
    { if (!ok) throw new RemoteModelProtocolException(message); }

    private sealed class Lines(Stream source)
    {
        private readonly byte[] buffer = new byte[8192];
        private int offset, length; private long total;
        internal async Task<byte[]?> ReadAsync(CancellationToken token)
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (offset == length)
                {
                    length = await source.ReadAsync(buffer, token).ConfigureAwait(false); offset = 0;
                    total += length;
                    Require(total <= RemotePreparedStreamingLimits.StreamBytes, "Prepared stream exceeded its total byte bound.");
                    if (length == 0)
                    {
                        Require(line.Length == 0, "Prepared stream ended inside an unterminated frame.");
                        return null;
                    }
                }
                var start = offset;
                while (offset < length && buffer[offset] != 10) offset++;
                var count = offset - start;
                Require(line.Length + count <= RemotePreparedStreamingLimits.TerminalBytes, "Prepared line exceeded its bounded frame size.");
                line.Write(buffer, start, count);
                if (offset == length) continue;
                offset++;
                Require(line.Length > 0, "Prepared stream returned a blank frame.");
                return line.ToArray();
            }
        }
    }
}
