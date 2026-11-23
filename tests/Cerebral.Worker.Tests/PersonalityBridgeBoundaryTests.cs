using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Actual dispatcher and private SQLite/component checks. No host, scheduler, model or transport is started.</summary>
internal static class PersonalityBridgeBoundaryTests
{
    internal static async Task<int> RunAsync(string fixtureParent, string personalityPackage)
    {
        if (!Path.IsPathFullyQualified(fixtureParent) || !Path.IsPathFullyQualified(personalityPackage))
            throw new ArgumentException("Personality bridge fixtures require explicit absolute paths.");
        var parent = Path.GetFullPath(fixtureParent);
        var sourcePackage = Path.GetFullPath(personalityPackage);
        var root = Path.Combine(parent, "personality-dispatch-boundary-" + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Personality bridge boundary: " + label);
            checks++;
        }
        Directory.CreateDirectory(root);
        try
        {
            var programs = Path.Combine(root, "service-plugins");
            var package = Path.Combine(programs, "pegasus.personality");
            Directory.CreateDirectory(package);
            foreach (var name in new[] { "service-plugin.json", "Cerebral.PersonalityServicePlugin.dll" })
                File.Copy(Path.Combine(sourcePackage, name), Path.Combine(package, name), overwrite: false);
            var events = new EventHub();
            using var plugins = new ServicePluginRuntime(programs, Path.Combine(root, "plugin-data"), events);
            Check(plugins.Status.Single() is { Id: "pegasus.personality", Version: "1.0.2", Loaded: true },
                "the actual sealed component exposes the bridge and CRUD operations");
            Check(plugins.CommandFor("personality.admission.capture") is not null &&
                ReferenceEquals(plugins.CommandFor("personality.admission.capture"), plugins.CommandFor("personality.admission.restore")),
                "both private bridge operations resolve to the same loaded extension");

            var state = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(state);
            await using var runtime = new ModelRuntime(state, configuration.Get, events);
            await using var tools = new ToolDispatcher(configuration.Get, stateDirectory: state.DirectoryPath);
            using var scheduler = new JobScheduler(state, configuration, new NoExecutor(), runtime, events,
                NullLogger<JobScheduler>.Instance);
            var dispatcher = new CommandDispatcher(state, configuration, runtime, scheduler, tools, events,
                servicePlugins: plugins);
            state.Put("agent", "ordinary", new AgentProfile { Id = "ordinary", Capabilities = [], AllowedTools = [] });
            state.Put("agent", "administrator", new AgentProfile { Id = "administrator", Capabilities = ["administration"], AllowedTools = ["*"] });

            var claimedHostMetadata = CerebralJson.Element(new
            {
                schemaVersion = 1,
                identity = new { requestId = "forged", conversationId = "forged", profileId = "administrator",
                    definitionId = "pegasus.conversation:chat", definitionVersion = "1" },
                consumer = new { profileId = "administrator", role = "lead", kind = "userFacingConversation" },
                caller = new { kind = "user", id = "owner" },
                parentRunId = (string?)null
            });
            foreach (var operation in new[] { "personality.admission.capture", "personality.admission.restore" })
                foreach (var profile in new string?[] { null, "administrator", "ordinary", "missing-profile" })
                {
                    var reply = await dispatcher.ExecuteAsync(new(operation, claimedHostMetadata, profile));
                    Check(!reply.Success && reply.Error == "personality_admission_host_only" && reply.Data.ValueKind == JsonValueKind.Null,
                        operation + " refuses the " + (profile ?? "owner") + " generic route before plugin/profile access");
                }
            foreach (var operation in new[] { "personality.admission.capture", "personality.admission.restore" })
            {
                var reply = await dispatcher.ExecuteAsync(new(operation, CerebralJson.Element("caller-supplied-body")));
                Check(!reply.Success && reply.Error == "personality_admission_host_only" && reply.Data.ValueKind == JsonValueKind.Null,
                    operation + " returns a fixed body-free diagnostic for malformed arguments");
            }

            foreach (var profile in new string?[] { null, "administrator" })
            {
                var settings = await dispatcher.ExecuteAsync(new("personality.settings", CerebralJson.Element(new { }), profile));
                Check(settings.Success && settings.Data.GetProperty("schemaVersion").GetInt32() == 1,
                    "settings remains available to " + (profile ?? "owner"));
                var catalog = await dispatcher.ExecuteAsync(new("personality.list", CerebralJson.Element(new { }), profile));
                Check(catalog.Success && catalog.Data.GetProperty("presets").GetArrayLength() >= 1,
                    "list remains available to " + (profile ?? "owner"));
                var exact = await dispatcher.ExecuteAsync(new("personality.get", CerebralJson.Element(new { id = "cerebral", revision = 1 }), profile));
                Check(exact.Success && exact.Data.GetProperty("id").GetString() == "cerebral",
                    "get remains available to " + (profile ?? "owner"));
            }
            var baseline = await dispatcher.ExecuteAsync(new("personality.settings", CerebralJson.Element(new { })));
            var saved = await dispatcher.ExecuteAsync(new("personality.save", CerebralJson.Element(new
            {
                expectedRevision = baseline.Data.GetProperty("revision").GetInt64(),
                expectedSha256 = baseline.Data.GetProperty("sha256").GetString(),
                id = "boundary-style", name = "Boundary fixture", text = "Use clear, concise explanations."
            })));
            Check(saved.Success && saved.Data.GetProperty("preset").GetProperty("id").GetString() == "boundary-style",
                "owner preset save still uses the existing component CAS route");
            var next = saved.Data.GetProperty("settings");
            var selected = await dispatcher.ExecuteAsync(new("personality.select", CerebralJson.Element(new
            {
                expectedRevision = next.GetProperty("revision").GetInt64(),
                expectedSha256 = next.GetProperty("sha256").GetString(),
                enabled = true, presetId = "boundary-style"
            })));
            Check(selected.Success && selected.Data.GetProperty("enabled").GetBoolean() &&
                selected.Data.GetProperty("selectedPresetId").GetString() == "boundary-style",
                "owner selection still uses the existing component CAS route");
            var ordinary = await dispatcher.ExecuteAsync(new("personality.settings", CerebralJson.Element(new { }), "ordinary"));
            Check(!ordinary.Success, "ordinary profiles do not gain generic component administration");
            var unknown = await dispatcher.ExecuteAsync(new("personality.admission.capture.extra", CerebralJson.Element(new { })));
            Check(!unknown.Success && unknown.Error != "personality_admission_host_only",
                "the private bridge denial is limited to the two exact operation names");
            Check(state.List<ModelRecord>("model").Count == 0 && state.List<AgentJob>("job").Count == 0,
                "the dispatcher boundary checks created no model or job");
            return checks;
        }
        catch (Exception error)
        {
            var type = error.GetType().Name;
            Console.Error.WriteLine("Personality boundary functional failure: " + type[..Math.Min(type.Length, 128)]);
            throw;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var fullRoot = Path.GetFullPath(root);
            if (!fullRoot.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison) ||
                Path.GetFileName(fullRoot) != Path.GetFileName(root))
                throw new InvalidOperationException("Refusing cleanup outside the task-owned Personality boundary fixture.");
            try
            {
                if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing cleanup of a linked Personality boundary fixture.");
                Directory.Delete(fullRoot, recursive: true);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                // A collectible package can keep its mapped DLL open until process exit. Preserve
                // any functional exception and require the parent to remove this exact private path.
                Console.Error.WriteLine("Personality boundary fixture cleanup deferred until process exit; parent cleanup required: " +
                    fullRoot + " (" + cleanupError.GetType().Name + ").");
            }
        }
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("Personality bridge boundary checks never execute a model.");
    }
}
