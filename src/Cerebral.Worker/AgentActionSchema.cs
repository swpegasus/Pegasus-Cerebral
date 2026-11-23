using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public static class AgentActionSchema
{
    public static JsonElement Create(IReadOnlyList<ToolDescriptor> tools, IReadOnlyList<string> helpers,
        IReadOnlyList<string>? presentationKinds = null, IReadOnlyCollection<string>? unresolvedFailureIds = null,
        IReadOnlyCollection<string>? missingRequiredToolNames = null, bool allowAsk = true,
        LongFormPlanRequest? longFormPlan = null, bool allowFinal = true, bool allowCompleted = true,
        int requiredQuestionCount = 0)
    {
        static object Constant(string value) => new { type = "string", @const = value };
        static object BooleanConstant(bool value) => new { type = "boolean", @const = value };
        static object Text() => new { type = "string", minLength = 1 };
        static object BoundedText(int maximum) => new { type = "string", minLength = 1, maxLength = maximum };
        static object Shape(params (string Name, object Schema)[] fields) => new
        {
            type = "object", properties = fields.ToDictionary(x => x.Name, x => x.Schema),
            required = fields.Select(x => x.Name).ToArray(), additionalProperties = false
        };

        if (requiredQuestionCount is not (0 or 2 or 3))
            throw new ArgumentOutOfRangeException(nameof(requiredQuestionCount), "An explicit question batch must contain two or three questions.");
        var failures = (unresolvedFailureIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var missingRequired = (missingRequiredToolNames ?? []).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var advertisedNames = tools.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var unavailableRequired = missingRequired.Where(x => !advertisedNames.Contains(x)).ToArray();
        if (unavailableRequired.Length > 0)
            throw new InvalidOperationException("Required tool evidence cannot be requested because these tools are not advertised: " + string.Join(", ", unavailableRequired));
        object resolutions;
        if (failures.Length == 0)
        {
            resolutions = new { type = "array", maxItems = 0 };
        }
        else
        {
            var resolution = Shape(
                ("failureId", new { type = "string", @enum = failures }),
                ("evidenceId", Text()),
                ("reason", new { type = "string", minLength = 1, maxLength = 1000 }));
            resolutions = new { type = "array", items = resolution, maxItems = Math.Min(16, failures.Length) };
        }

        var alternatives = new List<object>();
        if (missingRequired.Length == 0 && longFormPlan is not null)
        {
            object path = longFormPlan.Path is null ? BoundedText(32768) : Constant(longFormPlan.Path);
            var section = Shape(("title", BoundedText(120)), ("purpose", BoundedText(500)));
            alternatives.Add(Shape(("action", Constant("longform.plan")), ("path", path),
                ("sections", new { type = "array", items = section, minItems = longFormPlan.MinimumSections, maxItems = longFormPlan.MaximumSections })));
            return CerebralJson.Element(new { oneOf = alternatives });
        }
        if (missingRequired.Length == 0)
        {
            if (allowFinal)
                alternatives.Add(Shape(("action", Constant("final")), ("outcome", new { type = "string", @enum = allowCompleted ? new[] { "completed", "incomplete", "failed" } : new[] { "incomplete", "failed" } }), ("text", Text()),
                    ("resolutions", resolutions)));
            if (allowAsk)
            {
                var question = Shape(("question", Text()), ("reason", new { type = "string" }),
                    ("required", new { type = "boolean" }), ("choices", new { type = "array", items = new { type = "string" }, maxItems = 5 }),
                    ("canAssume", new { type = "boolean" }), ("assumption", new { type = "string", maxLength = AgentPrompt.MaxQuestionAssumptionCharacters }));
                if (requiredQuestionCount == 0)
                    alternatives.Add(Shape(("action", Constant("ask")), ("question", Text()), ("reason", new { type = "string" }),
                        ("required", new { type = "boolean" }), ("choices", new { type = "array", items = new { type = "string" }, maxItems = 5 }),
                        ("canAssume", new { type = "boolean" }), ("assumption", new { type = "string", maxLength = AgentPrompt.MaxQuestionAssumptionCharacters })));
                alternatives.Add(Shape(("action", Constant("ask.batch")),
                    ("questions", new { type = "array", items = question,
                        minItems = requiredQuestionCount == 0 ? 2 : requiredQuestionCount,
                        maxItems = requiredQuestionCount == 0 ? AgentPrompt.MaxPendingOptionalQuestions : requiredQuestionCount })));
            }
        }
        if (missingRequired.Length == 0 && presentationKinds is { Count: > 0 })
            alternatives.Add(Shape(("action", Constant("present")), ("kind", new { type = "string", @enum = presentationKinds }), ("path", new { type = "string", minLength = 1, maxLength = 32768 })));
        var missingSet = missingRequired.ToHashSet(StringComparer.Ordinal);
        var selectableTools = missingRequired.Length == 0 ? tools : tools.Where(x => missingSet.Contains(x.Name)).ToArray();
        foreach (var tool in selectableTools)
            alternatives.Add(Shape(("action", Constant("tool")), ("name", Constant(tool.Name)), ("arguments", tool.InputSchema)));
        if (missingRequired.Length > 0)
        {
            if (requiredQuestionCount == 0)
                alternatives.Add(Shape(("action", Constant("ask")), ("question", Text()), ("reason", new { type = "string" }),
                    ("required", BooleanConstant(true)), ("choices", new { type = "array", items = new { type = "string" }, maxItems = 5 }),
                    ("canAssume", BooleanConstant(false)), ("assumption", new { type = "string", maxLength = AgentPrompt.MaxQuestionAssumptionCharacters })));
            else
            {
                var requiredQuestion = Shape(("question", Text()), ("reason", new { type = "string" }),
                    ("required", BooleanConstant(true)), ("choices", new { type = "array", items = new { type = "string" }, maxItems = 5 }),
                    ("canAssume", BooleanConstant(false)), ("assumption", new { type = "string", maxLength = AgentPrompt.MaxQuestionAssumptionCharacters }));
                alternatives.Add(Shape(("action", Constant("ask.batch")),
                    ("questions", new { type = "array", items = requiredQuestion,
                        minItems = requiredQuestionCount, maxItems = requiredQuestionCount })));
            }
        }
        if (missingRequired.Length == 0 && helpers.Count > 0)
        {
            var profile = new { type = "string", @enum = helpers };
            var assignment = Shape(("profileId", profile), ("prompt", Text()));
            alternatives.Add(Shape(("action", Constant("delegate")), ("profileId", profile), ("prompt", Text())));
            alternatives.Add(Shape(("action", Constant("delegate")), ("assignments", new { type = "array", items = assignment, minItems = 1, maxItems = 8 })));
        }
        // Constrain wire shape only; saved grants and tool validation still decide what may execute.
        return CerebralJson.Element(new { oneOf = alternatives });
    }
}
