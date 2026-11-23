using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class ModelCapabilityTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-model-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }

        try
        {
        var qwenPath = Path.Combine(root, "weights.gguf"); File.WriteAllBytes(qwenPath, "GGUF"u8.ToArray());
        var qwen = new ModelRecord
        {
            Id = "qwen35", Name = "Qwen 3.5", Path = qwenPath, Architecture = "qwen35",
            DeclaredType = "model", MetadataSource = "gguf + metadata estimate", Purpose = "General"
        };
        var qwenCapabilities = ModelCapabilityPolicy.Assess(qwen);
        var qwenText = qwenCapabilities.Single(x => x.Id == ModelCapabilityIds.TextGeneration);
        var qwenImage = qwenCapabilities.Single(x => x.Id == ModelCapabilityIds.ImageInput);
        Check(qwenText.Usable && qwenText.PayloadStatus == ModelCapabilityStates.Available && qwenText.RuntimeStatus == ModelCapabilityStates.Available,
            "explicit Qwen 3.5 GGUF metadata keeps the implemented text path available");
        Check(!qwenImage.Usable && qwenImage.FamilyStatus == ModelCapabilityStates.Potential &&
              qwenImage.PayloadStatus == ModelCapabilityStates.Missing && qwenImage.RuntimeStatus == ModelCapabilityStates.Unknown &&
              qwenImage.EvidenceBasis == "filesystem-and-runtime-unverified" && qwenImage.Evidence.Contains("native image path is implemented", StringComparison.Ordinal) &&
              qwenImage.Evidence.Contains("projector", StringComparison.OrdinalIgnoreCase),
            "Qwen 3.5 remains family potential with missing component and unverified runtime despite implemented image path");

        var missing = CerebralJson.Read<ModelRecord>(CerebralJson.Element(qwen)); missing.Path = Path.Combine(root, "missing.gguf");
        var missingText = ModelCapabilityPolicy.Assess(missing).Single(x => x.Id == ModelCapabilityIds.TextGeneration);
        Check(!missingText.Usable && missingText.PayloadStatus == ModelCapabilityStates.Missing &&
            missingText.Evidence.Contains("missing", StringComparison.OrdinalIgnoreCase),
            "declared text metadata cannot make a missing local GGUF payload available");
        var invalid = CerebralJson.Read<ModelRecord>(CerebralJson.Element(qwen)); invalid.Path = Path.Combine(root, "invalid.gguf"); File.WriteAllText(invalid.Path, "NOPE");
        var invalidText = ModelCapabilityPolicy.Assess(invalid).Single(x => x.Id == ModelCapabilityIds.TextGeneration);
        Check(!invalidText.Usable && invalidText.PayloadStatus == ModelCapabilityStates.Unavailable &&
            invalidText.Evidence.Contains("valid GGUF header", StringComparison.Ordinal),
            "a readable non-GGUF file is not reported as an installed model payload");
        var locked = CerebralJson.Read<ModelRecord>(CerebralJson.Element(qwen)); locked.Path = Path.Combine(root, "locked.gguf"); File.WriteAllBytes(locked.Path, "GGUF"u8.ToArray());
        using (var exclusive = new FileStream(locked.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedText = ModelCapabilityPolicy.Assess(locked).Single(x => x.Id == ModelCapabilityIds.TextGeneration);
            Check(!lockedText.Usable && lockedText.PayloadStatus == ModelCapabilityStates.Unavailable &&
                lockedText.Evidence.Contains("cannot currently be opened", StringComparison.Ordinal),
                "an installed GGUF that the service cannot open is reported as unreadable rather than ready");
        }

        var misleadingName = new ModelRecord
        {
            Id = "llama", Name = "Qwen3.5-looking filename", Path = @"C:\models\Qwen3.5.gguf", Architecture = "llama",
            DeclaredType = "model", MetadataSource = "gguf", Purpose = "General"
        };
        Check(ModelCapabilityPolicy.Assess(misleadingName).Single(x => x.Id == ModelCapabilityIds.ImageInput).FamilyStatus == ModelCapabilityStates.Unknown,
            "image family potential is not inferred from a model name or filename");

        var legacyJson = JsonDocument.Parse("""{"id":"legacy","name":"Legacy","modelId":"qwen35"}""").RootElement;
        var legacy = CerebralJson.Read<AgentProfile>(legacyJson);
        ModelCapabilityPolicy.EnsureCompatible(legacy, qwen);
        ModelCapabilityPolicy.EnsureCompatible(legacy, null);
        Check(legacy.RequiredModelCapabilities.Count == 0 && ModelCapabilityPolicy.Requirements(legacy).SequenceEqual([ModelCapabilityIds.TextGeneration]),
            "legacy and remote profiles without the new field retain the implicit text requirement when local payload verification is unavailable");
        var missingTextRejected = false;
        try { ModelCapabilityPolicy.EnsureCompatible(legacy, missing); }
        catch (InvalidOperationException ex) { missingTextRejected = ex.Message.Contains("payload: missing", StringComparison.Ordinal); }
        Check(missingTextRejected, "a local text profile cannot accept a registered model whose GGUF payload is missing");

        var vision = CerebralJson.Read<AgentProfile>(CerebralJson.Element(legacy));
        vision.Name = "Vision helper";
        vision.RequiredModelCapabilities = [ModelCapabilityIds.ImageInput];
        var incompatible = false;
        try { ModelCapabilityPolicy.EnsureCompatible(vision, qwen); }
        catch (InvalidOperationException ex)
        {
            incompatible = ex.Message.Contains("cannot currently provide", StringComparison.Ordinal) &&
                ex.Message.Contains("payload: missing", StringComparison.Ordinal) && ex.Message.Contains("runtime path: unknown", StringComparison.Ordinal);
        }
        Check(incompatible, "a vision profile cannot be assigned to weights whose image path is not currently usable");

        vision.RequiredModelCapabilities = ["future-unknown"];
        var unknownRejected = false;
        try { Configuration.ValidateProfile(vision); }
        catch (ArgumentException ex) { unknownRejected = ex.Message.Contains("Unknown required model capability", StringComparison.Ordinal); }
        Check(unknownRejected, "unknown model requirements fail closed instead of being silently ignored");

        Console.WriteLine($"Model capability policy: {checks} checks passed (metadata/contract fixtures only; no model load, image input, inference, or network).\n");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
