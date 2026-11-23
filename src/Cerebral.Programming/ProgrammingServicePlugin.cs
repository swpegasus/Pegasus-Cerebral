using System.Text.Json;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

public sealed class ProgrammingServicePlugin : IServicePlugin, IAgentHarnessModule, IServiceWorkflowContextModule, IWorkflowModule
{
    private readonly ProgrammingCompilerContextContribution compilerContext = new();
    public IReadOnlyList<IWorkflowContextContribution> ContextContributions => [compilerContext];
    string IWorkflowModule.Id => "pegasus.programming";
    IReadOnlyList<IWorkflowDefinition> IWorkflowModule.Workflows => [];
    IReadOnlyList<IWorkflowCommandHandler> IWorkflowModule.Commands => [compilerContext];
    public IReadOnlyList<IAgentHarness> Harnesses { get; } = [new ProgrammingAgentHarness(), new ProgrammingPreparedInterventionHarness(), new ProgrammingVerificationRetryHarness(), new ProgrammingVerificationRetryHarness(runArguments: true), new ProgrammingFinalizationProgressHarness(), new ProgrammingInterfacePresentationHarness(), new ProgrammingPrerequisiteProgressHarness(), new ProgrammingAgentHarness(staged: true)];
    private ProgrammingLibrary? library;
    public IReadOnlyList<IToolExtension> ToolExtensions { get; private set; } = [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions { get; private set; } = [];
    public void Initialize(IServicePluginHost host)
    {
        if (library is not null) throw new InvalidOperationException("Pegasus Programming is already initialized.");
        library = new(host.DataDirectory); ToolExtensions = [new ProgrammingTools(library)]; CommandExtensions = [new ProgrammingCommands(library)];
    }
    public void Dispose() { library = null; ToolExtensions = []; CommandExtensions = []; }

    private sealed class ProgrammingCommands(ProgrammingLibrary library) : IServiceCommandExtension
    {
        public IReadOnlyList<string> Operations { get; } = ["programming.status", "programming.list", "programming.create", "programming.inspect", "programming.plan", "programming.item.update", "programming.decision.record", "programming.check.record", "programming.context"];
        public Task<object?> ExecuteAsync(string operation, JsonElement args, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); object result = operation switch
            {
                "programming.status" => library.Status(),
                "programming.list" => library.List(),
                "programming.create" => library.Create(Text(args, "name"), Text(args, "rootPath"), Optional(args, "purpose")),
                "programming.inspect" => library.Inspect(Text(args, "projectId")),
                "programming.plan" => library.SavePlan(Text(args, "projectId"), Plan(args)),
                "programming.item.update" => library.UpdateItem(Text(args, "projectId"), Long(args, "itemId"), Text(args, "status"), Optional(args, "notes"), Strings(args, "filePaths")),
                "programming.decision.record" => library.RecordDecision(Text(args, "projectId"), Text(args, "title"), Text(args, "decision"), Optional(args, "rationale")),
                "programming.check.record" => library.RecordCheck(Text(args, "projectId"), Text(args, "kind"), Optional(args, "command"), Text(args, "outcome"), Optional(args, "details")),
                "programming.context" => library.Context(Text(args, "projectId"), Optional(args, "focus"), Integer(args, "maxUtf8Bytes", 16 * 1024)),
                _ => throw new ArgumentException("Unknown programming command.")
            }; return Task.FromResult<object?>(result);
        }
    }

    private sealed class ProgrammingTools : IToolExtension
    {
        private readonly ProgrammingLibrary library;
        public ProgrammingTools(ProgrammingLibrary library) { this.library = library; }
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [
            Tool("program.list", "List durable programming workspaces and their opaque project IDs.", "program.read", [], new { }),
            Tool("program.inspect", "Inspect one programming workspace plan, work items, recent decisions, and recent checks without loading source files.", "program.read", ["projectId"], new { projectId = TextSchema("Exact project ID returned by program.list.") }),
            Tool("program.context", "Assemble bounded current programming context for one task. Source files remain available through Cerebral file tools.", "program.read", ["projectId"], new { projectId = TextSchema("Exact programming project ID."), focus = TextSchema("Current task or question."), maxUtf8Bytes = IntegerSchema(1024, 24 * 1024) }, ["program.inspect"]),
            Tool("program.plan", "Save the first milestone and work-item plan for a programming project. Plans guide execution and are not substituted for file, build, test, or runtime receipts.", "program.write", ["projectId", "milestones"], new { projectId = TextSchema("Exact programming project ID."), milestones = new { type = "array", minItems = 1, maxItems = 50, items = new { type = "object", properties = new { title = TextSchema("Milestone title."), objective = TextSchema("Milestone outcome."), items = new { type = "array", minItems = 1, maxItems = 100, items = new { type = "object", properties = new { title = TextSchema("Work item title."), details = TextSchema("Specific bounded work."), kind = TextSchema("design, code, test, review, documentation, or custom.") }, required = new[] { "title", "details", "kind" }, additionalProperties = false } } }, required = new[] { "title", "objective", "items" }, additionalProperties = false } } }, ["program.inspect"]),
             Tool("program.item.update", "Update one exact work item after real progress or a blocker. Include relevant paths but keep source and command output in their owned tools.", "program.write", ["projectId", "itemId", "status"], new { projectId = TextSchema("Exact programming project ID."), itemId = IntegerSchema(1, int.MaxValue), status = EnumSchema("planned", "active", "blocked", "done", "cancelled"), notes = TextSchema("Concise verified outcome or blocker."), filePaths = new { type = "array", maxItems = 32, items = TextSchema("Relevant path.") } }, ["program.inspect"]),
             Tool("program.decision.record", "Record a durable architecture or product decision and its rationale.", "program.write", ["projectId", "title", "decision"], new { projectId = TextSchema("Exact programming project ID."), title = TextSchema("Decision title."), decision = TextSchema("Chosen behavior or design."), rationale = TextSchema("Why this choice was made.") }, ["program.inspect"]),
             Tool("program.check.record", "Record the compact outcome of a real build, test, runtime, review, or diagnostic check. Execute the check through the appropriate Cerebral tool first.", "program.check", ["projectId", "kind", "outcome"], new { projectId = TextSchema("Exact programming project ID."), kind = TextSchema("build, test, runtime, review, or diagnostic."), command = TextSchema("Compact command or operation name."), outcome = EnumSchema("passed", "failed", "blocked", "skipped"), details = TextSchema("Concise evidence or failure." ) }, ["program.inspect"]),
             Tool(ProgrammingSourceStructure.Name, "Inspect current C# namespace/type/member boundaries and enclosing scopes at a physical line. Requires a fresh file.read SHA. Returns at most 4 KiB of syntax evidence with omissions, recovery and conditional-options caveats; eligible ranges describe isolated complete declarations, never semantic repair safety or verification. Optional proposedEdit adds exact-draft lexical and current statement/initializer boundary evidence for reconsideration before resubmission.", "filesystem.read", ["path", "expectedSha256"], new
             {
                 path = BoundedTextSchema("Existing scoped .cs file; at most 32 KiB UTF-8 source.", 1024),
                 expectedSha256 = new { type = "string", pattern = "^[0-9a-fA-F]{64}$", description = "Current whole-file SHA-256 from file.read." },
                 focusLine = IntegerSchema(1, 1_000_000),
                  proposedEdit = new { type = "object", properties = new {
                      startLine = IntegerSchema(1, 1_000_000), lineCount = IntegerSchema(1, 512),
                      newText = new { type = "string", maxLength = ProgrammingEditProposal.MaximumTextBytes,
                          description = "Exact proposed replacement, including empty deletion; at most 64 KiB strict UTF-8. Inspection applies no edit." }
                  }, required = new[] { "startLine", "lineCount", "newText" }, additionalProperties = false }
             }, ["file.read"]),
             Tool("program.source.generate", "Prepare one bounded complete replacement for a small existing source file. The service rereads the exact target and selected reference files, checks the target SHA-256, generates source as ordinary text, then sends it only to revision-checked file.write. Use this when a cohesive replacement is safer than embedding substantial code in structured JSON.", "filesystem.write", ["path", "expectedSha256", "instructions"], new
             {
                 path = BoundedTextSchema("Exact existing source file inside a saved project root.", 32768),
                 expectedSha256 = new { type = "string", pattern = "^[0-9a-fA-F]{64}$", description = "Whole-file SHA-256 returned by the latest file.read." },
                 instructions = BoundedTextSchema("Bounded implementation objective for this one source replacement.", 8192),
                 referencePaths = new { type = "array", maxItems = 8, uniqueItems = true, items = BoundedTextSchema("Other narrowly selected existing contract or example file inside an allowed root. The target source is included automatically; do not repeat path here.", 32768) }
             }, ["file.read", "file.write"])
         ];
         public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
         {
             cancellationToken.ThrowIfCancellationRequested(); try
             {
                 if (call.Name == ProgrammingSourceStructure.Name)
                     return await ProgrammingSourceStructure.InspectAsync(call.Arguments, context, cancellationToken);
                 if (call.Name == "program.source.generate")
                     return await ProgrammingSourceGeneration.PrepareAsync(call.Arguments, context, cancellationToken);
                 var args = call.Arguments; object value = call.Name switch
                 {
                    "program.list" => library.List(), "program.inspect" => library.Inspect(Text(args, "projectId")),
                    "program.context" => library.Context(Text(args, "projectId"), Optional(args, "focus"), Integer(args, "maxUtf8Bytes", 16 * 1024)),
                    "program.plan" => library.SavePlan(Text(args, "projectId"), Plan(args)),
                    "program.item.update" => library.UpdateItem(Text(args, "projectId"), Long(args, "itemId"), Text(args, "status"), Optional(args, "notes"), Strings(args, "filePaths")),
                    "program.decision.record" => library.RecordDecision(Text(args, "projectId"), Text(args, "title"), Text(args, "decision"), Optional(args, "rationale")),
                    "program.check.record" => library.RecordCheck(Text(args, "projectId"), Text(args, "kind"), Optional(args, "command"), Text(args, "outcome"), Optional(args, "details")),
                    _ => throw new ArgumentException("Unknown programming tool.")
                 }; return new ToolResult(true, JsonSerializer.Serialize(value, CerebralJson.Options));
             }
             catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { return new ToolResult(false, ex.Message); }
         }
    }

    private static ToolDescriptor Tool(string name, string description, string capability, string[] required, object properties, string[]? prerequisites = null) =>
        new(name, description, JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false }, CerebralJson.Options), capability) { PrerequisiteTools = prerequisites ?? [] };
     private static object TextSchema(string description) => new { type = "string", maxLength = 16000, description };
     private static object BoundedTextSchema(string description, int maximum) => new { type = "string", minLength = 1, maxLength = maximum, description };
    private static object IntegerSchema(int minimum, int maximum) => new { type = "integer", minimum, maximum };
    private static object EnumSchema(params string[] values) => new { type = "string", @enum = values };
    private static IReadOnlyList<ProgrammingPlanMilestone> Plan(JsonElement args) => args.GetProperty("milestones").EnumerateArray().Select(m => new ProgrammingPlanMilestone(Text(m, "title"), Optional(m, "objective"), m.GetProperty("items").EnumerateArray().Select(i => new ProgrammingPlanItem(Text(i, "title"), Optional(i, "details"), Text(i, "kind"))).ToArray())).ToArray();
    private static string Text(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new ArgumentException(name + " is required.");
    private static string Optional(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static long Long(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : throw new ArgumentException(name + " is required.");
    private static int Integer(JsonElement args, string name, int fallback) => args.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static string[] Strings(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToArray() : [];
}
