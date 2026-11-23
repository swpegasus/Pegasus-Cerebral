using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Deterministic journal projection only; no workspace construction, network or provider.</summary>
public static class ChatActivityCountChecks
{
    public static int Verify()
    {
        var count=0; void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); count++; }
        var at=DateTimeOffset.UnixEpoch;
        CerebralEvent Call(string id,string turn,long sequence) => new(id,"chat",turn,sequence,CerebralEventTypes.ToolCall,
            new(EventActorKind.Agent,"worker"),[],new(ModelInclusion.Never,HumanVisibility.Activity),new(EventSourceKind.Agent),at,
            CerebralJson.Element(new ToolCallData("same-call","fixture.read",WorkflowContract.EmptyObject)));
        CerebralEvent Receipt(string id,string turn,long sequence,string state,bool success) => new(id,"chat",turn,sequence,CerebralEventTypes.ToolResult,
            new(EventActorKind.Tool,"fixture.read"),[new StructuredContentPart("workflow.command.result",CerebralJson.Element(new {state}))],
            new(ModelInclusion.Never,HumanVisibility.Activity),new(EventSourceKind.Tool),at,
            CerebralJson.Element(new ToolResultData("same-call","fixture.read",success)));
        var call=Call("call","run",1);
        var unknown=Receipt("unknown","run",2,"Unknown",false);
        var completed=Receipt("completed","run",3,"Succeeded",true);
        Check(WorkflowWorkspace.RecordedActivityCallCount([call,call])==1,"Repeated observation of the same call cannot inflate calls shown.");
        Check(WorkflowWorkspace.RecordedActivityCallCount([call,Call("other","other-run",4)])==2,"Call IDs are scoped to exact conversation and run.");
        var outcomes=WorkflowWorkspace.RecordedActivityOutcomes([unknown,completed,completed]);
        Check(outcomes is {Completed:1,Failed:0,Cancelled:0,Unconfirmed:0},"Latest exact receipt supersedes Unknown and duplicate observation.");
        var cancelled=WorkflowWorkspace.RecordedActivityOutcomes([Receipt("cancelled","run",5,"Cancelled",false)]);
        Check(cancelled is {Completed:0,Failed:0,Cancelled:1},"Cancellation is distinct from confirmed failure.");
        var foreign=WorkflowWorkspace.RecordedActivityOutcomes([completed,Receipt("other-result","other-run",6,"Failed",false)]);
        Check(foreign is {Completed:1,Failed:1},"Identical call IDs in different runs must never be folded together.");
        Check(!WorkflowWorkspace.RecordedActivityOutcomes([call]).Observed,"Missing receipt is unknown, not a fabricated completion.");
        var run=new WorkflowConversationRun("run","chat","worker","fixture","fixture","1",1,WorkflowStatus.Running,null,null,null,null,false,null,null,1);
        var header=WorkflowWorkspace.ActivityHeader([call,completed],run,new Dictionary<string,string>());
        Check(header.Contains("1 call shown",StringComparison.Ordinal)&&header.Contains("1 completed receipts shown",StringComparison.Ordinal)&&
            header.Contains("Whole-run totals unavailable",StringComparison.Ordinal),"A bounded window is explicit about shown observations and absent whole-run totals.");
        var orphan=WorkflowWorkspace.ActivityHeader([completed],run,new Dictionary<string,string>());
        Check(orphan.Contains("receipts shown",StringComparison.Ordinal)&&orphan.Contains("Whole-run totals unavailable",StringComparison.Ordinal),
            "A receipt whose call fell outside the window must not masquerade as a whole-run call count.");
        var totals=new WorkflowActivityTotals("chat","run",1,3,5,3,1,0,1,true);
        var withTotals=run with { ActivityTotals=totals };
        var aggregateHeader=WorkflowWorkspace.ActivityHeader([call,completed],withTotals,new Dictionary<string,string>());
        var refreshedHeader=WorkflowWorkspace.ActivityHeader([completed],withTotals,new Dictionary<string,string>());
        Check(aggregateHeader.Contains("5 calls total",StringComparison.Ordinal)&&refreshedHeader.Contains("5 calls total",StringComparison.Ordinal)&&
            !aggregateHeader.Contains("Whole-run totals unavailable",StringComparison.Ordinal),
            "An exact authoritative total stays the same when Refresh shows a smaller event window.");
        foreach(var invalid in new[] {totals with {ConversationId="foreign"},totals with {RunId="foreign"},
            totals with {RunRevision=2},totals with {AsOfSequence=2},totals with {CoverageComplete=false,DiagnosticCode="invalid-receipt-metadata"}})
        {
            var invalidHeader=WorkflowWorkspace.ActivityHeader([call,completed],run with {ActivityTotals=invalid},new Dictionary<string,string>());
            Check(invalidHeader.Contains("Run totals updating / unavailable",StringComparison.Ordinal)&&!invalidHeader.Contains("5 calls total",StringComparison.Ordinal)&&!invalidHeader.Contains("call shown",StringComparison.Ordinal),
                "Foreign, stale, partial or invalid totals must fall back to explicit shown observations.");
        }
        var blockedHeader=WorkflowWorkspace.ActivityHeader([call,completed],run with {ActivityTotals=totals with {Calls=7,Blocked=2}},new Dictionary<string,string>());
        Check(blockedHeader.Contains("2 blocked",StringComparison.Ordinal)&&blockedHeader.Contains("1 failed",StringComparison.Ordinal)&&blockedHeader.Contains("7 calls total",StringComparison.Ordinal),
            "Known owner rejection is shown as blocked and never added to execution failure.");
        var earlierIteration=WorkflowWorkspace.ActivityHeader([call,completed],withTotals,new Dictionary<string,string>(),currentIteration:false);
        Check(earlierIteration.Contains("1 call shown",StringComparison.Ordinal)&&!earlierIteration.Contains("5 calls total",StringComparison.Ordinal),
            "An earlier iteration of the same run cannot inherit the current iteration's aggregate.");
        Check(WorkflowWorkspace.MatchingActivityTotals([call,Receipt("foreign","other-run",3,"Succeeded",true)],withTotals) is null,
            "A mixed-turn block cannot borrow a current run aggregate.");
        Check(WorkflowWorkspace.MatchingActivityTotals([completed with {ConversationId="foreign"}],withTotals) is null,
            "A foreign-conversation block cannot borrow a current run aggregate.");
        return count;
    }
}
