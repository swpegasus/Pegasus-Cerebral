using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Tools;

/// <summary>
/// Executes the fixed archive subset of the installed PAO CLI through the signed-in user's
/// resident host. Source-local Workers run as that user and do not need, or receive, the
/// installed service's private application credential.
/// </summary>
public sealed class CortexUserPaoArchiveClient(string? executablePath = null)
{
    private const int MaximumReceiptCharacters = 4 * 1024 * 1024;
    private readonly string? configuredExecutable = executablePath;

    public async Task<JsonElement> ExecuteAsync(JsonElement arguments, string? defaultWorkingDirectory,
        CancellationToken cancellationToken)
    {
        var request = Read(arguments, defaultWorkingDirectory);
        var executable = ResolveExecutable(configuredExecutable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = request.WorkingDirectory
        };
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("7z");
        start.ArgumentList.Add(request.Action);
        start.ArgumentList.Add(request.Source);
        if (request.Destination is not null) start.ArgumentList.Add(request.Destination);
        if (request.Clean) start.ArgumentList.Add("pol=clean");
        start.ArgumentList.Add("detail=true");
        start.ArgumentList.Add("cwd=" + request.WorkingDirectory);

        using var process = Process.Start(start) ?? throw new IOException("The installed Cortex PAO client did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (output.Length is 0 or > MaximumReceiptCharacters || error.Length > MaximumReceiptCharacters)
            throw new InvalidDataException("The Cortex PAO archive receipt is empty or exceeds its transport bound.");
        JsonElement receipt;
        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The Cortex PAO archive receipt is not an object.");
            receipt = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Cortex PAO archive receipt is not valid JSON.", exception);
        }
        if (process.ExitCode != 0 && receipt.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("The Cortex PAO process exit code conflicts with its success receipt.");
        return receipt;
    }

    private static Request Read(JsonElement arguments, string? defaultWorkingDirectory)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("op", out var operation) ||
            operation.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Source-local archive PAO requires one typed operation object.");
        var op = operation.GetString();
        var action = op switch
        {
            "7z_list" => "list",
            "7z_test" => "test",
            "7z_extract" => "extract",
            _ => throw new UnauthorizedAccessException("Source-local direct PAO is limited to fixed archive list, test and extract operations.")
        };
        var allowed = op == "7z_extract"
            ? new HashSet<string>(["op", "src", "dst", "pol", "detail", "output"], StringComparer.Ordinal)
            : new HashSet<string>(["op", "src", "detail", "output"], StringComparer.Ordinal);
        if (arguments.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            throw new ArgumentException("Source-local archive PAO contains an unsupported field.");
        var source = AbsoluteFile(arguments, "src");
        string? destination = null;
        var clean = false;
        if (op == "7z_extract")
        {
            destination = AbsoluteDirectoryPath(arguments, "dst");
            if (!arguments.TryGetProperty("pol", out var policy) || policy.ValueKind != JsonValueKind.String || policy.GetString() != "clean")
                throw new ArgumentException("Source-local archive extraction requires the fixed clean collision policy.");
            clean = true;
        }
        if (arguments.TryGetProperty("detail", out var detail) && detail.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("Source-local archive PAO detail must be boolean.");
        var working = string.IsNullOrWhiteSpace(defaultWorkingDirectory)
            ? Path.GetDirectoryName(source)! : Path.GetFullPath(defaultWorkingDirectory);
        if (!Path.IsPathFullyQualified(working) || !Directory.Exists(working))
            throw new ArgumentException("Source-local archive PAO requires an existing absolute working directory.");
        return new(action, source, destination, clean, working);
    }

    internal static JsonElement NativeArguments(JsonElement arguments, string? defaultWorkingDirectory)
    {
        var request = Read(arguments, defaultWorkingDirectory);
        var values = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        values["cwd"] = request.WorkingDirectory; values["detail"] = true;
        values["output"] = "structured";
        return CerebralJson.Element(values);
    }

    private static string AbsoluteFile(JsonElement arguments, string name)
    {
        var path = Text(arguments, name);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new ArgumentException($"Source-local archive PAO requires an existing absolute {name} file.");
        return Path.GetFullPath(path);
    }

    private static string AbsoluteDirectoryPath(JsonElement arguments, string name)
    {
        var path = Text(arguments, name);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException($"Source-local archive PAO requires an absolute {name} directory.");
        return Path.GetFullPath(path);
    }

    private static string Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) && !value.GetString()!.Contains('\0')
            ? value.GetString()!
            : throw new ArgumentException($"Source-local archive PAO requires nonempty {name} text.");

    private static string ResolveExecutable(string? configured)
    {
        var path = configured ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Pegasus", "Cortex", "bin", "pao.exe");
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !Path.GetFileName(path).Equals("pao.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Pegasus Cortex PAO is unavailable. Install or repair Pegasus Cortex before using source-local archive handling.", path);
        var exact = Path.GetFullPath(path);
        if ((File.GetAttributes(exact) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The installed Cortex PAO executable cannot be a reparse point.");
        return exact;
    }

    private sealed record Request(string Action, string Source, string? Destination, bool Clean, string WorkingDirectory);
}
