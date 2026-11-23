using System.Collections.ObjectModel;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

internal enum SourceRecoveryFailure
{
    manifest_invalid, manifest_changed, manifest_bound, path_invalid, path_missing, path_reparse,
    owner_mismatch, startup_invalid, installed_root_forbidden, baseline_invalid, baseline_changed,
    pin_invalid, pin_changed, pin_inventory_changed, initializer_review_invalid,
    store_unavailable, store_sidecars_invalid, store_changed, store_bound, settings_invalid,
    hold_mismatch, selection_mismatch, rows_mismatch, row_storage_invalid, row_bound,
    preflight_unavailable, resource_pause_required
}

/// <summary>Only an allowlisted code crosses the startup diagnostic boundary.</summary>
internal sealed class SourceRecoveryException : Exception
{
    internal SourceRecoveryException(SourceRecoveryFailure failure) : base(failure.ToString()) => Code = failure.ToString();
    public string Code { get; }
}

internal sealed record SourceRecoveryRoot(string Role, string Path);
internal sealed record SourceRecoveryFilePin(string Role, string RelativePath, long Length, string Sha256);
internal sealed record SourceRecoveryRowFingerprint(string Id, string Status, long Revision, string Sha256);
internal sealed record SourceRecoveryInitializerReview(string State, string? EvidencePath, string? EvidenceSha256,
    IReadOnlyList<SourceRecoveryFilePin> PackagePins);

/// <summary>
/// A task-specific, closed startup contract. It admits neither an ordinary installed host nor a
/// new baseline. Parsing a fixture manifest does not grant it production startup admission.
/// </summary>
internal sealed class SourceRecoveryManifest
{
    internal const string Schema = "cerebral.source-recovery/1";
    internal const string OriginalBaselineSha256 = "fa9acd914e1312d7b9c95c6b26ad4e611b7b46d66b10e40f89d4db2a307e4f83";
    internal const string OriginalInstallationId = "7d4e459937a64546a010778bbecac8d2";
    internal const string HoldId = "297e00ce8fb04042a1bdb4a16eda22a4";
    internal const string RequestId = "4cf414396e13448e8266635bffe2a768";
    internal const string FingerprintCodec = "ordered-column-triplets:column-name,python-value-type,value; blob=hex; ensure_ascii JSON compact UTF-8 SHA256; includes every selected workflow_runs column, excludes physical SQLite pages";
    internal const int MaximumManifestBytes = 256 * 1024;
    internal const int MaximumPins = 1024;
    internal static readonly string[] Roles = ["worker", "runtime", "sdk", "engine", "service"];

    private SourceRecoveryManifest(string path, byte[] bytes, JsonElement value)
    {
        ManifestPath = path; ManifestSha256 = Digest(bytes);
        RequireObject(value, ["schema", "sourceRoot", "ownerSid", "installationId", "pipeName", "pipeInstanceId",
            "settingsRevision", "settingsSha256", "holdSha256", "selectionSha256", "selectedPackageSha256",
            "selectedConfigurationSha256", "baselinePath", "baselineSha256", "roots", "files", "passiveInitializerReview"]);
        Require(Text(value, "schema", 64) == Schema, SourceRecoveryFailure.manifest_invalid);
        SourceRoot = CanonicalPath(Text(value, "sourceRoot", 1024));
        OwnerSid = Text(value, "ownerSid", 184);
        Require(OwnerSid.StartsWith("S-1-5-", StringComparison.Ordinal) &&
            OwnerSid.All(c => char.IsAsciiDigit(c) || c is 'S' or '-'), SourceRecoveryFailure.manifest_invalid);
        InstallationId = Identifier(value, "installationId");
        PipeName = Text(value, "pipeName", 160); PipeInstanceId = Identifier(value, "pipeInstanceId");
        try { new LocalWorkerEndpoint(PipeName, PipeInstanceId).Validate(); }
        catch (ArgumentException) { throw Error(SourceRecoveryFailure.manifest_invalid); }
        SettingsRevision = Integer(value, "settingsRevision", 1, int.MaxValue);
        SettingsSha256 = Hash(value, "settingsSha256"); HoldSha256 = Hash(value, "holdSha256");
        SelectionSha256 = Hash(value, "selectionSha256"); SelectedPackageSha256 = Hash(value, "selectedPackageSha256");
        SelectedConfigurationSha256 = Hash(value, "selectedConfigurationSha256");
        BaselinePath = CanonicalPath(Text(value, "baselinePath", 1024)); BaselineSha256 = Hash(value, "baselineSha256");
        var roots = Array(value, "roots", 5, 5).Select(item =>
        {
            RequireObject(item, ["role", "path"]);
            var role = Text(item, "role", 16);
            Require(Roles.Contains(role, StringComparer.Ordinal), SourceRecoveryFailure.pin_invalid);
            return new SourceRecoveryRoot(role, CanonicalPath(Text(item, "path", 1024)));
        }).ToArray();
        Require(roots.Select(x => x.Role).Distinct(StringComparer.Ordinal).Count() == Roles.Length &&
            roots.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Roles.Length,
            SourceRecoveryFailure.pin_invalid);
        Roots = System.Array.AsReadOnly(roots);
        var pins = Array(value, "files", 1, MaximumPins).Select(ReadPin).ToArray();
        Require(pins.Select(x => x.Role + ":" + x.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == pins.Length,
            SourceRecoveryFailure.pin_invalid);
        Require(pins.Select(Resolve).Distinct(StringComparer.OrdinalIgnoreCase).Count() == pins.Length,
            SourceRecoveryFailure.pin_invalid);
        Files = System.Array.AsReadOnly(pins);
        var review = value.GetProperty("passiveInitializerReview");
        RequireObject(review, ["state", "evidencePath", "evidenceSha256", "packagePins"]);
        var state = Text(review, "state", 16);
        Require(state is "deferred" or "reviewed", SourceRecoveryFailure.initializer_review_invalid);
        var evidencePath = NullableText(review, "evidencePath", 1024);
        var evidenceHash = NullableText(review, "evidenceSha256", 64);
        var reviewedPins = Array(review, "packagePins", 0, MaximumPins).Select(ReadPin).ToArray();
        Require(reviewedPins.All(x => x.Role is "engine" or "service") &&
            reviewedPins.Distinct().Count() == reviewedPins.Length &&
            reviewedPins.All(x => pins.Contains(x)), SourceRecoveryFailure.initializer_review_invalid);
        if (state == "deferred")
            Require(evidencePath is null && evidenceHash is null && reviewedPins.Length == 0,
                SourceRecoveryFailure.initializer_review_invalid);
        else
        {
            Require(evidencePath is not null && IsHash(evidenceHash), SourceRecoveryFailure.initializer_review_invalid);
            evidencePath = CanonicalPath(evidencePath!);
            var packagePins = pins.Where(x => x.Role is "engine" or "service").ToArray();
            Require(packagePins.Length > 0 && reviewedPins.Length == packagePins.Length &&
                packagePins.All(reviewedPins.Contains), SourceRecoveryFailure.initializer_review_invalid);
        }
        PassiveInitializerReview = new(state, evidencePath, evidenceHash, System.Array.AsReadOnly(reviewedPins));
    }

    public string ManifestPath { get; }
    public string ManifestSha256 { get; }
    public string SourceRoot { get; }
    public string OwnerSid { get; }
    public string InstallationId { get; }
    public string PipeName { get; }
    public string PipeInstanceId { get; }
    public long SettingsRevision { get; }
    public string SettingsSha256 { get; }
    public string HoldSha256 { get; }
    public string SelectionSha256 { get; }
    public string SelectedPackageSha256 { get; }
    public string SelectedConfigurationSha256 { get; }
    public string BaselinePath { get; }
    public string BaselineSha256 { get; }
    public IReadOnlyList<SourceRecoveryRoot> Roots { get; }
    public IReadOnlyList<SourceRecoveryFilePin> Files { get; }
    public SourceRecoveryInitializerReview PassiveInitializerReview { get; }

    public static SourceRecoveryManifest ParseFile(string path)
    {
        try
        {
            path = CanonicalPath(path);
            var bytes = ReadBounded(path, MaximumManifestBytes);
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            return new(path, bytes, document.RootElement);
        }
        catch (SourceRecoveryException) { throw; }
        catch (Exception ex) when (Ordinary(ex)) { throw Error(SourceRecoveryFailure.manifest_invalid); }
    }

    // The caller owns service.lock and invokes Verify before any StateStore/Configuration/initializer.
    public void ValidateStartup(IReadOnlyList<string> args, string exactResolvedDataPath,
        LocalWorkerEndpoint? pipeEndpoint, string workerExecutablePath)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw Error(SourceRecoveryFailure.startup_invalid);
            Require(Environment.Is64BitProcess, SourceRecoveryFailure.startup_invalid);
            ValidateCurrentOwner();
            Require(SamePath(SourceRoot, CanonicalPath(exactResolvedDataPath)), SourceRecoveryFailure.startup_invalid);
            Require(!SamePath(SourceRoot, CanonicalPath(InstallationPaths.DataDirectory)),
                SourceRecoveryFailure.installed_root_forbidden);
            CheckExistingPath(SourceRoot, true);
            var owner = new DirectoryInfo(SourceRoot).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            Require(owner?.Value == OwnerSid, SourceRecoveryFailure.owner_mismatch);
            Require(pipeEndpoint is not null && pipeEndpoint.PipeName == PipeName && pipeEndpoint.InstanceId == PipeInstanceId,
                SourceRecoveryFailure.startup_invalid);
            Require(args.Count is >= 13 and <= 17, SourceRecoveryFailure.startup_invalid);
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var local = false;
            for (var i = 0; i < args.Count; i++)
            {
                var option = args[i];
                if (option == "--local-run") { Require(!local, SourceRecoveryFailure.startup_invalid); local = true; continue; }
                Require(option is "--source-recovery-manifest" or "--data" or "--pipe-name" or "--pipe-instance" or
                    "--engines" or "--service-plugins" or "--desktop-sdk", SourceRecoveryFailure.startup_invalid);
                Require(++i < args.Count && !args[i].StartsWith("--", StringComparison.Ordinal) &&
                    options.TryAdd(option, args[i]), SourceRecoveryFailure.startup_invalid);
            }
            Require(local && options.Count == 7, SourceRecoveryFailure.startup_invalid);
            var sdk = Files.SingleOrDefault(x => x.Role == "sdk" && x.RelativePath == "Cerebral.Desktop.Plugins.dll");
            Require(sdk is not null, SourceRecoveryFailure.pin_invalid);
            Require(SamePath(CanonicalPath(options["--source-recovery-manifest"]), ManifestPath) &&
                SamePath(CanonicalPath(options["--data"]), SourceRoot) &&
                options["--pipe-name"] == PipeName && options["--pipe-instance"] == PipeInstanceId &&
                SamePath(CanonicalPath(options["--engines"]), Root("engine")) &&
                SamePath(CanonicalPath(options["--service-plugins"]), Root("service")) &&
                SamePath(CanonicalPath(options["--desktop-sdk"]), Resolve(sdk!)),
                SourceRecoveryFailure.startup_invalid);
            var executable = CanonicalPath(workerExecutablePath);
            var exe = Files.SingleOrDefault(x => x.Role == "worker" && x.RelativePath == "Cerebral.Worker.exe");
            Require(exe is not null && SamePath(executable, Resolve(exe)), SourceRecoveryFailure.pin_invalid);
            foreach (var name in new[] { "Cerebral.Worker.dll", "Cerebral.Worker.deps.json", "Cerebral.Worker.runtimeconfig.json" })
                Require(Files.Any(x => x.Role == "worker" && x.RelativePath == name), SourceRecoveryFailure.pin_invalid);
            foreach (var name in new[] { "Cerebral.Core.dll", "Cerebral.Contracts.dll", "Cerebral.Engine.Contracts.dll", "Cerebral.Desktop.Plugins.dll" })
                Require(Files.Any(x => x.Role == "sdk" && x.RelativePath == name), SourceRecoveryFailure.pin_invalid);
            Require(Files.Any(x => x.Role == "runtime" && x.RelativePath == "llama-server.exe") && Files.Any(x => x.Role == "engine") &&
                Files.Any(x => x.Role == "service"), SourceRecoveryFailure.pin_invalid);
            Require(Files.Any(x => x.Role == "engine" && x.RelativePath == "A/engine.ini" && x.Sha256 == SelectedPackageSha256) &&
                Files.Any(x => x.Role == "engine" && x.RelativePath == "A/engine-settings.ini" && x.Sha256 == SelectedConfigurationSha256),
                SourceRecoveryFailure.pin_invalid);
            Require(InstallationId == OriginalInstallationId && BaselineSha256 == OriginalBaselineSha256,
                SourceRecoveryFailure.baseline_invalid);
            RequireUnchanged();
            _ = ReadBaseline();
        }
        catch (SourceRecoveryException) { throw; }
        catch (Exception ex) when (Ordinary(ex)) { throw Error(SourceRecoveryFailure.startup_invalid); }
    }

    internal void ValidateCurrentOwner()
    {
        if (!OperatingSystem.IsWindows()) throw Error(SourceRecoveryFailure.owner_mismatch);
        using var identity = WindowsIdentity.GetCurrent();
        Require(!identity.IsSystem && identity.User?.Value == OwnerSid, SourceRecoveryFailure.owner_mismatch);
    }

    internal void RequireUnchanged()
    {
        Require(Digest(ReadBounded(ManifestPath, MaximumManifestBytes)) == ManifestSha256,
            SourceRecoveryFailure.manifest_changed);
    }

    internal IReadOnlyList<SourceRecoveryRowFingerprint> ReadBaseline()
    {
        var bytes = ReadBounded(BaselinePath, 128 * 1024);
        Require(Digest(bytes) == BaselineSha256, SourceRecoveryFailure.baseline_changed);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        var value = document.RootElement;
        RejectDuplicateProperties(value);
        Require(Text(value, "schema", 64) == "cerebral.core.source-retained-state/1" &&
            Text(value, "fingerprintCodec", 512) == FingerprintCodec &&
            SamePath(CanonicalPath(Text(value.GetProperty("projection"), "source_root", 1024)), SourceRoot),
            SourceRecoveryFailure.baseline_invalid);
        var configuration = value.GetProperty("projection").GetProperty("settings");
        Require(Identifier(configuration, "installation_id") == InstallationId, SourceRecoveryFailure.baseline_invalid);
        var rows = Array(value, "storageRowFingerprints", 14, 14).Select(item =>
        {
            RequireObject(item, ["id", "status", "revision", "sha256"]);
            var id = Text(item, "id", 512);
            Require(id.All(c => !char.IsControl(c)), SourceRecoveryFailure.baseline_invalid);
            Require(Text(item, "status", 32) == "Waiting", SourceRecoveryFailure.baseline_invalid);
            return new SourceRecoveryRowFingerprint(id, "Waiting", Integer(item, "revision", 1, long.MaxValue), Hash(item, "sha256"));
        }).ToArray();
        Require(rows.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() == 14 &&
            rows.Select(x => x.Id).SequenceEqual(rows.Select(x => x.Id).Order(StringComparer.Ordinal)),
            SourceRecoveryFailure.baseline_invalid);
        return System.Array.AsReadOnly(rows);
    }

    internal string Root(string role) => Roots.Single(x => x.Role == role).Path;
    internal string Resolve(SourceRecoveryFilePin pin)
    {
        var root = Root(pin.Role);
        var result = CanonicalPath(Path.Combine(root, pin.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        Require(IsWithin(result, root), SourceRecoveryFailure.pin_invalid);
        return result;
    }

    private static SourceRecoveryFilePin ReadPin(JsonElement value)
    {
        RequireObject(value, ["role", "relativePath", "length", "sha256"]);
        var role = Text(value, "role", 16);
        Require(Roles.Contains(role, StringComparer.Ordinal), SourceRecoveryFailure.pin_invalid);
        var path = Text(value, "relativePath", 512);
        Require(!Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') &&
            path.Split('/').All(x => x.Length > 0 && x is not ("." or "..") && x.All(c => !char.IsControl(c))),
            SourceRecoveryFailure.pin_invalid);
        return new(role, path, Integer(value, "length", 1, 512L * 1024 * 1024), Hash(value, "sha256"));
    }

    internal static string CanonicalPath(string value)
    {
        Require(value.Length is > 3 and <= 1024 && Path.IsPathFullyQualified(value) &&
            !value.StartsWith("\\\\", StringComparison.Ordinal) && !value.Any(char.IsControl) &&
            !value.Split(['/', '\\']).Any(x => x is "." or ".."), SourceRecoveryFailure.path_invalid);
        var result = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Require(result.Length > 3 && result[1] == ':' && !result[2..].Contains(':'),
            SourceRecoveryFailure.path_invalid);
        var drive = new DriveInfo(Path.GetPathRoot(result)!);
        Require(drive.DriveType is DriveType.Fixed or DriveType.Ram, SourceRecoveryFailure.path_invalid);
        return result;
    }

    internal static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    internal static bool IsWithin(string path, string root) => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void CheckExistingPath(string path, bool directory = false)
    {
        path = CanonicalPath(path);
        var attributes = File.GetAttributes(path);
        Require((attributes & FileAttributes.ReparsePoint) == 0, SourceRecoveryFailure.path_reparse);
        Require(((attributes & FileAttributes.Directory) != 0) == directory, SourceRecoveryFailure.path_invalid);
        var parent = directory ? path : Path.GetDirectoryName(path);
        while (parent is not null)
        {
            Require((File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0, SourceRecoveryFailure.path_reparse);
            parent = Path.GetDirectoryName(parent);
        }
    }

    internal static byte[] ReadBounded(string path, int maximum)
    {
        CheckExistingPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(stream.Length is > 0 && stream.Length <= maximum, SourceRecoveryFailure.manifest_bound);
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        Require(stream.ReadByte() == -1, SourceRecoveryFailure.manifest_bound);
        CheckExistingPath(path);
        return bytes;
    }

    internal static string Digest(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string Hash(JsonElement value, string name)
    {
        var result = Text(value, name, 64);
        Require(IsHash(result), SourceRecoveryFailure.manifest_invalid); return result;
    }
    internal static string Identifier(JsonElement value, string name)
    {
        var result = Text(value, name, 32);
        Require(Guid.TryParseExact(result, "N", out var id) && id != Guid.Empty && result == id.ToString("N"),
            SourceRecoveryFailure.manifest_invalid); return result;
    }
    internal static string Text(JsonElement value, string name, int maximum)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
            item.ValueKind == JsonValueKind.String, SourceRecoveryFailure.manifest_invalid);
        var result = value.GetProperty(name).GetString()!;
        Require(result.Length is > 0 && result.Length <= maximum, SourceRecoveryFailure.manifest_invalid); return result;
    }
    internal static string? NullableText(JsonElement value, string name, int maximum) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(value, name, maximum);
    internal static long Integer(JsonElement value, string name, long minimum, long maximum)
    {
        Require(value.GetProperty(name).ValueKind == JsonValueKind.Number && value.GetProperty(name).TryGetInt64(out var number) &&
            number >= minimum && number <= maximum, SourceRecoveryFailure.manifest_invalid);
        return value.GetProperty(name).GetInt64();
    }
    internal static IEnumerable<JsonElement> Array(JsonElement value, string name, int minimum, int maximum)
    {
        var array = value.GetProperty(name);
        Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() >= minimum &&
            array.GetArrayLength() <= maximum, SourceRecoveryFailure.manifest_invalid);
        return array.EnumerateArray();
    }
    internal static void RequireObject(JsonElement value, IReadOnlyList<string> keys)
    {
        Require(value.ValueKind == JsonValueKind.Object &&
            value.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)),
            SourceRecoveryFailure.manifest_invalid);
    }
    internal static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            Require(value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() ==
                value.EnumerateObject().Count(), SourceRecoveryFailure.manifest_invalid);
            foreach (var property in value.EnumerateObject()) RejectDuplicateProperties(property.Value);
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
    internal static void Require(bool condition, SourceRecoveryFailure failure) { if (!condition) throw Error(failure); }
    internal static SourceRecoveryException Error(SourceRecoveryFailure failure) => new(failure);
    internal static bool Ordinary(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    public override string ToString() => Schema + ":" + ManifestSha256;
}
