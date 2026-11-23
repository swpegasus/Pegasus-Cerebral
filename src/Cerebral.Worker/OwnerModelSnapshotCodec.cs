using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

internal sealed record OwnerSnapshotRawModelRow(string Id, byte[] Body);
internal sealed record OwnerSnapshotBindingEncoding(byte[] CanonicalJson, string Sha256, int FingerprintPreimageBytes);

/// <summary>Private versioned inspection codecs. Neither these digests nor a complete catalog grant preparation.</summary>
internal static class OwnerModelSnapshotCodec
{
    internal const int MaximumPreimageBytes = 4 * 1024 * 1024;
    internal const int MaximumCatalogRows = 256;
    internal const int MaximumModelBodyBytes = 8 * 1024 * 1024;
    internal const string BindingCodec = "Cerebral.RemotePreparedModelBinding.canonical/1";
    private static readonly byte[] BindingDomain = Encoding.ASCII.GetBytes("Cerebral.RemotePreparedModelBinding/1\n");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static OwnerSnapshotBindingEncoding EncodeBinding(RemotePreparedModelBinding binding)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(binding);
            binding.Validate();
            // IReadOnlyList alone is not immutable. Copy bounded stop values before encoding.
            var count = binding.StopSequences.Count;
            if (count > 128) throw new InvalidDataException("The binding stop sequence limit was exceeded.");
            var stops = new string[count];
            for (var index = 0; index < count; index++) stops[index] = binding.StopSequences[index];
            binding = binding with { StopSequences = Array.AsReadOnly(stops) };
            binding.Validate();
            var json = new CanonicalWriter(MaximumPreimageBytes - BindingDomain.Length);
            json.Ascii("{\"Id\":"); json.String(binding.Id);
            json.Ascii(",\"Path\":"); json.String(binding.Path);
            json.Ascii(",\"ChatTemplate\":"); json.NullableString(binding.ChatTemplate);
            json.Ascii(",\"StopSequences\":[");
            for (var index = 0; index < stops.Length; index++)
            {
                if (index != 0) json.Ascii(",");
                json.String(stops[index]);
            }
            json.Ascii("],\"MaximumContextTokens\":"); json.Integer(binding.MaximumContextTokens);
            json.Ascii(",\"ParallelRequests\":"); json.Integer(binding.ParallelRequests);
            json.Ascii(",\"GpuLayers\":"); json.Integer(binding.GpuLayers);
            json.Ascii(",\"VisionProjectorPath\":"); json.NullableString(binding.VisionProjectorPath);
            json.Ascii("}");
            var bytes = json.Bytes();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(BindingDomain); hash.AppendData(bytes);
            return new(bytes, Hex(hash.GetHashAndReset()), checked(bytes.Length + BindingDomain.Length));
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or EncoderFallbackException)
        {
            // Imported values and parser/provider messages must not cross the public diagnostic boundary.
            throw new InvalidDataException("The binding cannot be represented by the immutable inspection codec.");
        }
    }

    internal static string CatalogFence(IReadOnlyList<OwnerSnapshotRawModelRow> rows)
    {
        if (rows is null || rows.Count > MaximumCatalogRows)
            throw new InvalidDataException("The complete catalog exceeds its row bound.");
        var ordered = rows.OrderBy(row => row?.Id, StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("Cerebral.ModelCatalogFence/1\n"));
        string? previous = null; long bytes = 0;
        foreach (var row in ordered)
        {
            ValidateRow(row);
            if (previous == row.Id) throw new InvalidDataException("A complete catalog cannot contain duplicate storage IDs.");
            previous = row.Id;
            bytes += row.Body.Length;
            if (bytes > MaximumModelBodyBytes) throw new InvalidDataException("The complete catalog exceeds its byte bound.");
            AppendRow(hash, row);
        }
        return Hex(hash.GetHashAndReset());
    }

    internal static string RegistrationFence(OwnerSnapshotRawModelRow row)
    {
        ValidateRow(row);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("Cerebral.ModelRegistrationFence/1\n"));
        AppendRow(hash, row);
        return Hex(hash.GetHashAndReset());
    }

    internal static void ValidateIdentifier(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 || id.Any(char.IsControl) || id.Any(char.IsWhiteSpace))
            throw new InvalidDataException("A bounded exact identifier is required.");
        try { _ = StrictUtf8.GetByteCount(id); }
        catch (EncoderFallbackException) { throw new InvalidDataException("The identifier is not strict Unicode."); }
    }

    private static void ValidateRow(OwnerSnapshotRawModelRow? row)
    {
        if (row is null || row.Body is null || row.Body.Length is < 1 or > MaximumModelBodyBytes)
            throw new InvalidDataException("A complete bounded raw storage row is required.");
        ValidateIdentifier(row.Id);
        try { _ = StrictUtf8.GetCharCount(row.Body); }
        catch (DecoderFallbackException) { throw new InvalidDataException("The stored body is not strict UTF-8."); }
    }

    private static void AppendRow(IncrementalHash hash, OwnerSnapshotRawModelRow row)
    {
        var id = StrictUtf8.GetBytes(row.Id);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)id.Length));
        hash.AppendData(length); hash.AppendData(id); hash.AppendData(SHA256.HashData(row.Body));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed class CanonicalWriter(int maximumBytes)
    {
        private readonly ArrayBufferWriter<byte> buffer = new();

        internal void Ascii(string value)
        {
            Reserve(value.Length);
            Encoding.ASCII.GetBytes(value.AsSpan(), buffer.GetSpan(value.Length));
            buffer.Advance(value.Length);
        }

        internal void Integer(int value) => Ascii(value.ToString(CultureInfo.InvariantCulture));
        internal void NullableString(string? value) { if (value is null) Ascii("null"); else String(value); }

        internal void String(string value)
        {
            if (value is null) throw new InvalidDataException("A nonnull string is required by the binding codec.");
            Ascii("\"");
            for (var index = 0; index < value.Length;)
            {
                var current = value[index];
                if (current == '"') { Ascii("\\\""); index++; }
                else if (current == '\\') { Ascii("\\\\"); index++; }
                else if (current < 32) { Ascii("\\u00" + ((int)current).ToString("x2", CultureInfo.InvariantCulture)); index++; }
                else
                {
                    if (Rune.DecodeFromUtf16(value.AsSpan(index), out var scalar, out var consumed) != OperationStatus.Done)
                        throw new InvalidDataException("The binding contains an unpaired UTF-16 surrogate.");
                    var size = scalar.Utf8SequenceLength;
                    Reserve(size);
                    scalar.EncodeToUtf8(buffer.GetSpan(size));
                    buffer.Advance(size); index += consumed;
                }
            }
            Ascii("\"");
        }

        private void Reserve(int count)
        {
            if (count < 0 || buffer.WrittenCount > maximumBytes - count)
                throw new InvalidDataException("The complete binding exceeds the inspection preimage byte bound.");
        }

        internal byte[] Bytes() => buffer.WrittenSpan.ToArray();
    }
}
