using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.InterfaceRecovery.Tests;

public static class Program
{
    private static int checks;
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        checks++; Console.WriteLine("PASS: " + name);
    }
    private static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
            UnauthorizedAccessException or JsonException or OperationCanceledException)
        { Check(true, name); return; }
        throw new InvalidOperationException("FAIL: " + name + " was accepted.");
    }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static InterfaceRequest Clone(InterfaceRequest request) => CerebralJson.Read<InterfaceRequest>(CerebralJson.Element(request));

    public static async Task Main(string[] args)
    {
        if (args is ["--recover", var data, var directory, var key, var session, var id])
        {
            var journal = new DesktopInterfaceOutcomeJournal(directory);
            var entry = journal.Read(key, id)!;
            var requests = new InterfaceRequests(new StateStore(data), new EventHub());
            var delivered = 0;
            var result = await DesktopInterfaceOutcomeDelivery.RecoverAsync(journal, entry,
                (requestId, ct) => Task.FromResult(requests.GetForOwner(requestId, session)),
                (completion, ct) => { delivered++; throw new InvalidOperationException("Terminal restart must not redeliver."); },
                CancellationToken.None);
            if (result.Status != "completed" || delivered != 0 || journal.Read(key, id) is not null)
                throw new InvalidOperationException("Restart did not confirm the original stored outcome.");
            Console.WriteLine("RESTART-CONFIRMED-WITHOUT-DELIVERY");
            return;
        }
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-interface-recovery-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { await Run(root); Console.WriteLine($"Interface outcome recovery: {checks} checks passed. Real Windows files, catalog and SQLite; injected transport failures, no native activation, network or inference."); }
        finally
        {
            SqliteConnection.ClearAllPools();
            var allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task Run(string root)
    {
        var project = Path.Combine(root, "project"); var package = Path.Combine(project, "package");
        Directory.CreateDirectory(package);
        var manifest = Path.Combine(package, "desktop-plugin.json");
        File.Copy(typeof(MetadataFixture).Assembly.Location, Path.Combine(package, "fixture.dll"));
        File.Copy(typeof(IDesktopPlugin).Assembly.Location, Path.Combine(package, "Cerebral.Desktop.Plugins.dll"));
        await File.WriteAllTextAsync(manifest, """
            {"schemaVersion":1,"contract":"pegasus.cerebral.desktop","apiVersion":1,"id":"acceptance.outcome","name":"Outcome fixture","version":"1.0.0","description":"Metadata admission only","assembly":"fixture.dll","entryType":"Cerebral.InterfaceRecovery.Tests.MetadataFixture"}
            """);
        var store = new StateStore(Path.Combine(root, "data"));
        var requests = new InterfaceRequests(store, new EventHub());
        var profile = new AgentProfile { Id = "lead", Role = "lead", AllowedRoots = [project],
            Capabilities = ["filesystem.read", "process.execute", "desktop.plugins.manage"],
            AllowedTools = ["file.read", "process.run"] };
        store.Put("agent", profile.Id, profile);
        InterfaceRequest Pending(string name) => requests.CreateForWorkflow(
            new ToolExecutionContext(profile, "chat-" + name, "job-" + name, project), "plugin", manifest);
        var host = new HostInfo("Pegasus Cerebral", "0.2.22", 1, "fixture", "X64", "Windows", true, store.DirectoryPath,
            InstallationId: "fixture-installation");
        var key = DesktopInterfaceOutcomeJournal.ServiceKey(host);
        Check(key == DesktopInterfaceOutcomeJournal.ServiceKey(host with { Version = "next", Name = "restarted-process" }),
            "stable delivery identity survives Worker version/process changes on the same installation and data root");
        Check(key != DesktopInterfaceOutcomeJournal.ServiceKey(host with { DataDirectory = Path.Combine(root, "other-data") }) &&
              key != DesktopInterfaceOutcomeJournal.ServiceKey(host with { InstallationId = "other-installation" }),
            "another installation or database root cannot reuse the delivery identity");
        await Reject(() => Sync(() => DesktopInterfaceOutcomeJournal.ServiceKey(host with { Product = "Other" })),
            "an unverified product identity cannot be bound to outcome recovery");

        var directory = Path.Combine(root, "outcomes");
        var journal = new DesktopInterfaceOutcomeJournal(directory);
        DesktopInterfaceOutcome Reserve(InterfaceRequest request)
        {
            var claim = Guid.NewGuid().ToString("N");
            Check(journal.Reserve(request, claim, key, false), "attempt is durably reserved before Worker claim");
            Check(requests.Claim(request.Id, claim).Claimed, "the original pending request is claimed once");
            return journal.Read(key, request.Id)!;
        }
        InterfacePluginReceipt receipt = null!;
        DesktopInterfaceCompletion Completion(DesktopInterfaceOutcome entry, InterfacePluginReceipt? plugin = null,
            InterfaceResourceReceipt? resource = null) =>
            new(entry.Id, entry.ClaimId, true, null, "Fixture installed.", resource is null ? null : "interface-" + entry.Id,
                plugin ?? receipt!, false, null, resource);
        InterfaceRequest Deliver(DesktopInterfaceCompletion completion) => requests.Complete(completion.Id,
            completion.ClaimId, completion.Success, completion.Error, completion.Message, completion.Resource,
            completion.Plugin, completion.ControlTestsAllowed, completion.FailureStage);

        // Real catalog publication; the metadata fixture throws if activated and is never loaded.
        var catalog = new DesktopPluginCatalog(Path.Combine(root, "catalog"));
        var installed = catalog.Install(manifest);
        receipt = new InterfacePluginReceipt(installed.Id, installed.Version, Path.GetFileName(installed.Directory),
            installed.ManifestPath, [], false);
        var generations = Directory.GetDirectories(Path.GetDirectoryName(installed.Directory)!).Length;
        Check(generations == 1 && !installed.Enabled, "one actual immutable catalog generation is installed disabled without native activation");

        var before = Reserve(Pending("before-commit"));
        before = journal.SaveCompletion(before, Completion(before));
        var attempts = 0;
        await Reject(() => DesktopInterfaceOutcomeDelivery.RecoverAsync(journal, before,
            (id, ct) => Task.FromResult(requests.GetForOwner(id, before.SessionId)),
            (value, ct) => { attempts++; throw new IOException("Injected disconnect before Worker commit."); }, CancellationToken.None),
            "transport failure before Worker commit retains the exact classified outcome");
        Check(attempts == 1 && journal.Read(key, before.Id)?.Completion is not null &&
            requests.GetForOwner(before.Id).Status == "claimed", "unknown delivery does not erase intent or mark the Worker completed");
        var reopened = new DesktopInterfaceOutcomeJournal(directory);
        var recovered = await DesktopInterfaceOutcomeDelivery.RecoverAsync(reopened, reopened.Read(key, before.Id)!,
            (id, ct) => Task.FromResult(new InterfaceRequests(new StateStore(store.DirectoryPath), new EventHub()).GetForOwner(id, before.SessionId)),
            (value, ct) => { attempts++; return Task.FromResult(Deliver(value)); }, CancellationToken.None);
        Check(recovered.Status == "completed" && attempts == 2 && reopened.Read(key, before.Id) is null,
            "fresh journal/store instances deliver the original receipt once and remove only confirmed evidence");

        var after = Reserve(Pending("after-commit"));
        after = journal.SaveCompletion(after, Completion(after));
        var completeEvents = 0;
        await Reject(() => DesktopInterfaceOutcomeDelivery.RecoverAsync(journal, after,
            (id, ct) => Task.FromResult(requests.GetForOwner(id, after.SessionId)),
            (value, ct) => { completeEvents++; Deliver(value); throw new IOException("Injected lost acknowledgement after durable commit."); },
            CancellationToken.None), "lost acknowledgement after actual SQLite completion remains uncertain locally");
        Check(completeEvents == 1 && requests.GetForOwner(after.Id).Status == "completed" && journal.Read(key, after.Id) is not null,
            "the Worker keeps its committed receipt while the desktop retains delivery evidence");

        // Remove the source after completion; exact terminal inspection must not execute or revalidate a package.
        var sourceAssembly = Path.Combine(package, "fixture.dll");
        var movedAssembly = Path.Combine(package, "fixture.saved");
        File.Move(sourceAssembly, movedAssembly);
        await RestartConfirm(store.DirectoryPath, directory, key, after.SessionId, after.Id);
        Check(journal.Read(key, after.Id) is null && requests.GetForOwner(after.Id).Plugin?.Generation == receipt.Generation,
            "a separate process confirms the terminal original receipt without source files or a delivery callback");
        File.Move(movedAssembly, sourceAssembly);

        var entry = Reserve(Pending("mismatch"));
        var mounts = new List<InterfacePluginMountReceipt> { new("sidebar", receipt.Id + ".main") };
        entry = journal.SaveCompletion(entry, Completion(entry, receipt with { Mounts = mounts }));
        mounts.Clear();
        Check(entry.Completion!.Plugin!.Mounts.Count == 1, "saved outcome detaches receipt evidence from caller-owned mutable collections");
        await Reject(() => Sync(() => journal.SaveCompletion(entry, entry.Completion with { Message = "Different result" })),
            "a classified result cannot be overwritten by a new outcome");
        await Reject(() => Sync(() => journal.SaveCompletion(entry, entry.Completion with { ControlTestsAllowed = true })),
            "receipt recovery cannot escalate the original owner control-test consent");
        var reads = 0; var deliveries = 0;
        Task<InterfaceRequest> Read(string id, CancellationToken ct) { reads++; return Task.FromResult(requests.GetForOwner(id, entry.SessionId)); }
        Task<InterfaceRequest> Send(DesktopInterfaceCompletion c, CancellationToken ct) { deliveries++; return Task.FromResult(Deliver(c)); }
        foreach (var variant in new[] { "session", "claim", "fingerprint", "path", "job", "source", "profile", "date", "id" })
        {
            var other = Clone(requests.GetForOwner(entry.Id));
            switch (variant)
            {
                case "session": other.SessionId = "other"; break;
                case "claim": other.ClaimId = "other"; break;
                case "fingerprint": other.PackageFingerprint = new string('a', 64); break;
                case "path": other.Path = Path.Combine(project, "other.json"); break;
                case "job": other.JobId = "other"; break;
                case "source": other.Source = "legacy"; break;
                case "profile": other.ProfileId = "other"; break;
                case "date": other.CreatedAt = other.CreatedAt.AddSeconds(1); break;
                case "id": other.Id = Guid.NewGuid().ToString("N"); break;
            }
            await Reject(() => DesktopInterfaceOutcomeDelivery.RecoverAsync(journal, entry,
                (id, ct) => Task.FromResult(other), Send, CancellationToken.None),
                "mismatched " + variant + " stops receipt delivery without native replay");
        }
        Check(deliveries == 0 && journal.Read(key, entry.Id) is not null, "all binding mismatches preserve the original evidence");
        await Reject(() => DesktopInterfaceOutcomeDelivery.RecoverAsync(journal,
            entry with { Completion = entry.Completion with { Message = "Fabricated" } }, Read, Send, CancellationToken.None),
            "a fabricated callback frame cannot be delivered instead of the saved result");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Reject(() => DesktopInterfaceOutcomeDelivery.RecoverAsync(journal, entry, Read, Send, cancelled.Token),
                "cancellation stops before reading or sending a receipt");
        }
        Check(reads == 0 && deliveries == 0, "changed and cancelled frames produce no service callbacks");
        var actual = Deliver(entry.Completion);
        var different = Clone(actual); different.Result = "Changed terminal result.";
        await Reject(() => DesktopInterfaceOutcomeDelivery.RecoverAsync(journal, entry,
            (id, ct) => Task.FromResult(different), Send, CancellationToken.None),
            "a differing terminal receipt cannot clear the retained evidence");
        await DesktopInterfaceOutcomeDelivery.RecoverAsync(journal, entry,
            (id, ct) => Task.FromResult(actual), Send, CancellationToken.None);
        Check(deliveries == 0 && journal.Read(key, entry.Id) is null, "matching terminal state confirms through read only");

        var unclassifiedRequest = Pending("unclassified");
        var unknown = Reserve(unclassifiedRequest);
        await Reject(() => DesktopInterfaceOutcomeDelivery.RecoverAsync(journal, unknown, Read, Send, CancellationToken.None),
            "a crash window without classified native evidence cannot be guessed or replayed");
        Check(!journal.Reserve(unclassifiedRequest, unknown.ClaimId, key, false),
            "same pending admission cannot acquire another effect through its retained intent");
        await Reject(() => Sync(() => journal.Reserve(unclassifiedRequest, "different-claim", key, false)),
            "another claim cannot replace the uncertain native attempt");
        Check(reads == 0 && deliveries == 0 && journal.Read(key, unknown.Id)?.Completion is null,
            "unclassified intent remains visible and unexecuted");

        var failed = Reserve(Pending("failed"));
        var failure = new DesktopInterfaceCompletion(failed.Id, failed.ClaimId, false, "Fixture capture failed.",
            null, null, null, false, InterfaceFailureStage.ViewCapture);
        failed = journal.SaveCompletion(failed, failure);
        var failureResult = await DesktopInterfaceOutcomeDelivery.RecoverAsync(journal, failed,
            (id, ct) => Task.FromResult(requests.GetForOwner(id, failed.SessionId)),
            (value, ct) => Task.FromResult(Deliver(value)), CancellationToken.None);
        Check(failureResult.Status == "failed" && failureResult.FailureStage == InterfaceFailureStage.ViewCapture &&
            journal.Read(key, failed.Id) is null, "classified failure keeps its original phase and error through receipt recovery");

        var capture = Reserve(Pending("capture"));
        var resource = new InterfaceResourceReceipt(Guid.NewGuid().ToString("N"), 1, "image/png", "fixture.png", 42);
        capture = journal.SaveCompletion(capture, Completion(capture, resource: resource));
        var captured = Deliver(capture.Completion!);
        var wrongResource = Clone(captured); wrongResource.Resource = resource with { Revision = 2 };
        await Reject(() => Sync(() => journal.Confirm(capture, wrongResource)),
            "immutable attachment coordinate is part of exact confirmation");
        journal.Confirm(capture, captured);
        Check(journal.Read(key, capture.Id) is null, "the exact attachment and installed generation acknowledge one saved outcome");
        Check(Directory.GetDirectories(Path.GetDirectoryName(installed.Directory)!).Length == generations,
            "all delivery/recovery paths leave the actual catalog generation count unchanged");

        await StorageChecks(root, key, manifest);
    }

    private static async Task RestartConfirm(string data, string directory, string key, string session, string id)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("No executable identity.");
        var start = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in new[] { "--recover", data, directory, key, session, id }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Test child did not start.");
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } throw; }
        Check(child.ExitCode == 0 && (await output).Contains("RESTART-CONFIRMED-WITHOUT-DELIVERY", StringComparison.Ordinal),
            "actual child process reopens SQLite/journal and verifies terminal state without redelivery: " + await error);
    }

    private static async Task StorageChecks(string root, string key, string manifest)
    {
        var directory = Path.Combine(root, "storage"); var journal = new DesktopInterfaceOutcomeJournal(directory);
        InterfaceRequest Request(string chat = "quota") => new() { SessionId = chat, JobId = "job", ProfileId = "lead",
            Kind = "plugin", Path = manifest, PackageFingerprint = new string('f', 64) };
        var request = Request("storage");
        Check(journal.Reserve(request, "claim", key, false), "storage fixture reserves one bounded canonical entry");
        var entry = journal.Read(key, request.Id)!;
        var path = Path.Combine(directory, key + "." + request.Id + ".json");
        var original = await File.ReadAllTextAsync(path);
        foreach (var mutation in new[] {
            original.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            original.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"unexpected\":true", StringComparison.Ordinal),
            original.Replace("\"schemaVersion\":1", "\"schemaVersion\":9", StringComparison.Ordinal),
            original.Replace("\"sessionId\":\"storage\"", "\"sessionId\":null", StringComparison.Ordinal),
            new string(' ', DesktopInterfaceOutcomeJournal.MaximumDocumentBytes + 1), "{}" })
        {
            await File.WriteAllTextAsync(path, mutation);
            await Reject(() => Sync(() => journal.Read(key, request.Id)), "damaged, ambiguous or oversized evidence fails closed");
        }
        await File.WriteAllTextAsync(path, original);
        var stage = Path.Combine(directory, ".outcome-stage-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(stage, "Interrupted, non-authoritative staged evidence.");
        var classified = journal.SaveCompletion(entry, new(entry.Id, entry.ClaimId, false, "Fixture outcome.",
            null, null, null, false, InterfaceFailureStage.PackageInspection));
        Check(!File.Exists(stage) && classified.Completion is not null,
            "completion writes clean bounded orphan stages without adopting incomplete staged evidence");
        // A classified result cannot be discarded as unstarted; use an independent pending intent below.
        await Reject(() => Sync(() => journal.DiscardUnstarted(classified)),
            "classified evidence requires exact confirmation and cannot be dropped as an unstarted attempt");
        var confirmed = Request("storage");
        confirmed.Id = classified.Id; confirmed.ClaimId = classified.ClaimId; confirmed.CreatedAt = classified.CreatedAt;
        confirmed.Status = "failed"; confirmed.Error = "Fixture outcome."; confirmed.FailureStage = InterfaceFailureStage.PackageInspection;
        journal.Confirm(classified, confirmed);
        request = Request("storage");
        journal.Reserve(request, "claim", key, false);
        entry = journal.Read(key, request.Id)!;
        path = Path.Combine(directory, key + "." + request.Id + ".json");
        using (var held = new FileStream(Path.Combine(directory, ".delivery.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Reject(() => Sync(() => journal.Read(key, request.Id)), "a live OS-held delivery lease blocks another observer");
        Check(journal.Read(key, request.Id) == entry, "releasing the OS handle restores access without removing the empty lock file");
        var fileName = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".json");
        File.Move(path, fileName);
        await Reject(() => Sync(() => journal.List(key, request.SessionId)), "moved evidence cannot select another request ID");
        File.Move(fileName, path);
        Check(journal.List(key, "another-chat").Count == 0, "chat filtering never exposes another chat's delivery card");
        journal.DiscardUnstarted(entry);
        Check(journal.Read(key, request.Id) is null, "an explicitly unstarted intent can be removed without manufacturing a completion");
        for (var i = 0; i < DesktopInterfaceOutcomeJournal.MaximumEntries; i++)
            if (!journal.Reserve(Request(), "claim", key, false)) throw new InvalidOperationException("Quota fixture collision.");
        var overflow = Request();
        await Reject(() => Sync(() => journal.Reserve(overflow, "claim", key, false)),
            "128 unresolved outcomes block a new attempt before claim/effect without deleting older evidence");
        Check(Directory.GetFiles(directory, "*.json").Length == 128 && journal.Read(key, overflow.Id) is null &&
              journal.List(key, "quota").Count == 8, "entry and card bounds remain explicit after quota rejection");
    }
}

// Compiled metadata only. Any accidental native activation fails the test instead of claiming a runtime pass.
public sealed class MetadataFixture : IDesktopPlugin
{
    public void Initialize(IDesktopPluginHost host) => throw new InvalidOperationException("Never activate this fixture.");
    public IReadOnlyList<DesktopPanel> Panels => throw new InvalidOperationException("Metadata fixture.");
    public IReadOnlyList<DesktopAction> ComposerActions => throw new InvalidOperationException("Metadata fixture.");
    public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => throw new InvalidOperationException("Metadata fixture.");
    public void Dispose() => throw new InvalidOperationException("Metadata fixture.");
}
