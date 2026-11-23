using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>
/// Optional stdio presentation of a captured owner connection. It neither starts a Worker nor
/// falls back to the legacy HTTP/profile path. Independent requests cannot block cancellation.
/// </summary>
public static class OperatorMcpHost
{
    private const int MaximumLineCharacters = 512 * 1024;
    private static readonly string[] Protocols = ["2025-11-25", "2025-06-18", "2024-11-05"];

    public static async Task<int> RunInstalledAsync(string[] args, CancellationToken token = default,
        IEnumerable<OperatorMcpOperation>? additionalOperations = null)
    {
        try
        {
            var launch = ParseLaunch(args);
            OperatorMcpDesktopTransport? desktops = launch.Bindings.Count == 0 ? null : new(launch.Bindings);
            using var registry = OperatorMcpRegistry.ForInstalledWorker(desktops is null ? null : desktops.CallAsync, additionalOperations);
            return await RunAsync(registry, Console.In, Console.Out, launch.MaximumConcurrent, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            // Startup failures may contain private enrollment paths. The owner SDK retains its
            // normal diagnostics; no credential/path fallback belongs in MCP startup output.
            await Console.Error.WriteLineAsync("Cerebral operator MCP could not bind its configured installed owner connection; no Worker was started or alternate transport selected.");
            return 2;
        }
    }

    public static async Task<int> RunAsync(OperatorMcpRegistry registry, TextReader input, TextWriter output,
        int maximumConcurrent = 4, CancellationToken token = default)
    {
        if (maximumConcurrent is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximumConcurrent));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var writes = new SemaphoreSlim(1, 1);
        using var slots = new SemaphoreSlim(maximumConcurrent, maximumConcurrent);
        using var controlSlots = new SemaphoreSlim(2, 2);
        var calls = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var pending = new ConcurrentDictionary<long, Task>();
        long taskId = 0;
        var initialized = false;
        var ready = false;
        async Task Send(object message)
        {
            var json = JsonSerializer.Serialize(message, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false });
            if (Encoding.UTF8.GetByteCount(json) > 2 * OperatorMcpRegistry.MaximumResultBytes + 65_536)
                throw new InvalidDataException("MCP response exceeds its bound.");
            await writes.WaitAsync(token).ConfigureAwait(false);
            try { await output.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); }
            finally { writes.Release(); }
        }
        Task Error(JsonElement? id, int code, string message) => Send(new { jsonrpc = "2.0", id, error = new { code, message } });
        Task Reply(JsonElement id, object result) => Send(new { jsonrpc = "2.0", id, result });

        async Task Invoke(JsonElement id, JsonElement parameters, string key, CancellationTokenSource cancellation, SemaphoreSlim reservation)
        {
            try
            {
                OperatorMcpRegistry.ValidateClosedObject(parameters, ["name", "arguments", "_meta"], ["name"]);
                // MCP metadata is untrusted transport context, never operation arguments or authority.
                if (parameters.TryGetProperty("_meta", out var metadata) && metadata.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("MCP tool-call metadata must be an object.");
                var name = parameters.GetProperty("name").GetString();
                var arguments = parameters.TryGetProperty("arguments", out var supplied) ? supplied : CerebralJson.Element(new { });
                object result;
                switch (name)
                {
                    case "cerebral_discover":
                        OperatorMcpRegistry.ValidateClosedObject(arguments, [], []);
                        result = ToolResult(registry.Discover()); break;
                    case "cerebral_describe":
                        OperatorMcpRegistry.ValidateClosedObject(arguments, ["operation"], ["operation"]);
                        result = ToolResult(registry.Describe(Text(arguments, "operation"))); break;
                    case "cerebral_call":
                        OperatorMcpRegistry.ValidateClosedObject(arguments, ["operation", "arguments"], ["operation", "arguments"]);
                        var call = await registry.CallAsync(Text(arguments, "operation"), arguments.GetProperty("arguments"), cancellation.Token).ConfigureAwait(false);
                        result = ToolResult(CerebralJson.Element(call), call.State is not ("completed" or "accepted")); break;
                    default: throw new ArgumentException("Unknown operator MCP tool.");
                }
                await Reply(id, result).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                await Error(id, -32602, "Invalid or unadmitted operator tool arguments. Discover and describe the exact operation first.").ConfigureAwait(false);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                await Error(id, -32602, "The operator tool arguments or owning response do not match the admitted contract.").ConfigureAwait(false);
            }
            finally
            {
                calls.TryRemove(key, out _); cancellation.Dispose(); reservation.Release();
            }
        }

        try
        {
            await foreach (var line in ReadLines(input, lifetime.Token).ConfigureAwait(false))
            {
                JsonDocument document;
                try { document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 40 }); }
                catch (JsonException) { await Error(null, -32700, "Invalid JSON.").ConfigureAwait(false); continue; }
                using (document)
                {
                    var root = document.RootElement;
                    JsonElement? id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var value) ? value.Clone() : null;
                    var notification = id is null && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("method", out var notificationMethod) &&
                        notificationMethod.ValueKind == JsonValueKind.String;
                    try
                    {
                        ValidateUniqueFields(root);
                        OperatorMcpRegistry.ValidateClosedObject(root, ["jsonrpc", "id", "method", "params"], ["jsonrpc", "method"]);
                        if (root.GetProperty("jsonrpc").GetString() != "2.0") throw new ArgumentException("Invalid JSON-RPC version.");
                        var method = Text(root, "method");
                        var parameters = root.TryGetProperty("params", out var supplied) ? supplied : CerebralJson.Element(new { });
                        if (id is null)
                        {
                            if (method == "notifications/initialized")
                            {
                                OperatorMcpRegistry.ValidateClosedObject(parameters, [], []);
                                if (initialized) ready = true;
                            }
                            else if (method == "notifications/cancelled")
                            {
                                OperatorMcpRegistry.ValidateClosedObject(parameters, ["requestId", "reason"], ["requestId"]);
                                var key = IdKey(parameters.GetProperty("requestId"));
                                if (calls.TryGetValue(key, out var cancellation)) cancellation.Cancel();
                            }
                            continue; // Notifications never produce responses.
                        }
                        var requestKey = IdKey(id.Value);
                        if (method == "initialize")
                        {
                            if (initialized) { await Error(id, -32600, "This connection was already initialized.").ConfigureAwait(false); continue; }
                            OperatorMcpRegistry.ValidateClosedObject(parameters, ["protocolVersion", "capabilities", "clientInfo"], ["protocolVersion", "capabilities", "clientInfo"]);
                            var requested = Text(parameters, "protocolVersion");
                            initialized = true;
                            await Reply(id.Value, new { protocolVersion = Protocols.Contains(requested) ? requested : Protocols[0],
                                capabilities = new { tools = new { } }, serverInfo = new { name = "Pegasus Cerebral Operator", version = "1.0.0" },
                                instructions = "Operator calls use Cerebral's captured authenticated owner connection. Discover/describe exact controls. Mutations require current selection/revision/request identities. Accepted work is not completed work. After uncertain acknowledgement inspect original durable work; never replay automatically. Large outputs are artifact references, not inline model bytes." }).ConfigureAwait(false);
                            continue;
                        }
                        if (method == "ping") { await Reply(id.Value, new { }).ConfigureAwait(false); continue; }
                        if (!ready) { await Error(id, -32002, "Initialize the MCP connection before using tools.").ConfigureAwait(false); continue; }
                        if (method == "tools/list")
                        {
                            OperatorMcpRegistry.ValidateClosedObject(parameters, ["cursor"], []);
                            if (parameters.TryGetProperty("cursor", out _)) throw new ArgumentException("No tool cursor is supported.");
                            await Reply(id.Value, new { tools = ToolDefinitions() }).ConfigureAwait(false); continue;
                        }
                        if (method != "tools/call") { await Error(id, -32601, "Unsupported MCP method.").ConfigureAwait(false); continue; }
                        var control = parameters.TryGetProperty("name", out var toolName) && toolName.ValueKind == JsonValueKind.String &&
                            (toolName.GetString() is "cerebral_discover" or "cerebral_describe" ||
                             parameters.TryGetProperty("arguments", out var toolArguments) && toolArguments.ValueKind == JsonValueKind.Object &&
                             toolArguments.TryGetProperty("operation", out var operationName) && operationName.ValueKind == JsonValueKind.String &&
                             registry.IsControlOperation(operationName.GetString()!));
                        var reservation = control ? controlSlots : slots;
                        if (!reservation.Wait(0)) { await Error(id, -32001, "Operator MCP is busy; no operation was admitted. Status/cancellation notifications remain readable.").ConfigureAwait(false); continue; }
                        var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        if (!calls.TryAdd(requestKey, linked))
                        {
                            linked.Dispose(); reservation.Release();
                            await Error(id, -32600, "This JSON-RPC ID is already in flight; no second call was admitted.").ConfigureAwait(false); continue;
                        }
                        var sequence = Interlocked.Increment(ref taskId);
                        var work = Invoke(id.Value, parameters.Clone(), requestKey, linked, reservation);
                        pending[sequence] = work;
                        _ = work.ContinueWith(completed =>
                        {
                            if (completed.IsFaulted) { _ = completed.Exception; lifetime.Cancel(); }
                            pending.TryRemove(sequence, out _);
                        }, CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                    {
                        if (!notification) await Error(id, -32600, "Invalid JSON-RPC request.").ConfigureAwait(false);
                    }
                }
            }
            // EOF ends this adapter's waits, not the already accepted service operations.
            // Retained service identities remain the reconciliation source after disconnect.
            lifetime.Cancel();
            await Task.WhenAll(pending.Values).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 2; }
        catch (TimeoutException) { return 2; }
        catch (InvalidDataException)
        {
            await Error(null, -32600, "MCP input exceeded its framing bound; the connection is closing.").ConfigureAwait(false);
            return 2;
        }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(pending.Values).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException) { }
        }
    }

    private static object ToolResult(JsonElement data, bool error = false) => new
    {
        content = new[] { new { type = "text", text = data.GetRawText().Length <= 32_768
            ? JsonSerializer.Serialize(data, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = false })
            : "Bounded Cerebral result is available in structuredContent. Use compact/paginated operations for large histories." } },
        structuredContent = data, isError = error
    };
    private static object[] ToolDefinitions() =>
    [
        new { name = "cerebral_discover", description = "Discover the closed owner/operator operation catalog and explicit coverage limitations.",
            inputSchema = new { type = "object", properties = new { }, additionalProperties = false } },
        new { name = "cerebral_describe", description = "Describe one exact admitted Cerebral operator operation before use.",
            inputSchema = new { type = "object", properties = new { operation = new { type = "string", maxLength = 128 } }, required = new[] { "operation" }, additionalProperties = false } },
        new { name = "cerebral_call", description = "Execute one admitted operation through Cerebral's captured owner or native desktop control path. No shell, caller identity override, or automatic replay.",
            inputSchema = new { type = "object", properties = new { operation = new { type = "string", maxLength = 128 }, arguments = new { type = "object" } }, required = new[] { "operation", "arguments" }, additionalProperties = false } }
    ];
    private static string Text(JsonElement input, string property) => input.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 128 } result && !result.Any(char.IsControl) ? result : throw new ArgumentException("A bounded identifier is required.");
    private static string IdKey(JsonElement value)
    {
        // Compare decoded values, not JSON spellings: "a" and "\u0061" name the
        // same in-flight request and must not admit duplicate mutations.
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 128 } text) return "s:" + text;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return "n:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        throw new ArgumentException("JSON-RPC IDs must be bounded strings or integer values.");
    }
    private static void ValidateUniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new ArgumentException("Duplicate object field.");
                ValidateUniqueFields(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUniqueFields(item);
    }
    private static async IAsyncEnumerable<string> ReadLines(TextReader input, [EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new char[4096]; var line = new StringBuilder();
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    if (line.Length != 0) yield return line.ToString();
                    line.Clear();
                }
                else
                {
                    line.Append(buffer[i]);
                    if (line.Length > MaximumLineCharacters) throw new InvalidDataException("MCP frame is too large.");
                }
            }
        }
        if (line.Length != 0) throw new InvalidDataException("MCP ended with an incomplete line frame.");
    }

    private sealed record Launch(int MaximumConcurrent, IReadOnlyList<OperatorMcpDesktopBinding> Bindings);
    private static Launch ParseLaunch(string[] args)
    {
        if (args.Length < 2 || args[0] != "mcp" || args[1] != "--operator")
            throw new ArgumentException("Operator mode requires the explicit trusted mcp --operator entry point.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allowed = ["--max-concurrent", "--main-exe", "--main-sha256", "--main-entry-sha256", "--main-descriptor",
            "--configuration-exe", "--configuration-sha256", "--configuration-entry-sha256", "--configuration-descriptor"];
        for (var index = 2; index < args.Length; index += 2)
            if (index + 1 >= args.Length || !allowed.Contains(args[index]) || !values.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Unsupported or repeated trusted operator launch option.");
        var maximum = values.TryGetValue("--max-concurrent", out var count) && int.TryParse(count, out var parsed) ? parsed : 4;
        if (maximum is < 1 or > 16 || values.ContainsKey("--max-concurrent") && !int.TryParse(count, out _))
            throw new ArgumentException("Invalid concurrency bound.");
        var bindings = new List<OperatorMcpDesktopBinding>();
        foreach (var target in new[] { "main", "configuration" })
        {
            var prefix = "--" + target;
            var keys = new[] { prefix + "-exe", prefix + "-sha256", prefix + "-entry-sha256", prefix + "-descriptor" };
            if (!keys.Any(values.ContainsKey)) continue;
            if (!keys.All(values.ContainsKey)) throw new ArgumentException("A desktop binding requires the complete pinned payload and descriptor.");
            bindings.Add(new(target, values[keys[0]], values[keys[1]], values[keys[2]], values[keys[3]]));
        }
        return new(maximum, bindings);
    }
}
