using System.Text.Json;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private object ConversationPathsCommand(JsonElement arguments, bool save)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in arguments.EnumerateObject())
            if (!names.Add(field.Name) || (save ? field.Name is not ("id" or "expectedRevision" or "grants" or "initial") : field.Name != "id"))
                throw new ArgumentException("Chat path rules contain duplicate or unsupported arguments.");
        var service = new ConversationPathGrants(store,
            workflows ?? throw new InvalidOperationException("Native chat path rules are unavailable on this Worker."), events);
        var id = Text(arguments, "id");
        if (!save) return service.Get(id);
        if (!arguments.TryGetProperty("expectedRevision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
            !revision.TryGetInt64(out var expectedRevision) || !arguments.TryGetProperty("grants", out var grants) ||
            grants.ValueKind != JsonValueKind.Array || grants.GetArrayLength() > ConversationPathGrants.MaximumExternalGrants)
            throw new ArgumentException("An exact path revision and at most 32 explicit path rules are required.");
        var parsed = new List<ConversationPathGrants.Grant>();
        foreach (var grant in grants.EnumerateArray())
        {
            if (grant.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each path rule must be an object.");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in grant.EnumerateObject())
                if (!fields.Add(field.Name) || field.Name is not ("path" or "access"))
                    throw new ArgumentException("Path rules contain duplicate or unsupported fields.");
            var access = Text(grant, "access");
            if (access is not ("read" or "write")) throw new ArgumentException("Path access must be exactly read or write.");
            parsed.Add(new(Text(grant, "path"), access));
        }
        var initial = arguments.TryGetProperty("initial", out var preparation) ? ReadInitialChatPreparation(preparation) : null;
        return service.Save(id, expectedRevision, parsed, initial);
    }
    private static ConversationPathGrants.Initial ReadInitialChatPreparation(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Initial chat authority requires one complete object.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!fields.Add(field.Name) || field.Name is not ("firstRequestId" or "profileId" or "expectedProfileRevision" or "projectId" or "workingDirectory"))
                throw new ArgumentException("Initial chat authority contains duplicate or unsupported fields.");
        if (fields.Count != 5 || !value.TryGetProperty("expectedProfileRevision", out var revision) ||
            revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt32(out var expectedRevision) || expectedRevision < 1 ||
            !value.TryGetProperty("projectId", out var project) || project.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            !value.TryGetProperty("workingDirectory", out var root) || root.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new ArgumentException("All five initial chat fields and an exact positive profile revision are required.");
        return new(Text(value, "firstRequestId"), Text(value, "profileId"), expectedRevision,
            project.ValueKind == JsonValueKind.Null ? null : project.GetString(),
            root.ValueKind == JsonValueKind.Null ? null : root.GetString());
    }
}
