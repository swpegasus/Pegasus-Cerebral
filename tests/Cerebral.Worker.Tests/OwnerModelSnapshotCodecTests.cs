using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Models;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Deterministic unit checks of the actual internal codec against independently fixed reference
/// bytes/digests. No owner route, database, filesystem scan, provider, model, service or network is exercised.
/// </summary>
public static class OwnerModelSnapshotCodecTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] BindingDomain = Encoding.ASCII.GetBytes("Cerebral.RemotePreparedModelBinding/1\n");
    private static readonly string[] Fields =
        ["Id", "Path", "ChatTemplate", "StopSequences", "MaximumContextTokens", "ParallelRequests", "GpuLayers", "VisionProjectorPath"];

    // Expected constants were generated independently with ECMAScript JSON serialization,
    // short-control-escape translation, Node SHA-256 and UInt32BE length framing.
    // Task-private reference-vectors/generate.mjs and vectors.json retain the reproducible inputs.
    private static readonly BindingVector[] Bindings =
    [
        new("baseline", new("model-a", "/models/a.gguf", null,
            ["stop", ""], 4096, 1, -1, null),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsIiJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6NDA5NiwiUGFyYWxsZWxSZXF1ZXN0cyI6MSwiR3B1TGF5ZXJzIjotMSwiVmlzaW9uUHJvamVjdG9yUGF0aCI6bnVsbH0=", 179, 217,
            "2f0589d14a28bd459dee734e224f0f5c393407775b892346359ea56769080bb3"),
        new("Id", new("model-b", "/models/a.gguf", null,
            ["stop", ""], 4096, 1, -1, null),
            "eyJJZCI6Im1vZGVsLWIiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsIiJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6NDA5NiwiUGFyYWxsZWxSZXF1ZXN0cyI6MSwiR3B1TGF5ZXJzIjotMSwiVmlzaW9uUHJvamVjdG9yUGF0aCI6bnVsbH0=", 179, 217,
            "91d40f9ba38098d5cd0caa3d2c2333a1e2923ca92bbfe7acca02a1f4fb77fd3c"),
        new("Path", new("model-a", "/models/b.gguf", null,
            ["stop", ""], 4096, 1, -1, null),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9iLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsIiJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6NDA5NiwiUGFyYWxsZWxSZXF1ZXN0cyI6MSwiR3B1TGF5ZXJzIjotMSwiVmlzaW9uUHJvamVjdG9yUGF0aCI6bnVsbH0=", 179, 217,
            "d710bfe9d4c979497e6b45fdbb8be678505fb7f6c9faa07dbcf5861f6c657467"),
        new("ChatTemplate", new("model-a", "/models/a.gguf", "",
            ["stop", ""], 4096, 1, -1, null),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOiIiLCJTdG9wU2VxdWVuY2VzIjpbInN0b3AiLCIiXSwiTWF4aW11bUNvbnRleHRUb2tlbnMiOjQwOTYsIlBhcmFsbGVsUmVxdWVzdHMiOjEsIkdwdUxheWVycyI6LTEsIlZpc2lvblByb2plY3RvclBhdGgiOm51bGx9", 177, 215,
            "24d7c27f49247017995a6d9163011ff92b359575e198234ff31c178fd1da0e65"),
        new("StopSequences", new("model-a", "/models/a.gguf", null,
            ["", "stop"], 4096, 1, -1, null),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsiIiwic3RvcCJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6NDA5NiwiUGFyYWxsZWxSZXF1ZXN0cyI6MSwiR3B1TGF5ZXJzIjotMSwiVmlzaW9uUHJvamVjdG9yUGF0aCI6bnVsbH0=", 179, 217,
            "932640a71004f1aa0165f5c4c5b5cc1eb9fa27c4aaab3110719471ee49b888b5"),
        new("MaximumContextTokens", new("model-a", "/models/a.gguf", null,
            ["stop", ""], 8192, 1, -1, null),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsIiJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6ODE5MiwiUGFyYWxsZWxSZXF1ZXN0cyI6MSwiR3B1TGF5ZXJzIjotMSwiVmlzaW9uUHJvamVjdG9yUGF0aCI6bnVsbH0=", 179, 217,
            "7289e2f1f92a6b9c2b2d36d79d6c7b5a25300b4aca9cd5371cb1c260c807f7ae"),
        new("ParallelRequests", new("model-a", "/models/a.gguf", null,
            ["stop", ""], 4096, 2, -1, null),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsIiJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6NDA5NiwiUGFyYWxsZWxSZXF1ZXN0cyI6MiwiR3B1TGF5ZXJzIjotMSwiVmlzaW9uUHJvamVjdG9yUGF0aCI6bnVsbH0=", 179, 217,
            "cece90844a266804aabe0f8894da361e3b3eea69a41dcec541177c1342e155c6"),
        new("GpuLayers", new("model-a", "/models/a.gguf", null,
            ["stop", ""], 4096, 1, 0, null),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsIiJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6NDA5NiwiUGFyYWxsZWxSZXF1ZXN0cyI6MSwiR3B1TGF5ZXJzIjowLCJWaXNpb25Qcm9qZWN0b3JQYXRoIjpudWxsfQ==", 178, 216,
            "63f3a2f2dd8d8b555231939cc77f3d1b618cff88ed238de93f852f5982de1e95"),
        new("VisionProjectorPath", new("model-a", "/models/a.gguf", null,
            ["stop", ""], 4096, 1, -1, "/models/projector.gguf"),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsIiJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6NDA5NiwiUGFyYWxsZWxSZXF1ZXN0cyI6MSwiR3B1TGF5ZXJzIjotMSwiVmlzaW9uUHJvamVjdG9yUGF0aCI6Ii9tb2RlbHMvcHJvamVjdG9yLmdndWYifQ==", 199, 237,
            "fc761c0bd1244105be21cf986cbd60401a394350b4928c01f4ac1fda21f438de"),
        new("empty-projector", new("model-a", "/models/a.gguf", null,
            ["stop", ""], 4096, 1, -1, ""),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsIiJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6NDA5NiwiUGFyYWxsZWxSZXF1ZXN0cyI6MSwiR3B1TGF5ZXJzIjotMSwiVmlzaW9uUHJvamVjdG9yUGF0aCI6IiJ9", 177, 215,
            "085a4b5077694ecc59410d0b0fb0f2ada872faa5ecfecaa6dc47828e183a74dd"),
        new("duplicate-stops", new("model-a", "/models/a.gguf", null,
            ["stop", "", "stop"], 4096, 1, -1, null),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsIiIsInN0b3AiXSwiTWF4aW11bUNvbnRleHRUb2tlbnMiOjQwOTYsIlBhcmFsbGVsUmVxdWVzdHMiOjEsIkdwdUxheWVycyI6LTEsIlZpc2lvblByb2plY3RvclBhdGgiOm51bGx9", 186, 224,
            "6c03653a5df05fa1b7dc905385d7b11d1bca833a04831e1aa0767b96fefede46"),
        new("reordered-duplicates", new("model-a", "/models/a.gguf", null,
            ["stop", "stop", ""], 4096, 1, -1, null),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsInN0b3AiLCIiXSwiTWF4aW11bUNvbnRleHRUb2tlbnMiOjQwOTYsIlBhcmFsbGVsUmVxdWVzdHMiOjEsIkdwdUxheWVycyI6LTEsIlZpc2lvblByb2plY3RvclBhdGgiOm51bGx9", 186, 224,
            "8cdcf62490cf33451217ccee947a5b14b303b64278945a9a9cadcfe07565b92b"),
        new("single-stop", new("model-a", "/models/a.gguf", null,
            ["stop"], 4096, 1, -1, null),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6NDA5NiwiUGFyYWxsZWxSZXF1ZXN0cyI6MSwiR3B1TGF5ZXJzIjotMSwiVmlzaW9uUHJvamVjdG9yUGF0aCI6bnVsbH0=", 176, 214,
            "0fce7a98d802e19ae88f6ad15b638a3902a019e3ebe469f1c668d8d4a6db6e46"),
        new("unicode-control-paths", new("modèle😀", "C:\\Models\\猫/\"quoted\".gguf", "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\u000b\f\r\u000e\u000f\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f\"\\/",
            ["é", "é", "终", "😀", "\r\t"], 0, 64, -2147483648, "/srv/模型/projector\\name.gguf"),
            "eyJJZCI6Im1vZMOobGXwn5iAIiwiUGF0aCI6IkM6XFxNb2RlbHNcXOeMqy9cInF1b3RlZFwiLmdndWYiLCJDaGF0VGVtcGxhdGUiOiJcdTAwMDBcdTAwMDFcdTAwMDJcdTAwMDNcdTAwMDRcdTAwMDVcdTAwMDZcdTAwMDdcdTAwMDhcdTAwMDlcdTAwMGFcdTAwMGJcdTAwMGNcdTAwMGRcdTAwMGVcdTAwMGZcdTAwMTBcdTAwMTFcdTAwMTJcdTAwMTNcdTAwMTRcdTAwMTVcdTAwMTZcdTAwMTdcdTAwMThcdTAwMTlcdTAwMWFcdTAwMWJcdTAwMWNcdTAwMWRcdTAwMWVcdTAwMWZcIlxcLyIsIlN0b3BTZXF1ZW5jZXMiOlsiw6kiLCJlzIEiLCLnu4giLCLwn5iAIiwiXHUwMDBkXHUwMDA5Il0sIk1heGltdW1Db250ZXh0VG9rZW5zIjowLCJQYXJhbGxlbFJlcXVlc3RzIjo2NCwiR3B1TGF5ZXJzIjotMjE0NzQ4MzY0OCwiVmlzaW9uUHJvamVjdG9yUGF0aCI6Ii9zcnYv5qih5Z6LL3Byb2plY3RvclxcbmFtZS5nZ3VmIn0=", 461, 499,
            "fc48c00cdde8917bf37c147630fc11169ac642a4798cbc121e5bf5174d7f2214"),
        new("windows-path", new("model-a", "C:\\Models\\..\\a.gguf", null,
            ["stop", ""], 4096, 1, -1, "D:\\Projection\\model.GGUF"),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiQzpcXE1vZGVsc1xcLi5cXGEuZ2d1ZiIsIkNoYXRUZW1wbGF0ZSI6bnVsbCwiU3RvcFNlcXVlbmNlcyI6WyJzdG9wIiwiIl0sIk1heGltdW1Db250ZXh0VG9rZW5zIjo0MDk2LCJQYXJhbGxlbFJlcXVlc3RzIjoxLCJHcHVMYXllcnMiOi0xLCJWaXNpb25Qcm9qZWN0b3JQYXRoIjoiRDpcXFByb2plY3Rpb25cXG1vZGVsLkdHVUYifQ==", 211, 249,
            "01a2268d4262054bd68ec5a0bb276b3bca39a973ec45f88b72a8c323451a4368"),
        new("linux-verbatim-path", new("model-a", "/models/../a.gguf", null,
            ["stop", ""], 4096, 1, -1, "/Projectors//A.gguf"),
            "eyJJZCI6Im1vZGVsLWEiLCJQYXRoIjoiL21vZGVscy8uLi9hLmdndWYiLCJDaGF0VGVtcGxhdGUiOm51bGwsIlN0b3BTZXF1ZW5jZXMiOlsic3RvcCIsIiJdLCJNYXhpbXVtQ29udGV4dFRva2VucyI6NDA5NiwiUGFyYWxsZWxSZXF1ZXN0cyI6MSwiR3B1TGF5ZXJzIjotMSwiVmlzaW9uUHJvamVjdG9yUGF0aCI6Ii9Qcm9qZWN0b3JzLy9BLmdndWYifQ==", 199, 237,
            "3bd82fb45d7006004a1307c0c25e91153620295298eda3fe5efddf22b1a4fde4"),
        new("composed-id", new("é", "/models/a.gguf", null,
            ["stop", ""], 4096, 1, -1, null),
            "eyJJZCI6IsOpIiwiUGF0aCI6Ii9tb2RlbHMvYS5nZ3VmIiwiQ2hhdFRlbXBsYXRlIjpudWxsLCJTdG9wU2VxdWVuY2VzIjpbInN0b3AiLCIiXSwiTWF4aW11bUNvbnRleHRUb2tlbnMiOjQwOTYsIlBhcmFsbGVsUmVxdWVzdHMiOjEsIkdwdUxheWVycyI6LTEsIlZpc2lvblByb2plY3RvclBhdGgiOm51bGx9", 174, 212,
            "2d360699f133c6fcdeef483024960d399d689ba2cf028afac705c38818faee84"),
        new("decomposed-id", new("é", "/models/a.gguf", null,
            ["stop", ""], 4096, 1, -1, null),
            "eyJJZCI6ImXMgSIsIlBhdGgiOiIvbW9kZWxzL2EuZ2d1ZiIsIkNoYXRUZW1wbGF0ZSI6bnVsbCwiU3RvcFNlcXVlbmNlcyI6WyJzdG9wIiwiIl0sIk1heGltdW1Db250ZXh0VG9rZW5zIjo0MDk2LCJQYXJhbGxlbFJlcXVlc3RzIjoxLCJHcHVMYXllcnMiOi0xLCJWaXNpb25Qcm9qZWN0b3JQYXRoIjpudWxsfQ==", 175, 213,
            "baa8ce7bcf20762aa6b196be04ca2d4b5394e5e06f4a74ae4560c842a287d1e1")
    ];

    private static readonly FenceVector[] Fences =
    [
        new("empty", [],
            "db23884ccdbec7482550b2f8e410618d57fc770f89b564dc7b6477eff1402bea", null),
        new("alpha", [new("alpha", Convert.FromBase64String("eyJpZCI6ImFscGhhIiwidmFsdWUiOjF9"))],
            "345f24544a3ca17c3c1b40725831a1437677bd335e8d013af458ee0b0e58e4b8", "2bc3d05f0b73bd109513ac486959a650629f989ff82470ac02b1d0df06cce9c7"),
        new("alpha-beta", [new("beta", Convert.FromBase64String("eyJpZCI6ImJldGEiLCJ2YWx1ZSI6Mn0=")), new("alpha", Convert.FromBase64String("eyJpZCI6ImFscGhhIiwidmFsdWUiOjF9"))],
            "d5c02eb70a3391b53858808104a40cffd33606297fe7f7d7c7ec9c577aa3f430", "242688de5cf8e8d27a75311a8933c214fd054781ef55a97886c05e44ddbb6a69"),
        new("body-whitespace", [new("alpha", Convert.FromBase64String("eyAiaWQiOiAiYWxwaGEiLCAidmFsdWUiOiAxIH0=")), new("beta", Convert.FromBase64String("eyJpZCI6ImJldGEiLCJ2YWx1ZSI6Mn0="))],
            "8ef326f9fa33f13ff2785522347bf0971ee0b154605950d394516aa0728c7ff8", "dcd611a682441197d02bf670069849c35b7da296e3b48b1168b854e84ad1fa65"),
        new("body-value", [new("alpha", Convert.FromBase64String("eyJpZCI6ImFscGhhIiwidmFsdWUiOjJ9")), new("beta", Convert.FromBase64String("eyJpZCI6ImJldGEiLCJ2YWx1ZSI6Mn0="))],
            "9a12941c24f7fc7b6615c2891affefe934fa9cd2e0e47724bacc37028694e398", "f65f25a2b35c65ad7e3cbbbcef907c0cd546116fffcd59569b48d78bd3700b6f"),
        new("id-change", [new("Alpha", Convert.FromBase64String("eyJpZCI6ImFscGhhIiwidmFsdWUiOjF9")), new("beta", Convert.FromBase64String("eyJpZCI6ImJldGEiLCJ2YWx1ZSI6Mn0="))],
            "6698788ff240c8a07ebf058aa938b587b6bb40ac593d23ad4773cc177232c992", "799b2cae01e8096eeaed63a5dde2ff89fdb2b15a603c917c670df7adfc208bc3"),
        new("unicode-ordinal", [new("", Convert.FromBase64String("eyJ2YWx1ZSI6InByaXZhdGUtdXNlIn0=")), new("😀", Convert.FromBase64String("eyJ2YWx1ZSI6ImFzdHJhbCJ9")), new("é", Convert.FromBase64String("eyJ2YWx1ZSI6ImNvbXBvc2VkIn0=")), new("é", Convert.FromBase64String("eyJ2YWx1ZSI6ImRlY29tcG9zZWQifQ==")), new("a", Convert.FromBase64String("eyJ2YWx1ZSI6IkFTQ0lJIn0="))],
            "dcc60cd176e81516f32955243ee9d4b0b9d020865887bc0cee77960acd5e9ee5", "f8149cb2ca2d4126ab7e7772292df14c08f81bc66f762e1b4da2034612ef3df7"),
        new("ambiguous-a-bc", [new("a", Convert.FromBase64String("e30=")), new("bc", Convert.FromBase64String("e30="))],
            "a3adafc2076e81266e51075f650e5e7419ca8832ac0bc09b2c2d3c633fdbbddb", "4c96e7700e3ea9871b0292de5667556562cf590df0ffe5d3d3b02b5f7c239fe0"),
        new("ambiguous-ab-c", [new("ab", Convert.FromBase64String("e30=")), new("c", Convert.FromBase64String("e30="))],
            "dac761d1d3f4f0c2b040f211181b23422745ad51e3a223c73e81ef971a89b918", "ce1af9a67c36a8c9ea89cd6d68fd8bdde331daaa5cef46450eb784df4d702116")
    ];

    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }
        InvalidDataException Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException error) { checks++; return error; }
            throw new InvalidOperationException("FAIL: " + message);
        }
        Check(BindingDomain.Length == 38, "The versioned binding domain plus LF must be exactly 38 ASCII bytes.");
        Check(Bindings.Length == 18 && Fences.Length == 9,
            "The fixed vector inventory must retain all field, scalar, path, fence and collision cases.");
        foreach (var vector in Bindings)
        {
            var stopsBefore = vector.Binding.StopSequences.ToArray();
            vector.Binding.Validate();
            var encoded = Codec.Encode(vector.Binding);
            var expected = Convert.FromBase64String(vector.CanonicalBase64);
            Check(encoded.CanonicalJson.AsSpan().SequenceEqual(expected), vector.Name + " canonical bytes must equal the fixed reference exactly.");
            Check(encoded.Sha256 == vector.Sha256, vector.Name + " fingerprint must equal the fixed domain-separated SHA-256.");
            Check(encoded.CanonicalJson.Length == vector.JsonBytes &&
                encoded.FingerprintPreimageBytes == vector.PreimageBytes &&
                encoded.FingerprintPreimageBytes == encoded.CanonicalJson.Length + 38,
                vector.Name + " must report complete JSON and preimage byte counts separately.");
            Check(HashPreimage(encoded.CanonicalJson) == vector.Sha256,
                vector.Name + " actual output bytes must independently reproduce the fixed fingerprint.");
            using var document = JsonDocument.Parse(encoded.CanonicalJson);
            Check(document.RootElement.EnumerateObject().Select(property => property.Name).SequenceEqual(Fields),
                vector.Name + " must retain the exact eight PascalCase fields in their declared order.");
            Check(vector.Binding.StopSequences.SequenceEqual(stopsBefore),
                vector.Name + " encoding must not reorder, deduplicate or mutate the caller's stop list.");
        }
        var baseFingerprint = Bindings[0].Sha256;
        Check(Bindings.Skip(1).Take(8).Select(vector => vector.Name).SequenceEqual(Fields) &&
            Bindings.Skip(1).Take(8).All(vector => vector.Sha256 != baseFingerprint),
            "Drift in each of the eight exact fields must independently change the fixed binding fingerprint.");
        Check(Bindings.Single(vector => vector.Name == "ChatTemplate").Sha256 != baseFingerprint &&
            Bindings.Single(vector => vector.Name == "empty-projector").Sha256 != baseFingerprint,
            "Null and empty template/projector values must remain distinct.");
        Check(Bindings.Single(vector => vector.Name == "duplicate-stops").Sha256 !=
            Bindings.Single(vector => vector.Name == "reordered-duplicates").Sha256 &&
            Bindings.Single(vector => vector.Name == "duplicate-stops").Sha256 !=
            Bindings.Single(vector => vector.Name == "single-stop").Sha256,
            "Ordered and duplicate stops must remain semantic fingerprint input.");
        Check(Bindings.Single(vector => vector.Name == "composed-id").Sha256 !=
            Bindings.Single(vector => vector.Name == "decomposed-id").Sha256,
            "Unicode normalization must not collapse distinct scalar sequences.");
        VerifyCanonicalScalars(Check);
        VerifyCulture(Check);
        VerifyLargeBindings(Check);
        VerifyInvalidBindings(Reject);
        VerifyFences(Check, Reject);
        Console.WriteLine($"Owner model snapshot codec: {checks} deterministic unit assertions passed against independent fixed bytes/digests; no owner admission, retained database, runtime/model observation, network or deployment was exercised.");
        return checks;
    }

    private static void VerifyCanonicalScalars(Action<bool, string> check)
    {
        var vector = Bindings.Single(value => value.Name == "unicode-control-paths");
        var actual = StrictUtf8.GetString(Codec.Encode(vector.Binding).CanonicalJson);
        using var document = JsonDocument.Parse(actual);
        var templateJson = document.RootElement.GetProperty("ChatTemplate").GetRawText();
        var pathJson = document.RootElement.GetProperty("Path").GetRawText();
        var controls = string.Concat(Enumerable.Range(0, 32).Select(value => "\\u00" + value.ToString("x2", CultureInfo.InvariantCulture)));
        check(templateJson.Contains(controls, StringComparison.Ordinal) &&
            !templateJson.Contains("\\n", StringComparison.Ordinal) && !templateJson.Contains("\\r", StringComparison.Ordinal) &&
            !templateJson.Contains("\\t", StringComparison.Ordinal) && !templateJson.Contains("\\b", StringComparison.Ordinal) &&
            !templateJson.Contains("\\f", StringComparison.Ordinal),
            "All ASCII controls must use lowercase six-character escapes, with no short escape forms.");
        check(actual.Contains("modèle😀", StringComparison.Ordinal) && actual.Contains("猫", StringComparison.Ordinal) &&
            actual.Contains("e\u0301", StringComparison.Ordinal) && actual.Contains("-2147483648", StringComparison.Ordinal),
            "Unicode scalars and shortest signed decimal must remain verbatim.");
        check(!pathJson.Contains("\\/", StringComparison.Ordinal) && !actual.Contains("\\u00e9", StringComparison.Ordinal) &&
            actual.Contains("C:\\\\Models\\\\猫/\\\"quoted\\\".gguf", StringComparison.Ordinal),
            "Slash/non-ASCII must be emitted directly while quotes and Windows backslashes are escaped.");
        check(Codec.Encode(Bindings.Single(value => value.Name == "windows-path").Binding).Sha256 ==
            Bindings.Single(value => value.Name == "windows-path").Sha256 &&
            Codec.Encode(Bindings.Single(value => value.Name == "linux-verbatim-path").Binding).Sha256 ==
            Bindings.Single(value => value.Name == "linux-verbatim-path").Sha256,
            "Remote Windows/Linux path spelling, dot segments, case and repeated separators must not be normalized.");
    }

    private static void VerifyCulture(Action<bool, string> check)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var vector = Bindings.Single(value => value.Name == "unicode-control-paths");
            var encoded = Codec.Encode(vector.Binding);
            check(encoded.Sha256 == vector.Sha256 &&
                encoded.CanonicalJson.AsSpan().SequenceEqual(Convert.FromBase64String(vector.CanonicalBase64)),
                "Current culture must not change signed decimal or canonical JSON bytes.");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    private static void VerifyLargeBindings(Action<bool, string> check)
    {
        var basic = Bindings[0].Binding;
        var large = basic with
        {
            ChatTemplate = new string('t', 65536),
            StopSequences = Enumerable.Repeat(new string('s', 4096), 128).ToArray()
        };
        var maximalEscaping = basic with
        {
            Path = new string('\u0001', 32768),
            ChatTemplate = new string('\0', 65536),
            StopSequences = Enumerable.Repeat(new string('\u0001', 4096), 128).ToArray(),
            VisionProjectorPath = new string('\u0001', 32768)
        };
        foreach (var vector in new[]
        {
            new LargeVector("large-valid-ascii", large, 590375, 590413, "6bfb0d35b4dec43426c9648d478d32fda055af85441b79404791dbe07485f17c"),
            new LargeVector("maximal-valid-control-escaping", maximalEscaping, 3932695, 3932733, "3545cc5ec4147dc42e29e4206f87b276e0ed60d4c2ef5360bae9fa8a18a04b41")
        })
        {
            vector.Binding.Validate();
            var encoded = Codec.Encode(vector.Binding);
            check(encoded.CanonicalJson.Length == vector.JsonBytes &&
                encoded.FingerprintPreimageBytes == vector.PreimageBytes &&
                encoded.FingerprintPreimageBytes == encoded.CanonicalJson.Length + 38 &&
                encoded.CanonicalJson.Length > 256 * 1024 && encoded.FingerprintPreimageBytes <= 4 * 1024 * 1024,
                vector.Name + " must encode the entire valid binding above the response budget, within the fingerprint bound.");
            check(encoded.Sha256 == vector.Sha256 && HashPreimage(encoded.CanonicalJson) == vector.Sha256,
                vector.Name + " complete emitted bytes must match the independently fixed fingerprint.");
            using var document = JsonDocument.Parse(encoded.CanonicalJson);
            check(document.RootElement.GetProperty("ChatTemplate").GetString() == vector.Binding.ChatTemplate &&
                document.RootElement.GetProperty("StopSequences").EnumerateArray().Select(item => item.GetString())
                    .SequenceEqual(vector.Binding.StopSequences) &&
                document.RootElement.GetProperty("Path").GetString() == vector.Binding.Path &&
                document.RootElement.GetProperty("VisionProjectorPath").GetString() == vector.Binding.VisionProjectorPath,
                vector.Name + " output must contain full untruncated scalar/list values.");
        }
    }

    private static void VerifyInvalidBindings(Func<Action, string, InvalidDataException> reject)
    {
        var basic = Bindings[0].Binding;
        var invalid = new (string Reason, RemotePreparedModelBinding Binding)[]
        {
            ("null ID", basic with { Id = null! }),
            ("empty ID", basic with { Id = "" }),
            ("whitespace ID", basic with { Id = "model a" }),
            ("long ID", basic with { Id = new string('x', 129) }),
            ("null path", basic with { Path = null! }),
            ("empty path", basic with { Path = "" }),
            ("long path", basic with { Path = new string('x', 32769) }),
            ("NUL path", basic with { Path = "/models/a\0.gguf" }),
            ("long template", basic with { ChatTemplate = new string('x', 65537) }),
            ("null stops", basic with { StopSequences = null! }),
            ("null stop item", basic with { StopSequences = new string[] { null! } }),
            ("too many stops", basic with { StopSequences = Enumerable.Repeat("", 129).ToArray() }),
            ("long stop", basic with { StopSequences = new[] { new string('x', 4097) } }),
            ("NUL stop", basic with { StopSequences = new[] { "a\0b" } }),
            ("negative context", basic with { MaximumContextTokens = -1 }),
            ("small context", basic with { MaximumContextTokens = 511 }),
            ("large context", basic with { MaximumContextTokens = 1048577 }),
            ("zero parallel", basic with { ParallelRequests = 0 }),
            ("large parallel", basic with { ParallelRequests = 65 }),
            ("long projector", basic with { VisionProjectorPath = new string('x', 32769) }),
            ("high surrogate ID", basic with { Id = "\uD800" }),
            ("low surrogate path", basic with { Path = "/models/\uDC00" }),
            ("unpaired template", basic with { ChatTemplate = "\uD800x" }),
            ("unpaired stop", basic with { StopSequences = new[] { "\uDC00" } }),
            ("unpaired projector", basic with { VisionProjectorPath = "/models/\uD800" }),
            ("oversized unencodable preimage candidate", basic with { ChatTemplate = new string('\u0001', 4 * 1024 * 1024) })
        };
        foreach (var value in invalid)
            reject(() => Codec.Encode(value.Binding), value.Reason + " must fail with classified InvalidDataException without replacement/truncation.");
        reject(() => Codec.Encode(null!), "A null binding must fail with classified InvalidDataException.");
    }

    private static void VerifyFences(Action<bool, string> check, Func<Action, string, InvalidDataException> reject)
    {
        foreach (var vector in Fences)
        {
            check(Codec.Catalog(vector.Rows) == vector.CatalogSha256,
                vector.Name + " catalog fence must equal its independent fixed domain/framed digest.");
            check(Codec.Catalog(vector.Rows.Reverse().ToArray()) == vector.CatalogSha256,
                vector.Name + " catalog fence must be independent of input row order.");
            if (vector.RegistrationSha256 is not null)
            {
                check(Codec.Registration(vector.Rows[0]) == vector.RegistrationSha256,
                    vector.Name + " selected registration must equal the independent fixed ID/raw-body digest.");
                if (vector.Rows.Length == 1)
                    check(vector.RegistrationSha256 != vector.CatalogSha256,
                        "Registration and one-row catalog domains must produce distinct fixed digests.");
            }
        }
        var catalog = Fences.Single(value => value.Name == "alpha-beta");
        check(new[] { "body-whitespace", "body-value", "id-change" }.All(name =>
            Fences.Single(value => value.Name == name).CatalogSha256 != catalog.CatalogSha256),
            "Raw body whitespace/value and exact storage-ID changes must conservatively change the catalog fence.");
        var first = Fences.Single(value => value.Name == "ambiguous-a-bc");
        var second = Fences.Single(value => value.Name == "ambiguous-ab-c");
        check(string.Concat(first.Rows.Select(row => row.Id)) == string.Concat(second.Rows.Select(row => row.Id)) &&
            first.CatalogSha256 != second.CatalogSha256,
            "UInt32BE ID byte lengths must distinguish ambiguous concatenated ID lists.");
        var alpha = Fences.Single(value => value.Name == "alpha").Rows.Single();
        reject(() => Codec.Catalog([alpha, alpha]), "Duplicate IDs with identical bodies must not produce a complete catalog fence.");
        reject(() => Codec.Catalog([alpha, alpha with { Body = StrictUtf8.GetBytes("{\"different\":true}") }]),
            "Duplicate IDs with different bodies must not produce a complete catalog fence.");
        foreach (var invalid in new[]
        {
            new RawRow(null!, StrictUtf8.GetBytes("{}")),
            new RawRow("", StrictUtf8.GetBytes("{}")),
            new RawRow("\uD800", StrictUtf8.GetBytes("{}")),
            new RawRow("alpha", null!),
            new RawRow("alpha", new byte[] { 0xc3, 0x28 })
        })
        {
            reject(() => Codec.Catalog([invalid]), "Malformed/null/invalid UTF-8 row input must reject catalog fence generation.");
            reject(() => Codec.Registration(invalid), "Malformed/null/invalid UTF-8 row input must reject registration fence generation.");
        }
        reject(() => Codec.Catalog(null!), "A null row collection must not produce a complete-catalog digest.");
        reject(() => Codec.Catalog(Enumerable.Range(0, 257).Select(index => new RawRow("row-" + index,
            StrictUtf8.GetBytes("{}"))).ToArray()), "A catalog over 256 rows must fail closed instead of hashing a prefix.");
        var oversized = new RawRow("large", Enumerable.Repeat((byte)'x', 8 * 1024 * 1024 + 1).ToArray());
        reject(() => Codec.Catalog([oversized]), "Aggregate retained-body overflow must not produce a complete catalog digest.");
        reject(() => Codec.Registration(oversized), "Oversized retained-body evidence must not produce a selected registration digest.");
    }

    private static string HashPreimage(byte[] actualJson)
    {
        var preimage = new byte[BindingDomain.Length + actualJson.Length];
        BindingDomain.CopyTo(preimage, 0);
        actualJson.CopyTo(preimage, BindingDomain.Length);
        return Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant();
    }

    private sealed record BindingVector(string Name, RemotePreparedModelBinding Binding,
        string CanonicalBase64, int JsonBytes, int PreimageBytes, string Sha256);
    private sealed record LargeVector(string Name, RemotePreparedModelBinding Binding,
        int JsonBytes, int PreimageBytes, string Sha256);
    private sealed record RawRow(string Id, byte[] Body);
    private sealed record FenceVector(string Name, RawRow[] Rows, string CatalogSha256, string? RegistrationSha256);
    private sealed record EncodingResult(byte[] CanonicalJson, string Sha256, int FingerprintPreimageBytes);

    private static class Codec
    {
        private const BindingFlags StaticMethods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly Type Type = typeof(RemotePreparedInference).Assembly.GetType("Cerebral.Worker.OwnerModelSnapshotCodec")
            ?? throw new InvalidOperationException("Missing actual internal OwnerModelSnapshotCodec.");
        private static readonly Type RowType = typeof(RemotePreparedInference).Assembly.GetType("Cerebral.Worker.OwnerSnapshotRawModelRow")
            ?? throw new InvalidOperationException("Missing actual internal OwnerSnapshotRawModelRow.");
        private static readonly ConstructorInfo RowConstructor = RowType.GetConstructor([typeof(string), typeof(byte[])])
            ?? throw new InvalidOperationException("Missing actual raw-row constructor.");
        private static readonly MethodInfo EncodeMethod = Method("EncodeBinding");
        private static readonly MethodInfo CatalogMethod = Method("CatalogFence");
        private static readonly MethodInfo RegistrationMethod = Method("RegistrationFence");

        public static EncodingResult Encode(RemotePreparedModelBinding binding)
        {
            var result = Invoke(EncodeMethod, binding);
            T Property<T>(string name) => (T)(result.GetType().GetProperty(name)?.GetValue(result)
                ?? throw new InvalidOperationException("Missing actual encoding result property " + name));
            return new(Property<byte[]>("CanonicalJson"), Property<string>("Sha256"), Property<int>("FingerprintPreimageBytes"));
        }

        public static string Catalog(RawRow[] rows)
        {
            if (rows is null) return (string)Invoke(CatalogMethod, null);
            var actual = Array.CreateInstance(RowType, rows.Length);
            for (var index = 0; index < rows.Length; index++) actual.SetValue(Row(rows[index]), index);
            return (string)Invoke(CatalogMethod, actual);
        }

        public static string Registration(RawRow row) => (string)Invoke(RegistrationMethod, Row(row));
        private static object Row(RawRow row) => RowConstructor.Invoke([row.Id, row.Body]);
        private static MethodInfo Method(string name) => Type.GetMethod(name, StaticMethods)
            ?? throw new InvalidOperationException("Missing actual codec method " + name);

        private static object Invoke(MethodInfo method, object? argument)
        {
            try { return method.Invoke(null, [argument]) ?? throw new InvalidOperationException("The actual codec returned null."); }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }
    }
}
