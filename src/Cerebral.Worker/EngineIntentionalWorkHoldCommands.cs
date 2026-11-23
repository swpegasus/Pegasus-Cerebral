using System.Text.Json;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private async Task<object> IntentionalWorkHoldCommandAsync(string operation, JsonElement args, CancellationToken token)
    {
        var keys = operation switch
        {
            "engine.work-hold.status" => Array.Empty<string>(),
            "engine.work-hold" => ["requestId", "mode", "expectedEpoch", "expectedGeneration", "expectedRevision"],
            "engine.work-hold.resume" or "engine.work-hold.cancel" => ["holdId", "expectedEpoch", "expectedGeneration", "expectedRevision"],
            _ => throw new ArgumentException("Unknown intentional work hold operation.")
        };
        if (args.ValueKind != JsonValueKind.Object ||
            !args.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)))
            throw new ArgumentException("Intentional work hold commands require exactly their declared arguments, with no duplicates.");
        return await WithOwnerEngineMutationAsync(_ => Task.FromResult<object>(Execute()), token).ConfigureAwait(false);
        object Execute()
        {
            if (operation == "engine.work-hold.status") return scheduler.GetIntentionalWorkHoldStatus();
            var epoch = args.GetProperty("expectedEpoch").GetInt64();
            var generation = args.GetProperty("expectedGeneration").GetInt64();
            var revision = args.GetProperty("expectedRevision").GetInt64();
            var result = operation == "engine.work-hold"
                ? scheduler.HoldIntentionalWork(Text(args, "requestId"), Text(args, "mode"), epoch, generation, revision)
                : scheduler.ResolveIntentionalWorkHold(Text(args, "holdId"), epoch, generation, revision,
                    operation == "engine.work-hold.cancel");
            events.Publish("engine.work-hold.updated", null, result);
            events.Publish("engine.updated", null, workflows!.Engine!.Status());
            return result;
        }
    }

    private static IReadOnlyList<WorkerWorkflows.OperationDescriptor> IntentionalWorkHoldOperations { get; } =
    [
        new("engine.work-hold.status", 1, "Read the single bounded owner intentional hold and exact captured work. Startup prompts use only this marker, never old waiting rows.",
            HoldSchema("{}", "[]"), true),
        new("engine.work-hold", 1, "Persist an owner admission barrier, pause the engine, then optionally interrupt exact captured local executions. stop-now preserves uncertain receipts; finish-current lets legacy assignments finish, not individual responses.",
            HoldSchema("""
                {"requestId":{"type":"string","pattern":"^[0-9a-fA-F]{32}$"},"mode":{"type":"string","enum":["stop-now","finish-current"]},
                 "expectedEpoch":{"type":"integer","minimum":0},"expectedGeneration":{"type":"integer","minimum":0},"expectedRevision":{"type":"integer","minimum":0}}
                """, """["requestId","mode","expectedEpoch","expectedGeneration","expectedRevision"]"""), true),
        new("engine.work-hold.resume", 1, "Release an exact held owner marker after executions settle. Unknown effects and interrupted legacy work require review and are never replayed.",
            ResolutionSchema(), true),
        new("engine.work-hold.cancel", 1, "Explicitly cancel only captured held work; uncertain receipts remain. The engine stays paused after the hold is cleared.",
            ResolutionSchema(), true)
    ];

    private static JsonElement ResolutionSchema() => HoldSchema("""
        {"holdId":{"type":"string","pattern":"^[0-9a-fA-F]{32}$"},"expectedEpoch":{"type":"integer","minimum":0},
         "expectedGeneration":{"type":"integer","minimum":1},"expectedRevision":{"type":"integer","minimum":1}}
        """, """["holdId","expectedEpoch","expectedGeneration","expectedRevision"]""");
    private static JsonElement HoldSchema(string properties, string required)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":" + properties +
            ",\"required\":" + required + ",\"additionalProperties\":false}");
        return document.RootElement.Clone();
    }
}
