using System.IO;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Real file-browser handlers and WPF controls with deterministic delayed command responses; no Worker or shell launch.</summary>
    public async Task<int> CheckFileBrowserRaceUiAsync()
    {
        Dispatcher.VerifyAccess();
        var checks = 0;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-file-browser-check-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var originalProfiles = profiles; var originalLead = leadProfile; var originalSession = session;
        var originalNavigation = navigation; var originalProjectsDirectory = projectsDirectory;
        var originalGeneration = pluginGeneration; var originalDispatch = fileBrowserCommandOverride;
        var originalShared = sharedFiles.IsChecked; var originalSharedHost = sharedFilesHost; var originalSharedDirectory = sharedDirectory;
        var originalPath = filePath.Text;
        var pending = new List<(TaskCompletionSource<CommandResult> Response, Task Task)>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }
        static TaskCompletionSource<CommandResult> Held() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        static CommandResult Listed(string path) => CommandResult.Ok(new ToolResult(true,
            JsonSerializer.Serialize(new { entries = new[] { new { path, type = "file" } } })));
        static CommandResult Read(string content, string hash = "fixture-preimage", bool truncated = false) =>
            CommandResult.Ok(new ToolResult(true, JsonSerializer.Serialize(new { content, sha256 = hash }), truncated));
        static async Task Complete(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(5));
        async Task RejectChangedRead(string label, Action mutate, Action restore)
        {
            var held = Held();
            fileBrowserCommandOverride = (_, _) => held.Task;
            filePath.Text = Path.Combine(root, "document.md");
            fileContent.Text = "Current editor";
            var operation = ReadFile(); pending.Add((held, operation));
            mutate();
            held.SetResult(Read("Obsolete editor", "obsolete-preimage"));
            await Complete(operation);
            Check(fileContent.Text == "Current editor" && openFileHash is null && openFilePath is null,
                label + " rejects stale content and replacement authorization.");
            restore();
        }
        try
        {
            var profile = new AgentProfile
            {
                Id = "file-browser-lead", Name = "File browser fixture", Role = "lead", WorkerId = "local",
                AllowedRoots = [root], Capabilities = ["filesystem.read"], AllowedTools = ["file.read", "file.list"]
            };
            profiles = [profile]; leadProfile = profile;
            var currentProjectPath = Path.Combine(root, "CurrentProject"); Directory.CreateDirectory(currentProjectPath);
            var lazyProjectPath = Path.Combine(root, "FutureProject");
            var currentProject = new NavigationNode { Id = "current-project", Kind = "project", Title = "Current project", Path = currentProjectPath };
            var lazyProject = new NavigationNode { Id = "future-project", Kind = "project", Title = "Future project", Path = lazyProjectPath, CreateWhenUsed = true };
            navigation = new NavigationState { Nodes = [currentProject, lazyProject] }; projectsDirectory = root;
            session = new TaskSession { Id = "file-browser-conversation", ProfileId = profile.Id, ProjectId = currentProject.Id };
            sharedFiles.IsChecked = false; sharedFilesHost = "local"; sharedDirectory = root;
            filePath.Text = root;
            CommandRequest? observed = null;
            fileBrowserCommandOverride = (request, _) => { observed = request; return Task.FromResult(Listed(Path.Combine(root, "current.md"))); };
            await Complete(ListFiles());
            Check(fileEntries.Items.OfType<FileEntry>().Single().Path == Path.Combine(root, "current.md"), "Current folder response populates the actual browser.");
            Check(observed is { Operation: "tools.call" } && observed.Arguments.GetProperty("profileId").GetString() == profile.Id &&
                observed.Arguments.GetProperty("sessionId").GetString() == session.Id &&
                observed.Arguments.GetProperty("name").GetString() == "file.list" &&
                observed.Arguments.GetProperty("arguments").GetProperty("path").GetString() == root,
                "The dispatched folder request captures the original profile, legacy session, source and path.");

            await ShowRoots();
            var roots = fileEntries.Items.OfType<FileEntry>().ToArray();
            Check(roots.Any(x => x.Path == currentProjectPath && x.Label.StartsWith("Current project ·", StringComparison.Ordinal)) &&
                  roots.Any(x => x.Path == lazyProjectPath && x.Label.Contains("· empty", StringComparison.Ordinal)) &&
                  roots.Any(x => x.Path == root && x.Label.StartsWith("Projects folder ·", StringComparison.Ordinal)),
                "Project roots show the current, lazy and configured project locations without creating folders.");

            observed = null; fileBrowserCommandOverride = (request, _) =>
            {
                observed = request; return Task.FromResult(Listed(Path.Combine(currentProjectPath, "docs", "chapter.md")));
            };
            filePath.Text = Path.Combine(currentProjectPath, "docs", "drafts"); await Complete(NavigateFileUp());
            var parent = Path.Combine(currentProjectPath, "docs");
            Check(filePath.Text == parent && observed?.Arguments.GetProperty("arguments").GetProperty("path").GetString() == parent,
                "Up moves one level and lists the parent folder through the normal scoped file tool.");
            observed = null; await Complete(NavigateFileHome());
            Check(filePath.Text == currentProjectPath && observed?.Arguments.GetProperty("arguments").GetProperty("path").GetString() == currentProjectPath,
                "Home returns to the current registered project and lists it.");

            session.ProjectId = lazyProject.Id;
            fileBrowserCommandOverride = (_, _) => Task.FromResult(CommandResult.Fail("Directory does not exist"));
            await Complete(NavigateFileHome());
            Check(filePath.Text == lazyProjectPath && lazyProject.CreateWhenUsed && !Directory.Exists(lazyProjectPath) && fileEntries.Items.Count == 0,
                "Opening a lazy project shows an empty browser without creating or activating its folder.");
            session.ProjectId = currentProject.Id; filePath.Text = root;

            var slowList = Held();
            var calls = 0;
            fileBrowserCommandOverride = (_, _) => ++calls == 1 ? slowList.Task : Task.FromResult(Listed(Path.Combine(root, "newest.md")));
            var oldList = ListFiles(); pending.Add((slowList, oldList));
            await Complete(ListFiles());
            slowList.SetResult(Listed(Path.Combine(root, "obsolete.md"))); await Complete(oldList);
            Check(fileEntries.Items.OfType<FileEntry>().Single().Path.EndsWith("newest.md", StringComparison.Ordinal),
                "A newer request wins even when both requests target the same folder.");

            filePath.Text = Path.Combine(root, "document.md");
            fileBrowserCommandOverride = (_, _) => Task.FromResult(Read("Fresh document"));
            await Complete(ReadFile());
            Check(fileContent.Text == "Fresh document" && openFileHash == "fixture-preimage" && openFilePath == filePath.Text &&
                openFileProfileId == profile.Id && !openFileTruncated, "Only the matching complete read adopts editor content and preimage state.");
            filePath.Text = Path.Combine(root, "next.md");
            Check(openFileHash is null && openFilePath is null && openFileTruncated, "Path edits invalidate old replacement state immediately.");

            await RejectChangedRead("Path change and return", () =>
            {
                filePath.Text = Path.Combine(root, "another.md"); filePath.Text = Path.Combine(root, "document.md");
            }, () => { });
            var binding = pluginGeneration;
            await RejectChangedRead("Conversation generation change", () => pluginGeneration = Guid.NewGuid().ToString("N"), () => pluginGeneration = binding);
            var conversationId = session.Id;
            await RejectChangedRead("Conversation selection change", () => session.Id = "other-conversation", () => session.Id = conversationId);
            var otherProfile = new AgentProfile { Id = "another-file-profile", Role = "lead", WorkerId = "local", AllowedRoots = [root] };
            profiles.Add(otherProfile);
            await RejectChangedRead("Profile selection change", () => session.ProfileId = otherProfile.Id, () => session.ProfileId = profile.Id);
            await RejectChangedRead("Worker selection change", () => profile.WorkerId = "another-worker", () => profile.WorkerId = "local");
            await RejectChangedRead("Root grant change", () => profile.AllowedRoots = [Path.Combine(root, "other")], () => profile.AllowedRoots = [root]);
            await RejectChangedRead("Tool grant change", () => profile.AllowedTools = [], () => profile.AllowedTools = ["file.read", "file.list"]);
            await RejectChangedRead("Shared source change and return", () =>
            {
                sharedFiles.IsChecked = true; sharedFiles.IsChecked = false; filePath.Text = Path.Combine(root, "document.md");
            }, () => { });
            await RejectChangedRead("Shared host change", () => sharedFilesHost = "other-host", () => sharedFilesHost = "local");
            await RejectChangedRead("Shared directory change", () => sharedDirectory = Path.Combine(root, "other"), () => sharedDirectory = root);
            await RejectChangedRead("Captured connection replacement", () =>
                fileBrowserCommandOverride = (_, _) => Task.FromResult(Read("New service")), () => { });

            var failed = Held();
            fileBrowserCommandOverride = (_, _) => failed.Task;
            filePath.Text = root; fileListing.Text = "Current listing";
            var obsoleteFailure = ListFiles(); pending.Add((failed, obsoleteFailure));
            filePath.Text = Path.Combine(root, "other");
            failed.SetException(new IOException("Obsolete service failure")); await Complete(obsoleteFailure);
            Check(fileListing.Text == "Current listing", "A late failure from an obsolete source cannot replace the current view or surface an old error.");

            fileBrowserCommandOverride = (_, _) => Task.FromResult(CommandResult.Fail("Current service failure"));
            var currentFailure = false;
            try { await Complete(ListFiles()); }
            catch (InvalidOperationException error) when (error.Message == "Current service failure") { currentFailure = true; }
            Check(currentFailure, "A current service failure still reaches the normal error handler.");
            fileBrowserCommandOverride = (_, _) => Task.FromResult(Read("Completed inner read"));
            var awaitingPublication = await ReadFileBrowserAsync("read");
            filePath.Text = Path.Combine(root, "changed-before-publish.md");
            Check(CurrentFileBrowserResult(awaitingPublication) is null,
                "A view change between inner completion and caller publication still discards the result.");
            fileBrowserCommandOverride = (_, _) => Task.FromResult(CommandResult.Fail("Completed inner error"));
            var awaitingError = await ReadFileBrowserAsync("read");
            filePath.Text = Path.Combine(root, "changed-before-error.md");
            Check(CurrentFileBrowserResult(awaitingError) is null,
                "A view change before caller publication also discards a completed inner error.");
            fileBrowserCommandOverride = (_, _) => Task.FromResult(Read("Partial document", truncated: true));
            await Complete(ReadFile());
            Check(openFileHash is null && openFilePath is null && openFileTruncated, "A partial read never authorizes replacement.");

            var allowed = Path.Combine(root, "allowed"); var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(allowed); Directory.CreateDirectory(outside);
            var ordinary = Path.Combine(allowed, "plain.txt"); await File.WriteAllTextAsync(ordinary, "fixture");
            Check(ConfinedLocalShellPath(ordinary, [allowed]) == ordinary, "An ordinary existing file remains available for desktop opening.");
            void Denied(string path, string boundary, string label)
            {
                var rejected = false;
                try { ConfinedLocalShellPath(path, [boundary]); }
                catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException) { rejected = true; }
                Check(rejected, label);
            }
            Denied(Path.Combine(allowed, "..", "outside", "other.txt"), allowed, "Canonical parent traversal outside the root is rejected.");
            Denied(allowed + "-neighbor\\other.txt", allowed, "An adjacent folder sharing a text prefix is rejected.");
            Denied(ordinary + ":stream", allowed, "Alternate data streams are unavailable to shell opening.");
            Denied("\\\\?\\" + ordinary, allowed, "Device-prefixed paths are unavailable to shell opening.");
            var link = Path.Combine(allowed, "linked");
            var createdLink = false;
            try
            {
                CreateFileBrowserFixtureJunction(root, link, outside); createdLink = true;
                Check((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "The fixture created a real native directory junction.");
                Denied(Path.Combine(link, "other.txt"), allowed, "An existing linked ancestor cannot escape the desktop root.");
                Denied(Path.Combine(link, "other.txt"), link, "A root that is itself a link is rejected.");
                Directory.Delete(outside);
                Denied(Path.Combine(link, "other.txt"), allowed, "A dangling linked ancestor is also rejected.");
            }
            finally { if (createdLink) Directory.Delete(link); }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = true, fileBrowserRaceChecks = checks, nativeJunctionChecks = 4,
                evidence = "actual WPF file handlers with controlled async command responses; real native temporary junction checks",
                serviceCommandsExecuted = false, shellApplicationsLaunched = false, windowShown = false
            }));
            return checks;
        }
        finally
        {
            InvalidateFileBrowserRead();
            foreach (var item in pending) item.Response.TrySetCanceled();
            foreach (var item in pending)
                try { await item.Task.WaitAsync(TimeSpan.FromSeconds(1)); } catch (OperationCanceledException) { }
            profiles = originalProfiles; leadProfile = originalLead; session = originalSession;
            navigation = originalNavigation; projectsDirectory = originalProjectsDirectory;
            pluginGeneration = originalGeneration; fileBrowserCommandOverride = originalDispatch;
            sharedFilesHost = originalSharedHost; sharedDirectory = originalSharedDirectory; sharedFiles.IsChecked = originalShared;
            filePath.Text = originalPath;
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cerebral-file-browser-check-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned file-browser fixture.");
            Directory.Delete(root, true);
        }
    }

    // Junction creation does not require the symbolic-link privilege. This fixture only changes
    // a newly-created empty directory within its exact temporary root; no services or policy change.
    private static void CreateFileBrowserFixtureJunction(string fixtureRoot, string junction, string target)
    {
        var boundary = Path.GetFullPath(fixtureRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        junction = Path.GetFullPath(junction); target = Path.GetFullPath(target);
        if (!junction.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
            !target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || Directory.Exists(junction) || File.Exists(junction))
            throw new InvalidOperationException("The junction fixture must remain inside its fresh owned root.");
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var bytes = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)(bytes.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(bytes, 16); print.CopyTo(bytes, 16 + substitute.Length + 2);
        Directory.CreateDirectory(junction);
        try
        {
            using var handle = FileBrowserJunctionNative.CreateFile(junction, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !FileBrowserJunctionNative.DeviceIoControl(handle, 0x000900A4, bytes, (uint)bytes.Length,
                    IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the native test junction.");
        }
        catch { Directory.Delete(junction); throw; }
    }

    private static class FileBrowserJunctionNative
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
            IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
    }
}
