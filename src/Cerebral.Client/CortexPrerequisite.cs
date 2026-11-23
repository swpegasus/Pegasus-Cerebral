using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cerebral.Client;

public sealed record CortexPrerequisiteReport(bool Ready, string ReleaseVersion, string InstallDirectory,
    string DataDirectory, string ExecutablePath, string PaoPath);

/// <summary>Offline compatibility check of Cortex-owned installation metadata. Does not admit packages or open Cortex data.</summary>
public static class CortexPrerequisite
{
    // This release adds the bounded single behavior-pack import route and managed
    // same-pack revision semantics used by the Cerebral installer.
    public const string MinimumRelease = "1.2026.09.27.23.39.A";
    private static readonly Dictionary<string, string> RoleComponents = new(StringComparer.Ordinal)
    {
        ["mcp"] = "semantics", ["host"] = "execution", ["service"] = "execution", ["pao"] = "execution",
        ["process-host"] = "execution", ["ui-host"] = "execution", ["launcher"] = "launcher", ["setup"] = "installer"
    };

    public static CortexPrerequisiteReport InspectRegistered(string? directory = null, string? dataDirectory = null) =>
        Inspect(InstalledCortexDiscovery.ReadRegistered(), directory, dataDirectory);

    // The explicit location is a test seam, not a CLI registration override.
    public static CortexPrerequisiteReport Inspect(InstalledCortexLocation? location, string? directory = null, string? dataDirectory = null)
    {
        if (location is null) throw new InvalidDataException("Install Pegasus Cortex before installing Cerebral.");
        var installed = InstalledCortexDiscovery.FromRegistration(location.InstallDirectory, location.DataDirectory)! with
        {
            ReleaseVersion = location.ReleaseVersion
        };
        if (directory is not null) InstalledCortexDiscovery.ValidateSeparateDirectory(installed, directory);
        if (dataDirectory is not null) InstalledCortexDiscovery.ValidateSeparateDirectory(installed, dataDirectory);
        RequireFile(installed.ExecutablePath);
        RequireFile(installed.PaoPath);
        // Cortex may register a separate durable data root. Read the active
        // catalog from that binding, never from a guessed legacy install path.
        var catalogPath = Path.Combine(installed.DataDirectory, "install", "active.v1.json");
        RequireFile(catalogPath);
        using var input = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is <= 0 or > 65536) throw new InvalidDataException("The installed Cortex catalog exceeds the supported size. Repair Cortex first.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var catalog = document.RootElement;
        RequireObject(catalog);
        if (Text(catalog, "contract") != "pegasus-cortex-product-catalog" || Number(catalog, "version") != 1 ||
            Text(catalog, "product") != "Pegasus Cortex" || Text(catalog, "channel") != "Prod Alpha" ||
            Text(catalog, "executionProtocol") != "1.0" || Number(catalog, "cortexMcpSchema") != 2 || Number(catalog, "paoSchema") != 3)
            throw new InvalidDataException("The installed Cortex product contract is incompatible. Install a supported Cortex release first.");
        if (!catalog.TryGetProperty("stateSchemas", out var states)) throw new InvalidDataException("The installed Cortex state schema inventory is missing.");
        RequireObject(states);
        foreach (var name in new[] { "install", "semantic", "execution", "userExecution", "tray" })
            if (Number(states, name) < 1) throw new InvalidDataException("The installed Cortex state schema inventory is invalid.");
        var release = Text(catalog, "releaseVersion");
        RequireRelease(release, minimum: true);
        if (installed.ReleaseVersion is not null && installed.ReleaseVersion != release)
            throw new InvalidDataException("The Cortex ReleaseVersion registration does not match its active product catalog. Repair Cortex first.");
        if (!catalog.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array || roles.GetArrayLength() != RoleComponents.Count)
            throw new InvalidDataException("The installed Cortex component inventory is incomplete. Repair Cortex first.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles.EnumerateArray())
        {
            RequireObject(role);
            var name = Text(role, "role"); var component = Text(role, "component");
            if (!found.Add(name) || !RoleComponents.TryGetValue(name, out var expected) || component != expected)
                throw new InvalidDataException("The installed Cortex component identity is invalid. Repair Cortex first.");
            // A new catalog can still select old execution binaries after a partial component update.
            RequireRelease(Text(role, "componentVersion"), minimum: component == "execution");
            var relative = Text(role, "relativePath");
            if (relative.Length > 240 || relative.Any(char.IsControl) || relative.Contains('"') ||
                relative.Contains(':') || relative.Contains('\\') || Path.IsPathRooted(relative) ||
                relative.Split('/').Any(part => part is "" or "." or "..") ||
                relative.Equals("state", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("state/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed Cortex component path is invalid. Repair Cortex first.");
            // Both parts are validated lexical paths; preserve registered DOS aliases here.
            var path = Path.Combine(installed.InstallDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!path.StartsWith(installed.InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A Cortex component escaped its installation directory.");
            RequireFile(path);
        }
        return new(true, release, installed.InstallDirectory, installed.DataDirectory, installed.ExecutablePath, installed.PaoPath);
    }

    private static void RequireRelease(string version, bool minimum)
    {
        if (!TryRelease(version, out var actual) || !TryRelease(MinimumRelease, out var required) ||
            minimum && Compare(actual, required) < 0)
            throw new InvalidDataException($"Cerebral requires Cortex {MinimumRelease} or later with compatible execution components. Update Cortex first.");
    }

    private static bool TryRelease(string value, out (int Major, int Year, int Month, int Day, int Hour, int Minute, char Revision) version)
    {
        version = default;
        if (value.Length > 40) return false;
        var match = Regex.Match(value, @"^(?<major>[0-9]+)\.(?<year>[0-9]{4})\.(?<month>[0-9]{2})\.(?<day>[0-9]{2})\.(?<hour>[0-9]{2})\.(?<minute>[0-9]{2})\.(?<revision>[A-D])$", RegexOptions.CultureInvariant);
        var number = System.Globalization.NumberStyles.None;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, number, invariant, out var major) ||
            !int.TryParse(match.Groups["year"].Value, number, invariant, out var year) ||
            !int.TryParse(match.Groups["month"].Value, number, invariant, out var month) ||
            !int.TryParse(match.Groups["day"].Value, number, invariant, out var day) ||
            !int.TryParse(match.Groups["hour"].Value, number, invariant, out var hour) ||
            !int.TryParse(match.Groups["minute"].Value, number, invariant, out var minute)) return false;
        version = (major, year, month, day, hour, minute,
            match.Groups["revision"].Value[0]);
        return version.Month is >= 1 and <= 12 && version.Day is >= 1 and <= 31 &&
               version.Hour is >= 0 and <= 23 && version.Minute is >= 0 and <= 59;
    }

    private static int Compare((int Major, int Year, int Month, int Day, int Hour, int Minute, char Revision) left,
        (int Major, int Year, int Month, int Day, int Hour, int Minute, char Revision) right)
    {
        foreach (var result in new[]
        {
            left.Major.CompareTo(right.Major), left.Year.CompareTo(right.Year), left.Month.CompareTo(right.Month),
            left.Day.CompareTo(right.Day), left.Hour.CompareTo(right.Hour), left.Minute.CompareTo(right.Minute),
            left.Revision.CompareTo(right.Revision)
        }) if (result != 0) return result;
        return 0;
    }

    private static void RequireFile(string path)
    {
        InstalledCortexDiscovery.RejectLinks(path);
        if (!File.Exists(path)) throw new InvalidDataException("A required installed Cortex component is missing. Repair Cortex before installing Cerebral.");
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw new InvalidDataException("The installed Cortex catalog contains an invalid or ambiguous object.");
    }

    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text
            ? text : throw new InvalidDataException($"The installed Cortex catalog has an invalid {name}.");
    private static int Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
            ? number : throw new InvalidDataException($"The installed Cortex catalog has an invalid {name}.");
}
