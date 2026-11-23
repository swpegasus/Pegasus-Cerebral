using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private string? lastDesktopOpenPath;
    private DateTimeOffset lastDesktopOpenAt;

    private void ConfigureFileEntryInteraction()
    {
        AutomationProperties.SetAutomationId(fileEntries, "WorkspaceFileList");
        AutomationProperties.SetName(fileEntries, "Workspace files and folders");
        fileEntries.ToolTip = "Double-click or press Enter to open. Right-click or press Shift+F10 for more actions.";
        var itemStyle = Ui.ExtendStyle<ListBoxItem>();
        itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding("Label")));
        itemStyle.Setters.Add(new Setter(ToolTipProperty, new Binding("Path")));
        itemStyle.Setters.Add(new Setter(MinHeightProperty, 30d));
        fileEntries.ItemContainerStyle = itemStyle;

        fileEntries.MouseDoubleClick += (_, e) =>
        {
            if (fileEntries.SelectedItem is not FileEntry) return;
            e.Handled = true;
            Run(OpenSelectedFileEntry);
        };
        fileEntries.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || fileEntries.SelectedItem is not FileEntry) return;
            e.Handled = true;
            Run(OpenSelectedFileEntry);
        };
        fileEntries.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source) return;
            if (ItemsControl.ContainerFromElement(fileEntries, source) is not ListBoxItem item) return;
            item.IsSelected = true;
            item.Focus();
        };

        var menu = new ContextMenu();
        menu.Opened += (_, _) => PopulateFileEntryMenu(menu);
        fileEntries.ContextMenu = menu;
    }

    private void PopulateFileEntryMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (fileEntries.SelectedItem is not FileEntry entry)
        {
            menu.Items.Add(new MenuItem { Header = "No file or folder selected", IsEnabled = false });
            return;
        }

        AddFileEntryMenuItem(menu, "_Open", OpenSelectedFileEntry, "OpenFileEntry");
        AddFileEntryMenuItem(menu, entry.Type == "directory" ? "Open in _Cerebral" : "_Read in Cerebral",
            () => OpenFileEntryInCerebral(entry), "OpenFileEntryInCerebral");

        var hasLocalPath = TryResolveLocalFileEntry(entry, out _, out var reason);
        if (entry.Type == "file")
            AddFileEntryMenuItem(menu, "Open with _Windows", () => OpenFileEntryOnDesktop(entry), "OpenFileEntryOnDesktop", hasLocalPath, reason);
        if (entry.Type == "directory")
            AddFileEntryMenuItem(menu, "Open in File _Explorer", () => OpenFileEntryOnDesktop(entry), "OpenFolderInExplorer", hasLocalPath, reason);
        if (entry.Type == "file")
            AddFileEntryMenuItem(menu, "Open containing _folder", () => OpenContainingFolder(entry), "OpenContainingFolder", hasLocalPath, reason);

        menu.Items.Add(new Separator());
        AddFileEntryMenuItem(menu, "_Copy path", () =>
        {
            Clipboard.SetText(entry.Path);
            SetStatus("Copied path: " + entry.Path);
            return Task.CompletedTask;
        }, "CopyFileEntryPath");
        AddFileEntryMenuItem(menu, "Copy _name", () =>
        {
            var name = Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Clipboard.SetText(string.IsNullOrWhiteSpace(name) ? entry.Path : name);
            SetStatus("Copied name.");
            return Task.CompletedTask;
        }, "CopyFileEntryName");
        menu.Items.Add(new Separator());
        AddFileEntryMenuItem(menu, "_Refresh folder", ListFiles, "RefreshFileEntries");
    }

    private void AddFileEntryMenuItem(ContextMenu menu, string title, Func<Task> action, string automationId,
        bool enabled = true, string? disabledReason = null)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled, ToolTip = enabled ? null : disabledReason };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => Run(action);
        menu.Items.Add(item);
    }

    private Task OpenSelectedFileEntry()
    {
        var entry = fileEntries.SelectedItem as FileEntry ?? throw new InvalidOperationException("Select a file or folder first.");
        if (entry.Type == "directory") return OpenFileEntryInCerebral(entry);
        return TryResolveLocalFileEntry(entry, out _, out _) ? OpenFileEntryOnDesktop(entry) : OpenFileEntryInCerebral(entry);
    }

    private async Task OpenFileEntryInCerebral(FileEntry entry)
    {
        filePath.Text = entry.Path;
        if (entry.Type == "directory") await OpenCurrentFileFolder();
        else if (entry.Type == "file") await ReadFile();
        else throw new InvalidOperationException("The selected entry is neither a file nor a folder.");
    }

    private void UpdateFileLocation()
    {
        var path = filePath.Text.Trim(); var source = sharedFiles.IsChecked == true ? "Shared files" : "Workspace";
        fileLocation.Text = path.Length == 0 ? source + " · no location selected" : source + " · " + path;
        fileLocation.ToolTip = fileLocation.Text;
    }

    private async Task NavigateFileHome()
    {
        var home = DefaultFileRoot(ConversationProfile());
        if (string.IsNullOrWhiteSpace(home)) { await ShowRoots(); return; }
        filePath.Text = home; await OpenCurrentFileFolder();
    }

    private async Task NavigateFileUp()
    {
        var current = filePath.Text.Trim();
        if (string.IsNullOrWhiteSpace(current)) { await NavigateFileHome(); return; }
        string parent;
        if (sharedFiles.IsChecked == true) parent = FileBrowserParentPath(current, shared: true);
        else
        {
            var profile = ConversationProfile();
            var boundary = profile.AllowedRoots.Where(root => NavigationPathInside(current, root))
                .OrderByDescending(root => root.Length).FirstOrDefault();
            if (boundary is null) { await NavigateFileHome(); return; }
            parent = FileBrowserParentPath(current, shared: false);
            if (!NavigationPathInside(parent, boundary)) parent = boundary;
        }
        if (FileBrowserSamePath(parent, current, sharedFiles.IsChecked == true))
        {
            SetStatus("This is the top of the current file root."); return;
        }
        filePath.Text = parent; await OpenCurrentFileFolder();
    }

    private async Task OpenCurrentFileFolder()
    {
        var lazyProject = sharedFiles.IsChecked == true ? null : navigation.Nodes.FirstOrDefault(x =>
            x.Kind == "project" && x.CreateWhenUsed && x.Path is not null && FileBrowserSamePath(filePath.Text, x.Path, shared: false));
        try
        {
            await ListFiles();
            if (lazyProject is not null) await ChangeNavigation(lazyProject, copy => copy.CreateWhenUsed = false);
        }
        catch (InvalidOperationException error) when (lazyProject is not null && MissingLazyProjectFolder(error))
        {
            fileEntries.ItemsSource = Array.Empty<FileEntry>(); fileListing.Clear();
            if (fileListingDetails is not null) fileListingDetails.IsExpanded = false;
            SetStatus("This project has no files yet. Cerebral will create its folder when the first file is saved.");
        }
    }

    private static bool MissingLazyProjectFolder(InvalidOperationException error) =>
        error.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
        error.Message.Contains("directory", StringComparison.OrdinalIgnoreCase);

    private static string FileBrowserParentPath(string path, bool shared)
    {
        path = path.Trim();
        if (shared)
        {
            var relative = path.Replace('\\', '/').Trim('/');
            if (relative.Length == 0 || relative == ".") return ".";
            var split = relative.LastIndexOf('/');
            return split < 0 ? "." : relative[..split];
        }
        if (path.StartsWith('/') && !path.Contains('\\'))
        {
            var unix = path.TrimEnd('/');
            if (unix.Length == 0) return "/";
            var split = unix.LastIndexOf('/');
            return split <= 0 ? "/" : unix[..split];
        }
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
        return Path.GetDirectoryName(trimmed) ?? full;
    }

    private static bool FileBrowserSamePath(string left, string right, bool shared)
    {
        if (shared)
        {
            static string Relative(string value)
            {
                value = value.Trim().Replace('\\', '/').Trim('/');
                return value.Length == 0 ? "." : value;
            }
            return Relative(left).Equals(Relative(right), StringComparison.Ordinal);
        }
        return NavigationPathInside(left, right) && NavigationPathInside(right, left);
    }

    private Task OpenFileEntryOnDesktop(FileEntry entry)
    {
        if (!TryResolveLocalFileEntry(entry, out var localPath, out var reason))
            throw new InvalidOperationException(reason);
        if (entry.Type == "file" && !File.Exists(localPath)) throw new FileNotFoundException("The selected file is no longer available.", localPath);
        if (entry.Type == "directory" && !Directory.Exists(localPath)) throw new DirectoryNotFoundException("The selected folder is no longer available: " + localPath);
        if (entry.Type is not ("file" or "directory")) throw new InvalidOperationException("The selected entry is neither a file nor a folder.");

        // A double-click produces several closely spaced mouse messages. Keep a single user gesture
        // from launching two associated applications without blocking a later deliberate open.
        var now = DateTimeOffset.UtcNow;
        if (localPath.Equals(lastDesktopOpenPath, StringComparison.OrdinalIgnoreCase) && now - lastDesktopOpenAt < TimeSpan.FromSeconds(1))
            return Task.CompletedTask;
        lastDesktopOpenPath = localPath;
        lastDesktopOpenAt = now;

        using var process = Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
        SetStatus(entry.Type == "directory" ? "Opened folder in File Explorer: " + localPath : "Opened with the Windows associated application: " + localPath);
        return Task.CompletedTask;
    }

    private Task OpenContainingFolder(FileEntry entry)
    {
        if (entry.Type != "file") throw new InvalidOperationException("Open containing folder applies to files.");
        if (!TryResolveLocalFileEntry(entry, out var localPath, out var reason)) throw new InvalidOperationException(reason);
        if (!File.Exists(localPath)) throw new FileNotFoundException("The selected file is no longer available.", localPath);
        var folder = Path.GetDirectoryName(localPath) ?? throw new InvalidOperationException("The file has no containing folder.");
        using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        SetStatus("Opened containing folder: " + folder);
        return Task.CompletedTask;
    }

    private bool TryResolveLocalFileEntry(FileEntry entry, out string localPath, out string reason)
    {
        localPath = "";
        if (!ServiceIsLocal)
        {
            reason = "The selected item is on another computer. Open it in Cerebral or copy it to this computer first.";
            return false;
        }

        try
        {
            if (sharedFiles.IsChecked == true)
            {
                if (!sharedFilesHost.Equals("local", StringComparison.Ordinal))
                {
                    reason = "The shared-files host is another worker. Open the item in Cerebral or copy it locally first.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(sharedDirectory))
                {
                    reason = "The local shared-files directory is not configured.";
                    return false;
                }
                localPath = ConfinedLocalShellPath(Path.Combine(sharedDirectory, entry.Path), [sharedDirectory]);
            }
            else
            {
                var profile = TryConversationProfile();
                if (profile is null)
                {
                    reason = leadConfigurationError;
                    return false;
                }
                if (!profile.WorkerId.Equals("local", StringComparison.Ordinal))
                {
                    reason = "The selected item is on worker '" + profile.WorkerId + "'. Open it in Cerebral or copy it locally first.";
                    return false;
                }
                localPath = ConfinedLocalShellPath(entry.Path, profile.AllowedRoots);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            localPath = "";
            reason = "The selected local path must stay inside its configured root without links or junctions. " + ex.Message;
            return false;
        }

        reason = "";
        return true;
    }

    private static string ConfinedLocalShellPath(string path, IEnumerable<string> allowedRoots)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.Any(char.IsControl) || path.Length > 2 && path.IndexOf(':', 2) >= 0)
            throw new ArgumentException("Use an absolute local path without device prefixes or alternate data streams.");
        var resolved = Path.GetFullPath(path);
        if (!allowedRoots.Any(root => Path.IsPathFullyQualified(root) && NavigationPathInside(resolved, root)))
            throw new UnauthorizedAccessException("The selected path is outside the configured folders.");
        // Shell associations do not pass through the Worker's scoped file tools. Apply the same
        // reject-links boundary here, including a linked root and dangling links, before launch.
        for (var item = resolved; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
        {
            try
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Links and junctions are not permitted for scoped desktop opening.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return resolved;
    }
}
