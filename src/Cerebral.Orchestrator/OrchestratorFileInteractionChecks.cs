using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Deterministic WPF interaction check. It invokes no shell association, service command, or filesystem action.
    public int CheckFileInteractionUi()
    {
        var checks = 0; var filesDesiredWidth = 0d;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        static HashSet<string> Commands(ContextMenu menu) => menu.Items.OfType<MenuItem>()
            .Select(x => AutomationProperties.GetAutomationId(x)).Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.Ordinal);

        try
        {
            Check(AutomationProperties.GetAutomationId(fileEntries) == "WorkspaceFileList", "The file list needs a stable plugin and accessibility identity.");
            Check(AutomationProperties.GetName(fileEntries) == "Workspace files and folders", "The file list needs an accessible name.");
            Check(fileEntries.ToolTip?.ToString()?.Contains("Double-click", StringComparison.Ordinal) == true &&
                  fileEntries.ToolTip?.ToString()?.Contains("Shift+F10", StringComparison.Ordinal) == true,
                "The file list must advertise mouse and keyboard opening actions.");
            Check(fileHomeButton is not null && AutomationProperties.GetAutomationId(fileHomeButton) == "WorkspaceFilesHome" &&
                  fileUpButton is not null && AutomationProperties.GetAutomationId(fileUpButton) == "WorkspaceFilesUp" &&
                  fileRootsButton is not null && AutomationProperties.GetAutomationId(fileRootsButton) == "WorkspaceProjectRoots",
                "Files need stable Home, Up and project-root navigation actions.");
            Check(fileOpenButton is not null && AutomationProperties.GetAutomationId(fileOpenButton) == "OpenWorkspaceFile",
                "Files need a visible selected-item open action in addition to mouse and keyboard gestures.");
            Check(AutomationProperties.GetAutomationId(fileLocation) == "FileLocation" &&
                  AutomationProperties.GetName(filePath) == "Current folder or file path",
                "The current file location needs clear visual and accessible labels.");
            Check(fileListingDetails is { IsExpanded: false } && fileListingDetails.Content == fileListing,
                "Raw file-tool details stay available without consuming normal browser height.");
            Check(FileBrowserParentPath(@"C:\Projects\Novel\Chapters", shared: false) == @"C:\Projects\Novel" &&
                  FileBrowserParentPath("/projects/novel/chapters", shared: false) == "/projects/novel" &&
                  FileBrowserParentPath("novel/chapters", shared: true) == "novel" &&
                  FileBrowserParentPath("novel", shared: true) == ".",
                "Up navigation handles Windows, remote Unix and shared relative folders.");
            fileWorkspaceRoot?.Measure(new Size(992, 620));
            filesDesiredWidth = fileWorkspaceRoot?.DesiredSize.Width ?? 0;
            Check(fileWorkspaceRoot is not null && fileWorkspaceRoot.DesiredSize.Width <= 992,
                "The Files workspace fits the content width of a 1024 by 768 window without horizontal clipping.");
            var menu = fileEntries.ContextMenu ?? throw new InvalidOperationException("The file list needs a context menu.");

            var file = new FileEntry(@"C:\Projects\DeveloperWorkflowChecklist.md", "file");
            fileEntries.ItemsSource = new[] { file };
            fileEntries.SelectedItem = file;
            PopulateFileEntryMenu(menu);
            var fileCommands = Commands(menu);
            Check(fileCommands.Contains("OpenFileEntry") && fileCommands.Contains("OpenFileEntryInCerebral"),
                "Files need primary Open and Read in Cerebral actions.");
            Check(fileCommands.Contains("OpenFileEntryOnDesktop") && fileCommands.Contains("OpenContainingFolder"),
                "Files need explicit Windows association and containing-folder actions.");
            Check(fileCommands.Contains("CopyFileEntryPath"), "Files need a copy-path action.");
            Check(fileCommands.Contains("CopyFileEntryName") && fileCommands.Contains("RefreshFileEntries"),
                "Files need normal copy-name and refresh actions.");

            var folder = new FileEntry(@"C:\Projects", "directory");
            Check(folder.Label == "Folder · Projects" && file.Label == "File · DeveloperWorkflowChecklist.md",
                "Folder contents use human-readable file and folder labels.");
            fileEntries.ItemsSource = new[] { folder };
            fileEntries.SelectedItem = folder;
            PopulateFileEntryMenu(menu);
            var folderCommands = Commands(menu);
            Check(folderCommands.Contains("OpenFileEntry") && folderCommands.Contains("OpenFileEntryInCerebral") &&
                  folderCommands.Contains("OpenFolderInExplorer"), "Folders need navigation and explicit File Explorer actions.");
            Check(!folderCommands.Contains("OpenContainingFolder"), "Folder menus must not show the file-only containing-folder action.");

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = true,
                fileInteractionChecks = checks,
                responsive = new { window = "1024x768", availableContentWidth = 992, desiredContentWidth = Math.Round(filesDesiredWidth, 2), rawDetailsCollapsed = fileListingDetails?.IsExpanded == false },
                shellApplicationsLaunched = false,
                serviceCommandsExecuted = false,
                filesystemChanged = false,
                windowShown = false
            }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, fileInteractionChecks = checks, error = ex.ToString() }));
            return 1;
        }
        finally { Close(); }
    }
}
