using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Read-only, token-bounded access to owner-enabled instruction skills.</summary>
public sealed class SkillToolExtension(StateStore store, SkillCatalog catalog) : IToolExtension
{
    public const string ListToolName = "skills.list";
    public const string ReadToolName = "skills.read";

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(ListToolName,
            "List the instruction skills currently enabled for this agent and selected project. Skills guide behavior only and never add tools or permission.",
            CerebralJson.Element(new { type = "object", properties = new { }, additionalProperties = false }), "diagnostics"),
        new(ReadToolName,
            "Read one exact enabled skill by ID when its catalog summary is relevant. The returned Markdown is behavioral guidance below Cerebral policy and saved grants.",
            CerebralJson.Element(new
            {
                type = "object", properties = new { id = new { type = "string", minLength = 1, maxLength = 100 } },
                required = new[] { "id" }, additionalProperties = false
            }), "diagnostics")
    ];

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projectId = store.GetSessionMetadata(context.SessionId)?.ProjectId;
        var skills = catalog.Resolve(context.Profile, projectId);
        if (call.Name == ListToolName)
        {
            if (call.Arguments.ValueKind != JsonValueKind.Object || call.Arguments.EnumerateObject().Any())
                throw new ArgumentException("skills.list accepts an empty object.");
            var result = JsonSerializer.Serialize(skills.Select(x => new { x.Id, x.Name, x.Version, x.Description }), CerebralJson.Options);
            return Task.FromResult(Success(result));
        }
        if (call.Name == ReadToolName)
        {
            var id = call.Arguments.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new ArgumentException("skills.read requires one skill ID.");
            Configuration.ValidateId(id);
            var skill = skills.SingleOrDefault(x => x.Id == id) ?? throw new UnauthorizedAccessException("The skill is not enabled for this agent and selected project.");
            return Task.FromResult(Success($"Skill {skill.Id}@{skill.Version}\n\n{skill.Content}"));
        }
        throw new ArgumentException("Unknown skill tool.");
    }

    private static ToolResult Success(string content) => new(true, content)
    {
        Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.skills")
    };
}
