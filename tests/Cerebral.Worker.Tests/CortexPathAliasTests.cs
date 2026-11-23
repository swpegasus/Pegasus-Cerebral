using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Cerebral.Client;

namespace Cerebral.Worker.Tests;

public static class CortexPathAliasTests
{
    public static void Run()
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temporary, "cerebral-cortex-alias-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "Cortex Program");
        var data = Path.Combine(root, "Cortex Data");
        var assertions = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            assertions++;
            Console.WriteLine("PASS: " + label);
        }
        void Reject(Action action, string label, string? expectedMessage = null)
        {
            var rejected = false; var rejection = "No exception was reported.";
            try { action(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { rejection = error.Message; rejected = expectedMessage is null || error.Message.Contains(expectedMessage, StringComparison.Ordinal); }
            Check(rejected, rejected ? label : label + ": " + rejection);
        }
        try
        {
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(install, "PegasusCortex.exe"), "Static path fixture only; never executed.");
            var location = InstalledCortexDiscovery.FromRegistration(install, data)!;
            Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(location, install), "Cortex program root cannot be reused");
            Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(location, data), "Cortex data root cannot be reused");
            Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(location, root), "Cerebral cannot contain the Cortex installation");
            Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(location, Path.Combine(install, "Missing", "Cerebral")),
                "missing descendants retain existing Cortex ancestor identity");
            var sibling = Path.Combine(root, "Cerebral Program", "Missing");
            Check(InstalledCortexDiscovery.ValidateSeparateDirectory(location, sibling) == sibling && !Directory.Exists(sibling),
                "valid missing sibling retains its spelling and is not created by validation");
            foreach (var segment in new[] { "trailing.", "trailing ", "stream:ads", "bad*name", "bad?name", "bad|name",
                "bad<name", "bad>name", "CON", "con.txt", "NUL", "AUX", "PRN", "COM1", "LPT9.log", "CONIN$", "CONOUT$",
                "COM\u00b9", "LPT\u00b2", "COM\u00b3.txt", "CON .txt", ".", ".." })
            {
                Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(location, Path.Combine(root, segment, "Cerebral")),
                    "Windows path segment rejected: " + segment);
                Reject(() => InstalledCortexDiscovery.FromRegistration(Path.Combine(root, segment), data),
                    "registered program segment follows the same validation: " + segment);
            }
            var ordinary = Path.Combine(root, "COM10", "LPT10", "console.txt");
            Check(InstalledCortexDiscovery.ValidateSeparateDirectory(location, ordinary) == ordinary,
                "ordinary names resembling device prefixes remain allowed");
            var file = Path.Combine(root, "ancestor-file");
            File.WriteAllText(file, "static path fixture");
            Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(location, Path.Combine(file, "Cerebral")),
                "an existing file cannot stand in for an installation ancestor");

            if (OperatingSystem.IsWindows())
            {
                var shortInstall = ShortPath(install);
                var shortData = ShortPath(data);
                var shortAncestor = ShortPath(root);
                if (shortInstall.Equals(install, StringComparison.OrdinalIgnoreCase) ||
                    shortData.Equals(data, StringComparison.OrdinalIgnoreCase) ||
                    shortAncestor.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("SKIP: NTFS short-name overlap fixtures; this volume supplied no distinct 8.3 name for every fixture directory.");
                }
                else
                {
                    Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(location, shortInstall),
                        "short-name alias of the Cortex program root is rejected", "Choose a Cerebral directory separate");
                    Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(location, shortData),
                        "short-name alias of the Cortex data root is rejected", "Choose a Cerebral directory separate");
                    Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(location, shortAncestor),
                        "short-name alias of the containing ancestor is rejected", "Choose a Cerebral directory separate");
                    Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(location, Path.Combine(shortInstall, "Missing", "Cerebral")),
                        "missing child beneath short-name Cortex alias is rejected", "Choose a Cerebral directory separate");
                    var aliasSibling = Path.Combine(shortAncestor, "Cerebral Program", "Missing");
                    Check(InstalledCortexDiscovery.ValidateSeparateDirectory(location, aliasSibling) == aliasSibling &&
                        !Directory.Exists(aliasSibling), "short-name ancestor with a separate missing sibling is allowed without creating it");
                    var aliasedRegistration = InstalledCortexDiscovery.FromRegistration(shortInstall, shortData)!;
                    Check(aliasedRegistration.InstallDirectory == shortInstall && aliasedRegistration.DataDirectory == shortData &&
                        aliasedRegistration.ExecutablePath == Path.Combine(shortInstall, "PegasusCortex.exe"),
                        "registration aliases retain the original launch spelling");
                    Check(InstalledCortexDiscovery.ValidateExplicitPath(aliasedRegistration.ExecutablePath) == aliasedRegistration.ExecutablePath,
                        "explicit installed executable validation preserves registered short-name spelling");
                    Reject(() => InstalledCortexDiscovery.ValidateSeparateDirectory(aliasedRegistration, install),
                        "long spelling cannot bypass an aliased Cortex registration", "Choose a Cerebral directory separate");
                }
            }
            else Console.WriteLine("SKIP: Windows 8.3 alias fixtures require Windows.");
            Console.WriteLine($"Cortex path identity: {assertions} checks passed (static filesystem fixtures; no registry changes or application launch).");
        }
        finally
        {
            var exact = Path.GetFullPath(root);
            if (!exact.StartsWith(temporary.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(exact).StartsWith("cerebral-cortex-alias-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing fixture cleanup outside the exact temporary workspace.");
            if (Directory.Exists(exact)) Directory.Delete(exact, recursive: true);
        }
    }

    internal static string ShortPath(string path)
    {
        var buffer = new StringBuilder(261);
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        if (length == 0) throw new IOException("Could not inspect the fixture's Windows short path.",
            new Win32Exception(Marshal.GetLastWin32Error()));
        if (length >= buffer.Capacity) throw new IOException("Fixture short path exceeds its bounded buffer.");
        return buffer.ToString().TrimEnd(Path.DirectorySeparatorChar);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int capacity);
}
