using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class InterfaceRequests
{
    public InterfaceRequest Observe(ApplicationObservation observation)
    {
        if (observation.ProcessId <= 0 || observation.Status is not ("running" or "ready" or "exited" or "failed" or "stopped"))
            throw new ArgumentException("Use a valid process identity and application status.");
        if (observation.Output?.Length > 8192 || observation.Output?.Contains('\0') == true)
            throw new ArgumentException("Application output must be at most 8192 characters without NUL.");
        if (observation.Status is ("running" or "ready") && observation.ExitCode is not null || observation.Status == "exited" && observation.ExitCode != 0 ||
            observation.Status == "failed" && (observation.ExitCode is null || observation.ExitCode == 0))
            throw new ArgumentException("Application status and exit code do not agree.");
        var changed = false;
        var request = store.Locked(() =>
        {
            var value = Get(observation.RequestId);
            if (value.Kind != "application" || value.ClaimId != observation.ClaimId) throw new UnauthorizedAccessException("Application observation does not match its claimed request.");
            if (value.Status is not ("claimed" or "completed")) throw new InvalidOperationException("This request cannot accept application observations.");
            if (value.ProcessId is { } known && (known != observation.ProcessId || value.ProcessStartedAt != observation.ProcessStartedAt))
                throw new InvalidOperationException("The application process identity cannot be replaced.");
            if (value.ProcessStatus is "exited" or "failed" or "stopped")
            {
                if (value.ProcessStatus == observation.Status && value.ExitCode == observation.ExitCode) return value;
                // A process stopped by the service may race the GUI exit callback.
                if (value.StopRequested && value.ProcessStatus == "stopped") return value;
                throw new InvalidOperationException("A terminal application observation cannot be overwritten.");
            }
            value.ProcessId = observation.ProcessId; value.ProcessStartedAt = observation.ProcessStartedAt;
            value.ProcessStatus = value.StopRequested && observation.Status is ("exited" or "failed" or "stopped") ? "stopped" : observation.Status;
            value.ExitCode = observation.ExitCode; value.Output = observation.Output;
            var success = value.ProcessStatus is "running" or "ready" or "exited" or "stopped";
            var summary = $"Application {value.Path}; process {value.ProcessId}; state={value.ProcessStatus}; exitCode={value.ExitCode?.ToString() ?? "not exited"}.";
            if (!string.IsNullOrWhiteSpace(value.Output)) summary += "\nOutput:\n" + value.Output;
            var session = store.Append(value.SessionId, "user", summary +
                (value.ProcessStatus == "failed" ? "\nThe requested application crashed. Repair the observed failure, rebuild the same configuration, and present the verified executable. Do not hand available work back to the user." :
                value.ProcessStatus == "running" ? "\nThe process was observed running; that alone does not prove its visible content or overall correctness." : value.ProcessStatus == "ready" ? "\nThe user interface observed a responsive native application window after startup. This verifies startup, not every application feature." : ""), origin: "service");
            var job = store.Get<AgentJob>("job", value.JobId)!;
            job.Evidence.Add(new ExecutionEvidence { Kind = value.ProcessStatus == "running" ? "application-start" : value.ProcessStatus == "ready" ? "application-ready" : "application-exit", Name = value.Path,
                Success = success, ExitCode = value.ExitCode, Summary = summary, RequestId = value.Id, SourceSessionId = value.SessionId, MessageSequence = session.Sequence });
            job.Evidence = job.Evidence.TakeLast(100).ToList();
            if (value.ProcessStatus == "failed") { job.Outcome = "failed"; job.Error = "Application exited with code " + value.ExitCode; }
            store.Put("job", job.Id, job); store.Put(RecordKind, value.Id, value); changed = true; return value;
        });
        if (request.StopRequested && request.ProcessStatus is ("running" or "ready"))
        {
            try { OwnedApplication.Stop(request); return Observe(observation with { Status = "stopped", ExitCode = null }); }
            catch (Exception ex)
            {
                request.Error = "Stop requested; process exit not confirmed: " + ex.Message;
                store.Put(RecordKind, request.Id, request);
                events.Publish("error", request.JobId, new { message = request.Error });
            }
        }
        if (changed) { Publish(request); events.Publish("session.updated", request.JobId, new { id = request.SessionId }); }
        return request;
    }
}
