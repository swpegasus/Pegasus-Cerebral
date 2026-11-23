using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Orchestrator;

internal static class DesktopControlNormalChecks
{
    internal static async Task<int> RunAsync(OrchestratorWindow window, string root)
    {
        var checks = IdentityAdmissionChecks();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        bool Fields(JsonElement value, params string[] names) => value.ValueKind == JsonValueKind.Object &&
            value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal));
        DesktopControlServer? server = null;
        var captures = new List<string>();
        var originalContent = window.Content;
        try
        {
            server = await DesktopControlServer.StartNormalSemanticAsync(window, root);
            Check(server.DescriptorPath == Path.Combine(root, "Desktop", "control.json"),
                "Normal control must use the explicitly selected user UI root.");
            var leasedEndpoint = await DesktopControlIdentity.ReadEndpointAsync(server.DescriptorPath, CancellationToken.None);
            Check(leasedEndpoint == server.Endpoint,
                "The real normal descriptor must be readable while its Write/DeleteOnClose publication lease remains open.");
            var writerRefused = false;
            try
            {
                using var writer = new FileStream(server.DescriptorPath, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) { writerRefused = true; }
            var deletionRefused = false;
            try { File.Delete(server.DescriptorPath); }
            catch (IOException) { deletionRefused = true; }
            Check(writerRefused && deletionRefused && File.Exists(server.DescriptorPath),
                "A compatible descriptor reader must not weaken the publication lease's denial of other writers/deletion.");
            var caps = await Call(server, DesktopControlProtocol.CapabilitiesInspect, new { });
            var mode = caps.Data.GetProperty("desktopControl");
            Check(caps.State == "completed" && mode.GetProperty("mode").GetString() == "normal-semantic" &&
                caps.Data.GetProperty("schema").GetString() == "pegasus.main-control-discovery/1" &&
                mode.GetProperty("protocol").GetInt32() == 2 && caps.Data.GetProperty("endpoint").GetProperty("protocol").GetInt32() == 2 &&
                mode.GetProperty("coverage").GetString() == "bounded-main-subset" &&
                !caps.Data.GetProperty("readiness").GetProperty("connected").GetBoolean(),
                "Normal discovery must advertise its bounded mode and actual disconnected fixture readiness.");
            Check(Fields(caps.Data, "schema", "capturedAtUtc", "readiness", "semanticSelection", "workflowSelection",
                    "surface", "note", "desktopControl", "endpoint"),
                "Normal discovery must retain its metadata envelope without questions, drafts, history or generation-preview bodies.");
            var workflowSelection = caps.Data.GetProperty("workflowSelection");
            var availability = workflowSelection.GetProperty("availability");
            Check(Fields(workflowSelection, "definitionId", "definitionVersion", "origin", "fresh", "selectionAllowed",
                    "detailedToolFeedback", "choiceRevision", "availability", "reason") &&
                (workflowSelection.GetProperty("origin").GetString() is "retained_request" or "accepted_run" or "fresh_choice" or "unavailable") &&
                workflowSelection.EnumerateObject().Where(property => property.Name != "availability")
                    .All(property => property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) &&
                Fields(availability, "current", "exactCurrent", "generation", "choiceGeneration", "installationId", "profileId",
                    "profileRevision", "definitionVersion", "exactDefinitionId", "exactDefinitionVersion") &&
                availability.EnumerateObject().All(property => property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)),
                "Workflow discovery must contain only scalar identity, origin, revision and availability metadata.");
            var admitted = mode.GetProperty("admittedOperations").EnumerateArray().Select(item => item.GetString()).ToArray();
            Check(admitted.SequenceEqual(DesktopControlProtocol.NormalSemanticOperations.Order(StringComparer.Ordinal)) &&
                mode.GetProperty("commands").GetArrayLength() == admitted.Length &&
                mode.GetProperty("unsupportedOperations").EnumerateArray().Any(item => item.GetString() == "interface.owner.review"),
                "Normal discovery must describe exactly its admitted subset and disclose excluded owner review.");
            Check(!DesktopControlServer.ModeCapabilities(false, false).GetProperty("admittedOperations").EnumerateArray()
                    .Any(item => item.GetString() == "conversation.history.back") &&
                !DesktopControlServer.ModeCapabilities(false, true).GetProperty("admittedOperations").EnumerateArray()
                    .Any(item => item.GetString() == "window.capture"),
                "Normal control must not widen source-full history or private-attach capture admission.");
            foreach (var operation in new[] { "configuration.open", "interface.owner.review", "plugin.surface.invoke",
                         "plugin.command", "service-package.activate", "workflow.answer" })
            {
                var refused = await Call(server, operation, new { });
                Check(refused.State == "rejected" && refused.Data.GetProperty("code").GetString() == "operation_not_supported_in_mode",
                    operation + " must refuse before reaching a normal owner action.");
            }
            var before = await Call(server, "snapshot.inspect", new { });
            var expected = before.Data.GetProperty("semanticSelection").Clone();
            Check(JsonElement.DeepEquals(workflowSelection, before.Data.GetProperty("workflowSelection")) &&
                JsonElement.DeepEquals(caps.Data.GetProperty("semanticSelection"), expected),
                "Body-free discovery must identify the same selected workflow and selection token as the existing fixture readback.");
            Check((await Call(server, "composer.set", new { text = "must not apply" })).State == "rejected",
                "Normal mutation must require the inspected exact selection.");
            var stale = expected.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
            stale["selectionVersion"] = CerebralJson.Element(expected.GetProperty("selectionVersion").GetInt64() + 1);
            Check((await Call(server, "composer.set", new { text = "must not apply", expected = stale })).State == "rejected" &&
                (await Call(server, "snapshot.inspect", new { })).Data.GetProperty("composerCharacters").GetInt32() ==
                    before.Data.GetProperty("composerCharacters").GetInt32(),
                "Stale normal selection must preserve the existing unsent draft.");
            var editId = Guid.NewGuid().ToString("N");
            var edit = await Call(server, "composer.set", new { text = "normal fixture", expected }, editId);
            var repeated = await Call(server, "composer.set", new { text = "normal fixture", expected }, editId);
            Check(edit.State == "completed" && JsonSerializer.Serialize(repeated, CerebralJson.Options) == JsonSerializer.Serialize(edit, CerebralJson.Options),
                "An identical normal request retry must return its retained response.");
            Check((await Call(server, "composer.set", new { text = "different fixture", expected }, editId)).State == "conflict",
                "A normal request ID cannot be reused with changed input.");
            var draftCaps = await Call(server, DesktopControlProtocol.CapabilitiesInspect, new { });
            Check(draftCaps.State == "completed" && !draftCaps.Data.GetRawText().Contains("normal fixture", StringComparison.Ordinal) &&
                Fields(draftCaps.Data, "schema", "capturedAtUtc", "readiness", "semanticSelection", "workflowSelection",
                    "surface", "note", "desktopControl", "endpoint"),
                "Discovery with an unsent draft must stay body-free while preserving the existing capabilities envelope.");

            // A forged descriptor is rejected against the connected native pipe process
            // before its requested draft mutation is written to that connection.
            var forgedPath = Path.Combine(root, "forged-control.json");
            await File.WriteAllTextAsync(forgedPath, JsonSerializer.Serialize(server.Endpoint with { ProcessId = server.Endpoint.ProcessId + 1 }, CerebralJson.Options));
            var refusedPeer = false;
            try { _ = await DesktopControlClient.ExecuteAsync(forgedPath, "composer.set", Guid.NewGuid().ToString("N"),
                JsonSerializer.Serialize(new { text = "forged peer", expected }, CerebralJson.Options)); }
            catch (UnauthorizedAccessException) { refusedPeer = true; }
            Check(refusedPeer && (await Call(server, "snapshot.inspect", new { })).Data.GetProperty("composerCharacters").GetInt32() == 14,
                "Forged native process identity must fail before any draft mutation.");

            using (var anonymous = new NamedPipeClientStream(".", server.Endpoint.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Anonymous))
            using (var anonymousDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                var anonymousRefused = false;
                try
                {
                    await anonymous.ConnectAsync(anonymousDeadline.Token);
                    DesktopControlIdentity.RequireServer(anonymous, server.Endpoint);
                    var anonymousRequest = new DesktopControlRequest(server.Endpoint.InstanceId, Guid.NewGuid().ToString("N"),
                        "composer.set", CerebralJson.Element(new { text = "anonymous caller", expected }));
                    await LocalWorkerProtocol.WriteAsync(anonymous, anonymousRequest, anonymousDeadline.Token);
                    var anonymousReply = await LocalWorkerProtocol.ReadAsync<DesktopControlResponse>(anonymous, anonymousDeadline.Token);
                    DesktopControlProtocol.Validate(anonymousReply, anonymousRequest);
                    anonymousRefused = anonymousReply.State == "rejected" && anonymousReply.Data.GetProperty("code").GetString() is
                        "caller_identity_unavailable" or "caller_not_authorized";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Windows/CurrentUserOnly may refuse the anonymous security context
                    // before a framed rejection is possible. Either route must deny effects.
                    anonymousRefused = true;
                }
                Check(anonymousRefused, "A real anonymous-token pipe caller must be refused before any draft mutation.");
            }
            Check((await Call(server, "snapshot.inspect", new { })).Data.GetProperty("composerCharacters").GetInt32() == 14,
                "Rejected anonymous native caller must preserve the selected unsent draft.");

            // Only task-owned mounted WPF content is rendered. This is not native screen,
            // hardware rendering, provider, model, or independent screenshot evidence.
            var view = new Border { Width = 1600, Height = 800, Child = new TextBlock { Text = "Owned capture fixture" } };
            window.Content = view;
            view.Measure(new Size(1600, 800)); view.Arrange(new Rect(0, 0, 1600, 800)); view.UpdateLayout();
            var capture = await Call(server, "window.capture", new { }, "capture-fixture");
            var path = capture.Data.GetProperty("path").GetString()!; captures.Add(path);
            var imageBytes = await File.ReadAllBytesAsync(path);
            Check(capture.State == "completed" && capture.Data.GetProperty("method").GetString() == "wpf-render-target-bitmap" &&
                !capture.Data.GetProperty("nativePixels").GetBoolean() &&
                capture.Data.GetProperty("pixelWidth").GetInt32() == 1024 && capture.Data.GetProperty("pixelHeight").GetInt32() == 512 &&
                capture.Data.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant() &&
                capture.Data.GetProperty("target").GetProperty("processStartUtcTicks").GetString() == server.Endpoint.ProcessStartUtcTicks,
                "WPF capture must report actual scaled PNG dimensions, file hash and exact native process generation.");
            var replayCapture = await Call(server, "window.capture", new { }, "capture-fixture");
            var otherCapture = await Call(server, "window.capture", new { });
            var otherPath = otherCapture.Data.GetProperty("path").GetString()!; captures.Add(otherPath);
            var retainedImageBytes = await File.ReadAllBytesAsync(path);
            Check(replayCapture.Data.GetProperty("path").GetString() == path && otherPath != path &&
                imageBytes.SequenceEqual(retainedImageBytes),
                "Retained capture retries must reuse their immutable file; a new capture must not overwrite it.");

            var descriptor = server.DescriptorPath;
            var originalEndpoint = server.Endpoint;
            await server.DisposeAsync(); server = null;
            Check(!File.Exists(descriptor), "Normal shutdown must withdraw its exact leased descriptor.");
            Directory.CreateDirectory(Path.GetDirectoryName(descriptor)!);
            var foreign = Encoding.UTF8.GetBytes("foreign descriptor fixture");
            await File.WriteAllBytesAsync(descriptor, foreign);
            var collisionRefused = false;
            try { await using var collision = await DesktopControlServer.StartNormalSemanticAsync(window, root); }
            catch (Exception error) when (error is IOException or InvalidDataException) { collisionRefused = true; }
            var collisionDescriptorBytes = await File.ReadAllBytesAsync(descriptor);
            Check(collisionRefused && foreign.SequenceEqual(collisionDescriptorBytes),
                "Normal startup failure must preserve a preexisting foreign descriptor.");
            await CheckStaleDescriptorRecoveryAsync(window, root, DesktopControlIdentity.Shared(originalEndpoint), Check);

            var failureRoot = Path.Combine(root, "listener-failure");
            await using var failedServer = await DesktopControlServer.StartNormalSemanticAsync(window, failureRoot,
                listenerFactory: (name, first) => first
                    ? new NamedPipeServerStream(name, PipeDirection.InOut, 5, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance)
                    : throw new IOException("Owned fixture listener failure."));
            using (var failurePeer = new NamedPipeClientStream(".", failedServer.Endpoint.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                await failurePeer.ConnectAsync(1500);
            var listenerFailed = false;
            try { await failedServer.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (IOException) { listenerFailed = true; }
            Check(listenerFailed && !File.Exists(failedServer.DescriptorPath),
                "An unexpected normal listener failure must withdraw its exact descriptor lease.");
            await File.WriteAllBytesAsync(failedServer.DescriptorPath, foreign);
            await failedServer.DisposeAsync();
            var replacementDescriptorBytes = await File.ReadAllBytesAsync(failedServer.DescriptorPath);
            Check(foreign.SequenceEqual(replacementDescriptorBytes),
                "Disposal after listener failure must not remove a replacement foreign descriptor.");
            return checks;
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            window.Content = originalContent;
            foreach (var capture in captures) File.Delete(capture);
        }
    }

    private static async Task CheckStaleDescriptorRecoveryAsync(OrchestratorWindow window, string root,
        SemanticControlEndpoint current, Action<bool, string> check)
    {
        // The fixture owns a short-lived process so absence is an observed generation,
        // not an arbitrary PID guess or a modification to the installed descriptor.
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        int formerId, formerSession;
        DateTime formerStart;
        using (var former = Process.Start(new ProcessStartInfo(shell, "/d /q")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })
               ?? throw new IOException("The stale-descriptor fixture process did not start."))
        {
            formerId = former.Id;
            formerStart = former.StartTime.ToUniversalTime();
            formerSession = former.SessionId;
            await former.StandardInput.WriteLineAsync("exit 0");
            former.StandardInput.Close();
            await former.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            check(former.ExitCode == 0, "The owned stale-descriptor fixture process must exit normally.");
        }
        var stale = current with
        {
            ProcessId = formerId,
            ProcessStartUtcTicks = formerStart.Ticks.ToString(CultureInfo.InvariantCulture),
            StartedAt = new DateTimeOffset(formerStart),
            SessionId = formerSession
        };

        var baseRoot = Path.Combine(root, "descriptor-recovery");
        static string Descriptor(string selectedRoot)
        {
            var directory = Path.Combine(selectedRoot, "Desktop");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "control.json");
        }
        static byte[] Bytes(SemanticControlEndpoint endpoint) =>
            JsonSerializer.SerializeToUtf8Bytes(endpoint, CerebralJson.Options);
        async Task Refuse(string name, byte[] bytes)
        {
            var selectedRoot = Path.Combine(baseRoot, name);
            var path = Descriptor(selectedRoot);
            await File.WriteAllBytesAsync(path, bytes);
            var refused = false;
            try { await using var candidate = await DesktopControlServer.StartNormalSemanticAsync(window, selectedRoot); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { refused = true; }
            check(refused && File.ReadAllBytes(path).SequenceEqual(bytes) &&
                Directory.GetFiles(Path.GetDirectoryName(path)!, "control.json.stale.*.quarantine").Length == 0,
                name + " must refuse without modifying or quarantining the prior descriptor.");
        }

        var selectedRoot = Path.Combine(baseRoot, "stale-success");
        var oldPath = Descriptor(selectedRoot);
        var oldBytes = Bytes(stale);
        await File.WriteAllBytesAsync(oldPath, oldBytes);
        var oldFileId = FileId(oldPath);
        await using (var recovered = await DesktopControlServer.StartNormalSemanticAsync(window, selectedRoot))
        {
            var published = await DesktopControlIdentity.ReadEndpointAsync(oldPath, CancellationToken.None);
            var preserved = Directory.GetFiles(Path.GetDirectoryName(oldPath)!, "control.json.stale.*.quarantine");
            check(published == recovered.Endpoint && preserved.Length == 1 &&
                File.ReadAllBytes(preserved[0]).SequenceEqual(oldBytes) && FileId(preserved[0]) == oldFileId,
                "Stale recovery must publish the new endpoint and preserve the old file identity and bytes.");
        }
        check(!File.Exists(oldPath) &&
            Directory.GetFiles(Path.GetDirectoryName(oldPath)!, "control.json.stale.*.quarantine").Length == 1,
            "Disposing the new endpoint must leave the preserved old generation intact.");

        await Refuse("live-owner", Bytes(current));
        await Refuse("foreign-product", Bytes(stale with { Product = "foreign-product" }));
        await Refuse("malformed", Encoding.UTF8.GetBytes("not a desktop-control endpoint"));
        var lockedRoot = Path.Combine(baseRoot, "locked");
        var lockedPath = Descriptor(lockedRoot);
        await File.WriteAllBytesAsync(lockedPath, oldBytes);
        using (var lockHandle = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var refused = false;
            try { await using var candidate = await DesktopControlServer.StartNormalSemanticAsync(window, lockedRoot); }
            catch (IOException) { refused = true; }
            check(refused && File.ReadAllBytes(lockedPath).SequenceEqual(oldBytes),
                "A held descriptor must refuse recovery and preserve the original bytes.");
        }

        var streamRoot = Path.Combine(baseRoot, "alternate-stream");
        var streamPath = Descriptor(streamRoot);
        await File.WriteAllBytesAsync(streamPath, oldBytes);
        using (var stream = new FileStream(streamPath + ":fixture", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.WriteByte(1);
        var streamRefused = false;
        try { await using var candidate = await DesktopControlServer.StartNormalSemanticAsync(window, streamRoot); }
        catch (IOException) { streamRefused = true; }
        check(streamRefused && File.ReadAllBytes(streamPath).SequenceEqual(oldBytes),
            "A descriptor with an alternate stream must refuse recovery and retain its bytes.");

        var hardlinkRoot = Path.Combine(baseRoot, "hardlink");
        var hardlinkPath = Descriptor(hardlinkRoot);
        await File.WriteAllBytesAsync(hardlinkPath, oldBytes);
        var otherName = Path.Combine(Path.GetDirectoryName(hardlinkPath)!, "other-link.json");
        if (!CreateHardLink(otherName, hardlinkPath, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The owned hardlink fixture could not be created.");
        var hardlinkRefused = false;
        try { await using var candidate = await DesktopControlServer.StartNormalSemanticAsync(window, hardlinkRoot); }
        catch (IOException) { hardlinkRefused = true; }
        check(hardlinkRefused && File.ReadAllBytes(hardlinkPath).SequenceEqual(oldBytes) &&
            File.ReadAllBytes(otherName).SequenceEqual(oldBytes),
            "A multiply linked descriptor must refuse recovery and preserve both owned links.");

        var symlinkRoot = Path.Combine(baseRoot, "symlink");
        var symlinkPath = Descriptor(symlinkRoot);
        var target = Path.Combine(Path.GetDirectoryName(symlinkPath)!, "target.json");
        await File.WriteAllBytesAsync(target, oldBytes);
        try { _ = File.CreateSymbolicLink(symlinkPath, target); }
        catch (IOException error) when (error.HResult == unchecked((int)0x80070522))
        { Console.WriteLine("Desktop-control symlink refusal: SKIPPED (link privilege unavailable)."); return; }
        catch (UnauthorizedAccessException)
        { Console.WriteLine("Desktop-control symlink refusal: SKIPPED (link privilege unavailable)."); return; }
        var symlinkRefused = false;
        try { await using var candidate = await DesktopControlServer.StartNormalSemanticAsync(window, symlinkRoot); }
        catch (Exception error) when (error is IOException or InvalidDataException) { symlinkRefused = true; }
        check(symlinkRefused && File.ReadAllBytes(target).SequenceEqual(oldBytes),
            "A linked descriptor must refuse recovery and preserve its target.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

    private static string FileId(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var identity = new byte[24]; // FILE_ID_INFO: volume serial and 128-bit file ID.
        if (!GetFileInformationByHandleEx(file.SafeFileHandle, 18, identity, (uint)identity.Length))
            throw new IOException("The owned descriptor file identity is unavailable.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        return Convert.ToHexString(identity);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        [Out] byte[] information, uint length);

    private static int IdentityAdmissionChecks()
    {
        var checks = 0;
        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Desktop native identity admission regression.");
            checks++;
        }
        var instance = Guid.NewGuid().ToString("N");
        var started = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var endpoint = new DesktopControlEndpoint(DesktopControlProtocol.Product, DesktopControlProtocol.Version,
            "Pegasus.Cerebral.Desktop." + instance, instance, 1234, DateTimeOffset.UtcNow,
            started.Ticks.ToString(CultureInfo.InvariantCulture), 7);
        var peer = new InstalledWorkerProcessObservation(1234, "fixture-user", "C:\\app\\Cerebral.Orchestrator.exe", 7, started.ToFileTimeUtc());
        bool Matches(InstalledWorkerProcessObservation observed, string[]? argv = null) => DesktopControlIdentity.PeerMatches(
            endpoint, observed, argv ?? [observed.Executable], "fixture-user", 7,
            "C:\\app\\Cerebral.Orchestrator.exe", "C:\\runtime\\dotnet.exe", "C:\\app\\Cerebral.Orchestrator.dll");
        Check(Matches(peer));
        Check(Matches(peer, ["C:/app/Cerebral.Orchestrator.exe"]));
        Check(!Matches(peer with { ProcessId = 4321 }));
        Check(!Matches(peer with { CreationTimeFileTime = started.AddTicks(1).ToFileTimeUtc() }));
        Check(!Matches(peer with { AccountSid = "other-user" }));
        Check(!Matches(peer with { SessionId = 8 }));
        Check(!Matches(peer with { Executable = "C:\\other\\Cerebral.Orchestrator.exe" }));
        var managed = peer with { Executable = "C:\\runtime\\dotnet.exe" };
        Check(Matches(managed, [managed.Executable, "C:\\app\\Cerebral.Orchestrator.dll"]));
        Check(Matches(managed, ["C:/runtime/dotnet.exe", "C:/app/Cerebral.Orchestrator.dll"]));
        Check(!Matches(managed, [managed.Executable, "C:\\other\\Cerebral.Orchestrator.dll"]));
        Check(!Matches(managed, [managed.Executable, "C:/other/Cerebral.Orchestrator.dll"]));
        Check(!Matches(managed, [managed.Executable, "Cerebral.Orchestrator.dll"]));
        Check(!Matches(managed, [managed.Executable, "C:app\\Cerebral.Orchestrator.dll"]));
        Check(!Matches(managed, [managed.Executable, "\\\\server\\app\\Cerebral.Orchestrator.dll"]));
        Check(!Matches(managed, [managed.Executable, "//server/app/Cerebral.Orchestrator.dll"]));
        Check(!Matches(managed, [managed.Executable, "C:/app/Cerebral.Orchestrator.dll:other"]));
        Check(!Matches(managed, [managed.Executable, "exec", "C:\\app\\Cerebral.Orchestrator.dll"]));
        var caller = new InstalledWorkerClientIdentity("fixture-user", 7, true, false, false);
        bool AdmittedCaller(InstalledWorkerClientIdentity value) => DesktopControlServer.NormalCallerMatches(value, "fixture-user", 7);
        Check(AdmittedCaller(caller));
        Check(!AdmittedCaller(caller with { UserSid = "other-user" }));
        Check(!AdmittedCaller(caller with { SessionId = 8 }));
        Check(!AdmittedCaller(caller with { Authenticated = false }));
        Check(!AdmittedCaller(caller with { Anonymous = true }));
        Check(!AdmittedCaller(caller with { NetworkLogon = true }));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(endpoint, CerebralJson.Options);
        Check(DesktopControlIdentity.ParseEndpoint(bytes) == endpoint);
        foreach (var invalid in new[]
        {
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(endpoint with { Protocol = 1 }, CerebralJson.Options)),
            bytes.Concat(new byte[8192]).ToArray(),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(endpoint, CerebralJson.Options).TrimEnd().TrimEnd('}') + ",\"processId\":1234}"),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(endpoint with { ProcessStartUtcTicks = "" }, CerebralJson.Options)),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(endpoint with { SessionId = -1 }, CerebralJson.Options)),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(endpoint with { PipeName = "unrelated-pipe" }, CerebralJson.Options))
        })
        {
            var refused = false;
            try { _ = DesktopControlIdentity.ParseEndpoint(invalid); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { refused = true; }
            Check(refused);
        }
        return checks;
    }

    private static Task<DesktopControlResponse> Call(DesktopControlServer server, string operation, object arguments, string? requestId = null) =>
        DesktopControlClient.ExecuteAsync(server.DescriptorPath, operation, requestId ?? Guid.NewGuid().ToString("N"),
            JsonSerializer.Serialize(arguments, CerebralJson.Options));
}
