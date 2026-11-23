using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class CommandDispatcher
{
    private static DurableInferenceReadReply ReadDurableInferenceEvents(WorkerWorkflows host,
        JsonElement arguments, AgentProfile? actor, CancellationToken cancellationToken) =>
        DurableInferenceOwnerRead.Read(arguments, actor is null, id => host.GetOwned(id, actor),
            (conversation, after, limit, snapshot, token) =>
                host.Journal.ReadPage(conversation, after, limit, snapshot, token), cancellationToken);
}
