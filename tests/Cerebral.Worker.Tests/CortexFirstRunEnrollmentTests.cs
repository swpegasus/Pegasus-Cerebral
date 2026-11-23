using Cerebral.Client;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerebral.Worker.Tests;

/// <summary>Pure orchestration tests. No installed CLI, key, file, or Cortex state is touched.</summary>
internal static class CortexFirstRunEnrollmentTests
{
    private sealed class UnexpectedSuccessException(string message) : Exception(message) { }

    private sealed class FakeFiles : CortexFirstRunEnrollment.ICortexEnrollmentFiles
    {
        internal const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal const string Defaults = @"C:\Fixture\private\attempt.defaults";
        public bool Existing { get; set; }
        public bool Valid { get; set; } = true;
        public bool Pending { get; set; }
        public bool CleanupFails { get; set; }
        public int Creates { get; private set; }
        public int Deletes { get; private set; }
        public int LeaseAcquires { get; private set; }
        public int LeaseDisposes { get; private set; }
        public CortexEnrollmentAttemptStatus? Status { get; private set; }
        public IDisposable AcquireCodeLeases(CortexFirstRunEnrollmentPlan plan)
        {
            LeaseAcquires++;
            return new FakeLease(() => LeaseDisposes++);
        }
        private sealed class FakeLease(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
        public bool CredentialExists(string path) => Existing;
        public void RequireNoPending(string clientId)
        {
            if (Pending || Status?.State is ("unknown" or "partial"))
                throw new InvalidOperationException("fixture prior attempt remains unknown");
        }
        public CortexFirstRunEnrollment.CortexEnrollmentAttempt CreateAttempt(CortexFirstRunEnrollmentPlan plan, string defaultsSha256)
        {
            Creates++;
            Status = new(Id, "prepared", null, null, null, null);
            return new(Id, Defaults);
        }
        public void MarkLaunched(string attemptId, int? pid, long? startUtcTicks)
        {
            Status = Status is { State: "unknown" } prior
                ? CortexFirstRunEnrollment.MergeLateChildIdentity(prior, pid, startUtcTicks)
                : new(Id, "running", pid, startUtcTicks, null, null);
        }
        public void MarkUnknown(string attemptId, string issueCode)
        {
            if (Status?.State is "confirmed" or "partial" or "not_started") return;
            Status = Status! with { State = "unknown", IssueCode = issueCode };
        }
        public void MarkTerminal(string attemptId, string state, int? exitCode, string? issueCode)
        {
            Status = Status! with { State = state, ExitCode = exitCode, IssueCode = issueCode };
        }
        public void DeleteDefaults(string path)
        {
            Deletes++;
            if (CleanupFails) throw new IOException("fixture defaults cleanup failed");
        }
        public bool CredentialValid(string path) => Valid;
        public CortexEnrollmentAttemptStatus? Inspect(string attemptId) => Status;
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var worker = @"C:\Fixture\Cerebral.Worker.exe";
        var clientId = CortexFirstRunEnrollment.ClientIdForWorker(worker, "S-1-5-21-fixture");
        check(clientId == CortexFirstRunEnrollment.ClientIdForWorker(worker, "S-1-5-21-fixture") &&
            clientId == CortexFirstRunEnrollment.ClientIdForWorker(@"c:\fixture\cerebral.worker.exe", "S-1-5-21-fixture") &&
            clientId != CortexFirstRunEnrollment.ClientIdForWorker(@"D:\Portable\Cerebral.Worker.exe", "S-1-5-21-fixture") &&
            clientId != CortexFirstRunEnrollment.ClientIdForWorker(worker, "S-1-5-21-other") &&
            clientId.Length == 41,
            "client identity is stable for one Worker and distinct across portable roots or owners");
        var codeManifest = "bin/semantics/PegasusCortex.dll\t100\t" + new string('b', 64) + "\n" +
            "bin/host/PegasusCortex.Host.dll\t100\t" + new string('c', 64) + "\n";
        var codeDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(codeManifest)));
        var plan = new CortexFirstRunEnrollmentPlan(@"C:\Fixture\Cortex\PegasusCortex.exe",
            @"C:\Fixture\Cortex\bin\semantics\PegasusCortex.exe",
            @"C:\Fixture\Cortex\bin\host\PegasusCortex.Host.exe",
            worker, clientId, @"C:\Fixture\private\" + clientId + ".key",
            "S-1-5-21-fixture", 2, "2.2026.10.27.04.21.A", "2.2026.10.27.04.21.A", new string('b', 64),
            codeManifest, codeDigest);
        var selected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PegasusCortex.dll"] = "PegasusCortex.dll",
            ["PegasusCortex.Mcp.Local.dll"] = "PegasusCortex.Mcp.Local.dll",
            ["PegasusCortex.Core.dll"] = "PegasusCortex.Core.dll",
            ["PegasusCortex.Contracts.dll"] = "PegasusCortex.Contracts.dll",
            ["PegasusCortex.Product.dll"] = "PegasusCortex.Product.dll",
            ["PegasusCortex.Mcp.dll"] = "PegasusCortex.Mcp.dll",
            ["PegasusCortex.LocalTransport.dll"] = "PegasusCortex.LocalTransport.dll",
            ["Pegasus.Shared.dll"] = "lib/net8.0/Pegasus.Shared.dll"
        };
        CortexFirstRunEnrollment.RequireRoleClosure("semantics", selected, _ => true);
        check(!CortexFirstRunEnrollment.RecordProductAsset(selected, "Pegasus.Shared.dll", "lib/net8.0/Pegasus.Shared.dll"),
            "selected installed projection can repeat identically across dependency entries");
        var malformedRejected = false;
        try { CortexFirstRunEnrollment.RecordProductAsset(selected, "PegasusCortex.New.dll", "lib/net9.0/PegasusCortex.New.dll"); }
        catch (InvalidDataException) { malformedRejected = true; }
        check(malformedRejected, "unreviewed nested Pegasus projection is rejected");
        var conflictingProjectionRejected = false;
        try { CortexFirstRunEnrollment.RecordProductAsset(selected, "Pegasus.Shared.dll", "Pegasus.Shared.dll"); }
        catch (InvalidDataException) { conflictingProjectionRejected = true; }
        check(conflictingProjectionRejected, "conflicting declared paths for one physical Pegasus payload are rejected");
        var missingEntryRejected = false;
        try { CortexFirstRunEnrollment.RequireRoleClosure("semantics", selected,
            relative => relative != "bin/semantics/PegasusCortex.dll"); }
        catch (InvalidDataException) { missingEntryRejected = true; }
        check(missingEntryRejected, "missing managed semantic entry DLL is rejected even when deps names it");
        var missingHostEntryRejected = false;
        try { CortexFirstRunEnrollment.RequireRoleClosure("host", selected,
            relative => relative != "bin/host/PegasusCortex.Host.dll"); }
        catch (InvalidDataException) { missingHostEntryRejected = true; }
        check(missingHostEntryRejected, "missing managed native host entry DLL is rejected even if deps omits it");
        var missingMcp = new Dictionary<string, string>(selected, StringComparer.Ordinal);
        missingMcp.Remove("PegasusCortex.Mcp.Local.dll");
        missingMcp["PegasusCortex.New.dll"] = "PegasusCortex.New.dll";
        var missingMcpRejected = false;
        try { CortexFirstRunEnrollment.RequireRoleClosure("semantics", missingMcp, _ => true); }
        catch (InvalidDataException) { missingMcpRejected = true; }
        check(missingMcpRejected, "semantic MCP payload must be present in the selected projected closure");
        var empty = new CortexFirstRunEnrollment.CortexCliReply(0, "{\"ok\":true,\"clients\":[]}");
        var timely = await CortexFirstRunEnrollment.AwaitPublicListAsync(Task.FromResult(empty),
            TimeSpan.Zero, CancellationToken.None);
        check(timely == empty, "a complete public list remains available even at the UI deadline boundary");
        var lateList = new TaskCompletionSource<CortexFirstRunEnrollment.CortexCliReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await CortexFirstRunEnrollment.AwaitPublicListAsync(lateList.Task, TimeSpan.Zero,
                CancellationToken.None, 55, 123, plan.CliExecutablePath);
            throw new UnexpectedSuccessException("unconfirmed public list cannot permit registration");
        }
        catch (CortexConnectionStatusUnconfirmedException error)
        {
            check(error.ProcessId == 55 && error.ProcessStartUtcTicks == 123 &&
                error.CliExecutablePath == plan.CliExecutablePath && !lateList.Task.IsCompleted,
                "list timeout retains PID/start/image and leaves collection live without registration");
        }
        lateList.SetResult(empty);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledList = new TaskCompletionSource<CortexFirstRunEnrollment.CortexCliReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await CortexFirstRunEnrollment.AwaitPublicListAsync(canceledList.Task, TimeSpan.FromSeconds(1), canceled.Token);
            throw new UnexpectedSuccessException("UI cancellation cannot confirm a pending public list");
        }
        catch (CortexConnectionStatusUnconfirmedException)
        {
            check(!canceledList.Task.IsCompleted, "list cancellation leaves the collector live and blocks registration");
        }
        canceledList.SetResult(empty);
        var success = new CortexFirstRunEnrollment.CortexCliReply(0, JsonSerializer.Serialize(new
        {
            ok = true, clientId = plan.ClientId, keyId = new string('a', 32), transport = "native",
            expiresUtc = DateTimeOffset.UtcNow.AddDays(89), credentialFile = plan.CredentialFile,
            protection = "windows-current-user-dpapi"
        }));

        var completed = new TaskCompletionSource<CortexFirstRunEnrollment.CortexCliReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult(success);
        var files = new FakeFiles();
        var prepares = 0; var lists = 0; var starts = 0;
        var result = await CortexFirstRunEnrollment.RegisterCoreAsync(plan,
            _ => { prepares++; return plan; },
            (exe, args, _) =>
            {
                lists++;
                check(exe == plan.CliExecutablePath && args.SequenceEqual(new[] { "connections", "list" }),
                    "public status uses exact selected semantic CLI");
                return Task.FromResult(empty);
            },
            (exe, args) =>
            {
                starts++;
                check(exe == plan.CliExecutablePath && args.SequenceEqual(new[]
                {
                    "connections", "register", "--client", plan.ClientId, "--application", plan.WorkerExecutablePath,
                    "--transport", "native", "--defaults-file", FakeFiles.Defaults
                }), "registration submits one exact native command with no secret argument");
                return new CortexFirstRunEnrollment.CortexRegistrationProcess(777, 123456, completed.Task);
            }, files, Admit, TimeSpan.FromSeconds(1), CancellationToken.None);
        check(result.ClientId == plan.ClientId && result.CredentialFile == plan.CredentialFile &&
            prepares == 2 && lists == 1 && starts == 1 && files.Creates == 1 && files.Deletes == 1 &&
            files.LeaseAcquires == 1 && files.LeaseDisposes == 1 &&
            files.Status is { State: "confirmed", ProcessId: 777, ProcessStartUtcTicks: 123456, ExitCode: 0 },
            "confirmed registration retains public PID/start evidence and cleans defaults after terminal exit");

        foreach (var state in new[] { "active", "expired", "revoked" })
        {
            var existing = new CortexFirstRunEnrollment.CortexCliReply(0,
                JsonSerializer.Serialize(new { ok = true, clients = new[]
                {
                    new { clientId = plan.ClientId, application = plan.WorkerExecutablePath, transport = "native", state }
                } }));
            var blocked = new FakeFiles(); var launched = 0;
            try
            {
                await CortexFirstRunEnrollment.RegisterCoreAsync(plan, _ => plan,
                    (_, _, _) => Task.FromResult(existing),
                    (_, _) => { launched++; throw new InvalidOperationException("must not start"); },
                    blocked, Admit, TimeSpan.Zero, CancellationToken.None);
                throw new UnexpectedSuccessException("existing Cortex client is not reissued");
            }
            catch (InvalidOperationException)
            {
                check(launched == 0 && blocked.Creates == 0,
                    "existing " + state + " Cortex client stops before durable attempt and CLI register");
            }
        }

        var legacy = new CortexFirstRunEnrollment.CortexCliReply(0,
            JsonSerializer.Serialize(new { ok = true, clients = new[]
            {
                new { clientId = "cerebral", application = plan.WorkerExecutablePath, transport = "native", state = "active" }
            } }));
        var legacyRejected = false;
        try
        {
            CortexFirstRunEnrollment.RequireNoExistingClient(legacy, plan.ClientId, plan.WorkerExecutablePath);
        }
        catch (InvalidOperationException)
        {
            legacyRejected = true;
        }
        check(legacyRejected, "legacy literal client ID remains preserved for the same Worker");
        var otherRoot = new CortexFirstRunEnrollment.CortexCliReply(0,
            JsonSerializer.Serialize(new { ok = true, clients = new[]
            {
                new { clientId = "cerebral", application = @"D:\Portable\Cerebral.Worker.exe", transport = "native", state = "active" }
            } }));
        CortexFirstRunEnrollment.RequireNoExistingClient(otherRoot, plan.ClientId, plan.WorkerExecutablePath);
        check(true, "a different portable Worker's existing client entry is preserved without blocking this Worker");

        var orphan = new FakeFiles { Existing = true }; var orphanStarts = 0;
        try
        {
            await CortexFirstRunEnrollment.RegisterCoreAsync(plan, _ => plan,
                (_, _, _) => Task.FromResult(empty),
                (_, _) => { orphanStarts++; throw new InvalidOperationException("must not start"); },
                orphan, Admit, TimeSpan.Zero, CancellationToken.None);
            throw new UnexpectedSuccessException("orphaned key is not adopted");
        }
        catch (InvalidOperationException)
        {
            check(orphanStarts == 0 && orphan.Creates == 0, "orphaned credential blocks registration");
        }

        using var canceledAfterList = new CancellationTokenSource();
        var canceledFiles = new FakeFiles();
        try
        {
            await CortexFirstRunEnrollment.RegisterCoreAsync(plan, _ => plan,
                (_, _, _) => { canceledAfterList.Cancel(); return Task.FromResult(empty); },
                (_, _) => throw new InvalidOperationException("must not start"),
                canceledFiles, Admit, TimeSpan.Zero, canceledAfterList.Token);
            throw new UnexpectedSuccessException("canceled status phase cannot create an enrollment intent");
        }
        catch (OperationCanceledException)
        {
            check(canceledFiles.Creates == 0, "cancellation after confirmed list stops before durable intent");
        }

        var pending = new FakeFiles { Pending = true }; var pendingLists = 0;
        try
        {
            await CortexFirstRunEnrollment.RegisterCoreAsync(plan, _ => plan,
                (_, _, _) => { pendingLists++; return Task.FromResult(empty); },
                (_, _) => throw new InvalidOperationException("must not start"),
                pending, Admit, TimeSpan.Zero, CancellationToken.None);
            throw new UnexpectedSuccessException("unknown attempt cannot be replayed");
        }
        catch (InvalidOperationException)
        {
            check(pendingLists == 0 && pending.Creates == 0, "unknown durable attempt stops before CLI status or register");
        }

        var changed = new FakeFiles(); var changedPrepares = 0; var changedStarts = 0;
        try
        {
            await CortexFirstRunEnrollment.RegisterCoreAsync(plan,
                _ => ++changedPrepares == 1 ? plan : plan with { WorkerSha256 = new string('c', 64) },
                (_, _, _) => Task.FromResult(empty),
                (_, _) => { changedStarts++; throw new InvalidOperationException("must not start"); },
                changed, Admit, TimeSpan.Zero, CancellationToken.None);
            throw new UnexpectedSuccessException("changed portable Worker bytes cannot register from stale plan");
        }
        catch (InvalidOperationException)
        {
            check(changedPrepares == 2 && changedStarts == 0 &&
                changed.Status is { State: "not_started", IssueCode: "target_changed" } && changed.Deletes == 1,
                "late Worker replacement aborts the durable intent before launch and cleans defaults");
        }

        foreach (var oldEntryHash in new[] { new string('b', 64), new string('c', 64) })
        {
            var codeDrift = new FakeFiles(); var codeStarts = 0; var codePrepares = 0;
            var changedManifest = codeManifest.Replace(oldEntryHash, new string('e', 64), StringComparison.Ordinal);
            var changedDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(changedManifest)));
            try
            {
                await CortexFirstRunEnrollment.RegisterCoreAsync(plan,
                    _ => ++codePrepares == 1 ? plan : plan with
                    { CodeManifest = changedManifest, CodeManifestSha256 = changedDigest },
                    (_, _, _) => Task.FromResult(empty),
                    (_, _) => { codeStarts++; throw new InvalidOperationException("must not start"); },
                    codeDrift, Admit, TimeSpan.Zero, CancellationToken.None);
                throw new UnexpectedSuccessException("same-label Cortex entry DLL drift cannot register from a stale plan");
            }
            catch (InvalidOperationException)
            {
                check(codePrepares == 2 && codeStarts == 0 && codeDrift.LeaseAcquires == 0 &&
                    codeDrift.Status is { State: "not_started", IssueCode: "target_changed" },
                    "same-label semantic or host managed entry body drift aborts before CLI launch");
            }
        }

        var delayedList = new TaskCompletionSource<CortexFirstRunEnrollment.CortexCliReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerCurrent = true; var delayedStarts = 0;
        var ownerFiles = new FakeFiles();
        var delayedRegistration = CortexFirstRunEnrollment.RegisterCoreAsync(plan, _ => plan,
            (_, _, _) => delayedList.Task,
            (_, _) => { delayedStarts++; throw new InvalidOperationException("must not start"); },
            ownerFiles,
            (launchAction, _) =>
            {
                if (!ownerCurrent) throw new InvalidOperationException("fixture live owner changed");
                launchAction();
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(1), CancellationToken.None);
        ownerCurrent = false;
        delayedList.SetResult(empty);
        var ownerRejected = false;
        try { await delayedRegistration; }
        catch (InvalidOperationException) { ownerRejected = true; }
        check(ownerRejected && delayedStarts == 0 && ownerFiles.Creates == 1 && ownerFiles.Deletes == 1 &&
            ownerFiles.LeaseAcquires == 1 && ownerFiles.LeaseDisposes == 1 &&
            ownerFiles.Status is { State: "not_started", IssueCode: "owner_rejected" },
            "live owner drift during public list rejects the one-use launch with zero CLI register calls");

        var launch = new FakeFiles();
        try
        {
            await CortexFirstRunEnrollment.RegisterCoreAsync(plan, _ => plan,
                (_, _, _) => Task.FromResult(empty),
                (_, _) => throw new IOException("fixture launch result is ambiguous"),
                launch, Admit, TimeSpan.Zero, CancellationToken.None);
            throw new UnexpectedSuccessException("ambiguous launch cannot be retried");
        }
        catch (CortexEnrollmentUncertainException error)
        {
            check(error.AttemptId == FakeFiles.Id && launch.Status is { State: "unknown", IssueCode: "launch_unknown" } &&
                launch.Deletes == 0, "ambiguous launch retains intent and defaults for review");
        }

        var lateTcs = new TaskCompletionSource<CortexFirstRunEnrollment.CortexCliReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new FakeFiles(); var lateStarts = 0;
        try
        {
            await CortexFirstRunEnrollment.RegisterCoreAsync(plan, _ => plan,
                (_, _, _) => Task.FromResult(empty),
                (_, _) => { lateStarts++; return new CortexFirstRunEnrollment.CortexRegistrationProcess(889, 234567, lateTcs.Task); },
                late, Admit, TimeSpan.Zero, CancellationToken.None);
            throw new UnexpectedSuccessException("UI wait expiry cannot terminate running registration");
        }
        catch (CortexEnrollmentUncertainException error)
        {
            check(error.AttemptId == FakeFiles.Id && error.State == "unknown" && lateStarts == 1 &&
                late.Status is { State: "unknown", ProcessId: 889 } && late.Deletes == 0,
                "UI wait expiry keeps the attempt and defaults while the CLI is still running");
        }
        lateTcs.SetResult(success);
        await UntilAsync(() => late.Status?.State == "confirmed" && late.Deletes == 1);
        check(late.Status is { State: "confirmed", ExitCode: 0 } && lateStarts == 1,
            "late terminal reply updates the original attempt without a second registration");

        var launchEntered = new ManualResetEventSlim(false);
        var releaseLaunch = new ManualResetEventSlim(false);
        var latePidReply = new TaskCompletionSource<CortexFirstRunEnrollment.CortexCliReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latePid = new FakeFiles(); var latePidStarts = 0;
        try
        {
            await CortexFirstRunEnrollment.RegisterCoreAsync(plan, _ => plan,
                (_, _, _) => Task.FromResult(empty),
                (_, _) =>
                {
                    latePidStarts++;
                    launchEntered.Set();
                    if (!releaseLaunch.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("fixture launch gate expired");
                    return new CortexFirstRunEnrollment.CortexRegistrationProcess(992, 567890, latePidReply.Task);
                }, latePid,
                (launchAction, _) =>
                {
                    var admitted = Task.Run(launchAction);
                    if (!launchEntered.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("fixture did not enter launch");
                    return admitted;
                }, TimeSpan.Zero, CancellationToken.None);
            throw new UnexpectedSuccessException("an expiring UI wait cannot confirm a launching child");
        }
        catch (CortexEnrollmentUncertainException error)
        {
            check(error.AttemptId == FakeFiles.Id && latePidStarts == 1 &&
                latePid.Status is { State: "unknown", IssueCode: "admission_ended", ProcessId: null } &&
                latePid.Deletes == 0,
                "admission timeout during launch retains unknown intent before child identity is available");
        }
        finally { releaseLaunch.Set(); }
        await UntilAsync(() => latePid.Status is { State: "unknown", ProcessId: 992, ProcessStartUtcTicks: 567890 });
        var latePidReplayBlocked = false;
        try { latePid.RequireNoPending(plan.ClientId); }
        catch (InvalidOperationException) { latePidReplayBlocked = true; }
        check(latePid.Status is { State: "unknown", IssueCode: "admission_ended" } && latePid.Deletes == 0,
            "late child PID/start enrich unknown attempt without regressing to running");
        check(latePidReplayBlocked, "late PID enrichment does not reopen the unknown attempt for replay");
        var knownChild = latePid.Status;
        var conflictingPidRejected = false;
        try { latePid.MarkLaunched(FakeFiles.Id, 993, 567890); }
        catch (InvalidDataException) { conflictingPidRejected = true; }
        var conflictingStartRejected = false;
        try { latePid.MarkLaunched(FakeFiles.Id, 992, 567891); }
        catch (InvalidDataException) { conflictingStartRejected = true; }
        check(conflictingPidRejected && conflictingStartRejected && latePid.Status == knownChild &&
            latePid.Status is { State: "unknown", ProcessId: 992, ProcessStartUtcTicks: 567890 },
            "conflicting known PID or start cannot replace durable unknown child identity");
        latePidReply.SetResult(success);
        await UntilAsync(() => latePid.Status?.State == "confirmed" && latePid.Deletes == 1);

        var captureTcs = new TaskCompletionSource<CortexFirstRunEnrollment.CortexCliReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        captureTcs.SetException(new IOException("fixture capture failed"));
        var capture = new FakeFiles();
        try
        {
            await CortexFirstRunEnrollment.RegisterCoreAsync(plan, _ => plan,
                (_, _, _) => Task.FromResult(empty),
                (_, _) => new CortexFirstRunEnrollment.CortexRegistrationProcess(990, 345678, captureTcs.Task),
                capture, Admit, TimeSpan.FromSeconds(1), CancellationToken.None);
            throw new UnexpectedSuccessException("capture failure cannot report a safe retry");
        }
        catch (CortexEnrollmentUncertainException error)
        {
            check(error.AttemptId == FakeFiles.Id && capture.Status is { State: "unknown", IssueCode: "capture_failed" } &&
                capture.Deletes == 0, "capture failure retains unknown attempt and defaults");
        }

        var cleanupTcs = new TaskCompletionSource<CortexFirstRunEnrollment.CortexCliReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        cleanupTcs.SetResult(success);
        var cleanup = new FakeFiles { CleanupFails = true };
        try
        {
            await CortexFirstRunEnrollment.RegisterCoreAsync(plan, _ => plan,
                (_, _, _) => Task.FromResult(empty),
                (_, _) => new CortexFirstRunEnrollment.CortexRegistrationProcess(991, 456789, cleanupTcs.Task),
                cleanup, Admit, TimeSpan.FromSeconds(1), CancellationToken.None);
            throw new UnexpectedSuccessException("post-submit cleanup failure cannot look retryable");
        }
        catch (CortexEnrollmentUncertainException error)
        {
            check(error.AttemptId == FakeFiles.Id && error.State == "partial" &&
                cleanup.Status is { State: "partial", IssueCode: "defaults_cleanup_failed" } && cleanup.Deletes == 1,
                "terminal cleanup failure remains partial and retains attempt evidence");
        }
    }

    private static Task Admit(Action launch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        launch();
        return Task.CompletedTask;
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!ready())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(1, deadline.Token);
        }
    }
}
