using System.Text.Json.Nodes;

namespace Cerebral.Auditing;

public sealed record DecisionOption(string Id, string Description, double? Value);
public sealed record DecisionQuestion(string Id, string Type, string Instructions, IReadOnlyList<DecisionOption> Options);

public sealed record DecisionRequest(string SchemaVersion, string RequestId, string State, IReadOnlyList<DecisionQuestion> Questions)
{
    public const string Version = "strata.decision.v1";
    private static readonly string[] Types = ["choice", "boolean", "score"];

    public static DecisionRequest Parse(JsonNode? node)
    {
        var data = AuditorJson.Object(node, "decision_request");
        AuditorJson.Fields(data, "decision_request", ["schema_version", "request_id", "state", "questions"]);
        if (AuditorJson.Text(data["schema_version"], "schema_version") != Version)
            throw new AuditorContractException("decision_request version mismatch.");
        var rawQuestions = AuditorJson.Array(data["questions"], "questions");
        if (rawQuestions.Count is < 1 or > 64) throw new AuditorContractException("questions must contain 1..64 items.");
        var questions = rawQuestions.Select(ParseQuestion).ToArray();
        if (questions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != questions.Length)
            throw new AuditorContractException("duplicate question id.");
        return new(Version, AuditorJson.Identifier(data["request_id"], "request_id"),
            AuditorJson.Text(data["state"], "state", 100_000), questions);
    }

    public JsonObject ToNode()
    {
        var questions = new JsonArray();
        foreach (var question in Questions)
        {
            var item = new JsonObject
            {
                ["id"] = question.Id, ["type"] = question.Type, ["instructions"] = question.Instructions
            };
            if (question.Type != "boolean")
            {
                var options = new JsonArray();
                foreach (var option in question.Options)
                {
                    var value = new JsonObject { ["id"] = option.Id, ["description"] = option.Description };
                    if (option.Value is not null) value["value"] = option.Value.Value;
                    options.Add(value);
                }
                item["options"] = options;
            }
            questions.Add(item);
        }
        return new() { ["schema_version"] = SchemaVersion, ["request_id"] = RequestId, ["state"] = State, ["questions"] = questions };
    }

    public JsonArray QuestionSchemaNode()
    {
        var result = new JsonArray();
        foreach (var question in Questions)
        {
            var options = new JsonArray();
            foreach (var option in question.Options)
                options.Add(new JsonObject { ["id"] = option.Id, ["description"] = option.Description, ["value"] = option.Value });
            result.Add(new JsonObject
                { ["id"] = question.Id, ["type"] = question.Type, ["instructions"] = question.Instructions, ["options"] = options });
        }
        return result;
    }

    private static DecisionQuestion ParseQuestion(JsonNode? node)
    {
        var data = AuditorJson.Object(node, "question");
        AuditorJson.Fields(data, "question", ["id", "type", "instructions"], ["options"]);
        var type = AuditorJson.Enum(data["type"], "question.type", Types);
        IReadOnlyList<DecisionOption> options;
        if (type == "boolean")
        {
            if (data.ContainsKey("options")) throw new AuditorContractException("boolean questions must omit options.");
            options = [new("true", "True", null), new("false", "False", null)];
        }
        else
        {
            var rawOptions = AuditorJson.Array(data["options"], "question.options");
            if (rawOptions.Count is < 2 or > 26) throw new AuditorContractException("question.options must contain 2..26 items.");
            options = rawOptions.Select(item => ParseOption(item, type == "score")).ToArray();
            if (options.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != options.Count)
                throw new AuditorContractException("duplicate option id.");
            if (options.Select(item => item.Description.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
                throw new AuditorContractException("duplicate option description.");
            if (type == "score")
            {
                var hasValues = options.Any(item => item.Value is not null);
                if (hasValues && options.Any(item => item.Value is null))
                    throw new AuditorContractException("score option numeric values must be supplied for all options or none.");
                if (hasValues && options.Zip(options.Skip(1)).Any(pair => pair.First.Value >= pair.Second.Value))
                    throw new AuditorContractException("score option numeric values must be strictly increasing.");
            }
        }
        return new(AuditorJson.Identifier(data["id"], "question.id"), type,
            AuditorJson.Text(data["instructions"], "question.instructions", 8_000), options);
    }

    private static DecisionOption ParseOption(JsonNode? node, bool score)
    {
        var data = AuditorJson.Object(node, "option");
        AuditorJson.Fields(data, "option", ["id", "description"], score ? ["value"] : []);
        if (!score && data.ContainsKey("value")) throw new AuditorContractException("choice options cannot contain value.");
        return new(AuditorJson.Identifier(data["id"], "option.id"),
            AuditorJson.Text(data["description"], "option.description", 8_000),
            data["value"] is null ? null : AuditorJson.Number(data["value"], "option.value"));
    }
}
