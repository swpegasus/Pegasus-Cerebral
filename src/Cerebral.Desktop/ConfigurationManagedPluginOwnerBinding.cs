using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop;

/// <summary>
/// A private, versioned Main-to-Configuration plugin owner binding. The six public
/// attach-existing arguments still select the Worker and Configuration UI state.
/// This suffix selects the managed plugin roots and Main's product path projection;
/// it does not select Configuration preferences or its singleton state.
/// </summary>
public sealed class ConfigurationManagedPluginOwnerBinding
{
    public const string ArgumentName = "--managed-plugin-owner-v1";

    public string MainUiStateRoot { get; }
    public string CatalogRoot { get; }
    public string PluginDataRoot { get; }
    public string MainInstallDirectory { get; }
    public string MainBaseDirectory { get; }
    public bool MainPortable { get; }
    public string WorkerBindingSha256 { get; }
    public string OwnerBindingId { get; }

    private ConfigurationManagedPluginOwnerBinding(string mainUiStateRoot, string catalogRoot,
        string pluginDataRoot, string mainInstallDirectory, string mainBaseDirectory, bool mainPortable,
        string workerBindingSha256)
    {
        MainUiStateRoot = mainUiStateRoot;
        CatalogRoot = catalogRoot;
        PluginDataRoot = pluginDataRoot;
        MainInstallDirectory = mainInstallDirectory;
        MainBaseDirectory = mainBaseDirectory;
        MainPortable = mainPortable;
        WorkerBindingSha256 = workerBindingSha256;
        OwnerBindingId = Hash("managed-plugin-owner-v1", MainUiStateRoot.ToUpperInvariant(),
            CatalogRoot.ToUpperInvariant(), PluginDataRoot.ToUpperInvariant(),
            MainInstallDirectory.ToUpperInvariant(), MainBaseDirectory.ToUpperInvariant(),
            MainPortable ? "true" : "false", WorkerBindingSha256);
    }

    public static ConfigurationManagedPluginOwnerBinding CreateForMain(ExistingWorkerDesktopOptions main,
        ExistingWorkerDesktopOptions child, string actualCatalogRoot, string actualPluginDataRoot,
        DesktopInstancePaths mainPaths)
    {
        ArgumentNullException.ThrowIfNull(mainPaths);
        main = RequireOptions(main);
        child = RequireOptions(child);
        if (!SameWorker(main, child))
            throw new ArgumentException("Main and Configuration must retain the same existing Worker generation.");
        var mainRoot = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(main.DesktopDataDirectory);
        var catalog = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(actualCatalogRoot);
        var data = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(actualPluginDataRoot);
        var install = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(mainPaths.InstallDirectory);
        var baseDirectory = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(mainPaths.BaseDirectory);
        if (!mainPaths.DataDirectory.Equals(mainRoot, StringComparison.OrdinalIgnoreCase) ||
            !mainPaths.UserDataDirectory.Equals(mainRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected Main product paths do not match its private UI state.");
        ValidateRoots(mainRoot, child, catalog, data);
        return new(mainRoot, catalog, data, install, baseDirectory, mainPaths.Portable, Fingerprint(child));
    }

    /// <summary>Accepts exactly the unchanged six arguments or the closed managed v1 suffix.</summary>
    public static ConfigurationManagedPluginOwnerLaunch ParseAttachedArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length is not (6 or 14))
            throw new ArgumentException("Configuration requires the six attach-existing arguments or one complete managed plugin owner v1 suffix.");
        var options = ExistingWorkerDesktopOptions.FromArguments(args.Take(6).ToArray())
            ?? throw new ArgumentException("Configuration requires an explicit existing Worker binding.");
        if (args.Length == 6) return new(options, null);
        if (args[6] != ArgumentName)
            throw new ArgumentException("Configuration received an unknown managed plugin owner suffix.");
        var mainRoot = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(args[7]);
        var catalog = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(args[8]);
        var data = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(args[9]);
        var install = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(args[10]);
        var baseDirectory = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(args[11]);
        var portable = args[12] switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ArgumentException("Managed plugin owner has an invalid Main portability value.")
        };
        var supplied = ParseSha256(args[13]);
        ValidateRoots(mainRoot, options, catalog, data);
        var expected = Fingerprint(options);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(supplied), Convert.FromHexString(expected)))
            throw new ArgumentException("Managed plugin owner does not match the attached Worker generation.");
        return new(options, new(mainRoot, catalog, data, install, baseDirectory, portable, expected));
    }

    /// <summary>Rechecks the immutable binding immediately before constructing launch arguments.</summary>
    public void RequireForChild(ExistingWorkerDesktopOptions child)
    {
        child = RequireOptions(child);
        ValidateRoots(MainUiStateRoot, child, CatalogRoot, PluginDataRoot);
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(MainInstallDirectory);
        _ = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(MainBaseDirectory);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(WorkerBindingSha256),
                Convert.FromHexString(Fingerprint(child))))
            throw new ArgumentException("Managed plugin owner no longer matches the attached Worker generation.");
    }

    public void AppendArguments(ICollection<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (var value in new[] { ArgumentName, MainUiStateRoot, CatalogRoot, PluginDataRoot,
                     MainInstallDirectory, MainBaseDirectory, MainPortable ? "true" : "false", WorkerBindingSha256 })
            arguments.Add(value);
    }

    /// <summary>Projects, models, tools, and shared roots remain current Worker settings.</summary>
    public DesktopProductPaths ProductPaths(string? projectsDirectory = null, string? modelDirectory = null,
        string? toolsDirectory = null, string? sharedDirectory = null) =>
        DesktopInstancePaths.ForManagedPluginOwnerSnapshot(MainUiStateRoot, MainInstallDirectory,
            MainBaseDirectory, MainPortable).ProductPaths(projectsDirectory, modelDirectory, toolsDirectory, sharedDirectory);

    private static ExistingWorkerDesktopOptions RequireOptions(ExistingWorkerDesktopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", options.Endpoint.PipeName,
            options.Endpoint.InstanceId, options.WorkerDataDirectory, options.DesktopDataDirectory, options.InstallationId])!;
    }

    private static void ValidateRoots(string mainRoot, ExistingWorkerDesktopOptions child, string catalog, string data)
    {
        mainRoot = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(mainRoot);
        catalog = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(catalog);
        data = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(data);
        var childRoot = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(child.DesktopDataDirectory);
        var workerRoot = ExistingWorkerDesktopOptions.NormalizeLocalDirectory(child.WorkerDataDirectory);
        var selectedUserRoot = InstallationPaths.CurrentUserDataDirectory;
        if (Overlaps(mainRoot, childRoot) || Overlaps(mainRoot, workerRoot) ||
            ExistingWorkerDesktopOptions.OverlapsNormalUserData(mainRoot, selectedUserRoot) ||
            Overlaps(catalog, childRoot) || Overlaps(data, childRoot) ||
            Overlaps(catalog, workerRoot) || Overlaps(data, workerRoot) ||
            ExistingWorkerDesktopOptions.OverlapsNormalUserData(catalog, selectedUserRoot) ||
            ExistingWorkerDesktopOptions.OverlapsNormalUserData(data, selectedUserRoot))
            throw new ArgumentException("Managed plugin ownership must be separate from Configuration UI, Worker data, and normal user data.");
        // The Main supplies its actual roots. They may live outside Main UI state;
        // containment cannot be inferred from the child process or current directory.
        if (Overlaps(catalog, data))
            throw new ArgumentException("The managed plugin catalog and data roots must be separate.");
    }

    private static bool SameWorker(ExistingWorkerDesktopOptions first, ExistingWorkerDesktopOptions second) =>
        first.Endpoint == second.Endpoint &&
        first.WorkerDataDirectory.Equals(second.WorkerDataDirectory, StringComparison.OrdinalIgnoreCase) &&
        first.InstallationId.Equals(second.InstallationId, StringComparison.Ordinal);

    private static bool Overlaps(string first, string second) =>
        ScopedFilePolicy.IsWithin(first, second) || ScopedFilePolicy.IsWithin(second, first);

    private static string Fingerprint(ExistingWorkerDesktopOptions options) =>
        Hash("existing-worker-binding-v1", options.Endpoint.PipeName, options.Endpoint.InstanceId,
            options.WorkerDataDirectory.ToUpperInvariant(), options.InstallationId);

    private static string ParseSha256(string value)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Managed plugin owner requires an exact Worker binding fingerprint.");
        return value.ToUpperInvariant();
    }

    private static string Hash(params string[] fields)
    {
        var text = new StringBuilder();
        foreach (var field in fields) text.Append(field.Length).Append(':').Append(field);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

public sealed record ConfigurationManagedPluginOwnerLaunch(ExistingWorkerDesktopOptions Options,
    ConfigurationManagedPluginOwnerBinding? OwnerBinding);
