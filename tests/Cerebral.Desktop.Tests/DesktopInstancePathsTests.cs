using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

internal static class DesktopInstancePathsTests
{
    private static readonly string[] Variables = [DesktopInstancePaths.SourceDataEnvironmentVariable,
        DesktopInstancePaths.SourceProgramEnvironmentVariable, ConnectionStore.ProcessLocalDataEnvironmentVariable];

    public static int Run()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var root = Path.Combine(Path.GetTempPath(), "cerebral-instance-paths-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data one");
        var other = Path.Combine(root, "Data two");
        var program = Path.Combine(root, "Program payload");
        var saved = Variables.Select(Environment.GetEnvironmentVariable).ToArray();
        Directory.CreateDirectory(root);
        try
        {
            var source = DesktopInstancePaths.ForSource(data + Path.DirectorySeparatorChar, program);
            Check(source.SourceLocal && !source.Portable, "Source storage must remain distinct from installed and portable storage.");
            Check(source.DataDirectory == data && source.UserDataDirectory == data && source.BaseDirectory == data,
                "Source product and user data must use the explicit root.");
            Check(source.InstallDirectory == program, "The program root must come from its explicit binding.");
            Check(source.ConnectionDataDirectory == Path.Combine(data, "Desktop"), "The source connection must use its Desktop child.");
            Check(source.PluginDirectory == Path.Combine(data, "Plugins", "Desktop") &&
                source.PluginDataDirectory == Path.Combine(data, "PluginData"), "The catalog and plugin data must share the instance.");
            var paths = source.ProductPaths();
            Check(paths.DataDirectory == data && paths.UserDataDirectory == data && paths.InstallDirectory == program &&
                paths.BaseDirectory == data && !paths.Portable, "Source product paths must not leak installed roots.");
            Check(paths.ProjectsDirectory == Path.Combine(data, "Projects") && paths.ModelsDirectory == Path.Combine(data, "models") &&
                paths.ToolsDirectory == Path.Combine(program, "Tools") && paths.SharedDirectory == Path.Combine(data, "Shared"),
                "Source resources retain isolated data while bundled tools follow the selected payload.");
            var configured = source.ProductPaths(Path.Combine(other, "Projects"), Path.Combine(other, "Models"),
                Path.Combine(other, "Tools"), Path.Combine(other, "Shared"));
            Check(configured.ProjectsDirectory == Path.Combine(other, "Projects") && configured.ModelsDirectory == Path.Combine(other, "Models") &&
                configured.ToolsDirectory == Path.Combine(other, "Tools") && configured.SharedDirectory == Path.Combine(other, "Shared"),
                "Explicit resource settings must remain independent.");
            Check(source.ApplicationIdentity.Matches(DesktopApplicationIdentity.SourceLocal(data.ToUpperInvariant())),
                "Instance identity must compare canonical Windows paths without case sensitivity.");
            Check(!source.ApplicationIdentity.Matches(DesktopApplicationIdentity.SourceLocal(other)),
                "Another source data root must not activate this Configuration instance.");
            Check(!source.ApplicationIdentity.Matches(DesktopApplicationIdentity.DesktopInstance(data)),
                "Source and deployed Configuration must remain separate even with the same explicitly selected user root.");
            var diagnosticsProjection = typeof(DesktopPreferenceDiagnostics).GetMethod("ProjectDiagnosticsDirectory",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            string Diagnostics(string? privateRoot, Func<string> selectedRoot) =>
                (string)diagnosticsProjection.Invoke(null, new object?[] { privateRoot, selectedRoot })!;
            Check(Diagnostics(null, () => source.UserDataDirectory) == Path.Combine(data, "Diagnostics") &&
                Diagnostics(other, () => throw new InvalidOperationException("Private diagnostics must not read ordinary paths.")) ==
                    Path.Combine(other, "Diagnostics"),
                "Diagnostics follows the selected source root or an explicit private override without process selection.");
            var importProjection = typeof(DesktopPreferenceDiagnostics).GetMethod("ShouldImportLegacyUi",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            bool ShouldImport(DesktopInstancePaths selected, string ordinaryRoot) =>
                (bool)importProjection.Invoke(null, new object[] { selected, ordinaryRoot })!;
            var ordinary = DesktopInstancePaths.ForManagedPluginOwnerSnapshot(other, program, program, false);
            var portable = DesktopInstancePaths.ForManagedPluginOwnerSnapshot(other, program, program, true);
            Check(ShouldImport(ordinary, other) && !ShouldImport(source, data) &&
                !ShouldImport(portable, other) && !ShouldImport(ordinary, data),
                "Only the matching ordinary user root may import legacy UI files.");
            var copyMethod = typeof(DesktopPreferenceDiagnostics).GetMethod("CopyKnownLegacyUiFiles",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            void CopyKnown(string legacy, string selected) =>
                _ = copyMethod.Invoke(null, new object[] { legacy, selected });
            var oldUi = Path.Combine(root, "LegacyUi");
            var newUi = Path.Combine(root, "SelectedUi");
            Directory.CreateDirectory(oldUi); Directory.CreateDirectory(newUi);
            foreach (var name in new[] { "preferences.json", "string-preferences.json",
                         "Orchestrator.window.json", "Integrator.window.json" })
                File.WriteAllText(Path.Combine(oldUi, name), "legacy:" + name);
            File.WriteAllText(Path.Combine(oldUi, "unknown.window.json"), "unknown");
            File.WriteAllText(Path.Combine(oldUi, "connection.json"), "connection");
            File.WriteAllText(Path.Combine(newUi, "preferences.json"), "selected");
            CopyKnown(oldUi, newUi);
            Check(File.ReadAllText(Path.Combine(newUi, "preferences.json")) == "selected" &&
                File.ReadAllText(Path.Combine(newUi, "string-preferences.json")) == "legacy:string-preferences.json" &&
                File.ReadAllText(Path.Combine(newUi, "Orchestrator.window.json")) == "legacy:Orchestrator.window.json" &&
                File.ReadAllText(Path.Combine(newUi, "Integrator.window.json")) == "legacy:Integrator.window.json",
                "Known missing UI files copy while an existing selected preference stays authoritative.");
            Check(File.ReadAllText(Path.Combine(oldUi, "preferences.json")) == "legacy:preferences.json" &&
                !File.Exists(Path.Combine(newUi, "unknown.window.json")) &&
                !File.Exists(Path.Combine(newUi, "connection.json")),
                "Compatibility retains originals and excludes unknown windows and connection state.");
            CopyKnown(oldUi, oldUi);
            Check(File.ReadAllText(Path.Combine(oldUi, "preferences.json")) == "legacy:preferences.json",
                "An unchanged selected UI root performs no copy.");
            var completeUi = Path.Combine(root, "CompleteSelectedUi");
            Directory.CreateDirectory(completeUi);
            foreach (var name in new[] { "preferences.json", "string-preferences.json",
                         "Orchestrator.window.json", "Integrator.window.json" })
                File.WriteAllText(Path.Combine(completeUi, name), "selected");
            CopyKnown("relative-unused-legacy", completeUi);
            Check(File.ReadAllText(Path.Combine(completeUi, "preferences.json")) == "selected",
                "A complete selected profile never inspects an unused legacy root.");
            var brokenOldUi = Path.Combine(root, "BrokenLegacyUi");
            var brokenNewUi = Path.Combine(root, "BrokenSelectedUi");
            Directory.CreateDirectory(brokenOldUi);
            File.WriteAllText(Path.Combine(brokenOldUi, "preferences.json"), "first");
            Directory.CreateDirectory(Path.Combine(brokenOldUi, "string-preferences.json"));
            var copyFailed = false;
            try { CopyKnown(brokenOldUi, brokenNewUi); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException { InnerException: InvalidDataException })
            { copyFailed = true; }
            Check(copyFailed && File.ReadAllText(Path.Combine(brokenNewUi, "preferences.json")) == "first" &&
                !File.Exists(Path.Combine(brokenNewUi, "string-preferences.json")) &&
                !Directory.EnumerateFiles(brokenNewUi, ".cerebral-ui-copy-*.new").Any(),
                "A copy failure is actionable, preserves completed files, and publishes no partial selected file.");
            var linkedOldUi = Path.Combine(root, "LinkedLegacyUi");
            Directory.CreateDirectory(linkedOldUi);
            try { File.CreateSymbolicLink(Path.Combine(linkedOldUi, "preferences.json"), Path.Combine(oldUi, "preferences.json")); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
            if (File.Exists(Path.Combine(linkedOldUi, "preferences.json")))
            {
                var linkRejected = false;
                try { CopyKnown(linkedOldUi, Path.Combine(root, "LinkedSelectedUi")); }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException { InnerException: IOException })
                { linkRejected = true; }
                Check(linkRejected, "A linked known UI file cannot be imported as ordinary preference data.");
            }
            foreach (var invalid in new[] { "", "relative", data + '\0' })
            {
                var rejected = false;
                try { _ = DesktopInstancePaths.ForSource(invalid, program); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Invalid data roots must be rejected.");
                rejected = false;
                try { _ = DesktopInstancePaths.ForSource(data, invalid); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Invalid program roots must be rejected.");
            }
            foreach (var variable in Variables) Environment.SetEnvironmentVariable(variable, null);
            Check(DesktopInstancePaths.ReadInheritedSource() is null, "An absent binding must remain absent.");
            Environment.SetEnvironmentVariable(Variables[0], data);
            var incomplete = false;
            try { _ = DesktopInstancePaths.ReadInheritedSource(); } catch (ArgumentException) { incomplete = true; }
            Check(incomplete, "Half an inherited binding must not silently select defaults.");
            Environment.SetEnvironmentVariable(Variables[1], program);
            Environment.SetEnvironmentVariable(Variables[2], Path.Combine(other, "Desktop"));
            var mismatch = false;
            try { _ = DesktopInstancePaths.ReadInheritedSource(); } catch (ArgumentException) { mismatch = true; }
            Check(mismatch, "An inherited connection must not point to another source instance.");
            Environment.SetEnvironmentVariable(Variables[2], source.ConnectionDataDirectory);
            Check(DesktopInstancePaths.ReadInheritedSource() == source, "A complete inherited binding must round trip.");
            Environment.SetEnvironmentVariable(Variables[1], "relative");
            var malformed = false;
            try { _ = DesktopInstancePaths.ReadInheritedSource(); } catch (ArgumentException) { malformed = true; }
            Check(malformed, "Malformed inherited roots must not become defaults.");
            var installedPaths = DesktopInstancePaths.ForInstallation();
            Check(!installedPaths.SourceLocal && installedPaths.Portable == InstallationPaths.IsPortable &&
                installedPaths.DataDirectory == Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallationPaths.DataDirectory)) &&
                installedPaths.UserDataDirectory == Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallationPaths.CurrentUserDataDirectory)),
                "Installed/portable roots must keep existing ownership resolution.");
            Check(installedPaths.ProductPaths().ToolsDirectory == InstallationPaths.ToolsDirectory, "Bundled tools must use the running payload; explicit settings remain independent.");
            Check(installedPaths.PluginDirectory == Path.Combine(installedPaths.InstallDirectory, "Plugins", "Desktop") &&
                installedPaths.PluginDataDirectory == Path.Combine(installedPaths.UserDataDirectory, "PluginData"),
                "Ordinary launches must separate running plugin code from durable plugin data.");
            foreach (var fixtureData in new[] { data, other })
            {
                var folder = Path.Combine(fixtureData, "Plugins", "Desktop", "path-fixture");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, DesktopPluginCatalog.ManifestFileName), "{}");
                var valid = Path.Combine(fixtureData, "Plugins", "Desktop", "path-valid");
                Directory.CreateDirectory(valid);
                // Catalog metadata inspection never loads this assembly. Installation/runtime tests
                // separately enforce a real managed entrypoint; this fixture exercises list isolation only.
                File.WriteAllBytes(Path.Combine(valid, "Fixture.dll"), [0x4d, 0x5a]);
                File.WriteAllText(Path.Combine(valid, DesktopPluginCatalog.ManifestFileName),
                    JsonSerializer.Serialize(new DesktopPluginManifest
                    {
                        Id = "path-valid", Name = "Valid metadata fixture", Description = "Catalog metadata only",
                        Assembly = "Fixture.dll", EntryType = "Fixture.Plugin"
                    }, CerebralJson.Options));
            }
            var inherited = Child("inherited", data, program, other);
            Check(inherited.GetProperty("data").GetString() == data && inherited.GetProperty("pluginRoot").GetString() == source.PluginDirectory &&
                inherited.GetProperty("pluginData").GetString() == source.PluginDataDirectory, "A child must adopt the exact catalog/data binding.");
            Check(inherited.GetProperty("connection").GetString() == Path.Combine(data, "Desktop", "connection.json"), "A child must adopt the exact connection declaration.");
            Check(inherited.GetProperty("program").GetString() == program && inherited.GetProperty("userData").GetString() == data, "Child product roots must retain ownership.");
            Check(inherited.GetProperty("fixtureCount").GetInt32() == 1, "The selected catalog must see its instance-owned fixture.");
            Check(inherited.GetProperty("validCount").GetInt32() == 1, "An invalid package must not hide valid catalog metadata.");
            Check(inherited.GetProperty("dataRejected").GetBoolean() && inherited.GetProperty("programRejected").GetBoolean() &&
                inherited.GetProperty("caseAccepted").GetBoolean(), "The binding must reject switching roots and accept canonical case variants.");
            var explicitPaths = Child("explicit", data, program, other);
            Check(explicitPaths.GetProperty("data").GetString() == other &&
                explicitPaths.GetProperty("connection").GetString() == Path.Combine(other, "Desktop", "connection.json"), "Explicit launch arguments must override inherited roots.");
            Check(explicitPaths.GetProperty("fixtureCount").GetInt32() == 1, "Explicit selection must use only its own catalog.");
            Check(explicitPaths.GetProperty("validCount").GetInt32() == 1, "Explicit selection must preserve the valid row beside an invalid package.");
            var installed = Child("installed", data, program, other);
            Check(!installed.GetProperty("source").GetBoolean() && installed.GetProperty("userData").GetString() == installedPaths.UserDataDirectory,
                "Ordinary installed-layout launch must retain the selected durable dataset.");
            Check(installed.GetProperty("sourceRejected").GetBoolean(), "An already selected ordinary binding must reject a mid-process switch.");
            var normal = Child("normal", data, program, other);
            Check(!normal.GetProperty("source").GetBoolean() && normal.GetProperty("data").GetString() == installedPaths.DataDirectory &&
                normal.GetProperty("userData").GetString() == installedPaths.UserDataDirectory,
                "An ordinary source/copy launch must use the same selected durable roots.");
            Check(normal.GetProperty("pluginRoot").GetString() == installedPaths.PluginDirectory &&
                normal.GetProperty("program").GetString() == installedPaths.InstallDirectory,
                "An ordinary copy must retain its running plugin payload.");
            var copiedExplicit = Child("copied-explicit", data, program, other);
            Check(copiedExplicit.GetProperty("source").GetBoolean() && copiedExplicit.GetProperty("data").GetString() == other &&
                copiedExplicit.GetProperty("program").GetString() == program && copiedExplicit.GetProperty("validCount").GetInt32() == 1,
                "A complete copied payload must still allow explicitly isolated source data.");
            return checks;
        }
        finally
        {
            for (var index = 0; index < Variables.Length; index++) Environment.SetEnvironmentVariable(Variables[index], saved[index]);
            if (Path.GetFullPath(root).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    public static void RunChild(string[] args)
    {
        var mode = args[1]; var data = args[2]; var program = args[3]; var other = args[4];
        var paths = DesktopInstancePaths.InitializeConfiguration(mode is "explicit" or "copied-explicit" ? ["--source-local", other] : [],
            mode is "installed" or "copied-explicit", program);
        var dataRejected = false; var programRejected = false; var caseAccepted = false; var sourceRejected = false; var fixtureCount = 0; var validCount = 0;
        if (paths.SourceLocal)
        {
            try { _ = DesktopInstancePaths.UseSource(Path.Combine(paths.DataDirectory, "different"), program); } catch (InvalidOperationException) { dataRejected = true; }
            try { _ = DesktopInstancePaths.UseSource(paths.DataDirectory, Path.Combine(program, "different")); } catch (InvalidOperationException) { programRejected = true; }
            caseAccepted = ReferenceEquals(paths, DesktopInstancePaths.UseSource(paths.DataDirectory.ToUpperInvariant(), program.ToUpperInvariant()));
            var catalog = new DesktopPluginCatalog().List();
            fixtureCount = catalog.Count(plugin => plugin.Id == "path-fixture" && plugin.Error is not null);
            validCount = catalog.Count(plugin => plugin.Id == "path-valid" && plugin.Error is null);
        }
        else
        {
            try { _ = DesktopInstancePaths.InitializeConfiguration(["--source-local", data], true, program); } catch (InvalidOperationException) { sourceRejected = true; }
        }
        var projected = paths.ProductPaths();
        Console.WriteLine(JsonSerializer.Serialize(new { source = paths.SourceLocal, data = paths.DataDirectory,
            userData = projected.UserDataDirectory, program = projected.InstallDirectory,
            pluginRoot = DesktopPluginCatalog.DefaultDirectory, pluginData = DesktopPluginCatalog.DefaultDataDirectory,
            connection = ConnectionStore.FilePath, fixtureCount, validCount, dataRejected, programRejected, caseAccepted, sourceRejected }));
    }

    private static JsonElement Child(string mode, string data, string program, string other)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The fixture process path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in new[] { "--instance-paths-child", mode, data, program, other }) start.ArgumentList.Add(argument);
        foreach (var variable in Variables) start.Environment.Remove(variable);
        if (mode is not ("installed" or "normal"))
        {
            start.Environment[Variables[0]] = data;
            start.Environment[Variables[1]] = program;
            start.Environment[Variables[2]] = Path.Combine(data, "Desktop");
        }
        using var child = Process.Start(start) ?? throw new IOException("The isolated child fixture did not start.");
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(20_000)) { child.Kill(entireProcessTree: true); throw new TimeoutException("The child fixture exceeded its timeout."); }
        if (child.ExitCode != 0) throw new InvalidOperationException(error.GetAwaiter().GetResult());
        using var parsed = JsonDocument.Parse(output.GetAwaiter().GetResult());
        return parsed.RootElement.Clone();
    }
}
