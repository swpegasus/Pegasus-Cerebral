using System.Buffers.Binary;
using System.Text.Json;

namespace Cerebral.Worker.ModelOperations;

/// <summary>Closed, embedded PBR GLB and image profiles, independent of the NC vertex-color subset.</summary>
public static class ModelOperationArtifactValidation
{
    private const int MaximumTexturePixels = 4096 * 4096;
    private const int MaximumTextureCount = 8;
    private const int MaximumJsonBytes = 4 * 1024 * 1024;

    public static void Validate(string path, string mediaType, string profile, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ModelOperationArtifactStore.Ordinary(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { Validate(stream, mediaType, profile, token); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { throw new InvalidDataException("Artifact structure does not conform to its admitted media profile.", error); }
    }

    private static void Validate(Stream stream, string mediaType, string profile, CancellationToken token)
    {
        if (profile == "cerebral.gltf.pbr/1" && mediaType == "model/gltf-binary") { ValidateGlb(stream, token); return; }
        if (profile == "cerebral.image.preview/1" && mediaType.StartsWith("image/", StringComparison.Ordinal))
        { Dimensions(stream, 0, stream.Length, mediaType, token); return; }
        if (profile == "cerebral.model.provenance/1" && mediaType == "application/json")
        {
            if (stream.Length > MaximumJsonBytes) throw Invalid("Provenance exceeds the JSON budget.");
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            Json(document.RootElement); return;
        }
        throw Invalid("Artifact media type does not match its admitted profile.");
    }

    private static void ValidateGlb(Stream stream, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[12]; stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x46546c67 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != stream.Length)
            throw Invalid("Invalid glTF binary header or exact length.");
        Span<byte> chunk = stackalloc byte[8]; stream.ReadExactly(chunk);
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        if (BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 0x4e4f534a || jsonLength is < 2 or > MaximumJsonBytes ||
            jsonLength % 4 != 0 || jsonLength > stream.Length - stream.Position)
            throw Invalid("Invalid bounded GLB JSON chunk.");
        var bytes = new byte[jsonLength]; stream.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement; Json(root);
        if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0") throw Invalid("Only glTF2 is supported.");
        foreach (var name in new[] { "animations", "skins", "cameras" })
            if (root.TryGetProperty(name, out var unsupported) && unsupported.GetArrayLength() != 0)
                throw Invalid("Animated/skinned/camera content is outside the generated mesh profile.");
        foreach (var name in new[] { "extensionsUsed", "extensionsRequired" })
            if (root.TryGetProperty(name, out var extensions))
                foreach (var extension in extensions.EnumerateArray())
                    if (extension.GetString() != "EXT_texture_webp") throw Invalid("Unsupported glTF extension.");
        var buffers = Array(root, "buffers", 1, 1);
        if (buffers[0].TryGetProperty("uri", out _)) throw Invalid("External or data buffer URIs are not admitted.");
        stream.ReadExactly(chunk);
        var binaryLength = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        if (BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 0x004e4942 || binaryLength % 4 != 0 ||
            binaryLength != stream.Length - stream.Position)
            throw Invalid("GLB must contain exactly one embedded binary chunk.");
        var binaryStart = stream.Position;
        var declaredLength = buffers[0].GetProperty("byteLength").GetInt64();
        if (declaredLength <= 0 || declaredLength > binaryLength || binaryLength - declaredLength > 3)
            throw Invalid("Embedded buffer length differs.");
        var views = Array(root, "bufferViews", 1, 100_000);
        var ranges = new (long Offset, long Length, int Stride)[views.GetArrayLength()];
        for (var i = 0; i < ranges.Length; i++)
        {
            var view = views[i];
            var offset = Number(view, "byteOffset", 0); var length = view.GetProperty("byteLength").GetInt64();
            var stride = checked((int)Number(view, "byteStride", 0));
            if (Number(view, "buffer", 0) != 0 || offset < 0 || length <= 0 || offset > declaredLength - length ||
                stride != 0 && (stride < 4 || stride > 252 || stride % 4 != 0)) throw Invalid("Invalid embedded buffer view.");
            ranges[i] = (offset, length, stride);
        }
        var accessors = Array(root, "accessors", 1, 100_000);
        var layouts = new (long Offset, int Count, int Width, int Stride, int Component, string Type, bool Normalized, bool Interleaved)[accessors.GetArrayLength()];
        for (var i = 0; i < layouts.Length; i++)
        {
            var accessor = accessors[i];
            if (accessor.TryGetProperty("sparse", out _)) throw Invalid("Sparse accessor decoding is not admitted.");
            var view = ranges[Index(accessor, "bufferView", ranges.Length)];
            var offset = Number(accessor, "byteOffset", 0); var count = accessor.GetProperty("count").GetInt32();
            var component = accessor.GetProperty("componentType").GetInt32();
            var componentBytes = component switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw Invalid("Invalid accessor component.") };
            var type = accessor.GetProperty("type").GetString()!;
            var normalized = accessor.TryGetProperty("normalized", out var normalization) && normalization.GetBoolean();
            if (normalized && component is not (5120 or 5121 or 5122 or 5123)) throw Invalid("Only byte/short accessors can be normalized.");
            var components = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => throw Invalid("Unsupported accessor shape.") };
            var width = components * componentBytes; var stride = view.Stride == 0 ? width : view.Stride;
            if (count is < 1 or > 12_000_000 || offset < 0 || offset % componentBytes != 0 || (view.Offset + offset) % componentBytes != 0 || stride < width ||
                checked(offset + (long)(count - 1) * stride + width) > view.Length)
                throw Invalid("Accessor exceeds its embedded view or decode budget.");
            layouts[i] = (view.Offset + offset, count, width, stride, component, type, normalized, view.Stride != 0);
        }
        var materials = OptionalArray(root, "materials", 1024);
        var meshes = Array(root, "meshes", 1, 1024); long triangles = 0, vertices = 0;
        var meshInstances = ValidateScene(root, meshes.GetArrayLength());
        Span<byte> value = stackalloc byte[16];
        for (var meshIndex = 0; meshIndex < meshes.GetArrayLength(); meshIndex++)
        foreach (var primitive in Array(meshes[meshIndex], "primitives", 1, 1024).EnumerateArray())
        {
            var mesh = meshes[meshIndex];
            token.ThrowIfCancellationRequested();
            if (mesh.TryGetProperty("weights", out _)) throw Invalid("Morph weights are outside the static mesh profile.");
            if (Number(primitive, "mode", 4) != 4) throw Invalid("Only triangle meshes are supported.");
            var attributes = primitive.GetProperty("attributes");
            var position = layouts[Index(attributes, "POSITION", layouts.Length)];
            if (position.Type != "VEC3" || position.Component != 5126 || position.Normalized || position.Count > 4_000_000)
                throw Invalid("POSITION must contain bounded float3 vertices.");
            vertices = checked(vertices + (long)position.Count * meshInstances[meshIndex]);
            if (vertices > 4_000_000) throw Invalid("Mesh exceeds the vertex budget.");
            for (var v = 0; v < position.Count; v++)
            {
                if ((v & 4095) == 0) token.ThrowIfCancellationRequested();
                stream.Position = binaryStart + position.Offset + (long)v * position.Stride;
                stream.ReadExactly(value[..12]);
                for (var c = 0; c < 3; c++)
                    if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(value.Slice(c * 4, 4)))) throw Invalid("Nonfinite geometry.");
            }
            foreach (var attribute in attributes.EnumerateObject())
            {
                if (attribute.Name is not ("POSITION" or "NORMAL" or "TANGENT" or "TEXCOORD_0" or "COLOR_0"))
                    throw Invalid("Unsupported mesh attribute.");
                var index = attribute.Value.GetInt32();
                if (index < 0 || index >= layouts.Length || layouts[index].Count != position.Count) throw Invalid("Attribute vertex counts differ.");
                var layout = layouts[index];
                if (layout.Offset % 4 != 0 || layout.Stride % 4 != 0)
                    throw Invalid("Core vertex attributes require four-byte aligned starts and rows.");
                var expected = attribute.Name switch { "POSITION" or "NORMAL" => "VEC3", "TANGENT" => "VEC4", "TEXCOORD_0" => "VEC2", _ => layout.Type };
                if (layout.Type != expected || attribute.Name == "COLOR_0" && layout.Type is not ("VEC3" or "VEC4"))
                    throw Invalid("Invalid mesh attribute shape.");
                var coreFloat = layout.Component == 5126 && !layout.Normalized;
                var normalizedUnsigned = layout.Component is 5121 or 5123 && layout.Normalized;
                if (attribute.Name is "POSITION" or "NORMAL" or "TANGENT" ? !coreFloat : (!coreFloat && !normalizedUnsigned))
                    throw Invalid("Attribute component/normalization is not supported by core static glTF.");
                if (layout.Component == 5126)
                {
                    for (var n = 0; n < layout.Count; n++)
                    {
                        if ((n & 4095) == 0) token.ThrowIfCancellationRequested();
                        stream.Position = binaryStart + layout.Offset + (long)n * layout.Stride;
                        stream.ReadExactly(value[..layout.Width]);
                        for (var component = 0; component < layout.Width; component += 4)
                        {
                            var number = BinaryPrimitives.ReadSingleLittleEndian(value[component..]);
                            if (!float.IsFinite(number) || (attribute.Name == "COLOR_0" && number is < 0 or > 1))
                                throw Invalid("Nonfinite or invalid vertex-color attribute.");
                        }
                    }
                }
            }
            if (primitive.TryGetProperty("material", out _))
            {
                var material = materials[Index(primitive, "material", materials.GetArrayLength())];
                if (UsesTexture(material) && !attributes.TryGetProperty("TEXCOORD_0", out _))
                    throw Invalid("Textured primitives require TEXCOORD_0.");
            }
            if (primitive.TryGetProperty("targets", out _)) throw Invalid("Morph targets are outside the generated mesh profile.");
            var count = position.Count;
            if (primitive.TryGetProperty("indices", out var indices))
            {
                var index = indices.GetInt32();
                if (index < 0 || index >= layouts.Length) throw Invalid("Invalid index accessor.");
                var layout = layouts[index]; count = layout.Count;
                if (layout.Type != "SCALAR" || layout.Component is not (5121 or 5123 or 5125) || layout.Normalized || layout.Interleaved)
                    throw Invalid("Invalid mesh index component, normalization or stride.");
                for (var n = 0; n < count; n++)
                {
                    if ((n & 4095) == 0) token.ThrowIfCancellationRequested();
                    stream.Position = binaryStart + layout.Offset + (long)n * layout.Stride;
                    stream.ReadExactly(value[..layout.Width]);
                    var vertex = layout.Component == 5121 ? value[0] : layout.Component == 5123 ?
                        BinaryPrimitives.ReadUInt16LittleEndian(value) : BinaryPrimitives.ReadUInt32LittleEndian(value);
                    if (vertex >= position.Count) throw Invalid("Mesh index is outside the vertex array.");
                }
            }
            if (count % 3 != 0) throw Invalid("Triangle index count is not divisible by three.");
            triangles = checked(triangles + (long)(count / 3) * meshInstances[meshIndex]);
            if (triangles > 4_000_000) throw Invalid("Mesh exceeds triangle budget.");
        }
        var images = OptionalArray(root, "images", MaximumTextureCount);
        long decodedPixels = 0;
        foreach (var image in images.EnumerateArray())
        {
            if (image.TryGetProperty("uri", out _)) throw Invalid("Texture images must be embedded.");
            var view = ranges[Index(image, "bufferView", ranges.Length)];
            var size = Dimensions(stream, binaryStart + view.Offset, view.Length, image.GetProperty("mimeType").GetString()!, token);
            decodedPixels += (long)size.Width * size.Height;
            if (decodedPixels > (long)MaximumTexturePixels * MaximumTextureCount) throw Invalid("Texture decode budget exceeded.");
        }
        var textures = OptionalArray(root, "textures", MaximumTextureCount);
        var samplers = OptionalArray(root, "samplers", 64);
        foreach (var sampler in samplers.EnumerateArray())
        {
            if (sampler.TryGetProperty("magFilter", out _) && Number(sampler, "magFilter", 0) is not (9728 or 9729)) throw Invalid("Invalid texture magnification filter.");
            if (sampler.TryGetProperty("minFilter", out _) && Number(sampler, "minFilter", 0) is not (9728 or 9729 or 9984 or 9985 or 9986 or 9987)) throw Invalid("Invalid texture minification filter.");
            foreach (var field in new[] { "wrapS", "wrapT" })
                if (sampler.TryGetProperty(field, out _) && Number(sampler, field, 0) is not (33071 or 33648 or 10497)) throw Invalid("Invalid texture wrapping mode.");
        }
        foreach (var texture in textures.EnumerateArray())
        {
            if (texture.TryGetProperty("sampler", out _)) Index(texture, "sampler", samplers.GetArrayLength());
            var source = texture.TryGetProperty("source", out _);
            if (source) Index(texture, "source", images.GetArrayLength());
            var extension = texture.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("EXT_texture_webp", out _);
            if (extension) Index(ext.GetProperty("EXT_texture_webp"), "source", images.GetArrayLength());
            if (!source && !extension) throw Invalid("Texture has no admitted embedded image.");
        }
        foreach (var material in materials.EnumerateArray())
        {
            if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                Factor(pbr, "metallicFactor"); Factor(pbr, "roughnessFactor"); Factors(pbr, "baseColorFactor", 4);
                foreach (var field in new[] { "baseColorTexture", "metallicRoughnessTexture" })
                    if (pbr.TryGetProperty(field, out var texture)) TextureInfo(texture, textures.GetArrayLength());
            }
            Factors(material, "emissiveFactor", 3);
            if (material.TryGetProperty("alphaMode", out var alpha) && alpha.GetString() is not ("OPAQUE" or "MASK" or "BLEND")) throw Invalid("Invalid material alpha mode.");
            if (material.TryGetProperty("alphaCutoff", out _)) Factor(material, "alphaCutoff");
            foreach (var field in new[] { "normalTexture", "occlusionTexture", "emissiveTexture" })
                if (material.TryGetProperty(field, out var texture)) TextureInfo(texture, textures.GetArrayLength());
        }
        if (triangles == 0) throw Invalid("Generated mesh is empty.");
    }

    private static int[] ValidateScene(JsonElement root, int meshCount)
    {
        var nodes = Array(root, "nodes", 1, 16_384);
        var scenes = Array(root, "scenes", 1, 256);
        var selected = Number(root, "scene", 0);
        if (selected < 0 || selected >= scenes.GetArrayLength()) throw Invalid("Default scene is outside its array.");
        var visited = new HashSet<int>(); var meshInstances = new int[meshCount];
        void Visit(int index, int depth)
        {
            if (depth > 128 || index < 0 || index >= nodes.GetArrayLength() || !visited.Add(index))
                throw Invalid("Scene has invalid indices, a cycle, repeated parentage or excessive depth.");
            var node = nodes[index];
            if (node.ValueKind != JsonValueKind.Object || new[] { "skin", "weights", "camera" }.Any(name => node.TryGetProperty(name, out _)))
                throw Invalid("Scene nodes must be ordinary static mesh/group nodes.");
            if (node.TryGetProperty("matrix", out _) && new[] { "translation", "rotation", "scale" }.Any(name => node.TryGetProperty(name, out _)))
                throw Invalid("Node matrix and TRS transforms cannot be combined.");
            Transform(node, "matrix", 16); Transform(node, "translation", 3); Transform(node, "rotation", 4); Transform(node, "scale", 3);
            if (node.TryGetProperty("mesh", out _)) meshInstances[Index(node, "mesh", meshCount)]++;
            foreach (var child in OptionalArray(node, "children", 16_384).EnumerateArray()) Visit(child.GetInt32(), depth + 1);
        }
        foreach (var node in Array(scenes[(int)selected], "nodes", 1, 16_384).EnumerateArray()) Visit(node.GetInt32(), 0);
        if (visited.Count != nodes.GetArrayLength() || meshInstances.Any(count => count == 0))
            throw Invalid("Every static node and mesh must be reachable from the default scene.");
        // Other scenes cannot smuggle malformed references into a parser after default-scene admission.
        foreach (var scene in scenes.EnumerateArray())
            foreach (var node in OptionalArray(scene, "nodes", 16_384).EnumerateArray())
                if (node.GetInt32() < 0 || node.GetInt32() >= nodes.GetArrayLength()) throw Invalid("Scene root index is invalid.");
        return meshInstances;
    }

    private static void Transform(JsonElement node, string name, int count)
    {
        if (!node.TryGetProperty(name, out var array)) return;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != count) throw Invalid("Invalid static transform shape.");
        var values = array.EnumerateArray().Select(value => value.GetDouble()).ToArray();
        if (values.Any(value => !double.IsFinite(value) || Math.Abs(value) > float.MaxValue)) throw Invalid("Nonfinite or non-float transform.");
        if (name == "rotation" && Math.Abs(values.Sum(value => value * value) - 1) > 0.0001) throw Invalid("Rotation must be a unit quaternion.");
        if (name == "matrix" && (values[3] != 0 || values[7] != 0 || values[11] != 0 || values[15] != 1))
            throw Invalid("Static node matrix must be affine.");
    }

    private static bool UsesTexture(JsonElement material)
    {
        if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) &&
            (pbr.TryGetProperty("baseColorTexture", out _) || pbr.TryGetProperty("metallicRoughnessTexture", out _))) return true;
        return new[] { "normalTexture", "occlusionTexture", "emissiveTexture" }.Any(name => material.TryGetProperty(name, out _));
    }
    private static void TextureInfo(JsonElement texture, int count)
    { Index(texture, "index", count); if (Number(texture, "texCoord", 0) != 0) throw Invalid("Only TEXCOORD_0 texture coordinates are admitted."); }
    private static void Factor(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var factor)) return;
        var number = factor.GetDouble();
        if (!double.IsFinite(number) || number is < 0 or > 1) throw Invalid("PBR factor is outside its range.");
    }
    private static void Factors(JsonElement value, string name, int count)
    {
        if (!value.TryGetProperty(name, out var factors)) return;
        if (factors.ValueKind != JsonValueKind.Array || factors.GetArrayLength() != count) throw Invalid("Invalid PBR factor shape.");
        foreach (var factor in factors.EnumerateArray())
        { var number = factor.GetDouble(); if (!double.IsFinite(number) || number is < 0 or > 1) throw Invalid("PBR factor is outside its range."); }
    }

    private static (int Width, int Height) Dimensions(Stream stream, long start, long length, string mime, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (length < 12 || start < 0 || start > stream.Length - length) throw Invalid("Invalid embedded image range.");
        stream.Position = start;
        Span<byte> header = stackalloc byte[32]; var headerLength = (int)Math.Min(length, header.Length);
        stream.ReadExactly(header[..headerLength]); int width, height;
        if (mime == "image/png" && headerLength >= 24 && header[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) && header.Slice(12,4).SequenceEqual("IHDR"u8))
        { width = BinaryPrimitives.ReadInt32BigEndian(header[16..]); height = BinaryPrimitives.ReadInt32BigEndian(header[20..]); }
        else if (mime == "image/webp" && headerLength >= 30 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8,4).SequenceEqual("WEBP"u8))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) + 8L != length) throw Invalid("WebP framing length differs.");
            if (header.Slice(12,4).SequenceEqual("VP8X"u8)) { width = U24(header[24..]) + 1; height = U24(header[27..]) + 1; }
            else if (header.Slice(12,4).SequenceEqual("VP8L"u8) && header[20] == 0x2f)
            { var bits = BinaryPrimitives.ReadUInt32LittleEndian(header[21..]); width = (int)(bits & 0x3fff) + 1; height = (int)((bits >> 14) & 0x3fff) + 1; }
            else if (header.Slice(12,4).SequenceEqual("VP8 "u8) && header.Slice(23,3).SequenceEqual(new byte[] {0x9d,1,0x2a}))
            { width = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]) & 0x3fff; height = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]) & 0x3fff; }
            else throw Invalid("Unsupported WebP framing.");
        }
        else if (mime == "image/jpeg" && header[0] == 0xff && header[1] == 0xd8)
        {
            stream.Position = start + 2; width = height = 0;
            Span<byte> size = stackalloc byte[2];
            Span<byte> frame = stackalloc byte[5];
            while (stream.Position < start + length)
            {
                token.ThrowIfCancellationRequested();
                if (stream.ReadByte() != 0xff) throw Invalid("Invalid JPEG marker.");
                int marker; do { marker = stream.ReadByte(); } while (marker == 0xff);
                if (marker is < 0 or 0xd9 or 0xda) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                stream.ReadExactly(size);
                var segment = BinaryPrimitives.ReadUInt16BigEndian(size);
                if (segment < 2 || stream.Position > start + length - (segment - 2)) throw Invalid("JPEG segment exceeds the image.");
                if (marker is 0xc0 or 0xc1 or 0xc2)
                {
                    if (segment < 8) throw Invalid("Invalid JPEG frame.");
                    stream.ReadExactly(frame);
                    height = BinaryPrimitives.ReadUInt16BigEndian(frame[1..]); width = BinaryPrimitives.ReadUInt16BigEndian(frame[3..]); break;
                }
                stream.Seek(segment - 2, SeekOrigin.Current);
            }
        }
        else throw Invalid("Image bytes do not match the supported declared type.");
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || (long)width * height > MaximumTexturePixels)
            throw Invalid("Image exceeds the qualified 4K limit or has invalid dimensions.");
        return (width, height);
    }

    private static int U24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
    private static long Number(JsonElement value, string name, long fallback) => value.TryGetProperty(name, out var item) ? item.GetInt64() : fallback;
    private static int Index(JsonElement value, string name, int count)
    { var index = value.GetProperty(name).GetInt32(); if (index < 0 || index >= count) throw Invalid("glTF resource index is out of bounds."); return index; }
    private static JsonElement Array(JsonElement root, string name, int min, int max)
    { var array = root.GetProperty(name); if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < min || array.GetArrayLength() > max) throw Invalid("glTF array exceeds bounds."); return array; }
    private static JsonElement OptionalArray(JsonElement root, string name, int max)
    { if (!root.TryGetProperty(name, out _)) return JsonSerializer.SerializeToElement(System.Array.Empty<object>()); return Array(root, name, 0, max); }
    private static void Json(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw Invalid("Duplicate JSON property.");
                if (field.Name == "extensions" && field.Value.ValueKind == JsonValueKind.Object &&
                    field.Value.EnumerateObject().Any(x => x.Name != "EXT_texture_webp")) throw Invalid("Unsupported glTF extension.");
                Json(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Json(item);
        else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number))) throw Invalid("Nonfinite JSON value.");
    }
    private static InvalidDataException Invalid(string message) => new(message);
}
