using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Cerebral.AppearanceThemePlugin;
using Cerebral.Core;

namespace Cerebral.Desktop.Tests;

internal static class AppearancePortableThemeFileTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        static async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }
        static void Reject<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        var root = Path.Combine(Path.GetTempPath(), "Pegasus-Cerebral", "AppearancePortableTheme", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var custom = JsonNode.Parse(DesktopThemePresetPolicy.Serialize(DesktopThemePresetPolicy.BlueWhite))!.AsObject();
            custom["id"] = "portable.blue-white";
            custom["name"] = "Portable Blue White";
            custom["light"]!["line"] = "#CCD8EB";
            using var customDocument = JsonDocument.Parse(custom.ToJsonString());
            var expected = DesktopThemePresetPolicy.Parse(customDocument.RootElement);
            var destination = Path.Combine(root, "Portable Blue White.CerebralTheme");

            await PortableThemeFile.WriteAsync(destination, customDocument.RootElement);
            var originalBytes = await File.ReadAllBytesAsync(destination);
            Check(originalBytes.Length is > 0 and <= PortableThemeFile.MaximumBytes,
                "A portable export must be nonempty bounded JSON on the real filesystem.");
            Check(!Encoding.UTF8.GetString(originalBytes).Contains(root, StringComparison.OrdinalIgnoreCase),
                "A portable export must not embed its source or destination machine path.");
            var imported = await PortableThemeFile.ReadAsync(destination);
            var accepted = DesktopThemePresetPolicy.Parse(imported);
            Check(DesktopThemePresetPolicy.Serialize(accepted) == DesktopThemePresetPolicy.Serialize(expected),
                "Writing and reading a portable theme file must preserve the exact normalized preset.");

            var unreadable = JsonNode.Parse(DesktopThemePresetPolicy.Serialize(expected))!.AsObject();
            unreadable["dark"]!["ink"] = unreadable["dark"]!["surface"]!.GetValue<string>();
            var unreadablePath = Path.Combine(root, "Unreadable.CerebralTheme");
            await File.WriteAllTextAsync(unreadablePath, unreadable.ToJsonString());
            var unreadableImport = await PortableThemeFile.ReadAsync(unreadablePath);
            Reject<ArgumentException>(() => DesktopThemePresetPolicy.Parse(unreadableImport),
                "A syntactically valid import with unreadable text must fail semantic validation.");
            Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(originalBytes),
                "A rejected imported palette must not alter an existing exported preset file.");

            var malformedPath = Path.Combine(root, "Malformed.CerebralTheme");
            await File.WriteAllTextAsync(malformedPath, "{\"schemaVersion\":1");
            await RejectAsync<JsonException>(() => PortableThemeFile.ReadAsync(malformedPath),
                "Malformed theme JSON must be rejected by the bounded file reader.");
            Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(originalBytes),
                "A malformed import must leave the existing portable preset unchanged.");

            using var invalidExport = JsonDocument.Parse("[]");
            await RejectAsync<InvalidDataException>(() => PortableThemeFile.WriteAsync(destination, invalidExport.RootElement),
                "A non-object export must be rejected before replacing the existing file.");
            Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(originalBytes),
                "An invalid export must preserve the existing destination bytes.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await RejectAsync<OperationCanceledException>(
                () => PortableThemeFile.WriteAsync(destination, customDocument.RootElement, cancelled.Token),
                "A cancelled export must report cancellation.");
            Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(originalBytes) &&
                  !Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Any(),
                "A cancelled export must preserve the destination and remove its task-owned temporary file.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        return checks;
    }
}
