using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private static readonly JsonElement ActionSchema = Parse("""
        {"oneOf":[
          {"type":"object","properties":{"action":{"const":"final"},"text":{"type":"string","minLength":1,"maxLength":32768}},"required":["action","text"],"additionalProperties":false},
          {"type":"object","properties":{"action":{"const":"blocked"},"reason":{"type":"string","minLength":1,"maxLength":4000}},"required":["action","reason"],"additionalProperties":false},
          {"type":"object","properties":{"action":{"const":"describe"},"names":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"string","minLength":1,"maxLength":256}}},"required":["action","names"],"additionalProperties":false},
          {"type":"object","properties":{"action":{"const":"tool"},"name":{"type":"string","minLength":1,"maxLength":256},"arguments":{"type":"object"}},"required":["action","name","arguments"],"additionalProperties":false},
          {"type":"object","properties":{"action":{"const":"questions"},"fields":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"object","properties":{"id":{"type":"string","minLength":1,"maxLength":64},"label":{"type":"string","minLength":1,"maxLength":2048},"required":{"type":"boolean"},"multiple":{"type":"boolean"},"description":{"type":"string","minLength":1,"maxLength":8192},"choices":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"string","minLength":1,"maxLength":1024}}},"required":["id","label"],"additionalProperties":false}}},"required":["action","fields"],"additionalProperties":false},
          {"type":"object","properties":{"action":{"const":"receipt"},"effectId":{"type":"string","minLength":1,"maxLength":128},"offset":{"type":"integer","minimum":0},"expectedRevision":{"type":"integer","minimum":1}},"required":["action","effectId","offset"],"additionalProperties":false},
          {"type":"object","properties":{"action":{"const":"history"},"beforeSequence":{"type":"integer","minimum":1}},"required":["action","beforeSequence"],"additionalProperties":false},
          {"type":"object","properties":{"action":{"const":"history"},"eventId":{"type":"string","minLength":1,"maxLength":128},"offset":{"type":"integer","minimum":0}},"required":["action","eventId","offset"],"additionalProperties":false}
        ]}
        """);

    private static JsonElement GenerationActionSchema(HarnessState state)
    {
        var alternatives = new JsonArray();
        foreach (var variant in ActionSchema.GetProperty("oneOf").EnumerateArray())
            if (variant.GetProperty("properties").GetProperty("action").GetProperty("const").GetString() != "tool")
            {
                var generated = JsonNode.Parse(variant.GetRawText())!;
                if (variant.GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "questions")
                {
                    // Fresh model decisions explicitly choose necessity, mode and an explanation. Admission
                    // still accepts older actions that omitted the additive metadata.
                    generated["properties"]!["fields"]!["items"] = GeneratedQuestionFieldSchema(
                        generated["properties"]!["fields"]!["items"]!);
                }
                alternatives.Add(generated);
            }
        var specialized = new HashSet<string>(StringComparer.Ordinal);
        // Generation advertises only current ID/revision pairs; host admission still rechecks them.
        if (state.Helpers.Count != 0) alternatives.Add(JsonNode.Parse(Element(new
        {
            type = "object", properties = new { action = new { @const = "delegate" }, assignments = new
            {
                type = "array", minItems = 1, maxItems = 8, items = new { oneOf = state.Helpers.Select(helper => new
                {
                    type = "object", properties = new
                    {
                        profileId = new { type = "string", @const = helper.Id },
                        profileRevision = new { type = "integer", @const = helper.Revision },
                        instruction = new { type = "string", minLength = 1, maxLength = 8192 },
                        workerContextId = new { type = "string", minLength = 1, maxLength = 128,
                            description = "Omit this property for a new worker. For reuse, send only an exact host-returned workerContextId. Never send null." },
                        workerName = new { type = "string", minLength = 1, maxLength = 128 },
                        requestedReasoningTokens = new { type = "integer", minimum = 0, maximum = 32768 },
                        requestedContextTokens = new { type = "integer", minimum = 512, maximum = 1048576 },
                        requestedResponseTokens = new { type = "integer", minimum = 128, maximum = 131072 },
                        maxContextTokens = new { type = "integer", minimum = 512, maximum = 1048576 },
                        maxResponseTokens = new { type = "integer", minimum = 128, maximum = 131072 }
                    }, required = new[] { "profileId", "profileRevision", "instruction" }, additionalProperties = false
                }).ToArray() }
            } }, required = new[] { "action", "assignments" }, additionalProperties = false
        }).GetRawText()));
        // A current revision-checked mutation must not lose its generation branch to a
        // later optional schema. This prioritizes guidance only; host admission is unchanged.
        foreach (var tool in state.Selected.Where(tool => ModelToolExecutable(state, tool.Name)).Reverse().OrderBy(tool =>
                     state.KnownFileRevisions.Count > 0 && tool.Name is ("file.write" or "file.edit") ? 0 : 1))
        {
            if (tool.InputSchema is not { ValueKind: JsonValueKind.Object } schema) continue;
            // A local $ref cannot be moved under a different schema root without rewriting
            // its references. Keep that tool on the normal host-validated discovery lane.
            if (schema.GetRawText().Contains("\"$ref\"", StringComparison.Ordinal)) continue;
            var argumentSchemas = GeneratedToolArgumentSchemas(tool.Name, schema, state);
            var added = 0;
            foreach (var arguments in argumentSchemas)
            {
                var variant = Element(new
                {
                    type = "object",
                    properties = new { action = new { @const = "tool" }, name = new { @const = tool.Name }, arguments },
                    required = new[] { "action", "name", "arguments" }, additionalProperties = false
                });
                alternatives.Add(JsonNode.Parse(variant.GetRawText()));
                // Generation arguments retain the readable selected contracts too. Bound the
                // additional grammar so it cannot crowd out the existing durable request.
                if (Encoding.UTF8.GetByteCount(alternatives.ToJsonString()) > MaximumCatalogBytes / 2)
                {
                    alternatives.RemoveAt(alternatives.Count - 1);
                    break;
                }
                added++;
            }
            // A selected mutation contract that has no current host-known preimage is omitted
            // from generation rather than falling back to unconstrained arbitrary arguments.
            if (added > 0 || tool.Name is "file.edit" or "file.write") specialized.Add(tool.Name);
        }
        // Unknown argument shapes must be described first. A generic discovery-call
        // branch lets malformed JSON get stuck in correction before exact lookup ever
        // runs. Keep executable argument fallback only for selected contracts that cannot be
        // embedded (for example root-relative references or the grammar size bound).
        var remaining = state.Selected.Select(tool => tool.Name).Distinct(StringComparer.Ordinal)
            .Where(name => ModelToolExecutable(state, name) && !specialized.Contains(name)).ToArray();
        if (remaining.Length > 0)
            alternatives.Add(JsonNode.Parse(Element(new
            {
                type = "object",
                properties = new { action = new { @const = "tool" }, name = new { type = "string", @enum = remaining }, arguments = new { type = "object" } },
                required = new[] { "action", "name", "arguments" }, additionalProperties = false
            }).GetRawText()));
        var unselected = state.Discovery.Select(tool => tool.Name)
            .Where(name => ModelToolExecutable(state, name) && !state.Selected.Any(tool => tool.Name == name)).Distinct(StringComparer.Ordinal).ToArray();
        if (unselected.Length > 0)
            alternatives.Add(JsonNode.Parse(Element(new
            {
                type = "object",
                properties = new
                {
                    action = new { @const = "tool" }, name = new { type = "string", @enum = unselected },
                    arguments = new { type = "object", properties = new { }, additionalProperties = false }
                },
                required = new[] { "action", "name", "arguments" }, additionalProperties = false
            }).GetRawText()));
        return Element(new { oneOf = alternatives });
    }

    private static JsonObject GeneratedQuestionFieldSchema(JsonNode field)
    {
        // Selection mode and its choices form one contract. A loose Boolean allowed the
        // decoder to repeatedly request multiple selection without any offered choices.
        // Separate closed shapes also keep free text from acquiring invented choices.
        var variants = new JsonArray();
        foreach (var mode in new[] { (Choices: false, Multiple: false), (Choices: true, Multiple: false), (Choices: true, Multiple: true) })
        {
            var variant = field.DeepClone();
            var properties = variant["properties"]!.AsObject();
            properties["multiple"] = new JsonObject { ["const"] = mode.Multiple };
            if (!mode.Choices) properties.Remove("choices");
            var required = new JsonArray("id", "label", "required", "multiple", "description");
            if (mode.Choices) required.Add("choices");
            variant["required"] = required;
            variants.Add(variant);
        }
        return new JsonObject { ["oneOf"] = variants };
    }

    // This is action guidance, never an authorization grant; the provider rechecks admission.
    private static bool ModelToolExecutable(HarnessState state, string name) =>
        (name != "conversation.response-balance" || state.TaskBudget is null) &&
        (name != "plugin.scaffold" || state.ScaffoldReadiness?.Ready == true) &&
        (name != "file.write" || !RejectedExistingDestinationNeedsRead(state));

    private static JsonElement ExplicitDefaultArguments(JsonElement schema)
    {
        // Decoders tend to omit optional options even after a failed implicit default.
        // Make those choices explicit in generation only; do not change the published
        // tool contract, supply a default, or relax the host's mutation preconditions.
        var generated = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (generated["properties"] is not JsonObject properties) return schema;
        var required = generated["required"] as JsonArray ?? new JsonArray();
        var names = required.Select(value => value?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var property in properties)
            if (property.Value is JsonObject field && field.ContainsKey("default") && names.Add(property.Key))
                required.Add(property.Key);
        if (generated["required"] is null && required.Count > 0) generated["required"] = required;
        return Element(generated);
    }

    private static Input ReadInput(JsonElement args)
    {
        ObjectFields(args, [], ["workingDirectory", "projectId", "responseSharePercent", "requiredSuccessfulTools", "requestedReasoningTokens",
            "requestedContextTokens", "requestedResponseTokens", "maxContextTokens", "maxResponseTokens"]);
        var directory = args.TryGetProperty("workingDirectory", out var path) ? StringValue(path, 32768) : null;
        if (directory is not null && !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("workingDirectory must be an absolute path; creating a conversation does not create a folder.");
        var project = args.TryGetProperty("projectId", out var projectId) ? StringValue(projectId, 128) : null;
        var share = args.TryGetProperty("responseSharePercent", out var response)
            ? checked((int)Integer(response, 5, 50)) : (int?)null;
        var requestedReasoningTokens = args.TryGetProperty("requestedReasoningTokens", out var requested)
            ? checked((int)Integer(requested, 0, 32768)) : (int?)null;
        var requestedContextTokens = args.TryGetProperty("requestedContextTokens", out var context)
            ? checked((int)Integer(context, 512, 1048576)) : (int?)null;
        var requestedResponseTokens = args.TryGetProperty("requestedResponseTokens", out var output)
            ? checked((int)Integer(output, 128, 131072)) : (int?)null;
        var maxContextTokens = args.TryGetProperty("maxContextTokens", out var contextMax)
            ? checked((int)Integer(contextMax, 512, 1048576)) : (int?)null;
        var maxResponseTokens = args.TryGetProperty("maxResponseTokens", out var outputMax)
            ? checked((int)Integer(outputMax, 128, 131072)) : (int?)null;
        if (requestedResponseTokens is not null && share is not null)
            throw new ArgumentException("An explicit response allowance cannot also use response share.");
        var requiredTools = args.TryGetProperty("requiredSuccessfulTools", out var requiredValue) ? Strings(requiredValue) : [];
        if (requiredTools.Length > 8 || requiredTools.Distinct(StringComparer.Ordinal).Count() != requiredTools.Length)
            throw new ArgumentException("requiredSuccessfulTools accepts at most eight distinct exact tool names.");
        foreach (var tool in requiredTools)
        {
            BoundedText(tool, 256);
            if (tool.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
                throw new ArgumentException("Required tool names use ASCII letters, numbers, dots, underscores or hyphens.");
        }
        return new(directory, project, share, requiredTools, requestedReasoningTokens,
            RequestedContextTokens: requestedContextTokens, RequestedResponseTokens: requestedResponseTokens,
            MaxContextTokens: maxContextTokens, MaxResponseTokens: maxResponseTokens);
    }

    private static void ValidateAction(JsonElement action, bool allowCatalogDiscovery = false,
        bool allowReasoningAdjustment = false, bool allowTaskBudget = false)
    {
        if (action.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(action.GetRawText()) > 64 * 1024)
            throw new ArgumentException("A bounded JSON action object is required.");
        UniqueProperties(action);
        var kind = RequiredText(action, "action", 32);
        switch (kind)
        {
            case "answer":
            case "final": ObjectFields(action, ["action", "text"], []); RequiredText(action, "text", 32768); break;
            case "coordinate": ObjectFields(action, ["action", "instruction"], []); RequiredText(action, "instruction", 8192); break;
            case "blocked": ObjectFields(action, ["action", "reason"], []); RequiredText(action, "reason", 4000); break;
            case "reasoning":
                if (!allowReasoningAdjustment) throw new ArgumentException("This task has no owner-approved reasoning adjustment.");
                ObjectFields(action, ["action", "tokens"], []);
                Integer(action.GetProperty("tokens"), 0, 32768);
                break;
            case "budget":
                if (!allowTaskBudget) throw new ArgumentException("This task has no owner-approved autonomous budget selection.");
                ObjectFields(action, ["action"], ["contextTokens", "responseTokens"]);
                var hasContext = action.TryGetProperty("contextTokens", out var selectedContext);
                var hasResponse = action.TryGetProperty("responseTokens", out var selectedResponse);
                if (!hasContext && !hasResponse)
                    throw new ArgumentException("Choose at least one enabled budget dimension.");
                if (hasContext) Integer(selectedContext, 512, 1048576);
                if (hasResponse) Integer(selectedResponse, 128, 131072);
                break;
            case "describe":
                ObjectFields(action, ["action", "names"], []);
                var names = Strings(action.GetProperty("names"));
                if (names.Length is < 1 or > 8 || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                    throw new ArgumentException("Describe requires one to eight distinct names.");
                foreach (var name in names) BoundedText(name, 256);
                break;
            case "discover":
                if (!allowCatalogDiscovery) throw new ArgumentException("This accepted edition has no paged catalog action.");
                ObjectFields(action, ["action"], ["query", "afterName"]);
                _ = ReadCatalogPageRequest(DiscoveryArguments(action));
                break;
            case "tool":
                ObjectFields(action, ["action", "name", "arguments"], []); RequiredText(action, "name", 256);
                if (action.GetProperty("arguments").ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Tool arguments must be an object.");
                break;
            case "questions":
                ObjectFields(action, ["action", "fields"], []); _ = ReadQuestions(action.GetProperty("fields")); break;
            case "delegate":
                ObjectFields(action, ["action", "assignments"], []);
                var assignments = action.GetProperty("assignments");
                if (assignments.ValueKind != JsonValueKind.Array || assignments.GetArrayLength() is < 1 or > 8)
                    throw new ArgumentException("Delegation requires one to eight assignments.");
                for (var assignmentIndex = 0; assignmentIndex < assignments.GetArrayLength(); assignmentIndex++)
                {
                    var assignment = assignments[assignmentIndex];
                    ObjectFields(assignment, ["profileId", "profileRevision", "instruction"], ["workerContextId", "workerName", "requestedReasoningTokens",
                        "requestedContextTokens", "requestedResponseTokens", "maxContextTokens", "maxResponseTokens"]);
                    RequiredText(assignment, "profileId", 128); RequiredText(assignment, "instruction", 8192);
                    Integer(assignment.GetProperty("profileRevision"), 0, int.MaxValue);
                    foreach (var name in new[] { "workerContextId", "workerName" })
                        if (assignment.TryGetProperty(name, out var optionalText))
                        {
                            if (optionalText.ValueKind != JsonValueKind.String)
                                throw new DelegateOptionalTextTypeException(assignmentIndex, name, optionalText.ValueKind);
                            RequiredText(assignment, name, 128);
                        }
                    if (assignment.TryGetProperty("requestedReasoningTokens", out var childTokens))
                        Integer(childTokens, 0, 32768);
                    foreach (var name in new[] { "requestedContextTokens", "maxContextTokens" })
                        if (assignment.TryGetProperty(name, out var value)) Integer(value, 512, 1048576);
                    foreach (var name in new[] { "requestedResponseTokens", "maxResponseTokens" })
                        if (assignment.TryGetProperty(name, out var value)) Integer(value, 128, 131072);
                }
                break;
            case "receipt":
                ObjectFields(action, ["action", "effectId", "offset"], ["expectedRevision"]);
                RequiredText(action, "effectId", 128); Integer(action.GetProperty("offset"), 0, int.MaxValue);
                if (action.TryGetProperty("expectedRevision", out var revision)) Integer(revision, 1, long.MaxValue);
                break;
            case "history":
                if (action.TryGetProperty("eventId", out _))
                {
                    ObjectFields(action, ["action", "eventId", "offset"], []);
                    RequiredText(action, "eventId", 128); Integer(action.GetProperty("offset"), 0, int.MaxValue);
                }
                else
                {
                    ObjectFields(action, ["action", "beforeSequence"], []);
                    Integer(action.GetProperty("beforeSequence"), 1, long.MaxValue);
                }
                break;
            default: throw new ArgumentException("Unknown conversation action.");
        }
    }

    private static WorkflowQuestion[] ReadQuestions(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() is < 1 or > 8)
            throw new ArgumentException("Ask one to eight necessary questions in a form.");
        var result = new List<WorkflowQuestion>();
        foreach (var field in fields.EnumerateArray())
        {
            ObjectFields(field, ["id", "label"], ["required", "choices", "multiple", "description"]);
            var id = RequiredText(field, "id", 64);
            if (id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
                throw new ArgumentException("Question IDs must use ASCII letters, numbers, dot, underscore or hyphen.");
            var label = RequiredText(field, "label", 2048);
            var required = true;
            if (field.TryGetProperty("required", out var requiredValue))
            {
                if (requiredValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Question required must be a boolean.");
                required = requiredValue.GetBoolean();
            }
            string[]? choices = null;
            if (field.TryGetProperty("choices", out var choicesValue))
            {
                choices = Strings(choicesValue);
                if (choices.Length is < 1 or > 16 || choices.Distinct(StringComparer.Ordinal).Count() != choices.Length)
                    throw new ArgumentException("Question choices must contain one to sixteen distinct values.");
                foreach (var choice in choices) BoundedText(choice, 1024);
            }
            if (!ReadBooleanValue(field, "multiple", out var multiple)) throw new ArgumentException("Question multiple must be a boolean.");
            if (multiple && choices is null) throw new ArgumentException("Multiple selection requires offered choices.");
            var description = field.TryGetProperty("description", out _) ? RequiredText(field, "description", 8192) : null;
            if (result.Any(question => question.Id == id)) throw new ArgumentException("Question IDs must be unique.");
            result.Add(new(id, label, required, choices) { Multiple = multiple, Description = description });
        }
        return result.ToArray();
    }

    private static void ObjectFields(JsonElement value, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("An object is required.");
        UniqueProperties(value);
        var missing = required.Where(name => !value.TryGetProperty(name, out _)).ToArray();
        var allowed = required.Concat(optional).ToArray();
        var unexpected = value.EnumerateObject().Select(property => property.Name)
            .Where(name => !allowed.Contains(name, StringComparer.Ordinal)).ToArray();
        if (missing.Length != 0 || unexpected.Length != 0)
            throw new ArgumentException("The object has missing or unexpected fields. " +
                "Missing keys: " + KeyNames(missing) + ". Unexpected keys: " + KeyNames(unexpected) +
                ". Allowed keys: " + KeyNames(allowed) + ". Use those exact JSON keys; no action was dispatched.");

        static string KeyNames(IEnumerable<string> names)
        {
            const int maximumKeys = 8, maximumNameCharacters = 64;
            var keys = names.Order(StringComparer.Ordinal).Take(maximumKeys + 1).ToArray();
            var bounded = keys.Take(maximumKeys).Select(name =>
            {
                var length = name.Length > maximumNameCharacters ? maximumNameCharacters - 1 : name.Length;
                if (length < name.Length && length > 0 && char.IsHighSurrogate(name[length - 1])) length--;
                return name[..length] + (length < name.Length ? "…" : "");
            }).ToArray();
            return JsonSerializer.Serialize(bounded, Json) + (keys.Length > maximumKeys ? " (more keys omitted)" : "");
        }
    }
    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArgumentException("Duplicate JSON property '" + property.Name[..Math.Min(property.Name.Length, 128)] +
                        "'. Keep exactly one occurrence of that field in its object. No action was dispatched; correct the envelope and retry the intended action.");
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }
    // Details help the model repair the actual field without exposing its value. The
    // correction ledger maps this subtype to the original generic string-error identity.
    private sealed class DelegateOptionalTextTypeException(int index, string field, JsonValueKind actual)
        : ArgumentException("delegate.optional-text.type: $.assignments[" + index + "]." + field +
            " requires a JSON string; received " +
            (actual is JsonValueKind.True or JsonValueKind.False ? "boolean" : actual.ToString().ToLowerInvariant()) + ". " +
            (field == "workerContextId"
                ? "For a new worker, omit workerContextId. For reuse, send only an exact host-returned handle; never send null."
                : "Omit workerName when unused; otherwise send a bounded nonempty string."));

    private static string RequiredText(JsonElement value, string name, int maximum) =>
        value.TryGetProperty(name, out var text) ? StringValue(text, maximum) : throw new ArgumentException(name + " is required.");
    private static string StringValue(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("A string is required.");
        var text = value.GetString()!; BoundedText(text, maximum); return text;
    }
    private static void BoundedText(string text, int maximum, bool allowEmpty = false)
    {
        if (text is null || text.Length > maximum || !allowEmpty && string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("A bounded nonempty string is required.");
    }
    private static long Integer(JsonElement value, long minimum, long maximum) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= minimum && number <= maximum
            ? number : throw new ArgumentException("An integer in the accepted range is required.");
    private static string[] Strings(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
            ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : throw new ArgumentException("A string array is required.");

    private sealed record Input(string? WorkingDirectory, string? ProjectId, int? ResponseSharePercent,
        IReadOnlyList<string> RequiredSuccessfulTools, int? RequestedReasoningTokens = null,
        int? RequestedContextTokens = null, int? RequestedResponseTokens = null,
        int? MaxContextTokens = null, int? MaxResponseTokens = null, bool ForceCompaction = false)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public bool UseWorkflowProgress { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public string? MainSourceRunId { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool UseMainConversation => MainSourceRunId is not null;
        // Authenticated role only. This flag confers no ownership, tool or delegation admission.
        [System.Text.Json.Serialization.JsonIgnore]
        public bool UseCoordinatorConversation { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool UseRepeatedSuccessGuard { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool UseOwnerVerificationCompletion { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public OwnerVerificationRequirement? OwnerVerificationRequirement { get; init; }
        public IReadOnlyList<string> HostAddedRequiredSuccessfulTools { get; init; } = [];
        public string? AutomaticPluginTestPresentationId { get; init; }
        public bool AutomaticReviewHandoffEligible { get; init; }
        public IReadOnlyList<string> HostPluginTestMountIds { get; init; } = [];
    }
    private sealed record CatalogItem(string Name, string Description, string Capability, JsonElement? InputSchema = null)
    {
        public IReadOnlyList<string>? PrerequisiteTools { get; init; }
    }
    private sealed record HelperWorkflow(string DefinitionId, string DefinitionVersion, string Description);
    private sealed record HelperReasoningPolicy(bool Allowed, int MinimumTokens, int MaximumTokens, int DefaultTokens);
    private sealed record HelperTaskBudgetPolicy(int? MaxContextTokens, int? MaxResponseTokens,
        bool AllowAutonomousSelection, int? CurrentFitContextTokens);
    private sealed record HelperEntry(string Id, int Revision, string Name, string Purpose)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public HelperWorkflow? Workflow { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public HelperReasoningPolicy? ReasoningAdjustment { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? ConfiguredContextTokens { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public HelperTaskBudgetPolicy? TaskBudgetPolicy { get; init; }
    }
    private sealed record AgentAssignment(string ProfileId, int ProfileRevision, string Instruction,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        int? RequestedReasoningTokens = null)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? WorkerContextId { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? WorkerName { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? RequestedContextTokens { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? RequestedResponseTokens { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxContextTokens { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxResponseTokens { get; init; }
    }
    private sealed record PluginSourceCoordinate(int SchemaVersion, string PresentationId, string Root);
    private sealed record PluginSourceReference(int SchemaVersion, string PresentationId, string Root,
        string CatalogEffectId, string? InspectionEffectId = null);
    private sealed record CatalogData(IReadOnlyList<CatalogItem> Tools, bool Truncated,
        IReadOnlyList<HelperEntry>? Helpers = null, PluginSourceCoordinate? PluginSource = null);
    private sealed record HistoryData(ContextWindow Context, bool HasOlder, long? BeforeSequence, long SnapshotSequence)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public MainHostActions? MainHostActions { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ReasoningAdjustmentPolicy? ReasoningAdjustment { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public TaskBudgetAdmission? TaskBudget { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public PersonalityAdmissionData? Personality { get; init; }
    }
    private sealed record HistoryPage(string EventId, long Sequence, long SnapshotSequence, int Offset, int Limit, int TotalBytes,
        string Json, int? NextOffset, bool Truncated)
    {
        public long? CheckpointCoverageThroughSequence { get; init; }
    }
    private sealed record GenerationData(ModelGenerationResult? Generation, PreparedModelMetadata? Prepared = null,
        bool OutputOmitted = false, ContextProjection? Context = null);
    private sealed record CompactData(ContextCheckpoint? Checkpoint, PreparedModelMetadata? Before, PreparedModelMetadata? After);
    private sealed record ToolData(string Name, string Content, bool Truncated, ToolExecutionEvidence? Evidence, bool FollowUpRequired = false,
        IReadOnlyList<string>? RequiredSuccessfulTools = null, ToolGenerationRequest? Generation = null,
        OwnerVerificationReceipt? Verification = null)
    {
        public WorkingDirectoryOwnerAction? OwnerAction { get; init; }
        public InternetOwnerAction? InternetAction { get; init; }
    }
    private sealed record WorkingDirectoryOwnerAction(int SchemaVersion, string Kind, string SourceRunId,
        string SourceRequestId, string EffectId, string ToolName, string ArgumentsSha256,
        long PropertiesRevision, bool NoEffect);
    private sealed record OwnerVerificationReceipt(int SchemaVersion, string RecipeSha256,
        string InputSha256, string EffectId, bool ProcessDispatched, bool Complete,
        int? ExitCode, IReadOnlyList<VerificationInputPin> Inputs, string? ReportSha256 = null);
    private sealed record VerificationInputPin(string Role, string Path, string Sha256, long Bytes);
    private sealed record OwnerVerificationRequirement(string RecipeSha256, string RequestId,
        string ExecutableId, IReadOnlyList<string> ModelArguments, string WorkingDirectory);
    private sealed record OwnerCheckEvidence(int SourceGeneration, string BuildEffectId,
        string ProviderId, OwnerVerificationReceipt Receipt);
    private enum PluginInventoryReceiptKind { BroadAdmission, ExactReadOnly, Invalidates }
    private sealed record ToolReceipt(string EffectId, string Name, string State, string? Outcome, int? ExitCode, bool Truncated,
        PluginInventoryReceiptKind? InventoryKind = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        QaPresentationIdentity? Presentation = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        CanonicalReceiptProof? CanonicalProof = null)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? RunId { get; init; }
    }
    private sealed record QaPresentationIdentity(string SourceManifest, string PackageFingerprint, string RequestId);
    private sealed record ReceiptRequest(string EffectId, int Offset, long? ExpectedRevision)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? Limit { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ReceiptPageObservation? Observation { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool Failed { get; init; }
    }
    private sealed record FailedCatalogSelection(IReadOnlyList<string> Names, string EffectId, string? Diagnostic);
    private sealed record RejectedToolAttempt(string Name, JsonElement Arguments, string EffectId, string? Diagnostic = null,
        string? GeneratedContentProperty = null);
    private sealed record PluginBuildCandidate(string Root, string Component, string ProjectFile, string ExecutableId,
        IReadOnlyList<string> Arguments, string WorkingDirectory, string BuiltManifest, string Origin, string? SourceFile = null);
    private sealed record PluginWorkBinding(PluginBuildCandidate Build, int SourceGeneration = 0,
        int? SuccessfulBuildGeneration = null, string? BuildEffectId = null,
        int? VerifiedGeneration = null, string? VerifyEffectId = null,
        int? PresentedGeneration = null, string? PresentEffectId = null, string? PresentRequestId = null,
        string? VerifiedPackageFingerprint = null, string? PresentedPackageFingerprint = null,
        string? LastMutationPath = null, int? DiagnosticBuildAttemptGeneration = null,
        string? LastCanonicalBuildEffectId = null, int? LastCanonicalBuildGeneration = null,
        CanonicalBuildFailure? LastCanonicalBuildFailure = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        int CompletedBuildCorrections = 0)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ApprovedControlReference? ApprovedControlReference { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public KnownFileRevision? BuildSourceRevision { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public FailedBuildObservation? FailedBuildObservation { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public OwnerCheckEvidence? OwnerCheck { get; init; }
    }
    private sealed record CanonicalBuildFailure(string EffectId, int SourceGeneration, string ProjectKey,
        int? ExitCode, int? Errors, int? Warnings, string? Diagnostic);
    private sealed record PluginScaffoldReadiness(bool Ready, string EffectId, string Reason);
    private sealed record KnownFileRevision(string Path, string Sha256, string EffectId);
    private sealed record PendingToolGeneration(ToolGenerationRequest Request, string SourceEffectId, int Attempt = 1);
    private sealed record ChildCheckFailures(bool Failed, bool Cancelled, bool Unknown = false);
    private sealed record HarnessState
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public MainHostActions? MainHostActions { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public RequiredReadDisplayState? RequiredReadDisplays { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ReasoningAdjustmentPolicy? ReasoningAdjustment { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ReasoningGenerationReceipt? PendingReasoningGeneration { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? RequestedReasoningTokens { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public TaskBudgetAdmission? TaskBudget { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public TaskBudgetGenerationReceipt? PendingTaskBudgetGeneration { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? SelectedContextTokens { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? SelectedResponseTokens { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ToolCatalogPageState? ToolCatalogPage { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public LocalGenerationState? LocalGenerationV2 { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ChildCheckFailures? FailedChildChecks { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool UnresolvedDelegationAttempt { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool MainCoordinatorWork { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public MainCoordinatorResult? MainResult { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? PendingDelegationRunIds { get; init; }
        public string Phase { get; init; } = "start";
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ContextContributionsState? ContextContributions { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ToolContextProjectionState? ToolContextProjections { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public PendingToolContextProjection? PendingToolContextProjection { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool InitialContextCompactionAttempted { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public PersonalityState? Personality { get; init; }
        public int Repairs { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<ActionCorrection>? ActionCorrections { get; init; }
        public int SteeringCount { get; init; }
        public int Descriptions { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public FailedCatalogSelection? LastFailedCatalogSelection { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int RepeatedCatalogFailureCorrections { get; init; }
        public bool HistoryLoaded { get; init; }
        public bool CatalogLoaded { get; init; }
        public bool QaSourceCatalogChecked { get; init; }
        public bool QaSourceCatalogRefreshAttempted { get; init; }
        public bool QaSourceInventorySchemaLookupUsed { get; init; }
        public PluginSourceReference? PluginSource { get; init; }
        public long SnapshotSequence { get; init; }
        public bool HasOlder { get; init; }
        public long? BeforeSequence { get; init; }
        public IReadOnlyList<string> HistoryRequests { get; init; } = [];
        public IReadOnlyList<CatalogItem> Discovery { get; init; } = [];
        public bool CatalogTruncated { get; init; }
        public bool DiscoveryNamesOnly { get; init; }
        public IReadOnlyList<CatalogItem> Selected { get; init; } = [];
        public IReadOnlyList<HelperEntry> Helpers { get; init; } = [];
        public IReadOnlyList<string> PrimaryCatalogNames { get; init; } = [];
        public IReadOnlyList<ToolReceipt> ToolReceipts { get; init; } = [];
        public IReadOnlyDictionary<string, int> CompletionMilestones { get; init; } = new Dictionary<string, int>();
        public IReadOnlyDictionary<string, string> RetainedFileReads { get; init; } = new Dictionary<string, string>();
        public IReadOnlyList<PluginBuildCandidate> PluginBuildCandidates { get; init; } = [];
        public bool PluginBuildCandidatesTruncated { get; init; }
        public IReadOnlyList<string> PluginDiscoveryRoots { get; init; } = [];
        public PluginWorkBinding? PluginWork { get; init; }
        public PluginScaffoldReadiness? ScaffoldReadiness { get; init; }
        public IReadOnlyList<NativeControlFailure> NativeControlFailures { get; init; } = [];
        public int NativeControlFinalCorrections { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int AutomaticPresentationCorrections { get; init; }
        public IReadOnlyList<NativeControlCoverage> NativeControlCoverage { get; init; } = [];
        public IReadOnlyList<InitialQaObservation> InitialQaObservations { get; init; } = [];
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool InitialQaSchemaLookupAttempted { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public InitialFailedReviewRead? InitialFailedReviewRead { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool InitialFailedReviewSchemaLookupAttempted { get; init; }
        public NativeReviewFailure? NativeReviewFailure { get; init; }
        public IReadOnlyList<KnownFileRevision> KnownFileRevisions { get; init; } = [];
        public int RepeatedReadCorrections { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public RetainedReadRecovery? ReadRecovery { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public FailedBuildRecoveryPolicy? FailedBuildRecovery { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? AdmissionRecoveryRunId { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<AdmissionRejection>? AdmissionRejections { get; init; }
        public RejectedToolAttempt? LastRejectedTool { get; init; }
        public int RepeatedRejectionCorrections { get; init; }
        public int ConsecutiveSameRejections { get; init; }
        public JsonElement? PendingArguments { get; init; }
        public string? PendingName { get; init; }
        public PendingToolGeneration? PendingGeneration { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public PendingPluginTargetAction? PendingPluginTargetAction { get; init; }
        public JsonElement? DeferredToolArguments { get; init; }
        public string? DeferredToolName { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<AnsweredFormProvenance>? AnsweredForms { get; init; }
        public IReadOnlyList<WorkflowQuestion>? PendingQuestions { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public WorkingDirectoryOwnerAction? PendingWorkingDirectoryRequirement { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public InternetOwnerAction? PendingInternetRequirement { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? InternetToolArguments { get; init; }
        public Cerebral.Contracts.Collaboration.CollaborationQuestionBinding? PendingCollaboration { get; init; }
        public ReceiptRequest? PendingReceipt { get; init; }
        public IReadOnlyList<ReceiptRequest> ReceiptPages { get; init; } = [];
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int ReceiptReadAccountingVersion { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int ReceiptReadAttempts { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int ReceiptReadRequestedBytes { get; init; }
        public long ContextRevision { get; init; }
        public long ExclusionRevision { get; init; }
        public long LastCompactionRevision { get; init; } = -1;
        public string? CompactionReason { get; init; }
        public ContextCheckpoint? Checkpoint { get; init; }
        public IReadOnlyList<ContextContribution> Contributions { get; init; } = [];
    }
}
