using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cerebral.Worker;

/// <summary>
/// A bounded, allocation-conscious view of model-identifying GGUF metadata. Inspection stops
/// immediately after the key/value table and never reads tensor descriptors or tensor data.
/// </summary>
public sealed class GgufModelMetadata
{
    public uint Version { get; init; }
    public ulong TensorCount { get; init; }
    public ulong MetadataEntryCount { get; init; }
    public long MetadataEndOffset { get; init; }
    public string Name { get; init; } = "";
    public string? Architecture { get; init; }
    public string? DeclaredType { get; init; }
    public ulong? ParameterCount { get; init; }
    public string? SizeLabel { get; init; }
    public uint? FileType { get; init; }
    public string? Quantization { get; init; }
    public int? NativeContextTokens { get; init; }
    public int? BlockCount { get; init; }
    public int? EmbeddingLength { get; init; }
    public int? HeadCount { get; init; }
    public int? KvHeadCount { get; init; }
    internal IReadOnlyList<int>? KvHeadCountsByLayer { get; init; }
    internal string? KvShapeWarning { get; init; }
    public bool HasEmbeddedChatTemplate { get; init; }
    public string Purpose { get; init; } = "General text";
    public string PurposeConfidence { get; init; } = "low";
    public bool UsesFilenameEstimate { get; init; }
    public bool UsesMetadataEstimate { get; init; }
    public IReadOnlyList<string> SourceNotes { get; init; } = [];
}

public static partial class GgufMetadataInspector
{
    public const ulong MaximumMetadataEntries = 16_384;
    public const ulong MaximumArrayElements = 4_000_000;
    public const long MaximumMetadataBytes = 512L * 1024 * 1024;
    private const ulong MaximumTensorCount = 4_000_000;
    private const int MaximumKeyBytes = 1_024;
    private const int MaximumCapturedTextBytes = 1024 * 1024;
    private const int MaximumCapturedTags = 64;
    private const int MaximumCapturedLayerCounts = 4_096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static GgufModelMetadata Inspect(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (ModelRepository.IsSplitFile(Path.GetFileName(fullPath)))
            throw new InvalidDataException("Split GGUF files are not supported by this inspector; choose a single-file GGUF.");
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        try
        {
            return Inspect(stream, Path.GetFileNameWithoutExtension(fullPath), cancellationToken);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("GGUF metadata ended unexpectedly before the declared key/value table was complete.", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("GGUF metadata contains invalid UTF-8 text.", ex);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("GGUF metadata contains a length or count that is too large.", ex);
        }
    }

    private static GgufModelMetadata Inspect(Stream stream, string fileStem, CancellationToken cancellationToken)
    {
        var reader = new MetadataReader(stream, cancellationToken);
        Span<byte> magic = stackalloc byte[4];
        reader.ReadExactly(magic);
        if (!magic.SequenceEqual("GGUF"u8)) throw new InvalidDataException("File is not a GGUF model (missing GGUF signature).");

        var version = reader.ReadUInt32();
        if (version is 0x02000000 or 0x03000000)
            throw new InvalidDataException("Big-endian GGUF metadata is not supported; use a little-endian GGUF v2 or v3 file.");
        if (version is not (2 or 3))
            throw new InvalidDataException($"Unsupported GGUF version {version}; only little-endian GGUF v2 and v3 are supported.");

        var tensorCount = reader.ReadUInt64();
        var metadataCount = reader.ReadUInt64();
        if (tensorCount > MaximumTensorCount) throw new InvalidDataException($"GGUF declares too many tensors ({tensorCount:N0}; maximum {MaximumTensorCount:N0}).");
        if (metadataCount > MaximumMetadataEntries) throw new InvalidDataException($"GGUF declares too many metadata entries ({metadataCount:N0}; maximum {MaximumMetadataEntries:N0}).");

        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var layerNumbers = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        var notes = new List<string>();
        var tags = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var hasTemplate = false;
        var usesFilenameEstimate = false;
        var usesMetadataEstimate = false;

        for (ulong index = 0; index < metadataCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = reader.ReadString(MaximumKeyBytes, "metadata key");
            if (key.Length == 0 || key.Any(char.IsControl)) throw new InvalidDataException($"GGUF metadata key {index} is empty or contains control characters.");
            if (!keys.Add(key)) throw new InvalidDataException($"GGUF contains duplicate metadata key '{key}'.");
            var type = reader.ReadValueType($"metadata key '{key}'");
            var interest = InterestFor(key);
            // Only Gemma 4's observed attention metadata admits per-layer KV-head arrays.
            if (interest == ValueInterest.LayerNumber && type == GgufValueType.Array &&
                key != "gemma4.attention.head_count_kv")
                throw new InvalidDataException($"GGUF metadata key '{key}' has an unsupported per-layer KV-head array shape.");
            var value = reader.ReadValue(type, interest is ValueInterest.Text or ValueInterest.Tags,
                interest == ValueInterest.Tags, interest == ValueInterest.LayerNumber, key);

            switch (interest)
            {
                case ValueInterest.Text:
                    if (type != GgufValueType.String || value.Text is null)
                        throw new InvalidDataException($"GGUF metadata key '{key}' must contain a string.");
                    text[key] = value.Text;
                    break;
                case ValueInterest.Number:
                    if (!value.Integer.HasValue)
                        throw new InvalidDataException($"GGUF metadata key '{key}' must contain a non-negative integer.");
                    numbers[key] = value.Integer.Value;
                    break;
                case ValueInterest.LayerNumber:
                    if (type == GgufValueType.Array)
                    {
                        if (value.Integers is null) throw new InvalidDataException($"GGUF metadata key '{key}' must contain integer layer counts.");
                        layerNumbers[key] = value.Integers;
                    }
                    else
                    {
                        if (value.Integer is null || value.Integer.Value > int.MaxValue)
                            throw new InvalidDataException($"GGUF metadata key '{key}' must contain a non-negative supported integer.");
                        numbers[key] = value.Integer.Value;
                    }
                    break;
                case ValueInterest.Tags:
                    if (type == GgufValueType.String && value.Text is not null) tags.Add(value.Text);
                    else if (type == GgufValueType.Array && value.ArrayElementType == GgufValueType.String && value.Strings is not null) tags.AddRange(value.Strings);
                    else throw new InvalidDataException($"GGUF metadata key '{key}' must contain a string or string array.");
                    break;
                case ValueInterest.Template:
                    if (type != GgufValueType.String && !(type == GgufValueType.Array && value.ArrayElementType == GgufValueType.String))
                        throw new InvalidDataException($"GGUF metadata key '{key}' must contain a string or string array.");
                    hasTemplate |= value.NonEmpty;
                    break;
            }
        }

        if (numbers.TryGetValue("split.count", out var splitCount) && splitCount > 1)
            throw new InvalidDataException($"Split GGUF files are not supported by this inspector (metadata declares {splitCount:N0} shards); choose a single-file GGUF.");

        var architecture = Text(text, "general.architecture");
        foreach (var entry in layerNumbers)
        {
            var blockKey = entry.Key[..^"attention.head_count_kv".Length] + "block_count";
            if (!numbers.TryGetValue(blockKey, out var declaredBlocks) || declaredBlocks == 0 ||
                declaredBlocks != (ulong)entry.Value.Count)
                throw new InvalidDataException($"GGUF metadata key '{entry.Key}' must have one positive integer per declared block in '{blockKey}'.");
        }
        var declaredType = Text(text, "general.type")?.ToLowerInvariant();
        var metadataName = Text(text, "general.name");
        var name = metadataName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fileStem;
            usesFilenameEstimate = true;
            notes.Add("Name inferred from the GGUF filename.");
        }
        else notes.Add("Name read from general.name.");
        if (!string.IsNullOrWhiteSpace(architecture)) notes.Add("Architecture read from general.architecture.");
        if (!string.IsNullOrWhiteSpace(declaredType)) notes.Add("Model type read from general.type.");

        numbers.TryGetValue("general.parameter_count", out var rawParameters);
        ulong? parameterCount = numbers.ContainsKey("general.parameter_count") ? rawParameters : null;
        var sizeLabel = Text(text, "general.size_label");
        if (parameterCount.HasValue) notes.Add("Parameter count read from general.parameter_count.");
        if (!string.IsNullOrWhiteSpace(sizeLabel)) notes.Add("Size label read from general.size_label.");
        if (!parameterCount.HasValue && TryInferParameterCount(sizeLabel, out var sizeParameters))
        {
            parameterCount = sizeParameters;
            usesMetadataEstimate = true;
            notes.Add("Parameter count estimated from general.size_label.");
        }
        if (!parameterCount.HasValue && TryInferParameterCount(name + " " + fileStem, out var nameParameters))
        {
            parameterCount = nameParameters;
            usesFilenameEstimate = true;
            notes.Add("Parameter count estimated from the model name or filename.");
        }
        if (string.IsNullOrWhiteSpace(sizeLabel) && parameterCount.HasValue) sizeLabel = FormatParameterCount(parameterCount.Value);

        uint? fileType = null;
        if (numbers.TryGetValue("general.file_type", out var rawFileType))
        {
            if (rawFileType > uint.MaxValue) throw new InvalidDataException("GGUF general.file_type exceeds the supported unsigned 32-bit range.");
            fileType = (uint)rawFileType;
        }
        var quantization = fileType.HasValue ? FileTypeLabel(fileType.Value) : null;
        if (fileType.HasValue) notes.Add("Quantization read from general.file_type.");
        if (quantization is null && TryInferQuantization(name + " " + fileStem, out var inferredQuantization))
        {
            quantization = inferredQuantization;
            usesFilenameEstimate = true;
            notes.Add("Quantization inferred from the model name or filename.");
        }

        var context = Metric(numbers, architecture, "context_length", notes, "Native context");
        var blocks = Metric(numbers, architecture, "block_count", notes, "Block count");
        var embedding = Metric(numbers, architecture, "embedding_length", notes, "Embedding length");
        var heads = Metric(numbers, architecture, "attention.head_count", notes, "Attention-head count");
        var kvKey = SelectKvHeadKey(numbers, layerNumbers, architecture);
        IReadOnlyList<int>? kvByLayer = kvKey is not null && layerNumbers.TryGetValue(kvKey, out var selectedLayers)
            ? selectedLayers : null;
        int? kvHeads = null;
        if (kvByLayer is not null && kvByLayer.All(x => x == kvByLayer[0])) kvHeads = kvByLayer[0];
        else if (kvByLayer is null && kvKey is not null && numbers.TryGetValue(kvKey, out var scalarKvHeads))
        {
            kvHeads = (int)scalarKvHeads;
            notes.Add($"KV-head count read from {kvKey}.");
        }
        string? kvShapeWarning = null;
        if (kvByLayer is not null)
        {
            notes.Add($"KV-head counts read per layer from {kvKey}.");
            if (kvHeads is null)
                kvShapeWarning = "Per-layer KV-head counts vary; automatic context sizing cannot infer one cache width.";
        }
        var keyLength = Metric(numbers, architecture, "attention.key_length", notes, "Attention-key length");
        var valueLength = Metric(numbers, architecture, "attention.value_length", notes, "Attention-value length");
        if (keyLength.HasValue || valueLength.HasValue)
        {
            var expectedWidth = embedding is > 0 && heads is > 0 && embedding % heads == 0 ? embedding / heads : null;
            if (keyLength is not > 0 || valueLength is not > 0 || expectedWidth is null ||
                keyLength != expectedWidth || valueLength != expectedWidth)
            {
                kvHeads = null;
                kvShapeWarning += " Explicit attention key/value widths differ from the generic cache estimate; automatic context sizing remains unavailable.";
                kvShapeWarning = kvShapeWarning.TrimStart();
            }
        }
        if (hasTemplate) notes.Add("Embedded chat template detected in GGUF metadata.");

        var purpose = InferPurpose(text, tags, metadataName, fileStem, architecture, hasTemplate);
        usesFilenameEstimate |= purpose.Source == "filename";
        notes.Add(purpose.Note);
        return new GgufModelMetadata
        {
            Version = version,
            TensorCount = tensorCount,
            MetadataEntryCount = metadataCount,
            MetadataEndOffset = reader.Position,
            Name = name,
            Architecture = architecture,
            DeclaredType = declaredType,
            ParameterCount = parameterCount,
            SizeLabel = sizeLabel,
            FileType = fileType,
            Quantization = quantization,
            NativeContextTokens = context,
            BlockCount = blocks,
            EmbeddingLength = embedding,
            HeadCount = heads,
            KvHeadCount = kvHeads,
            KvHeadCountsByLayer = kvByLayer,
            KvShapeWarning = kvShapeWarning,
            HasEmbeddedChatTemplate = hasTemplate,
            Purpose = purpose.Purpose,
            PurposeConfidence = purpose.Confidence,
            UsesFilenameEstimate = usesFilenameEstimate,
            UsesMetadataEstimate = usesMetadataEstimate,
            SourceNotes = notes
        };
    }

    private enum ValueInterest { None, Text, Number, LayerNumber, Tags, Template }

    private static ValueInterest InterestFor(string key)
    {
        if (key is "general.name" or "general.architecture" or "general.size_label" or "general.type" or "general.description" or "general.finetune" or "general.basename") return ValueInterest.Text;
        if (key is "general.parameter_count" or "general.file_type" or "split.count" || key.EndsWith(".context_length", StringComparison.Ordinal) ||
            key.EndsWith(".block_count", StringComparison.Ordinal) || key.EndsWith(".embedding_length", StringComparison.Ordinal) ||
            key.EndsWith(".attention.head_count", StringComparison.Ordinal) ||
            key.EndsWith(".attention.key_length", StringComparison.Ordinal) ||
            key.EndsWith(".attention.value_length", StringComparison.Ordinal)) return ValueInterest.Number;
        if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal)) return ValueInterest.LayerNumber;
        if (key == "general.tags") return ValueInterest.Tags;
        if (key == "tokenizer.chat_templates" || key == "tokenizer.chat_template" || key.StartsWith("tokenizer.chat_template.", StringComparison.Ordinal)) return ValueInterest.Template;
        return ValueInterest.None;
    }

    private static string? Text(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int? Metric(IReadOnlyDictionary<string, ulong> values, string? architecture, string suffix, List<string> notes, string label)
    {
        KeyValuePair<string, ulong>? selected = null;
        if (!string.IsNullOrWhiteSpace(architecture) && values.TryGetValue(architecture + "." + suffix, out var exact))
            selected = new(architecture + "." + suffix, exact);
        else
        {
            var matches = values.Where(x => x.Key.EndsWith("." + suffix, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1) selected = matches[0];
        }
        if (!selected.HasValue) return null;
        if (selected.Value.Value > int.MaxValue) throw new InvalidDataException($"GGUF metadata key '{selected.Value.Key}' exceeds the supported integer range.");
        notes.Add($"{label} read from {selected.Value.Key}.");
        return (int)selected.Value.Value;
    }

    private static string? SelectKvHeadKey(IReadOnlyDictionary<string, ulong> numbers,
        IReadOnlyDictionary<string, IReadOnlyList<int>> layers, string? architecture)
    {
        var exact = architecture + ".attention.head_count_kv";
        if (!string.IsNullOrWhiteSpace(architecture) && (numbers.ContainsKey(exact) || layers.ContainsKey(exact))) return exact;
        var matches = numbers.Keys.Concat(layers.Keys)
            .Where(x => x.EndsWith(".attention.head_count_kv", StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool TryInferParameterCount(string? input, out ulong count)
    {
        count = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var match = ParameterPattern().Match(input);
        if (!match.Success || !decimal.TryParse(match.Groups["value"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return false;
        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch { "K" => 1_000m, "M" => 1_000_000m, "B" => 1_000_000_000m, "T" => 1_000_000_000_000m, _ => 0m };
        if (multiplier == 0 || value <= 0 || value * multiplier > ulong.MaxValue) return false;
        count = (ulong)decimal.Round(value * multiplier, 0, MidpointRounding.AwayFromZero);
        return true;
    }

    private static string FormatParameterCount(ulong count)
    {
        if (count >= 1_000_000_000_000) return (count / 1_000_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "T";
        if (count >= 1_000_000_000) return (count / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        if (count >= 1_000_000) return (count / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (count >= 1_000) return (count / 1_000d).ToString("0.##", CultureInfo.InvariantCulture) + "K";
        return count.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryInferQuantization(string input, out string quantization)
    {
        var match = QuantizationPattern().Match(input);
        quantization = match.Success ? match.Value.ToUpperInvariant() : "";
        return match.Success;
    }

    private static string? FileTypeLabel(uint value) => value switch
    {
        0 => "F32", 1 => "F16", 2 => "Q4_0", 3 => "Q4_1", 7 => "Q8_0", 8 => "Q5_0", 9 => "Q5_1",
        10 => "Q2_K", 11 => "Q3_K_S", 12 => "Q3_K_M", 13 => "Q3_K_L", 14 => "Q4_K_S", 15 => "Q4_K_M",
        16 => "Q5_K_S", 17 => "Q5_K_M", 18 => "Q6_K", 19 => "IQ2_XXS", 20 => "IQ2_XS", 21 => "Q2_K_S",
        22 => "IQ3_XS", 23 => "IQ3_XXS", 24 => "IQ1_S", 25 => "IQ4_NL", 26 => "IQ3_S", 27 => "IQ3_M",
        28 => "IQ2_S", 29 => "IQ2_M", 30 => "IQ4_XS", 31 => "IQ1_M", 32 => "BF16", 36 => "TQ1_0",
        37 => "TQ2_0", 38 => "MXFP4_MOE", 39 => "NVFP4", 40 => "Q1_0", 41 => "Q2_0",
        _ => "GGUF file type " + value.ToString(CultureInfo.InvariantCulture)
    };

    private static (string Purpose, string Confidence, string Source, string Note) InferPurpose(
        IReadOnlyDictionary<string, string> text, IReadOnlyList<string> tags, string? metadataName, string fileStem, string? architecture, bool hasTemplate)
    {
        var declaredType = Text(text, "general.type");
        if (PurposeForDeclaredType(declaredType) is { } declaredPurpose)
            return (declaredPurpose, "high", "metadata", $"Purpose read from authoritative GGUF general.type '{declaredType}'.");

        var metadata = string.Join(' ', new[]
        {
            Text(text, "general.description"), Text(text, "general.finetune"),
            Text(text, "general.basename"), metadataName, architecture, string.Join(' ', tags)
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var fromMetadata = PurposeFrom(metadata);
        if (fromMetadata is not null) return (fromMetadata, "high", "metadata", "Purpose inferred from GGUF model metadata.");
        var fromFile = PurposeFrom(fileStem);
        if (fromFile is not null) return (fromFile, "medium", "filename", "Purpose inferred from the GGUF filename.");
        if (hasTemplate) return ("General assistant", "medium", "template", "General assistant purpose inferred from the embedded chat template.");
        return ("General text", "low", "fallback", "No specialized purpose metadata was found; general text is a low-confidence fallback.");
    }

    internal static string? PurposeForDeclaredType(string? declaredType)
    {
        if (string.IsNullOrWhiteSpace(declaredType)) return null;
        return declaredType.Trim().ToLowerInvariant() switch
        {
            "adapter" or "lora" => "Adapter",
            "projector" or "mmproj" or "vision-projector" or "vision_projector" => "Vision projector",
            "embedding" or "embedder" => "Embedding",
            "reranker" or "reranking" => "Reranking",
            "audio" or "speech" => "Audio",
            "vision" or "image" => "Vision",
            _ => null
        };
    }

    private static string? PurposeFrom(string value)
    {
        if (PurposeAdapterPattern().IsMatch(value)) return "Adapter";
        if (PurposeProjectorPattern().IsMatch(value)) return "Vision projector";
        if (PurposeEmbeddingPattern().IsMatch(value)) return "Embedding";
        if (PurposeRerankingPattern().IsMatch(value)) return "Reranking";
        if (PurposeAudioPattern().IsMatch(value)) return "Audio";
        if (PurposeVisionPattern().IsMatch(value)) return "Vision";
        if (PurposeCodingPattern().IsMatch(value)) return "Coding";
        if (PurposeMathPattern().IsMatch(value)) return "Mathematics";
        if (PurposeReasoningPattern().IsMatch(value)) return "Reasoning";
        if (PurposeWritingPattern().IsMatch(value)) return "Writing";
        if (PurposeAssistantPattern().IsMatch(value)) return "General assistant";
        return null;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<value>\d+(?:\.\d+)?)\s*(?<unit>[KMBT])(?=(?:[-_.\s]|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParameterPattern();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:IQ\d(?:_[A-Za-z0-9]+)+|Q\d(?:_[A-Za-z0-9]+)+|TQ\d(?:_[A-Za-z0-9]+)+|BF16|F16|F32|MXFP4(?:_MOE)?|NVFP4)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantizationPattern();
    [GeneratedRegex(@"\b(?:adapter|lora)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeAdapterPattern();
    [GeneratedRegex(@"\b(?:mmproj|projector)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeProjectorPattern();
    [GeneratedRegex(@"\b(?:embedding|embeddings|embedder)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeEmbeddingPattern();
    [GeneratedRegex(@"\b(?:rerank|reranker|reranking)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeRerankingPattern();
    [GeneratedRegex(@"\b(?:audio|speech|whisper|transcription|tts)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeAudioPattern();
    [GeneratedRegex(@"\b(?:vision|visual|multimodal|vlm)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeVisionPattern();
    [GeneratedRegex(@"\b(?:code|coder|coding|programming|fim)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeCodingPattern();
    [GeneratedRegex(@"\b(?:math|mathematics|mathematical)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeMathPattern();
    [GeneratedRegex(@"\b(?:reasoning|reasoner|thinking)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeReasoningPattern();
    [GeneratedRegex(@"\b(?:writer|writing|creative-writing)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeWritingPattern();
    [GeneratedRegex(@"\b(?:assistant|instruct|instruction|chat)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PurposeAssistantPattern();

    private enum GgufValueType : uint
    {
        UInt8 = 0, Int8 = 1, UInt16 = 2, Int16 = 3, UInt32 = 4, Int32 = 5, Float32 = 6,
        Bool = 7, String = 8, Array = 9, UInt64 = 10, Int64 = 11, Float64 = 12
    }

    private readonly record struct ParsedValue(string? Text, ulong? Integer, IReadOnlyList<string>? Strings, bool NonEmpty,
        GgufValueType? ArrayElementType = null, IReadOnlyList<int>? Integers = null);

    private sealed class MetadataReader(Stream stream, CancellationToken cancellationToken)
    {
        private long consumed;
        public long Position => stream.Position;

        public void ReadExactly(Span<byte> destination)
        {
            EnsureBudget(destination.Length);
            var offset = 0;
            while (offset < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(destination[offset..]);
                if (read == 0) throw new EndOfStreamException();
                offset += read; consumed += read;
            }
        }

        public uint ReadUInt32()
        {
            Span<byte> bytes = stackalloc byte[4]; ReadExactly(bytes); return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public ulong ReadUInt64()
        {
            Span<byte> bytes = stackalloc byte[8]; ReadExactly(bytes); return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }

        public GgufValueType ReadValueType(string location)
        {
            var raw = ReadUInt32();
            if (raw > (uint)GgufValueType.Float64) throw new InvalidDataException($"GGUF {location} uses unsupported value type {raw}.");
            return (GgufValueType)raw;
        }

        public string ReadString(int maximumBytes, string location)
        {
            var length = ReadUInt64();
            if (length > (ulong)maximumBytes) throw new InvalidDataException($"GGUF {location} exceeds the {maximumBytes:N0}-byte inspection limit.");
            var bytes = new byte[(int)length]; ReadExactly(bytes); return StrictUtf8.GetString(bytes);
        }

        public ParsedValue ReadValue(GgufValueType type, bool captureText, bool captureStringArray,
            bool captureIntegerArray, string key)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (type)
            {
                case GgufValueType.UInt8: return new(null, ReadByte(), null, true);
                case GgufValueType.Int8: return Signed(ReadSByte());
                case GgufValueType.UInt16: return new(null, ReadUInt16(), null, true);
                case GgufValueType.Int16: return Signed(ReadInt16());
                case GgufValueType.UInt32: return new(null, ReadUInt32(), null, true);
                case GgufValueType.Int32: return Signed(ReadInt32());
                case GgufValueType.Float32: Skip(4); return new(null, null, null, true);
                case GgufValueType.Bool:
                    var boolean = ReadByte();
                    if (boolean > 1) throw new InvalidDataException($"GGUF metadata key '{key}' contains invalid boolean value {boolean}.");
                    return new(null, null, null, true);
                case GgufValueType.String:
                    var stringLength = ReadUInt64();
                    if (!captureText) { Skip(stringLength); return new(null, null, null, stringLength > 0); }
                    if (stringLength > MaximumCapturedTextBytes) throw new InvalidDataException($"GGUF metadata key '{key}' exceeds the {MaximumCapturedTextBytes:N0}-byte captured-text limit.");
                    var bytes = new byte[(int)stringLength]; ReadExactly(bytes);
                    var value = StrictUtf8.GetString(bytes); return new(value, null, null, value.Length > 0);
                case GgufValueType.Array:
                    return ReadArray(captureStringArray, captureIntegerArray, key);
                case GgufValueType.UInt64: return new(null, ReadUInt64(), null, true);
                case GgufValueType.Int64: return Signed(ReadInt64());
                case GgufValueType.Float64: Skip(8); return new(null, null, null, true);
                default: throw new InvalidDataException($"GGUF metadata key '{key}' uses unsupported value type {(uint)type}.");
            }
        }

        private ParsedValue ReadArray(bool captureStrings, bool captureIntegers, string key)
        {
            var elementType = ReadValueType($"array '{key}'");
            if (elementType == GgufValueType.Array) throw new InvalidDataException($"GGUF metadata array '{key}' cannot contain nested arrays.");
            var count = ReadUInt64();
            if (count > MaximumArrayElements) throw new InvalidDataException($"GGUF metadata array '{key}' has too many elements ({count:N0}; maximum {MaximumArrayElements:N0}).");
            if (captureIntegers)
            {
                if (count == 0 || count > MaximumCapturedLayerCounts ||
                    elementType is not (GgufValueType.UInt8 or GgufValueType.Int8 or GgufValueType.UInt16 or
                        GgufValueType.Int16 or GgufValueType.UInt32 or GgufValueType.Int32 or
                        GgufValueType.UInt64 or GgufValueType.Int64))
                    throw new InvalidDataException($"GGUF metadata array '{key}' must contain a bounded nonempty list of integer layer counts.");
                var integers = new int[(int)count];
                for (var index = 0; index < integers.Length; index++)
                {
                    var value = ReadValue(elementType, false, false, false, key).Integer;
                    if (value is null || value.Value == 0 || value.Value > int.MaxValue)
                        throw new InvalidDataException($"GGUF metadata array '{key}' contains a non-positive or unsupported layer count.");
                    integers[index] = (int)value.Value;
                }
                return new(null, null, null, true, elementType, Array.AsReadOnly(integers));
            }
            List<string>? strings = captureStrings && elementType == GgufValueType.String ? [] : null;
            if (elementType == GgufValueType.String)
            {
                for (ulong index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = ReadUInt64();
                    if (strings is not null && strings.Count < MaximumCapturedTags)
                    {
                        if (length > MaximumCapturedTextBytes) throw new InvalidDataException($"GGUF metadata array '{key}' contains a string exceeding the captured-text limit.");
                        var bytes = new byte[(int)length]; ReadExactly(bytes); strings.Add(StrictUtf8.GetString(bytes));
                    }
                    else Skip(length);
                }
            }
            else
            {
                var size = elementType switch
                {
                    GgufValueType.UInt8 or GgufValueType.Int8 or GgufValueType.Bool => 1UL,
                    GgufValueType.UInt16 or GgufValueType.Int16 => 2UL,
                    GgufValueType.UInt32 or GgufValueType.Int32 or GgufValueType.Float32 => 4UL,
                    GgufValueType.UInt64 or GgufValueType.Int64 or GgufValueType.Float64 => 8UL,
                    _ => throw new InvalidDataException($"GGUF metadata array '{key}' has unsupported element type {(uint)elementType}.")
                };
                Skip(checked(count * size));
            }
            return new(null, null, strings, count > 0, elementType);
        }

        private static ParsedValue Signed(long value)
        {
            if (value < 0) return new(null, null, null, true);
            return new(null, (ulong)value, null, true);
        }

        private byte ReadByte() { Span<byte> bytes = stackalloc byte[1]; ReadExactly(bytes); return bytes[0]; }
        private sbyte ReadSByte() => unchecked((sbyte)ReadByte());
        private ushort ReadUInt16() { Span<byte> bytes = stackalloc byte[2]; ReadExactly(bytes); return BinaryPrimitives.ReadUInt16LittleEndian(bytes); }
        private short ReadInt16() { Span<byte> bytes = stackalloc byte[2]; ReadExactly(bytes); return BinaryPrimitives.ReadInt16LittleEndian(bytes); }
        private int ReadInt32() { Span<byte> bytes = stackalloc byte[4]; ReadExactly(bytes); return BinaryPrimitives.ReadInt32LittleEndian(bytes); }
        private long ReadInt64() { Span<byte> bytes = stackalloc byte[8]; ReadExactly(bytes); return BinaryPrimitives.ReadInt64LittleEndian(bytes); }

        private void Skip(ulong count)
        {
            if (count > long.MaxValue) throw new InvalidDataException("GGUF metadata value is too large to inspect.");
            EnsureBudget((long)count);
            var target = checked(stream.Position + (long)count);
            if (!stream.CanSeek) throw new InvalidDataException("GGUF metadata inspection requires a seekable file.");
            if (target > stream.Length) throw new EndOfStreamException();
            stream.Position = target; consumed += (long)count;
        }

        private void EnsureBudget(long count)
        {
            if (count < 0 || count > MaximumMetadataBytes - consumed)
                throw new InvalidDataException($"GGUF metadata exceeds the {MaximumMetadataBytes / 1048576:N0} MiB inspection limit.");
        }
    }
}
