using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class ConfigurationBackupTests
{
    public static Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-config-backup-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0; Exception? failure = null;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); checks++; }
        string Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
            { checks++; return error.Message; }
            throw new Exception("FAIL: " + label);
        }
        try
        {
            static IReadOnlyDictionary<string, ServicePluginPortableSettingsBinding> Schemas(string packageVersion = "1.0.0",
                int settingsVersion = 1, int maximumUtf8Bytes = 64) => new Dictionary<string, ServicePluginPortableSettingsBinding>(StringComparer.Ordinal)
            {
                ["example.plugin"] = ServicePluginPortableSettingsPolicy.Freeze("example.plugin", packageVersion,
                    new ServicePluginPortableSettings
                    {
                        Version = settingsVersion,
                        Fields = [new() { Name = "portable", Type = "string", Required = true, MaximumUtf8Bytes = maximumUtf8Bytes }]
                    })
            };
            var sourceSchemas = Schemas();
            var destinationSchemas = Schemas("1.1.0");
            var source = Seed(Path.Combine(root, "source"), "source-private");
            source.Put("settings", "interface", new InterfacePreferences
            {
                Revision = 3, EnterSends = true, NavigationVisible = false, PanelsVisible = true, SelectedSurface = "files",
                AppearanceTheme = AppearanceThemePolicy.Dark, MessageTimeFormat = MessageTimeFormatPolicy.TwentyFourHour, RecentChatLimit = 15
            });
            var interfaceStore = new InterfacePreferencesStore(source);
            Check(interfaceStore.Set(CerebralJson.Element(new
                { name = InterfacePreferencePolicy.AppearanceTheme, value = AppearanceThemePolicy.Light })).AppearanceTheme == AppearanceThemePolicy.Light &&
                interfaceStore.Set(CerebralJson.Element(new
                { name = InterfacePreferencePolicy.AppearanceTheme, value = AppearanceThemePolicy.Dark })).AppearanceTheme == AppearanceThemePolicy.Dark,
                "appearance changes autosave through the narrow revisioned interface-preference patch");
            Reject(() => interfaceStore.Set(CerebralJson.Element(new
                { name = InterfacePreferencePolicy.AppearanceTheme, value = "neon" })),
                "the interface preference patch rejects unsupported future skin IDs until the core contract admits them");
            Check(interfaceStore.Set(CerebralJson.Element(new
                { name = InterfacePreferencePolicy.MessageTimeFormat, value = MessageTimeFormatPolicy.TwelveHour })).MessageTimeFormat == MessageTimeFormatPolicy.TwelveHour &&
                interfaceStore.Set(CerebralJson.Element(new
                { name = InterfacePreferencePolicy.MessageTimeFormat, value = MessageTimeFormatPolicy.TwentyFourHour })).MessageTimeFormat == MessageTimeFormatPolicy.TwentyFourHour,
                "message time format changes autosave through the narrow revisioned interface-preference patch");
            Reject(() => interfaceStore.Set(CerebralJson.Element(new
                { name = InterfacePreferencePolicy.MessageTimeFormat, value = "seconds-since-midnight" })),
                "the interface preference patch rejects unsupported message time formats");
            Check(RecentChatPolicy.Effective(new InterfacePreferences().RecentChatLimit) == 20 &&
                new InterfacePreferencesStore(new StateStore(Path.Combine(root, "source"))).Get().RecentChatLimit == 15,
                "recent chat defaults remain20 and the configured preference survives a reopened state store");
            foreach (var recentLimit in new[] { 1, 200, 15 })
                Check(interfaceStore.Set(CerebralJson.Element(new { name = InterfacePreferencePolicy.RecentChatLimit, value = recentLimit })).RecentChatLimit == recentLimit &&
                    interfaceStore.Get().SelectedSurface == "files" && interfaceStore.Get().EnterSends == true,
                    "recent chat field patches admit inclusive bounds without clobbering other preferences");
            var recentBefore = JsonSerializer.Serialize(interfaceStore.Get(), CerebralJson.Options);
            foreach (var invalidRecent in new object[] { 0, 201, -1, 1.5, "20" })
            {
                Reject(() => interfaceStore.Set(CerebralJson.Element(new { name = InterfacePreferencePolicy.RecentChatLimit, value = invalidRecent })),
                    "recent chat patch rejects invalid bounds and noninteger values");
                Check(JsonSerializer.Serialize(interfaceStore.Get(), CerebralJson.Options) == recentBefore,
                    "a rejected recent chat patch preserves all saved interface choices and revision");
            }
            var sourceHost = source.Get<HostSettings>("settings", "host")!;
            sourceHost.EngineEventHistoryLimit = 3456; sourceHost.EngineEventHistoryMegabytes = 96;
            sourceHost.EngineEventHistoryDays = 45; sourceHost.MaxLogicalProcessorCount = 3;
            sourceHost.PromptCacheDiagnosticsEnabled = false; sourceHost.PromptCacheDiagnosticsChangePercentagePoints = 12;
            sourceHost.AllowedLogicalProcessors = [1, 3, 5, 7]; source.Put("settings", "host", sourceHost);
            var sourceModel = source.Get<ModelRecord>("model", "model")!;
            sourceModel.SourceUrl = "https://example.invalid/model.gguf?token=url-private";
            sourceModel.State = "error"; sourceModel.Error = "runtime-private"; sourceModel.Architecture = "old-observation";
            sourceModel.SizeBytes = 999; sourceModel.CapabilityStatus = [new() { Id = "image-input", Usable = true, Evidence = "evidence-private" }];
            source.Put("model", sourceModel.Id, sourceModel);
            var sourceInstructions = new SystemInstructions(source);
            sourceInstructions.UpsertForRestore("system", "Shared source guidance");
            sourceInstructions.UpsertForRestore("helper", "");
            var leadPath = sourceInstructions.PathForRole("lead");
            Directory.CreateDirectory(Path.GetDirectoryName(leadPath)!);
            File.WriteAllText(leadPath, "Existing lead file guidance");
            var sourceSkills = new SkillCatalog(source);
            var portableSkill = sourceSkills.Install(new SkillManifest
            {
                Id = "portable-review", Name = "Portable review", Version = "1.2.0",
                Description = "Review portable output.", Triggers = ["portable review"]
            }, "# Portable review\n\nReview the durable result.", "source fixture", null);
            sourceSkills.Configure("portable-review", true, [new("role", "lead")], portableSkill.Summary.Revision);
            var defaultBackup = new ConfigurationBackups(Seed(Path.Combine(root, "default-interface"), "default-private", entities: false),
                () => false).Export(owner: true);
            Check(defaultBackup.Document.InterfacePreferences?.RecentChatLimit == 20,
                "schema12 explicitly exports the effective20-chat default when no user override exists");
            var sourceAgent = source.Get<AgentProfile>("agent", "agent")!;
            sourceAgent.CompactionTriggerPercent = 65; source.Put("agent", sourceAgent.Id, sourceAgent);
            var sourceBefore = Saved(source);
            var backups = new ConfigurationBackups(source, () => false, pluginSettingsSchemas: () => sourceSchemas);
            var export = backups.Export(owner: true); var document = export.Document;
            var json = JsonSerializer.Serialize(export, CerebralJson.Options);
            Check(new[] { "source-private", "url-private", "runtime-private", "evidence-private", "old-observation", "accessKey", "llamaServerPath" }
                .All(value => !json.Contains(value, StringComparison.Ordinal)), "typed export omits credentials, connection arguments, opaque settings and observed runtime metadata");
            Check(document.Models.Single().SourceUrl is null && export.Attention.Any(x => x.Code == "unsafe-url-omitted") &&
                export.Attention.Any(x => x.Code == "opaque-settings-omitted"), "unsafe URL and opaque settings omissions are explicit without their values");
            Check(document.Agents.Single().PluginSettings?.Single() is { PluginId: "example.plugin", SettingsVersion: 1 } portable &&
                  portable.Values.GetProperty("portable").GetString() == "source-portable" &&
                  !portable.Values.TryGetProperty("private", out _),
                "export includes only fields admitted by the installed plugin's nonsecret portable schema");
            Check(document.Agents.Single().Instructions == "Owner-reviewed instructions" && export.RequiresTextReview &&
                export.Attention.Any(x => x.Section == "free-text"), "free text remains useful and requires review without promising secret scrubbing");
            Check(Saved(source) == sourceBefore && !Directory.Exists(sourceHost.ProjectsDirectory) && !Directory.Exists(sourceHost.ModelDirectory),
                "export reads SQLite without provisioning referenced data directories");
            Check(document.Prompts is { Count: 4 } && document.Prompts.Single(x => x.Role == "helper").Content == "" &&
                document.Prompts.Single(x => x.Role == "lead").Content == "Existing lead file guidance",
                "version 2 exports all effective SQL, legacy-file and built-in prompt layers including blank overrides");
            Check(document.SchemaVersion == 14 && document.Host is
                { EngineEventHistoryLimit: 3456, EngineEventHistoryMegabytes: 96, EngineEventHistoryDays: 45,
                  PromptCacheDiagnosticsEnabled: false, PromptCacheDiagnosticsChangePercentagePoints: 12,
                  MaxLogicalProcessorCount: 3, AllowedLogicalProcessors: [1, 3, 5, 7] } &&
                document.Paths.ToolsDirectory == sourceHost.ToolsDirectory &&
                document.InterfacePreferences is
                { EnterSends: true, NavigationVisible: false, PanelsVisible: true, SelectedSurface: "files", AppearanceTheme: "dark", RecentChatLimit: 15 } &&
                document.Skills is { Count: 2 } && document.Skills.Single(x => x.BuiltIn).Manifest.Id == "response-balance" &&
                document.Skills.Single(x => !x.BuiltIn) is { Content: not null, Enabled: true },
                "version 10 retains Engine limits, processor policy, portable appearance and interface choices, and bounded skill configuration");
            Check(!File.Exists(sourceInstructions.PathForRole("system")) && File.ReadAllText(leadPath) == "Existing lead file guidance",
                "export does not materialize or modify prompt files");
            Reject(() => backups.Export(false), "export requires installation owner");
            Reject(() => backups.Preview(CerebralJson.Element(new { unexpected = "private" }), false), "preview checks ownership before parsing");
            Reject(() => backups.Apply(CerebralJson.Element(new { }), false), "apply checks ownership before parsing");

            var previewInput = new ConfigurationRestorePreviewRequest(document);
            var invalid = JsonSerializer.SerializeToNode(previewInput, CerebralJson.Options)!.AsObject();
            invalid["document"]!["host"]!["accessKey"] = "input-private";
            var invalidMessage = Reject(() => backups.Preview(CerebralJson.Element(invalid), true), "unknown sensitive fields are rejected");
            Check(!invalidMessage.Contains("input-private", StringComparison.Ordinal), "invalid-field errors do not quote field values");
            var validJson = JsonSerializer.Serialize(previewInput, CerebralJson.Options);
            using (var duplicate = JsonDocument.Parse(validJson.Replace($"\"schemaVersion\": {ConfigurationBackupContract.SchemaVersion}",
                       $"\"schemaVersion\": {ConfigurationBackupContract.SchemaVersion}, \"schemaVersion\": {ConfigurationBackupContract.SchemaVersion}", StringComparison.Ordinal)))
                Reject(() => backups.Preview(duplicate.RootElement, true), "duplicate fields cannot override an admitted value");
            using (var casing = JsonDocument.Parse(validJson.Replace("\"schemaVersion\"", "\"SchemaVersion\"", StringComparison.Ordinal)))
                Reject(() => backups.Preview(casing.RootElement, true), "wrong-case aliases are not accepted as contract fields");
            var missing = JsonSerializer.SerializeToNode(previewInput, CerebralJson.Options)!.AsObject();
            missing["document"]!.AsObject().Remove("schemaVersion");
            Reject(() => backups.Preview(CerebralJson.Element(missing), true), "missing required constructor fields are rejected");
            var missingNullable = JsonSerializer.SerializeToNode(previewInput, CerebralJson.Options)!.AsObject();
            missingNullable["document"]!["paths"] = null;
            Reject(() => backups.Preview(CerebralJson.Element(missingNullable), true), "nonnull contract sections cannot be null");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with { Document = document with { SchemaVersion = 99 } }), true), "unsupported backup version is rejected");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with { Document = document with { Host = document.Host with { QueueCapacity = 0 } } }), true), "host limits are validated before preview admission");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with { Document = document with { Host = document.Host with { EngineEventHistoryMegabytes = 0 } } }), true), "Engine event retention limits are validated before preview admission");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { Host = document.Host with { EngineEventHistoryLimit = null } } }), true),
                "schema 7 requires every Engine event retention limit instead of silently preserving a destination value");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { Host = document.Host with { AllowedLogicalProcessors = null } } }), true),
                "schema 9 requires the processor policy instead of silently preserving a destination value");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { Host = document.Host with { AllowedLogicalProcessors = [1, 1] } } }), true),
                "portable processor policy rejects duplicate logical processor identifiers");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { InterfacePreferences = document.InterfacePreferences! with { SelectedSurface = "unknown" } } }), true),
                "portable interface preferences accept only stable core surfaces");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { InterfacePreferences = document.InterfacePreferences! with { AppearanceTheme = "neon" } } }), true),
                "portable interface preferences accept only core System, Light and Dark appearance choices");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { InterfacePreferences = document.InterfacePreferences! with { AppearanceTheme = null } } }), true),
                "schema 10 requires an explicit backward-compatible appearance value");
            foreach (var invalidRecent in new int?[] { null, 0, 201 })
                Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                    { Document = document with { InterfacePreferences = document.InterfacePreferences! with { RecentChatLimit = invalidRecent } } }), true),
                    "schema12 requires a bounded explicit recent chat limit");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { SchemaVersion = 11 } }), true),
                "schema11 cannot spoof the new recent chat preference");
            var version11 = document with
            {
                SchemaVersion = 11, Settings = null,
                InterfacePreferences = document.InterfacePreferences! with { RecentChatLimit = null }
            };
            Check(backups.Preview(CerebralJson.Element(previewInput with { Document = version11 }), true).PreviewId.Length > 0,
                "a genuine schema11 backup remains readable and preserves the destination recent preference");
            var portableEntry = document.Agents.Single().PluginSettings!.Single();
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { SchemaVersion = 5,
                    Host = document.Host with { EngineEventHistoryLimit = null, EngineEventHistoryMegabytes = null, EngineEventHistoryDays = null },
                    Agents = [document.Agents[0] with { PluginSettings = [] }] } }), true),
                "backup versions before 6 reject even an empty portable plugin settings section");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { Agents = [document.Agents[0] with { PluginSettings = [portableEntry with { Values = CerebralJson.Element(new { portable = "ok", undeclared = true }) }] }] } }), true),
                "portable plugin settings reject fields outside the installed declaration");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { Agents = [document.Agents[0] with { PluginSettings = [portableEntry with { Values = CerebralJson.Element(new { portable = 7 }) }] }] } }), true),
                "portable plugin settings reject values that do not match their declared type");
            Reject(() => backups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(
                document with { Paths = document.Paths with { SharedDirectory = document.Paths.ModelDirectory } },
                new ConfigurationRestorePathOverrides(SharedDirectory: Path.Combine(root, "mapped-shared")))), true),
                "one source root cannot silently choose between conflicting mapped destinations");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with { Document = document with { Agents = [document.Agents[0] with { ModelId = "absent" }] } }), true), "missing model references fail closed");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with { Document = document with { Agents = [document.Agents[0] with { ModelWorkerId = "absent-worker" }] } }), true), "unavailable remote connections are not invented");
            Reject(() => backups.Preview(CerebralJson.Element(previewInput with { Document = document with { Agents = [document.Agents[0] with { Role = "lead", FollowsDefaultModel = false }] } }), true), "lead profiles cannot bypass the normal default-model binding invariant");
            var capabilityMessage = Reject(() => backups.Preview(CerebralJson.Element(previewInput with
                { Document = document with { Agents = [document.Agents[0] with { RequiredModelCapabilities = ["capability-private"] }] } }), true),
                "unsupported model capabilities are rejected");
            Check(!capabilityMessage.Contains("capability-private", StringComparison.Ordinal), "profile validation does not echo opaque capability input");
            foreach (var path in new[] { "relative", Path.Combine(root, "..", "escape"), AppContext.BaseDirectory })
                Reject(() => backups.Preview(CerebralJson.Element(previewInput with { PathOverrides = new(ProjectsDirectory: path) }), true), "invalid or replaceable program data path is rejected");
            if (OperatingSystem.IsWindows())
                foreach (var path in new[] { @"\\?\C:\private", @"\\.\C:\private", @"C:\private:stream", "C:\\bad\tpath" })
                    Reject(() => backups.Preview(CerebralJson.Element(previewInput with { PathOverrides = new(ProjectsDirectory: path) }), true), "device, stream and control path syntax is rejected");
            var oversized = document with { Agents = Enumerable.Range(0, 40).Select(i => document.Agents[0] with { Id = "agent" + i, Instructions = new string('x', 32768) }).ToArray() };
            Reject(() => backups.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(oversized)), true), "document payload is bounded to one MiB");

            Reject(() => backups.Preview(CerebralJson.Element(previewInput with { Document = document with { Prompts = null } }), true), "version 2 requires an explicit prompt section");
            foreach (var prompts in new IReadOnlyList<ConfigurationBackupPrompt>[]
            {
                [new("unknown-role", "text")], [new("lead", "one"), new("lead", "two")],
                [new("system", "invalid\0content")], [new("system", new string('界', 12000))]
            })
                Reject(() => backups.Preview(CerebralJson.Element(previewInput with { Document = document with { Prompts = prompts } }), true),
                    "prompt roles, duplicates and strict text bounds are validated before preview");
            var destination = Seed(Path.Combine(root, "destination"), "destination-private");
            destination.Put("settings", "interface", new InterfacePreferences
            {
                Revision = 8, EnterSends = false, NavigationVisible = true, PanelsVisible = false, SelectedSurface = "chat",
                AppearanceTheme = AppearanceThemePolicy.Light, MessageTimeFormat = MessageTimeFormatPolicy.TwelveHour
            });
            var destinationInstructions = new SystemInstructions(destination);
            destinationInstructions.UpsertForRestore("system", "Destination guidance");
            var destinationHost = destination.Get<HostSettings>("settings", "host")!;
            destinationHost.QueueCapacity = 11; destination.Put("settings", "host", destinationHost);
            var extraModel = new ModelRecord { Id = "unrelated-model", Name = "Unrelated", Path = Path.Combine(root, "unrelated.gguf") };
            var extraAgent = new AgentProfile { Id = "unrelated-agent", Name = "Unrelated", ModelId = extraModel.Id, Instructions = "Keep me" };
            destination.Put("model", extraModel.Id, extraModel); destination.Put("agent", extraAgent.Id, extraAgent);
            var secretPath = Path.Combine(destination.DirectoryPath, "local-authorization.fixture");
            File.WriteAllText(secretPath, "destination-private");
            var mapping = new ConfigurationRestorePathOverrides(Path.Combine(root, "portable", "models"),
                Path.Combine(root, "portable", "projects"), Path.Combine(root, "portable", "shared"),
                Path.Combine(root, "portable", "tools"));
            var notifications = 0; var busy = false;
            var restore = new ConfigurationBackups(destination, () => busy, () =>
            {
                if (new StateStore(destination.DirectoryPath).Get<HostSettings>("settings", "host")!.QueueCapacity != document.Host.QueueCapacity)
                    throw new Exception("Restore notification preceded its database commit.");
                notifications++;
            }, pluginSettingsSchemas: () => destinationSchemas);
            var destinationBefore = Saved(destination);
            var preview = restore.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document, mapping)), true);
            Check(preview.Mode == "upsert" && preview.UpdatedAgents.SequenceEqual(["agent"]) && preview.UpdatedModels.SequenceEqual(["model"]) &&
                preview.Attention.Any(x => x.Code == "ordinary-grants-review") && preview.Attention.Any(x => x.Code == "model-file-missing") &&
                preview.UpdatedPromptRoles?.Count == 4 && preview.UpdatedSkillIds?.Count == 2 &&
                preview.Attention.Any(x => x.Code == "plugin-package-version-changed"),
                "preview reports selected upserts, ordinary grants and missing model payloads");
            Check(!JsonSerializer.Serialize(preview, CerebralJson.Options).Contains("destination-private", StringComparison.Ordinal) &&
                Saved(destination) == destinationBefore && !Directory.Exists(Path.Combine(root, "portable")), "preview neither leaks local values nor provisions directories");
            var apply = new ConfigurationRestoreApplyRequest(preview.PreviewId, preview.ExpectedHostRevision, true);
            Reject(() => restore.Apply(CerebralJson.Element(apply with { ReviewedFreeText = false }), true), "apply requires the owner text and grant review acknowledgement");
            Reject(() => restore.Apply(CerebralJson.Element(apply with { ExpectedHostRevision = apply.ExpectedHostRevision + 1 }), true), "apply binds the approved host revision");
            busy = true; Reject(() => restore.Apply(CerebralJson.Element(apply), true), "active work blocks restore inside its transaction"); busy = false;
            Check(Saved(destination) == destinationBefore && notifications == 0, "denied apply leaves every selected and unrelated record unchanged");
            var applied = restore.Apply(CerebralJson.Element(apply), true);
            var actualHost = destination.Get<HostSettings>("settings", "host")!;
            var actualAgent = destination.Get<AgentProfile>("agent", "agent")!;
            Check(document.Agents.Single().CompactionTriggerPercent == 65 && actualAgent.CompactionTriggerPercent == 65 &&
                new StateStore(Path.Combine(root, "destination")).Get<AgentProfile>("agent", "agent")!.CompactionTriggerPercent == 65,
                "configured early compaction trigger survives portable export, restore and a reopened store");
            var actualModel = destination.Get<ModelRecord>("model", "model")!;
            var actualInterface = destination.Get<InterfacePreferences>("settings", "interface")!;
            Check(applied.HostRevision == destinationHost.Revision + 1 && actualAgent.Revision == 5 && notifications == 1,
                "one committed restore increments local revisions and notifies once");
            Check(actualHost.QueueCapacity == document.Host.QueueCapacity && actualHost.ProjectsDirectory == mapping.ProjectsDirectory &&
                actualHost.ToolsDirectory == mapping.ToolsDirectory &&
                actualHost.EngineEventHistoryLimit == 3456 && actualHost.EngineEventHistoryMegabytes == 96 && actualHost.EngineEventHistoryDays == 45 &&
                actualHost.MaxLogicalProcessorCount == 3 && actualHost.AllowedLogicalProcessors.SequenceEqual([1, 3, 5, 7]) &&
                !actualHost.PromptCacheDiagnosticsEnabled && actualHost.PromptCacheDiagnosticsChangePercentagePoints == 12 &&
                actualModel.Path == Path.Combine(mapping.ModelDirectory!, "model.gguf") && actualAgent.AllowedRoots.SequenceEqual([mapping.ProjectsDirectory!]),
                "portable host limits and path overrides restore together");
            Check(actualHost.AccessKey == "destination-private" && actualHost.Workers[0].AccessKey == "destination-private" &&
                actualHost.McpServers[0].Arguments.Contains("destination-private") && actualHost.Executables[0].PrefixArguments.Contains("destination-private") &&
                actualHost.LlamaServerPath == destinationHost.LlamaServerPath && actualHost.ExternalCommandsEnabled == destinationHost.ExternalCommandsEnabled,
                "current credentials, MCP/executable settings and runtime paths are retained exactly");
            Check(actualAgent.PluginSettings["example.plugin"].GetProperty("private").GetString() == "destination-private" &&
                  actualAgent.PluginSettings["example.plugin"].GetProperty("portable").GetString() == "source-portable" &&
                  applied.PortablePluginSettingsRestored == 1 && File.ReadAllText(secretPath) == "destination-private",
                "declared portable plugin values update while opaque local fields and protected state remain unchanged");
            Check(actualModel.State == "unloaded" && actualModel.Architecture.Length == 0 && actualModel.Error is null && actualModel.SizeBytes == 0 && actualModel.CapabilityStatus.Count == 0,
                "restore never adopts runtime state or unverified model capability claims");
            Check(destination.Get<AgentProfile>("agent", extraAgent.Id)!.Instructions == "Keep me" && destination.Get<ModelRecord>("model", extraModel.Id) is not null &&
                !Directory.Exists(Path.Combine(root, "portable")), "upsert preserves unrelated registrations and keeps referenced folders lazy");
            Check(applied.PromptRoles?.Count == 4 && destinationInstructions.ReadForAgent() == "Shared source guidance" &&
                destinationInstructions.ReadForAgent("helper") == "" && destinationInstructions.ReadForAgent("lead") == "Existing lead file guidance",
                "restore adopts shared and role prompts in SQLite together with settings, preserving intentional empty layers");
            Check(applied.InterfacePreferencesRestored && actualInterface.Revision == 9 && actualInterface.EnterSends == true &&
                actualInterface.NavigationVisible == false && actualInterface.PanelsVisible == true && actualInterface.SelectedSurface == "files" &&
                actualInterface.AppearanceTheme == AppearanceThemePolicy.Dark && actualInterface.MessageTimeFormat == MessageTimeFormatPolicy.TwentyFourHour && actualInterface.RecentChatLimit == 15,
                "restore atomically adopts typed portable composer, layout, appearance and message-time preferences while incrementing the destination revision");
            var restoredSkill = new SkillCatalog(destination).Inspect("portable-review");
            Check(applied.SkillsRestored == 2 && restoredSkill.Content.Contains("durable result", StringComparison.Ordinal) &&
                  restoredSkill.Summary.Bindings.SequenceEqual([new SkillBinding("role", "lead")]),
                "restore atomically adopts portable skill content and scope while preserving built-in ownership");
            Check(!Directory.Exists(Path.Combine(destination.DirectoryPath, "Instructions")) &&
                new SystemInstructions(new StateStore(destination.DirectoryPath)).ReadForAgent("lead") == "Existing lead file guidance",
                "restored prompts survive reopening without creating instruction files");

            var unicodeSource = Seed(Path.Combine(root, "unicode-source"), "unicode-source-private");
            var unicodeAgent = unicodeSource.Get<AgentProfile>("agent", "agent")!;
            var unicodeValue = new string('Ж', 10_000);
            unicodeAgent.PluginSettings["example.plugin"] = CerebralJson.Element(new { portable = unicodeValue });
            Configuration.ValidateProfile(unicodeAgent);
            unicodeSource.Put("agent", unicodeAgent.Id, unicodeAgent);
            var unicodeSchemas = Schemas(maximumUtf8Bytes: 32_768);
            var unicodeDocument = new ConfigurationBackups(unicodeSource, () => false,
                pluginSettingsSchemas: () => unicodeSchemas).Export(true).Document;
            var unicodeDestination = Seed(Path.Combine(root, "unicode-destination"), "unicode-destination-private");
            var unicodeRestore = new ConfigurationBackups(unicodeDestination, () => false,
                pluginSettingsSchemas: () => unicodeSchemas);
            var unicodePreview = unicodeRestore.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(unicodeDocument)), true);
            unicodeRestore.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(
                unicodePreview.PreviewId, unicodePreview.ExpectedHostRevision, true)), true);
            Check(unicodeDestination.Get<AgentProfile>("agent", "agent")!.PluginSettings["example.plugin"]
                    .GetProperty("portable").GetString() == unicodeValue,
                "near-boundary Unicode portable settings use one canonical export and restore size calculation");

            var oversizedUnicodeSource = Seed(Path.Combine(root, "unicode-oversized-source"), "unicode-oversized-private");
            var oversizedUnicodeAgent = oversizedUnicodeSource.Get<AgentProfile>("agent", "agent")!;
            oversizedUnicodeAgent.PluginSettings["example.plugin"] = CerebralJson.Element(new { portable = new string('Ж', 11_000) });
            // This represents older admitted state whose semantic string satisfies the schema but whose canonical
            // JSON exceeds the portable entry bound. Export must omit it instead of creating an unrestorable backup.
            oversizedUnicodeSource.Put("agent", oversizedUnicodeAgent.Id, oversizedUnicodeAgent);
            var oversizedUnicodeExport = new ConfigurationBackups(oversizedUnicodeSource, () => false,
                pluginSettingsSchemas: () => unicodeSchemas).Export(true);
            Check(oversizedUnicodeExport.Document.Agents.Single().PluginSettings?.Count == 0 &&
                  oversizedUnicodeExport.Attention.Any(item => item.Code == "invalid-portable-settings-omitted"),
                "canonical portable payloads above 64 KiB are omitted during export rather than failing later restore");

            var duplicateDestination = Seed(Path.Combine(root, "duplicate-destination"), "duplicate-destination-private");
            var duplicateDestinationAgent = duplicateDestination.Get<AgentProfile>("agent", "agent")!;
            using (var duplicateDestinationJson = JsonDocument.Parse(
                       "{\"portable\":\"destination-portable\",\"private\":\"first\",\"private\":\"second\"}"))
                duplicateDestinationAgent.PluginSettings["example.plugin"] = duplicateDestinationJson.RootElement.Clone();
            duplicateDestination.Put("agent", duplicateDestinationAgent.Id, duplicateDestinationAgent);
            var duplicateDestinationBefore = Saved(duplicateDestination);
            var duplicateRestore = new ConfigurationBackups(duplicateDestination, () => false,
                pluginSettingsSchemas: () => destinationSchemas);
            Reject(() => duplicateRestore.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document)), true),
                "restore rejects duplicate opaque destination fields instead of collapsing them during merge");
            Check(Saved(duplicateDestination) == duplicateDestinationBefore,
                "rejected duplicate opaque destination fields remain byte-for-byte represented in durable configuration");

            using (var duplicatePluginJson = JsonDocument.Parse("{\"outer\":{\"value\":1,\"value\":2}}"))
            {
                var duplicateProfile = unicodeDestination.Get<AgentProfile>("agent", "agent")!;
                duplicateProfile.PluginSettings["duplicate.plugin"] = duplicatePluginJson.RootElement.Clone();
                Reject(() => Configuration.ValidateProfile(duplicateProfile),
                    "profile admission rejects recursively duplicated opaque plugin fields");
            }
            var replay = restore.Apply(CerebralJson.Element(apply), true);
            Check(JsonSerializer.Serialize(replay, CerebralJson.Options) == JsonSerializer.Serialize(applied, CerebralJson.Options) &&
                destination.Get<HostSettings>("settings", "host")!.Revision == applied.HostRevision && notifications == 1,
                "exact retry returns the original committed receipt without changing revisions or notifying again");
            Check(new StateStore(destination.DirectoryPath).Get<HostSettings>("settings", "host")!.ProjectsDirectory == mapping.ProjectsDirectory,
                "restored configuration survives a new store instance");

            var empty = Seed(Path.Combine(root, "fresh"), "fresh-private", entities: false);
            var fresh = new ConfigurationBackups(empty, () => false, pluginSettingsSchemas: () => destinationSchemas);
            var freshPreview = fresh.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(document, mapping)), true);
            Check(freshPreview.AddedAgents.SequenceEqual(["agent"]) && freshPreview.AddedModels.SequenceEqual(["model"]), "a new machine store can preview portable registrations without source credentials");
            fresh.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(freshPreview.PreviewId, freshPreview.ExpectedHostRevision, true)), true);
            var freshSettings = empty.Get<AgentProfile>("agent", "agent")!.PluginSettings["example.plugin"];
            Check(empty.Get<HostSettings>("settings", "host")!.AccessKey == "fresh-private" &&
                  freshSettings.GetProperty("portable").GetString() == "source-portable" && !freshSettings.TryGetProperty("private", out _),
                "fresh restore uses local identity and imports only declared portable plugin settings");
            Check(empty.Get<InterfacePreferences>("settings", "interface") is { Revision: 1, SelectedSurface: "files", AppearanceTheme: "dark", RecentChatLimit: 15 } &&
                new InterfacePreferencesStore(new StateStore(empty.DirectoryPath)).Get().RecentChatLimit == 15,
                "portable interface choices restore into a fresh service store without reading desktop files");

            var legacyDestination = Seed(Path.Combine(root, "legacy-v6"), "legacy-private");
            legacyDestination.Put("settings", "interface", new InterfacePreferences { Revision = 1, RecentChatLimit = 25 });
            var legacyHost = legacyDestination.Get<HostSettings>("settings", "host")!;
            legacyHost.EngineEventHistoryLimit = 321; legacyHost.EngineEventHistoryMegabytes = 22;
            legacyHost.EngineEventHistoryDays = 44; legacyHost.MaxLogicalProcessorCount = 2;
            legacyHost.PromptCacheDiagnosticsEnabled = true; legacyHost.PromptCacheDiagnosticsChangePercentagePoints = 18;
            legacyHost.AllowedLogicalProcessors = [2, 4]; legacyDestination.Put("settings", "host", legacyHost);
            var legacyDocument = document with
            {
                SchemaVersion = 6, Settings = null,
                InterfacePreferences = document.InterfacePreferences! with { RecentChatLimit = null },
                Skills = null,
                Host = document.Host with
                {
                    EngineEventHistoryLimit = null, EngineEventHistoryMegabytes = null, EngineEventHistoryDays = null
                }
            };
            var legacyRestore = new ConfigurationBackups(legacyDestination, () => false,
                pluginSettingsSchemas: () => destinationSchemas);
            var legacyPreview = legacyRestore.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(legacyDocument)), true);
            legacyRestore.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(
                legacyPreview.PreviewId, legacyPreview.ExpectedHostRevision, true)), true);
            var restoredLegacyHost = legacyDestination.Get<HostSettings>("settings", "host")!;
            Check(restoredLegacyHost.PromptCacheDiagnosticsEnabled && restoredLegacyHost.PromptCacheDiagnosticsChangePercentagePoints == 18,
                "backups predating schema13 preserve destination optional cache diagnostic settings");
            Check(restoredLegacyHost.EngineEventHistoryLimit == 321 &&
                restoredLegacyHost.EngineEventHistoryMegabytes == 22 && restoredLegacyHost.EngineEventHistoryDays == 44 &&
                restoredLegacyHost.MaxLogicalProcessorCount == 2 && restoredLegacyHost.AllowedLogicalProcessors.SequenceEqual([2, 4]),
                "version 6 backups preserve destination Engine retention and processor settings that were absent from that schema");
            Check(legacyDestination.Get<InterfacePreferences>("settings", "interface")?.AppearanceTheme == AppearanceThemePolicy.System,
                "backup versions before 10 ignore attached appearance data and use the destination's backward-compatible System default");
            Check(legacyDestination.Get<InterfacePreferences>("settings", "interface")?.RecentChatLimit == 25,
                "a backup without a recent chat preference preserves the destination choice");

            var staleStore = Seed(Path.Combine(root, "stale"), "stale-private");
            var stale = new ConfigurationBackups(staleStore, () => false);
            var stalePreview = stale.Preview(CerebralJson.Element(previewInput), true);
            var modified = staleStore.Get<AgentProfile>("agent", "agent")!; modified.MaxSteps++;
            staleStore.Put("agent", modified.Id, modified);
            Reject(() => stale.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(stalePreview.PreviewId, stalePreview.ExpectedHostRevision, true)), true),
                "profile content changes invalidate preview even without a host or profile revision bump");
            stalePreview = stale.Preview(CerebralJson.Element(previewInput), true);
            staleStore.Put("model", extraModel.Id, extraModel);
            Reject(() => stale.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(stalePreview.PreviewId, stalePreview.ExpectedHostRevision, true)), true),
                "registration set changes invalidate preview");
            stalePreview = stale.Preview(CerebralJson.Element(previewInput), true);
            var retuned = staleStore.Get<ModelRecord>("model", "model")!; retuned.ContextTokens = 8192; staleStore.Put("model", retuned.Id, retuned);
            Reject(() => stale.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(stalePreview.PreviewId, stalePreview.ExpectedHostRevision, true)), true),
                "model preference changes invalidate preview without a model revision field");
            stalePreview = stale.Preview(CerebralJson.Element(previewInput), true);
            var rotated = staleStore.Get<HostSettings>("settings", "host")!; rotated.AccessKey = "rotated-private"; staleStore.Put("settings", "host", rotated);
            var rotationError = Reject(() => stale.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(stalePreview.PreviewId, stalePreview.ExpectedHostRevision, true)), true),
                "local identity rotation invalidates the preview instead of restoring its cached old identity");
            Check(!rotationError.Contains("rotated-private", StringComparison.Ordinal), "stale-preview errors do not expose current protected values");
            stalePreview = stale.Preview(CerebralJson.Element(previewInput), true);
            var staleInstructions = new SystemInstructions(staleStore);
            var oldPrompt = staleInstructions.Get("helper");
            staleInstructions.Save("Temporary change", oldPrompt.Sha256, "helper");
            staleInstructions.Save(oldPrompt.Content, staleInstructions.Get("helper").Sha256, "helper");
            Reject(() => stale.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(stalePreview.PreviewId, stalePreview.ExpectedHostRevision, true)), true),
                "a prompt edited and reverted after preview still invalidates its original revision");
            var loaded = staleStore.Get<ModelRecord>("model", "model")!; loaded.State = "loaded"; staleStore.Put("model", loaded.Id, loaded);
            stalePreview = stale.Preview(CerebralJson.Element(previewInput), true);
            Reject(() => stale.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(stalePreview.PreviewId, stalePreview.ExpectedHostRevision, true)), true),
                "saved loaded model state blocks restore even when caller busy callback is false");

            var rollbackStore = Seed(Path.Combine(root, "rollback"), "rollback-private");
            var rollback = new ConfigurationBackups(rollbackStore, () => false);
            var changed = document with { Agents = [document.Agents[0] with { Name = "Changed" }], Models = [document.Models[0] with { Name = "Changed" }] };
            var rollbackPreview = rollback.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(changed, mapping)), true);
            var rollbackRequest = new ConfigurationRestoreApplyRequest(rollbackPreview.PreviewId, rollbackPreview.ExpectedHostRevision, true);
            var rollbackBefore = Saved(rollbackStore);
            Sql(rollbackStore, "CREATE TRIGGER fail_restore BEFORE UPDATE ON records WHEN NEW.kind='settings' BEGIN SELECT RAISE(ABORT,'fixture rollback'); END;");
            var faulted = false;
            try { rollback.Apply(CerebralJson.Element(rollbackRequest), true); } catch (SqliteException) { faulted = true; }
            Check(faulted && Saved(rollbackStore) == rollbackBefore, "database failure after agent/model writes rolls back the entire settings transaction");
            Sql(rollbackStore, "DROP TRIGGER fail_restore;");
            rollback.Apply(CerebralJson.Element(rollbackRequest), true);
            Check(rollbackStore.Get<AgentProfile>("agent", "agent")!.Name == "Changed", "failed transaction retains its valid preview for an explicit retry");

            var beforeLegacyInterface = empty.Get<InterfacePreferences>("settings", "interface")!;
            var v1 = document with
            {
                SchemaVersion = 1, Settings = null, Prompts = null, Connections = null, InterfacePreferences = null, Skills = null,
                Host = document.Host with { EngineEventHistoryLimit = null, EngineEventHistoryMegabytes = null, EngineEventHistoryDays = null },
                Agents = document.Agents.Select(agent => agent with { PluginSettings = null }).ToArray()
            };
            var older = new ConfigurationBackups(empty, () => false);
            var beforeV1 = new SystemInstructions(empty).Get("system");
            var olderPreview = older.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(v1)), true);
            Check(olderPreview.UpdatedPromptRoles?.Count == 0 && olderPreview.Attention.Any(x => x.Section == "prompts"),
                "version 1 backup explicitly preserves prompts absent from its contract");
            older.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(olderPreview.PreviewId, olderPreview.ExpectedHostRevision, true)), true);
            Check(new SystemInstructions(empty).Get("system") == beforeV1 &&
                JsonSerializer.Serialize(empty.Get<InterfacePreferences>("settings", "interface"), CerebralJson.Options) ==
                JsonSerializer.Serialize(beforeLegacyInterface, CerebralJson.Options),
                "version 1 restore leaves prompt and portable interface preference content unchanged");
            Reject(() => older.Preview(CerebralJson.Element(new ConfigurationRestorePreviewRequest(v1 with { Prompts = document.Prompts })), true),
                "version 1 cannot silently smuggle version 2 prompt overrides");

            var fakeTime = new TestTime(); var limited = new ConfigurationBackups(source, () => false, clock: fakeTime);
            var first = limited.Preview(CerebralJson.Element(previewInput), true);
            for (var i = 1; i < 8; i++) limited.Preview(CerebralJson.Element(previewInput), true);
            Reject(() => limited.Preview(CerebralJson.Element(previewInput), true), "pending preview count is bounded");
            fakeTime.Advance(TimeSpan.FromMinutes(11));
            Reject(() => limited.Apply(CerebralJson.Element(new ConfigurationRestoreApplyRequest(first.PreviewId, first.ExpectedHostRevision, true)), true), "preview expires after ten minutes");
            Check(limited.Preview(CerebralJson.Element(previewInput), true).ExpiresAt > fakeTime.GetUtcNow(), "expired preview slots are released");
            Console.WriteLine($"Configuration backup: {checks} typed export, validation and SQLite atomic restore assertions passed. No model loading, service changes or installation.");
            return Task.CompletedTask;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(root).StartsWith("cerebral-config-backup-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact backup fixture directory.");
            try { Directory.Delete(root, recursive: true); }
            catch (Exception cleanupError) when (failure is not null) { throw new AggregateException("Backup fixture failed and cleanup also failed.", failure, cleanupError); }
        }
    }

    private static StateStore Seed(string directory, string secret, bool entities = true)
    {
        var store = new StateStore(directory);
        var host = new HostSettings
        {
            Revision = 7, AccessKey = secret, ModelDirectory = Path.Combine(directory, "models"),
            ProjectsDirectory = Path.Combine(directory, "projects"), SharedDirectory = Path.Combine(directory, "shared"),
            ToolsDirectory = Path.Combine(directory, "tools"),
            LlamaServerPath = Path.Combine(directory, "runtime", "llama-server.exe"), ExternalCommandsEnabled = false,
            Workers = [new() { Id = "remote", Name = "Remote", Url = "http://127.0.0.1:65534", AccessKey = secret }],
            McpServers = [new() { Id = "cortex", Executable = Path.Combine(Path.GetTempPath(), "cerebral-fixture-runtime", "local-mcp.exe"),
                Url = "http://127.0.0.1:6124", Arguments = [secret] }],
            Executables = [new() { Id = "dotnet", Path = Path.Combine(Path.GetTempPath(), "cerebral-fixture-runtime", "local-dotnet.exe"), PrefixArguments = [secret] }],
            DefaultModelId = entities ? "model" : ""
        };
        store.Put("settings", "host", host);
        if (entities)
        {
            store.Put("model", "model", new ModelRecord { Id = "model", Name = "Model", Path = Path.Combine(host.ModelDirectory, "model.gguf"), SourceUrl = "https://example.invalid/model.gguf" });
            store.Put("agent", "agent", new AgentProfile
            {
                Id = "agent", Revision = 4, Name = "Agent", ModelId = "model", Instructions = "Owner-reviewed instructions",
                AllowedRoots = [host.ProjectsDirectory], AllowedTools = ["file.read"], Capabilities = ["diagnostics", "filesystem.read"],
                PluginSettings = new(StringComparer.Ordinal)
                {
                    ["example.plugin"] = CerebralJson.Element(new
                    {
                        portable = secret.StartsWith("source", StringComparison.Ordinal) ? "source-portable" : "destination-portable",
                        @private = secret
                    })
                }
            });
        }
        return store;
    }

    private static string Saved(StateStore store) => JsonSerializer.Serialize(new
    {
        host = store.Get<HostSettings>("settings", "host"),
        agents = store.List<AgentProfile>("agent").OrderBy(x => x.Id, StringComparer.Ordinal),
        models = store.List<ModelRecord>("model").OrderBy(x => x.Id, StringComparer.Ordinal),
        prompts = new SystemInstructions(store).CaptureEffective()
        , interfacePreferences = store.Get<InterfacePreferences>("settings", "interface")
    }, CerebralJson.Options);

    private static void Sql(StateStore store, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(store.DirectoryPath, "cerebral.db"), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
