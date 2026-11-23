using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private static OwnerVerificationRequirement ReadOwnerVerificationRequirement(AgentHarnessSnapshot snapshot)
    {
        // The Worker freezes this validated recipe in SettingsSnapshot at submission. Workflow
        // input, model output and a tool-name requirement cannot supply this obligation.
        var settings = snapshot.Submission.SettingsSnapshot;
        if (settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("resolvedSettingsVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var edition) || edition != 8 ||
            !settings.TryGetProperty("ownerVerification", out var recipe) || recipe.ValueKind != JsonValueKind.Object ||
            !recipe.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var recipeEdition) || recipeEdition != 2)
            throw new ArgumentException("The owner-verified edition requires a frozen Worker owner recipe.");
        static string Required(JsonElement source, string name, int maximum)
        {
            if (!source.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
                value.GetString() is not { Length: > 0 } text || text.Length > maximum || text.Any(char.IsControl))
                throw new ArgumentException("The frozen owner recipe is missing its exact tuple or checker invocation.");
            return text;
        }
        var requestId = Required(recipe, "requestId", 128);
        if (requestId != snapshot.Run.Id || requestId != snapshot.Submission.RequestId ||
            Required(recipe, "conversationId", 128) != snapshot.Submission.ConversationId ||
            Required(recipe, "profileId", 128) != snapshot.Submission.ProfileId ||
            Required(recipe, "definitionId", 128) != snapshot.Submission.DefinitionId ||
            Required(recipe, "definitionVersion", 128) != OwnerVerifiedPreparedVersion)
            throw new ArgumentException("The frozen owner recipe belongs to another conversation run or definition.");
        var executableId = Required(recipe, "executableId", 64);
        var workingDirectory = Required(recipe, "workingDirectory", 4096);
        if (!CanonicalStoredPath(workingDirectory) ||
            !recipe.TryGetProperty("modelArguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() != 1 ||
            arguments[0].ValueKind != JsonValueKind.String ||
            arguments[0].GetString() is not { Length: > 0 and <= 4096 } checkerPath ||
            !CanonicalStoredPath(checkerPath))
            throw new ArgumentException("The frozen owner checker invocation is invalid.");
        // Reserializing the frozen typed recipe with the Worker's canonical options removes
        // its nesting indentation while preserving the admitted field order and values.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(recipe, CerebralJson.Options);
        if (canonical.Length > 16 * 1024)
            throw new ArgumentException("The frozen owner recipe exceeds its byte bound.");
        return new(Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant(), requestId,
            executableId, [checkerPath], workingDirectory);
    }

    private static string OwnerCheckDiagnostic(Input input)
    {
        var required = input.OwnerVerificationRequirement
            ?? throw new ArgumentException("The owner checker requirement is missing.");
        return "The current source build has no successful admitted owner checker receipt. " +
            "Run the exact host-frozen process.run checker before verification, presentation or final: " +
            JsonSerializer.Serialize(new { executableId = required.ExecutableId,
                arguments = required.ModelArguments, workingDirectory = required.WorkingDirectory,
                timeoutSeconds = 180, expectedExitCode = 0 }, Json) + ".";
    }

    private static bool HasCurrentOwnerCheck(Input input, HarnessState state)
    {
        if (!input.UseOwnerVerificationCompletion || input.OwnerVerificationRequirement is not { } required ||
            state.PluginWork is not { OwnerCheck: { } checkedWork } work ||
            checkedWork.SourceGeneration != work.SourceGeneration ||
            checkedWork.BuildEffectId != work.BuildEffectId ||
            checkedWork.ProviderId != "cerebral.owner-verification" ||
            work.SuccessfulBuildGeneration != work.SourceGeneration ||
            checkedWork.Receipt is not { } verification ||
            verification.RecipeSha256 != required.RecipeSha256 ||
            !ValidCurrentInputCheckReceipt(work, verification, verification.EffectId))
            return false;
        return state.ToolReceipts.Any(receipt => receipt.EffectId == checkedWork.Receipt.EffectId &&
            receipt.Name == "process.run" && receipt.State == nameof(WorkflowEffectState.Succeeded) &&
            receipt.Outcome == nameof(ToolExecutionOutcome.Succeeded) && receipt.ExitCode == 0 &&
            !receipt.Truncated);
    }

    // Only a complete Worker-authored checker receipt can preserve the current canonical
    // build after process.run. The ordinary process path retains its invalidation policy.
    private static bool ConfirmedCurrentInputCheck(Input input, PluginWorkBinding? work,
        WorkflowEffect effect, ToolData? data)
    {
        if (!input.UseRepeatedSuccessGuard || work is null ||
            work.SuccessfulBuildGeneration != work.SourceGeneration || work.BuildEffectId is null ||
            effect.State != WorkflowEffectState.Succeeded || data is not
            {
                Name: "process.run", Truncated: false,
                Evidence: { ProviderId: "cerebral.owner-verification", Outcome: ToolExecutionOutcome.Succeeded,
                    State: "completed", ExitCode: 0, Partial: not true },
                Verification: { SchemaVersion: 1, ProcessDispatched: true, Complete: true, ExitCode: 0 } receipt
            } ||
            input.UseOwnerVerificationCompletion &&
                receipt.RecipeSha256 != input.OwnerVerificationRequirement?.RecipeSha256 ||
            !ValidCurrentInputCheckReceipt(work, receipt, effect.Id))
            return false;
        return true;
    }

    private static bool ValidCurrentInputCheckReceipt(PluginWorkBinding work,
        OwnerVerificationReceipt receipt, string effectId)
    {
        if (receipt.SchemaVersion != 1 || !receipt.ProcessDispatched || !receipt.Complete || receipt.ExitCode != 0 ||
            receipt.EffectId != effectId || !Digest(receipt.RecipeSha256) ||
            !Digest(receipt.InputSha256) || !Digest(receipt.ReportSha256) ||
            receipt.Inputs is null || receipt.Inputs.Count != 7)
            return false;
        var roles = new[] { "targetSource", "targetProject", "targetSourceManifest", "targetManifest",
            "targetAssembly", "targetDeps", "targetSdk" };
        if (receipt.Inputs.Any(pin => pin is null) ||
            !roles.All(role => receipt.Inputs.Count(pin => pin.Role == role) == 1) ||
            receipt.Inputs.Select(pin => pin.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 7 ||
            receipt.Inputs.Any(pin => pin is null || pin.Path is null || !CanonicalStoredPath(pin.Path) ||
                !Digest(pin.Sha256) || pin.Bytes is < 1 or > 64 * 1024 * 1024))
            return false;
        var source = receipt.Inputs.Single(pin => pin.Role == "targetSource");
        var manifest = receipt.Inputs.Single(pin => pin.Role == "targetManifest");
        if (!PathWithin(source.Path, work.Build.Root) ||
            !PathEquals(manifest.Path, work.Build.BuiltManifest) ||
            work.Build.SourceFile is { } expectedSource && !PathEquals(source.Path, expectedSource))
            return false;
        foreach (var role in roles.Where(role => role != "targetSdk"))
            if (!PathWithin(receipt.Inputs.Single(pin => pin.Role == role).Path, work.Build.Root))
                return false;
        return InputIdentity(receipt.RecipeSha256, receipt.Inputs) == receipt.InputSha256;
    }

    private static bool Digest(string? value) => value is { Length: 64 } &&
        value.All(char.IsAsciiHexDigit);

    private static string InputIdentity(string recipeSha256, IReadOnlyList<VerificationInputPin> pins)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(recipeSha256);
        foreach (var pin in pins.OrderBy(item => item.Role, StringComparer.Ordinal))
        {
            Add(pin.Role);
            var path = Path.GetFullPath(pin.Path);
            Add(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path);
            Add(pin.Sha256.ToLowerInvariant());
            Add(pin.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Add(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
    }
}
