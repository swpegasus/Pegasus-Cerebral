using System.Text.Json;
using Cerebral.Conversation;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

public static partial class ConversationHarnessTests
{
    private static int RunRepeatPreparedChecks()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Conversation repeat/prepared: " + name);
            checks++;
        }

        var harness = ConversationAgentHarness.CreateRepeatPreparedEdition();
        var fixture = new Fixture(selectedHarness: harness);
        var schema = CerebralJson.Element(new
        {
            type = "object", properties = new { path = new { type = "string" } },
            required = new[] { "path" }, additionalProperties = false
        });
        fixture.Select("file.read", schema);
        fixture.Model(new { action = "tool", name = "file.read", arguments = new { path = "C:\\fixture\\source.txt" } });
        Check(fixture.Is("tool"), "new edition reaches a normal admitted tool intent");
        var prepared = fixture.PendingPreparedRun();
        var effect = prepared.Effect!;
        Check(harness.CanRejectPrepared(new(prepared)),
            "exact inactive never-dispatched prepared tool is eligible for owner rejection");
        Check(!ConversationAgentHarness.CreateRepeatedSuccessGuardEdition().CanRejectPrepared(new(prepared)) &&
            !harness.CanRejectPrepared(new(prepared with
            {
                Submission = prepared.Submission with
                    { DefinitionVersion = ConversationAgentHarness.RepeatedSuccessGuardVersion }
            })),
            "accepted old repeat edition remains unsupported even with the same pending shape");
        foreach (var invalid in new[]
        {
            prepared with { Status = WorkflowStatus.Running },
            prepared with { CancelRequested = true },
            prepared with { Effect = effect with { State = WorkflowEffectState.Dispatched } },
            prepared with { Effect = effect with { State = WorkflowEffectState.Unknown } },
            prepared with { Effect = effect with { ProviderReceiptId = "provider-receipt" } },
            prepared with { Effect = effect with { ReservedExecutionMilliseconds = 1 } },
            prepared with { Effect = effect with { FailureBudgetKey = "cerebral.host:launch" } }
        })
            Check(!harness.CanRejectPrepared(new(invalid)),
                "active, cancelled, dispatched, uncertain or provider-owned effects refuse prepared rejection");

        const string requestId = "owner-correction-1", reason = "Target is a library; do not run it as an app.";
        var originalHash = WorkflowPreparedEffectIdentity.Sha256(effect);
        var rejected = prepared with
        {
            Status = WorkflowStatus.Running, Revision = 3,
            Effect = effect with { State = WorkflowEffectState.RejectedBeforeDispatch, Error = reason },
            Signal = new(WorkflowSignalKind.PreparedRejected,
                CerebralJson.Element(new WorkflowPreparedRejectionSignal(effect.Id, reason, requestId))),
            Steering = [new WorkflowSteeringEntry(requestId, 1,
                "Build the library and use a compatible host for visual checks.", null, DateTimeOffset.UtcNow)],
            PreparedInterventions = [new WorkflowPreparedInterventionRecord(requestId,
                WorkflowPreparedInterventionKind.Reject, new string('a', 64), effect.Id, originalHash, 2)]
        };
        var accepted = harness.Advance(new(rejected));
        Check(accepted.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:generate" } &&
            accepted.State.GetProperty("pendingName").ValueKind == JsonValueKind.Null &&
            accepted.State.GetProperty("pendingArguments").ValueKind == JsonValueKind.Null &&
            accepted.State.GetProperty("toolReceipts").GetArrayLength() ==
                prepared.State.GetProperty("toolReceipts").GetArrayLength(),
            "matching owner rejection clears only undispatched intent and requests a new decision");
        var forged = harness.Advance(new(rejected with
        {
            PreparedInterventions = [rejected.PreparedInterventions![0] with { EffectSha256 = new string('0', 64) }]
        }));
        Check(forged.Next is CompleteWorkflow { Success: false },
            "foreign rejection audit cannot reopen the pending tool");
        return checks;
    }
}
