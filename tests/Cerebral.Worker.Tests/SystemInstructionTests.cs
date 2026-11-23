using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class SystemInstructionTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-instructions-tests-" + Guid.NewGuid().ToString("N")));
        var assertions = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); assertions++; Console.WriteLine("PASS: " + name); }
        bool Rejects(Action action) { try { action(); return false; } catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { return true; } }
        try
        {
            var store = new StateStore(root); var instructions = new SystemInstructions(store);
            var roles = new[] { "system", "lead", "helper", "compactor" };
            Check(instructions.ReadForAgent() == SystemInstructions.DefaultContent && !Directory.Exists(Path.Combine(root, "Instructions")),
                "inference reads defaults without creating legacy prompt files");
            var initial = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => new SystemInstructions(store).Get())));
            var document = initial[0];
            Check(initial.All(x => x.Sha256 == document.Sha256 && x.Content == SystemInstructions.DefaultContent) &&
                store.List<JsonElement>(SystemInstructions.RecordKind).Count == 0 && !Directory.Exists(Path.Combine(root, "Instructions")),
                "concurrent gets neither create overrides nor materialize legacy files");
            Check(document.Path.StartsWith("Built-in", StringComparison.Ordinal) && instructions.FilePath == Path.Combine(root, "Instructions", "SYSTEM.md"),
                "source labels are distinct from the retained legacy path helper");
            var capturedDefaults = instructions.CaptureEffective();
            Check(capturedDefaults.Select(x => x.Role).SequenceEqual(roles) && capturedDefaults.All(x => x.Content == SystemInstructions.DefaultForRole(x.Role) && x.Sha256.Length == 64),
                "one capture returns all four effective role layers and their CAS tokens");
            var builtInBase = SystemInstructions.ComposeBehaviorLayers(AgentPrompt.Protocol,
                SystemInstructions.DefaultContent, "", "");
            Check(SystemInstructions.BuiltInBaseUtf8Bytes(AgentPrompt.Protocol) == Encoding.UTF8.GetByteCount(builtInBase) &&
                Encoding.UTF8.GetByteCount(builtInBase) <= SystemInstructions.MaximumBuiltInBaseUtf8Bytes &&
                roles.Skip(1).All(role => Encoding.UTF8.GetByteCount(SystemInstructions.DefaultForRole(role)) <=
                    SystemInstructions.MaximumBuiltInRoleUtf8Bytes),
                "built-in base and role defaults stay inside explicit UTF-8 growth budgets");
            var markdown = "# Shared guidance\n\nPreserve café, λ, and exact code.\n";
            var saved = instructions.Save(markdown, document.Sha256);
            var stored = store.Get<JsonElement>(SystemInstructions.RecordKind, "system");
            Check(saved.Content == markdown && instructions.ReadForAgent() == markdown && stored.GetProperty("content").GetString() == markdown &&
                stored.GetProperty("schemaVersion").GetInt32() == 1 && stored.GetProperty("revision").GetInt64() == 1,
                "new Unicode prompt saves are typed SQLite records");
            Check(saved.Path.StartsWith("SQLite system prompt", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(root, "Instructions")) &&
                new SystemInstructions(new StateStore(root)).Get().Content == markdown,
                "SQLite overrides survive reopening and do not produce instruction files");
            Check(Rejects(() => instructions.Save("stale overwrite", document.Sha256)) && instructions.Get().Content == markdown,
                "stale source tokens preserve current instructions");
            Check(Rejects(() => instructions.Save("bad token", "made-up")) && instructions.Get().Sha256 == saved.Sha256,
                "saving requires a valid current revision token");
            var same = instructions.Save(markdown, saved.Sha256);
            Check(same.Sha256 != saved.Sha256 && Rejects(() => instructions.Save("stale after same-content save", saved.Sha256)),
                "same-content saves advance the opaque revision token");
            var changed = instructions.Save("temporary change", same.Sha256);
            saved = instructions.Save(markdown, changed.Sha256);
            Check(saved.Sha256 != same.Sha256 && Rejects(() => instructions.Save("stale reverted content", same.Sha256)),
                "changing and reverting text cannot revive an obsolete editor token");
            var composed = SystemInstructions.ComposeBehaviorLayers("FIXED-APP-PROTOCOL", markdown, "PROFILE-LAYER");
            Check(composed.StartsWith("FIXED-APP-PROTOCOL", StringComparison.Ordinal) &&
                composed.IndexOf(markdown, StringComparison.Ordinal) < composed.IndexOf("PROFILE-LAYER", StringComparison.Ordinal) &&
                composed.Contains(SystemInstructions.ApplicationBoundary, StringComparison.Ordinal),
                "application protocol and grant boundary precede editable shared and profile guidance");
            var empty = instructions.Save("", saved.Sha256);
            Check(instructions.ReadForAgent() == "" && store.Get<JsonElement>(SystemInstructions.RecordKind, "system").GetProperty("content").GetString() == "",
                "an explicit blank SQLite override disables only that guidance layer");
            var full = new string('é', SystemInstructions.MaxUtf8Bytes / 2);
            saved = instructions.Save(full, empty.Sha256);
            Check(Encoding.UTF8.GetByteCount(saved.Content) == SystemInstructions.MaxUtf8Bytes,
                "the prompt bound is measured in UTF-8 bytes");
            Check(Rejects(() => instructions.Save(full + "é", saved.Sha256)) && instructions.Get().Sha256 == saved.Sha256,
                "oversized UTF-8 saves preserve current state");
            Check(Rejects(() => instructions.Save("bad\0text", saved.Sha256)) &&
                Rejects(() => instructions.Save("\ud800", saved.Sha256)) && instructions.Get().Sha256 == saved.Sha256,
                "NUL and malformed Unicode saves fail without replacing the override");
            var sharedToken = saved.Sha256;
            var concurrent = await Task.WhenAll(new[] { "first editor", "second editor" }.Select(text => Task.Run(() =>
            {
                try { new SystemInstructions(new StateStore(root)).Save(text, sharedToken); return true; }
                catch (InvalidOperationException) { return false; }
            })));
            Check(concurrent.Count(x => x) == 1, "separate SQLite store instances admit only one save for the same previous revision");
            saved = instructions.Save("Owner maintained content.", instructions.Get().Sha256);
            var snapshot = instructions.CaptureEffective();
            instructions.Save("Later shared content.", saved.Sha256);
            Check(snapshot[0].Content == "Owner maintained content." && snapshot[0].Sha256 == saved.Sha256 &&
                capturedDefaults[0].Content == SystemInstructions.DefaultContent,
                "captured prompt snapshots remain detached from later saves");
            instructions.Save("Owner maintained content.", instructions.Get().Sha256);

            var legacyRoot = Path.Combine(root, "legacy");
            var legacyDirectory = Path.Combine(legacyRoot, "Instructions");
            Directory.CreateDirectory(legacyDirectory);
            var legacyPath = Path.Combine(legacyDirectory, "SYSTEM.md");
            var legacyLeadPath = Path.Combine(legacyDirectory, "ORCHESTRATOR.md");
            await File.WriteAllTextAsync(legacyPath, "External shared guidance", new UTF8Encoding(false));
            await File.WriteAllTextAsync(legacyLeadPath, "Owner's legacy lead guidance", new UTF8Encoding(false));
            var legacyStore = new StateStore(legacyRoot);
            var legacy = new SystemInstructions(legacyStore);
            var oldLegacy = legacy.Get();
            Check(oldLegacy.Content == "External shared guidance" && oldLegacy.Path.Contains("read only; saves to SQLite", StringComparison.Ordinal) &&
                legacy.CaptureEffective()[1].Content == "Owner's legacy lead guidance" &&
                legacyStore.List<JsonElement>(SystemInstructions.RecordKind).Count == 0 &&
                !File.Exists(legacy.PathForRole("lead")) && File.Exists(legacyLeadPath),
                "legacy files remain effective read-only fallbacks without migration or materialization");
            await File.WriteAllTextAsync(legacyPath, "External editor changed this file.", new UTF8Encoding(false));
            Check(Rejects(() => legacy.Save("overwrite external edit", oldLegacy.Sha256)) &&
                legacy.ReadForAgent() == "External editor changed this file.",
                "legacy edits invalidate effective CAS tokens before an override is saved");
            await File.WriteAllBytesAsync(legacyPath, [0xff, 0xfe, 0x41, 0x00]);
            Check(Rejects(() => legacy.Get()) && Rejects(() => legacy.CaptureEffective()),
                "invalid legacy UTF-8 fails instead of silently substituting default guidance");
            await File.WriteAllBytesAsync(legacyPath, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("BOM Markdown")).ToArray());
            var bom = legacy.Get();
            Check(bom.Content == "BOM Markdown", "legacy UTF-8 BOM content is accepted");
            await File.WriteAllTextAsync(legacyPath, "BOM Markdown", new UTF8Encoding(false));
            Check(legacy.Get().Sha256 != bom.Sha256, "legacy CAS binds exact source bytes including the BOM");
            await File.WriteAllBytesAsync(legacyPath, new byte[SystemInstructions.MaxUtf8Bytes + 1]);
            Check(Rejects(() => legacy.ReadForAgent()), "oversized legacy files remain bounded before entering model context");
            await File.WriteAllTextAsync(legacyPath, "bad\0legacy", new UTF8Encoding(false));
            Check(Rejects(() => legacy.Get()), "legacy NUL characters fail without being treated as a blank override");
            await File.WriteAllTextAsync(legacyPath, "Preserved legacy text.", new UTF8Encoding(false));
            var legacyBytes = await File.ReadAllBytesAsync(legacyPath);
            legacy.Save("New SQL override.", legacy.Get().Sha256);
            Check((await File.ReadAllBytesAsync(legacyPath)).SequenceEqual(legacyBytes) && legacy.ReadForAgent() == "New SQL override.",
                "saving a fallback creates an override while preserving the legacy file bytes");
            await File.WriteAllTextAsync(legacyPath, "Later manual legacy edit.", new UTF8Encoding(false));
            Check(legacy.ReadForAgent() == "New SQL override." && (await File.ReadAllTextAsync(legacyPath)) == "Later manual legacy edit.",
                "SQL source precedence is explicit and does not overwrite later legacy edits");
            legacy.Save("", legacy.Get().Sha256);
            Check(legacy.ReadForAgent() == "", "a blank SQL override takes precedence over a populated legacy file");
            await File.WriteAllTextAsync(legacy.PathForRole("lead"), "Canonical legacy lead.", new UTF8Encoding(false));
            Check(legacy.Get("lead").Content == "Canonical legacy lead." && (await File.ReadAllTextAsync(legacyLeadPath)) == "Owner's legacy lead guidance",
                "canonical lead fallback takes precedence without moving or deleting ORCHESTRATOR.md");

            Check(SystemInstructions.CerebralContent.Contains("Never ask the user to explain your own features", StringComparison.Ordinal) &&
                SystemInstructions.CerebralContent.Contains("reasonable reversible defaults", StringComparison.Ordinal) &&
                SystemInstructions.CerebralContent.Contains("Several distinct questions", StringComparison.Ordinal) &&
                SystemInstructions.CerebralContent.Contains("established character, location, timeline, plot fact", StringComparison.Ordinal) &&
                SystemInstructions.CerebralContent.Contains("public contracts, data behavior, compatibility, architecture", StringComparison.Ordinal),
                "lead defaults preserve question, writing and programming guidance");
            Check(SystemInstructions.DefaultContent.Contains("required question pauses only", StringComparison.Ordinal) &&
                SystemInstructions.AgentContent.Contains("Continue independent assigned work", StringComparison.Ordinal),
                "shared and helper defaults preserve the staged question policy");
            foreach (var role in roles.Skip(1))
            {
                var roleDocument = instructions.Get(role);
                Check(roleDocument.Content == SystemInstructions.DefaultForRole(role) && !File.Exists(instructions.PathForRole(role)),
                    role + " get preserves the built-in fallback without creating a file");
                var edited = instructions.Save("Owner's " + role + " guidance", roleDocument.Sha256, role);
                Check(instructions.ReadForAgent(role) == edited.Content && instructions.ReadForAgent() == "Owner maintained content.",
                    role + " SQL edits preserve the shared layer");
                Check(Rejects(() => instructions.Save("stale role", roleDocument.Sha256, role)),
                    role + " stale editor cannot overwrite current guidance");
            }
            var layered = SystemInstructions.ComposeBehaviorLayers("APP", "SHARED", instructions.ReadForAgent("helper"), "PROFILE");
            Check(layered.Contains("Owner's helper guidance", StringComparison.Ordinal) && !layered.Contains("Owner's lead guidance", StringComparison.Ordinal) &&
                layered.StartsWith("APP", StringComparison.Ordinal) &&
                layered.IndexOf(SystemInstructions.ApplicationBoundary, StringComparison.Ordinal) < layered.IndexOf("SHARED", StringComparison.Ordinal) &&
                layered.IndexOf("SHARED", StringComparison.Ordinal) < layered.IndexOf("Owner's helper", StringComparison.Ordinal) &&
                layered.IndexOf("Owner's helper", StringComparison.Ordinal) < layered.IndexOf("PROFILE", StringComparison.Ordinal),
                "the stable protocol boundary precedes shared, selected role, and profile layers in order");
            var emptyLayers = SystemInstructions.ComposeBehaviorLayers("APP", "", "", "");
            Check(!emptyLayers.Contains("Shared system guidance", StringComparison.Ordinal) &&
                !emptyLayers.Contains("Assignment role guidance", StringComparison.Ordinal) &&
                !emptyLayers.Contains("Agent profile guidance", StringComparison.Ordinal),
                "empty optional behavior layers add no headings or placeholder prompt text");
            Check(Rejects(() => instructions.Get("../SYSTEM")) && Rejects(() => instructions.ReadForAgent("unknown")) &&
                Rejects(() => SystemInstructions.ValidatePrompt("", "")) && Rejects(() => SystemInstructions.ValidatePrompt("Lead", "")) &&
                Rejects(() => SystemInstructions.ValidatePrompt("system", null!)),
                "typed restore validation rejects missing, malformed or unknown roles and null content");

            var restoreStore = new StateStore(Path.Combine(root, "restore"));
            var restore = new SystemInstructions(restoreStore);
            var beforeRestore = restore.CaptureEffective();
            Check(Rejects(() => restoreStore.Locked<int>(() =>
            {
                restoreStore.Put("settings", "transaction-marker", "should roll back");
                restore.UpsertForRestore("system", "Transactional override");
                restore.UpsertForRestore("lead", "");
                throw new InvalidOperationException("Abort the enclosing restore.");
            })) && restoreStore.Get<string>("settings", "transaction-marker") is null &&
                restore.CaptureEffective().SequenceEqual(beforeRestore) &&
                restoreStore.List<JsonElement>(SystemInstructions.RecordKind).Count == 0,
                "prompt upserts and other settings roll back together in the caller's SQLite transaction");
            restoreStore.Locked(() =>
            {
                foreach (var role in roles) restore.UpsertForRestore(role, role == "helper" ? "" : "Restored " + role);
                return true;
            });
            var restored = restore.CaptureEffective();
            Check(restored.All(x => x.Content == (x.Role == "helper" ? "" : "Restored " + x.Role)) &&
                !Directory.Exists(Path.Combine(restoreStore.DirectoryPath, "Instructions")),
                "all four prompt roles commit atomically with an explicit blank and no legacy writes");
            Check(Rejects(() => restoreStore.Locked<int>(() =>
            {
                restore.UpsertForRestore("system", "Must roll back");
                restore.UpsertForRestore("invalid-role", "Invalid");
                return 1;
            })) && restore.CaptureEffective().SequenceEqual(restored),
                "a later invalid role rolls back earlier upserts and their revision tokens");
            var restoredToken = restored[0].Sha256;
            restore.UpsertForRestore("system", "Restored system");
            Check(restore.Get().Sha256 != restoredToken && Rejects(() => restore.Save("Stale after restore", restoredToken)),
                "same-content restore invalidates previously open prompt editors");

            var events = new EventHub(); var configuration = new Configuration(store);
            await using var runtime = new ModelRuntime(store, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get);
            using var scheduler = new JobScheduler(store, configuration, new NoExecutor(), runtime, events, NullLogger<JobScheduler>.Instance);
            var commands = new CommandDispatcher(store, configuration, runtime, scheduler, tools, events);
            var profile = new AgentProfile { Id = "ordinary", Capabilities = ["filesystem.read", "filesystem.write"], AllowedTools = ["*"] };
            store.Put("agent", profile.Id, profile);
            var deniedGet = await commands.ExecuteAsync(new("instructions.get", CerebralJson.Element(new { }), profile.Id));
            var deniedSave = await commands.ExecuteAsync(new("instructions.save", CerebralJson.Element(new { content = "agent rewrite", expectedSha256 = instructions.Get().Sha256 }), profile.Id));
            Check(!deniedGet.Success && !deniedSave.Success && instructions.Get().Content == "Owner maintained content.",
                "ordinary agent grants cannot access prompt management commands");
            var ownerGet = await commands.ExecuteAsync(new("instructions.get", CerebralJson.Element(new { })));
            var ownerDocument = CerebralJson.Read<SystemInstructionDocument>(ownerGet.Data);
            var ownerSave = await commands.ExecuteAsync(new("instructions.save", CerebralJson.Element(new { content = "Changed through the shared command.", expectedSha256 = ownerDocument.Sha256 })));
            Check(ownerGet.Success && ownerSave.Success && instructions.ReadForAgent() == "Changed through the shared command.",
                "existing owner get/save commands persist SQLite prompts");
            var cleared = await commands.ExecuteAsync(new("instructions.save", CerebralJson.Element(new { content = "", expectedSha256 = CerebralJson.Read<SystemInstructionDocument>(ownerSave.Data).Sha256 })));
            Check(cleared.Success && instructions.ReadForAgent() == "", "owner save retains an explicitly blank layer");
            var leadGet = await commands.ExecuteAsync(new("instructions.get", CerebralJson.Element(new { role = "lead" })));
            Check(leadGet.Success && CerebralJson.Read<SystemInstructionDocument>(leadGet.Data).Content == "Owner's lead guidance",
                "owner role selection reads the correct SQLite layer");
            var helperDocument = instructions.Get("helper");
            var helperSave = await commands.ExecuteAsync(new("instructions.save", CerebralJson.Element(new { role = "helper", content = "Helper command edit", expectedSha256 = helperDocument.Sha256 })));
            Check(helperSave.Success && instructions.ReadForAgent("helper") == "Helper command edit" && instructions.ReadForAgent("lead") == "Owner's lead guidance",
                "owner helper edits preserve other roles");
            var initialProfile = await commands.ExecuteAsync(new("agents.instructions.get", CerebralJson.Element(new { profileId = profile.Id })));
            var profileDocument = CerebralJson.Read<AgentInstructionDocument>(initialProfile.Data);
            var profileSave = await commands.ExecuteAsync(new("agents.instructions.save",
                CerebralJson.Element(new AgentInstructionSaveRequest(profile.Id, "Profile-only prompt", profileDocument.Revision))));
            var profileSaved = CerebralJson.Read<AgentInstructionDocument>(profileSave.Data);
            var afterProfileSave = store.Get<AgentProfile>("agent", profile.Id)!;
            Check(profileSave.Success && profileSaved.Revision == profileDocument.Revision + 1 &&
                afterProfileSave.Instructions == "Profile-only prompt" && afterProfileSave.Capabilities.SequenceEqual(profile.Capabilities) &&
                afterProfileSave.AllowedTools.SequenceEqual(profile.AllowedTools),
                "narrow owner profile prompt edits advance revision and preserve other settings and grants");
            Check(!(await commands.ExecuteAsync(new("agents.instructions.save",
                CerebralJson.Element(new AgentInstructionSaveRequest(profile.Id, "Stale prompt", profileDocument.Revision))))).Success &&
                store.Get<AgentProfile>("agent", profile.Id)!.Instructions == "Profile-only prompt",
                "a stale profile instruction revision cannot overwrite a newer prompt");
            profile.Capabilities.Add("administration"); profile.Revision = afterProfileSave.Revision; store.Put("agent", profile.Id, profile);
            Check(!(await commands.ExecuteAsync(new("agents.instructions.get", CerebralJson.Element(new { profileId = profile.Id }), profile.Id))).Success &&
                !(await commands.ExecuteAsync(new("agents.instructions.save",
                    CerebralJson.Element(new AgentInstructionSaveRequest(profile.Id, "agent rewrite", profile.Revision)), profile.Id))).Success,
                "narrow profile prompt management requires owner route even for administrative profiles");
            foreach (var invalidRequest in new object[]
            {
                new { profileId = profile.Id, content = "text", expectedRevision = profile.Revision, capabilities = new[] { "*" } },
                new AgentInstructionSaveRequest(profile.Id, new string('界', 12000), profile.Revision),
                new AgentInstructionSaveRequest(profile.Id, "bad\0text", profile.Revision),
                new AgentInstructionSaveRequest(profile.Id, "", -1)
            })
                Check(!(await commands.ExecuteAsync(new("agents.instructions.save", CerebralJson.Element(invalidRequest)))).Success,
                    "profile prompt endpoint rejects unsupported fields and invalid text or revisions");
            var clearProfile = await commands.ExecuteAsync(new("agents.instructions.save",
                CerebralJson.Element(new AgentInstructionSaveRequest(profile.Id, "", profile.Revision))));
            Check(clearProfile.Success && store.Get<AgentProfile>("agent", profile.Id)!.Instructions == "",
                "profile prompt autosave can intentionally clear only that instruction layer");
            Console.WriteLine($"Shared and role instructions: {assertions} checks passed (real SQLite transactions, UTF-8 fallback files and command authorization; no model inference).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporaryRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact temporary instruction-test workspace.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Instruction tests do not execute a model.");
    }
}
