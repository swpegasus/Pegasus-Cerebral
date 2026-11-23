using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class ServicePackageLifecycle
{
    private sealed record OfflineRecoveryIntent(string Schema, string RequestId, string RequestSha256,
        string PackageId, long ExpectedRevision, string ExpectedLifecycleSha256,
        string ExpectedGeneration, string RestoredGeneration, string RestoredFingerprint);
    private sealed record OfflineRecoveryPreparedResult(string Schema, string RequestId,
        string RequestSha256, string PackageId, long ResultRevision, string ResultVersion,
        string ResultGeneration, string ResultFingerprint);
    private static readonly JsonSerializerOptions RecoveryJournalJson = new(CerebralJson.Options)
    {
        PropertyNameCaseInsensitive = false, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static ServicePackageOfflineRecoveryResult RecoverOffline(
        ServicePackageOfflineRecoveryRequest request, string requestSha256)
    {
        ServicePackageOfflineRecovery.RequireHash(requestSha256);
        var root = ServicePackageOfflineRecovery.ExistingDirectory(request.RepositoryRoot);
        var lifecycle = ServicePackageOfflineRecovery.ExistingDirectory(Path.Combine(root, LifecycleDirectoryName));
        var recoveryDirectory = Path.Combine(lifecycle, ".recovery");
        if (Directory.Exists(recoveryDirectory)) ServicePackageOfflineRecovery.RejectLinks(recoveryDirectory);
        var generation = SafeGeneration(request.RestoreVersion + "-recovery-" + request.RequestId);
        var destination = Path.Combine(root, PackagesDirectoryName, request.PackageId, generation);
        var intentPath = Path.Combine(recoveryDirectory, request.RequestId + ".intent.json");
        var preparedPath = Path.Combine(recoveryDirectory, request.RequestId + ".prepared-result.json");
        var intent = new OfflineRecoveryIntent("cerebral.service-package-offline-recovery-intent/1",
            request.RequestId, requestSha256, request.PackageId, request.ExpectedCurrentRevision,
            request.ExpectedCurrentLifecycleSha256, request.ExpectedCurrentGeneration,
            generation, request.PreservedPackageFingerprint);
        var prepared = new OfflineRecoveryPreparedResult("cerebral.service-package-offline-recovery-prepared/1",
            request.RequestId, requestSha256, request.PackageId,
            checked(request.ExpectedCurrentRevision + 1), request.RestoreVersion, generation,
            request.PreservedPackageFingerprint);
        // This check reads both durable stores without invoking their write-capable constructors.
        SourceRecoveryPreflight.VerifyOfflinePackageRecoveryWork(request);
        var current = ReadStateAt(root, request.PackageId)
            ?? throw new InvalidDataException("The selected service package lifecycle state is missing.");
        VerifyPinnedPackage(request.PreservedPackageRoot, request.PreservedFiles,
            request.PreservedPackageFingerprint, request.PackageId, request.RestoreVersion);
        VerifySelectedGraph(request, root);
        if (File.Exists(intentPath))
        {
            // Never redrive an ambiguous prior attempt. Only an exact intent, prepared
            // result, selected state and restored byte closure make a lost reply reconcilable.
            var savedIntent = ReadJournal<OfflineRecoveryIntent>(intentPath);
            if (savedIntent != intent || !File.Exists(preparedPath) ||
                ReadJournal<OfflineRecoveryPreparedResult>(preparedPath) != prepared ||
                current.Revision != prepared.ResultRevision || !current.DesiredActive ||
                current.DesiredVersion != prepared.ResultVersion ||
                current.DesiredGeneration != prepared.ResultGeneration ||
                current.StagedVersion is not null || current.StagedGeneration is not null ||
                !Directory.Exists(destination))
                throw new InvalidOperationException("A previous offline package recovery effect is uncertain; inspect its intent, prepared result and lifecycle state.");
            VerifyPinnedPackage(destination, request.PreservedFiles, request.PreservedPackageFingerprint,
                request.PackageId, request.RestoreVersion);
            return Result(request, prepared, intentPath, preparedPath);
        }
        if (File.Exists(preparedPath) || Directory.Exists(destination))
            throw new InvalidOperationException("An unbound recovery result or generation exists; inspect it before a new attempt.");
        RequireOriginalState(request, root, current);
        if (Directory.Exists(recoveryDirectory))
        {
            var conflicts = Directory.EnumerateFiles(recoveryDirectory, "*.intent.json", SearchOption.TopDirectoryOnly)
                .Take(257).ToArray();
            if (conflicts.Length > 256) throw new InvalidDataException("The recovery intent journal exceeds its bound.");
            foreach (var path in conflicts)
            {
                var prior = ReadJournal<OfflineRecoveryIntent>(path);
                if (prior.PackageId == request.PackageId && prior.ExpectedRevision == request.ExpectedCurrentRevision)
                    throw new InvalidOperationException("Another recovery intent already owns this lifecycle revision.");
            }
        }
        Directory.CreateDirectory(recoveryDirectory);
        ServicePackageOfflineRecovery.RejectLinks(recoveryDirectory);
        WriteJournalCreateNew(intentPath, intent);
        // Any interruption after this point retains the intent. A retry inspects only.
        var incoming = Path.Combine(root, ".incoming-" + request.PackageId + "-" + request.RequestId);
        if (Directory.Exists(incoming) || Directory.Exists(destination))
            throw new InvalidOperationException("A prior package copy exists after intent creation; inspect it.");
        Directory.CreateDirectory(incoming);
        foreach (var pin in request.PreservedFiles.OrderBy(pin => pin.RelativePath, StringComparer.Ordinal))
        {
            var relative = pin.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.Combine(incoming, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(request.PreservedPackageRoot, relative), target, overwrite: false);
        }
        VerifyPinnedPackage(incoming, request.PreservedFiles, request.PreservedPackageFingerprint,
            request.PackageId, request.RestoreVersion);
        var packageStore = Path.Combine(root, PackagesDirectoryName, request.PackageId);
        Directory.CreateDirectory(packageStore);
        ServicePackageOfflineRecovery.RejectLinks(packageStore);
        Directory.Move(incoming, destination);
        VerifyPinnedPackage(destination, request.PreservedFiles, request.PreservedPackageFingerprint,
            request.PackageId, request.RestoreVersion);
        WriteJournalCreateNew(preparedPath, prepared);
        // The service lock excludes a compliant Worker, but direct file edits still must
        // be detected before the one lifecycle state replacement.
        SourceRecoveryPreflight.VerifyOfflinePackageRecoveryWork(request);
        VerifyPinnedPackage(request.PreservedPackageRoot, request.PreservedFiles,
            request.PreservedPackageFingerprint, request.PackageId, request.RestoreVersion);
        VerifySelectedGraph(request, root);
        current = ReadStateAt(root, request.PackageId)
            ?? throw new InvalidDataException("The lifecycle state disappeared before recovery selection.");
        RequireOriginalState(request, root, current);
        var next = current with
        {
            Revision = prepared.ResultRevision,
            DesiredVersion = prepared.ResultVersion,
            DesiredGeneration = prepared.ResultGeneration
        };
        WriteStateAt(root, next);
        if (ReadStateAt(root, request.PackageId) != next)
            throw new IOException("The recovery selection write has an unconfirmed readback.");
        return Result(request, prepared, intentPath, preparedPath);
    }

    private static void RequireOriginalState(ServicePackageOfflineRecoveryRequest request,
        string root, LifecycleState state)
    {
        RequireRevision(state, request.ExpectedCurrentRevision);
        var path = ServicePackageOfflineRecovery.ExistingFile(Path.Combine(root, LifecycleDirectoryName,
            request.PackageId + ".json"));
        if (ServicePackageOfflineRecovery.FileHash(path) != request.ExpectedCurrentLifecycleSha256 ||
            !state.DesiredActive || state.DesiredVersion != request.ExpectedCurrentVersion ||
            state.DesiredGeneration != request.ExpectedCurrentGeneration ||
            state.StagedVersion is not null || state.StagedGeneration is not null)
            throw new InvalidOperationException("The selected service package lifecycle bytes or desired tuple changed.");
    }

    private static void VerifySelectedGraph(ServicePackageOfflineRecoveryRequest request, string root)
    {
        var restoreId = request.PackageId;
        var selected = ResolveRepository(root);
        if (selected.Count > 128 || selected.Any(row => row.Id != restoreId && row.Error is not null))
            throw new InvalidDataException("The selected foreign service package graph cannot be proven.");
        var packages = new Dictionary<string, ServicePluginManifest>(StringComparer.Ordinal);
        foreach (var item in selected.Where(row => row.Id != restoreId))
            packages.Add(item.Id, ServicePluginRuntime.InspectPackage(item.Directory,
                Path.Combine(item.Directory, ServicePluginRuntime.ManifestFileName), item.Id,
                requireMatchingDirectory: false));
        var target = selected.SingleOrDefault(row => row.Id == restoreId);
        if (target is null)
            throw new InvalidDataException("The selected service package lifecycle is absent from the repository.");
        var restored = ServicePluginRuntime.InspectPackage(request.PreservedPackageRoot,
            Path.Combine(request.PreservedPackageRoot, ServicePluginRuntime.ManifestFileName),
            restoreId, requireMatchingDirectory: false);
        packages.Add(restoreId, restored);
        // A broken/missing newly selected target is an expected recovery reason. Its bytes
        // are left untouched and never used as admission for the restored old package.
        foreach (var manifest in packages.Values)
            foreach (var dependency in manifest.Dependencies.Where(dependency => !dependency.Optional))
                if (!packages.TryGetValue(dependency.Id, out var required) ||
                    !ServicePluginRuntime.DependencyCompatible(dependency, required.Version))
                    throw new InvalidOperationException("The restored selected service package graph has an unavailable or incompatible required dependency.");
        if (ServicePluginRuntime.DependencyCycles(packages).Count != 0)
            throw new InvalidOperationException("The restored selected service package graph has a dependency cycle.");
    }

    private static ServicePackageOfflineRecoveryResult Result(ServicePackageOfflineRecoveryRequest request,
        OfflineRecoveryPreparedResult prepared, string intentPath, string preparedPath) =>
        new("cerebral.service-package-offline-recovery-result/1", request.RequestId,
            request.PackageId, prepared.ResultRevision, prepared.ResultVersion, prepared.ResultGeneration,
            prepared.ResultFingerprint, intentPath, preparedPath, "applied");

    private static void VerifyPinnedPackage(string directory, OfflineRecoveryFilePin[] pins,
        string fingerprint, string id, string version)
    {
        ServicePackageOfflineRecovery.ExistingDirectory(directory);
        var files = PackageFiles(directory);
        if (files.Count != pins.Length) throw new InvalidDataException("The package file closure changed.");
        foreach (var pin in pins)
        {
            var path = Path.Combine(directory, pin.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!files.Contains(path, StringComparer.OrdinalIgnoreCase) ||
                new FileInfo(path).Length != pin.Bytes ||
                ServicePackageOfflineRecovery.FileHash(path) != pin.Sha256)
                throw new InvalidDataException("A pinned package file changed.");
        }
        var manifest = ServicePluginRuntime.InspectPackage(directory,
            Path.Combine(directory, ServicePluginRuntime.ManifestFileName), id,
            requireMatchingDirectory: false);
        if (manifest.Version != version || FingerprintPackage(directory) != fingerprint)
            throw new InvalidDataException("The preserved package is not the reviewed version.");
    }

    private static T ReadJournal<T>(string path)
    {
        ServicePackageOfflineRecovery.ExistingFile(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 2 or > 16_384) throw new InvalidDataException("The recovery journal file exceeds its bound.");
        using (var document = JsonDocument.Parse(bytes))
            if (document.RootElement.EnumerateObject().Select(property => property.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.RootElement.EnumerateObject().Count())
                throw new InvalidDataException("The recovery journal has duplicate fields.");
        return JsonSerializer.Deserialize<T>(bytes, RecoveryJournalJson)
            ?? throw new InvalidDataException("The recovery journal file is empty.");
    }

    private static void WriteJournalCreateNew<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, RecoveryJournalJson);
        if (bytes.Length is < 2 or > 16_384) throw new InvalidDataException("The recovery journal entry exceeds its bound.");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }
}
