using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class ModelRepositoryTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-model-tests-" + Guid.NewGuid().ToString("N")));
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
        bool Rejects(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
        byte[] MinimalGguf(string marker = "")
        {
            using var data = new MemoryStream(); using var writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("GGUF")); writer.Write((uint)3); writer.Write((ulong)0); writer.Write((ulong)0);
            writer.Write(Encoding.UTF8.GetBytes(marker)); writer.Flush(); return data.ToArray();
        }
        byte[] TypedAdapterGguf()
        {
            using var data = new MemoryStream(); using var writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("GGUF")); writer.Write((uint)3); writer.Write((ulong)0); writer.Write((ulong)4);
            WriteText(writer, "general.name", "Story Writer Helper");
            WriteText(writer, "general.architecture", "llama");
            WriteText(writer, "general.type", "ADAPTER");
            WriteText(writer, "general.description", "A coding and creative-writing helper");
            writer.Flush(); return data.ToArray();
        }
        void WriteText(BinaryWriter writer, string key, string value)
        {
            WriteString(writer, key); writer.Write((uint)8); WriteString(writer, value);
        }
        void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes);
        }
        try
        {
            var inputRoot = Path.Combine(root, "incoming"); Directory.CreateDirectory(inputRoot);
            var source = Path.Combine(inputRoot, "fixture.gguf");
            var original = MinimalGguf("header-only fixture; no inference");
            await File.WriteAllBytesAsync(source, original);
            var nested = Path.Combine(inputRoot, "nested"); Directory.CreateDirectory(nested);
            await File.WriteAllBytesAsync(Path.Combine(nested, "second.GGUF"), original);
            await File.WriteAllTextAsync(Path.Combine(inputRoot, "invalid.gguf"), "not a model");
            await File.WriteAllBytesAsync(Path.Combine(inputRoot, "split-00001-of-00002.gguf"), original);
            await File.WriteAllTextAsync(Path.Combine(inputRoot, "notes.txt"), "ignored");
            var registered = new ModelRecord { Id = "existing", Name = "Fixture", Path = source };
            var scan = await new ModelRepository(inputRoot).ScanAsync([registered], CancellationToken.None);
            Check(scan.Models.Count == 4 && scan.Models.Single(x => x.Path == source).RegisteredId == registered.Id,
                "repository scan finds nested case-insensitive GGUF names and identifies existing registrations");
            Check(scan.Models.Count(x => x.Error is not null) == 2 && !scan.Truncated,
                "scan marks invalid and unsupported split weights without loading or registering them");
            Check(File.ReadAllBytes(source).SequenceEqual(original), "discovery leaves existing model bytes unchanged");
            var missing = await new ModelRepository(Path.Combine(root, "absent")).ScanAsync([], CancellationToken.None);
            Check(missing.Models.Count == 0 && missing.Issues.Count == 1, "missing repository returns a useful empty discovery result");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); var stopped = false;
                try { await new ModelRepository(inputRoot).ScanAsync([], cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "repository discovery observes cancellation");
            }

            Check(ModelRepository.RepositoryId("https://huggingface.co/owner/model/") == "owner/model",
                "Hugging Face repository URLs normalize to exact owner/model coordinates");
            var url = ModelRepository.DownloadUrl("owner/model", "refs/pr/2", "sub folder/quoted #é.gguf");
            Check(url == "https://huggingface.co/owner/model/resolve/refs%2Fpr%2F2/sub%20folder/quoted%20%23%C3%A9.gguf?download=true",
                "Hugging Face revision and each filename segment are encoded independently");
            Check(Rejects(() => ModelRepository.RepositoryId("https://attacker.invalid/owner/model")) &&
                Rejects(() => ModelRepository.RepositoryId("https://user:password@huggingface.co/owner/model")) &&
                Rejects(() => ModelRepository.DownloadUrl("owner/model", "main", "../escape.gguf")) &&
                Rejects(() => ModelRepository.DownloadUrl("owner/model", "main", "sub\\escape.gguf")) &&
                Rejects(() => ModelRepository.DownloadUrl("owner/model", "main", "split-00001-of-00002.gguf")),
                "repository import rejects wrong hosts, embedded credentials, traversal, and split weights");

            await using var harness = new Harness(Path.Combine(root, "state"));
            async Task<CommandResult> Call(string operation, object args, CancellationToken ct = default) =>
                await harness.Commands.ExecuteAsync(new(operation, CerebralJson.Element(args)), ct);
            var typedRoot = Path.Combine(root, "typed"); Directory.CreateDirectory(typedRoot);
            var typedSource = Path.Combine(typedRoot, "Roleplay-Coder-Adapter.gguf");
            await File.WriteAllBytesAsync(typedSource, TypedAdapterGguf());
            var typedDiscovery = await new ModelRepository(typedRoot).ScanAsync([], CancellationToken.None);
            Check(typedDiscovery.Models.Single().DeclaredType == "adapter" && typedDiscovery.Models.Single().Purpose == "Adapter",
                "repository scan propagates authoritative declared type without roleplay or coding overrides");
            var typedImportResult = await Call("models.import", new { path = typedSource, name = "Typed adapter", id = "typed-adapter", copy = true });
            var typedImport = CerebralJson.Read<ModelRecord>(typedImportResult.Data);
            Check(typedImportResult.Success && typedImport.DeclaredType == "adapter" && typedImport.Purpose == "Adapter" && typedImport.MetadataSource == "gguf",
                "model registration persists authoritative declared type with direct-metadata provenance");
            var importedResult = await Call("models.import", new { path = source, name = "Fixture model", id = "fixture", copy = true, license = "fixture-license", contextTokens = 4096, parallelRequests = 1 });
            Check(importedResult.Success, "model import copies a valid fixture through the actual shared command");
            var imported = CerebralJson.Read<ModelRecord>(importedResult.Data);
            Check(imported.Path == Path.Combine(harness.Configuration.Get().ModelDirectory, "fixture.gguf") && imported.SizeBytes == original.Length &&
                File.ReadAllBytes(imported.Path).SequenceEqual(original), "model import stores exact bytes at the configured repository destination");
            var listedModels = CerebralJson.Read<List<ModelRecord>>((await Call("models.list", new { })).Data);
            var listedImported = listedModels.Single(x => x.Id == imported.Id);
            Check(listedImported.CapabilityStatus.Single(x => x.Id == ModelCapabilityIds.TextGeneration).Usable &&
                !listedImported.CapabilityStatus.Single(x => x.Id == ModelCapabilityIds.ImageInput).Usable,
                "models.list projects layered capability status for Configuration without loading the model");
            var projectorBytes = MinimalGguf("matching image component fixture; no inference");
            var (componentUrl, servedComponent) = ServeOnce(projectorBytes);
            var componentDownload = await Call("models.component.download", new
            {
                id = imported.Id,
                url = componentUrl,
                fileName = imported.Id + ".projector.gguf"
            });
            await servedComponent.WaitAsync(TimeSpan.FromSeconds(5));
            var componentModel = CerebralJson.Read<ModelRecord>(componentDownload.Data);
            Check(componentDownload.Success && componentModel.VisionProjectorPath is { } componentPath &&
                File.ReadAllBytes(componentPath).SequenceEqual(projectorBytes),
                "owner component download stores and binds a validated matching GGUF through the shared command");
            Check(!(await Call("models.component.download", new { id = imported.Id, url = "https://example.test/mmproj.gguf", fileName = "nested/mmproj.gguf" })).Success &&
                !Directory.Exists(Path.Combine(harness.Configuration.Get().ModelDirectory, "nested")),
                "component download rejects destination paths before network or filesystem mutation");
            var downloadedProjector = componentModel.VisionProjectorPath!;
            Check((await Call("models.configure", new { id = imported.Id, visionProjectorPath = (string?)null })).Success &&
                harness.Store.Get<ModelRecord>("model", imported.Id)!.VisionProjectorPath is null,
                "owner can explicitly return the model to text-only operation");
            File.Delete(downloadedProjector);
            var legacyTextProfile = new AgentProfile { Id = "legacy-text", Name = "Legacy text helper", ModelId = imported.Id };
            Check((await Call("agents.save", legacyTextProfile)).Success,
                "agent assignment preserves the legacy implicit text requirement");
            var savedProfile = harness.Store.Get<AgentProfile>("agent", legacyTextProfile.Id)!;
            var staleProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(savedProfile));
            var promptUpdate = await Call("agents.instructions.save",
                new AgentInstructionSaveRequest(savedProfile.Id, "Latest profile guidance", savedProfile.Revision));
            Check(promptUpdate.Success, "narrow prompt save advances the profile revision");
            staleProfile.Name = "Stale form";
            Check(!(await Call("agents.save", staleProfile)).Success &&
                harness.Store.Get<AgentProfile>("agent", savedProfile.Id)!.Instructions == "Latest profile guidance",
                "stale full-profile form cannot overwrite a newer prompt edit");
            var currentProfile = harness.Store.Get<AgentProfile>("agent", savedProfile.Id)!;
            currentProfile.Name = "Updated helper";
            Check((await Call("agents.save", currentProfile)).Success &&
                harness.Store.Get<AgentProfile>("agent", savedProfile.Id)!.Instructions == "Latest profile guidance",
                "current full-profile form remains saveable and preserves acknowledged guidance");
            var missingPayload = CerebralJson.Read<ModelRecord>(CerebralJson.Element(imported));
            missingPayload.Id = "missing-payload"; missingPayload.Name = "Missing payload"; missingPayload.Path = Path.Combine(root, "missing-payload.gguf");
            harness.Store.Put("model", missingPayload.Id, missingPayload);
            var listedMissing = CerebralJson.Read<List<ModelRecord>>((await Call("models.list", new { })).Data).Single(x => x.Id == missingPayload.Id);
            Check(!listedMissing.CapabilityStatus.Single(x => x.Id == ModelCapabilityIds.TextGeneration).Usable &&
                listedMissing.CapabilityStatus.Single(x => x.Id == ModelCapabilityIds.TextGeneration).PayloadStatus == ModelCapabilityStates.Missing,
                "models.list reports a registered missing GGUF payload as unavailable despite retained metadata");
            var missingProfile = new AgentProfile { Id = "missing-text", Name = "Missing text helper", ModelId = missingPayload.Id };
            var missingSave = await Call("agents.save", missingProfile);
            Check(!missingSave.Success && missingSave.Error?.Contains("payload: missing", StringComparison.Ordinal) == true && harness.Store.Get<AgentProfile>("agent", missingProfile.Id) is null,
                "agents.save rejects an implicit text profile whose local GGUF payload is missing");
            harness.Store.Put("agent", missingProfile.Id, missingProfile); // Simulate a stale legacy record created before admission checks.
            var missingSubmit = await Call("jobs.submit", new { requestId = "missing-payload-job", profileId = missingProfile.Id, prompt = "hello" });
            Check(!missingSubmit.Success && missingSubmit.Error?.Contains("payload: missing", StringComparison.Ordinal) == true &&
                harness.Store.Get<AgentJob>("job", "missing-payload-job") is null,
                "scheduler admission rejects a stale local profile before creating work when its payload is missing");
            harness.Store.Delete("agent", missingProfile.Id); harness.Store.Delete("model", missingPayload.Id);
            var absentLocalProfile = new AgentProfile { Id = "absent-local", Name = "Absent local model", ModelId = "not-registered" };
            var absentLocalSave = await Call("agents.save", absentLocalProfile);
            Check(!absentLocalSave.Success && absentLocalSave.Error?.Contains("Import the selected model first", StringComparison.Ordinal) == true,
                "agents.save rejects an unregistered local model instead of treating legacy text as locally available");
            harness.Store.Put("agent", absentLocalProfile.Id, absentLocalProfile); // Simulate a stale profile whose registration was removed outside normal commands.
            var absentLocalSubmit = await Call("jobs.submit", new { requestId = "absent-local-job", profileId = absentLocalProfile.Id, prompt = "hello" });
            Check(!absentLocalSubmit.Success && absentLocalSubmit.Error?.Contains("not registered on this service", StringComparison.Ordinal) == true &&
                harness.Store.Get<AgentJob>("job", "absent-local-job") is null,
                "scheduler admission rejects a stale local profile whose model registration is missing");
            harness.Store.Delete("agent", absentLocalProfile.Id);
            var remoteLegacyProfile = new AgentProfile
            {
                Id = "remote-legacy-text", Name = "Remote legacy text", ModelId = "remote-model",
                WorkerId = "remote-worker", ModelWorkerId = "remote-worker"
            };
            var unpairedRemoteSave = await Call("agents.save", remoteLegacyProfile);
            Check(!unpairedRemoteSave.Success && harness.Store.Get<AgentProfile>("agent", remoteLegacyProfile.Id) is null,
                "remote profile saves require a registered paired owner route instead of trusting unavailable payload claims");
            harness.Store.Delete("agent", remoteLegacyProfile.Id);
            var visionProfile = new AgentProfile
            {
                Id = "vision-helper", Name = "Vision helper", ModelId = imported.Id,
                RequiredModelCapabilities = [ModelCapabilityIds.ImageInput]
            };
            var incompatibleAssignment = await Call("agents.save", visionProfile);
            Check(!incompatibleAssignment.Success && incompatibleAssignment.Error?.Contains("Configure the matching image projector", StringComparison.Ordinal) == true &&
                harness.Store.Get<AgentProfile>("agent", visionProfile.Id) is null,
                "agents.save rejects a vision profile before persisting an incompatible model assignment");
            harness.Store.Delete("agent", legacyTextProfile.Id);
            var duplicateImport = await Call("models.import", new { path = source, name = "Duplicate", id = "fixture", copy = true });
            Check(!duplicateImport.Success && File.ReadAllBytes(imported.Path).SequenceEqual(original), "duplicate model identifier does not overwrite existing weights");
            imported.SourceUrl = "https://huggingface.co/owner/model/resolve/commit/fixture.gguf";
            imported.Publisher = "Fixture publisher";
            imported.UpstreamModelId = "owner/original-model";
            imported.UpstreamModelUrl = "https://huggingface.co/owner/original-model";
            imported.LicenseUrl = "https://example.test/license";
            imported.UpstreamRevision = "fixture-revision";
            imported.Description = "A bounded package provenance fixture.";
            imported.Tags = ["fixture", "text-generation"];
            imported.PackageId = "pegasus.cerebral.fixture";
            imported.PackageVersion = "2.1";
            imported.PackagePurpose = "Test package";
            imported.StarterPackage = true;
            harness.Store.Put("model", imported.Id, imported);
            var exported = Path.Combine(root, "fixture.CerebralLLM");
            Check((await Call("models.package.export", new { id = imported.Id, path = exported })).Success && File.Exists(exported),
                "package export creates the selected standalone ZIP");
            using (var archive = ZipFile.OpenRead(exported))
            {
                Check(archive.Entries.Select(x => x.FullName).ToHashSet().SetEquals(["manifest.json", "model.gguf"]),
                    "export contains only versioned metadata and one weight file");
                using var metadata = archive.GetEntry("manifest.json")!.Open();
                var package = await JsonSerializer.DeserializeAsync<ModelPackage>(metadata, CerebralJson.Options);
                Check(package!.Model.Path == "" && package.Model.Id == "" && package.Model.State == "unloaded",
                    "portable model packages omit source-machine paths, identifiers, and loaded state");
                Check(package.Package.Id == imported.PackageId && package.Package.Version == imported.PackageVersion && package.Package.Starter && package.Package.SettingsMode == "current" &&
                    package.Model.UpstreamModelUrl == imported.UpstreamModelUrl && package.Model.LicenseUrl == imported.LicenseUrl,
                    "package manifest records package identity, official upstream model and licensing metadata");
                var weights = archive.GetEntry("model.gguf")!;
                Check(weights.CompressedLength == weights.Length,
                    "GGUF weights are stored byte-for-byte without ineffective recompression inside the package container");
            }
            var defaultsExport = Path.Combine(root, "fixture-defaults.CerebralLLM");
            Check((await Call("models.package.export", new { id = imported.Id, path = defaultsExport, settingsMode = "defaults" })).Success,
                "model export can select portable safe defaults");
            using (var archive = ZipFile.OpenRead(defaultsExport))
            using (var metadata = archive.GetEntry("manifest.json")!.Open())
            {
                var package = await JsonSerializer.DeserializeAsync<ModelPackage>(metadata, CerebralJson.Options);
                Check(package!.Package.SettingsMode == "defaults" && package.Model.AutomaticContextSizing && package.Model.ContextTokens == 4096 &&
                      package.Model.ParallelRequests == 1 && package.Model.GpuLayers == -1 && package.Model.IdleMinutes == 5,
                    "safe-default export resets machine tuning while retaining portable model identity and provenance");
            }
            var exportedBytes = await File.ReadAllBytesAsync(exported);
            Check(!(await Call("models.package.export", new { id = imported.Id, path = exported })).Success &&
                File.ReadAllBytes(exported).SequenceEqual(exportedBytes), "package export refuses an existing destination without overwriting it");
            var roundtrip = await Call("models.package.import", new { path = exported });
            Check(roundtrip.Success, "actual exported package imports through the shared command");
            var restored = CerebralJson.Read<ModelRecord>(roundtrip.Data);
            Check(restored.Id != imported.Id && restored.Path != imported.Path && File.ReadAllBytes(restored.Path).SequenceEqual(original) &&
                restored.License == imported.License && restored.SourceUrl == imported.SourceUrl && restored.ContextTokens == imported.ContextTokens &&
                restored.Publisher == imported.Publisher && restored.UpstreamModelId == imported.UpstreamModelId &&
                restored.UpstreamModelUrl == imported.UpstreamModelUrl && restored.LicenseUrl == imported.LicenseUrl &&
                restored.UpstreamRevision == imported.UpstreamRevision && restored.Description == imported.Description &&
                restored.Tags.SequenceEqual(imported.Tags) && restored.PackageId == imported.PackageId &&
                restored.PackageVersion == imported.PackageVersion && restored.PackagePurpose == imported.PackagePurpose && restored.StarterPackage,
                "package roundtrip preserves weights and useful metadata while assigning local identity");

            // Queued work owns its accepted profile snapshot, even after the saved profile changes model.
            var movedProfile = new AgentProfile { Id = "moved-profile", ModelId = imported.Id, ContextTokens = 4096 };
            harness.Store.Put("agent", movedProfile.Id, movedProfile);
            var acceptedProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(movedProfile));
            acceptedProfile.ModelId = restored.Id;
            var oldAssignment = new AgentJob { Id = "old-model-assignment", ProfileId = movedProfile.Id, Profile = acceptedProfile, Status = "queued" };
            harness.Store.Put("job", oldAssignment.Id, oldAssignment);
            var changedModel = CerebralJson.Read<ModelRecord>(CerebralJson.Element(restored));
            changedModel.ContextTokens = 2048; changedModel.Name = "Changed after completion";
            var blockedSave = await Call("models.save", changedModel);
            Check(!blockedSave.Success && blockedSave.Error?.Contains("assignments", StringComparison.Ordinal) == true &&
                harness.Store.Get<ModelRecord>("model", restored.Id)!.ContextTokens == 4096,
                "queued old-profile snapshot prevents changing the model after its saved agent chooses another model");
            var blockedDelete = await Call("models.delete", new { id = restored.Id });
            Check(!blockedDelete.Success && blockedDelete.Error?.Contains("assignments", StringComparison.Ordinal) == true &&
                harness.Store.Get<ModelRecord>("model", restored.Id) is not null && File.ReadAllBytes(restored.Path).SequenceEqual(original),
                "queued old-profile snapshot prevents unregistering its model and preserves its weights");
            oldAssignment.Status = "completed"; harness.Store.Put("job", oldAssignment.Id, oldAssignment);
            Check((await Call("models.save", changedModel)).Success &&
                harness.Store.Get<ModelRecord>("model", restored.Id)!.ContextTokens == 2048,
                "completed assignment history does not block later model settings");
            Check((await Call("models.delete", new { id = restored.Id })).Success &&
                harness.Store.Get<ModelRecord>("model", restored.Id) is null && File.ReadAllBytes(restored.Path).SequenceEqual(original),
                "completed old-profile assignment permits unregistering without deleting its model file");
            harness.Store.Delete("agent", movedProfile.Id);

            var budgetModel = new ModelRecord
            {
                Id = "budget-model", Name = "Budget fixture", Path = source, SizeBytes = original.Length,
                State = "unloaded", ContextTokens = 8192, RecommendedContextTokens = 4096
            };
            harness.Store.Put("model", budgetModel.Id, budgetModel);
            var automaticBudget = new AgentProfile
            {
                Id = "automatic-budget", Revision = 3, Name = "Automatic budget", ModelId = budgetModel.Id,
                FollowsModelBudgets = true, ContextTokens = 8192, ResponseTokens = 2048
            };
            var customBudget = new AgentProfile
            {
                Id = "custom-budget", Revision = 6, Name = "Custom budget", ModelId = budgetModel.Id,
                FollowsModelBudgets = false, ContextTokens = 6000, ResponseTokens = 1200,
                ReasoningMode = "on", ReasoningTokens = 1024
            };
            harness.Store.Put("agent", automaticBudget.Id, automaticBudget);
            harness.Store.Put("agent", customBudget.Id, customBudget);
            var reducedBudgetModel = CerebralJson.Read<ModelRecord>(CerebralJson.Element(budgetModel));
            reducedBudgetModel.ContextTokens = 2048;
            var reducedBudgetResult = await Call("models.save", reducedBudgetModel);
            var resolvedAutomatic = harness.Store.Get<AgentProfile>("agent", automaticBudget.Id)!;
            var resolvedCustom = harness.Store.Get<AgentProfile>("agent", customBudget.Id)!;
            Check(reducedBudgetResult.Success && resolvedAutomatic.Revision == 4 && resolvedAutomatic.FollowsModelBudgets &&
                resolvedAutomatic.ContextTokens == 2048 && resolvedAutomatic.ResponseTokens == 512,
                "reducing model context through the service re-resolves an automatic profile from the model policy");
            Check(resolvedCustom.Revision == 7 && !resolvedCustom.FollowsModelBudgets && resolvedCustom.ContextTokens == 2048 &&
                resolvedCustom.ResponseTokens == 767 && resolvedCustom.ReasoningMode == "on" && resolvedCustom.ReasoningTokens == 1024,
                "the same model reduction preserves the separately configured reasoning reserve and clamps the visible response to safe headroom");
            harness.Store.Delete("agent", automaticBudget.Id);
            harness.Store.Delete("agent", customBudget.Id);
            harness.Store.Delete("model", budgetModel.Id);

            string Package(string name, string manifest, byte[]? weights = null, string? extra = null)
            {
                var path = Path.Combine(root, name + ".zip");
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                using (var output = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false))) output.Write(manifest);
                using (var output = archive.CreateEntry("model.gguf").Open()) output.Write(weights ?? original);
                if (extra is not null) using (var output = new StreamWriter(archive.CreateEntry(extra).Open())) output.Write("unexpected");
                return path;
            }
            var validMetadata = JsonSerializer.Serialize(new ModelPackage { Model = new ModelRecord { Id = "foreign", Path = Path.Combine(root, "must-not-write.gguf"), Name = "Foreign fixture", ContextTokens = 4096 } }, CerebralJson.Options);
            var beforeInvalid = harness.Store.List<ModelRecord>("model").Count;
            foreach (var malformed in new[]
            {
                Package("missing-contract", "{\"model\":{\"name\":\"Bad\"}}"),
                Package("wrong-version", validMetadata.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal)),
                Package("duplicate-version", validMetadata.Insert(validMetadata.IndexOf('{') + 1, "\"Version\":2,")),
                Package("unexpected-entry", validMetadata, extra: "../escape.txt"),
                Package("bad-weights", validMetadata, Encoding.UTF8.GetBytes("BAD!")),
                Package("oversized-metadata", new string(' ', 65537))
            })
            {
                Check(!(await Call("models.package.import", new { path = malformed })).Success, "invalid model package is rejected: " + Path.GetFileName(malformed));
            }
            Check(harness.Store.List<ModelRecord>("model").Count == beforeInvalid && !File.Exists(Path.Combine(root, "must-not-write.gguf")) &&
                !Directory.EnumerateFiles(harness.Configuration.Get().ModelDirectory).Any(x => x.EndsWith(".partial", StringComparison.Ordinal)),
                "failed imports preserve registrations, reject remote paths, and clean only their own incomplete transfers");

            var replacement = Path.Combine(inputRoot, "replacement.gguf");
            var replacementBytes = MinimalGguf("replacement fixture bytes; no inference");
            await File.WriteAllBytesAsync(replacement, replacementBytes);
            Check(!(await Call("models.replace", new { id = imported.Id, path = Path.Combine(inputRoot, "invalid.gguf") })).Success &&
                File.ReadAllBytes(imported.Path).SequenceEqual(original), "invalid replacement preserves the usable current weight file");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Check(!(await Call("models.replace", new { id = imported.Id, path = replacement }, cancelled.Token)).Success &&
                    File.ReadAllBytes(imported.Path).SequenceEqual(original), "cancelled replacement preserves the current file");
            }
            harness.Store.Put("job", "active", new AgentJob { Id = "active", Status = "queued", Profile = new AgentProfile { ModelId = imported.Id } });
            Check(!(await Call("models.replace", new { id = imported.Id, path = replacement })).Success,
                "queued assignments protect their selected model from replacement");
            harness.Store.Delete("job", "active");
            var alias = CerebralJson.Read<ModelRecord>(CerebralJson.Element(imported)); alias.Id = "alias"; alias.State = "loaded";
            harness.Store.Put("model", alias.Id, alias);
            Check(!(await Call("models.replace", new { id = imported.Id, path = replacement })).Success &&
                File.ReadAllBytes(imported.Path).SequenceEqual(original), "another loaded registration of the same file prevents replacement");
            alias.State = "unloaded"; harness.Store.Put("model", alias.Id, alias);
            if (OperatingSystem.IsWindows())
            {
                using var held = new FileStream(imported.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Check(!(await Call("models.replace", new { id = imported.Id, path = replacement })).Success &&
                    File.ReadAllBytes(imported.Path).SequenceEqual(original), "a locked target fails replacement without damaging the existing file");
            }
            var replaced = await Call("models.replace", new { id = imported.Id, path = replacement });
            Check(replaced.Success && File.ReadAllBytes(imported.Path).SequenceEqual(replacementBytes) &&
                harness.Store.Get<ModelRecord>("model", alias.Id)!.SizeBytes == replacementBytes.Length,
                "replacement publishes complete bytes and updates registrations sharing the file");
            Check(!Directory.EnumerateFiles(harness.Configuration.Get().ModelDirectory).Any(x => x.EndsWith(".replacement", StringComparison.Ordinal) || x.EndsWith(".partial", StringComparison.Ordinal)),
                "successful and failed replacements leave no staging or backup files");

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var mutation = Task.Run(async () => await harness.Runtime.WithUnloadedModelAsync(imported.Id, _ =>
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture gate was not released.");
                return true;
            }, CancellationToken.None));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var load = harness.Runtime.LoadAsync(imported.Id, CancellationToken.None);
            try
            {
                Check(await Task.WhenAny(load, Task.Delay(100)) != load, "automatic runtime load waits while the unloaded weight mutation owns the lifecycle gate");
            }
            finally { release.Set(); }
            Check(await mutation, "the bounded unloaded mutation completes after releasing its fixture gate");
            var missingRuntime = false;
            try { await load; } catch (FileNotFoundException) { missingRuntime = true; }
            Check(missingRuntime, "subsequent load reports absent runtime honestly; lifecycle test launched no model process");

            await using var bootstrap = new Harness(Path.Combine(root, "bootstrap"));
            async Task<CommandResult> Bootstrap(object args) => await bootstrap.Commands.ExecuteAsync(new("agents.bootstrap", CerebralJson.Element(args)));
            var fixtureModel = new ModelRecord { Id = "bootstrap-model", Name = "Fixture", Path = source, SizeBytes = original.Length, State = "unloaded", ContextTokens = 4096 };
            bootstrap.Store.Put("model", fixtureModel.Id, fixtureModel);
            Check(!(await Bootstrap(new { modelId = fixtureModel.Id })).Success && bootstrap.Store.List<AgentProfile>("agent").Count == 0,
                "default-agent setup rejects a model without a successful loaded state");
            fixtureModel.State = "loaded"; bootstrap.Store.Put("model", fixtureModel.Id, fixtureModel);
            var setup = bootstrap.Configuration.Get(); setup.McpServers =
            [
                new McpRegistration { Id = "selected-provider", Enabled = true },
                new McpRegistration { Id = "unrelated-cortex-name", Enabled = true },
                new McpRegistration { Id = "disabled", Enabled = false }
            ];
            bootstrap.Configuration.Save(setup);
            Check(!(await Bootstrap(new { modelId = fixtureModel.Id, cortexServerId = "disabled" })).Success &&
                bootstrap.Store.List<AgentProfile>("agent").Count == 0, "default-agent setup rejects unavailable exact provider selection without partial profiles");
            var ready = await Bootstrap(new { modelId = fixtureModel.Id, cortexServerId = "selected-provider" });
            Check(ready.Success, "explicit loaded-state fixture bootstraps the default profile set");
            var profiles = CerebralJson.Read<List<AgentProfile>>(ready.Data);
            var lead = profiles.Single(x => x.Role == "lead"); var compactor = profiles.Single(x => x.Role == "compactor");
            Check(profiles.Count == 5 && profiles.Select(x => x.ModelId).Distinct().Single() == fixtureModel.Id &&
                lead.Capabilities.Contains("agents.delegate") && profiles.Count(x => x.Role == "helper") == 3,
                "one shared model serves a delegating Orchestrator, three helpers, and one compactor");
            Check(profiles.All(x => x.FollowsDefaultModel && x.FollowsModelBudgets) && bootstrap.Configuration.Get().DefaultModelId == fixtureModel.Id,
                "bootstrapped profiles follow the saved default model and its recommended token policy");
            Check(profiles.All(x => !string.IsNullOrWhiteSpace(x.Instructions)) && profiles.Select(x => x.Instructions).Distinct(StringComparer.Ordinal).Count() == 5 &&
                lead.Instructions.Contains("straightforward work directly", StringComparison.Ordinal) &&
                profiles.Single(x => x.Name == "Coding helper").Instructions.Contains("focused checks", StringComparison.Ordinal) &&
                profiles.Single(x => x.Name == "Analysis helper").Instructions.Contains("units, assumptions", StringComparison.Ordinal) &&
                profiles.Single(x => x.Name == "Writing helper").Instructions.Contains("ask the author", StringComparison.Ordinal) &&
                compactor.Instructions.Contains("one concise checkpoint", StringComparison.Ordinal),
                "each built-in agent type receives distinct automatic role guidance");
            var smaller = new ModelRecord { Id = "smaller", Name = "Smaller", Path = source, ContextTokens = 4096, RecommendedContextTokens = 16384 };
            var adjusted = DefaultModelProfileSettings.ForModel(lead, smaller);
            Check(adjusted.ModelId == smaller.Id && adjusted.ContextTokens == 4096 && adjusted.ResponseTokens == 1024 &&
                adjusted.FollowsDefaultModel && adjusted.FollowsModelBudgets && lead.ModelId == fixtureModel.Id,
                "automatic default-model planning respects the configured model ceiling without mutating the saved profile");
            var recommended = new ModelRecord { Id = "recommended", Name = "Recommended", Path = source, ContextTokens = 32768, RecommendedContextTokens = 16384 };
            var derived = DefaultModelProfileSettings.ForModel(lead, recommended);
            Check(derived.ContextTokens == 32768 && derived.ResponseTokens == 8192,
                "automatic model budgets follow the selected context and derive one quarter for the response allowance");
            var automaticPolicy = new AgentProfile { FollowsModelBudgets = true, ReasoningMode = "off" };
            foreach (var (context, response) in new[]
            {
                (512, 128), (8192, 2048), (16384, 4096), (32768, 8192),
                (48128, 12032), (1048576, AgentTokenLimits.MaximumResponseTokens)
            })
            {
                var resolved = AgentBudgetPolicy.Resolve(automaticPolicy, new ModelRecord { ContextTokens = context });
                Check(resolved.ContextTokens == context && resolved.ResponseTokens == response,
                    "automatic response budget follows context and the existing v1 maximum at " + context);
            }
            var fitted = AgentBudgetPolicy.Resolve(automaticPolicy,
                new ModelRecord { ContextTokens = 32768 }, recommendedContextTokens: 8192);
            Check(fitted.ContextTokens == 8192 && fitted.ResponseTokens == 2048,
                "a confirmed 8K fit narrows both context and automatic response without changing the model record");
            var explicitPolicy = new AgentProfile { FollowsModelBudgets = false, ContextTokens = 32768,
                ResponseTokens = 3072, ReasoningMode = "off" };
            Check(AgentBudgetPolicy.Resolve(explicitPolicy, new ModelRecord { ContextTokens = 32768 }).ResponseTokens == 3072 &&
                AgentTokenLimits.ResolveResponseShare(explicitPolicy, 25) == 8192,
                "explicit profile budget and separately requested response share remain independent of the automatic default");
            var reasoningPolicy = new AgentProfile { FollowsModelBudgets = true, ReasoningMode = "on", ReasoningTokens = 32768 };
            var reasoned = AgentBudgetPolicy.Resolve(reasoningPolicy, new ModelRecord { ContextTokens = 2048 });
            Check(reasoned.ReasoningTokens == 1663 && reasoned.ResponseTokens == 128 &&
                Rejects(() => AgentBudgetPolicy.Resolve(reasoningPolicy, new ModelRecord { ContextTokens = 512 })),
                "reasoning reserves clamp visible output or refuse an impossible context");
            var custom = CerebralJson.Read<AgentProfile>(CerebralJson.Element(lead));
            custom.FollowsModelBudgets = false;
            custom.ContextTokens = 6000;
            custom.ResponseTokens = 1200;
            custom.ReasoningMode = "on";
            custom.ReasoningTokens = 512;
            var roomy = new ModelRecord { Id = "roomy", Name = "Roomy", Path = source, ContextTokens = 8192, RecommendedContextTokens = 4096 };
            var preserved = DefaultModelProfileSettings.ForModel(custom, roomy);
            Check(preserved.ModelId == roomy.Id && preserved.FollowsDefaultModel && !preserved.FollowsModelBudgets &&
                preserved.ContextTokens == 6000 && preserved.ResponseTokens == 1200 && preserved.ReasoningTokens == 512 &&
                custom.ModelId == fixtureModel.Id && custom.ContextTokens == 6000,
                "a default-following agent can retain custom token budgets independently of model recommendations");
            var largeCustom = CerebralJson.Read<AgentProfile>(CerebralJson.Element(custom));
            largeCustom.ContextTokens = 8192;
            largeCustom.ResponseTokens = 6000;
            largeCustom.ReasoningTokens = 1024;
            var constrained = new ModelRecord { Id = "constrained", Name = "Constrained", Path = source, ContextTokens = 2048, RecommendedContextTokens = 2048 };
            var clamped = DefaultModelProfileSettings.ForModel(largeCustom, constrained);
            Check(clamped.ContextTokens == 2048 && clamped.ResponseTokens == 767 && clamped.ReasoningMode == "on" && clamped.ReasoningTokens == 1024 &&
                !clamped.FollowsModelBudgets && largeCustom.ContextTokens == 8192 && largeCustom.ResponseTokens == 6000,
                "custom budgets survive a default switch while reasoning remains separate and the response is clamped to the target ceiling");
            var fittingCandidate = CerebralJson.Read<ModelRecord>(CerebralJson.Element(fixtureModel));
            fittingCandidate.AutomaticContextSizing = true;
            fittingCandidate.ContextTokens = 48128;
            var beforeFit = CommandDispatcher.TargetModelFingerprint.Capture(fittingCandidate);
            var loadedFit = CerebralJson.Read<ModelRecord>(CerebralJson.Element(fittingCandidate));
            loadedFit.ContextTokens = 46080; loadedFit.State = "loaded";
            var verifiedFit = CerebralJson.Read<ModelRecord>(CerebralJson.Element(loadedFit));
            Check(beforeFit.MatchesConfirmedLoad(loadedFit, verifiedFit),
                "a confirmed local automatic context fit is admitted without treating it as an owner edit");
            var loadedBudget = DefaultModelProfileSettings.ForModel(lead, verifiedFit, verifiedFit.ContextTokens);
            Check(loadedBudget.ContextTokens == 46080 && lead.ModelId == fixtureModel.Id,
                "confirmed loaded context rebuilds inherited budgets without changing the prior saved profile");
            var laterContextEdit = CerebralJson.Read<ModelRecord>(CerebralJson.Element(verifiedFit));
            laterContextEdit.ContextTokens = 45056;
            Check(!beforeFit.MatchesConfirmedLoad(loadedFit, laterContextEdit),
                "a later context edit cannot masquerade as the runtime's exact fitted result");
            var otherSettingEdit = CerebralJson.Read<ModelRecord>(CerebralJson.Element(loadedFit));
            otherSettingEdit.ParallelRequests++;
            Check(!beforeFit.MatchesConfirmedLoad(otherSettingEdit, otherSettingEdit),
                "automatic fitting cannot admit a changed concurrency setting");
            fittingCandidate.AutomaticContextSizing = false;
            var fixedContextLoad = CerebralJson.Read<ModelRecord>(CerebralJson.Element(loadedFit));
            fixedContextLoad.AutomaticContextSizing = false;
            Check(!CommandDispatcher.TargetModelFingerprint.Capture(fittingCandidate).MatchesConfirmedLoad(fixedContextLoad, fixedContextLoad),
                "fixed-context registrations reject a runtime context change");
            fittingCandidate.AutomaticContextSizing = true;
            var fixedPayload = CommandDispatcher.TargetModelFingerprint.Capture(fittingCandidate);
            var oldWriteTime = File.GetLastWriteTimeUtc(source);
            File.SetLastWriteTimeUtc(source, oldWriteTime.AddMinutes(1));
            Check(!fixedPayload.MatchesConfirmedLoad(loadedFit, loadedFit),
                "a local payload timestamp change cannot masquerade as automatic context fitting");
            File.SetLastWriteTimeUtc(source, oldWriteTime);

            await using (var fitHarness = new Harness(Path.Combine(root, "capacity-profile-preimages")))
            {
                var fitModel = CerebralJson.Read<ModelRecord>(CerebralJson.Element(fittingCandidate));
                fitModel.Id = "fit-model";
                fitHarness.Store.Put("model", fitModel.Id, fitModel);
                var switchProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(lead));
                switchProfile.Id = "switch-lead"; switchProfile.ModelId = fitModel.Id;
                switchProfile.ModelWorkerId = "local"; switchProfile.ContextTokens = 48128; switchProfile.Revision = 1;
                var pinnedProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(switchProfile));
                pinnedProfile.Id = "pinned-local"; pinnedProfile.Role = "helper"; pinnedProfile.Revision = 7;
                pinnedProfile.FollowsDefaultModel = false;
                fitHarness.Store.Put("agent", switchProfile.Id, switchProfile);
                fitHarness.Store.Put("agent", pinnedProfile.Id, pinnedProfile);
                fitHarness.Runtime.ApplyCapacityPlan(fitModel, 46080, new HashSet<string> { switchProfile.Id });
                var stillExpected = fitHarness.Store.Get<AgentProfile>("agent", switchProfile.Id)!;
                var fittedPinned = fitHarness.Store.Get<AgentProfile>("agent", pinnedProfile.Id)!;
                Check(stillExpected.ContextTokens == 48128 && stillExpected.Revision == 1 &&
                    fittedPinned.ContextTokens == 46080 && fittedPinned.Revision == 8 &&
                    fitHarness.Store.Get<ModelRecord>("model", fitModel.Id)!.ContextTokens == 46080,
                    "switch-owned fitting preserves affected profile preimages while updating the model and unrelated pinned budgets");
                stillExpected.Purpose = "Concurrent owner edit"; stillExpected.Revision++;
                fitHarness.Store.Put("agent", stillExpected.Id, stillExpected);
                fitHarness.Runtime.ApplyCapacityPlan(fitModel, 45056, new HashSet<string> { switchProfile.Id });
                var ownerEdited = fitHarness.Store.Get<AgentProfile>("agent", switchProfile.Id)!;
                Check(ownerEdited.Purpose == "Concurrent owner edit" && ownerEdited.Revision == 2 && ownerEdited.ContextTokens == 48128,
                    "deferred fitting does not overwrite an affected owner's changed profile or revision");
                fitHarness.Runtime.ApplyCapacityPlan(fitModel, 44032);
                var ordinaryLoad = fitHarness.Store.Get<AgentProfile>("agent", switchProfile.Id)!;
                Check(ordinaryLoad.ContextTokens == 44032 && ordinaryLoad.Revision == 3 && ordinaryLoad.Purpose == "Concurrent owner edit",
                    "ordinary capacity fitting keeps normal profile-budget updates and preserves other owner settings");
            }

            var runtimeSettings = bootstrap.Configuration.Get();
            runtimeSettings.LlamaServerPath = Path.Combine(root, "missing-llama-server.exe");
            bootstrap.Configuration.Save(runtimeSettings);
            var declaredAdapter = new ModelRecord
            {
                Id = "declared-adapter", Name = "Misleading text model name", Path = source, SizeBytes = original.Length,
                Architecture = "llama", DeclaredType = "adapter", MetadataSource = "gguf", Purpose = "General",
                ContextTokens = 4096, RecommendedContextTokens = 4096, ChatTemplate = "alpaca"
            };
            bootstrap.Store.Put("model", declaredAdapter.Id, declaredAdapter);
            var adapterSwitch = await bootstrap.Commands.ExecuteAsync(new("models.default.switch", CerebralJson.Element(new { modelId = declaredAdapter.Id })));
            Check(!adapterSwitch.Success && adapterSwitch.Error?.Contains("declares itself as an adapter", StringComparison.Ordinal) == true &&
                bootstrap.Configuration.Get().DefaultModelId == fixtureModel.Id && bootstrap.Store.List<AgentProfile>("agent").All(x => x.ModelId == fixtureModel.Id),
                "default switch rejects authoritative adapter metadata without changing saved assignments");
            var heuristicPurpose = CerebralJson.Read<ModelRecord>(CerebralJson.Element(declaredAdapter));
            heuristicPurpose.Id = "heuristic-purpose"; heuristicPurpose.DeclaredType = "model"; heuristicPurpose.Purpose = "Adapter";
            bootstrap.Store.Put("model", heuristicPurpose.Id, heuristicPurpose);
            var heuristicSwitch = await bootstrap.Commands.ExecuteAsync(new("models.default.switch", CerebralJson.Element(new { modelId = heuristicPurpose.Id })));
            Check(!heuristicSwitch.Success && heuristicSwitch.Error?.Contains("llamaServerPath", StringComparison.Ordinal) == true &&
                heuristicSwitch.Error.Contains("detected as", StringComparison.Ordinal) == false && !bootstrap.Runtime.IsLoaded(heuristicPurpose.Id) &&
                bootstrap.Configuration.Get().DefaultModelId == fixtureModel.Id && bootstrap.Store.List<AgentProfile>("agent").All(x => x.ModelId == fixtureModel.Id),
                "heuristic purpose labels defer compatibility to the real runtime and preserve assignments when loading fails");
            Check(profiles.All(x => x.AllowedRoots.SequenceEqual([setup.ProjectsDirectory])) &&
                profiles.All(x => !x.Capabilities.Contains("administration")) && compactor.AllowedTools.Count == 0,
                "default grants use the configured project root and no administrative or compactor tool authority");
            Check(profiles.Where(x => x != compactor).All(x => x.Capabilities.Contains("mcp.selected-provider") && !x.Capabilities.Contains("mcp.unrelated-cortex-name") && !x.Capabilities.Contains("cortex.write")),
                "bootstrap grants only the exact chosen provider without inferring names or durable write approval");
            Check(profiles.Where(x => x != compactor).All(x => x.CompactionProfileId == compactor.Id) && !compactor.AutomaticCompaction,
                "default context compaction is linked once without recursive compaction");
            Check(!(await Bootstrap(new { modelId = fixtureModel.Id })).Success && bootstrap.Store.List<AgentProfile>("agent").Count == 5,
                "repeated bootstrap preserves the existing single Orchestrator and profiles");
            foreach (var profile in profiles) bootstrap.Store.Delete("agent", profile.Id);
            var withoutCortex = CerebralJson.Read<List<AgentProfile>>((await Bootstrap(new { modelId = fixtureModel.Id })).Data);
            Check(withoutCortex.All(x => x.Capabilities.All(cap => !cap.StartsWith("mcp.", StringComparison.Ordinal))),
                "omitted provider selection never silently grants a similarly named MCP connection");
            Console.WriteLine($"Model repository: {checks} checks passed (real temporary file/ZIP/SQLite commands and one loopback HTTP component transfer; header-only fixtures and declared loaded-state fixture, no inference or external network).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static (string Url, Task Completed) ServeOnce(byte[] content)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var completed = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await using var stream = client.GetStream();
                var request = new byte[8192];
                _ = await stream.ReadAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(content);
                await stream.FlushAsync();
            }
            finally { listener.Stop(); }
        });
        return ($"http://127.0.0.1:{port}/component.gguf", completed);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public StateStore Store { get; }
        public Configuration Configuration { get; }
        public ModelRuntime Runtime { get; }
        public CommandDispatcher Commands { get; }
        private readonly ToolDispatcher tools;
        private readonly JobScheduler scheduler;
        public Harness(string directory)
        {
            Store = new(directory); Configuration = new(Store); var events = new EventHub();
            Runtime = new(Store, Configuration.Get, events); tools = new(Configuration.Get);
            scheduler = new(Store, Configuration, new NoExecutor(), Runtime, events, NullLogger<JobScheduler>.Instance);
            Commands = new(Store, Configuration, Runtime, scheduler, tools, events);
        }
        public async ValueTask DisposeAsync() { scheduler.Dispose(); await tools.DisposeAsync(); await Runtime.DisposeAsync(); }
    }
    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Model repository tests never execute an agent.");
    }
}
