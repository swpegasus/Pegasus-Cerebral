using System.Diagnostics;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class JobScheduler
{
    public HashSet<string> ConversationJobIds(string sessionId)
    {
        if (store.GetSessionMetadata(sessionId) is null) throw new ArgumentException("Conversation not found.");
        var jobs = store.List<AgentJob>("job");
        var ids = jobs.Where(x => x.SessionId == sessionId).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        bool added;
        do { added = false; foreach (var job in jobs) if (job.ParentJobId is { } parent && ids.Contains(parent)) added |= ids.Add(job.Id); } while (added);
        return ids;
    }

    public object StopConversation(string sessionId)
    {
        var ids = ConversationJobIds(sessionId);
        var active = store.ActiveJobs().Where(x => ids.Contains(x.Id)).ToList();
        // Suppress delayed repair callbacks even when Stop follows a just-finished/crashed turn.
        store.Locked(() =>
        {
            foreach (var id in ids) { var job = Get(id); job.CancelRequested = true; store.Put("job", id, job); }
            foreach (var request in store.List<InterfaceRequest>("interface").Where(x => ids.Contains(x.JobId) && x.ProcessStatus == "failed"))
            { request.StopRequested = true; store.Put("interface", request.Id, request); }
            return true;
        });
        foreach (var job in active.Where(x => x.ParentJobId is null || !ids.Contains(x.ParentJobId))) Cancel(job.Id);
        foreach (var job in active) if (IsActive(Get(job.Id))) Cancel(job.Id);
        var applications = StopPresentations(ids);
        events.Publish("session.updated", null, new { id = sessionId });
        return new { cancelledJobIds = active.Select(x => x.Id).ToArray(), stoppedApplications = applications.Stopped, pendingApplications = applications.Pending };
    }

    private (int Stopped, int Pending) StopPresentations(HashSet<string> jobIds)
    {
        var stopped = 0; var pending = 0;
        foreach (var candidate in store.List<InterfaceRequest>("interface").Where(x => jobIds.Contains(x.JobId) && x.Status != "cancelled" &&
            (x.Status is "pending" or "claimed" || x.ProcessStatus is "running" or "ready" or "starting" || (x.Kind == "application" && x.ProcessStatus is null))))
        {
            var request = store.Locked(() =>
            {
                var current = store.Get<InterfaceRequest>("interface", candidate.Id)!;
                current.StopRequested = true;
                if (current.Status == "pending") { current.Status = "cancelled"; current.CompletedAt = DateTimeOffset.UtcNow; }
                store.Put("interface", current.Id, current); return current;
            });
            if (request.ProcessId is not null && request.ProcessStartedAt is not null)
            {
                try
                {
                    if (OwnedApplication.Stop(request)) stopped++;
                    request.ProcessStatus = "stopped";
                    store.Put("interface", request.Id, request);
                }
                catch (Exception ex)
                {
                    pending++; request.Error = "Stop requested; " + ex.Message;
                    store.Put("interface", request.Id, request);
                }
            }
            else if (request.Status != "cancelled") pending++;
            events.Publish("interface.updated", request.JobId, request);
        }
        return (stopped, pending);
    }

    public void ProcessQuestionTimeouts()
    {
        var settings = configuration.Get();
        if (settings.QuestionTimeoutSeconds == 0 || settings.QuestionTimeoutAction != "assume") return;
        foreach (var pending in store.List<UserQuestion>("question").Where(x => x.Answer is null && !x.Required && x.CanAssume &&
                     !string.IsNullOrWhiteSpace(x.Assumption) && x.RemoteQuestionId is null))
        {
            var pendingJob = store.Get<AgentJob>("job", pending.JobId);
            if (pendingJob is null) continue;
            var rootSessionId = RootJob(pendingJob).SessionId;
            store.LinearizedSessions([pending.SessionId, rootSessionId], () => store.Locked(() =>
            {
                var question = store.Get<UserQuestion>("question", pending.Id)!;
                if (question.SessionId != pending.SessionId) throw new InvalidDataException("Question conversation identity changed.");
                var job = Get(question.JobId);
                var root = RootJob(job);
                if (root.SessionId != rootSessionId) throw new InvalidDataException("Question assignment ancestry changed.");
                if (question.RemoteQuestionId is not null || question.Answer is not null || job.CancelRequested || !IsActive(job)) return false;
                var newDeadline = question.DeadlineAt is null;
                question.DeadlineAt ??= question.CreatedAt.AddSeconds(settings.QuestionTimeoutSeconds);
                if (question.DeadlineAt > DateTimeOffset.UtcNow) { store.Put("question", question.Id, question); if (newDeadline) events.Publish("question.updated", job.Id, question); return false; }
                question.Answer = question.Assumption; question.Assumed = true; store.Put("question", question.Id, question);
                var context = ProjectQuestionState(question, job, delegated: false);
                var leadContext = ProjectDelegatedQuestionState(question, job);
                var continuation = FindActiveQuestionContinuation(job, root);
                if (continuation is not null)
                {
                    var delivery = continuation.SessionId == context.SessionId ? context : leadContext ?? context;
                    QueueQuestionContextForActiveJob(continuation, question, delivery.Message);
                }
                events.Publish("question.updated", job.Id, question);
                if (context.Changed) events.Publish("session.updated", job.Id, new { id = context.SessionId });
                if (leadContext?.Changed == true) events.Publish("session.updated", job.Id, new { id = leadContext.SessionId });
                return true;
            }));
        }
    }

    private void ResumePresentationFailures()
    {
        foreach (var pending in store.List<InterfaceRequest>("interface").Where(x => x.ProcessStatus == "failed" && !x.RepairScheduled && !x.StopRequested))
        {
            store.Locked(() =>
            {
                var request = store.Get<InterfaceRequest>("interface", pending.Id)!;
                var job = Get(request.JobId);
                if (request.RepairScheduled || request.StopRequested || job.CancelRequested || job.Status is "cancelled" or "interrupted") return false;
                if (store.List<AgentJob>("job").Any(x => x.SessionId == job.SessionId && x.CreatedAt > job.CreatedAt))
                { request.RepairScheduled = true; store.Put("interface", request.Id, request); return false; }
                // One continuation of the existing assignment, with its existing time/step limits.
                // Repeated launch failures stay recorded but cannot create an endless new-job loop.
                var prior = store.List<InterfaceRequest>("interface").Any(x => x.JobId == job.Id && x.Id != request.Id && x.RepairScheduled);
                if (IsActive(job)) return false;
                request.RepairScheduled = true; store.Put("interface", request.Id, request);
                if (prior || job.Evidence.Any(x => x.RequestId == request.Id && x.Resolution is not null) || job.StepCount >= job.Profile.MaxSteps || job.ActiveSeconds >= job.Profile.MaxSeconds) return false;
                job.Status = "queued"; job.Outcome = null; job.Result = null; job.FinishedAt = null;
                job.WaitingReason = "Application failed; continuing the requested repair with the recorded error.";
                Save(job); return true;
            });
        }
    }
}

/// <summary>Identifies the exact application launched by the interface; never matches a process name alone.</summary>
public static class OwnedApplication
{
    public static bool Stop(InterfaceRequest request)
    {
        if (request.ProcessId is not { } id || request.ProcessStartedAt is not { } started) return false;
        Process process;
        try { process = Process.GetProcessById(id); } catch (ArgumentException) { return false; }
        using (process)
        {
            if (process.HasExited) return false;
            if (Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalMilliseconds) > 50)
                throw new InvalidOperationException("The process identity changed; no process was stopped.");
            var image = process.MainModule?.FileName;
            if (image is null || !Path.GetFullPath(image).Equals(Path.GetFullPath(request.Path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("The executable identity changed; no process was stopped.");
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(2000)) throw new TimeoutException("The owned process has not confirmed exit yet.");
            return true;
        }
    }
}
