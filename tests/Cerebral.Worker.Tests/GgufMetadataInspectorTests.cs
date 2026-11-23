using System.Text;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class GgufMetadataInspectorTests
{
    private const uint UInt8 = 0;
    private const uint Int8 = 1;
    private const uint UInt32 = 4;
    private const uint Float32 = 6;
    private const uint String = 8;
    private const uint Array = 9;
    private const uint UInt64 = 10;

    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-gguf-inspector-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }

        void Rejects(string path, string messageFragment, string label)
        {
            try
            {
                GgufMetadataInspector.Inspect(path);
                throw new Exception("FAIL: " + label + " (inspection unexpectedly succeeded)");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase))
            {
                checks++;
                Console.WriteLine("PASS: " + label);
            }
        }

        try
        {
            var richPath = Path.Combine(root, "Acme-Coder-9B-Q4_K_M.gguf");
            var metadataEnd = WriteGguf(richPath, version: 3, tensorCount: 27,
            [
                Text("general.name", "Acme Coder 9B Instruct"),
                Text("general.architecture", "llama"),
                Number64("general.parameter_count", 9_100_000_000),
                Text("general.size_label", "9.1B"),
                Number32("general.file_type", 15),
                Text("general.type", "model"),
                Text("general.description", "A focused coding assistant."),
                StringArray("general.tags", ["code", "assistant"]),
                Number32("llama.context_length", 32_768),
                Number32("llama.block_count", 48),
                Number32("llama.embedding_length", 4_096),
                Number32("llama.attention.head_count", 32),
                Number32("llama.attention.head_count_kv", 8),
                Text("tokenizer.chat_template", "{% for message in messages %}{{ message.content }}{% endfor %}"),
                StringArray("tokenizer.ggml.tokens", ["one", "two", "three"]),
                FloatArray("tokenizer.ggml.scores", [1f, 0.5f, -1f])
            ]);

            var rich = GgufMetadataInspector.Inspect(richPath);
            Check(rich.Version == 3 && rich.TensorCount == 27 && rich.MetadataEntryCount == 16,
                "v3 header counts are read from the bounded metadata section");
            Check(rich.MetadataEndOffset == metadataEnd && metadataEnd < new FileInfo(richPath).Length,
                "inspection stops at the end of metadata before tensor descriptors or body bytes");
            Check(rich.Name == "Acme Coder 9B Instruct" && rich.Architecture == "llama" && rich.DeclaredType == "model" &&
                rich.ParameterCount == 9_100_000_000 && rich.SizeLabel == "9.1B",
                "identity, architecture, declared type, parameter count, and size label come from GGUF metadata");
            Check(rich.FileType == 15 && rich.Quantization == "Q4_K_M",
                "GGUF file type maps to its quantization label");
            Check(rich.NativeContextTokens == 32_768 && rich.BlockCount == 48 && rich.EmbeddingLength == 4_096 &&
                rich.HeadCount == 32 && rich.KvHeadCount == 8,
                "architecture-scoped dimensions are selected without scanning tensor data");
            Check(rich.HasEmbeddedChatTemplate && rich.Purpose == "Coding" && rich.PurposeConfidence == "high",
                "embedded template and metadata purpose are reported with source confidence");
            Check(rich.SourceNotes.Any(x => x.Contains("general.name", StringComparison.Ordinal)) &&
                rich.SourceNotes.Any(x => x.Contains("general.architecture", StringComparison.Ordinal)) &&
                rich.SourceNotes.Any(x => x.Contains("Purpose", StringComparison.Ordinal)),
                "source notes distinguish directly read and inferred values");
            Check(!rich.UsesFilenameEstimate && !rich.UsesMetadataEstimate,
                "fully declared GGUF values are not mislabeled as estimates");

            var uniformPath = Path.Combine(root, "uniform-layer-kv.gguf");
            WriteGguf(uniformPath, version: 3, tensorCount: 0,
            [
                Text("general.architecture", "gemma4"),
                Number32("gemma4.block_count", 48),
                Number32("gemma4.embedding_length", 2_560),
                Number32("gemma4.attention.head_count", 8),
                IntegerArray("gemma4.attention.head_count_kv", Enumerable.Repeat(8U, 48).ToArray())
            ]);
            var uniform = GgufMetadataInspector.Inspect(uniformPath);
            Check(uniform.KvHeadCount == 8 && uniform.KvHeadCountsByLayer is { Count: 48 } &&
                uniform.KvHeadCountsByLayer.All(x => x == 8),
                "uniform integer layer counts retain all values and admit their exact scalar equivalent");

            var scalarZeroPath = Path.Combine(root, "scalar-zero-kv.gguf");
            WriteGguf(scalarZeroPath, version: 3, tensorCount: 0,
            [Number32("llama.block_count", 2), Number32("llama.attention.head_count_kv", 0)]);
            Check(GgufMetadataInspector.Inspect(scalarZeroPath).KvHeadCount == 0,
                "existing scalar zero remains admitted and cannot establish a usable KV cache shape");

            var nonuniformPath = Path.Combine(root, "nonuniform-layer-kv.gguf");
            var mixed = Enumerable.Range(0, 48).Select(i => i % 6 == 5 ? 1U : 8U).ToArray();
            WriteGguf(nonuniformPath, version: 3, tensorCount: 0,
            [
                Text("general.architecture", "gemma4"),
                Number32("gemma4.block_count", 48),
                Number32("gemma4.embedding_length", 3_840),
                Number32("gemma4.attention.head_count", 16),
                IntegerArray("gemma4.attention.head_count_kv", mixed),
                Number32("gemma4.attention.key_length", 512),
                Number32("gemma4.attention.value_length", 512)
            ]);
            var nonuniform = GgufMetadataInspector.Inspect(nonuniformPath);
            Check(nonuniform.KvHeadCount is null && nonuniform.KvHeadCountsByLayer is { Count: 48 } &&
                nonuniform.KvHeadCountsByLayer.SequenceEqual(mixed.Select(x => (int)x)) &&
                nonuniform.KvShapeWarning?.Contains("vary", StringComparison.Ordinal) == true,
                "nonuniform layer values stay exact without a fabricated scalar");
            var shapeRecord = ModelMetadataDefaults.Apply(new ModelRecord
            {
                SizeBytes = 1L << 30, ContextTokens = 4_096, MaximumContextTokens = 16_384,
                ParallelRequests = 1, AutomaticContextSizing = true, GpuLayers = 0
            }, nonuniform,
                preserveConfiguredContext: false, preserveDisplayName: false);
            Check(shapeRecord.AttentionHeadCountKv is null && ModelCapacity.Estimate(shapeRecord).KvCacheMb is null &&
                shapeRecord.MetadataWarning?.Contains("vary", StringComparison.Ordinal) == true,
                "nonuniform shape disables generic KV sizing and carries an actionable warning");
            var shapePlan = ModelCapacity.Plan(shapeRecord, new ResourceInfo(32_768, 32_768, []), 1_024);
            Check(shapePlan.ContextTokens == shapeRecord.ContextTokens && shapePlan.SelectionSource == "retained_fallback",
                "unknown per-layer shape cannot enlarge automatic context from a generic estimate");

            var asymmetricPath = Path.Combine(root, "explicit-width-mismatch.gguf");
            WriteGguf(asymmetricPath, version: 3, tensorCount: 0,
            [
                Text("general.architecture", "gemma4"),
                Number32("gemma4.block_count", 42),
                Number32("gemma4.embedding_length", 2_560),
                Number32("gemma4.attention.head_count", 8),
                Number32("gemma4.attention.head_count_kv", 2),
                Number32("gemma4.attention.key_length", 512),
                Number32("gemma4.attention.value_length", 512)
            ]);
            var asymmetric = GgufMetadataInspector.Inspect(asymmetricPath);
            Check(asymmetric.KvHeadCount is null &&
                asymmetric.KvShapeWarning?.Contains("widths differ", StringComparison.Ordinal) == true,
                "explicit 512-wide key and value dimensions do not authorize a 320-wide generic cache estimate");

            var wrongLengthPath = Path.Combine(root, "wrong-layer-count.gguf");
            WriteGguf(wrongLengthPath, version: 3, tensorCount: 0,
            [Number32("gemma4.block_count", 6), IntegerArray("gemma4.attention.head_count_kv", [8, 8, 1])]);
            Rejects(wrongLengthPath, "one positive integer per declared block", "layer array length must match block count");
            var floatLayerPath = Path.Combine(root, "float-layer-count.gguf");
            WriteGguf(floatLayerPath, version: 3, tensorCount: 0,
            [Number32("gemma4.block_count", 2), FloatArray("gemma4.attention.head_count_kv", [8f, 1f])]);
            Rejects(floatLayerPath, "bounded nonempty list of integer layer counts", "layer array rejects floating-point elements");
            var unsupportedArrayPath = Path.Combine(root, "unsupported-architecture-layer-kv.gguf");
            WriteGguf(unsupportedArrayPath, version: 3, tensorCount: 0,
            [Number32("llama.block_count", 2), IntegerArray("llama.attention.head_count_kv", [8, 8])]);
            Rejects(unsupportedArrayPath, "unsupported per-layer KV-head array shape",
                "per-layer arrays are admitted only for the observed Gemma 4 metadata key");
            var negativeLayerPath = Path.Combine(root, "negative-layer-count.gguf");
            WriteGguf(negativeLayerPath, version: 3, tensorCount: 0,
            [Number32("gemma4.block_count", 2), new Entry("gemma4.attention.head_count_kv", Array, writer =>
            {
                writer.Write(Int8); writer.Write(2UL); writer.Write((sbyte)8); writer.Write((sbyte)-1);
            })]);
            Rejects(negativeLayerPath, "non-positive or unsupported layer count", "layer array rejects negative values");
            var overboundLayerPath = Path.Combine(root, "overbound-layer-count.gguf");
            WriteGguf(overboundLayerPath, version: 3, tensorCount: 0,
            [Number32("gemma4.block_count", 4_097), new Entry("gemma4.attention.head_count_kv", Array, writer =>
            {
                writer.Write(UInt32); writer.Write(4_097UL);
            })]);
            Rejects(overboundLayerPath, "bounded nonempty list of integer layer counts", "layer array capture has a fixed bound");
            using (var cancelledLayer = new CancellationTokenSource())
            {
                cancelledLayer.Cancel();
                try
                {
                    GgufMetadataInspector.Inspect(nonuniformPath, cancelledLayer.Token);
                    throw new Exception("FAIL: canceled layer inspection unexpectedly succeeded");
                }
                catch (OperationCanceledException) { Check(true, "layer-array inspection observes cancellation"); }
            }

            var fallbackPath = Path.Combine(root, "Acme-Math-7B-Q4_K_M.gguf");
            WriteGguf(fallbackPath, version: 2, tensorCount: 0,
            [
                Text("general.architecture", "llama"),
                Number32("llama.context_length", 8_192)
            ]);
            var fallback = GgufMetadataInspector.Inspect(fallbackPath);
            Check(fallback.Version == 2 && fallback.Name == "Acme-Math-7B-Q4_K_M" &&
                fallback.ParameterCount == 7_000_000_000 && fallback.SizeLabel == "7B" && fallback.Quantization == "Q4_K_M",
                "v2 single-file GGUF inspection uses bounded filename fallbacks for missing identity fields");
            Check(fallback.Purpose == "Mathematics" && fallback.PurposeConfidence == "medium" &&
                fallback.SourceNotes.Any(x => x.Contains("filename", StringComparison.OrdinalIgnoreCase)),
                "filename-only purpose is marked below metadata-derived confidence");
            Check(fallback.UsesFilenameEstimate && !fallback.UsesMetadataEstimate,
                "filename fallbacks have explicit structured provenance");

            var metadataEstimatePath = Path.Combine(root, "neutral-metadata-estimate.gguf");
            WriteGguf(metadataEstimatePath, version: 3, tensorCount: 0,
            [
                Text("general.name", "Plain Foundation Model"),
                Text("general.architecture", "llama"),
                Text("general.type", "model"),
                Text("general.size_label", "6.5B")
            ]);
            var metadataEstimate = GgufMetadataInspector.Inspect(metadataEstimatePath);
            Check(metadataEstimate.ParameterCount == 6_500_000_000 && metadataEstimate.UsesMetadataEstimate && !metadataEstimate.UsesFilenameEstimate,
                "size-label estimates are distinguished from filename estimates");

            var adapterPath = Path.Combine(root, "Roleplay-Coder-Adapter.gguf");
            WriteGguf(adapterPath, version: 3, tensorCount: 0,
            [
                Text("general.name", "Story Writer Helper"),
                Text("general.architecture", "llama"),
                Text("general.type", "ADAPTER"),
                Text("general.description", "A coding and creative-writing helper")
            ]);
            var adapter = GgufMetadataInspector.Inspect(adapterPath);
            Check(adapter.DeclaredType == "adapter" && adapter.Purpose == "Adapter" && adapter.PurposeConfidence == "high",
                "authoritative non-text general.type overrides writing and coding heuristics");
            var discoveredAdapter = new ModelRepository(root).ScanAsync([], CancellationToken.None).GetAwaiter().GetResult().Models.Single(x => x.Path == adapterPath);
            Check(discoveredAdapter.DeclaredType == "adapter" && discoveredAdapter.Purpose == "Adapter",
                "repository discovery preserves declared type without applying roleplay filename heuristics");

            var unsupported = Path.Combine(root, "unsupported.gguf");
            WriteHeader(unsupported, version: 4, tensorCount: 0, metadataCount: 0);
            Rejects(unsupported, "Unsupported GGUF version", "unsupported GGUF versions fail clearly");

            var bigEndian = Path.Combine(root, "big-endian.gguf");
            using (var output = File.Create(bigEndian))
            {
                output.Write("GGUF"u8);
                output.Write([0, 0, 0, 3]);
            }
            Rejects(bigEndian, "Big-endian", "big-endian GGUF metadata fails with a specific diagnostic");

            var badMagic = Path.Combine(root, "bad-magic.gguf");
            File.WriteAllBytes(badMagic, "NOPE"u8.ToArray());
            Rejects(badMagic, "missing GGUF signature", "non-GGUF files are rejected before metadata parsing");

            var excessiveKeys = Path.Combine(root, "excessive-keys.gguf");
            WriteHeader(excessiveKeys, version: 3, tensorCount: 0, metadataCount: GgufMetadataInspector.MaximumMetadataEntries + 1);
            Rejects(excessiveKeys, "too many metadata entries", "declared metadata entry count is bounded");

            var truncated = Path.Combine(root, "truncated.gguf");
            using (var stream = File.Create(truncated))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                WriteHeader(writer, version: 3, tensorCount: 0, metadataCount: 1);
                WriteString(writer, "general.name");
                writer.Write(String);
                writer.Write(12UL);
                writer.Write((byte)'x');
            }
            Rejects(truncated, "ended unexpectedly", "truncated declared strings fail clearly");

            var unknownType = Path.Combine(root, "unknown-type.gguf");
            using (var stream = File.Create(unknownType))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                WriteHeader(writer, version: 3, tensorCount: 0, metadataCount: 1);
                WriteString(writer, "general.name");
                writer.Write(99U);
            }
            Rejects(unknownType, "unsupported value type 99", "unknown metadata value types fail at their key");

            var nestedArray = Path.Combine(root, "nested-array.gguf");
            using (var stream = File.Create(nestedArray))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                WriteHeader(writer, version: 3, tensorCount: 0, metadataCount: 1);
                WriteString(writer, "unused.array");
                writer.Write(Array);
                writer.Write(Array);
                writer.Write(0UL);
            }
            Rejects(nestedArray, "cannot contain nested arrays", "nested GGUF arrays are rejected");

            var excessiveArray = Path.Combine(root, "excessive-array.gguf");
            using (var stream = File.Create(excessiveArray))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                WriteHeader(writer, version: 3, tensorCount: 0, metadataCount: 1);
                WriteString(writer, "unused.array");
                writer.Write(Array);
                writer.Write(UInt8);
                writer.Write(GgufMetadataInspector.MaximumArrayElements + 1);
            }
            Rejects(excessiveArray, "too many elements", "declared metadata array count is bounded");

            var splitPath = Path.Combine(root, "model-00001-of-00002.gguf");
            WriteHeader(splitPath, version: 3, tensorCount: 0, metadataCount: 0);
            Rejects(splitPath, "Split GGUF files", "split GGUF weights fail with an actionable single-file diagnostic");

            var renamedSplitPath = Path.Combine(root, "ordinary-model-name.gguf");
            WriteGguf(renamedSplitPath, version: 3, tensorCount: 0,
            [
                Text("general.name", "Renamed shard"),
                Number32("split.count", 2)
            ]);
            Rejects(renamedSplitPath, "metadata declares 2 shards", "split metadata rejects a renamed shard that evades filename detection");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var observed = false;
                try { GgufMetadataInspector.Inspect(richPath, cancelled.Token); }
                catch (OperationCanceledException) { observed = true; }
                Check(observed, "metadata inspection observes cancellation before parsing entries");
            }

            Console.WriteLine($"GGUF metadata inspector: {checks} checks passed (synthetic header/KV fixtures; no tensor descriptors, model load, inference, or network).");
        }
        finally
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Entry(string Key, uint Type, Action<BinaryWriter> WriteValue);

    private static Entry Text(string key, string value) => new(key, String, writer => WriteString(writer, value));
    private static Entry Number32(string key, uint value) => new(key, UInt32, writer => writer.Write(value));
    private static Entry Number64(string key, ulong value) => new(key, UInt64, writer => writer.Write(value));
    private static Entry IntegerArray(string key, IReadOnlyList<uint> values) => new(key, Array, writer =>
    {
        writer.Write(UInt32);
        writer.Write((ulong)values.Count);
        foreach (var value in values) writer.Write(value);
    });
    private static Entry StringArray(string key, IReadOnlyList<string> values) => new(key, Array, writer =>
    {
        writer.Write(String);
        writer.Write((ulong)values.Count);
        foreach (var value in values) WriteString(writer, value);
    });
    private static Entry FloatArray(string key, IReadOnlyList<float> values) => new(key, Array, writer =>
    {
        writer.Write(Float32);
        writer.Write((ulong)values.Count);
        foreach (var value in values) writer.Write(value);
    });

    private static long WriteGguf(string path, uint version, ulong tensorCount, IReadOnlyList<Entry> entries)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteHeader(writer, version, tensorCount, (ulong)entries.Count);
        foreach (var entry in entries)
        {
            WriteString(writer, entry.Key);
            writer.Write(entry.Type);
            entry.WriteValue(writer);
        }
        writer.Flush();
        var metadataEnd = stream.Position;
        writer.Write([0xBA, 0xAD, 0xF0, 0x0D]);
        writer.Flush();
        return metadataEnd;
    }

    private static void WriteHeader(string path, uint version, ulong tensorCount, ulong metadataCount)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        WriteHeader(writer, version, tensorCount, metadataCount);
    }

    private static void WriteHeader(BinaryWriter writer, uint version, ulong tensorCount, ulong metadataCount)
    {
        writer.Write("GGUF"u8.ToArray());
        writer.Write(version);
        writer.Write(tensorCount);
        writer.Write(metadataCount);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }
}
