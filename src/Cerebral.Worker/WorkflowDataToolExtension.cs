using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;

namespace Cerebral.Worker;

/// <summary>Workflow data uses the existing typed tool and receipt-recovery seams.</summary>
public sealed class WorkflowDataToolExtension(Func<WorkerWorkflows> host) : IToolExtension, IToolRecoveryExtension
{
    public const string GuideTool = "workflow.catalog.guide";
    public const string RegisterTool = "workflow.catalog.register";
    public const string ListTool = "workflow.catalog.list";
    public const string GetTool = "workflow.catalog.get";
    public const string RunTool = "workflow.catalog.run";
    public const string StatusTool = "workflow.catalog.status";
    private const string Provider = "cerebral.workflow-data";
    private sealed record RunRequest(string Id, string Version, string Hash, JsonElement Input, int TimeoutSeconds = 30);
    private sealed record GetRequest(string Id, string Version, string Hash);
    private sealed record ListRequest(int Limit = 40, string? After = null);
    private sealed record StatusRequest(string RunId, bool IncludeOutput = false);

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        Tool(GuideTool, "Read the versioned compact workflow-data authoring contract and example before publishing a reusable workflow.", "{}", "[]"),
        Tool(RegisterTool, "Publish an immutable reusable workflow in this assignment's project. Read the exact workflow-data guide first. Identical content at the same ID/version is idempotent; changed content is rejected. Publication does not execute commands.",
            """{"edition":{"type":"object"}}""", """["edition"]"""),
        Tool(ListTool, "List concise reusable workflow IDs, exact versions, hashes and descriptions in this project. Read one selected edition with workflow.catalog.get.",
            """{"limit":{"type":"integer","minimum":1,"maximum":40},"after":{"type":["string","null"]}}""", "[]"),
        Tool(GetTool, "Read a selected reusable workflow's complete input/output schemas and bounded stages. Use its exact ID/version/hash when running.",
            """{"id":{"type":"string"},"version":{"type":"string"},"hash":{"type":"string","minLength":64,"maxLength":64}}""", """["id","version","hash"]"""),
        Tool(RunTool, "First list the project catalog and get the selected exact edition. Copy its ID/version/hash; the edition hash is not an input-file hash. Execute it with typed input inside this assignment's accepted permissions. Commands run in order without model decisions between them. Return a compact terminal receipt or exact uncertainty receipt; never repeat an uncertain run.",
            """{"id":{"type":"string"},"version":{"type":"string"},"hash":{"type":"string","minLength":64,"maxLength":64},"input":{"type":"object"},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":300,"default":30}}""", """["id","version","hash","input"]"""),
        Tool(StatusTool, "Inspect a data run from this source assignment without dispatching it again. Completed confirms success; other states remain distinct. includeOutput reads the final typed result only when needed.",
            """{"runId":{"type":"string"},"includeOutput":{"type":"boolean"}}""", """["runId"]""")
    ];

    public static string RunId(ToolExecutionContext context) => RunId(context.JobId, context.EffectId);

    internal static string RunId(string sourceId, string? effectId)
    {
        if (string.IsNullOrWhiteSpace(effectId)) throw new UnauthorizedAccessException("A durable effect identity is required.");
        return "workflow-data-run-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(sourceId + "\n" + effectId))).ToLowerInvariant()[..32];
    }

    internal static bool MatchesRunArguments(JsonElement arguments, WorkflowDataRunInput input)
    {
        var request = WorkerWorkflows.Read<RunRequest>(arguments);
        return request.Id == input.Id && request.Version == input.Version && request.Hash == input.Hash &&
            JsonNode.DeepEquals(JsonNode.Parse(request.Input.GetRawText()), JsonNode.Parse(input.Arguments.GetRawText()));
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var worker = host();
        var source = worker.ValidateDataContext(context, call.Name);
        var saved = WorkerWorkflows.Read<WorkerWorkflowEffects.ToolArguments>(source.Effect!.Arguments);
        if (!JsonNode.DeepEquals(JsonNode.Parse(saved.Arguments.GetRawText()), JsonNode.Parse(call.Arguments.GetRawText())))
            throw new UnauthorizedAccessException("The catalog arguments do not match the committed calling effect.");
        cancellationToken.ThrowIfCancellationRequested();
        switch (call.Name)
        {
            case GuideTool:
                if (call.Arguments.EnumerateObject().Any()) throw new ArgumentException("The workflow guide takes no arguments.");
                return ReadResult(context, new { schema = WorkflowDataCatalog.Schema, guide = Guide });
            case RegisterTool:
                if (call.Arguments.EnumerateObject().Count() != 1 || !call.Arguments.TryGetProperty("edition", out var value))
                    throw new ArgumentException("Publication accepts only edition.");
                var edition = WorkerWorkflows.Read<WorkflowDataEdition>(value);
                try
                {
                    var registered = worker.RegisterWorkflowData(new(context.JobId, edition), context.Profile);
                    return ReadResult(context, new { status = registered.Created ? "created" : "unchanged",
                        registered.Entry.Edition.Id, registered.Entry.Edition.Version, registered.Entry.Hash });
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or
                    Microsoft.Data.Sqlite.SqliteException)
                {
                    // Recovery inspects only the committed immutable coordinates; it never republishes.
                    return Unknown(context, "Publication was not confirmed; reconcile the exact edition before retrying.");
                }
            case ListTool:
                var list = WorkerWorkflows.Read<ListRequest>(call.Arguments);
                return ReadResult(context, worker.ListWorkflowData(new(context.JobId, list.Limit, list.After), context.Profile));
            case GetTool:
                var get = WorkerWorkflows.Read<GetRequest>(call.Arguments);
                try
                {
                    return ReadResult(context, worker.GetWorkflowData(new(context.JobId, get.Id, get.Version, get.Hash), context.Profile));
                }
                catch (Exception error) when (error is KeyNotFoundException or InvalidDataException or ArgumentException)
                {
                    return Rejected(context, "workflow_catalog_selection_rejected", error.Message);
                }
            case StatusTool:
                var status = WorkerWorkflows.Read<StatusRequest>(call.Arguments);
                var existing = Inspect(worker, context, status.RunId);
                return RunResult(context, existing, status.IncludeOutput);
            case RunTool:
                var request = WorkerWorkflows.Read<RunRequest>(call.Arguments);
                try
                {
                    var run = await worker.ExecuteWorkflowDataAsync(context,
                        new(context.JobId, request.Id, request.Version, request.Hash, request.Input), cancellationToken);
                    return RunResult(context, run, false);
                }
                catch (WorkflowDataAdmissionRejectedException error)
                {
                    return Rejected(context, "workflow_not_admitted", error.Message);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or
                    Microsoft.Data.Sqlite.SqliteException)
                {
                    return Unknown(context, "The wait was interrupted; inspect the exact data run. It may still be running.", RunId(context));
                }
            default: throw new ArgumentException("Unknown workflow catalog tool.");
        }
    }

    public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
        ToolExecutionEvidence previousEvidence, CancellationToken cancellationToken)
    {
        ToolResult Unconfirmed(string message) => Unknown(context, message, call.Name == RunTool ? RunId(context) : null);
        cancellationToken.ThrowIfCancellationRequested();
        if (previousEvidence.ProviderId != Provider || previousEvidence.RequestId != context.EffectId)
            return Task.FromResult(Unconfirmed("No matching catalog receipt is available; no command was replayed."));
        var worker = host();
        var source = worker.ValidateDataContext(context, call.Name);
        var saved = WorkerWorkflows.Read<WorkerWorkflowEffects.ToolArguments>(source.Effect!.Arguments);
        if (!JsonNode.DeepEquals(JsonNode.Parse(saved.Arguments.GetRawText()), JsonNode.Parse(call.Arguments.GetRawText())))
            return Task.FromResult(Unconfirmed("The receipt arguments changed; no command was replayed."));
        if (call.Name == RunTool)
        {
            if (previousEvidence.JobId != RunId(context))
                return Task.FromResult(Unconfirmed("The receipt belongs to a different run; no command was replayed."));
            var requested = WorkerWorkflows.Read<RunRequest>(call.Arguments);
            var run = worker.Store.TryGet(previousEvidence.JobId);
            if (run is null) return Task.FromResult(Unconfirmed("The exact run is unavailable; no command was replayed."));
            run = Inspect(worker, context, run.Id);
            var accepted = WorkerWorkflows.Read<WorkflowDataRunInput>(run.Submission.Input);
            if (accepted.Id != requested.Id || accepted.Version != requested.Version || accepted.Hash != requested.Hash ||
                !JsonNode.DeepEquals(JsonNode.Parse(accepted.Arguments.GetRawText()), JsonNode.Parse(requested.Input.GetRawText())))
                return Task.FromResult(Unconfirmed("The saved run input differs; no command was replayed."));
            return Task.FromResult(RunResult(context, run, false));
        }
        if (call.Name == RegisterTool)
        {
            var edition = WorkerWorkflows.Read<WorkflowDataEdition>(call.Arguments.GetProperty("edition"));
            var entry = worker.GetWorkflowData(new(context.JobId, edition.Id, edition.Version, WorkflowDataCatalog.Hash(edition)), context.Profile);
            return Task.FromResult(ReadResult(context, new { status = "present", entry.Edition.Id, entry.Edition.Version, entry.Hash }));
        }
        // These operations only read retained state. Mutating publication/run never enter this path.
        return ExecuteAsync(call, context, cancellationToken);
    }

    private static WorkflowRun Inspect(WorkerWorkflows worker, ToolExecutionContext context, string runId)
    {
        var run = worker.GetOwned(runId, context.Profile);
        var selected = WorkerWorkflows.Read<WorkflowDataRunInput>(run.Submission.Input);
        var source = worker.GetOwned(context.JobId, context.Profile);
        if (run.Submission.DefinitionId != WorkflowDataModule.DefinitionId || selected.SourceRequestId != context.JobId ||
            run.Submission.ProjectId != source.Submission.ProjectId ||
            run.Submission.ConversationId != WorkerWorkflows.WorkflowDataConversationId(run.Id))
            throw new UnauthorizedAccessException("The receipt is not a data run from this source assignment.");
        return run;
    }

    private static ToolResult RunResult(ToolExecutionContext context, WorkflowRun run, bool includeOutput)
    {
        var outcome = run.Status switch
        {
            WorkflowStatus.Completed => ToolExecutionOutcome.Succeeded,
            WorkflowStatus.Failed => ToolExecutionOutcome.Failed,
            WorkflowStatus.Cancelled => ToolExecutionOutcome.Cancelled,
            _ => ToolExecutionOutcome.Unknown
        };
        var completed = run.State.TryGetProperty("index", out var index) && index.TryGetInt32(out var count) ? count : 0;
        // Failure-budget counters only cover throttled operations. A linear workflow stops
        // on its first failed command, whose exact outcome remains in the durable signal.
        var failed = run.Signal.Kind == WorkflowSignalKind.EffectResult &&
            WorkerWorkflows.Read<WorkflowCommandResult>(run.Signal.Data).State == WorkflowEffectState.Failed ? 1 : 0;
        var body = CerebralJson.Element(new { runId = run.Id, status = run.Status.ToString(), completedCommands = completed,
            failedCommands = failed, message = BoundedDiagnostic(run.Effect?.Error ?? run.Error ?? run.WaitingReason),
            outputAvailable = run.Result.HasValue, output = includeOutput ? run.Result : null });
        return new(outcome == ToolExecutionOutcome.Succeeded, body.GetRawText())
        { Evidence = new(outcome, Provider, context.EffectId, run.Id, run.Status.ToString()) };
    }

    private static ToolResult ReadResult(ToolExecutionContext context, object value) =>
        new(true, CerebralJson.Element(value).GetRawText())
        { Evidence = new(ToolExecutionOutcome.Succeeded, Provider, context.EffectId) };
    private static ToolResult Rejected(ToolExecutionContext context, string code, string message) =>
        new(false, CerebralJson.Element(new { status = "rejected", code, execution = "no_workflow_dispatched",
            message = BoundedDiagnostic(message),
            hint = "List the project catalog, get the selected exact edition, and copy its ID/version/hash and input schema. The edition hash is not a file hash; do not invent coordinates." }).GetRawText())
        { Evidence = new(ToolExecutionOutcome.Rejected, Provider, context.EffectId, State: "rejected") };
    private static ToolResult Unknown(ToolExecutionContext context, string message, string? runId = null) =>
        new(false, message) { Evidence = new(ToolExecutionOutcome.Unknown, Provider, context.EffectId, runId) };
    private static string? BoundedDiagnostic(string? value) => value is null || value.Length <= 1024 ? value : value[..1024];
    internal const string Guide = """
        Workflows are immutable project-scoped DATA, not executable modules. Use workflow.catalog.list, then get only the selected exact ID/version/hash. Reuse an existing edition where appropriate.
        To publish, call workflow.catalog.register with {"edition": EDITION}. Edition:
        {"schema":"cerebral.workflow-data/1","id":"inspect-source","version":"1.0","description":"Inspect one source file",
         "inputSchema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false},
         "outputSchema":{"type":"string"},"requiredCapabilities":["filesystem.read"],
         "stages":[{"id":"inspect","tool":"file.inspect","arguments":{"path":{"$input":"/path"}}}],
         "output":{"$stage":{"id":"inspect","pointer":"/content"}}}
        Bounds: complete edition32KiB,1-16 linear stages,128 editions/project. IDs use ASCII letters/digits/._-, max100. Exact version max64; no inferred latest version. Description max1024 UTF-8 bytes.
        Template grammar: JSON literals, arrays and objects; singleton {"$input":"/pointer"} selects typed input; singleton {"$stage":{"id":"earlierStage","pointer":"/content"}} selects a prior successful standard ToolData result. Empty pointer selects the whole value. RFC6901 escaping uses ~0 for tilde and ~1 for slash. There are no expressions, loops, JSON parsing or dynamic tool names.
        Each stage has id/tool/arguments and optional literal workingDirectory. Every tool capability must appear in requiredCapabilities. The edition's input/output schemas use the engine's closed JSON Schema subset. Current tool input validation still applies after references resolve.
        Use only currently granted/discovered tool names. PAO normally runs behind semantic file/process/Git tools; its raw gateway is intentionally absent from the agent catalog. Grammar support does not advertise or grant a raw PAO action. Guide examples are illustrative, not installed editions. A hash selects canonical workflow DATA, never the input file or an empty-file digest. First slice supports existing math/file/process/Git/HTTP/scaffold/verify typed tools and a constrained native PAO subset: stat,ls,find,search,read_lines,sha,wr,replace,mkdir,cp,dotnet_build,dotnet_test,dotnet_restore,git_status,git_diff,run,job_stat. PAO op must be a literal; root,do,model/engine/install/present/delegation and unknown extension semantics are excluded. Registered process execution retains existing path/executable rules; it is not an operating-system sandbox.
        Registration grants nothing. Identical ID/version content is idempotent; changed content must use a new exact version. A run inherits this source assignment's accepted tools, roots, capabilities, Cortex scope and current revocations, and freezes the complete selected edition.
        Call workflow.catalog.run {"id":"inspect-source","version":"1.0","hash":"HASH_FROM_REGISTRATION","input":{"path":"ALLOWED_PATH"},"timeoutSeconds":120}. The normal tool timeout defaults to30seconds; use an explicit 1-300second value for longer sequences, within accepted source budgets. Commands execute in order without model decisions between them. Failed, partial, truncated, generation/follow-up, cancelled or uncertain outcomes cannot mean success. Results/state are bounded to128KiB. Read the compact returned receipt; request workflow.catalog.status {"runId":"EXACT_RECEIPT_ID","includeOutput":true} for final output when needed. An uncertain run must be inspected/reconciled, never submitted again.
        Native PAO recipe edition/hash pinning, branching and cross-project publication are not part of this grammar. Ordinary projects and authenticated durable assignments are required; legacy/direct calls have no durable source identity.
        """;
    private static ToolDescriptor Tool(string name, string description, string properties, string required) =>
        new(name, description, JsonDocument.Parse("{\"type\":\"object\",\"properties\":" + properties +
            ",\"required\":" + required + ",\"additionalProperties\":false}").RootElement.Clone(), "diagnostics")
        { PrerequisiteTools = name switch { RunTool => [ListTool, GetTool], GetTool => [ListTool],
            RegisterTool => [GuideTool], _ => [] } };
}
