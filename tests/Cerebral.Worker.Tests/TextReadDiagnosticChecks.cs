using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Worker.Tests;

internal static class TextReadDiagnosticChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("Text read diagnostics: " + name); count++; }
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-text-read-diagnostics-" + Guid.NewGuid().ToString("N")));
        var scope = Path.Combine(root, "scope");
        Directory.CreateDirectory(scope);
        try
        {
            var profile = new AgentProfile { Id = "text-read-fixture", Name = "Text read fixture",
                AllowedRoots = [scope], AllowedTools = ["file.read", "file.inspect"], Capabilities = ["filesystem.read"] };
            await using var dispatcher = new ToolDispatcher(() => new HostSettings(), stateDirectory: Path.Combine(root, "authorization"));
            async Task<ToolResult> Invoke(string name, string path, int startLine = 1, int lineCount = 200) =>
                await dispatcher.ExecuteAsync(new(name, name == "file.read"
                    ? CerebralJson.Element(new { path, startLine, lineCount }) : CerebralJson.Element(new { path })),
                    new(profile, "text-read-session", "text-read-run") { EnforceProfileScope = true, EffectId = Guid.NewGuid().ToString("N") }, default);
            var binary = Path.Combine(scope, "compiled.dll");
            byte[] binaryBytes = [0x4d, 0x5a, 0x90, 0x00, 0x03, 0x00];
            await File.WriteAllBytesAsync(binary, binaryBytes);
            foreach (var operation in new[] { "file.read", "file.inspect" })
            {
                var result = await Invoke(operation, binary);
                Check(!result.Success && !result.Truncated && result.Evidence?.Outcome == ToolExecutionOutcome.Rejected,
                    operation + " rejects invalid text without uncertain execution");
                Check(result.Content.StartsWith(operation + " requires UTF-8", StringComparison.Ordinal) &&
                    result.Content.Contains("matching source file", StringComparison.Ordinal) &&
                    result.Content.Contains("different input or operation", StringComparison.Ordinal) &&
                    !result.Content.Contains("Unable to translate bytes", StringComparison.Ordinal),
                    operation + " provides a supported correction rather than decoder bytes");
            }
            Check((await File.ReadAllBytesAsync(binary)).SequenceEqual(binaryBytes), "reads leave binary bytes unchanged");
            var textPath = Path.Combine(scope, "source.cs");
            var textBytes = new UTF8Encoding(false, true).GetBytes("alpha\nbeta\n");
            await File.WriteAllBytesAsync(textPath, textBytes);
            var read = await Invoke("file.read", textPath, 2, 1);
            using (var parsed = JsonDocument.Parse(read.Content))
                Check(read.Success && read.Truncated && parsed.RootElement.GetProperty("content").GetString() == "beta" &&
                    parsed.RootElement.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(textBytes)).ToLowerInvariant(),
                    "valid UTF-8 keeps bounded line selection and whole-file hash");
            foreach (var encoding in new Encoding[] { new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
            {
                var unicodePath = Path.Combine(scope, encoding.CodePage + ".txt");
                await File.WriteAllBytesAsync(unicodePath, encoding.GetPreamble().Concat(encoding.GetBytes("caf\u00e9")).ToArray());
                var unicodeRead = await Invoke("file.read", unicodePath);
                using var parsed = JsonDocument.Parse(unicodeRead.Content);
                Check(unicodeRead.Success && parsed.RootElement.GetProperty("content").GetString() == "caf\u00e9",
                    "supported BOM Unicode remains readable " + encoding.CodePage);
            }
            var outside = Path.Combine(root, "outside.dll");
            await File.WriteAllBytesAsync(outside, binaryBytes);
            var denied = await Invoke("file.read", outside);
            Check(!denied.Success && denied.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                !denied.Content.Contains("requires UTF-8", StringComparison.Ordinal), "path scope is checked before decoding");
            Console.WriteLine($"Text read diagnostics: {count} checks passed using the actual dispatcher and synthetic local file fixtures; no SDK, model or native process calls.");
        }
        finally
        {
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("cerebral-text-read-diagnostics-", StringComparison.Ordinal))
                throw new InvalidOperationException("The fixture cleanup path escaped its owned temporary root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
