using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

/// <summary>Explicit real native-provider acceptance against one fresh isolated source harness.
/// This helper never launches a fixture, resolves installed credentials, loads a model, or uses an MCP fallback.</summary>
internal static class NativeContinuityInteropProbe
{
    private const string CortexRoot = @"C:\Development\Pegasus-Cortex";
    private const string CerebralRoot = @"C:\Development\Pegasus-Cerebral";
    private const string Space = "project.cerebral-interop";
    private const string PrivateAssignment = "PRIVATE_ASSIGNMENT_SENTINEL";
    private const string PrivateState = "PRIVATE_STATE_SENTINEL";

    public static async Task<int> RunAsync(string descriptorPath)
    {
        var phase = "descriptor";
        object? coordinatorState = null;
        object? recoveryChecks = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows owner-session fixture required.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await using var admitted = await AdmitDescriptorAsync(descriptorPath, deadline.Token);
            phase = "identity";
            await using var dispatcher = new ToolDispatcher(() => admitted.Host);
            var report = await dispatcher.InspectCortexAsync("cortex", deadline.Token);
            Require(report.State == "ready" && report.Transport == "cortex-native-pipe" && report.McpIdentity is null &&
                report.Network == false && report.NativeIdentity is { Network: false } identity &&
                identity.Contract == CortexNativeProtocol.Contract && identity.ClientId == "cerebral" &&
                identity.OwnerSid == admitted.Owner.AccountSid && identity.Session == admitted.Owner.SessionId &&
                identity.StorageKind == "sqlite-v2" && identity.State == "ready",
                "Authenticated fixture identity/catalog/status mismatch.");
            var checks = 0;
            void Check(bool value, string label)
            {
                Require(value, label); checks++; Console.WriteLine("PASS: " + label);
            }
            Check(true, "fresh isolated descriptor, OS pipe-server identity and authenticated real native hello are verified");

            phase = "admission";
            var workRoot = CreateWorkRoot();
            var fixture = NativeCortexContinuityTests.Fixture.Create(workRoot, "state");
            var host = fixture.Configuration.Get();
            host.McpServers = admitted.Host.McpServers;
            host.CortexNamespaces = [Space]; host.CortexWriteNamespaces = [Space];
            fixture.Configuration.Save(host);
            var consent = fixture.Projects.Get(fixture.ProjectId);
            consent.ReadNamespaces = [Space]; consent.WriteNamespace = Space; consent.AutomaticContinuity = true;
            fixture.Projects.Save(consent);
            fixture.Profile.AutomaticCompaction = false; fixture.Profile.FollowsModelBudgets = false;
            fixture.Profile.AllowedTools = ["mcp.cortex.cortex_start", "mcp.cortex.cortex_task", "mcp.cortex.cortex_finish", "mcp.cortex.cortex_read"];
            fixture.Profile.Capabilities = ["mcp.cortex", "cortex.write"];
            fixture.Store.Put("agent", fixture.Profile.Id, fixture.Profile);
            // Use actual discovered schemas and the production dispatcher for admission and all coordinator calls.
            var observed = new ObservedTools(dispatcher);
            var snapshot = await WorkerExecutionSnapshot.CaptureAsync(fixture.Store, fixture.Profile,
                fixture.Configuration, observed, [], deadline.Token, projectId: fixture.ProjectId);
            var frozen = WorkerExecutionSnapshot.Read(snapshot);
            Check(frozen.NativeContinuity?.ProjectSettingsRevision == fixture.Projects.Get(fixture.ProjectId).Revision &&
                frozen.CortexScope is { WriteNamespace: Space } scope && scope.ReadNamespaces.SequenceEqual([Space]) &&
                frozen.Tools.Count == 4 && observed.Calls.Count == 0,
                "real schema discovery admits only the explicit frozen project consent and lifecycle/read grants");

            var conversationId = "native-continuity-" + Guid.NewGuid().ToString("N");
            var runId = "turn-" + Guid.NewGuid().ToString("N");
            var objective = conversationId + " " + string.Concat(Enumerable.Repeat("Public isolated objective café Ω 漢字. ", 400));
            var run = fixture.Submit(runId, conversationId, settingsSnapshot: snapshot, userText: objective);
            var manager = new NativeCortexContinuity(fixture.Store, fixture.Configuration, observed, new(),
                fixture.Workflows, fixture.Journal);

            phase = "started";
            manager.Observe(run);
            await DrainAsync(manager, deadline.Token);
            coordinatorState = CoordinatorDiagnostic(manager, conversationId);
            Require(manager.Get(conversationId) is { TaskReady: true, PendingOperation: null } confirmed1 &&
                confirmed1.TaskRevision == 1, "Coordinator must confirm revision1 before exact provider reads.");
            var started = await ExactTaskAsync(observed, fixture, run, expectedRevision: 1, deadline.Token);
            Check(manager.Get(conversationId) is { TaskRevision: 1, TaskReady: true, PendingOperation: null } &&
                started.GetProperty("content").GetString()!.Contains(conversationId, StringComparison.Ordinal) &&
                observed.Calls.Count(IsMutation) == 1 &&
                observed.Calls.Single(IsMutation).Call.Arguments.GetProperty("action").GetString() == "create",
                "the host-owned start/create milestone commits the actual conversation task once at revision1");

            phase = "waiting";
            run = fixture.Workflows.TryClaim(run.Id)!;
            run = fixture.Workflows.ApplyStep(run.Id, run.Revision,
                new(CerebralJson.Element(new { privateState = PrivateState }),
                    new AskWorkflowQuestions([new("answer", "Isolated synthetic owner question")])));
            manager.Observe(run);
            await DrainAsync(manager, deadline.Token);
            coordinatorState = CoordinatorDiagnostic(manager, conversationId);
            Require(manager.Get(conversationId) is { TaskReady: true, PendingOperation: null } confirmed2 &&
                confirmed2.TaskRevision == 2, "Coordinator must confirm revision2 before exact provider reads.");
            var waiting = await ExactTaskAsync(observed, fixture, run, expectedRevision: 2, deadline.Token);
            Check(waiting.GetProperty("checkpointSubject").GetString() == "Cerebral waiting-user" &&
                waiting.GetProperty("phaseId").GetString() == run.Id && manager.Get(conversationId)?.TaskRevision == 2,
                "a canonical waiting milestone updates the same real task to revision2 while it stays open");

            phase = "completed";
            var questions = run.Questions!;
            run = fixture.Workflows.Answer(new("answer-" + Guid.NewGuid().ToString("N"), run.Id, questions.Id,
                questions.Revision, new Dictionary<string, string> { ["answer"] = "Fixture answer" }));
            run = fixture.Complete(run, new { result = "MODEL_REPORTED_RESULT_SENTINEL" });
            manager.Observe(run);
            await DrainAsync(manager, deadline.Token);
            coordinatorState = CoordinatorDiagnostic(manager, conversationId);
            Require(manager.Get(conversationId) is { TaskReady: true, PendingOperation: null } confirmed3 &&
                confirmed3.TaskRevision == 3, "Coordinator must confirm revision3 before exact provider reads.");
            var completed = await ExactTaskAsync(observed, fixture, run, expectedRevision: 3, deadline.Token);
            Check(run.Status == WorkflowStatus.Completed && completed.GetProperty("checkpointSubject").GetString() == "Cerebral completed" &&
                completed.GetProperty("content").GetString()!.Contains("MODEL_REPORTED_RESULT_SENTINEL", StringComparison.Ordinal) &&
                completed.GetProperty("content").GetString()!.Contains("Model-reported result (verify against service evidence)", StringComparison.Ordinal) &&
                manager.Get(conversationId)?.TaskRevision == 3 && observed.Calls.Count(IsMutation) == 3,
                "terminal finish updates the ongoing task to revision3 with an explicitly labeled synthetic result");

            var sent = string.Join("", observed.Calls.Select(item => item.Call.Arguments.GetRawText()));
            var mutations = observed.Calls.Where(IsMutation).ToArray();
            Check(!sent.Contains(PrivateAssignment, StringComparison.Ordinal) && !sent.Contains(PrivateState, StringComparison.Ordinal) &&
                mutations.All(item => item.Context is { FullAccess: false, EnforceProfileScope: true } &&
                    item.Context.Cortex is { KnowledgeWriteApproved: false, WriteNamespace: Space } &&
                    Text(item.Call.Arguments, "namespace") == Space && Text(item.Call.Arguments, "taskId") == conversationId) &&
                mutations.All(BoundedMutation) &&
                mutations.Count(item => item.Call.Name == "mcp.cortex.cortex_finish" &&
                    Text(item.Call.Arguments, "taskAction") == "update" &&
                    item.Call.Arguments.GetProperty("knowledge").GetArrayLength() == 0) == 2,
                "actual sent mutations remain bounded, task-only and isolated without exporting private assignment/state");

            phase = "recovery";
            var before = manager.Get(conversationId)!;
            var lastOperationId = before.LastOperation!.Id;
            var payloadHash = before.LastOperation.PayloadHash;
            // A fresh dispatcher forces another authenticated native connection; it does not replay any prior call.
            await using var freshDispatcher = new ToolDispatcher(() => admitted.Host);
            var freshObserved = new ObservedTools(freshDispatcher);
            var freshState = new StateStore(fixture.Store.DirectoryPath);
            var freshConfiguration = new Configuration(freshState);
            var freshJournal = new EventJournal(Path.Combine(freshState.DirectoryPath, "engine.db"));
            var freshWorkflows = new WorkflowStore(freshJournal);
            var recovered = new NativeCortexContinuity(freshState, freshConfiguration, freshObserved, new(), freshWorkflows, freshJournal);
            recovered.Recover();
            await DrainAsync(recovered, deadline.Token);
            coordinatorState = CoordinatorDiagnostic(recovered, conversationId);
            var recoveredState = recovered.Get(conversationId);
            var recoveredRun = freshWorkflows.Get(run.Id);
            var noProviderCalls = freshObserved.Calls.Count == 0;
            var sameTaskRevision = recoveredState?.TaskRevision == 3;
            var noPendingOperation = recoveredState is not null && recoveredState.PendingOperation is null;
            var noPendingMilestones = recoveredState?.Pending.Count == 0;
            var sameOperationId = recoveredState?.LastOperation?.Id == lastOperationId;
            var samePayloadHash = recoveredState?.LastOperation?.PayloadHash == payloadHash;
            var sameTerminalStatus = recoveredRun.Status == WorkflowStatus.Completed;
            var acceptedRaw = snapshot.GetRawText();
            var recoveredRaw = recoveredRun.Submission.SettingsSnapshot.GetRawText();
            var sameSnapshotRaw = recoveredRaw == acceptedRaw;
            var sameSnapshotSemantic = JsonNode.DeepEquals(JsonNode.Parse(recoveredRaw), JsonNode.Parse(acceptedRaw));
            recoveryChecks = new
            {
                noProviderCalls, sameTaskRevision, noPendingOperation, noPendingMilestones,
                sameOperationId, samePayloadHash, sameTerminalStatus, sameSnapshotRaw, sameSnapshotSemantic,
                providerCallCount = freshObserved.Calls.Count,
                acceptedSnapshotBytes = Bytes(acceptedRaw), recoveredSnapshotBytes = Bytes(recoveredRaw),
                acceptedSnapshotHasNewline = acceptedRaw.Contains('\n'), recoveredSnapshotHasNewline = recoveredRaw.Contains('\n')
            };
            Check(noProviderCalls && sameTaskRevision && noPendingOperation && noPendingMilestones &&
                sameOperationId && samePayloadHash && sameTerminalStatus && sameSnapshotSemantic,
                "fresh local coordinator recovery preserves confirmed milestones and canonical state without duplicate provider effects");
            var afterReconnect = await ExactTaskAsync(freshObserved, fixture, run, expectedRevision: 3, deadline.Token);
            Check(afterReconnect.GetRawText() == completed.GetRawText() && freshObserved.Calls.Count(IsMutation) == 0,
                "fresh authenticated typed get/read retains the exact task body and revision after confirmed-state recovery");

            phase = "evidence";
            var proof = new
            {
                schema = "cerebral.native-continuity-interop/1", success = true, checks,
                isolated = true, network = false, @namespace = Space, conversationId, runId,
                providerProcessId = admitted.Provider.ProcessId, providerVersion = report.NativeIdentity!.Version,
                taskRevisions = new[] { 1, 2, 3 }, finalTaskStatus = "open",
                providerMutationCount = mutations.Length, recoveryMutationCount = freshObserved.Calls.Count(IsMutation),
                transport = "cortex-native-pipe", deterministicSemanticTransport = false, recoveryChecks,
                unknownWriteReplayProof = false, installedProviderProof = false, inference = false
            };
            var proofPath = Path.Combine(workRoot, "proof.json");
            await File.WriteAllBytesAsync(proofPath, JsonSerializer.SerializeToUtf8Bytes(proof, CerebralJson.Options), deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, checks, proofPath,
                isolated = true, installedProviderProof = false, unknownWriteReplayProof = false, inference = false }));
            return 0;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Never echo a provider body, raw request, protected credential or arbitrary exception message.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, phase, error = error.GetType().Name,
                assertion = error is ProbeAssertionException assertion ? assertion.Code : null, coordinatorState, recoveryChecks,
                isolated = true, installedProviderProof = false, unknownWriteReplayProof = false }));
            return 3;
        }
    }

    private sealed record Admission(HostSettings Host, InstalledWorkerProcessIdentity Owner, InstalledWorkerProcessObservation Provider,
        FileStream CredentialLease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => CredentialLease.DisposeAsync();
    }

    [SupportedOSPlatform("windows")]
    private static async Task<Admission> AdmitDescriptorAsync(string selected, CancellationToken token)
    {
        var parent = Path.GetFullPath(Path.Combine(CortexRoot, ".tmp", "native-interop"));
        Require(Path.IsPathFullyQualified(selected) && selected == Path.GetFullPath(selected), "Canonical absolute fixture descriptor required.");
        var path = Path.GetFullPath(selected);
        var root = Path.GetDirectoryName(path)!;
        var id = Path.GetFileName(root);
        Require(PathEquals(Path.GetDirectoryName(root)!, parent) && Path.GetFileName(path) == "endpoint.json" &&
            id.Length == 32 && id.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c)) && Guid.TryParseExact(id, "N", out _),
            "Only the exact fresh native-interop descriptor is admitted.");
        ScopedFilePolicy.RejectLinks(path);
        Require(File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddSeconds(-900) &&
            File.GetLastWriteTimeUtc(path) <= DateTime.UtcNow.AddSeconds(5), "Stale fixture descriptor rejected.");
        var descriptor = await ReadJsonAsync(path, token);
        var owner = InstalledWorkerWindows.ReadCurrentProcessIdentity();
        Require(owner.SessionId > 0 && owner.AccountSid is not (InstalledWorkerAdmission.SystemSid or InstalledWorkerBinding.LocalServiceSid),
            "Signed-in owner session required.");
        var executable = Text(descriptor, "expectedHostExecutable");
        var keyFile = Path.Combine(root, "connections", "cerebral.key");
        Require(descriptor.GetProperty("ok").ValueKind == JsonValueKind.True &&
            Text(descriptor, "schema") == "pegasus.cortex.native-interop/1" &&
            descriptor.GetProperty("isolated").ValueKind == JsonValueKind.True &&
            descriptor.GetProperty("network").ValueKind == JsonValueKind.False && descriptor.GetProperty("pao").ValueKind == JsonValueKind.False &&
            Text(descriptor, "contract") == CortexNativeProtocol.Contract && Text(descriptor, "fixtureNamespace") == Space &&
            Text(descriptor, "clientId") == "cerebral" && Text(descriptor, "personalProfile") == "not_loaded" &&
            Text(descriptor, "ownerSid") == owner.AccountSid && descriptor.GetProperty("session").GetInt32() == owner.SessionId &&
            PathEquals(Text(descriptor, "root"), root) && PathEquals(Text(descriptor, "credentialFile"), keyFile) &&
            PathEquals(Text(descriptor, "credentialDirectory"), Path.Combine(root, "connections")) &&
            PathEquals(Text(descriptor, "databasePath"), Path.Combine(root, "data", "cortex-v2.db")) &&
            Text(descriptor, "nativePipe") == "pegasus-cortex-interop-" + id &&
            descriptor.GetProperty("maximumRequestBytes").GetInt32() == CortexNativeProtocol.RequestBytes &&
            descriptor.GetProperty("maximumResponseBytes").GetInt32() == CortexNativeProtocol.ResponseBytes &&
            descriptor.GetProperty("lifetimeSeconds").GetInt32() is >= 1 and <= 900 &&
            Path.IsPathFullyQualified(executable) && Within(executable, CortexRoot) &&
            Path.GetFileName(executable) == "PegasusCortex.Mcp.Local.Tests.exe",
            "Fixture metadata must exactly bind the isolated source host, namespace and private fixture key path.");
        ScopedFilePolicy.RejectLinks(executable); ScopedFilePolicy.RejectLinks(keyFile);
        ScopedFilePolicy.RejectLinks(Path.Combine(root, "data", "cortex-v2.db"));
        Require(File.Exists(executable) && File.Exists(keyFile), "Exact fixture source host and protected fixture credential must exist.");
        CheckSingleLinkFile(executable, FileShare.Read);
        CheckSingleLinkFile(Path.Combine(root, "data", "cortex-v2.db"), FileShare.ReadWrite);
        var readyPath = Path.Combine(root, "ready.json"); ScopedFilePolicy.RejectLinks(readyPath);
        var ready = await ReadJsonAsync(readyPath, token);
        var expires = ready.GetProperty("expiresUtc").GetDateTimeOffset();
        Require(ready.GetProperty("isolated").ValueKind == JsonValueKind.True &&
            Text(ready, "nativePipe") == Text(descriptor, "nativePipe") && PathEquals(Text(ready, "expectedHostExecutable"), executable) &&
            expires > DateTimeOffset.UtcNow && expires <= DateTimeOffset.UtcNow.AddSeconds(905),
            "An unexpired matching fixture readiness record is required.");
        InstalledWorkerProcessObservation observed;
        // Observe the real pipe peer before the production client may read the fixture DPAPI file.
        // No hello, credential or semantic request is sent on this short identity-only connection.
        await using (var pipe = await InstalledWorkerWindows.ConnectClientAsync(Text(descriptor, "nativePipe"), token))
            observed = InstalledWorkerWindows.ObserveServerProcess(pipe);
        Require(observed.ProcessId == ready.GetProperty("pid").GetInt32() && observed.AccountSid == owner.AccountSid &&
            observed.SessionId == owner.SessionId && PathEquals(observed.Executable, executable) &&
            DateTime.FromFileTimeUtc(observed.CreationTimeFileTime) >= DateTime.UtcNow.AddSeconds(-900),
            "Actual pipe-server PID-derived source executable, owner and session must match the fresh fixture.");
        var native = new CortexNativeConnection(executable, keyFile, owner.SessionId, PipeName: Text(descriptor, "nativePipe"));
        native.Validate();
        var registration = new McpRegistration { Id = "cortex", Executable = executable, Enabled = true, ViaUserConnector = false, CortexNative = native };
        FileStream? credentialLease = null;
        try
        {
            // Inspect metadata only. Keep this read-share lease until all authenticated clients are disposed,
            // preventing replacement/writes while the real client alone reads/decrypts the fixture credential.
            credentialLease = new(keyFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            RequireSingleLink(credentialLease);
            Require(credentialLease.Length is >= 1 and <= 4096, "Protected fixture credential exceeds its bound.");
            return new(new HostSettings { McpServers = [registration], CortexNamespaces = [Space], CortexWriteNamespaces = [Space] },
                owner, observed, credentialLease);
        }
        catch
        {
            if (credentialLease is not null) await credentialLease.DisposeAsync();
            throw;
        }
    }

    private static string CreateWorkRoot()
    {
        var parent = Path.GetFullPath(Path.Combine(CerebralRoot, ".tmp", "native-continuity-interop"));
        ScopedFilePolicy.RejectLinks(parent);
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Require(!Directory.Exists(root) && !File.Exists(root), "Acceptance state must be fresh.");
        Directory.CreateDirectory(root); ScopedFilePolicy.RejectLinks(root);
        return root;
    }

    private static async Task<JsonElement> ExactTaskAsync(ObservedTools tools, NativeCortexContinuityTests.Fixture fixture,
        WorkflowRun run, int expectedRevision, CancellationToken token)
    {
        var scope = WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).CortexScope!;
        var context = new ToolExecutionContext(fixture.Profile, run.Submission.ConversationId, run.Id,
            Cortex: scope, FullAccess: false) { EnforceProfileScope = true };
        var get = Body(await tools.ExecuteAsync(new("mcp.cortex.cortex_task", CerebralJson.Element(new
        { action = "get", @namespace = Space, taskId = run.Submission.ConversationId, maxUtf8Bytes = 4096 })), context, token));
        var summary = get.GetProperty("task");
        Require(Text(summary, "namespace") == Space && Text(summary, "taskId") == run.Submission.ConversationId &&
            summary.GetProperty("revision").GetInt32() == expectedRevision && Text(summary, "status") == "open" &&
            get.GetProperty("effectiveNamespaces").EnumerateArray().Select(x => x.GetString()).SequenceEqual([Space]),
            "Exact typed task get must confirm its isolated coordinate, revision and ongoing status.");
        var read = Body(await tools.ExecuteAsync(new("mcp.cortex.cortex_read", CerebralJson.Element(new
        { @namespace = Space, recordType = "task", recordId = run.Submission.ConversationId, revision = expectedRevision, maxUtf8Bytes = 8192 })), context, token));
        var record = read.GetProperty("record");
        Require(Text(read, "recordType") == "task" && Text(read, "recordId") == run.Submission.ConversationId &&
            read.GetProperty("revision").GetInt32() == expectedRevision &&
            read.GetProperty("effectiveNamespaces").EnumerateArray().Select(x => x.GetString()).SequenceEqual([Space]) &&
            Text(record, "namespace") == Space && Text(record, "taskId") == run.Submission.ConversationId &&
            record.GetProperty("revision").GetInt32() == expectedRevision && Text(record, "status") == "open",
            "Exact typed task read must confirm the full stored record at its expected revision.");
        return record;
    }

    private static object CoordinatorDiagnostic(NativeCortexContinuity coordinator, string conversationId)
    {
        var state = coordinator.Get(conversationId);
        // Only host-owned enum-like states and receipt coordinates are diagnostic. Never export Message or Arguments.
        return new
        {
            status = state?.Status switch
            {
                null => "absent",
                "queued" or "attempting" or "uncertain" or "unavailable" or "checkpointed" or "checkpointed-old-binding" => state.Status,
                _ => "unsupported"
            },
            taskRevision = state?.TaskRevision, taskReady = state?.TaskReady,
            pendingOperation = state?.PendingOperation is not null,
            lastConfirmedRevision = state?.LastOperation?.ResultRevision
        };
    }

    private static async Task DrainAsync(NativeCortexContinuity coordinator, CancellationToken token)
    {
        for (var i = 0; i < 12; i++) if (!await coordinator.ProcessNextAsync(token)) return;
        throw new InvalidOperationException("Isolated coordinator exceeded its bounded maintenance pass.");
    }
    private static bool BoundedMutation(RecordedCall item)
    {
        var args = item.Call.Arguments;
        if (Encoding.UTF8.GetByteCount(args.GetRawText()) > 16_384) return false;
        return item.Call.Name == "mcp.cortex.cortex_finish"
            ? Bytes(Text(args, "taskContent")) <= 4096 && Bytes(Text(args, "evidence")) <= 2000
            : Bytes(Text(args, "title")) <= 180 && Bytes(Text(args, "content")) <= 4096;
    }
    private static bool IsMutation(RecordedCall item) => CortexOperationPolicy.IsMutation(item.Call.Name, item.Call.Arguments);
    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);
    private static JsonElement Body(ToolResult result)
    {
        Require(result.Success && !result.Truncated && Bytes(result.Content) <= CortexNativeProtocol.ResponseBytes, "Complete bounded provider reply required.");
        using var document = JsonDocument.Parse(result.Content);
        var root = document.RootElement;
        Require(!root.TryGetProperty("isError", out var error) || error.ValueKind == JsonValueKind.False, "Provider semantic error rejected.");
        return root.GetProperty("structuredContent").Clone();
    }
    private static async Task<JsonElement> ReadJsonAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        RequireSingleLink(file);
        Require(file.Length is >= 1 and <= 16_384, "Fixture metadata exceeds its bound.");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, token);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
        RejectDuplicateProperties(document.RootElement);
        Require(document.RootElement.ValueKind == JsonValueKind.Object, "Fixture metadata object required.");
        return document.RootElement.Clone();
    }
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "Duplicate fixture metadata property rejected.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private static void CheckSingleLinkFile(string path, FileShare sharing)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, sharing);
        RequireSingleLink(file); // This helper never reads credential, database or executable bytes.
    }
    private static void RequireSingleLink(FileStream file)
    {
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var info))
            throw new IOException("Fixture file metadata could not be verified.");
        Require(info.NumberOfLinks == 1 && (info.Attributes & (uint)FileAttributes.ReparsePoint) == 0,
            "Linked fixture files are not permitted.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ??
        throw new InvalidDataException("Required public fixture field absent.");
    private static bool PathEquals(string a, string b) => Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b) &&
        Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private sealed class ProbeAssertionException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
    private static void Require(bool value, string code) { if (!value) throw new ProbeAssertionException(code); }

    private sealed record RecordedCall(ToolCall Call, ToolExecutionContext Context);
    private sealed class ObservedTools(ToolDispatcher inner) : IToolDispatcher
    {
        public bool FullAccessActive => inner.FullAccessActive;
        public List<RecordedCall> Calls { get; } = [];
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) => inner.ListAsync(profile, token);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            Require(Calls.Count < 32, "Acceptance call bound reached.");
            Calls.Add(new(call with { Arguments = call.Arguments.Clone() }, context));
            return inner.ExecuteAsync(call, context, token);
        }
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context, ToolExecutionEvidence? previousEvidence, CancellationToken token) =>
            throw new InvalidOperationException("This probe does not reconcile or replay uncertain effects.");
    }
}
