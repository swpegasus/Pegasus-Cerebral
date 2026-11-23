using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Client;

/// <summary>Trusted launch configuration, never a model-authored tool argument.</summary>
public sealed record OperatorMcpDesktopBinding(string Target, string Executable, string ExecutableSha256,
    string ManagedEntrySha256, string DescriptorPath);

/// <summary>
/// Reuses the installed products' semantic CLI clients without a Windows-desktop dependency in
/// the portable Worker. Those clients retain native server identity and selection validation.
/// </summary>
public sealed class OperatorMcpDesktopTransport
{
    private readonly IReadOnlyDictionary<string, OperatorMcpDesktopBinding> bindings;
    public OperatorMcpDesktopTransport(IEnumerable<OperatorMcpDesktopBinding> bindings)
    {
        var items = bindings.ToArray();
        if (items.Any(x => x.Target is not ("main" or "configuration")) || items.Select(x => x.Target).Distinct().Count() != items.Length)
            throw new ArgumentException("Each desktop target requires one fixed binding.");
        foreach (var item in items)
        {
            AbsoluteOrdinary(item.Executable); AbsoluteOrdinary(item.DescriptorPath);
            if (!ValidHash(item.ExecutableSha256) || !ValidHash(item.ManagedEntrySha256))
                throw new ArgumentException("Desktop payload pins must be lowercase SHA-256 values.");
        }
        this.bindings = items.ToDictionary(x => x.Target, StringComparer.Ordinal);
    }

    public async Task<OperatorMcpCallResult> CallAsync(string target, string operation, string requestId,
        JsonElement arguments, CancellationToken token)
    {
        if (!bindings.TryGetValue(target, out var binding))
            return new(target + "." + operation, "unavailable", CerebralJson.Element(new { }), "The target's trusted desktop binding is absent.");
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 || requestId.Any(char.IsControl) ||
            operation.Length > 128 || arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 65_536)
            throw new ArgumentException("Invalid bounded desktop-control request.");
        AbsoluteOrdinary(binding.DescriptorPath);
        // Keep both executable/managed-entry files closed to writes and replacement until the
        // helper exits. The existing product client independently verifies the connected server.
        using var executablePin = Pin(binding.Executable, binding.ExecutableSha256);
        using var managedPin = Pin(Path.ChangeExtension(binding.Executable, ".dll"), binding.ManagedEntrySha256);
        var start = new ProcessStartInfo(binding.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(binding.Executable)!
        };
        foreach (var argument in new[] { target == "main" ? "--desktop-control-call" : "--configuration-control-call",
                     binding.DescriptorPath, operation, requestId, arguments.GetRawText() }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("The semantic client did not start.");
        process.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        void StopHelper()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, OperatorMcpRegistry.MaximumResultBytes, StopHelper, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 65_536, StopHelper, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode is not (0 or 2 or 3)) throw new IOException("The semantic client did not confirm a product response.");
            using var document = JsonDocument.Parse(await stdout.ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            OperatorMcpRegistry.ValidateClosedObject(root, ["ok", "id", "op", "state", "data", "error"], ["ok", "id", "op", "state", "data", "error"]);
            var state = root.GetProperty("state").GetString();
            if (root.GetProperty("id").GetString() != requestId || root.GetProperty("op").GetString() != operation ||
                state is not ("completed" or "accepted" or "rejected" or "uncertain" or "failed" or "conflict") ||
                root.GetProperty("ok").GetBoolean() != (state is "completed" or "accepted") ||
                (process.ExitCode == 0) != (state is "completed" or "accepted") || (process.ExitCode == 3) != (state == "uncertain"))
                throw new InvalidDataException("The semantic response is unrelated or inconsistent.");
            return new(target + "." + operation, state!, OperatorMcpRegistry.Sanitize(root.GetProperty("data")),
                root.GetProperty("error").ValueKind == JsonValueKind.Null ? null : OperatorMcpRegistry.RedactDiagnostic(root.GetProperty("error").GetString() ?? ""), state == "uncertain");
        }
        finally
        {
            StopHelper();
            // Only the short-lived semantic CLI belongs to us. Its server and actual operation
            // can outlive a lost reply, so killing this helper must never be called cancellation.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }

    private static FileStream Pin(string path, string expected)
    {
        AbsoluteOrdinary(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(expected)))
                throw new InvalidDataException("The trusted desktop payload changed. Refresh its launch binding.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    private static bool ValidHash(string value) => value.Length == 64 && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void AbsoluteOrdinary(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Any(char.IsControl) ||
            OperatingSystem.IsWindows() && path.AsSpan(2).Contains(':'))
            throw new ArgumentException("Desktop bindings require absolute local paths.");
        for (var cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Linked desktop bindings are unsupported.");
    }
    private static async Task<string> ReadBoundedAsync(Stream input, int maximum, Action overflow, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximum) { overflow(); throw new IOException("Semantic client output exceeded its bound."); }
            output.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
}
