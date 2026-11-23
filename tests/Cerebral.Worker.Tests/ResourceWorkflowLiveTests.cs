using System.Text;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Real local image/file inference through Chat's durable harness; no fake model and no desktop claim.</summary>
public static class ResourceWorkflowLiveTests
{
    public static async Task RunAsync(string weights, string server, string projector)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-resource-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = false;
        try
        {
            var state = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(state);
            var settings = configuration.Get();
            settings.LlamaServerPath = Path.GetFullPath(server);
            settings.DefaultModelId = "resource-model";
            settings.ReservedMemoryMb = 1024;
            settings.McpServers = [];
            settings.CortexNamespaces = [];
            settings.CortexWriteNamespaces = [];
            settings.ModelDirectory = Path.Combine(root, "models");
            settings.ProjectsDirectory = Path.Combine(root, "projects");
            settings.SharedDirectory = Path.Combine(root, "shared");
            configuration.Save(settings);
            var profile = new AgentProfile
            {
                Id = "resource-lead", Name = "Resource workflow acceptance", Role = "lead", ModelId = "resource-model",
                WorkerId = "local", ModelWorkerId = "local", FollowsModelBudgets = false,
                ContextTokens = 8192, ResponseTokens = 1024, ReasoningMode = "off", Temperature = 0,
                MaxSteps = 20, MaxSeconds = 240, AllowedTools = [], Capabilities = [],
                Instructions = "Use the provided native image and file reference content. Answer directly with the requested evidence. No tools are needed."
            };
            Configuration.ValidateProfile(profile); state.Put("agent", profile.Id, profile);
            state.Put("model", profile.ModelId, new ModelRecord
            {
                Id = profile.ModelId, Name = "Native resources", Path = Path.GetFullPath(weights),
                VisionProjectorPath = Path.GetFullPath(projector), SizeBytes = new FileInfo(weights).Length,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192,
                AutomaticContextSizing = false, ParallelRequests = 1, IdleMinutes = 0
            });
            var journal = new EventJournal(Path.Combine(state.DirectoryPath, "engine.db"));
            var resources = new AttachmentStore(journal);
            var conversation = "resource-conversation";
            var scope = new AttachmentAccessContext(profile.Id, conversation);
            var marker = "orchid-" + Guid.NewGuid().ToString("N")[..12];
            AttachmentReference image, file;
            using (var stream = new MemoryStream(ResourceModelTests.Image()))
                image = await resources.ImportAsync(scope, "image-input", "colors.png", stream);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("Acceptance marker: " + marker)))
                file = await resources.ImportAsync(scope, "file-input", "reference.txt", stream);

            var events = new EventHub();
            await using var dispatcher = new ToolDispatcher(configuration.Get);
            await using var runtime = new ModelRuntime(state, configuration.Get, events);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var scheduler = new JobScheduler(state, configuration, new NoLegacyExecutor(), runtime, events,
                NullLogger<JobScheduler>.Instance);
            var commands = new CommandDispatcher(state, configuration, runtime, scheduler, dispatcher, events);
            var verification = await commands.ExecuteAsync(new("models.verify-image", CerebralJson.Element(new { id = profile.ModelId })), deadline.Token);
            Require(verification.Success, "Owner image verification did not succeed: " + verification.Error);
            var initialProof = CerebralJson.Read<ModelImageVerification>(verification.Data);
            Require(initialProof.AdapterId == "llama.cpp.native.vision" && initialProof.InputTokens > 0,
                "Image verification did not retain native measured evidence.");
            profile = state.Get<AgentProfile>("agent", profile.Id)!;
            profile.RequiredModelCapabilities = [ModelCapabilityIds.ImageInput];
            var saved = await commands.ExecuteAsync(new("agents.save", CerebralJson.Element(profile)), deadline.Token);
            Require(saved.Success, "Verified image-required profile could not be saved: " + saved.Error);
            profile = CerebralJson.Read<AgentProfile>(saved.Data);
            await runtime.UnloadAsync(profile.ModelId, deadline.Token);
            Require(!runtime.IsLoaded(profile.ModelId) && runtime.CurrentImageVerification(state.Get<ModelRecord>("model", profile.ModelId)!) is null,
                "Unload retained live image readiness.");
            Require(state.Get<AgentProfile>("agent", profile.Id)!.RequiredModelCapabilities.Contains(ModelCapabilityIds.ImageInput),
                "Unload removed the saved image requirement.");
            Console.WriteLine("Actual native verification and owner image-required agent save passed; unload retired proof and preserved configuration.");
            WorkerWorkflows Open()
            {
                var reopenedJournal = new EventJournal(Path.Combine(state.DirectoryPath, "engine.db"));
                var reopened = new AttachmentStore(reopenedJournal);
                var host = new WorkerWorkflowEffects(state, configuration, runtime, dispatcher,
                    submission => new LlamaResourceModelAdapter(runtime, reopened, new(submission.ProfileId, submission.ConversationId)));
                return new(state, configuration, events, [new ConversationWorkflowModule(), host],
                    [ConversationWorkflowModule.PackageBinding, new(host.Id, WorkerWorkflowEffects.ModuleVersion, true, [])],
                    dispatcher, submission => LlamaResourceModelAdapter.ValidateAcceptedContent(reopened, submission), reopenedJournal);
            }
            var workflows = Open();
            var request = new SubmitWorkflowRequest("resource-first", conversation, profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Inspect the attached image and reference file. Reply exactly with left=<color>;right=<color>;marker=<the acceptance marker from the file>. Use common color names. Do not ask questions.",
                null, CerebralJson.Element(new { responseSharePercent = 20 }), StepLimit: 20)
                { AcceptedContent = [new ImageContentPart(image), new FileContentPart(file)] };
            var accepted = await workflows.SubmitAsync(request, null, deadline.Token);
            var first = await workflows.Runtime.RunAsync(accepted.Id, deadline.Token);
            Require(first.Status == WorkflowStatus.Completed, "First image/file Chat request did not complete: " + first.Error);
            var expected = "left=red;right=blue;marker=" + marker;
            Require(Summary(first).Trim().Equals(expected, StringComparison.OrdinalIgnoreCase),
                "First image/file answer did not match actual inputs: " + Summary(first));
            var firstGenerations = Generations(workflows.Journal, conversation);
            Require(firstGenerations.Length != 0 && firstGenerations.All(x => x.Prepared.AdapterId == "llama.cpp.native.vision" &&
                x.Prepared.InputTokens == x.Generation.EvaluatedTokens), "The complete harness did not preserve measured native image input.");
            var reloadedProof = runtime.CurrentImageVerification(state.Get<ModelRecord>("model", profile.ModelId)!);
            Require(reloadedProof is not null && reloadedProof.InstanceId != initialProof.InstanceId,
                "The image-required workflow did not acquire fresh verification after automatic reload.");
            Console.WriteLine("Actual Chat image/file response matched colors and unique file marker; native input matched evaluation after verified automatic reload.");

            workflows = Open();
            var same = await workflows.SubmitAsync(request, null, deadline.Token);
            Require(same.Id == first.Id && same.Status == WorkflowStatus.Completed && same.Revision == first.Revision,
                "Reopened request did not recover the exact completed receipt.");
            var secondRequest = new SubmitWorkflowRequest("resource-second", conversation, profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Look again at the earlier image. Are the two colored rectangles the same size? Reply exactly: same size, or different sizes. No tools or questions.",
                null, CerebralJson.Element(new { responseSharePercent = 20 }), StepLimit: 20);
            var second = await workflows.SubmitAsync(secondRequest, null, deadline.Token);
            second = await workflows.Runtime.RunAsync(second.Id, deadline.Token);
            Require(second.Status == WorkflowStatus.Completed && Summary(second).Trim().Equals("same size", StringComparison.OrdinalIgnoreCase),
                "Reopened historical image was not used successfully: " + second.Error + " " + Summary(second));
            var allGenerations = Generations(workflows.Journal, conversation);
            Require(allGenerations.Length > firstGenerations.Length &&
                allGenerations.Skip(firstGenerations.Length).All(x => x.Prepared.AdapterId == "llama.cpp.native.vision" &&
                    x.Prepared.InputTokens == x.Generation.EvaluatedTokens), "Historical image reference did not reach native vision.");
            Require(runtime.CurrentImageVerification(state.Get<ModelRecord>("model", profile.ModelId)!) == reloadedProof,
                "The unchanged live instance repeated or lost its image verification.");
            Require(!Directory.EnumerateFileSystemEntries(settings.ProjectsDirectory).Any(), "A chat with attachments created an unused project folder.");
            await runtime.UnloadAsync(profile.ModelId, deadline.Token);
            Require(!runtime.IsLoaded(profile.ModelId), "Owned model did not unload.");
            Console.WriteLine("Resource workflow live passed: actual native verification, image-required agent save, unload and automatic re-verification, file/image Chat, exact retry after SQLite reopen, historical native image input, lazy project path and final unload. No desktop or installed-service claim.");
            passed = true;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (passed) Directory.Delete(root, true);
            else Console.WriteLine("RETAINED FOR DIAGNOSIS: " + root);
        }
    }

    private sealed class NoLegacyExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("This acceptance uses the real durable Chat harness, never the legacy executor.");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static string Summary(WorkflowRun run) =>
        run.Result is { } result && result.TryGetProperty("summary", out var text) ? text.GetString() ?? "" : "";
    private static WorkerWorkflowEffects.GenerationData[] Generations(EventJournal journal, string conversation)
    {
        var events = new List<CerebralEvent>(); long after = 0; long? snapshot = null;
        while (true)
        {
            var page = journal.ReadPage(conversation, after, 100, snapshot);
            snapshot ??= page.SnapshotSequence;
            events.AddRange(page.Events);
            Require(events.Count <= 512, "Resource acceptance exceeded its bounded evidence inspection.");
            if (page.NextAfterSequence is not { } next) break;
            Require(next > after, "Resource evidence page did not advance.");
            after = next;
        }
        return events.Where(x => x.Type == CerebralEventTypes.ToolResult)
            .SelectMany(x => x.Content.OfType<StructuredContentPart>())
            .Where(x => x.Schema == "workflow.command.result")
            .Select(x => CerebralJson.Read<WorkflowCommandResult>(x.Value))
            .Where(x => x.Data is { } data && data.TryGetProperty("generation", out _))
            .Select(x => CerebralJson.Read<WorkerWorkflowEffects.GenerationData>(x.Data!.Value)).ToArray();
    }
}
