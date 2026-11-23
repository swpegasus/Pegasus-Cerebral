using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Programming;

namespace Cerebral.Worker.Tests;

/// <summary>Opt-in real Source/GB10 acceptance. Creates one tiny task-owned project and retained
/// coordination chat; never starts a model or enables auditing.</summary>
internal static class SourceHarnessCoordinationAcceptance
{
    private const string RunId = "engine-harness-coordination-gb10-20261008";
    private const string Alpha = "00-engine-harness-alpha";
    private const string Programmer = "00-engine-harness-programmer";

    public static async Task RunAsync(CerebralClient client, EngineStatus engine, bool submit, CancellationToken token)
    {
        if (engine.Active?.Slot != "A" || engine.Paused || engine.CandidatePending || engine.Error is not null)
            throw new InvalidOperationException("This canary requires the known-good active Source engine.");
        if (submit)
        {
            var profiles = await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: token);
            var lead = profiles.Single(profile => profile.Id == "7980ce1190f648a8b832b7516a67adbc");
            if (lead.WorkerId != "local" || lead.ModelWorkerId != "worker-3f6ab53f3a5a" ||
                lead.ModelId != "source-qwen35-9b" || lead.Role != "lead" ||
                !lead.Capabilities.Contains("agents.delegate", StringComparer.Ordinal))
                throw new InvalidOperationException("The existing Source lead no longer has the verified GB10 coordination route.");
            var project = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pegasus", "Cerebral", "Source", "Projects", "EngineHarnessAcceptance-20261008"));
            if (Directory.Exists(project))
                throw new InvalidOperationException("The task-owned live project already exists; inspect the retained run instead of resubmitting.");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Combine(project, "EngineHarnessAcceptance.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>", token);
            await File.WriteAllTextAsync(Path.Combine(project, "Program.cs"), "Console.WriteLine(\"ENGINE_HARNESS_BUILD_OK\");\n", token);

            async Task Configure(string id, bool programming)
            {
                if (profiles.Any(profile => profile.Id == id))
                    throw new InvalidOperationException("The acceptance helper identity already exists; do not replace unrelated configuration.");
                var profile = JsonSerializer.Deserialize<AgentProfile>(JsonSerializer.Serialize(lead, CerebralJson.Options), CerebralJson.Options)!;
                profile.Id = id; profile.Revision = 0; profile.Name = programming ? "Engine harness Programming check" : "Engine harness arithmetic check";
                profile.Role = "helper"; profile.Purpose = programming ? "Read and build the existing tiny .NET acceptance project" : "Independent arithmetic evidence";
                profile.Instructions = programming
                    ? "Inspect the requested existing project and build Release. Do not modify source, scaffold plugins, request audit or delegate."
                    : "Return only the short requested arithmetic evidence. Do not call tools or delegate.";
                profile.AllowedTools = programming ? ["file.read", "process.run"] : [];
                profile.AllowedRoots = programming ? [project] : [];
                profile.Capabilities = programming ? ["filesystem.read", "filesystem.write", "process.execute"] : [];
                profile.RequiredModelCapabilities = []; profile.PluginSettings = [];
                profile.FollowsDefaultModel = false; profile.FollowsModelBudgets = false;
                profile.ResponseTokens = programming ? 2048 : 1024; profile.MaxSteps = 24; profile.MaxSeconds = 240;
                profile.AutomaticCompaction = false;
                if (programming)
                    profile.PluginSettings["cerebral.coordination"] = CerebralJson.Element(new
                    {
                        schemaVersion = 1, definitionId = ProgrammingAgentHarness.Id, definitionVersion = ProgrammingAgentHarness.Version,
                        input = new { rootPath = project, projectFile = "EngineHarnessAcceptance.csproj", configuration = "Release",
                            selectedTools = new[] { "file.read", "process.run" }, requiredChecks = new[] { "build" } }
                    });
                await client.GetAsync<AgentProfile>("agents.save", profile, cancellationToken: token);
            }
            await Configure(Alpha, false);
            await Configure(Programmer, true);
            var run = await client.GetAsync<WorkflowRun>("jobs.workflow.submit", new SubmitWorkflowRequest(RunId, RunId, lead.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Coordinate two independent helpers using their exact current revisions from availableHelpers. " +
                "Assign " + Alpha + " to compute 2 + 3 and return ALPHA_RESULT: 5. " +
                "Assign " + Programmer + " to inspect Program.cs and EngineHarnessAcceptance.csproj in " + project +
                " and build the existing project in Release with the registered dotnet tool. The Programming helper already has its workflow/input bound. " +
                "Do not create or modify source. After both children return, synthesize their actual outcomes, including whether the real build succeeded. " +
                "Directly answering without both child contexts does not satisfy this coordination check.",
                null, WorkflowContract.EmptyObject), cancellationToken: token);
            Console.WriteLine(JsonSerializer.Serialize(new { run.Id, run.Status, run.Revision, lead.ModelWorkerId,
                helpers = new[] { Alpha, Programmer }, project }, CerebralJson.Options));
            return;
        }

        var parent = await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = RunId }, cancellationToken: token);
        var page = await client.GetAsync<JsonElement>("jobs.workflow.events", new { id = RunId, limit = 200 }, cancellationToken: token);
        var childIds = new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonElement value, int depth = 0)
        {
            if (depth > 32) throw new InvalidOperationException("Unexpected retained event depth.");
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name == "runIds" && property.Value.ValueKind == JsonValueKind.Array)
                        foreach (var item in property.Value.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } id && id.StartsWith("child-", StringComparison.Ordinal))
                                childIds.Add(id);
                    Collect(property.Value, depth + 1);
                }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Collect(item, depth + 1);
        }
        Collect(page);
        if (childIds.Count > 8) throw new InvalidOperationException("Unexpected child wave bound.");
        var children = new List<WorkflowRun>();
        foreach (var id in childIds)
            children.Add(await client.GetAsync<WorkflowRun>("jobs.workflow.get", new { id }, cancellationToken: token));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            parent.Id, parent.Status, parent.Revision, parent.WaitingReason, parent.Error, parent.ActiveMilliseconds, parent.Result,
            children = children.Select(child => new { child.Id, child.Status, child.Error, child.ActiveMilliseconds,
                child.Submission.ProfileId, child.Submission.DefinitionId, child.Submission.DefinitionVersion,
                child.Submission.ProjectId, child.Result })
        }, CerebralJson.Options));
        if (parent.IsTerminal)
        {
            if (parent.Status != WorkflowStatus.Completed || children.Count != 2 ||
                children.Any(child => child.Status != WorkflowStatus.Completed || child.Submission.ParentRunId != parent.Id) ||
                !children.Any(child => child.Submission.ProfileId == Programmer &&
                    child.Submission.DefinitionId == ProgrammingAgentHarness.Id &&
                    child.Submission.DefinitionVersion == ProgrammingAgentHarness.Version))
                throw new InvalidOperationException("The mixed live helper wave did not complete with both exact workflow kinds.");
            var programmed = children.Single(child => child.Submission.ProfileId == Programmer);
            if (programmed.Result is not { } result || !result.TryGetProperty("checks", out var checks) ||
                !checks.EnumerateArray().Any(check => check.GetProperty("kind").GetString() == "build"))
                throw new InvalidOperationException("The Programming child has no retained real build receipt.");
            var buildId = checks.EnumerateArray().Single(check => check.GetProperty("kind").GetString() == "build")
                .GetProperty("receiptId").GetString()!;
            var pageReceipt = await client.GetAsync<WorkflowReceiptPage>("jobs.workflow.receipt",
                new WorkflowReceiptRequest(programmed.Id, buildId, Limit: 16384), cancellationToken: token);
            if (pageReceipt.State != WorkflowEffectState.Succeeded || pageReceipt.Truncated)
                throw new InvalidOperationException("The actual Programming build receipt is not confirmed and complete.");
            using var retainedReceipt = JsonDocument.Parse(pageReceipt.Json);
            var evidence = retainedReceipt.RootElement.GetProperty("data").GetProperty("evidence");
            if (evidence.GetProperty("exitCode").GetInt32() != 0)
                throw new InvalidOperationException("The actual retained compiler process did not exit successfully.");
            Console.WriteLine(JsonSerializer.Serialize(new { actualBuild = new { receiptId = buildId,
                pageReceipt.ProviderReceiptId, providerId = evidence.GetProperty("providerId").GetString(), exitCode = 0 } }, CerebralJson.Options));
        }
    }
}
