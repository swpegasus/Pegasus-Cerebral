using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Contracts;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>
/// Synthetic public-event projections and unshown WPF controls only. Run on an STA thread
/// without any provider, transport, database or visible window.
/// This file is included in the review project but is not wired to any automatic startup.
/// </summary>
internal static class ActivityAttributionChecks
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); count++; }
        JsonElement Json(object? value) => CerebralJson.Element(value);
        ActivityTimelineContext Context(string? label = "Shared label", string member = "worker-a",
            string conversation = "worker-chat-a", string run = "run-a", ActivityTimelineRole role = ActivityTimelineRole.Worker) =>
            new("owner-a", role)
            {
                OwningMainConversationId = "main-a", MemberCoordinate = member, ClassifyOperations = true,
                ContextAttribution = new("owner-a", "main-a", member, conversation, run, role) { DisplayLabel = label }
            };
        CerebralEvent Call(string id, string tool, object? arguments = null, string conversation = "worker-chat-a",
            string run = "run-a", long sequence = 1) =>
            new(id, conversation, run, sequence, CerebralEventTypes.ToolCall,
                new(EventActorKind.Agent, "agent-a", "Recorded worker"), [],
                new(ModelInclusion.Required, HumanVisibility.Activity), new(EventSourceKind.Agent, AgentId: "agent-a"),
                DateTimeOffset.UnixEpoch.AddSeconds(sequence), Json(new ToolCallData(id, tool, Json(arguments ?? new { }))));
        CerebralEvent Receipt(string callId, string tool, long sequence = 2) =>
            new("receipt-" + callId, "worker-chat-a", "run-a", sequence, CerebralEventTypes.ToolResult,
                new(EventActorKind.Tool, tool, "Tool receipt label"), [],
                new(ModelInclusion.Evidence, HumanVisibility.Activity), new(EventSourceKind.Tool, AgentId: "agent-a"),
                DateTimeOffset.UnixEpoch.AddSeconds(sequence), Json(new ToolResultData(callId, tool, true)));
        ActivityTimelineItem Project(CerebralEvent item, ActivityTimelineContext? context = null) =>
            WorkflowWorkspace.ProjectActivityTimelineEvents([item], context ?? Context()).Items.Single();

        var call = Call("read-a", "cerebral.host:tool", new { name = "file.read", arguments = new { path = @"C:\Fixture\a.cs" } });
        var oldContext = new ActivityTimelineContext("owner-a", ActivityTimelineRole.Worker)
        { OwningMainConversationId = "main-a", MemberCoordinate = "worker-a" };
        var old = Project(call, oldContext);
        var current = Project(call);
        Check(old.AgentDisplay == old.Agent && old.OperationDisplay == old.OperationType && old.OperationKind is null &&
            old.ContextAttribution is null, "Existing constructor/default callers retain original display behavior.");
        Check(current.Agent == old.Agent && current.Caption == old.Caption && current.RequestedAction == old.RequestedAction &&
            current.Outcome == old.Outcome && current.Evidence == old.Evidence && current.RowText == old.RowText,
            "Context/category additions do not rewrite recorded actor, detail, copy text or source evidence.");
        Check(current.AgentDisplay == "Worker · Shared label (context)" &&
            current.AgentDisplayTooltip.Contains("Recorded actor: Recorded worker", StringComparison.Ordinal),
            "A contextual label is explicit and its recorded actor remains separate.");
        Check(current.Key == old.Key && current.EventIds.SequenceEqual(old.EventIds) &&
            current.MemberCoordinate == old.MemberCoordinate, "Added labels/category do not change row/filter identity.");

        foreach (var tool in new[] { "cerebral.host:generate", "cerebral.host:history", "cerebral.host:catalog",
                     "cerebral.host:receipt", "cerebral.host:compact" })
            Check(Project(Call(tool, tool)).OperationKind == ActivityOperationKind.Internal,
                "Exact fixed host model/context commands are internal.");
        foreach (var (tool, label) in new[] { ("cerebral.host:coordinate-owned", "Coordinate work"),
                     ("cerebral.host:delegate", "Delegate workers") })
        {
            var handoffCall = Call("handoff", tool);
            var handoff = Project(handoffCall);
            var original = Project(handoffCall, oldContext);
            Check(handoff.OperationKind == ActivityOperationKind.Handoff && handoff.OperationDisplay == "Handoff · " + label,
                "Both exact registered host handoffs have clear opt-in display labels.");
            Check(handoff.State == "Awaiting receipt" && handoff.State == original.State,
                "A handoff category/label does not establish acceptance or completed execution.");
            Check(original.OperationKind is null && original.OperationDisplay == original.OperationType &&
                handoff.Agent == original.Agent && handoff.Caption == original.Caption &&
                handoff.RequestedAction == original.RequestedAction && handoff.Outcome == original.Outcome &&
                handoff.Evidence == original.Evidence && handoff.RowText == original.RowText &&
                handoff.Key == original.Key && handoff.EventIds.SequenceEqual(original.EventIds),
                "Opt-in handoff display leaves default callers, recorded detail, copy text and identity unchanged.");
        }
        foreach (var tool in new[] { "file.read", "file.list" })
        {
            Check(Project(Call("direct-" + tool, tool)).OperationKind == ActivityOperationKind.ExecutionTool,
                "The exact verified registered Cerebral file contract is an execution tool.");
            Check(Project(Call("wrapped-" + tool, "cerebral.host:tool", new { name = tool, arguments = new { } }))
                .OperationKind == ActivityOperationKind.ExecutionTool, "A valid wrapper unwraps one exact registered contract.");
        }
        foreach (var tool in new[] { "project.file.read", "file.read.extra", "mcp.project.pao", "mcp__project__pao",
                     "cerebral.host:generate-extra", "cerebral.host:tool-extra", "collaboration.unknown",
                     "cerebral.host:delegate-extra", "delegate", "cerebral.host:delegation",
                     "cerebral.host:coordinate-owned-extra" })
            Check(Project(Call(tool, tool)).OperationKind == ActivityOperationKind.Unclassified,
                "Arbitrary prefixes and aliases never establish an execution/internal contract.");
        foreach (var args in new object[] { new { name = "file.read" },
                     new { name = "file.read", arguments = "malformed" },
                     new { name = "project.file.read", arguments = new { } },
                     new { name = "cerebral.host:generate", arguments = new { } },
                     new { name = "cerebral.host:coordinate-owned", arguments = new { } },
                     new { name = "cerebral.host:delegate", arguments = new { } } })
            Check(Project(Call("unknown-wrapper", "cerebral.host:tool", args)).OperationKind == ActivityOperationKind.Unclassified,
                "Malformed/unknown wrappers cannot borrow classification from host or tool-looking names.");

        foreach (var role in new[] { ActivityTimelineRole.Main, ActivityTimelineRole.Coordinator, ActivityTimelineRole.Worker })
        {
            var sourceConversation = role == ActivityTimelineRole.Main ? "main-a" : "worker-chat-a";
            var coordinate = role == ActivityTimelineRole.Main ? "main-a" : "worker-a";
            var absent = Project(Call("fallback", "file.read", conversation: sourceConversation),
                Context(label: null, member: coordinate, conversation: sourceConversation, role: role));
            Check(absent.AgentDisplay == role + " (context)" && absent.ContextAttribution?.DisplayLabel is null &&
                absent.Agent == "Recorded worker", "Verified role fallback does not fabricate a recorded name.");
        }
        Check(Project(call, Context(label: "   ")).AgentDisplay == "Worker (context)",
            "A missing contextual display label retains the verified role.");
        var binding = Context().ContextAttribution!;
        foreach (var invalid in new[]
        {
            binding with { OwnerIdentity = "other-owner" }, binding with { OwningMainConversationId = "other-main" },
            binding with { MemberCoordinate = "other-member" }, binding with { ConversationId = "other-chat" },
            binding with { RunId = "other-run" }, binding with { Role = ActivityTimelineRole.Coordinator },
            binding with { DisplayLabel = "Bad\nlabel" }, binding with { DisplayLabel = new string('x', 513) }
        })
        {
            var rejected = Project(call, Context() with { ContextAttribution = invalid });
            Check(rejected.ContextAttribution is null && rejected.AgentDisplay == rejected.Agent && rejected.Key == current.Key,
                "Foreign owner/main/member/chat/run/role or malformed label cannot establish context attribution.");
        }
        Check((current with { RunId = "different-run" }).AgentDisplay == current.Agent,
            "A copied item cannot reuse context metadata after its source run changes.");
        var unpaired = Project(Receipt("orphan", "cerebral.host:tool"));
        Check(unpaired.State == "Unpaired receipt" && unpaired.Agent == "Agent not recorded" &&
            unpaired.AgentDisplayTooltip.Contains("Recorded actor: Agent not recorded", StringComparison.Ordinal),
            "An unpaired Tool actor remains unpaired and cannot become a named caller through context metadata.");
        var paired = WorkflowWorkspace.ProjectActivityTimelineEvents([call, Receipt("read-a", "cerebral.host:tool")], Context()).Items.Single();
        Check(paired.Agent == old.Agent && paired.EventIds.Count == 2 && paired.Key == old.Key,
            "Exact receipt pairing preserves the recorded call actor and row identity.");

        var second = Project(Call("read-b", "file.list", conversation: "worker-chat-b", run: "run-b"),
            Context(member: "worker-b", conversation: "worker-chat-b", run: "run-b"));
        var view = new ActivityTimelineView();
        ActivityTimelineSnapshot Snapshot(ActivityTimelineItem first) => new([first, second], false)
        {
            Members = [new("main-a", "worker-a", "Shared label"), new("main-a", "worker-b", "Shared label")]
        };
        view.Update(Snapshot(current), "main-a", null);
        var filter = Find<ComboBox>(view, "ActivityAgentFilter");
        object Choice(string coordinate) => filter.Items.Cast<object>().Single(item =>
            item.GetType().GetProperty("Coordinate")!.GetValue(item) as string == coordinate);
        Check(filter.Items.Count == 3, "Duplicate contextual names retain two coordinate-keyed choices plus All.");
        filter.SelectedItem = Choice("worker-b");
        Check(view.SelectedMemberCoordinate == "worker-b" && Find<ListView>(view, "ActivityTimelineRows").Items.Count == 1,
            "Agent selection is keyed by stable member coordinate, not display label.");
        view.Update(Snapshot(current with { ContextAttribution = binding with { DisplayLabel = "Changed label" } }), "main-a", null);
        Check(view.SelectedMemberCoordinate == "worker-b", "Renaming a label preserves selected filter identity.");
        view.FocusTurn(new("other-owner", "worker-chat-a", "run-a"));
        Check(Find<ListView>(view, "ActivityTimelineRows").Items.Count == 0, "Turn focus cannot cross the owner.");
        view.FocusTurn(new("owner-a", "worker-chat-a", "other-run"));
        Check(Find<ListView>(view, "ActivityTimelineRows").Items.Count == 0, "Turn focus cannot cross the run.");
        view.FocusTurn(new("owner-a", "worker-chat-a", "run-a"));
        Check(Find<ListView>(view, "ActivityTimelineRows").Items.Count == 1, "Exact owner/chat/run focus remains usable.");

        var detail = view.CreateDetailForInspection(current);
        try
        {
            var captured = (ActivityTimelineItem)detail.Tag;
            var evidence = Find<TextBox>(detail, "ActivityEvidence").Text;
            view.Update(Snapshot(current with { Outcome = "Later outcome", ContextAttribution = binding with { DisplayLabel = "Later label" } }), "main-a", null);
            Check(captured.Agent == current.Agent && captured.ContextAttribution?.DisplayLabel == "Shared label" &&
                ReferenceEquals(detail.Tag, captured), "Detached detail retains the immutable actor and original context snapshot.");
            Check(Find<TextBox>(detail, "ActivityRequested").Text == current.RequestedAction &&
                Find<TextBox>(detail, "ActivityOutcome").Text == current.Outcome &&
                Find<TextBox>(detail, "ActivityEvidence").Text == evidence, "Updates do not rewrite existing immutable detail fields.");
            Check(Find<TextBox>(detail, "ActivityOwnedContext").Text == current.AgentDisplayTooltip,
                "Context provenance is displayed separately from the original recorded detail.");
        }
        finally { detail.Close(); }
        var columns = ((GridView)Find<ListView>(view, "ActivityTimelineRows").View).Columns;
        Check((string)columns[^2].Header == "File name" && (string)columns[^1].Header == "File path",
            "The two file columns remain final and ordered.");
        return count;
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants(child)) yield return item;
    }
}
