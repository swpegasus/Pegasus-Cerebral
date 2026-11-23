using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

public static class LongFormDiagnosticsTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            checks++;
            Console.WriteLine("PASS: " + message);
        }

        var directory = Path.Combine(Path.GetTempPath(), "cerebral-longform-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new StateStore(directory);
        var configuration = new Configuration(store);
        var events = new EventHub();
        var runtime = new ModelRuntime(store, configuration.Get, events);
        var tools = new ToolDispatcher(configuration.Get);
        var scheduler = new JobScheduler(store, configuration, new NoExecutor(), runtime, events, NullLogger<JobScheduler>.Instance);
        var commands = new CommandDispatcher(store, configuration, runtime, scheduler, tools, events);
        try
        {
            var owner = new AgentProfile { Id = "lead", Name = "Lead", ModelId = "unused" };
            var outsider = new AgentProfile { Id = "other", Name = "Other", ModelId = "unused" };
            store.Put("agent", owner.Id, owner);
            store.Put("agent", outsider.Id, outsider);
            var session = new TaskSession { Id = "draft-session", ProfileId = owner.Id, Title = "Draft" };
            var empty = new TaskSession { Id = "empty-session", ProfileId = owner.Id, Title = "Empty" };
            store.Put("session", session.Id, session);
            store.Put("session", empty.Id, empty);

            var state = new LongFormState
            {
                SessionId = session.Id,
                SourceJobId = "draft-job",
                SourceRequestSequence = 17,
                Revision = 8,
                Path = @"C:\drafts\novel.txt",
                TargetLabel = "approximately 1,000 words",
                TargetWords = 1000,
                WordsPerPage = 250,
                VerifiedWords = 640,
                VerifiedLines = 82,
                VerifiedBytes = 4096,
                Sha256 = new string('a', 64),
                RecentTail = "RECENT-TAIL-PROSE-MUST-STAY-PRIVATE",
                CurrentSectionIndex = 2,
                PendingAppend = new()
                {
                    JobId = "draft-job",
                    Step = 6,
                    Path = @"C:\drafts\novel.txt",
                    ExpectedSha256 = new string('b', 64),
                    ExpectedBytes = 4096,
                    Content = "PENDING-APPEND-PROSE-MUST-STAY-PRIVATE",
                    Fingerprint = new string('c', 64),
                    CreatedAt = DateTimeOffset.Parse("2026-09-14T18:30:00Z")
                },
                Sections = Enumerable.Range(1, 35).Select(index => new LongFormSection
                {
                    Title = "Section " + index,
                    Purpose = "Purpose " + index,
                    TargetWords = 100,
                    CompletedAtWords = index <= 2 ? 640 : null
                }).ToList()
            };
            store.Put("longform", session.Id, state);

            var direct = await commands.ExecuteAsync(new("sessions.longform", CerebralJson.Element(new { id = session.Id })));
            var profile = await commands.ExecuteAsync(new("sessions.longform", CerebralJson.Element(new { id = session.Id }), owner.Id));
            var denied = await commands.ExecuteAsync(new("sessions.longform", CerebralJson.Element(new { id = session.Id }), outsider.Id));
            var raw = direct.Data.GetRawText();
            Check(direct.Success && profile.Success && !denied.Success,
                "long-form diagnostics allow the installation owner and owning profile but reject another profile");
            Check(direct.Data.GetProperty("tracked").GetBoolean() && direct.Data.GetProperty("path").GetString() == state.Path &&
                direct.Data.GetProperty("verifiedWords").GetInt32() == 640 && direct.Data.GetProperty("remainingWords").GetInt32() == 360 &&
                direct.Data.GetProperty("sha256").GetString() == state.Sha256,
                "long-form diagnostics expose persisted artifact identity, receipt, and measured progress");
            Check(direct.Data.GetProperty("sectionCount").GetInt32() == 35 && direct.Data.GetProperty("sections").GetArrayLength() == 32 &&
                direct.Data.GetProperty("sectionsTruncated").GetBoolean(),
                "long-form diagnostics bound an oversized persisted outline while reporting its actual count");
            var pending = direct.Data.GetProperty("pendingAppend");
            Check(pending.GetProperty("jobId").GetString() == "draft-job" && pending.GetProperty("step").GetInt32() == 6 &&
                pending.GetProperty("expectedBytes").GetInt32() == 4096 && pending.GetProperty("fingerprint").GetString() == new string('c', 64) &&
                pending.GetProperty("contentCharacters").GetInt32() == state.PendingAppend.Content.Length,
                "pending append diagnostics expose replay metadata and payload measurements");
            Check(!raw.Contains(state.PendingAppend.Content, StringComparison.Ordinal) && !raw.Contains(state.RecentTail, StringComparison.Ordinal) &&
                !raw.Contains("\"content\":", StringComparison.OrdinalIgnoreCase) && !raw.Contains("recentTail", StringComparison.OrdinalIgnoreCase),
                "long-form diagnostics never expose pending append content or retained recent prose");

            var absent = await commands.ExecuteAsync(new("sessions.longform", CerebralJson.Element(new { id = empty.Id }), owner.Id));
            Check(absent.Success && !absent.Data.GetProperty("tracked").GetBoolean(),
                "a conversation without long-form state returns a bounded untracked result after reconnect");
        }
        finally
        {
            scheduler.Dispose();
            await tools.DisposeAsync();
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        Console.WriteLine($"Long-form diagnostics tests passed: {checks}");
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken cancellationToken) => throw new InvalidOperationException("No model is used by this fixture.");
    }
}
