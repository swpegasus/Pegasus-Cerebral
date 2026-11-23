using System.IO;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop;

/// <summary>
/// The immutable desktop storage binding selected before a window is constructed. A Worker
/// connection is not a declaration of plugin ownership: source launches carry both roots
/// explicitly, while installed and portable launches retain their registered path resolution.
/// </summary>
public sealed record DesktopInstancePaths
{
    public const string SourceDataEnvironmentVariable = "PEGASUS_CEREBRAL_SOURCE_DATA";
    public const string SourceProgramEnvironmentVariable = "PEGASUS_CEREBRAL_SOURCE_PROGRAM";
    private static readonly object Gate = new();
    private static DesktopInstancePaths? selected;

    public bool SourceLocal { get; }
    public bool Portable { get; }
    public string BaseDirectory { get; }
    public string InstallDirectory { get; }
    public string DataDirectory { get; }
    public string UserDataDirectory { get; }
    public string ToolsDirectory { get; }
    public string ConnectionDataDirectory => SourceLocal ? Path.Combine(DataDirectory, "Desktop") : UserDataDirectory;
    public string PluginDirectory => Path.Combine(SourceLocal ? UserDataDirectory : InstallDirectory, "Plugins", "Desktop");
    public string UserPluginDirectory => Path.Combine(UserDataDirectory, "Plugins", "Desktop");
    public string PluginDataDirectory => Path.Combine(UserDataDirectory, "PluginData");
    public DesktopApplicationIdentity ApplicationIdentity => SourceLocal
        ? DesktopApplicationIdentity.SourceLocal(DataDirectory) : DesktopApplicationIdentity.DesktopInstance(UserDataDirectory);

    public static string DefaultSourceDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegasus", "Cerebral", "Source");

    public static DesktopInstancePaths Current
    {
        get { lock (Gate) return selected ??= ForInstallation(); }
    }

    private DesktopInstancePaths(bool sourceLocal, bool portable, string baseDirectory, string installDirectory,
        string dataDirectory, string userDataDirectory, string toolsDirectory)
    {
        SourceLocal = sourceLocal;
        Portable = portable;
        BaseDirectory = Normalize(baseDirectory);
        InstallDirectory = Normalize(installDirectory);
        DataDirectory = Normalize(dataDirectory);
        UserDataDirectory = Normalize(userDataDirectory);
        ToolsDirectory = Normalize(toolsDirectory);
    }

    public static DesktopInstancePaths ForSource(string dataDirectory, string programDirectory) =>
        new(true, false, dataDirectory, programDirectory, dataDirectory, dataDirectory, Path.Combine(programDirectory, "Tools"));

    /// <summary>Private UI storage is separate from the connected Worker's durable root and never
    /// declares Source startup or publishes a Source binding to child applications.</summary>
    public static DesktopInstancePaths UseAttachedExisting(string desktopDirectory, string programDirectory)
    {
        var binding = new DesktopInstancePaths(false, false, programDirectory, programDirectory,
            desktopDirectory, desktopDirectory, Path.Combine(programDirectory, "Tools"));
        lock (Gate)
        {
            if (selected is not null && !selected.SameBinding(binding))
                throw new InvalidOperationException("This process already selected a different desktop storage binding.");
            ConnectionStore.UseProcessLocalDataDirectory(binding.ConnectionDataDirectory);
            return selected = selected ?? binding;
        }
    }

    /// <summary>
    /// Builds a product-path projection for a managed plugin owner without selecting
    /// process storage, preferences, connections, or an application singleton.
    /// </summary>
    public static DesktopInstancePaths ForManagedPluginOwnerSnapshot(string mainUiStateRoot,
        string mainInstallDirectory, string mainBaseDirectory, bool mainPortable)
    {
        var ui = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(mainUiStateRoot);
        var install = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(mainInstallDirectory);
        var baseDirectory = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(mainBaseDirectory);
        return new(false, mainPortable, baseDirectory, install, ui, ui, Path.Combine(install, "Tools"));
    }

    public static DesktopInstancePaths ForInstallation() =>
        new(false, InstallationPaths.IsPortable, InstallationPaths.BaseDirectory, InstallationPaths.InstallDirectory,
            InstallationPaths.DataDirectory, InstallationPaths.CurrentUserDataDirectory, InstallationPaths.ToolsDirectory);

    /// <summary>Returns only an explicit, complete inherited source binding; malformed bindings never become defaults.</summary>
    public static DesktopInstancePaths? ReadInheritedSource()
    {
        var data = Environment.GetEnvironmentVariable(SourceDataEnvironmentVariable);
        var program = Environment.GetEnvironmentVariable(SourceProgramEnvironmentVariable);
        if (data is null && program is null) return null;
        if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(program))
            throw new ArgumentException("The inherited source desktop binding requires both its data and program directories.");
        var binding = ForSource(data, program);
        var connection = Environment.GetEnvironmentVariable(ConnectionStore.ProcessLocalDataEnvironmentVariable);
        if (connection is not null && !Normalize(connection).Equals(binding.ConnectionDataDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The inherited source desktop binding does not match its connection directory.");
        return binding;
    }

    /// <summary>Select once, and publish the same non-secret binding to child desktop processes.</summary>
    public static DesktopInstancePaths UseSource(string dataDirectory, string programDirectory)
    {
        var binding = ForSource(dataDirectory, programDirectory);
        lock (Gate)
        {
            if (selected is not null && !selected.SameBinding(binding))
                throw new InvalidOperationException("This process already selected a different desktop storage binding.");
            binding = selected ?? binding;
            ConnectionStore.UseProcessLocalDataDirectory(binding.ConnectionDataDirectory);
            selected = binding;
            Environment.SetEnvironmentVariable(SourceDataEnvironmentVariable, binding.DataDirectory);
            Environment.SetEnvironmentVariable(SourceProgramEnvironmentVariable, binding.InstallDirectory);
            Environment.SetEnvironmentVariable(ConnectionStore.ProcessLocalDataEnvironmentVariable, binding.ConnectionDataDirectory);
            return binding;
        }
    }

    /// <summary>
    /// Configuration can open independently of Chat. Explicit source launch arguments take precedence;
    /// a child launch inherits the exact selected binding. Ordinary launches use selected durable roots regardless of binary layout.
    /// </summary>
    public static DesktopInstancePaths InitializeConfiguration(string[] args, bool installedLayout, string programDirectory)
    {
        var indexes = args.Select((value, index) => (value, index)).Where(item => item.value == "--source-local")
            .Select(item => item.index).ToArray();
        if (indexes.Length > 1) throw new ArgumentException("Use --source-local once.");
        if (indexes.Length == 1)
        {
            var index = indexes[0];
            var data = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1] : DefaultSourceDataDirectory;
            return UseSource(data, programDirectory);
        }
        var inherited = ReadInheritedSource();
        return inherited is null ? Current : UseSource(inherited.DataDirectory, inherited.InstallDirectory);
    }

    public DesktopProductPaths ProductPaths(string? projects = null, string? models = null, string? tools = null, string? shared = null) =>
        new(InstallDirectory, DataDirectory, UserDataDirectory,
            string.IsNullOrWhiteSpace(projects) ? Path.Combine(DataDirectory, "Projects") : projects,
            string.IsNullOrWhiteSpace(models) ? Path.Combine(DataDirectory, "models") : models,
            string.IsNullOrWhiteSpace(tools) ? ToolsDirectory : tools,
            string.IsNullOrWhiteSpace(shared) ? Path.Combine(DataDirectory, "Shared") : shared)
        { BaseDirectory = BaseDirectory, Portable = Portable };

    private bool SameBinding(DesktopInstancePaths other) =>
        SourceLocal == other.SourceLocal && Portable == other.Portable &&
        new[] { (BaseDirectory, other.BaseDirectory), (InstallDirectory, other.InstallDirectory),
            (DataDirectory, other.DataDirectory), (UserDataDirectory, other.UserDataDirectory),
            (ToolsDirectory, other.ToolsDirectory) }
        .All(pair => pair.Item1.Equals(pair.Item2, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || directory.Contains('\0'))
            throw new ArgumentException("Desktop instance storage requires an absolute directory.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }
}
