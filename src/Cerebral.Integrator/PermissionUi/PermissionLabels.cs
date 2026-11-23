using System.Globalization;

namespace Cerebral.PermissionUi;

internal static class PermissionLabels
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["file.read"] = "Read files", ["file.write"] = "Write files", ["file.list"] = "List files",
        ["file.edit"] = "Edit files", ["file.edit-range"] = "Edit a file range", ["file.search"] = "Search files",
        ["process.run"] = "Run a process", ["process.execute"] = "Run processes",
        ["filesystem.read"] = "Read local files", ["filesystem.write"] = "Change local files",
        ["diagnostics"] = "View diagnostics", ["network.http"] = "Make HTTP requests",
        ["git.read"] = "Read Git information", ["git.write"] = "Change Git repositories",
        ["shared.read"] = "Read shared files", ["shared.write"] = "Change shared files",
        ["math.calculate"] = "Perform calculations", ["administration"] = "Administrative operations",
        ["agents.collaborate"] = "Collaborate with agents"
    };
    internal static string Name(string id, bool capability)
    {
        if (Labels.TryGetValue(id, out var label)) return label;
        // A presentation fallback, never an interpretation of an unknown grant's authority.
        var text = string.Join(" ", id.Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? (capability ? "Saved capability" : "Saved tool") : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }
}
