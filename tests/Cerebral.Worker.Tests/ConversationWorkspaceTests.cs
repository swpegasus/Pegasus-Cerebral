using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class ConversationWorkspaceTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-conversation-workspaces-" + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL: " + label);
            checks++; Console.WriteLine("PASS: " + label);
        }
        static bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or ArgumentOutOfRangeException) { return true; }
        }

        try
        {
            var store = new StateStore(root); var events = new EventHub();
            var session = new TaskSession { Id = "conversation", ProfileId = "lead", Title = "Workspace binding" };
            store.Put("session", session.Id, session);
            var workspaces = new ConversationWorkspaces(store, null, events);
            Check(workspaces.Get(session.Id) is null, "a saved conversation starts on the normal conversation surface");
            var first = workspaces.Set(session.Id, "fixture.plugin", "planning", "resource-1", 0);
            Check(first.Revision == 1 && first.ConversationId == session.Id && first.PluginId == "fixture.plugin" &&
                workspaces.Get(session.Id) == first, "the service persists one small plugin-owned workspace coordinate");
            Check(Rejects(() => workspaces.Set(session.Id, "fixture.plugin", "planning", "resource-2", 0)),
                "replacement requires the current workspace revision");
            var second = workspaces.Set(session.Id, "fixture.plugin", "planning", "resource-2", first.Revision);
            Check(second.Revision == 2 && second.ResourceId == "resource-2", "a current revision can replace the coordinate without copying domain state");
            var cleared = workspaces.Clear(session.Id, second.Revision);
            Check(workspaces.Get(session.Id) is null && cleared is not null, "clearing restores the normal conversation projection");
            Check(Rejects(() => workspaces.Set("missing", "fixture.plugin", "planning", "resource", 0)),
                "a workspace cannot create a phantom conversation");
            Check(Rejects(() => workspaces.Set(session.Id, "fixture.plugin", "bad\nview", "resource", 0)),
                "workspace coordinates reject control characters");
            Console.WriteLine($"Conversation workspace: {checks} service persistence checks passed.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
