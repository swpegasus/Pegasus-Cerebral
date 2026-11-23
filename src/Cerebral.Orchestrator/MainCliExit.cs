using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

// Access is serialized on Main's dispatcher. Transport admission remains the pinned
// Desktop server's same-owner/session check; this token acknowledges a read reply.
internal sealed class MainExitAdmission(TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private string? admissionToken;
    private long preparedAt;
    private bool committed;

    internal DesktopControlResponse Handle(DesktopControlRequest request, string instanceId, bool exitAvailable, Action exit)
    {
        DesktopControlProtocol.Validate(request, instanceId);
        var fields = request.Arguments.EnumerateObject().ToArray();
        var prepare = request.Operation == DesktopControlProtocol.FullExitPrepare;
        if (!prepare && request.Operation != DesktopControlProtocol.FullExitCommit)
            throw new ArgumentException("Unknown Main exit operation.");
        if (fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            (prepare ? fields.Length != 0 : fields.Length != 2 ||
             fields.Any(field => field.Name is not ("instanceId" or "admissionToken") || field.Value.ValueKind != JsonValueKind.String)))
            throw new ArgumentException("Exit prepare takes no arguments; commit requires exactly instanceId and admissionToken.");
        if (committed)
            return DesktopControlProtocol.Result(request, "rejected", new { code = "exit_already_committed", instanceId },
                "Exit was already requested for this Main generation. Inspect Main; use its tray Exit if saving or Worker shutdown kept it open. Do not retry automatically.");
        if (!exitAvailable)
            return DesktopControlProtocol.Result(request, "rejected", new { code = "exit_not_ready", instanceId },
                "Main's ordinary Exit handler is not ready. Wait for startup and use tray Exit.");
        if (prepare)
        {
            if (admissionToken is null || Expired())
            {
                admissionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                preparedAt = clock.GetTimestamp();
            }
            return DesktopControlProtocol.Result(request, "completed", new
            {
                schema = "cerebral.main-exit-admission/1", instanceId, admissionToken, expiresAfterSeconds = 30,
                effect = "none", next = DesktopControlProtocol.FullExitCommit
            });
        }
        if (request.Arguments.GetProperty("instanceId").GetString() != instanceId ||
            admissionToken is null || Expired() ||
            request.Arguments.GetProperty("admissionToken").GetString() != admissionToken)
            return DesktopControlProtocol.Result(request, "rejected", new { code = "exit_admission_invalid", instanceId },
                "Exit admission is absent, expired, or belongs to another Main generation. No Exit was dispatched.");
        // A caller can return this unpredictable token only after reading prepare's reply.
        // Consume BEFORE invoking the existing tray route, even if it synchronously closes Main.
        committed = true;
        admissionToken = null;
        exit();
        return DesktopControlProtocol.Result(request, "accepted", new { instanceId, admissionAcknowledged = true, exitRequested = true });
    }

    private bool Expired() => clock.GetElapsedTime(preparedAt) >= TimeSpan.FromSeconds(30);
}

internal sealed class MainExitClientSession(string instanceId)
{
    internal bool CommitAttempted { get; private set; }

    internal async Task<DesktopControlResponse> ExecuteAsync(Func<string, string, string, Task<DesktopControlResponse>> call)
    {
        var capabilities = await call(DesktopControlProtocol.CapabilitiesInspect, Guid.NewGuid().ToString("N"), "{}");
        if (!capabilities.Success ||
            !capabilities.Data.TryGetProperty("endpoint", out var endpoint) ||
            endpoint.GetProperty("instanceId").GetString() != instanceId ||
            !capabilities.Data.TryGetProperty("desktopControl", out var control) ||
            control.GetProperty("mode").GetString() != "normal-semantic" ||
            !control.GetProperty("admittedOperations").EnumerateArray().Any(value => value.GetString() == DesktopControlProtocol.FullExitPrepare) ||
            !control.GetProperty("admittedOperations").EnumerateArray().Any(value => value.GetString() == DesktopControlProtocol.FullExitCommit))
            throw new InvalidOperationException("This Main does not advertise supported CLI Exit. Use its tray Exit; install the new candidate after that manual exit.");
        var prepare = await call(DesktopControlProtocol.FullExitPrepare, Guid.NewGuid().ToString("N"), "{}");
        if (prepare.State != "completed" || prepare.Data.GetProperty("schema").GetString() != "cerebral.main-exit-admission/1" ||
            prepare.Data.GetProperty("instanceId").GetString() != instanceId ||
            prepare.Data.GetProperty("effect").GetString() != "none" ||
            prepare.Data.GetProperty("next").GetString() != DesktopControlProtocol.FullExitCommit)
            throw new InvalidOperationException(prepare.Error ?? "Main did not return valid Exit admission. No commit was sent.");
        var token = prepare.Data.GetProperty("admissionToken").GetString();
        if (token is null || token.Length != 64 || !token.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Main returned invalid Exit admission. No commit was sent.");
        var arguments = JsonSerializer.Serialize(new { instanceId, admissionToken = token }, CerebralJson.Options);
        CommitAttempted = true;
        return await call(DesktopControlProtocol.FullExitCommit, Guid.NewGuid().ToString("N"), arguments);
    }
}

internal static class MainExitCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (!(args.Length == 1 && args[0] == "--exit") &&
            !(args.Length == 3 && args[0] == "--exit" && args[1] == "--endpoint"))
        {
            Console.Error.WriteLine("Usage: --exit [--endpoint <absolute-control.json>]. Use the installed Main payload in the signed-in user's session.");
            return 2;
        }
        try
        {
            var descriptor = args.Length == 3 ? args[2] : Path.Combine(InstallationPaths.CurrentUserDataDirectory, "Desktop", "control.json");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var endpoint = await SemanticControlIdentity.ReadEndpointAsync(descriptor, DesktopControlProtocol.Product,
                DesktopControlProtocol.PipePrefix, deadline.Token);
            using var target = Process.GetProcessById(endpoint.ProcessId);
            _ = target.SafeHandle; // Hold this process generation across a lost commit reply; never follow a recycled PID.
            if (target.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) != endpoint.ProcessStartUtcTicks ||
                target.SessionId != endpoint.SessionId)
                throw new InvalidOperationException("Main's descriptor is stale. No Exit was dispatched; inspect the current Main.");
            var session = new MainExitClientSession(endpoint.InstanceId);
            DesktopControlResponse? commit = null;
            string? dispatchError = null;
            try
            {
                commit = await session.ExecuteAsync((operation, requestId, arguments) =>
                    DesktopControlClient.ExecuteAsync(descriptor, operation, requestId, arguments, deadline.Token));
            }
            catch (Exception error) when (session.CommitAttempted) { dispatchError = error.Message; }
            if (commit is { Success: false })
            {
                Write(false, commit.State, endpoint.InstanceId, commit.Error ?? "Main refused Exit. Inspect Main before another action.");
                return commit.State == "uncertain" ? 3 : 2;
            }
            try
            {
                if (commit is not null && (commit.State != "accepted" ||
                    commit.Data.GetProperty("instanceId").GetString() != endpoint.InstanceId ||
                    !commit.Data.GetProperty("admissionAcknowledged").GetBoolean() || !commit.Data.GetProperty("exitRequested").GetBoolean()))
                    dispatchError = "The commit reply did not confirm this generation's acknowledged Exit request.";
            }
            catch (Exception error) { dispatchError = "Invalid commit reply; dispatch remains unconfirmed. " + error.Message; }
            using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await target.WaitForExitAsync(observation.Token); }
            catch (OperationCanceledException)
            {
                Write(false, "uncertain", endpoint.InstanceId,
                    "Exit commit was attempted; this exact Main has not exited within 30 seconds. Inspect Main for draft-save or Worker-stop refusal; do not retry automatically. " + dispatchError);
                return 3;
            }
            Write(true, "exited", endpoint.InstanceId,
                "The original Main process exited after acknowledged admission and a commit attempt. " +
                (dispatchError is null ? "Commit was accepted." : "The commit reply was unconfirmed; process exit was observed. Worker shutdown was not independently observed."));
            return 0;
        }
        catch (Exception error)
        {
            Write(false, "refused", null, "No confirmed Exit commit. " + error.Message + " Use the tray Exit if Main remains open. No automatic retry was made.");
            return 2;
        }
    }

    private static void Write(bool ok, string state, string? instanceId, string detail) =>
        Console.WriteLine(JsonSerializer.Serialize(new { ok, state, instanceId, detail }, CerebralJson.Options));
}
