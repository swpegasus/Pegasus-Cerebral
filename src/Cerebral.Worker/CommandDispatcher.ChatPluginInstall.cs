using System.Text.Json;
using Cerebral.Core;
using static Cerebral.Worker.ChatPluginInstallPolicy;

namespace Cerebral.Worker;

// Integration is deliberately separate from the currently reserved dispatcher switch.
// Every method requires the already-resolved actor; owner-only checks are also enforced here.
public sealed partial class CommandDispatcher
{
    private static void RequireChatInstallOwner(AgentProfile? actor)
    {
        RequireOwnerProfile(actor);
    }

    private ChatPluginInstallRuntime ChatPluginInstall() => new(store,
        workflows?.Store ?? throw new InvalidOperationException("Native-chat install permission is unavailable."),
        events, () => tools.FullAccessActive);

    private object ConversationPluginInstallPermissionCommand(JsonElement args, bool save, AgentProfile? actor)
    {
        RequireChatInstallOwner(actor);
        Fields(args, save ? ["id", "expectedRevision", "allowAutomaticInstall"] : ["id"]);
        var id = Text(args, "id");
        var service = ChatPluginInstall().Policies;
        if (!save) return service.Get(id, actor?.Id);
        if (!args.GetProperty("expectedRevision").TryGetInt64(out var revision) || revision < 0 ||
            args.GetProperty("allowAutomaticInstall").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("An exact permission revision and explicit boolean choice are required.");
        return service.Save(id, actor?.Id, revision, args.GetProperty("allowAutomaticInstall").GetBoolean());
    }

    private object ReadChatPluginInstallAuthorization(JsonElement args, AgentProfile? actor)
    {
        RequireChatInstallOwner(actor); Fields(args, "id", "sessionId");
        var runtime = ChatPluginInstall();
        return runtime.Policies.Inspect(Text(args, "id"), Text(args, "sessionId"), actor?.Id, runtime.ResolveCurrent);
    }

    private InterfaceClaimResult ClaimChatAuthorizedPlugin(JsonElement args, AgentProfile? actor)
    {
        RequireChatInstallOwner(actor); Fields(args, "id", "claimId", "chatInstallAuthorization");
        var value = args.GetProperty("chatInstallAuthorization");
        Fields(value, "conversationId", "permissionRevision", "decisionId");
        if (!value.GetProperty("permissionRevision").TryGetInt64(out var revision) || revision < 1)
            throw new ArgumentException("An exact positive permission revision is required.");
        var runtime = ChatPluginInstall();
        return runtime.Policies.Claim(Text(args, "id"), Text(value, "conversationId"), Text(args, "claimId"),
            revision, Text(value, "decisionId"), actor?.Id, runtime.ResolveCurrent, interfaceRequests.Claim);
    }

    private object RecheckChatPluginInstallEffect(JsonElement args, AgentProfile? actor)
    {
        RequireChatInstallOwner(actor); Fields(args, "id", "sessionId", "claimId", "permissionRevision", "decisionId");
        if (!args.GetProperty("permissionRevision").TryGetInt64(out var revision) || revision < 1)
            throw new ArgumentException("An exact positive permission revision is required.");
        var runtime = ChatPluginInstall();
        var id = Text(args, "id"); var session = Text(args, "sessionId"); var claim = Text(args, "claimId");
        runtime.Policies.BeforeEffect(id, session, claim, revision, Text(args, "decisionId"), actor?.Id, runtime.ResolveCurrent);
        return new { schemaVersion = Schema, id, sessionId = session, claimId = claim, permissionRevision = revision, authorized = true };
    }
}
