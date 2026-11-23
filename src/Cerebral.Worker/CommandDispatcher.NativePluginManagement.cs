using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private readonly NativePluginManagementRequests nativePluginManagement = new(store, events);

    private object NativePluginManagementCommand(string operation, JsonElement args)
    {
        string[] fields = operation switch
        {
            "interface.native-plugins.claim" or "interface.native-plugins.ready" => ["id", "sessionId", "claimId"],
            "interface.native-plugins.get" or "interface.native-plugins.reject" => ["id", "sessionId"],
            "interface.native-plugins.complete" => ["id", "sessionId", "claimId", "status", "result", "error"],
            _ => throw new ArgumentException("Unknown native plugin-management owner command.")
        };
        var actual = args.EnumerateObject().ToArray();
        if (actual.Length != fields.Length || actual.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            actual.Any(x => !fields.Contains(x.Name, StringComparer.Ordinal)))
            throw new ArgumentException("Use only the exact native plugin-management owner fields, once each.");
        var id = Text(args, "id"); var sessionId = Text(args, "sessionId");
        void Validate(NativePluginManagementRequest request)
        {
            var host = workflows ?? throw new InvalidOperationException("The workflow engine is unavailable; no native plugin change may be delivered.");
            var profile = store.Get<AgentProfile>("agent", request.ProfileId)
                ?? throw new UnauthorizedAccessException("The requesting profile is unavailable.");
            NativePluginManagementRequests.RequireExecuting(host,
                new(profile, request.SessionId, request.JobId, EffectId: request.Id),
                NativePluginManagementRequests.ToolFor(request.Operation));
        }
        return operation switch
        {
            "interface.native-plugins.claim" => nativePluginManagement.Claim(id, sessionId, Text(args, "claimId"), Validate),
            "interface.native-plugins.ready" => nativePluginManagement.Ready(id, sessionId, Text(args, "claimId"), Validate),
            "interface.native-plugins.get" => nativePluginManagement.GetForOwner(id, sessionId),
            "interface.native-plugins.reject" => nativePluginManagement.Reject(id, sessionId),
            _ => nativePluginManagement.Complete(id, sessionId, Text(args, "claimId"), Text(args, "status"),
                args.GetProperty("result").ValueKind == JsonValueKind.Null ? null : CerebralJson.Read<NativePluginManagementResult>(args.GetProperty("result")),
                args.GetProperty("error").ValueKind == JsonValueKind.Null ? null : Text(args, "error"))
        };
    }
}
