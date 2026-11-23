using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker;

/// <summary>Known validation failure before any child admission or stage dispatch.</summary>
internal sealed class WorkflowDataAdmissionRejectedException(string message) : ArgumentException(message);

/// <summary>Data publication never changes grants; accepted runs inherit their source assignment.</summary>
public sealed partial class WorkerWorkflows
{
    public WorkflowDataCatalog DataCatalog { get; private set; } = null!;

    public sealed record WorkflowDataRegisterRequest(string SourceRequestId, WorkflowDataEdition Edition);
    public sealed record WorkflowDataListRequest(string SourceRequestId, int Limit = 40, string? After = null);
    public sealed record WorkflowDataGetRequest(string SourceRequestId, string Id, string Version, string Hash);
    private sealed record DataScope(WorkflowRun Source, WorkerExecutionSnapshot.Snapshot Snapshot,
        AgentProfile Profile, string ProjectId);

    private DataScope ResolveDataScope(string sourceRequestId, AgentProfile? actor)
    {
        Configuration.ValidateId(sourceRequestId);
        var source = GetOwned(sourceRequestId, actor);
        if (source.Submission.DefinitionId == WorkflowDataModule.DefinitionId)
            throw new UnauthorizedAccessException("A data workflow cannot publish or recursively invoke another workflow.");
        if (source.Submission.ProjectId is not { } projectId)
            throw new InvalidOperationException("Reusable workflows require an existing project-owned source assignment.");
        new ProjectKnowledge(legacy, configuration, events).Project(projectId);
        var snapshot = WorkerExecutionSnapshot.Read(source.Submission.SettingsSnapshot);
        var effective = WorkerExecutionSnapshot.EffectiveProfile(legacy, snapshot, source.Submission.ProfileId);
        if (snapshot.RoutingFingerprint != WorkerExecutionSnapshot.RoutingFingerprint(configuration.Get()))
            throw new WorkflowConflictException("The source tool routing changed. Accept a fresh assignment before publishing or running a workflow.");
        return new(source, snapshot, effective, projectId);
    }

    internal WorkflowRun ValidateDataContext(ToolExecutionContext context, string toolName)
    {
        if (!context.EnforceProfileScope || string.IsNullOrWhiteSpace(context.EffectId))
            throw new UnauthorizedAccessException("Workflow tools require a host-owned durable tool effect.");
        var scope = ResolveDataScope(context.JobId, context.Profile);
        var source = scope.Source;
        if (source.Submission.ConversationId != context.SessionId || source.IsTerminal || source.CancelRequested ||
            source.Effect is not { } effect || effect.Id != context.EffectId ||
            effect.CommandId != WorkerWorkflowEffects.ToolCommand || effect.CommandVersion != 1 ||
            effect.State is not (WorkflowEffectState.Dispatched or WorkflowEffectState.Unknown))
            throw new UnauthorizedAccessException("The calling effect does not own this source assignment.");
        var arguments = Read<WorkerWorkflowEffects.ToolArguments>(effect.Arguments);
        if (arguments.Name != toolName)
            throw new UnauthorizedAccessException("The saved effect names a different workflow tool.");
        return source;
    }

    private static IReadOnlyList<ToolDescriptor> DataTools(DataScope scope) =>
        scope.Snapshot.Tools.Where(tool => WorkerExecutionSnapshot.AllowsTool(scope.Profile, tool.Name) &&
            scope.Profile.Capabilities.Contains(tool.Capability, StringComparer.Ordinal) &&
            (SupportedDataTools.Contains(tool.Name) || tool.Name.StartsWith("mcp.", StringComparison.Ordinal) && tool.Name.EndsWith(".pao", StringComparison.Ordinal))).ToArray();

    private static readonly HashSet<string> SupportedDataTools = new(StringComparer.Ordinal)
    {
        "math.calculate", "file.read", "file.inspect", "file.list", "file.search", "file.copy", "file.write",
        "file.append", "file.edit", "file.mkdir", "process.run", "git.status", "git.diff", "git.run",
        "http.fetch", "plugin.scaffold", "plugin.verify"
    };
    private static readonly HashSet<string> SupportedPaoOperations = new(StringComparer.Ordinal)
    {
        "stat", "ls", "find", "search", "read_lines", "sha", "wr", "replace", "mkdir", "cp",
        "dotnet_build", "dotnet_test", "dotnet_restore", "git_status", "git_diff", "run", "job_stat"
    };
    private static void ValidateDataStages(WorkflowDataEdition edition)
    {
        foreach (var stage in edition.Stages.Where(stage => stage.Tool.StartsWith("mcp.", StringComparison.Ordinal)))
        {
            if (!stage.Tool.EndsWith(".pao", StringComparison.Ordinal) ||
                !stage.Arguments.TryGetProperty("op", out var operation) || operation.ValueKind != JsonValueKind.String ||
                !SupportedPaoOperations.Contains(operation.GetString()!) ||
                stage.Arguments.TryGetProperty("root", out _) || stage.Arguments.TryGetProperty("sandbox_permissions", out _))
                throw new ArgumentException("A data workflow PAO stage requires one supported literal user operation; privileged, recipe and lifecycle operations are excluded.");
        }
    }

    public WorkflowDataRegistration RegisterWorkflowData(WorkflowDataRegisterRequest request, AgentProfile? actor) =>
        legacy.Locked(() =>
        {
            var scope = ResolveDataScope(request.SourceRequestId, actor);
            WorkflowDataCatalog.Validate(request.Edition);
            ValidateDataStages(request.Edition);
            WorkflowDataCatalog.ValidateToolNames(request.Edition, DataTools(scope));
            if (!request.Edition.RequiredCapabilities.All(capability => scope.Profile.Capabilities.Contains(capability, StringComparer.Ordinal)))
                throw new UnauthorizedAccessException("The source assignment does not grant every declared workflow capability.");
            return DataCatalog.Register(scope.ProjectId, request.Edition);
        });

    public WorkflowDataPage ListWorkflowData(WorkflowDataListRequest request, AgentProfile? actor)
    {
        var scope = ResolveDataScope(request.SourceRequestId, actor);
        return DataCatalog.List(scope.ProjectId, request.Limit, request.After);
    }

    public WorkflowDataEntry GetWorkflowData(WorkflowDataGetRequest request, AgentProfile? actor)
    {
        var scope = ResolveDataScope(request.SourceRequestId, actor);
        return DataCatalog.Get(scope.ProjectId, request.Id, request.Version, request.Hash);
    }

    private string DataProjectId(SubmitWorkflowRequest input)
    {
        var selected = Read<WorkflowDataRunInput>(input.Input);
        var scope = ResolveDataScope(selected.SourceRequestId,
            legacy.Get<AgentProfile>("agent", input.ProfileId) ?? throw new ArgumentException("Agent profile not found."));
        if (input.ConversationId != WorkflowDataConversationId(input.RequestId))
            throw new ArgumentException("Data workflows use the exact derived execution conversation, separate from Chat.");
        return scope.ProjectId;
    }

    private WorkflowRun SubmitDataWorkflow(SubmitWorkflowRequest input, AgentProfile? actor, IdleAdmission idleAdmission)
    {
        input = FreezeInput(input);
        ValidateCaller(input, actor);
        var selected = Read<WorkflowDataRunInput>(input.Input);
        // Retry uses the already frozen edition and caller identity even after current configuration changes.
        if (Store.TryGet(input.RequestId) is { } accepted)
            return SubmitCore(input, actor, accepted.Submission.SettingsSnapshot, null, null, null, idleAdmission: idleAdmission);
        JsonElement frozen;
        string expectedProfile;
        try
        {
            var scope = ResolveDataScope(selected.SourceRequestId,
                actor ?? legacy.Get<AgentProfile>("agent", input.ProfileId) ?? throw new ArgumentException("Agent profile not found."));
            if (scope.Source.Submission.ProfileId != input.ProfileId)
                throw new UnauthorizedAccessException("Data workflows inherit the source profile.");
            var entry = DataCatalog.Get(scope.ProjectId, selected.Id, selected.Version, selected.Hash);
            ValidateDataStages(entry.Edition);
            WorkflowDataCatalog.ValidateToolNames(entry.Edition, DataTools(scope));
            WorkflowSchema.Validate(selected.Arguments, entry.Edition.InputSchema);
            if (!entry.Edition.RequiredCapabilities.All(capability => scope.Profile.Capabilities.Contains(capability, StringComparer.Ordinal)))
                throw new UnauthorizedAccessException("The source assignment lacks a required workflow capability.");
            if (input.ConversationId != WorkflowDataConversationId(input.RequestId))
                throw new ArgumentException("Data workflows require their derived execution conversation.");
            var dataConsumer = WorkerToolContextProjectionAdmission.FreezeConsumer(
                Registry.GetDefinition(input.DefinitionId, input.DefinitionVersion).Descriptor);
            if (!dataConsumer.Supported && scope.Snapshot.ContextContributions.Any(item =>
                    WorkerToolContextProjectionAdmission.IsReceipt(item.Descriptor) &&
                    item.Selection.Disposition != WorkflowContextDisposition.Omitted))
                throw new InvalidDataException("A data workflow cannot inherit unsupported receipt context.");
            // The child keeps only its selected provider/tool roots, but they must reconstruct
            // the source's entire accepted graph. A disappeared or remapped tool owner cannot
            // silently fall out of a later lookup and produce a narrower child graph.
            RequireSelectedToolOwnersCurrent(scope.Snapshot);
            var selectedToolOwners = SelectedWorkflowToolOwners(scope.Snapshot);
            if ((scope.Snapshot.ApprovedPluginSettings ?? []).Any(item =>
                    !selectedToolOwners.Contains(item.PluginId, StringComparer.Ordinal)))
                throw new InvalidDataException("A derived workflow lost an accepted selected tool owner.");
            var roots = WorkflowPortablePackageRoots(scope.Snapshot)
                .Concat(scope.Snapshot.ApprovedPluginSettings?.Select(item => item.PluginId) ?? [])
                .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (packages.Check(scope.Source.Submission, scope.Source.Submission.DefinitionId) is not null ||
                !JsonElement.DeepEquals(CerebralJson.Element(scope.Snapshot.PackageBindings),
                    CerebralJson.Element(packages.Capture(scope.Source.Submission.DefinitionId, roots))))
                throw new InvalidDataException("A derived workflow selected tool or context owner changed.");
            var dataBindings = packages.Capture(WorkflowDataModule.DefinitionId, roots);
            foreach (var root in roots)
            {
                var acceptedBinding = scope.Snapshot.PackageBindings.Single(binding => binding.Id == root);
                var currentBinding = dataBindings.Single(binding => binding.Id == root);
                if (!JsonElement.DeepEquals(CerebralJson.Element(acceptedBinding), CerebralJson.Element(currentBinding)))
                    throw new InvalidDataException("A derived workflow owner no longer matches its accepted source.");
            }
            var settings = JsonNode.Parse(scope.Source.Submission.SettingsSnapshot.GetRawText())!.AsObject();
            settings["packageBindings"] = JsonSerializer.SerializeToNode(dataBindings, CerebralJson.Options);
            settings["workflowData"] = JsonSerializer.SerializeToNode(entry, CerebralJson.Options);
            if (scope.Snapshot.SourceSettingsVersion >= 6)
                settings["toolReceiptConsumer"] = JsonSerializer.SerializeToNode(dataConsumer, CerebralJson.Options);
            frozen = JsonSerializer.SerializeToElement(settings, CerebralJson.Options);
            if (JsonSerializer.SerializeToUtf8Bytes(frozen).Length > WorkflowContract.MaximumStateBytes)
                throw new InvalidOperationException("The source snapshot and complete workflow edition exceed the durable limit.");
            expectedProfile = JsonSerializer.Serialize(legacy.Get<AgentProfile>("agent", input.ProfileId), CerebralJson.Options);
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or
            InvalidOperationException or InvalidDataException or KeyNotFoundException or JsonException)
        {
            // This block only resolves/validates the frozen input. Durable admission starts below.
            // Do not widen this catch across SubmitCore or runtime execution: their outcome may be unknown.
            throw new WorkflowDataAdmissionRejectedException(error.Message);
        }
        return SubmitCore(input, actor, frozen, expectedProfile, null, null, idleAdmission: idleAdmission);
    }

    private WorkflowSubmission BindDataWorkflowLimits(WorkflowSubmission submission)
    {
        if (submission.DefinitionId != WorkflowDataModule.DefinitionId) return submission;
        var source = Store.Get(Read<WorkflowDataRunInput>(submission.Input).SourceRequestId);
        var snapshot = WorkerExecutionSnapshot.Read(source.Submission.SettingsSnapshot);
        long enclosingReservation = 0;
        if (source.Effect is { State: WorkflowEffectState.Dispatched, CommandId: WorkerWorkflowEffects.ToolCommand, CommandVersion: 1 } active &&
            submission.RequestId == WorkflowDataToolExtension.RunId(source.Id, active.Id))
        {
            var call = Read<WorkerWorkflowEffects.ToolArguments>(active.Arguments);
            if (call.Name == WorkflowDataToolExtension.RunTool &&
                WorkflowDataToolExtension.MatchesRunArguments(call.Arguments, Read<WorkflowDataRunInput>(submission.Input)))
                enclosingReservation = active.ReservedExecutionMilliseconds;
        }
        // Only the exact enclosing command is precharged for this still-running child. Completed
        // and uncertain commands already reconciled their reservation into measured active time.
        var remainingMilliseconds = checked(source.Submission.ActiveTimeLimitSeconds * 1000L) -
            source.ActiveMilliseconds + enclosingReservation;
        if (remainingMilliseconds < 1000)
            throw new InvalidOperationException("The source assignment has no remaining execution time for a data workflow.");
        var limits = source.Submission.FailureLimits.ToDictionary(pair => pair.Key,
            pair => pair.Value - source.FailedEffects.GetValueOrDefault(pair.Key), StringComparer.Ordinal);
        if (limits.Values.Any(limit => limit <= 0))
            throw new InvalidOperationException("The source assignment exhausted its failure allowance. No data workflow was accepted.");
        return submission with
        {
            ActiveTimeLimitSeconds = Math.Min(submission.ActiveTimeLimitSeconds,
                (int)Math.Min(remainingMilliseconds / 1000, Math.Min(300, snapshot.Profile.MaxSeconds))),
            PolicyStepLimit = Math.Min(submission.PolicyStepLimit, snapshot.Profile.MaxSteps),
            FailureLimits = limits,
            AuditPolicy = source.Submission.AuditPolicy
        };
    }

    public static string WorkflowDataConversationId(string requestId)
    {
        Configuration.ValidateId(requestId);
        return "workflow-data-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId))).ToLowerInvariant()[..32];
    }

    internal async Task<WorkflowRun> ExecuteWorkflowDataAsync(ToolExecutionContext context, WorkflowDataRunInput selected,
        CancellationToken cancellationToken)
    {
        ValidateDataContext(context, WorkflowDataToolExtension.RunTool);
        if (selected.SourceRequestId != context.JobId)
            throw new UnauthorizedAccessException("The selected workflow has a different source assignment.");
        var requestId = WorkflowDataToolExtension.RunId(context);
        var request = new SubmitWorkflowRequest(requestId, WorkflowDataConversationId(requestId), context.Profile.Id,
            WorkflowDataModule.DefinitionId, WorkflowDataModule.DefinitionVersion, "Run reusable workflow " + selected.Id,
            null, CerebralJson.Element(selected), 32);
        using var idleAdmission = BeginIdleAdmission(request.ConversationId);
        var run = SubmitDataWorkflow(request, context.Profile, idleAdmission);
        // The same persisted claim also protects against a scheduler winning the race. In that case,
        // only inspect its result; never create a second run or repeat an uncertain effect.
        run = await Runtime.RunAsync(run.Id, cancellationToken).ConfigureAwait(false);
        while (run.Status == WorkflowStatus.Running && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            run = Store.Get(run.Id);
        }
        return run;
    }

    internal static IReadOnlyList<OperationDescriptor> DataOperationDescriptors { get; } =
    [
        new("workflows.catalog.register", 1, "Publish one immutable project-scoped workflow edition. Identical publication is idempotent; changed content at the same ID/version is rejected. No command runs or new grants are created.",
            DataOperationSchema("\"sourceRequestId\":{\"type\":\"string\"},\"edition\":{\"type\":\"object\"}", "\"sourceRequestId\",\"edition\"")),
        new("workflows.catalog.list", 1, "Page concise reusable workflow editions in the owned source assignment's project. Exact version/hash selection; no latest-version guess.",
            DataOperationSchema("\"sourceRequestId\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":40},\"after\":{\"type\":[\"string\",\"null\"]}", "\"sourceRequestId\"")),
        new("workflows.catalog.get", 1, "Read the complete exact project workflow edition by ID, version and content hash.",
            DataOperationSchema("\"sourceRequestId\":{\"type\":\"string\"},\"id\":{\"type\":\"string\"},\"version\":{\"type\":\"string\"},\"hash\":{\"type\":\"string\"}", "\"sourceRequestId\",\"id\",\"version\",\"hash\""))
    ];

    private static JsonElement DataOperationSchema(string fields, string required)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{" + fields +
            "},\"required\":[" + required + "],\"additionalProperties\":false}");
        return document.RootElement.Clone();
    }
}
