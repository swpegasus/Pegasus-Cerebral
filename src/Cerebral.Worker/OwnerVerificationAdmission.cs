using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>A direct owner freezes one checker recipe. Model arguments can select only this exact
/// invocation; they cannot add files, executable paths, inputs or a failure allowance.</summary>
internal sealed record OwnerVerificationAdmission(
    int SchemaVersion, string RecipeId, string RequestId, string ConversationId, string ProfileId,
    string DefinitionId, string DefinitionVersion, string ExecutableId, string ExecutablePath,
    IReadOnlyList<string> PrefixArguments, IReadOnlyList<string> ModelArguments, string WorkingDirectory,
    string CheckerRoot, string TargetRoot, IReadOnlyList<OwnerVerificationAdmission.File> Files,
    OwnerVerificationAdmission.Contract CheckerContract, int MaximumFailuresPerInput)
{
    internal const int CurrentSchemaVersion = 2;
    internal const int CheckerTimeoutSeconds = 180;
    internal enum Scope { Checker, Target }
    internal sealed record File(string Role, Scope Scope, string RelativePath, string OriginalPath,
        string? Sha256 = null, long? Bytes = null);
    // The owner pins this checker's concrete protocol and role map. The host validates the
    // relationship among those facts, staged handles and output without knowing the project.
    internal sealed record Contract(string DescriptorSchema, string ClosureSchema, string CheckerVersion,
        string SummarySchema, string ReportSchema, string EntryRole,
        IReadOnlyList<string> LoadedRoles, IReadOnlyList<string> AssertionNames);

    internal string RecipeSha256 => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(this, CerebralJson.Options))).ToLowerInvariant();

    internal bool MatchesProcessRun(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not
                ("executableId" or "arguments" or "workingDirectory" or "timeoutSeconds" or "expectedExitCode"))
                return false;
        if (!arguments.TryGetProperty("executableId", out var id) || id.ValueKind != JsonValueKind.String ||
            id.GetString() != ExecutableId ||
            !arguments.TryGetProperty("arguments", out var argv) || argv.ValueKind != JsonValueKind.Array ||
            !arguments.TryGetProperty("workingDirectory", out var cwd) || cwd.ValueKind != JsonValueKind.String ||
            cwd.GetString() != WorkingDirectory)
            return false;
        var values = argv.EnumerateArray().ToArray();
        if (values.Length != ModelArguments.Count ||
            values.Where((item, index) => item.ValueKind != JsonValueKind.String ||
                item.GetString() != ModelArguments[index]).Any()) return false;
        if (arguments.TryGetProperty("expectedExitCode", out var exit) &&
            (exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out var code) || code != 0)) return false;
        return arguments.TryGetProperty("timeoutSeconds", out var timeout) &&
            timeout.ValueKind == JsonValueKind.Number && timeout.TryGetInt32(out var seconds) &&
            seconds == CheckerTimeoutSeconds;
    }

    // Reserve the owner-selected checker image even when the model supplies an invalid
    // invocation. Otherwise a near match falls through to ordinary process.run and can
    // launch the checker without its host-only staged arguments.
    internal bool ReservesProcessRun(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var executable = false;
        var checkerImage = false;
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name == "executableId" && property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString() == ExecutableId)
                executable = true;
            if (property.Name == "arguments" && property.Value.ValueKind == JsonValueKind.Array)
            {
                var values = property.Value.EnumerateArray();
                if (values.MoveNext() && values.Current.ValueKind == JsonValueKind.String &&
                    values.Current.GetString() == ModelArguments[0])
                    checkerImage = true;
            }
        }
        // Inspect every occurrence so duplicate JSON properties cannot hide a checker
        // reference behind the last value chosen by ordinary argument parsing.
        return executable && checkerImage;
    }

    internal static OwnerVerificationAdmission Read(JsonElement value)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value).Length > 16 * 1024)
            throw new InvalidDataException("Owner verification admission exceeds its byte bound.");
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("schemaVersion", out var edition) ||
            edition.ValueKind != JsonValueKind.Number || !edition.TryGetInt32(out var number) ||
            number != CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported owner verification recipe contract; submit a fresh owner-reviewed edition-two recipe.");
        Exact(value, "schemaVersion", "recipeId", "requestId", "conversationId", "profileId",
            "definitionId", "definitionVersion", "executableId", "executablePath", "prefixArguments",
            "modelArguments", "workingDirectory", "checkerRoot", "targetRoot", "files",
            "checkerContract", "maximumFailuresPerInput");
        if (value.GetProperty("files").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Owner verification files must be an array.");
        foreach (var file in value.GetProperty("files").EnumerateArray())
            Exact(file, ["role", "scope", "relativePath", "originalPath", "sha256", "bytes"], ["sha256", "bytes"]);
        Exact(value.GetProperty("checkerContract"), "descriptorSchema", "closureSchema", "checkerVersion",
            "summarySchema", "reportSchema", "entryRole", "loadedRoles", "assertionNames");
        var admission = value.Deserialize<OwnerVerificationAdmission>(CerebralJson.Options)
            ?? throw new InvalidDataException("Owner verification admission is missing.");
        admission.Validate();
        return admission;
    }

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported owner verification recipe contract; submit a fresh owner-reviewed edition-two recipe.");
        if (RecipeId is not { Length: > 0 and <= 128 } ||
            RecipeId.Any(char.IsControl) || !ValidId(RequestId) || !ValidId(ConversationId) ||
            !ValidId(ProfileId) || !ValidId(DefinitionId) || !ValidId(DefinitionVersion) ||
            string.IsNullOrWhiteSpace(ExecutableId) || ExecutableId.Length > 64 ||
            !ExecutableId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ||
            !ExactPath(ExecutablePath) || !string.Equals(Path.GetFileName(ExecutablePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            !ExactPath(WorkingDirectory) || !ExactPath(CheckerRoot) || !ExactPath(TargetRoot) ||
            CheckerRoot.Equals(TargetRoot, StringComparison.OrdinalIgnoreCase) ||
            PrefixArguments is null || PrefixArguments.Count != 0 || ModelArguments is null || ModelArguments.Count != 1 ||
            PrefixArguments.Concat(ModelArguments).Any(argument => argument is null || argument.Length > 4096 || argument.Contains('\0')) ||
            Files is null || Files.Count is < 1 or > 512 || CheckerContract is null ||
            MaximumFailuresPerInput != OwnerVerificationBudget.MaximumFailuresPerInput)
            throw new InvalidDataException("Owner verification recipe identity, executable or bounded arguments are invalid.");
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var originals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var staged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null || file.Scope is not (Scope.Checker or Scope.Target) ||
                file.Role is not { Length: > 0 and <= 64 } ||
                !file.Role.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ||
                !roles.Add(file.Role) || !ExactPath(file.OriginalPath) || !originals.Add(file.OriginalPath) ||
                !RelativePath(file.RelativePath) || !staged.Add(file.Scope + ":" + file.RelativePath) ||
                file.Scope == Scope.Checker && !Within(file.OriginalPath, CheckerRoot) ||
                file.Scope == Scope.Target && !Within(file.OriginalPath, TargetRoot) ||
                file.Scope == Scope.Checker && (file.Sha256 is not { Length: 64 } ||
                    file.Sha256.Any(character => !char.IsAsciiHexDigit(character)) || file.Bytes is < 1 or > 64 * 1024 * 1024) ||
                file.Scope == Scope.Target && (file.Sha256 is not null || file.Bytes is not null))
                throw new InvalidDataException("Owner verification file closure is missing, duplicated or outside its root.");
        }
        // Original files may have build-output paths below their admitted source root. The
        // owner-selected relative path names their private staged location, not the source
        // location; the stage copies only these exact retained handles into that closure.
        var contract = CheckerContract;
        if (!ContractText(contract.DescriptorSchema, 256) || !ContractText(contract.ClosureSchema, 256) ||
            !ContractText(contract.CheckerVersion, 64) ||
            !ContractText(contract.SummarySchema, 256) || !ContractText(contract.ReportSchema, 256) ||
            !RoleName(contract.EntryRole) ||
            Files.Count(file => file.Role == contract.EntryRole && file.Scope == Scope.Checker &&
                file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) != 1 ||
            ModelArguments[0] != Files.Single(file => file.Role == contract.EntryRole).OriginalPath ||
            contract.LoadedRoles is null || contract.LoadedRoles.Count is < 1 or > 512 ||
            contract.LoadedRoles.Distinct(StringComparer.Ordinal).Count() != contract.LoadedRoles.Count ||
            contract.LoadedRoles.Any(role => !RoleName(role) ||
                Files.Count(file => file.Role == role && file.Scope == Scope.Target &&
                    file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) != 1) ||
            contract.AssertionNames is null || contract.AssertionNames.Count < 1 ||
            contract.AssertionNames.Distinct(StringComparer.Ordinal).Count() != contract.AssertionNames.Count ||
            contract.AssertionNames.Any(name => name is not { Length: > 0 and <= 128 } ||
                name.Any(char.IsControl)))
            throw new InvalidDataException("Owner verification checker contract is missing, duplicated or inconsistent with its pinned closure.");
        // This is the same 16 KiB admission bound enforced for wire reads. It also bounds
        // all variable-length role and assertion collections constructed in process.
        if (JsonSerializer.SerializeToUtf8Bytes(this, CerebralJson.Options).Length > 16 * 1024)
            throw new InvalidDataException("Owner verification admission exceeds its byte bound.");
    }

    private static bool ContractText(string? value, int maximum) => value is { Length: > 0 } &&
        value.Length <= maximum && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    private static bool RoleName(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static void Exact(JsonElement value, params string[] required) => Exact(value, required, []);
    private static void Exact(JsonElement value, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.EnumerateObject().Count() ||
            required.Except(optional).Any(name => !value.TryGetProperty(name, out _)) ||
            value.EnumerateObject().Any(property => !required.Contains(property.Name, StringComparer.Ordinal)))
            throw new InvalidDataException("Owner verification admission has missing, duplicate or unknown fields.");
    }

    private static bool ExactPath(string? path) => path is { Length: > 0 and <= 4096 } &&
        Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path &&
        !path.StartsWith("\\\\", StringComparison.Ordinal) && !path.StartsWith("\\\\?\\", StringComparison.Ordinal) &&
        !path.AsSpan(2).Contains(':');

    private static bool RelativePath(string? path) => path is { Length: > 0 and <= 512 } &&
        !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(part => part is not ("" or "." or ".."));

    private static bool Within(string path, string root) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } &&
        !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));
}
