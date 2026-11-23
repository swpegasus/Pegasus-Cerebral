using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Resources;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic archive-provider fixture over real SQLite originals and real derivative files.</summary>
public static class AttachmentArtifactTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-attachment-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var checks = 0; var extracts = 0; var unsafeListing = false;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + message);
            checks++; Console.WriteLine("PASS: " + message);
        }
        try
        {
            var state = new StateStore(root);
            var profile = new AgentProfile { Id = "archive-lead", Name = "Archive lead" };
            state.Put("agent", profile.Id, profile);
            var originals = new AttachmentStore(new EventJournal(Path.Combine(root, "engine.db")));
            var access = new AttachmentAccessContext(profile.Id, "archive-chat");
            var first = await ImportZipAsync(originals, access, "archive-one", "source.zip");

            Task<JsonElement> Pao(JsonElement arguments, string? _, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = arguments.GetProperty("op").GetString();
                if (operation == "7z_list")
                {
                    var path = unsafeListing ? "../escape.txt" : "hello.txt";
                    return Task.FromResult(CerebralJson.Element(new
                    {
                        ok = true, state = "done", op = operation,
                        data = new { count = 1, expanded_bytes = 6, entries = new[] { new { path, directory = false, size = 6 } }, truncated = false }
                    }));
                }
                if (operation == "7z_test") return Task.FromResult(CerebralJson.Element(new { ok = true, state = "done", op = operation, data = new { } }));
                if (operation == "7z_extract")
                {
                    extracts++;
                    var destination = arguments.GetProperty("dst").GetString()!;
                    Directory.CreateDirectory(destination);
                    File.WriteAllText(Path.Combine(destination, "hello.txt"), "hello!", new UTF8Encoding(false));
                    return Task.FromResult(CerebralJson.Element(new { ok = true, state = "done", op = operation, data = new { } }));
                }
                throw new InvalidOperationException("Unexpected fixture operation " + operation);
            }

            using var artifacts = new AttachmentArtifacts(originals, state, Path.Combine(root, "artifacts"), Pao);
            var inspection = await artifacts.InspectAsync(access, first.Id, first.Revision, CancellationToken.None);
            Check(inspection.Entries is [{ Path: "hello.txt", Length: 6, Directory: false }] && inspection.ExpandedBytes == 6,
                "inspection returns a bounded archive identity without host paths");
            var receipt = await artifacts.ExtractAsync(access, first.Id, first.Revision, CancellationToken.None);
            Check(receipt.State == "ready" && receipt.Entries.Count == 1 && receipt.ExpiresAt > receipt.CreatedAt,
                "extraction commits one disposable derivative receipt with expiry");
            var text = await artifacts.ReadTextAsync(access, first.Id, first.Revision, "hello.txt", 32, CancellationToken.None);
            Check(text.Text == "hello!" && text.EntryPath == "hello.txt",
                "one exact derivative member is read as strict UTF-8 with source attribution");
            var projected = await artifacts.ResolveModelTextAsync(access, first, 32, CancellationToken.None);
            Check(projected is [{ EntryPath: "hello.txt", Text: "hello!" }],
                "model projection resolves bounded members while retaining the archive as a separate input reference");
            var replay = await artifacts.ExtractAsync(access, first.Id, first.Revision, CancellationToken.None);
            Check(replay.State == receipt.State && replay.AttachmentId == receipt.AttachmentId &&
                replay.CreatedAt == receipt.CreatedAt && extracts == 1,
                "an exact extraction retry reuses the retained derivative instead of creating versions");
            Check(artifacts.Status(access, first.Id, first.Revision).State == "ready",
                "status reconciles the durable receipt without rerunning 7-Zip");
            Check(!JsonSerializer.Serialize(receipt, CerebralJson.Options).Contains(root, StringComparison.OrdinalIgnoreCase),
                "public derivative receipts disclose no service filesystem paths");

            unsafeListing = true;
            var second = await ImportZipAsync(originals, access, "archive-two", "unsafe.zip");
            var rejected = false;
            try { _ = await artifacts.InspectAsync(access, second.Id, second.Revision, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && !File.Exists(Path.Combine(root, "escape.txt")),
                "unsafe archive paths are rejected before extraction and cannot escape staging");
            Console.WriteLine($"Attachment artifacts: {checks} deterministic archive checks passed.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
        await AttachmentCleanupChecks.RunAsync();
    }

    public static async Task RunDirectBoundaryAsync(string applicationStateDirectory)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-attachment-artifacts-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = new StateStore(root); var configuration = new Configuration(state);
            var profile = new AgentProfile { Id = "archive-live", Name = "Archive live" };
            state.Put("agent", profile.Id, profile);
            var access = new AttachmentAccessContext(profile.Id, "archive-live-chat");
            var originals = new AttachmentStore(new EventJournal(Path.Combine(root, "engine.db")));
            var reference = await ImportZipAsync(originals, access, "archive-live", "live.zip");
            await using var tools = new ToolDispatcher(configuration.Get, stateDirectory: Path.GetFullPath(applicationStateDirectory));
            try
            {
                _ = await tools.EnableFullAccessAsync(CancellationToken.None);
                throw new InvalidOperationException("A standalone test host was incorrectly admitted to the protected Cerebral application pipe.");
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Attachment authorization boundary: a standalone test host was denied as expected; live archive acceptance must use the installed Worker owner route.");
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    public static async Task RunWorkerLiveAsync(string discoveryPath)
    {
        using var discovery = JsonDocument.Parse(await File.ReadAllTextAsync(Path.GetFullPath(discoveryPath)));
        var endpoint = discovery.RootElement.GetProperty("endpoint");
        using var client = new CerebralClient(new LocalWorkerEndpoint(
            endpoint.GetProperty("pipeName").GetString()!, endpoint.GetProperty("instanceId").GetString()!));
        await ExerciseWorkerAsync(client, "source Worker", requiresFullAccess: false);
    }

    public static async Task RunInstalledWorkerLiveAsync(string dataDirectory)
    {
        var state = new StateStore(Path.GetFullPath(dataDirectory));
        var settings = new Configuration(state).Get();
        using var client = new CerebralClient(settings.ListenUrl, settings.AccessKey);
        await ExerciseWorkerAsync(client, "installed Worker", requiresFullAccess: true);
    }

    private static async Task ExerciseWorkerAsync(CerebralClient client, string route, bool requiresFullAccess)
    {
        var profiles = await client.GetAsync<List<AgentProfile>>("agents.list");
        var profile = profiles.FirstOrDefault(x => x.Role == "lead") ?? profiles.FirstOrDefault()
            ?? throw new InvalidOperationException("The " + route + " has no saved profile for attachment acceptance.");
        if (requiresFullAccess)
        {
            var access = await client.CommandAsync("access.full.enable");
            if (!access.Success) throw new InvalidOperationException("The " + route + " could not establish Cortex application authorization: " + access.Error);
        }

        await using var source = new MemoryStream();
        using (var archive = new ZipArchive(source, ZipArchiveMode.Create, leaveOpen: true))
        {
            var member = archive.CreateEntry("hello.txt");
            await using var writer = member.Open();
            await writer.WriteAsync("hello!"u8.ToArray());
        }
        source.Position = 0;
        var conversation = "archive-live-" + Guid.NewGuid().ToString("N");
        var transfer = new AttachmentTransferClient(new(Guid.NewGuid().ToString("N"), profile.Id, conversation,
            "cortex-live.zip", source.Length), client);
        var uploaded = await transfer.UploadAsync(source);
        var reference = uploaded.Attachment ?? throw new InvalidOperationException("The actual Worker did not retain the archive original.");
        var receipt = await client.GetAsync<AttachmentArtifactReceipt>("resources.attachments.archive.extract",
            new AttachmentArchiveRequest(profile.Id, conversation, reference.Id, reference.Revision));
        var text = await client.GetAsync<AttachmentArtifactText>("resources.attachments.archive.text",
            new AttachmentArchiveTextRequest(profile.Id, conversation, reference.Id, reference.Revision, "hello.txt", 32));
        if (receipt.State != "ready" || text.Text != "hello!")
            throw new InvalidOperationException("The actual Worker did not return the exact Cortex-extracted archive member.");
        Console.WriteLine("Attachment artifacts live: the " + route + " imported an isolated original and used Cortex 7-Zip through its " +
            (requiresFullAccess ? "authenticated application pipe." : "ordinary signed-in-user PAO host."));
    }

    private static async Task<Cerebral.Contracts.AttachmentReference> ImportZipAsync(AttachmentStore store,
        AttachmentAccessContext access, string requestId, string name)
    {
        await using var source = new MemoryStream();
        using (var archive = new ZipArchive(source, ZipArchiveMode.Create, leaveOpen: true))
        {
            var member = archive.CreateEntry("hello.txt");
            await using var writer = member.Open();
            await writer.WriteAsync("hello!"u8.ToArray());
        }
        source.Position = 0;
        return await store.ImportAsync(access, requestId, name, source);
    }
}
