using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private object ConversationPropertiesCommand(JsonElement arguments, bool save)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in arguments.EnumerateObject())
            if (!names.Add(field.Name) || (save ? field.Name is not ("id" or "expectedRevision" or "responseSharePercent" or "workingDirectory") : field.Name != "id"))
                throw new ArgumentException("Chat Properties contain duplicate or unsupported arguments.");
        var service = new ConversationProperties(store,
            workflows ?? throw new InvalidOperationException("Native chat Properties are unavailable on this Worker."),
            events, executionTools);
        var id = Text(arguments, "id");
        if (!save) return service.Get(id);
        if (!arguments.TryGetProperty("expectedRevision", out var revision) || !revision.TryGetInt64(out var expectedRevision) ||
            !arguments.TryGetProperty("responseSharePercent", out var response))
            throw new ArgumentException("An exact settings revision and explicit response share (or null) are required.");
        int? share = null;
        if (response.ValueKind != JsonValueKind.Null)
        {
            if (!response.TryGetInt32(out var number))
                throw new ArgumentException("Response share must be an integer percentage or null.");
            share = number;
        }
        var hasDirectory = arguments.TryGetProperty("workingDirectory", out var directory);
        if (hasDirectory && directory.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new ArgumentException("Working directory must be an absolute path or null.");
        return service.Save(id, expectedRevision, share, hasDirectory,
            hasDirectory && directory.ValueKind == JsonValueKind.String ? directory.GetString() : null);
    }
}
