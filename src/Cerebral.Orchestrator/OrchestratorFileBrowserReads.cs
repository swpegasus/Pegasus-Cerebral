using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private long fileBrowserReadVersion;
    private Func<CommandRequest, CancellationToken, Task<CommandResult>>? fileBrowserCommandOverride;

    private sealed record FileBrowserSelection(long Version, DesktopConversationBinding Conversation,
        string Connection, string? ProjectId, string ProfileId, string ProfileName, string WorkerId, string[] Roots,
        string[] Capabilities, string[] Tools, bool Shared, string SharedHost, string SharedDirectory, string Path,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> Dispatch, Func<bool> ConnectionCurrent);

    private sealed record FileBrowserReadOutcome(FileBrowserSelection Selection, ToolResult? Result, Exception? Error = null);

    private void InvalidateFileBrowserRead()
    {
        fileBrowserReadVersion++;
        openFileHash = null; openFilePath = null; openFileProfileId = null; openFileTruncated = true;
    }

    private FileBrowserSelection CaptureFileBrowserRead()
    {
        Dispatcher.VerifyAccess();
        var profile = ConversationProfile();
        Func<CommandRequest, CancellationToken, Task<CommandResult>> dispatch;
        Func<bool> current;
        if (fileBrowserCommandOverride is { } supplied)
        {
            dispatch = supplied;
            current = () => ReferenceEquals(fileBrowserCommandOverride, supplied);
        }
        else
        {
            var client = Client;
            dispatch = (request, token) => client.CommandAsync(request.Operation, request.Arguments, request.ProfileId, token);
            current = () => IsConnected && ReferenceEquals(Client, client);
        }
        return new(++fileBrowserReadVersion, PluginBinding(), ServiceConnectionIdentity, ConversationProjectId, profile.Id, profile.Name,
            profile.WorkerId, profile.AllowedRoots.ToArray(), profile.Capabilities.ToArray(), profile.AllowedTools.ToArray(),
            sharedFiles.IsChecked == true, sharedFilesHost, sharedDirectory, filePath.Text, dispatch, current);
    }

    private bool FileBrowserReadCurrent(FileBrowserSelection selection)
    {
        Dispatcher.VerifyAccess();
        var profile = TryConversationProfile();
        return !Lifetime.IsCancellationRequested && selection.Version == fileBrowserReadVersion &&
            selection.Conversation == PluginBinding() && selection.Connection == ServiceConnectionIdentity &&
            selection.ProjectId == ConversationProjectId &&
            selection.ConnectionCurrent() && profile is not null && profile.Id == selection.ProfileId &&
            profile.WorkerId == selection.WorkerId && profile.AllowedRoots.SequenceEqual(selection.Roots, StringComparer.Ordinal) &&
            profile.Capabilities.SequenceEqual(selection.Capabilities, StringComparer.Ordinal) &&
            profile.AllowedTools.SequenceEqual(selection.Tools, StringComparer.Ordinal) &&
            selection.Shared == (sharedFiles.IsChecked == true) && selection.SharedHost == sharedFilesHost &&
            selection.SharedDirectory == sharedDirectory && selection.Path == filePath.Text;
    }

    private ToolResult? CurrentFileBrowserResult(FileBrowserReadOutcome? read)
    {
        // Recheck at the publishing caller too: completing the inner async read may queue its
        // caller behind a navigation event on the dispatcher. Errors obey the same boundary.
        if (read is null || !FileBrowserReadCurrent(read.Selection)) return null;
        if (read.Error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return read.Result ?? throw new InvalidOperationException("The file read returned no result.");
    }

    private async Task<FileBrowserReadOutcome?> ReadFileBrowserAsync(string action)
    {
        var selection = CaptureFileBrowserRead();
        object arguments = action == "list"
            ? new { path = selection.Path, recursive = false, limit = 200 }
            : new { path = selection.Path, startLine = 1, lineCount = 1000 };
        var request = new CommandRequest("tools.call", CerebralJson.Element(new
        {
            profileId = selection.ProfileId, sessionId = session?.Id,
            name = (selection.Shared ? "shared." : "file.") + action, arguments
        }));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // Bind the actual client before awaiting. A late success or failure belongs to that
            // selected view, never the next connection, conversation, source or read request.
            var response = await selection.Dispatch(request, timeout.Token);
            if (!FileBrowserReadCurrent(selection)) return null;
            if (!response.Success) throw new InvalidOperationException(response.Error ?? "Could not read workspace files.");
            var result = CerebralJson.Read<ToolResult>(response.Data);
            if (!result.Success) throw new InvalidOperationException(result.Content);
            return new(selection, result);
        }
        catch (Exception) when (!FileBrowserReadCurrent(selection)) { return null; }
        catch (Exception error) { return new(selection, null, error); }
    }
}
