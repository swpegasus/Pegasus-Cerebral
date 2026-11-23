using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static class WorkflowContextTests
{
    public static async Task RunAsync()
    {
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            assertions++;
            Console.WriteLine("PASS: " + name);
        }

        const string visible = "Help me plan this book.";
        const string privateContext = "Use book.production.status, keep BookBible.db canonical, and continue from chapter 4.";
        var session = new TaskSession
        {
            Checkpoint = "Earlier chapters and accepted author choices are preserved.",
            CheckpointThroughSequence = 2,
            Messages =
            [
                new() { Sequence = 1, Role = "user", Origin = "user", Content = "Create a science-fiction comedy." },
                new() { Sequence = 2, Role = "assistant", Origin = "agent", Content = "The Book Bible was created." },
                new() { Sequence = 3, Role = "user", Origin = "user", Content = visible },
                new() { Sequence = 4, Role = "assistant", Origin = "agent", Content = "Preparing the next bounded step." },
                new() { Sequence = 5, Role = "user", Origin = "service", Content = "Saved workflow receipt." }
            ]
        };
        var job = new AgentJob
        {
            Prompt = privateContext,
            VisiblePrompt = visible,
            WorkflowSource = "pegasus.books",
            StartSequence = 3
        };
        var assignment = JobAssignmentContext.From(job);
        Check(assignment is not null, "accepted plugin workflow creates explicit job-scoped model context");

        var projected = WorkingContext.Assemble(session, "SYSTEM AUTHORITY", job.StartSequence, assignment);
        Check(projected[0] is { Role: "system", Content: "SYSTEM AUTHORITY" } &&
              projected[1].Role == "user" && projected[1].Content.Contains("not system guidance", StringComparison.Ordinal) &&
              projected[1].Content.Contains("Workflow source: pegasus.books", StringComparison.Ordinal) &&
              projected[1].Content.Contains(privateContext, StringComparison.Ordinal),
            "plugin workflow context reaches model input as a labelled user-level message below system guidance");
        Check(projected.Any(x => x.Content == visible) && session.Messages.All(x => !x.Content.Contains(privateContext, StringComparison.Ordinal)),
            "the exact visible request remains model-visible while private workflow context stays out of retained transcript messages");
        Check(!WorkingContext.Assemble(session, "SYSTEM AUTHORITY", job.StartSequence)
                .Any(x => x.Content.Contains(privateContext, StringComparison.Ordinal)),
            "ordinary conversation projection cannot invent or leak a job-scoped plugin handoff");

        var counted = new List<IReadOnlyList<ModelMessage>>();
        Task<int> Count(IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counted.Add(messages.Select(x => new ModelMessage(x.Role, x.Content)).ToArray());
            return Task.FromResult(messages.Sum(x => Math.Max(1, x.Content.Length / 8)));
        }

        var fit = await WorkingContext.FitCheckpointAsync(session, "SYSTEM AUTHORITY", job.StartSequence, null,
            assignment, 4096, 512, "off", 0, Count, default);
        Check(fit.Messages.Any(x => x.Content.Contains(privateContext, StringComparison.Ordinal)) &&
              counted.Count > 0 && counted.All(messages => messages.Any(x => x.Content.Contains(privateContext, StringComparison.Ordinal))),
            "checkpoint fitting counts and preserves the exact plugin assignment context in every evaluated model prompt");

        counted.Clear();
        var floor = await WorkingContext.FitAfterCompactionFloorAsync(session, "SYSTEM AUTHORITY", job.StartSequence, null,
            assignment, 4096, 512, "off", 0, Count, default);
        Check(floor is not null && floor.Messages.Any(x => x.Content.Contains(privateContext, StringComparison.Ordinal)) &&
              counted.All(messages => messages.Any(x => x.Content.Contains(privateContext, StringComparison.Ordinal))),
            "post-compaction floor retains and counts job-scoped plugin assignment context");

        var current = WorkingContext.CurrentTurnMessages(session, job.StartSequence, assignment);
        Check(current.Any(x => x.Content.Contains(privateContext, StringComparison.Ordinal)) &&
              current.Any(x => x.Content == visible) && current.All(x => !x.Content.Contains("Create a science-fiction comedy.", StringComparison.Ordinal)),
            "current-turn overflow diagnosis includes the private assignment and visible request without unrelated old history");

        var resumed = CerebralJson.Read<AgentJob>(CerebralJson.Element(job));
        Check(JobAssignmentContext.From(resumed)?.Prompt == privateContext,
            "persisted job resume reconstructs the same private workflow context without transcript storage");
        Check(JobAssignmentContext.From(new AgentJob { Prompt = visible }) is null,
            "ordinary jobs add no synthetic assignment context");

        var invalidRejected = false;
        try { _ = JobAssignmentContext.From(new AgentJob { Prompt = privateContext, WorkflowSource = "pegasus.books" }); }
        catch (InvalidDataException) { invalidRejected = true; }
        Check(invalidRejected, "an incomplete persisted plugin assignment fails closed instead of silently dropping its context");

        Console.WriteLine($"Workflow context: {assertions} deterministic checks passed; no model, service, or user database invoked.");
    }
}
