using System.IO;
using System.Reflection;
using Cerebral.Core;
using Microsoft.Win32;

namespace Cerebral.Desktop.Tests;

internal static class RuntimePathTests
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-runtime-roots-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(root, "Copied payload");
        var home = Path.Combine(root, "Selected data");
        var infer = typeof(InstallationPaths).GetMethod("InferInstallDirectory", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The runtime-root resolver is unavailable.");
        var portable = typeof(InstallationPaths).GetMethod("ResolvePortableRoot", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The portable-marker resolver is unavailable.");
        var projectRegistration = typeof(InstallationPaths).GetMethod("ProjectRegistration", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The registered-root projector is unavailable.");
        var selectRegisteredPath = typeof(InstallationPaths).GetMethod("SelectRegisteredPath", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The registered-root selector is unavailable.");
        var registeredStringValue = typeof(InstallationPaths).GetMethod("RegisteredStringValue", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The registered-root value-kind admission is unavailable.");
        string Infer(string path) => (string)infer.Invoke(null, [path])!;
        string? Portable(string path) => (string?)portable.Invoke(null, [path]);
        object Project(bool present, object? install, object? baseDirectory, object? data, object? userData, bool unavailable = false) =>
            projectRegistration.Invoke(null, [present, install, baseDirectory, data, userData, unavailable])!;
        string State(object projection) => projection.GetType().GetProperty("State")!.GetValue(projection)!.ToString()!;
        string? Root(object projection, string property) => (string?)projection.GetType().GetProperty(property)!.GetValue(projection);
        string? Select(object projection, string name) => (string?)selectRegisteredPath.Invoke(null, [projection, name]);
        object? Value(RegistryValueKind kind, object? value) => registeredStringValue.Invoke(null, [kind, value]);
        bool Refuses(object projection)
        {
            foreach (var name in new[] { "BaseDir", "DataDir", "UserDataDir" })
            {
                try { _ = Select(projection, name); return false; }
                catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
            }
            return true;
        }
        var savedHome = Environment.GetEnvironmentVariable(InstallationPaths.HomeEnvironmentVariable);
        Directory.CreateDirectory(root);
        try
        {
            Check(Infer(payload) == payload, "A flat copied payload must resolve to its own running directory.");
            Check(Infer(Path.Combine(payload, "Applications", "Desktop")) == payload,
                "Grouped Desktop binaries must resolve their payload root.");
            Check(Infer(Path.Combine(payload, "Applications", "Worker")) == payload,
                "Grouped Worker binaries must resolve the same payload root.");
            Check(Infer(Path.Combine(payload, "Orchestrator")) == payload &&
                Infer(Path.Combine(payload, "Integrator")) == payload &&
                Infer(Path.Combine(payload, "Worker")) == payload,
                "Legacy sibling application folders must retain their payload root.");
            Check(ManagementProcess.RegisteredRootMatches(payload, payload.ToUpperInvariant() + Path.DirectorySeparatorChar, false),
                "Installed-management identity must compare canonical paths without case sensitivity.");
            Check(!ManagementProcess.RegisteredRootMatches(payload, Path.Combine(root, "Installed payload"), false),
                "A copied payload cannot acquire another installation's management identity.");
            Check(!ManagementProcess.RegisteredRootMatches(payload, null, false) &&
                !ManagementProcess.RegisteredRootMatches(payload, payload, true),
                "Unregistered and explicit portable payloads must not acquire installed-management identity.");
            var absent = Project(false, null, null, null, null);
            var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pegasus", "Cerebral");
            Check(State(absent) == "Absent" && Select(absent, "DataDir") is null &&
                Root(absent, "InstallDirectory") == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pegasus", "Cerebral") &&
                Root(absent, "BaseDirectory") == programData && Root(absent, "DataDirectory") == programData &&
                Root(absent, "UserDataDirectory") == Path.Combine(programData, "Users"),
                "Program Files and ProgramData are installation defaults, not a silent new legacy writer location.");
            var registeredInstall = Path.Combine(root, "Registered payload");
            var registeredBase = Path.Combine(root, "Registered data");
            var registeredUsers = Path.Combine(registeredBase, "Users");
            var registered = Project(true, registeredInstall, registeredBase, registeredBase, registeredUsers);
            Check(State(registered) == "Complete" && Select(registered, "DataDir") == registeredBase &&
                Select(registered, "BaseDir") == registeredBase && Select(registered, "UserDataDir") == registeredUsers &&
                Infer(payload) == payload && !ManagementProcess.RegisteredRootMatches(payload, registeredInstall, false),
                "A copied payload reads an exact registered DataDir even when DataDir equals BaseDir, without gaining installed-management identity.");
            Check(Value(RegistryValueKind.String, registeredBase) is string admitted && admitted == registeredBase &&
                new[] { Value(RegistryValueKind.ExpandString, registeredBase),
                        Value(RegistryValueKind.MultiString, new[] { registeredBase }),
                        Value(RegistryValueKind.DWord, 1), Value(RegistryValueKind.String, 1) }
                    .All(value => value is null) &&
                Refuses(Project(true, registeredInstall, registeredBase,
                    Value(RegistryValueKind.ExpandString, registeredBase), registeredUsers)),
                "Only literal REG_SZ paths may enter the registered-root projection.");
            var separateData = Path.Combine(root, "Separate service data");
            var separated = Project(true, registeredInstall, registeredBase, separateData, registeredUsers);
            Check(State(separated) == "Complete" && Select(separated, "DataDir") == separateData &&
                Select(separated, "BaseDir") == registeredBase,
                "An explicit DataDir remains authoritative instead of becoming a child of BaseDir.");
            Check(new[] { Project(true, null, registeredBase, separateData, registeredUsers),
                    Project(true, registeredInstall, null, separateData, registeredUsers),
                    Project(true, registeredInstall, registeredBase, null, registeredUsers),
                    Project(true, registeredInstall, registeredBase, separateData, null) }
                .All(projection => State(projection) == "Partial" && Refuses(projection)),
                "Any missing registered root must be refused before a fresh store is selected.");
            Check(new object?[] { "relative", @"\\server\share", registeredBase + ":stream", " ",
                    registeredBase + "\\bad\"name", registeredBase + "\\bad*name", registeredBase + "\\bad?name",
                    Path.GetPathRoot(registeredBase),
                    new string('x', 2049) }
                .All(path => Refuses(Project(true, registeredInstall, registeredBase, path, registeredUsers))),
                "Malformed registered local roots must be refused before state access.");
            var unavailable = Project(false, null, null, null, null, unavailable: true);
            Check(State(unavailable) == "Unavailable" && Refuses(unavailable),
                "An unreadable registration must not be treated as absent.");
            Directory.CreateDirectory(payload);
            var application = Path.Combine(payload, "Applications", "Desktop");
            Check(Portable(application) is null, "A copy alone must not opt into self-contained data.");
            var marker = Path.Combine(payload, "installation.ini");
            File.WriteAllText(marker, "[Cerebral]\nProduct=PegasusCerebral\nPortable=0\n");
            Check(Portable(application) is null, "A non-portable installation marker must keep durable-data selection.");
            File.WriteAllText(marker, "[Cerebral]\nProduct=PegasusCerebral\nPortable=1\n");
            Check(Portable(application) == payload, "An explicit portable marker must resolve the containing payload.");
            File.WriteAllText(marker, "[Cerebral]\nProduct=AnotherProduct\nPortable=1\n");
            Check(Portable(application) is null, "Another product's marker must not select Cerebral portable data.");
            var runningRoot = InstallationPaths.InstallDirectory;
            Check(ManagementProcess.SourceProgramDirectory == runningRoot &&
                BundledDesktopPluginPaths.DirectoryPath == Path.Combine(runningRoot, "Plugins", "Desktop") &&
                InstallationPaths.ToolsDirectory == Path.Combine(runningRoot, "Tools"),
                "Runtime code, bundled plugins and tools must share the executing payload.");
            Environment.SetEnvironmentVariable(InstallationPaths.HomeEnvironmentVariable, home);
            Check(InstallationPaths.InstallDirectory == runningRoot &&
                InstallationPaths.ToolsDirectory == Path.Combine(runningRoot, "Tools"),
                "A selected data home must not redirect binaries or bundled tools.");
            Check(InstallationPaths.IsPortable ||
                InstallationPaths.BaseDirectory == home && InstallationPaths.DataDirectory == Path.Combine(home, "Data") &&
                InstallationPaths.CurrentUserDataDirectory.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "An explicit data home must retain durable settings independently from the running payload.");
            return checks;
        }
        finally
        {
            Environment.SetEnvironmentVariable(InstallationPaths.HomeEnvironmentVariable, savedHome);
            if (Path.GetFullPath(root).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
}
