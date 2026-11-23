using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cerebral.Core;

namespace Cerebral.Tools;

public sealed partial class ToolDispatcher(Func<HostSettings> settings, IEnumerable<IToolExtension>? extensions = null, string? stateDirectory = null) : IToolDispatcher, IJobReceiptLookup, IAsyncDisposable
{
    private const string ExistingDestinationWriteDiagnostic = "Destination already exists; no write occurred. Call file.read for this exact destination now, then retry once with collision=replace and the exact expectedSha256 returned by that read. Never repeat collision=fail.";
    private const string MissingReplacementDestinationDiagnostic = "Replacement requires an existing destination; no write occurred. Create the absent file with collision=fail.";
    private readonly Func<HostSettings> _settings = settings;
    private readonly IReadOnlyList<IToolExtension> _extensions = extensions?.ToArray() ?? [];
    private readonly CancellationTokenSource _lifetime = new();
    // Existing authenticated loopback connector transport; managed web tools use the host broker.
    private readonly HttpClient _connectorHttp = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim FileMutationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _httpGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _httpLast = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _diagnostics = new(StringComparer.Ordinal);
    private readonly CortexApplicationAuthorization _applicationAuthorization = new(stateDirectory);
    public IReadOnlyDictionary<string, string> Diagnostics => new Dictionary<string, string>(_diagnostics);
    public bool FullAccessActive => _applicationAuthorization.Configured;
    public string? FullAccessUnavailableReason => _applicationAuthorization.UnavailableReason;
    public FullAccessStatus FullAccessStatus()
    {
        var status = _applicationAuthorization.Snapshot();
        return new(status.Active, status.Remembered, status.Active ? "cortex.application.pipe" : null, status.EnabledAt, status.Message);
    }

    /// <summary>
    /// Executes one fixed PAO operation for trusted service-owned infrastructure. This seam is deliberately absent
    /// from model tool discovery; callers must construct a typed operation and retain their own authorization scope.
    /// </summary>
    public Task<JsonElement> ExecuteHostPaoAsync(JsonElement arguments, string? workingDirectory,
        CancellationToken cancellationToken) =>
        _applicationAuthorization.ExecuteUnifiedAsync(arguments, workingDirectory, cancellationToken);

    /// <summary>Private owner workflow route. The Worker supplies its already pinned staged
    /// closure; this is not a semantic tool, model grant or arbitrary executable selector.</summary>
    public async Task<(int ExitCode, string Output, string Error, bool Truncated)> ExecuteOwnerVerificationHostAsync(
        string executableId, string executablePath, string stagedCheckerAssembly,
        string checkerRoot, string targetRoot, string resultsRoot, string descriptorPath,
        CancellationToken cancellationToken)
    {
        var registration = _settings().Executables.SingleOrDefault(item => item.Id == executableId);
        if (registration is null || registration.Path != executablePath ||
            registration.PrefixArguments.Count != 0 ||
            !string.Equals(Path.GetFileName(executablePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The exact registered .NET checker host changed.");
        PortableRegistrationPolicy.RequireReady(registration);
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        if (!ScopedFilePolicy.IsWithin(executablePath, installed) ||
            !Path.IsPathFullyQualified(stagedCheckerAssembly) ||
            !ScopedFilePolicy.IsWithin(stagedCheckerAssembly, checkerRoot) ||
            !Path.IsPathFullyQualified(descriptorPath) ||
            !descriptorPath.Equals(Path.Combine(Path.GetDirectoryName(checkerRoot)!, "verification.json"),
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(targetRoot) != Path.GetDirectoryName(checkerRoot) ||
            Path.GetDirectoryName(resultsRoot) != Path.GetDirectoryName(checkerRoot) ||
            checkerRoot.Equals(targetRoot, StringComparison.OrdinalIgnoreCase) ||
            checkerRoot.Equals(resultsRoot, StringComparison.OrdinalIgnoreCase) ||
            targetRoot.Equals(resultsRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(stagedCheckerAssembly) || !File.Exists(descriptorPath))
            throw new UnauthorizedAccessException("The private checker stage is incomplete or outside its exact sibling roots.");
        return await ManagedProcess.RunOwnerVerificationAsync(executablePath,
            [stagedCheckerAssembly, "--checker-root", checkerRoot, "--target-root", targetRoot,
                "--results-root", resultsRoot, "--descriptor", descriptorPath],
            checkerRoot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FullAccessStatus> EnableFullAccessAsync(CancellationToken ct)
    {
        var status = await _applicationAuthorization.EnableAsync(ct);
        _diagnostics.TryRemove("full-access", out _);
        return new(status.Active, status.Remembered, "cortex.application.pipe", status.EnabledAt, status.Message);
    }

    public async Task<FullAccessStatus> RotateFullAccessAsync(CancellationToken ct)
    {
        var status = await _applicationAuthorization.RotateAsync(ct);
        _diagnostics.TryRemove("full-access", out _);
        return new(status.Active, status.Remembered, "cortex.application.pipe", status.EnabledAt, status.Message);
    }

    public async Task<FullAccessStatus> DisableFullAccessAsync(CancellationToken ct)
    {
        var status = await _applicationAuthorization.DisableAsync(ct);
        _diagnostics.TryRemove("full-access", out _);
        return new(status.Active, status.Remembered, null, status.EnabledAt, status.Message);
    }

    private static readonly ToolDescriptor[] Builtins =
    [
        Define("math.calculate", "Calculate exact rational arithmetic: integers, decimals, + - * /, parentheses, choose(n,k), factorial(n). Example: choose(5,2)*choose(7,1)/choose(12,3). Function arguments are integers 0..1000; expressions are bounded to 4096 characters. Returns a reduced exact result and a decimal approximation; no files or scripts.", "math.calculate", ["expression"], ("expression", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4096 }), ("decimalPlaces", Integer(0, 50, 20))),
        Define("file.read", "Read bounded UTF-8 or BOM-marked UTF-16 text from an allowed path and its current whole-file hash (sha256). Compiled binaries need their source file or a permitted format-aware tool. Read before replacement/editing; pass the hash as expectedSha256. Output can be partial.", "filesystem.read", ["path"],
            ("path", Text("Target file path.")), ("startLine", Integer(1, 1_000_000, 1)), ("lineCount", Integer(1, 1000, 200))),
        Define("file.inspect", "Inspect a bounded text file without returning its contents. Returns its current hash, byte count, line count, and word count. Use this before and between bounded long-document append actions.", "filesystem.read", ["path"],
            ("path", Text("Existing text file path."))),
        Define("file.list", "List a bounded directory without following links. Recursive traversal is opt-in.", "filesystem.read", ["path"],
            ("path", Text("Target directory path.")), ("recursive", Boolean(false)), ("limit", Integer(1, 500, 100))),
        Define("file.search", "Find literal text in bounded UTF text files without following links. Recursive and case-insensitive search are opt-in.", "filesystem.read", ["path", "text"],
            ("path", Text("Target file or directory path.")), ("text", Text("Nonempty literal text to find.", 1)), ("recursive", Boolean(false)), ("ignoreCase", Boolean(false)), ("limit", Integer(1, 500, 100))),
        Define("file.copy", "Copy one existing bounded file byte-for-byte to a new destination beneath the saved file roots. The destination directory must exist and the destination must be absent. Use this for SDK templates or exact artifacts instead of regenerating known bytes.", "filesystem.write", ["sourcePath", "destinationPath"],
            ("sourcePath", Text("Existing source file path inside a readable root.")), ("destinationPath", Text("New destination file path inside a writable root."))),
        Define("file.write", "Create text with collision=fail (default). To replace an existing file: file.read it, then set collision=replace and expectedSha256 to the returned hash. Re-read after a hash mismatch. The parent directory must exist; use file.mkdir first.", "filesystem.write", ["path", "content"],
            ("path", Text("Destination file path.")), ("content", Text("Complete new file text, not a patch.")), ("collision", new() { ["type"] = "string", ["enum"] = new JsonArray("fail", "skip", "replace"), ["default"] = "fail", ["description"] = "Existing destination: fail rejects, skip leaves it unchanged, replace requires its current expectedSha256." }), ("expectedSha256", Hash())),
        Define("file.append", "Append one bounded section to an existing text artifact. First use file.inspect and pass its returned sha256 and bytes as expectedSha256 and expectedBytes; each successful append returns the next hash, byte length, and total word count. An exact replay after a committed append is recognized without duplicating text. Include any intended leading blank lines in content.", "filesystem.write", ["path", "content", "expectedSha256", "expectedBytes"],
            ("path", Text("Existing destination file path.")), ("content", Text("One continuation section to append.", 1)), ("expectedSha256", Hash()), ("expectedBytes", Integer(0, ToolPolicy.MaxInput, 0))),
        Define("file.edit", "First file.read the existing file, then copy its hash to expectedSha256. Replace exactly one nonempty oldText match with different newText. Unchanged replacements are rejected. On a hash mismatch or nonunique match, read again and correct the edit.", "filesystem.write", ["path", "oldText", "newText", "expectedSha256"],
            ("path", Text("Existing file path.")), ("oldText", Text("Exact, nonempty text occurring once.", 1)), ("newText", Text("Replacement text; empty deletes the match.")), ("expectedSha256", Hash())),
        Define(ToolPolicy.RangeEditToolName, "Replace complete physical lines in an existing task-owned UTF-8/no-BOM/LF-only file using its current whole-file hash. Requires a literal file.edit-range grant; wildcard alone is insufficient. Lines include their terminators: an empty file has zero editable lines and a read's trailing empty segment is not an editable line. Nonempty newText gains a final LF when a suffix remains or the selected last line was terminated; empty deletes the range. No insertion, whitespace matching or unchanged edit. Uses optimistic preimage validation and atomic publication; requires exclusive coordinated ownership, not concurrent writers.", "filesystem.write", ["path", "expectedSha256", "startLine", "lineCount", "newText"],
            ("path", Text("Existing writable file path.")), ("expectedSha256", Hash()), ("startLine", Integer(1, 1_000_000, 1)), ("lineCount", Integer(1, 512, 1)),
            ("newText", new JsonObject { ["type"] = "string", ["maxLength"] = 65536, ["description"] = "LF-only replacement, at most 64 KiB strict UTF-8; empty deletes the selected complete lines." })),
        Define("file.mkdir", "Ensure a directory exists beneath an allowed root, including missing parent directories. This operation is idempotent; it does not prove the directory was newly created.", "filesystem.write", ["path"], ("path", Text("Directory path."))),
        Define("process.run", "Run a registered executable ID with explicit separate argv in an existing allowed working directory. The executable ID selects only the program; the working directory never implies a command. .NET example: arguments=[\"run\",\"--project\",\"App.csproj\"]. PowerShell: first create a .ps1 file with file.write, then arguments=[\"-NoProfile\",\"-NonInteractive\",\"-File\",\"script.ps1\",...]. The script must be an existing file inside an allowed root. -Command, -EncodedCommand, command strings and shell pipelines are prohibited. Script arguments follow its path. Processes run with the service identity; this is not an OS sandbox.", "process.execute", ["executableId", "arguments", "workingDirectory"],
            ("executableId", Text("Exact host-registered executable ID, not a path or command.")), ("arguments", Arguments("Required separate command arguments; use [] only when the program itself needs no arguments. Including registered prefixes: at most 256 arguments and 32768 characters total. PowerShell accepts only supported startup options before -File; the script path and its arguments follow.")), ("workingDirectory", Text("Existing directory inside an allowed root.")), ("timeoutSeconds", Integer(1, 600, 30)),
            ("expectedExitCode", new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 255, ["default"] = 0, ["description"] = "Expected completed child exit code; defaults to zero. Changes semantic success only, never the actual exit or output. Nonzero expectations are supported only by direct native process execution, not mapped dotnet build/test operations." })),
        Define("git.status", "Read bounded Git status in an allowed repository.", "git.read", ["path"],
            ("path", Text("Existing repository directory.")), ("timeoutSeconds", Integer(1, 600, 30))),
        Define("git.diff", "Read bounded Git diff without external diff tools or text converters. staged=false compares unstaged changes.", "git.read", ["path"],
            ("path", Text("Existing repository directory.")), ("staged", Boolean(false)), ("timeoutSeconds", Integer(1, 600, 30))),
        Define("git.run", "Run separate Git arguments with git.write authority; this can mutate repository state. Repository directory overrides are prohibited.", "git.write", ["path", "arguments"],
            ("path", Text("Existing repository directory.")), ("arguments", Arguments("Separate Git arguments. Including registered prefixes: at most 256 arguments and 32768 characters total. Do not use -C, --git-dir or --work-tree.")), ("timeoutSeconds", Integer(1, 600, 30))),
        Define("http.fetch", "Fetch bounded HTTP(S) GET content without embedded URL credentials. Requests are paced by destination host; redirects are returned without following.", "network.http", ["url"],
            ("url", Text("Absolute HTTP(S) URL without embedded credentials.")), ("timeoutSeconds", Integer(1, 600, 30)), ("minimumIntervalMs", Integer(250, 60_000, 1000)))
    ];

    private static readonly Lazy<FrozenSet<string>> BuiltinNames = new(() =>
        Builtins.Concat(SharedBuiltins).Select(x => x.Name).ToFrozenSet(StringComparer.Ordinal));

    /// <summary>Derived from the actual descriptors; admission must reject collisions before exposing a package.</summary>
    public static IReadOnlySet<string> BuiltinToolNames => BuiltinNames.Value;

    public static bool IsHostToolName(string name) => BuiltinToolNames.Contains(name) ||
        name.StartsWith("mcp.", StringComparison.Ordinal) || name.StartsWith("shared.", StringComparison.Ordinal);

    private static JsonObject Text(string description, int? minimum = null)
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = description };
        if (minimum is { } value) schema["minLength"] = value;
        return schema;
    }
    private static JsonObject Integer(int minimum, int maximum, int value) => new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum, ["default"] = value };
    private static JsonObject Boolean(bool value) => new() { ["type"] = "boolean", ["default"] = value };
    private static JsonObject Hash() => new() { ["type"] = "string", ["pattern"] = "^[a-fA-F0-9]{64}$", ["description"] = "Copy the current whole-file SHA-256 from the preceding read; never invent a hash. Read again after a mismatch." };
    private static JsonObject Arguments(string description) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = 256, ["description"] = description };

    private static ToolDescriptor Define(string name, string description, string capability, string[] required, params (string Name, JsonObject Schema)[] fields)
    {
        var properties = new JsonObject();
        foreach (var (field, schema) in fields) properties[field] = schema;
        return new(name, description, CerebralJson.Element(new { type = "object", properties, required, additionalProperties = false }), capability);
    }

    public async Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var extensionTools = _extensions.SelectMany(extension =>
            extension.Tools.Select(tool => (Owner: extension, Tool: tool))).ToList();
        if (extensionTools.Select(x => x.Tool.Name).Intersect(Builtins.Concat(SharedBuiltins).Select(x => x.Name), StringComparer.Ordinal).Any() ||
            extensionTools.GroupBy(x => x.Tool.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Tool extensions contain a duplicate tool name.");
        // Validate all extension names before hiding this SDK publisher's author-only contracts.
        var availableExtensions = extensionTools.Where(item => item.Owner is not PluginScaffoldToolExtension ||
            PluginScaffoldToolExtension.CanAuthorPlugin(profile)).Select(item => item.Tool);
        var result = Builtins.Concat(SharedBuiltins).Concat(availableExtensions).Where(x => ToolPolicy.AllowsTool(profile, x.Name) && profile.Capabilities.Contains(x.Capability, StringComparer.Ordinal)).ToList();
        foreach (var server in _settings().McpServers.Where(x => x.Enabled && profile.Capabilities.Contains($"mcp.{x.Id}", StringComparer.Ordinal) && (profile.AllowedTools.Contains("*", StringComparer.Ordinal) || profile.AllowedTools.Any(t => t.StartsWith($"mcp.{x.Id}.", StringComparison.Ordinal)))))
        {
            try
            {
                // PAO is an execution backend for Cerebral's semantic tools. Advertising its raw multiplexed
                // operation schema makes small models invent provider arguments and bypasses the stable file,
                // process, and Git contracts above. Keep direct PAO dispatch available to trusted structured
                // clients, but never put the gateway itself in an agent action catalog.
                result.AddRange((await DiscoverAsync(server, linked.Token))
                    .Where(t => !IsPaoGateway(server, t.Name) && ToolPolicy.AllowsTool(profile, t.Name))
                    .Select(t => CortexDescriptor(t) with { Description = t.Description[..Math.Min(t.Description.Length, 1000)] }));
                _diagnostics.TryRemove(server.Id, out _);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _diagnostics[server.Id] = "MCP unavailable: " + ex.Message; }
        }
        return result;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var attempt = new ExecutionAttempt();
        try
        {
            ValidateEffectId(context.EffectId);
            cancellationToken.ThrowIfCancellationRequested();
            if (HasReservedBuiltinCollision(call.Name))
                throw new InvalidOperationException("A tool extension conflicts with a host tool name. Nothing was dispatched.");
            RequireRegistrationReady(call);
            if (context.EnforceProfileScope) context = context with { FullAccess = context.FullAccess && _applicationAuthorization.Configured };
            else if (_applicationAuthorization.Configured) context = context with { FullAccess = true };
            if (call.Arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(call.Arguments.GetRawText()) > ToolPolicy.MaxInput) throw new ArgumentException("Tool arguments must be a bounded JSON object.");
            // A remembered Full Access credential is an available route, not a prerequisite for
            // every semantic tool. Project-scoped reads, writes, builds and extensions must keep
            // working when the protected Cortex session is stopped or needs reconnection. The
            // application pipe is opened lazily below only when an admitted operation actually
            // escapes the profile's saved roots.
            if (call.Name.StartsWith("mcp.", StringComparison.Ordinal)) return await ExecuteMcpAsync(call, context, attempt, linked.Token);
            if (call.Name.StartsWith("shared.", StringComparison.Ordinal))
            {
                attempt.Dispatch("cerebral.shared");
                return await ExecuteSharedAsync(call, context, linked.Token);
            }
            var extension = _extensions.SingleOrDefault(item => item.Tools.Any(x => x.Name == call.Name));
            if (extension is not null && IsReservedBuiltinName(call.Name))
                throw new InvalidOperationException("A tool extension conflicts with a host tool name. Nothing was dispatched.");
            var descriptor = Builtins.SingleOrDefault(x => x.Name == call.Name) ?? extension?.Tools.Single(x => x.Name == call.Name) ?? throw new ArgumentException("Unknown tool.");
            ToolPolicy.Grant(context.Profile, descriptor.Name, descriptor.Capability);
            if (InternetAuthorityContract.ManagedToolIds.Contains(call.Name, StringComparer.Ordinal))
            {
                if (context.Internet is not { Version: InternetAuthorityContract.Version } internet ||
                    internet.Authority.ToolName != call.Name || internet.Authority.ConversationId != context.SessionId ||
                    internet.Authority.RunId != context.JobId || internet.Authority.EffectId != context.EffectId)
                    throw new UnauthorizedAccessException("The exact host-managed Internet scope is missing or mismatched.");
                if (extension is not null && extension is not IManagedInternetToolProvider { ManagedInternetVersion: InternetAuthorityContract.Version })
                    throw new UnauthorizedAccessException("This browser provider does not support the managed web contract. No raw-network fallback is permitted.");
            }

            if (call.Name == "file.copy") ToolPolicy.Grant(context.Profile, "file.read", "filesystem.read");
            ValidateSchema(call.Arguments, descriptor.InputSchema);
            if (call.Name == "process.run") _ = ExpectedProcessExitCode(call.Arguments);
            var defaultTimeout = extension is IToolExecutionTimeoutPolicy timeoutPolicy
                ? timeoutPolicy.GetDefaultTimeoutSeconds(call.Name) : 30;
            using var timeout = ToolPolicy.Timeout(call.Arguments, linked.Token, defaultTimeout);
            var ct = timeout.Token;
            var args = call.Arguments;
            if (extension is not null)
            {
                attempt.Dispatch("cerebral.plugin");
                var extended = await extension.ExecuteAsync(call, context, ct);
                var bounded = ToolPolicy.Limit(extended.Content, extended.Success && extended.EffectiveOutcome == ToolExecutionOutcome.Succeeded);
                return bounded with
                {
                    Truncated = extended.Truncated || bounded.Truncated,
                    Generation = extended.Generation,
                    CompleteAssignment = extended.CompleteAssignment,
                    Evidence = extended.Evidence,
                    RequiredSuccessfulTools = extended.RequiredSuccessfulTools
                };
            }
            var pao = await TryPaoAsync(call, context, attempt, ct);
            if (pao is not null) return pao;
            RequireNoReservedBuiltinCollision(call.Name);
            // Full Access may widen only an operation that has a fixed PAO mapping and therefore
            // executes through the protected application route. Native fallbacks stay inside the
            // saved profile roots; an unmapped out-of-root operation fails closed.
            var localContext = context with { FullAccess = false };
            switch (call.Name)
            {
                case "math.calculate": return ToolPolicy.Result(MathCalculator.Calculate(ToolPolicy.Text(args, "expression"), ToolPolicy.Number(args, "decimalPlaces", 20, 0, 50), ct));
                case "file.read": return await ReadAsync(args, localContext, ct);
                case "file.inspect": return await InspectAsync(args, localContext, ct);
                case "file.list": return await ListFilesAsync(args, localContext, false, ct);
                case "file.search": return await ListFilesAsync(args, localContext, true, ct);
                case "file.copy": return await CopyAsync(args, localContext, attempt, ct);
                case "file.write": return await WriteAsync(args, localContext, false, attempt, ct);
                case "file.append": return await AppendAsync(args, localContext, attempt, ct);
                case "file.edit": return await WriteAsync(args, localContext, true, attempt, ct);
                case ToolPolicy.RangeEditToolName:
                    throw new ArgumentException("The fixed PAO range-edit provider is unavailable or unsupported. No range edit was submitted; no native writer is used.");
                case "file.mkdir":
                    var directory = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), localContext);
                    if (Directory.Exists(directory))
                    {
                        ToolPolicy.RejectLinks(directory);
                        return ToolPolicy.Result(new { path = directory, ensured = true, existedBefore = true });
                    }
                    attempt.Dispatch("cerebral.filesystem");
                    Directory.CreateDirectory(directory); ToolPolicy.RejectLinks(directory);
                    return ToolPolicy.Result(new { path = directory, ensured = true, existedBefore = false });
                case "process.run": return await RunAsync(args, localContext, null, attempt, ct);
                case "git.status": return await RunAsync(args, localContext, ["--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=normal"], attempt, ct, readOnly: true);
                case "git.diff": return await RunAsync(args, localContext, ["--no-optional-locks", "diff", "--no-ext-diff", "--no-textconv", .. ToolPolicy.Boolean(args, "staged") ? new[] { "--cached" } : Array.Empty<string>()], attempt, ct, readOnly: true);
                case "git.run": return await RunAsync(args, localContext, ToolPolicy.Strings(args, "arguments"), attempt, ct);
                case "http.fetch": return await FetchAsync(args, context, attempt, ct);
                default: throw new ArgumentException("Unknown tool.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            return new(false, attempt.Dispatched ? "Tool timed out after dispatch; the outcome is unconfirmed. Inspect the existing operation before retrying."
                : "Tool timed out before dispatch; no operation was submitted.")
            { Evidence = attempt.Evidence(attempt.Dispatched ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Cancelled) };
        }
        catch (OperationCanceledException) when (context.EffectId is not null)
        {
            // Durable workflows need the known receipt identity even when their caller cancels.
            // Interrupting the client transport does not prove a broker-owned job was cancelled.
            return new(false, attempt.Dispatched ? "Tool communication was cancelled after dispatch; the outcome remains unconfirmed. Inspect the existing receipt before retrying."
                : "Tool execution was cancelled before dispatch; no operation was submitted.")
            { Evidence = attempt.Evidence(attempt.Dispatched ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Cancelled) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var evidence = attempt.Evidence(attempt.Dispatched ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Rejected);
            if (call.Name == ToolPolicy.RangeEditToolName && !attempt.Dispatched) evidence = evidence with { Partial = false };
            return ToolPolicy.Limit(ex.Message, false) with
            { Evidence = evidence };
        }
    }

    private void RequireRegistrationReady(ToolCall call)
    {
        var settings = _settings();
        if (call.Name.StartsWith("mcp.", StringComparison.Ordinal))
        {
            var pieces = call.Name.Split('.', 3);
            if (pieces.Length == 3 && settings.McpServers.SingleOrDefault(x => x.Id == pieces[1]) is { } server)
                PortableRegistrationPolicy.RequireReady(server);
        }
        if (call.Name.StartsWith("shared.", StringComparison.Ordinal) && settings.SharedFilesWorkerId != "local" &&
            settings.Workers.SingleOrDefault(x => x.Id == settings.SharedFilesWorkerId) is { } worker)
            PortableRegistrationPolicy.RequireReady(worker);
        var executableId = call.Name == "process.run" ? ToolPolicy.Text(call.Arguments, "executableId")
            : call.Name.StartsWith("git.", StringComparison.Ordinal) ? "git" : null;
        if (executableId is not null && settings.Executables.SingleOrDefault(x => x.Id == executableId) is { } executable)
            PortableRegistrationPolicy.RequireReady(executable);
    }

    private static bool IsReservedBuiltinName(string name) =>
        Builtins.Any(item => item.Name == name) || SharedBuiltins.Any(item => item.Name == name);

    private bool HasReservedBuiltinCollision(string name) =>
        IsReservedBuiltinName(name) && _extensions.Any(extension => extension.Tools.Any(item => item.Name == name));

    private void RequireNoReservedBuiltinCollision(string name)
    {
        if (HasReservedBuiltinCollision(name))
            throw new InvalidOperationException("A tool extension conflicts with a host tool name. Nothing was dispatched.");
    }

    private sealed class ExecutionAttempt
    {
        public bool Dispatched { get; private set; }
        public string ProviderId { get; private set; } = "cerebral";
        public string? RequestId { get; private set; }
        private ToolExecutionEvidence? _acknowledged;
        public void Dispatch(string providerId, string? requestId = null) { Dispatched = true; ProviderId = providerId; RequestId = requestId; }
        public void Acknowledge(ToolExecutionEvidence evidence) => _acknowledged = evidence;
        public ToolExecutionEvidence Evidence(ToolExecutionOutcome outcome) =>
            _acknowledged is { } retained ? retained with { Outcome = outcome } : new(outcome, ProviderId, RequestId);
        public void ConfirmNotSubmitted() { Dispatched = false; ProviderId = "cerebral"; RequestId = null; _acknowledged = null; }
    }

    private static void ValidateEffectId(string? effectId)
    {
        if (effectId is not null && !IsReceiptIdentifier(effectId, 128))
            throw new ArgumentException("Host effect identity must contain 1..128 ASCII letters, digits, dots, colons, underscores or hyphens.");
    }

    private static bool IsReceiptIdentifier(string? value, int maximum = 256) => value is { Length: > 0 } && value.Length <= maximum &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');

    private static JsonElement BindPaoEffect(JsonElement arguments, ToolExecutionContext context)
    {
        if (context.EffectId is null) return arguments;
        if (arguments.EnumerateObject().Any(x => x.Name.Equals("rid", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("PAO request identity is owned by the Cerebral workflow host.");
        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        node["rid"] = context.EffectId;
        return CerebralJson.Element(node);
    }

    private static ToolResult ConfirmEffectReceipt(ToolResult result, string? effectId)
    {
        if (effectId is null || result.Evidence?.RequestId == effectId) return result;
        // Preserve what the provider actually returned; never replace a mismatched receipt identity
        // with the requested identity and thereby turn it into evidence for a different effect.
        return ToolPolicy.Result(new { error = "pao_effect_identity_unconfirmed", message = "The provider receipt did not identify the requested workflow effect. Inspect the original execution before retrying." }) with
        { Success = false, Evidence = (result.Evidence ?? new(ToolExecutionOutcome.Unknown, "cerebral")) with { Outcome = ToolExecutionOutcome.Unknown } };
    }

    private static bool RequiresApplicationAuthorization(JsonElement arguments, AgentProfile profile)
    {
        var roots = profile.AllowedRoots.Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).ToArray();
        foreach (var name in new[] { "src", "dst", "cwd" })
        {
            if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString())) continue;
            var path = Path.GetFullPath(value.GetString()!);
            if (!roots.Any(root => ToolPolicy.IsWithin(path, root))) return true;
        }
        return false;
    }

    private static bool ApplicationAuthorizationFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or
            System.ComponentModel.Win32Exception;

    private ToolResult ApplicationAuthorizationUnavailable(Exception error, ExecutionAttempt attempt)
    {
        _diagnostics["full-access"] = error.Message;
        return ToolPolicy.Result(new
        {
            error = "cortex_application_pipe",
            message = "Cerebral Full Access is configured, but its protected Cortex service session is unavailable. Reconnect or rotate Full Access before retrying this out-of-scope operation. Project-scoped tools remain available."
        }) with
        {
            Success = false,
            Evidence = new(attempt.Dispatched ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Rejected,
                attempt.ProviderId, attempt.RequestId)
        };
    }

    public string? CurrentProviderFingerprint(string providerId)
    {
        if (!providerId.StartsWith("mcp.", StringComparison.Ordinal)) return null;
        var serverId = providerId[4..];
        var current = _settings().McpServers.SingleOrDefault(x => x.Enabled && x.Id == serverId);
        return current is null || current.RequiresConfiguration ? null : CortexOperationPolicy.ProviderFingerprint(current);
    }

    public void RequireFileEditAdmission(ToolCall call, ToolExecutionContext context, JobReceiptLookupHint hint)
    {
        if (call.Name != "file.edit" || hint.EffectId != context.EffectId ||
            CurrentProviderFingerprint(hint.ProviderId) != hint.ProviderFingerprint ||
            !IsReceiptIdentifier(hint.JobIdHint))
            throw new UnauthorizedAccessException("The exact file-edit receipt lookup binding is unavailable.");
        var descriptor = Builtins.Single(item => item.Name == "file.edit");
        ToolPolicy.Grant(context.Profile, descriptor.Name, descriptor.Capability);
        ValidateSchema(call.Arguments, descriptor.InputSchema);
        _ = ToolPolicy.PathInScope(ToolPolicy.Text(call.Arguments, "path"), context);
        if (ToolPolicy.Text(call.Arguments, "oldText").Equals(ToolPolicy.Text(call.Arguments, "newText"), StringComparison.Ordinal))
            throw new ArgumentException("An unchanged file edit cannot be reconciled.");
    }

    public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
        ToolExecutionEvidence? previousEvidence, CancellationToken cancellationToken) =>
        ReconcileCoreAsync(call, context, previousEvidence, null, cancellationToken);

    public Task<ToolResult> ReconcileMissingJobAsync(ToolCall call, ToolExecutionContext context,
        ToolExecutionEvidence previousEvidence, JobReceiptLookupHint hint, CancellationToken cancellationToken) =>
        ReconcileCoreAsync(call, context, previousEvidence, hint, cancellationToken);

    private async Task<ToolResult> ReconcileCoreAsync(ToolCall call, ToolExecutionContext context,
        ToolExecutionEvidence? previousEvidence, JobReceiptLookupHint? hint, CancellationToken cancellationToken)
    {
        ToolResult Unknown(string message) => ToolPolicy.Limit(message, false) with
        {
            Evidence = previousEvidence is null ? new(ToolExecutionOutcome.Unknown, "cerebral")
                : previousEvidence with { Outcome = ToolExecutionOutcome.Unknown }
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = linked.Token;
        try
        {
            ValidateEffectId(context.EffectId);
            if (HasReservedBuiltinCollision(call.Name))
                return Unknown("A tool extension conflicts with a host tool name. No operation was replayed.");
            if (context.EnforceProfileScope) context = context with { FullAccess = context.FullAccess && _applicationAuthorization.Configured };
            if (context.EffectId is null || previousEvidence is null)
                return Unknown("The saved execution lacks a host effect identity or provider receipt. No operation was replayed.");
            if (previousEvidence.RequestId is { } previousRequest && previousRequest != context.EffectId)
                return Unknown("The saved provider receipt belongs to another request. No operation was replayed.");
            if (hint is not null && (call.Name != "file.edit" || previousEvidence.JobId is not null ||
                previousEvidence.RequestId != context.EffectId || hint.EffectId != context.EffectId ||
                hint.ProviderId != previousEvidence.ProviderId ||
                !IsReceiptIdentifier(hint.JobIdHint) ||
                CurrentProviderFingerprint(hint.ProviderId) != hint.ProviderFingerprint))
                return Unknown("The owner-supplied job receipt hint does not match the saved effect and current provider. No operation was replayed.");
            var recoveringExtension = _extensions.SingleOrDefault(item => item.Tools.Any(x => x.Name == call.Name));
            if (recoveringExtension is not null && IsReservedBuiltinName(call.Name))
                return Unknown("A tool extension conflicts with a host tool name. No operation was replayed.");
            if (hint is not null && recoveringExtension is not null)
                return Unknown("A host extension cannot use the file-edit provider receipt lookup.");
            if (recoveringExtension is IToolRecoveryExtension recovery)
            {
                var descriptor = recoveringExtension.Tools.Single(x => x.Name == call.Name);
                ToolPolicy.Grant(context.Profile, descriptor.Name, descriptor.Capability);
                if (call.Arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(call.Arguments.GetRawText()) > ToolPolicy.MaxInput)
                    return Unknown("The saved extension input is invalid; no operation was replayed.");
                ValidateSchema(call.Arguments, descriptor.InputSchema);
                return await recovery.ReconcileAsync(call, context, previousEvidence, ct);
            }
            var jobId = hint?.JobIdHint ?? previousEvidence.JobId;
            if (!IsReceiptIdentifier(jobId))
                return Unknown("An exact provider job ID is required. Cortex has no exact request-ID lookup; recent-job scans cannot establish this effect's outcome.");
            if (call.Arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(call.Arguments.GetRawText()) > ToolPolicy.MaxInput)
                return Unknown("The saved tool request is invalid; the existing execution remains unconfirmed.");

            // This host-only seam checks the original grant. It does not expose job administration
            // to model tool arguments, nor turn a recovery request into permission to execute again.
            var directPao = call.Name.Split('.', 3);
            if (call.Name.StartsWith("mcp.", StringComparison.Ordinal))
            {
                if (directPao.Length != 3 || directPao[2] != "pao")
                    return Unknown("This MCP tool has no exact execution-receipt recovery adapter.");
                ToolPolicy.Grant(context.Profile, call.Name, "mcp." + directPao[1]);
                _ = ValidatePao(call.Arguments, context, reconciling: true);
            }
            else
            {
                if (call.Name is not ("file.read" or "file.list" or "file.search" or "file.copy" or "file.write" or "file.edit" or ToolPolicy.RangeEditToolName or "file.mkdir" or "process.run" or "git.status" or "git.diff"))
                    return Unknown("This tool has no exact provider-receipt recovery adapter.");
                var descriptor = Builtins.Single(x => x.Name == call.Name);
                ToolPolicy.Grant(context.Profile, descriptor.Name, descriptor.Capability);
                if (call.Name == "file.copy") ToolPolicy.Grant(context.Profile, "file.read", "filesystem.read");
                ValidateSchema(call.Arguments, descriptor.InputSchema);
                if (call.Name == "process.run") _ = ExpectedProcessExitCode(call.Arguments);
                if (call.Name == ToolPolicy.RangeEditToolName)
                {
                    _ = RangeEditArguments(call.Arguments);
                    _ = ToolPolicy.PathInScope(ToolPolicy.Text(call.Arguments, "path"), context);
                }
                if (call.Name == "file.edit" && ToolPolicy.Text(call.Arguments, "oldText").Equals(
                        ToolPolicy.Text(call.Arguments, "newText"), StringComparison.Ordinal))
                    return Unknown("An unchanged replacement cannot establish a source edit; no operation was replayed.");
            }

            // The status query gets its own provider-generated request ID. Reusing the effect RID
            // here could cause a deduplicating provider to return the original call instead of status.
            var arguments = CerebralJson.Element(new { op = "job_stat", jid = jobId });
            JsonElement receipt;
            if (previousEvidence.ProviderId == "cortex.application.pipe")
            {
                if (!_applicationAuthorization.Configured)
                    return Unknown("The original Cortex application session is no longer configured; no substitute provider was queried.");
                receipt = await _applicationAuthorization.ExecuteUnifiedAsync(arguments, context.WorkingDirectory, ct,
                    () => RequireNoReservedBuiltinCollision(call.Name));
            }
            else if (previousEvidence.ProviderId.StartsWith("mcp.", StringComparison.Ordinal))
            {
                var serverId = previousEvidence.ProviderId[4..];
                if (directPao.Length == 3 && directPao[0] == "mcp" && directPao[1] != serverId)
                    return Unknown("The saved receipt provider does not match the original tool provider.");
                var selected = _settings().McpServers.SingleOrDefault(x => x.Enabled && x.Id == serverId);
                if (selected is null) return Unknown("The original PAO provider is unavailable; no substitute provider was queried.");
                var server = CerebralJson.Read<McpRegistration>(CerebralJson.Element(selected));
                var fingerprint = CortexOperationPolicy.ProviderFingerprint(server);
                if (hint is not null && fingerprint != hint.ProviderFingerprint)
                    return Unknown("The original provider registration changed before the receipt lookup.");
                void RequireCurrentProvider()
                {
                    RequireNoReservedBuiltinCollision(call.Name);
                    var current = _settings().McpServers.SingleOrDefault(x => x.Enabled && x.Id == serverId);
                    if (current is null || CortexOperationPolicy.ProviderFingerprint(current) != fingerprint)
                        throw new UnauthorizedAccessException("The provider registration changed during execution reconciliation.");
                }
                var descriptor = (await DiscoverAsync(server, ct)).SingleOrDefault(x => x.Name == $"mcp.{serverId}.pao");
                RequireCurrentProvider();
                if (descriptor is null) return Unknown("The original provider no longer exposes PAO receipt lookup.");
                ValidateSchema(arguments, descriptor.InputSchema);
                var response = await CallProviderAsync(server, "pao", arguments, ct, RequireCurrentProvider);
                if (!TryPaoReceipt(response, out receipt) ||
                    response.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
                    return Unknown("The provider did not confirm the status lookup; the original execution remains unconfirmed.");
            }
            else return Unknown("This native or extension provider cannot inspect a retained execution receipt. No operation was replayed.");

            var wrapper = PaoEvidence(receipt, previousEvidence.ProviderId, out var validWrapper);
            if ((hint is not null && (!validWrapper || !PaoOperationMatches(receipt, "job_stat") ||
                    !IsReceiptIdentifier(wrapper.RequestId) || wrapper.RequestId == context.EffectId ||
                    wrapper.Partial != false)) ||
                wrapper.Outcome != ToolExecutionOutcome.Succeeded ||
                !receipt.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("rec", out var existing) || existing.ValueKind != JsonValueKind.Object)
                return Unknown("The provider status lookup did not return the original execution receipt.");
            var actual = PaoEvidence(existing, previousEvidence.ProviderId, out var validOriginal);
            var mappedOperation = ExpectedPaoOperation(call.Name, call.Arguments);
            if ((hint is not null && (!validOriginal || mappedOperation != "replace" ||
                    actual.Partial != false || actual.Outcome is not (ToolExecutionOutcome.Succeeded or ToolExecutionOutcome.Failed or ToolExecutionOutcome.Rejected))) ||
                actual.RequestId != context.EffectId || actual.JobId != jobId ||
                mappedOperation is not null && !PaoOperationMatches(existing, mappedOperation))
                return Unknown("The provider returned a receipt for a different request, job or operation; the original execution remains unconfirmed.");
            // Interpret the original receipt, not the successful job_stat wrapper around it.
            var original = PaoReceiptResult(existing, previousEvidence.ProviderId);
            if (call.Name is not ("file.write" or "file.edit" or ToolPolicy.RangeEditToolName)) return original;
            var destination = ToolPolicy.PathInScope(ToolPolicy.Text(call.Arguments, "path"), context);
            var expectedPreimage = call.Arguments.TryGetProperty("expectedSha256", out var expected) &&
                expected.ValueKind == JsonValueKind.String ? expected.GetString() : null;
            var writeCollision = call.Name == "file.write" ? ToolPolicy.Text(call.Arguments, "collision", "fail") : null;
            return PaoMutationProjection.Project(original, call.Name, destination, expectedPreimage, writeCollision,
                call.Name == ToolPolicy.RangeEditToolName ? call.Arguments.GetProperty("startLine").GetInt32() : null,
                call.Name == ToolPolicy.RangeEditToolName ? call.Arguments.GetProperty("lineCount").GetInt32() : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { return Unknown("The status lookup timed out; the original execution remains unconfirmed. No operation was replayed."); }
        catch (OperationCanceledException) when (context.EffectId is not null)
        { return Unknown("The status lookup was cancelled; the original execution and its saved receipt remain unconfirmed. No operation was replayed."); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return Unknown("The existing execution could not be inspected: " + ToolPolicy.Limit(ex.Message, false).Content); }
    }

    private static async Task<ToolResult> ReadAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, ScopedPathAccess.Read, true);
        var bytes = await ReadBoundedAsync(path, ct); var text = DecodeReadText(bytes, "file.read");
        var start = ToolPolicy.Number(args, "startLine", 1, 1, 1_000_000); var count = ToolPolicy.Number(args, "lineCount", 200, 1, 1000);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var selected = start == 1 && count >= lines.Length ? text : string.Join('\n', lines.Skip(start - 1).Take(count));
        var result = ToolPolicy.Result(new { path, sha256 = ToolPolicy.Hash(bytes), startLine = start, totalLines = lines.Length, content = selected });
        return result with { Truncated = result.Truncated || start - 1 + count < lines.Length };
    }

    private static async Task<ToolResult> ListFilesAsync(JsonElement args, ToolExecutionContext context, bool search, CancellationToken ct)
    {
        var root = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, ScopedPathAccess.Read, true);
        var needle = search ? ToolPolicy.Text(args, "text") : "";
        if (search && needle.Length == 0) throw new ArgumentException("Search text cannot be empty.");
        var limit = ToolPolicy.Number(args, "limit", 100, 1, 500); var recursive = ToolPolicy.Boolean(args, "recursive");
        var entries = new List<object>(); var skipped = 0; var visited = 0; var pending = new Stack<string>(); pending.Push(root); var truncated = false;
        while (pending.TryPop(out var item))
        {
            ct.ThrowIfCancellationRequested();
            if (++visited > 5000) { truncated = true; break; }
            try { ToolPolicy.PathInScope(item, context, ScopedPathAccess.Read, true); }
            catch (UnauthorizedAccessException) { skipped++; continue; }
            if (Directory.Exists(item))
            {
                if (item != root && !search) entries.Add(new { path = item, type = "directory" });
                if (item == root || recursive)
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(item))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (pending.Count + visited >= 5000) { truncated = true; break; }
                        pending.Push(child);
                    }
                }
            }
            else if (!search) entries.Add(new { path = item, type = "file", bytes = new FileInfo(item).Length });
            else
            {
                try
                {
                    var text = ToolPolicy.Decode(await ReadBoundedAsync(item, ct)); var line = 0;
                    foreach (var content in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                    {
                        ct.ThrowIfCancellationRequested(); line++;
                        if (content.Contains(needle, ToolPolicy.Boolean(args, "ignoreCase") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) entries.Add(new { path = item, line, content = content[..Math.Min(content.Length, 1000)] });
                        if (entries.Count >= limit) break;
                    }
                }
                catch (Exception ex) when (ex is DecoderFallbackException or IOException or UnauthorizedAccessException) { skipped++; }
            }
            if (entries.Count >= limit) { truncated = pending.Count > 0 || search; break; }
        }
        var result = ToolPolicy.Result(new { entries, skipped, visited }); return result with { Truncated = result.Truncated || truncated };
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        ToolPolicy.RejectLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length > ToolPolicy.MaxInput) throw new IOException("File exceeds the 1 MiB text-operation limit; use a scoped registered tool for larger files.");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, ct); return bytes;
    }

    private static async Task<ToolResult> InspectAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, ScopedPathAccess.Read, true);
        if (!File.Exists(path)) throw new ArgumentException("File inspection requires an existing text file.");
        var bytes = await ReadBoundedAsync(path, ct);
        var text = DecodeReadText(bytes, "file.inspect");
        return ToolPolicy.Result(FileDetails(path, bytes, text));
    }

    private static async Task<ToolResult> WriteAsync(JsonElement args, ToolExecutionContext context, bool edit, ExecutionAttempt attempt, CancellationToken ct)
    {
        var path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context);
        var gate = FileMutationGate; await gate.WaitAsync(ct);
        string? temporary = null;
        try
        {
            ToolPolicy.PathInScope(path, context);
            var exists = File.Exists(path); var collision = edit ? "replace" : ToolPolicy.Text(args, "collision", "fail");
            if (collision is not ("fail" or "skip" or "replace")) throw new ArgumentException("collision must be fail, skip, or replace.");
            if (WriteDestinationDiagnostic(exists, collision, edit) is { } destinationDiagnostic) throw new IOException(destinationDiagnostic);
            if (exists && collision == "skip") return ToolPolicy.Result(new { path, skipped = true });
            if (edit && !exists) throw new FileNotFoundException("Edit requires an existing file.");
            byte[] previous = exists ? await ReadBoundedAsync(path, ct) : [];
            if (exists && !ToolPolicy.Hash(previous).Equals(ToolPolicy.Text(args, "expectedSha256"), StringComparison.OrdinalIgnoreCase)) throw new IOException("Preimage hash changed; read the file again before editing.");
            var content = edit ? ToolPolicy.Decode(previous) : ToolPolicy.Text(args, "content");
            if (edit)
            {
                var old = ToolPolicy.Text(args, "oldText"); var replacement = ToolPolicy.Text(args, "newText");
                content = TextEdits.ReplaceOne(content, old, replacement);
            }
            var encoding = exists ? ToolPolicy.TextEncoding(previous) : new UTF8Encoding(false, true);
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
            if (bytes.Length > ToolPolicy.MaxInput) throw new ArgumentException("Text output exceeds 1 MiB.");
            var parent = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Create the destination directory explicitly first.");
            temporary = Path.Combine(parent, ".cerebral-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            ToolPolicy.PathInScope(path, context);
            if (exists && !ToolPolicy.Hash(await ReadBoundedAsync(path, ct)).Equals(ToolPolicy.Hash(previous), StringComparison.Ordinal)) throw new IOException("Preimage changed during write.");
            ct.ThrowIfCancellationRequested(); attempt.Dispatch("cerebral.filesystem");
            File.Move(temporary, path, exists && collision == "replace"); temporary = null;
            return ToolPolicy.Result(FileDetails(path, bytes, content));
        }
        finally { if (temporary is not null) File.Delete(temporary); gate.Release(); }
    }

    private static string? WriteDestinationDiagnostic(bool exists, string collision, bool edit) =>
        exists && collision == "fail" ? ExistingDestinationWriteDiagnostic :
        !edit && !exists && collision == "replace" ? MissingReplacementDestinationDiagnostic : null;

    private static async Task<ToolResult> CopyAsync(JsonElement args, ToolExecutionContext context,
        ExecutionAttempt attempt, CancellationToken ct)
    {
        var source = ToolPolicy.PathInScope(ToolPolicy.Text(args, "sourcePath"), context, ScopedPathAccess.Read, true);
        var destination = ToolPolicy.PathInScope(ToolPolicy.Text(args, "destinationPath"), context);
        var gate = FileMutationGate; await gate.WaitAsync(ct);
        string? temporary = null;
        try
        {
            ToolPolicy.PathInScope(source, context, ScopedPathAccess.Read, true); ToolPolicy.PathInScope(destination, context);
            if (!File.Exists(source)) throw new FileNotFoundException("Copy requires an existing source file.", source);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("Copy destination already exists; choose a new path or use an explicit revision-checked edit.");
            var parent = Path.GetDirectoryName(destination)!;
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Create the destination directory explicitly first.");
            var bytes = await ReadBoundedAsync(source, ct);
            temporary = Path.Combine(parent, ".cerebral-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            ToolPolicy.PathInScope(source, context, ScopedPathAccess.Read, true); ToolPolicy.PathInScope(destination, context);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Copy destination appeared during the operation.");
            ct.ThrowIfCancellationRequested(); attempt.Dispatch("cerebral.filesystem");
            File.Move(temporary, destination); temporary = null;
            return ToolPolicy.Result(new { sourcePath = source, destinationPath = destination, bytes = bytes.Length, sha256 = ToolPolicy.Hash(bytes) });
        }
        finally { if (temporary is not null) File.Delete(temporary); gate.Release(); }
    }

    private static async Task<ToolResult> AppendAsync(JsonElement args, ToolExecutionContext context, ExecutionAttempt attempt, CancellationToken ct)
    {
        var path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, true);
        var gate = FileMutationGate;
        await gate.WaitAsync(ct);
        string? temporary = null;
        try
        {
            ToolPolicy.PathInScope(path, context, true);
            if (!File.Exists(path)) throw new FileNotFoundException("Append requires an existing file; create it with file.write first.");
            var previous = await ReadBoundedAsync(path, ct);
            var expected = ToolPolicy.Text(args, "expectedSha256");
            var expectedBytes = ToolPolicy.Number(args, "expectedBytes", 0, 0, ToolPolicy.MaxInput);
            var encoding = ToolPolicy.TextEncoding(previous);
            var addition = ToolPolicy.Text(args, "content");
            var additionBytes = encoding.GetBytes(addition);
            var preimageMatches = previous.Length == expectedBytes && ToolPolicy.Hash(previous).Equals(expected, StringComparison.OrdinalIgnoreCase);
            if (!preimageMatches)
            {
                var exactReplay = previous.Length == expectedBytes + additionBytes.Length &&
                    ToolPolicy.Hash(previous[..expectedBytes]).Equals(expected, StringComparison.OrdinalIgnoreCase) &&
                    previous.AsSpan(expectedBytes).SequenceEqual(additionBytes);
                if (!exactReplay) throw new IOException("Preimage hash or byte length changed; inspect the file again before appending.");
                var replayText = ToolPolicy.Decode(previous);
                var replay = FileDetails(path, previous, replayText);
                return ToolPolicy.Result(new { replay.path, alreadyApplied = true, appendedCharacters = addition.Length, replay.bytes, replay.lines, replay.words, replay.sha256 });
            }
            var priorText = ToolPolicy.Decode(previous);
            var content = priorText + addition;
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
            if (bytes.Length > ToolPolicy.MaxInput) throw new ArgumentException("Text output exceeds 1 MiB.");
            var parent = Path.GetDirectoryName(path)!;
            temporary = Path.Combine(parent, ".cerebral-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            ToolPolicy.PathInScope(path, context, true);
            if (!ToolPolicy.Hash(await ReadBoundedAsync(path, ct)).Equals(ToolPolicy.Hash(previous), StringComparison.Ordinal))
                throw new IOException("Preimage changed during append.");
            ct.ThrowIfCancellationRequested();
            attempt.Dispatch("cerebral.filesystem");
            File.Move(temporary, path, true);
            temporary = null;
            var details = FileDetails(path, bytes, content);
            return ToolPolicy.Result(new
            {
                details.path,
                alreadyApplied = false,
                appendedCharacters = addition.Length,
                details.bytes,
                details.lines,
                details.words,
                details.sha256
            });
        }
        finally { if (temporary is not null) File.Delete(temporary); gate.Release(); }
    }

    private static FileDetail FileDetails(string path, byte[] bytes, string text) => new(
        path,
        bytes.Length,
        text.Length == 0 ? 0 : text.Count(x => x == '\n') + 1,
        Regex.Matches(text, @"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Count,
        ToolPolicy.Hash(bytes));

    private sealed record FileDetail(string path, int bytes, int lines, int words, string sha256);

    private static int ExpectedProcessExitCode(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("expectedExitCode", out var expected)) return 0;
        if (arguments.EnumerateObject().Count(item => item.Name == "expectedExitCode") != 1 ||
            expected.ValueKind != JsonValueKind.Number || !expected.TryGetInt32(out var code) || code is < 0 or > 255)
            throw new ArgumentException("'expectedExitCode' must be a single integer between 0 and 255.");
        // Mapped PAO operations do not promise a distinct, complete child exit on every
        // deferred/recovery route. Never submit an expectation that such a route would drop.
        if (code != 0 && DeferredPaoOperation("process.run", arguments) is not null)
            throw new ArgumentException("A nonzero expectedExitCode is not supported for mapped dotnet build/test operations; nothing was dispatched.");
        return code;
    }

    private async Task<ToolResult> RunAsync(JsonElement args, ToolExecutionContext context, string[]? gitArgs, ExecutionAttempt attempt, CancellationToken ct, bool readOnly = false)
    {
        var registrations = _settings().Executables;
        var registration = registrations.SingleOrDefault(x => x.Id == (gitArgs is null ? ToolPolicy.Text(args, "executableId") : "git"));
        if (registration is null)
        {
            // Return only bounded public registration IDs, never paths, prefixes or configuration bodies.
            // Listing an ID does not change tool grants or bypass its readiness and argument checks.
            var ids = registrations.Select(x => x.Id).Where(id => id.Length is > 0 and <= 64 &&
                id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(8);
            var registered = string.Join(", ", ids);
            throw new UnauthorizedAccessException("Executable ID is not registered on this host. Registered IDs: " +
                (registered.Length == 0 ? "none" : registered) + ". Nothing was launched. " +
                "Use an exact registered ID with separate arguments; a program path is not an ID.");
        }
        PortableRegistrationPolicy.RequireReady(registration);
        var cwd = ToolPolicy.PathInScope(ToolPolicy.Text(args, gitArgs is null ? "workingDirectory" : "path"), context,
            readOnly ? ScopedPathAccess.Read : ScopedPathAccess.Write, true);
        if (!Directory.Exists(cwd)) throw new ArgumentException("Working directory must be a directory.");
        var argv = registration.PrefixArguments.Concat(gitArgs ?? ToolPolicy.Strings(args, "arguments")).ToArray();
        if (argv.Length > 256 || argv.Sum(x => x.Length) > 32768) throw new ArgumentException("Process arguments exceed limits.");
        if (gitArgs is not null && argv.Any(x => x == "-C" || x.StartsWith("--git-dir", StringComparison.Ordinal) || x.StartsWith("--work-tree", StringComparison.Ordinal))) throw new UnauthorizedAccessException("Git directory overrides are not permitted.");
        var name = Path.GetFileNameWithoutExtension(registration.Path).ToLowerInvariant();
        if (name is "cmd" or "bash" or "sh" or "zsh") throw new UnauthorizedAccessException("Shell command strings are not supported; register a program or a PowerShell script-file invocation.");
        if (name is "pwsh" or "powershell")
        {
            var index = Array.FindIndex(argv, x => x.Equals("-File", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= argv.Length) throw new ArgumentException("PowerShell requires -File followed by an existing scoped script file; -Command and -EncodedCommand are prohibited.");
            // Only host startup options may precede -File. A later -File must never disguise an earlier command.
            for (var i = 0; i < index; i++)
            {
                switch (argv[i].ToLowerInvariant())
                {
                    case "-nologo": case "-noprofile": case "-noninteractive": case "-sta": case "-mta": case "-login": break;
                    case "-executionpolicy": case "-inputformat": case "-outputformat": case "-windowstyle":
                        if (++i >= index || argv[i].StartsWith('-')) throw new ArgumentException("PowerShell startup option requires its separate value before -File.");
                        break;
                    default: throw new UnauthorizedAccessException("PowerShell accepts only startup options before -File; -Command, -EncodedCommand and abbreviated command switches are prohibited.");
                }
            }
            argv[index + 1] = ToolPolicy.PathInScope(argv[index + 1], context with { WorkingDirectory = cwd },
                ScopedPathAccess.Read, true);
            if (!File.Exists(argv[index + 1])) throw new ArgumentException("PowerShell -File requires an existing file, not a directory.");
        }
        var expectedExitCode = gitArgs is null ? ExpectedProcessExitCode(args) : 0;
        attempt.Dispatch("cerebral.process");
        var result = await ManagedProcess.RunAsync(registration.Path, argv, cwd, ct);
        var output = ToolPolicy.Result(new { result.ExitCode, stdout = result.Output, stderr = result.Error }, preserveStringEnds: true);
        return output with { Success = output.Success && result.ExitCode == expectedExitCode, Truncated = output.Truncated || result.Truncated,
            Evidence = new(result.ExitCode == expectedExitCode ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
                "cerebral.process", State: "completed", ExitCode: result.ExitCode) };
    }

    private async Task<ToolResult> FetchAsync(JsonElement args, ToolExecutionContext context, ExecutionAttempt attempt, CancellationToken ct)
    {
        try
        {
            var internet = context.Internet;
            if (internet is null || internet.Version != InternetAuthorityContract.Version)
                throw new InternetAccessException("internet-authority-missing", "Managed web tools require the host-owned Main Internet gate.");
            if (!Uri.TryCreate(ToolPolicy.Text(args, "url"), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
                throw new ArgumentException("An HTTP(S) URL without embedded credentials is required.");
            var origin = uri.IdnHost;
            if (_httpGates.Count >= 256 && !_httpGates.ContainsKey(origin))
                throw new InvalidOperationException("HTTP host catalog is full; restart the dispatcher to admit other hosts.");
            var gate = _httpGates.GetOrAdd(origin, _ => new(1, 1));
            var interval = ToolPolicy.Number(args, "minimumIntervalMs", 1000, 250, 60_000);
            await gate.WaitAsync(ct);
            try
            {
                // Preserve the caller's stricter legacy host pacing; it cannot raise either owner bucket.
                if (_httpLast.TryGetValue(origin, out var last))
                { var wait = last.AddMilliseconds(interval) - DateTimeOffset.UtcNow; if (wait > TimeSpan.Zero) await Task.Delay(wait, ct); }
                _httpLast[origin] = DateTimeOffset.UtcNow;
                var response = await internet.SendAsync(new("GET", uri, new Dictionary<string, string[]>
                { ["User-Agent"] = ["PegasusCerebral/0.1"], ["Accept-Encoding"] = ["identity"] },
                    MaximumResponseBytes: ToolPolicy.MaxOutput, ReturnPartialBody: true), ct).ConfigureAwait(false);
                attempt.Dispatch("cerebral.http");
                var bytes = response.Body; var truncated = response.Truncated;
                // Older fetch returned bounded decoded text. Never inflate an unbounded compressed response.
                if (response.Headers.TryGetValue("Content-Encoding", out var encodings) &&
                    encodings.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                {
                    if (truncated || encodings.Length != 1)
                        throw new InternetAccessException("internet-content-encoding", "A partial or multiply encoded response cannot be decoded safely.", true);
                    using var raw = new MemoryStream(bytes, writable: false);
                    using Stream decoded = encodings[0].ToLowerInvariant() switch
                    {
                        "gzip" => new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress),
                        "deflate" => new System.IO.Compression.DeflateStream(raw, System.IO.Compression.CompressionMode.Decompress),
                        "br" => new System.IO.Compression.BrotliStream(raw, System.IO.Compression.CompressionMode.Decompress),
                        _ => throw new InternetAccessException("internet-content-encoding", "The response encoding is unsupported.", true)
                    };
                    var decodedBytes = new byte[ToolPolicy.MaxOutput + 1]; var used = 0;
                    while (used < decodedBytes.Length)
                    { var read = await decoded.ReadAsync(decodedBytes.AsMemory(used), ct); if (read == 0) break; used += read; }
                    truncated = used > ToolPolicy.MaxOutput; bytes = decodedBytes[..Math.Min(used, ToolPolicy.MaxOutput)];
                }
                var content = Encoding.UTF8.GetString(bytes);
                var result = ToolPolicy.Result(new { status = response.StatusCode,
                    contentType = response.Headers.GetValueOrDefault("Content-Type")?.FirstOrDefault(),
                    location = response.Headers.GetValueOrDefault("Location")?.FirstOrDefault(), content,
                    response.GlobalRevision, response.MainRevision, response.ThrottledMilliseconds });
                var success = response.StatusCode is >= 200 and < 300;
                return result with { Success = success, Truncated = result.Truncated || truncated,
                    Evidence = new(success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
                        "cerebral.http", State: "completed", RequestId: context.EffectId) };
            }
            finally { gate.Release(); }
        }
        catch (InternetAccessException error)
        {
            return new(false, error.Code + ": " + error.Message)
            { Evidence = new(error.Dispatched ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Rejected,
                "cerebral.http", State: error.Code, RequestId: context.EffectId) };
        }
    }

    private async Task<IReadOnlyList<ToolDescriptor>> DiscoverAsync(McpRegistration server, CancellationToken ct)
    {
        ValidateRegistration(server);
        if (server.CortexNative is not null)
            return (await NativeProviderRequestAsync(server, "list", null, null, ct)).Deserialize<List<ToolDescriptor>>(CerebralJson.Options) ?? [];
        if (server.ViaUserConnector)
        {
            var result = await ConnectorRequestAsync(server, "list", null, null, ct);
            return result.Deserialize<List<ToolDescriptor>>(CerebralJson.Options) ?? [];
        }
        await using var client = await McpStdioClient.ConnectAsync(server, ct);
        return await client.ListToolsAsync(server.Id, ct);
    }


    private static void RequireNativeCortexRuntime(McpRegistration server)
    {
        if (server.CortexNative is null)
            throw new InvalidOperationException("Cortex requires an enrolled native pipe connection and cannot fall back to MCP, HTTP or stdio. Configure its user-instance enrollment; other work can continue.");
    }

    private async Task<ToolResult> ExecuteMcpAsync(ToolCall call, ToolExecutionContext context, ExecutionAttempt attempt, CancellationToken ct)
    {
        var pieces = call.Name.Split('.', 3);
        if (pieces.Length != 3) throw new ArgumentException("MCP names have the form mcp.<server>.<tool>.");
        var selected = _settings().McpServers.SingleOrDefault(x => x.Enabled && x.Id == pieces[1]) ?? throw new UnauthorizedAccessException("MCP server is not enabled.");
        // Settings can be edited while discovery awaits a provider. Hold the exact selected registration,
        // including a copy of argv, and refuse dispatch if that registration has since changed.
        var server = CerebralJson.Read<McpRegistration>(CerebralJson.Element(selected));
        var providerFingerprint = CortexOperationPolicy.ProviderFingerprint(server);
        var cortexOperation = pieces[2].StartsWith("cortex_", StringComparison.Ordinal);
        var boundCortexProvider = context.Cortex?.ServerId == server.Id;
        if ((cortexOperation || boundCortexProvider) && context.Cortex is { } bound &&
            (bound.ServerId != server.Id || bound.ProviderFingerprint is { } expected && expected != providerFingerprint))
            throw new UnauthorizedAccessException("The project's Cortex provider changed before discovery. Refresh the project context.");
        void RequireCurrentRegistration()
        {
            var current = _settings().McpServers.SingleOrDefault(x => x.Enabled && x.Id == server.Id);
            if (current is null || current.RequiresConfiguration || CortexOperationPolicy.ProviderFingerprint(current) != providerFingerprint)
                throw new UnauthorizedAccessException("The MCP provider registration changed during discovery. Refresh and retry the intended operation.");
            // The owner-authored marker is outside the saved provider fingerprint.
            if (current.CortexNativeRequired) RequireNativeCortexRuntime(current);
        }
        ToolPolicy.Grant(context.Profile, call.Name, $"mcp.{server.Id}");
        ct.ThrowIfCancellationRequested();
        // Semantic routing and host scope restrict transport, never grant permission
        // or identify the actual pipe peer. Native admission still proves that identity.
        if (cortexOperation || boundCortexProvider || server.CortexNativeRequired)
            RequireNativeCortexRuntime(server);
        var descriptor = CortexDescriptor((await DiscoverAsync(server, ct)).SingleOrDefault(x => x.Name == call.Name) ?? throw new ArgumentException("Tool is not exposed by the live MCP server."));
        RequireCurrentRegistration();
        var arguments = call.Arguments;
        if (pieces[2].StartsWith("cortex_", StringComparison.Ordinal))
        {
            if (context.Cortex is { } project && project.ServerId != server.Id) throw new UnauthorizedAccessException("This project is bound to a different Cortex connection.");
            arguments = CortexArguments(arguments, descriptor.InputSchema, pieces[2], context);
        }
        if (pieces[2] == "pao") arguments = BindPaoEffect(ValidatePao(arguments, context), context);
        ValidateSchema(arguments, descriptor.InputSchema);
        JsonElement result;
        if (pieces[2] == "pao" && _applicationAuthorization.Configured && RequiresApplicationAuthorization(arguments, context.Profile))
        {
            try
            {
                attempt.Dispatch("cortex.application.pipe", context.EffectId);
                var receipt = await _applicationAuthorization.ExecuteUnifiedAsync(arguments, context.WorkingDirectory, ct);
                _diagnostics.TryRemove("full-access", out _);
                return ConfirmEffectReceipt(PaoReceiptResult(receipt, attempt.ProviderId), context.EffectId);
            }
            catch (Exception ex) when (ApplicationAuthorizationFailure(ex))
            {
                return ApplicationAuthorizationUnavailable(ex, attempt);
            }
        }
        result = await CallProviderAsync(server, pieces[2], arguments, ct, () =>
        {
            RequireCurrentRegistration();
            attempt.Dispatch("mcp." + server.Id, pieces[2] == "pao" ? context.EffectId : null);
        });
        if (pieces[2] == "pao") return ConfirmEffectReceipt(PaoResult(result, attempt.ProviderId), context.EffectId);
        var success = !result.TryGetProperty("isError", out var error) || !error.GetBoolean();
        if (pieces[2].StartsWith("cortex_", StringComparison.Ordinal))
        {
            // Exact reads must retain their complete structured record. The caller chooses a smaller
            // budget for working context; truncating a JSON record here would erase exact-read evidence.
            var content = JsonSerializer.Serialize(result, CerebralJson.Options);
            var bounded = Encoding.UTF8.GetByteCount(content) <= 256 * 1024
                ? new ToolResult(success, content) : new ToolResult(false, "Cortex semantic result exceeds the 256 KiB limit; request fewer records or a smaller response budget.", true);
            if (pieces[2] == "cortex_read" && bounded.Success)
            {
                try
                {
                    RequireCurrentRegistration();
                    // A host namespace restriction may arrive while the passive read is in flight.
                    CortexArguments(arguments, descriptor.InputSchema, pieces[2], context);
                    ValidateExactCortexRead(arguments, result);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException)
                {
                    // Retain the exact JSON for receipt inspection, but never admit it as a full read.
                    return bounded with { Success = false,
                        Evidence = new(ToolExecutionOutcome.Unknown, attempt.ProviderId,
                            State: ex.Message[..Math.Min(ex.Message.Length, 512)], Partial: true) };
                }
            }
            var confirmed = CortexOperationPolicy.ConfirmMutation(pieces[2], arguments, bounded);
            return confirmed with { Evidence = new(confirmed.Success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Unknown, attempt.ProviderId) };
        }
        var limited = ToolPolicy.Result(result);
        return limited with { Success = limited.Success && success,
            Evidence = new(limited.Success && success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Unknown, attempt.ProviderId) };
    }

    public static string ValidateWorkingDirectory(string path, AgentProfile profile)
    {
        var admitted = ToolPolicy.PathInScope(path, new(profile, "", ""), mustExist: true);
        if (!Directory.Exists(admitted)) throw new ArgumentException("Project working directory must be an existing directory.");
        return admitted;
    }

    // Models must see the same closed Cortex result limit that dispatch enforces. Preserve
    // provider restrictions when the host's eight-result policy is the tighter contract.
    private static (int Minimum, int Maximum, int Default)? CortexLimit(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty("limit", out var rule)) return null;
        if (rule.ValueKind != JsonValueKind.Object || rule.TryGetProperty("type", out var type) &&
            (type.ValueKind != JsonValueKind.String || type.GetString() != "integer"))
            throw new InvalidDataException("Cortex limit must expose an integer contract.");
        decimal? Bound(string name)
        {
            if (!rule.TryGetProperty(name, out var bound)) return null;
            if (bound.ValueKind != JsonValueKind.Number || !bound.TryGetDecimal(out var value))
                throw new InvalidDataException("Cortex limit has an invalid numeric bound.");
            return value;
        }
        var minimum = Bound("minimum"); var maximum = Bound("maximum");
        var exclusiveMinimum = Bound("exclusiveMinimum"); var exclusiveMaximum = Bound("exclusiveMaximum");
        var hasEnum = rule.TryGetProperty("enum", out var values);
        if (hasEnum && values.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Cortex limit has an invalid enum contract.");
        var allowed = Enumerable.Range(1, 8).Where(value =>
            (minimum is null || value >= minimum.Value) && (maximum is null || value <= maximum.Value) &&
            (exclusiveMinimum is null || value > exclusiveMinimum.Value) && (exclusiveMaximum is null || value < exclusiveMaximum.Value) &&
            (!hasEnum || values.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) && number == value))).ToArray();
        if (allowed.Length == 0) throw new InvalidDataException("Cortex limit has no supported value within the host's one-to-eight result policy.");
        var preferred = 8;
        if (rule.TryGetProperty("default", out var fallback) && (fallback.ValueKind != JsonValueKind.Number || !fallback.TryGetInt32(out preferred)))
            throw new InvalidDataException("Cortex limit has an invalid integer default.");
        return (allowed[0], allowed[^1], allowed.OrderBy(value => Math.Abs((long)value - preferred)).First());
    }

    private static ToolDescriptor CortexDescriptor(ToolDescriptor descriptor)
    {
        var pieces = descriptor.Name.Split('.', 3);
        if (pieces.Length != 3 || !pieces[2].StartsWith("cortex_", StringComparison.Ordinal) || CortexLimit(descriptor.InputSchema) is not { } limit) return descriptor;
        var schema = JsonNode.Parse(descriptor.InputSchema.GetRawText())!.AsObject();
        var rule = schema["properties"]!["limit"]!.AsObject();
        rule["minimum"] = limit.Minimum; rule["maximum"] = limit.Maximum; rule["default"] = limit.Default;
        return descriptor with { InputSchema = CerebralJson.Element(schema) };
    }

    private JsonElement CortexArguments(JsonElement args, JsonElement schema, string tool, ToolExecutionContext context)
    {
        var node = JsonNode.Parse(args.GetRawText())!.AsObject();
        var mutation = CortexOperationPolicy.IsMutation(tool, args);
        var namespaceIndependent = tool is "cortex_status" or "cortex_namespaces";
        var host = _settings();
        var spaces = mutation ? host.CortexWriteNamespaces : host.CortexNamespaces;
        var project = context.Cortex;
        if (project is not null)
        {
            spaces = mutation ? (project.WriteNamespace is { } target && host.CortexWriteNamespaces.Contains(target, StringComparer.Ordinal) ? new List<string> { target } : [])
                : project.ReadNamespaces.Intersect(host.CortexNamespaces, StringComparer.Ordinal).ToList();
        }
        if (CortexOperationPolicy.IsDurableWrite(tool, args) &&
            project?.RequireKnowledgeApproval == true && project.KnowledgeWriteApproved != true)
            throw new UnauthorizedAccessException("This durable knowledge change requires author approval of its exact proposed payload.");
        if (spaces.Count == 0 && !namespaceIndependent) throw new UnauthorizedAccessException("No Cortex namespaces are enabled for this project operation.");
        if (mutation && !context.Profile.Capabilities.Contains("cortex.write", StringComparer.Ordinal)) throw new UnauthorizedAccessException("Cortex mutation requires the cortex.write capability.");
        if (mutation) CortexOperationPolicy.ValidateExplicitMutation(tool, args, context.SessionId);
        var properties = schema.TryGetProperty("properties", out var p) ? p : default;
        bool Has(string name) => properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out _);
        var limitPolicy = CortexLimit(schema);
        if (!mutation)
        {
            if (Has("maxUtf8Bytes") || tool == "cortex_read") node["maxUtf8Bytes"] ??= tool == "cortex_start" ? 4096 : 8192;
            if (Has("limit") && !node.ContainsKey("limit")) node["limit"] = limitPolicy?.Default ?? 8;
            if (Has("namespaces")) node["namespaces"] ??= JsonSerializer.SerializeToNode(spaces);
            if (Has("namespace")) node["namespace"] ??= spaces.FirstOrDefault() ?? throw new InvalidOperationException("Explicit Cortex namespaces are required.");
            if (Has("taskId")) node["taskId"] ??= context.SessionId;
            if (Has("format")) node["format"] ??= "compact";
        }
        bool NamespaceKey(string key) => key is "namespace" or "namespaces";
        void Visit(JsonNode? value, string key)
        {
            if (NamespaceKey(key) && value is not JsonArray && (value is not JsonValue namespaceValue || !namespaceValue.TryGetValue<string>(out var namespaceText) || !spaces.Contains(namespaceText, StringComparer.Ordinal)))
                throw new UnauthorizedAccessException("Cortex namespace must identify an enabled namespace.");
            if (value is JsonObject obj)
            {
                if (obj["taskId"] is JsonValue task && task.GetValue<string>() != context.SessionId)
                    throw new UnauthorizedAccessException("Cortex taskId must match the current Cerebral session.");
                if (tool == "cortex_read" && obj["recordType"] is JsonValue type && string.Equals(type.GetValue<string>(), "task", StringComparison.OrdinalIgnoreCase) &&
                    (obj["recordId"] is not JsonValue id || id.GetValue<string>() != context.SessionId))
                    throw new UnauthorizedAccessException("Cortex task recordId must match the current Cerebral session, including each batch coordinate.");
                foreach (var property in obj) Visit(property.Value, property.Key);
            }
            else if (value is JsonArray array)
            {
                if (NamespaceKey(key) && array.Count == 0) throw new UnauthorizedAccessException("Cortex namespace selections cannot be empty.");
                if (key == "records" && array.Count is < 1 or > 8) throw new ArgumentException("Cortex exact-read batches contain one to eight coordinates.");
                foreach (var item in array) Visit(item, key);
            }
            else if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                if (NamespaceKey(key) && !spaces.Contains(text, StringComparer.Ordinal)) throw new UnauthorizedAccessException("Cortex namespace is outside configured namespaces.");
                // Finish batches contain nested knowledge records; the same per-field bound applies to every record.
                if (key is "content" or "taskContent" && text.Length > 24 * 1024) throw new ArgumentException("Cortex record bodies are limited to 24 Ki characters.");
                if (key is "checkpoint" or "evidence" or "handoff" && text.Length > 4096) throw new ArgumentException("Cortex checkpoint fields are limited to 4096 characters.");
            }
        }
        Visit(node, "");
        if (node["taskId"] is JsonValue task && task.GetValue<string>() != context.SessionId) throw new UnauthorizedAccessException("Cortex taskId must match the current Cerebral session.");
        var maximumBytes = tool == "cortex_start" ? 8192 : tool == "cortex_read" ? 256 * 1024 : 64 * 1024;
        if (node["maxUtf8Bytes"] is JsonValue max && (max.GetValue<int>() < 512 || max.GetValue<int>() > maximumBytes)) throw new ArgumentException($"Cortex response budget must be between 512 and {maximumBytes} UTF-8 bytes.");
        if (node.TryGetPropertyValue("limit", out var requestedLimit))
        {
            var range = limitPolicy ?? (Minimum: 1, Maximum: 8, Default: 8);
            if (requestedLimit is not JsonValue limit || !limit.TryGetValue<int>(out var value) || value < range.Minimum || value > range.Maximum)
                throw new ArgumentException($"Cortex result limit must be between {range.Minimum} and {range.Maximum}.");
        }
        return mutation ? args.Clone() : CerebralJson.Element(node);
    }

    private async Task<ToolResult?> TryPaoAsync(ToolCall call, ToolExecutionContext context, ExecutionAttempt attempt, CancellationToken ct)
    {
        // Use PAO only where the fixed operation preserves the complete semantic contract.
        // Current wr has no create-only or compare-and-swap guarantee. Semantic writes therefore
        // stay on the confined native path before any provider discovery or mutation submission.
        if (call.Name is not ("file.read" or "file.list" or "file.search" or "file.copy" or "file.edit" or ToolPolicy.RangeEditToolName or "file.mkdir" or "process.run" or "git.status" or "git.diff")) return null;
        var args = call.Arguments;
        var mapped = new JsonObject();
        switch (call.Name)
        {
            case "file.read":
                var path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, ScopedPathAccess.Read, true);
                var count = ToolPolicy.Number(args, "lineCount", 200, 1, 1000); if (count > 512) return null;
                mapped["op"] = "read_lines"; mapped["src"] = path; mapped["line"] = ToolPolicy.Number(args, "startLine", 1, 1, 1_000_000); mapped["count"] = count; break;
            case "file.list":
                path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, ScopedPathAccess.Read, true);
                var limit = ToolPolicy.Number(args, "limit", 100, 1, 500); if (ToolPolicy.Boolean(args, "recursive") || limit > 48) return null;
                mapped["op"] = "ls"; mapped["src"] = path; mapped["max"] = limit; break;
            case "file.search":
                path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, ScopedPathAccess.Read, true);
                var pattern = ToolPolicy.Text(args, "text"); if (pattern.Length == 0) throw new ArgumentException("Search text cannot be empty.");
                mapped["op"] = "search"; mapped["src"] = path; mapped["pattern"] = pattern; mapped["fmt"] = "literal"; mapped["recur"] = ToolPolicy.Boolean(args, "recursive"); mapped["ignore_case"] = ToolPolicy.Boolean(args, "ignoreCase"); mapped["max"] = ToolPolicy.Number(args, "limit", 100, 1, 500); break;
            case "file.copy":
                path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "sourcePath"), context, ScopedPathAccess.Read, true);
                var destination = ToolPolicy.PathInScope(ToolPolicy.Text(args, "destinationPath"), context);
                mapped["op"] = "cp"; mapped["src"] = path; mapped["dst"] = destination; mapped["pol"] = "fail"; break;
            case ToolPolicy.RangeEditToolName:
                path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, true);
                var range = RangeEditArguments(args);
                await ValidateRangeEditSourceAsync(path, range, ct);
                mapped["op"] = "edit"; mapped["dst"] = path; mapped["line"] = range.StartLine; mapped["count"] = range.LineCount;
                mapped["sha"] = range.ExpectedSha256; mapped["text"] = range.NewText; mapped["enc"] = "utf8";
                break;
            case "file.edit":
                if (ToolPolicy.Text(args, "oldText").Equals(ToolPolicy.Text(args, "newText"), StringComparison.Ordinal))
                    throw new ArgumentException("Edit requires newText to differ from oldText; an unchanged replacement is not a source edit.");
                path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, true);
                if (!await CanPaoPreserveUtf8Async(path, ct)) return null;
                mapped["op"] = "replace"; mapped["dst"] = path; mapped["old"] = ToolPolicy.Text(args, "oldText"); mapped["enc"] = "utf-8";
                mapped["text"] = ToolPolicy.Text(args, "newText"); mapped["count"] = 1;
                mapped["sha"] = ToolPolicy.Text(args, "expectedSha256");
                break;
            case "file.mkdir":
                path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context);
                // A project-scoped directory that already exists needs no provider mutation. Keep the
                // protected application route for paths admitted only by Full Access.
                try
                {
                    var scoped = ToolPolicy.PathInScope(path, context with { FullAccess = false }, mustExist: true);
                    if (Directory.Exists(scoped))
                    {
                        ToolPolicy.RejectLinks(scoped);
                        return ToolPolicy.Result(new { path = scoped, ensured = true, existedBefore = true });
                    }
                }
                catch (Exception scopeFailure) when (scopeFailure is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException) { }
                mapped["op"] = "mkdir"; mapped["dst"] = path; break;
            case "process.run":
                if (!TryMapDotnet(args, context, mapped)) return null;
                break;
            case "git.status":
                path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, ScopedPathAccess.Read, true);
                mapped["op"] = DeferredPaoOperation(call.Name, args); mapped["cwd"] = path; mapped["tmo"] = ToolPolicy.Number(args, "timeoutSeconds", 30, 1, 600); break;
            case "git.diff":
                path = ToolPolicy.PathInScope(ToolPolicy.Text(args, "path"), context, ScopedPathAccess.Read, true);
                mapped["op"] = DeferredPaoOperation(call.Name, args); mapped["cwd"] = path; mapped["tmo"] = ToolPolicy.Number(args, "timeoutSeconds", 30, 1, 600); mapped["arg"] = JsonSerializer.SerializeToNode(new[] { "--no-ext-diff", "--no-textconv" }.Concat(ToolPolicy.Boolean(args, "staged") ? ["--cached"] : [])); break;
        }
        var arguments = BindPaoEffect(CerebralJson.Element(mapped), context);
        ToolResult SemanticResult(ToolResult result) => call.Name is "file.edit" or ToolPolicy.RangeEditToolName
            ? PaoMutationProjection.Project(result, call.Name, mapped["dst"]!.GetValue<string>(),
                mapped["sha"]?.GetValue<string>(), requestedStartLine: call.Name == ToolPolicy.RangeEditToolName ? mapped["line"]!.GetValue<int>() : null,
                requestedLineCount: call.Name == ToolPolicy.RangeEditToolName ? mapped["count"]!.GetValue<int>() : null)
            : result;
        if (_applicationAuthorization.Configured && RequiresApplicationAuthorization(arguments, context.Profile))
        {
            try
            {
                var receipt = await _applicationAuthorization.ExecuteUnifiedAsync(arguments, context.WorkingDirectory, ct,
                    () =>
                    {
                        RequireNoReservedBuiltinCollision(call.Name);
                        attempt.Dispatch("cortex.application.pipe", context.EffectId);
                    });
                _diagnostics.TryRemove("full-access", out _);
                return SemanticResult(ConfirmEffectReceipt(PaoReceiptResult(receipt, attempt.ProviderId), context.EffectId));
            }
            catch (Exception ex) when (ApplicationAuthorizationFailure(ex))
            {
                return ApplicationAuthorizationUnavailable(ex, attempt);
            }
        }
        var selected = _settings().McpServers.FirstOrDefault(x => x.Enabled && (x.Id.Equals("pao", StringComparison.OrdinalIgnoreCase) || x.Id.Contains("cortex", StringComparison.OrdinalIgnoreCase)));
        if (selected is null) return null;
        var server = CerebralJson.Read<McpRegistration>(CerebralJson.Element(selected));
        var providerFingerprint = CortexOperationPolicy.ProviderFingerprint(server);
        void RequireCurrentRegistration()
        {
            RequireNoReservedBuiltinCollision(call.Name);
            var current = _settings().McpServers.SingleOrDefault(x => x.Enabled && x.Id == server.Id);
            if (current is null || current.RequiresConfiguration || CortexOperationPolicy.ProviderFingerprint(current) != providerFingerprint)
                throw new UnauthorizedAccessException("The PAO provider registration changed during discovery. Refresh and retry the intended operation.");
        }
        ToolDescriptor? descriptor;
        try { descriptor = (await DiscoverAsync(server, ct)).SingleOrDefault(x => x.Name == $"mcp.{server.Id}.pao"); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No operation was submitted during discovery, so a native equivalent remains safe.
            _diagnostics[server.Id] = call.Name == ToolPolicy.RangeEditToolName
                ? "PAO discovery unavailable; range edit not submitted and no fallback writer used: " + ex.Message
                : "PAO discovery unavailable; native equivalent used: " + ex.Message;
            return null;
        }
        RequireCurrentRegistration();
        if (descriptor is null) return null;
        if (!descriptor.InputSchema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty("op", out var selector) || !selector.TryGetProperty("enum", out var operations) || !operations.EnumerateArray().Any(x => x.GetString() == mapped["op"]!.GetValue<string>())) return null;
        if (call.Name is "git.status" or "git.diff" && !properties.TryGetProperty("tmo", out _))
        {
            // Older strict gateways accepted these Git operations without provider timeout metadata.
            // Preserve that request shape; the existing caller deadline still bounds this operation.
            mapped.Remove("tmo");
            arguments = BindPaoEffect(CerebralJson.Element(mapped), context);
        }
        var deferred = context.EffectId is not null && DeferredPaoOperation(call.Name, args) is not null &&
            server.CortexNative is { Relay: null } && !server.ViaUserConnector && SupportsDeferredPao(descriptor.InputSchema);
        if (deferred)
        {
            mapped["wait"] = false;
            arguments = BindPaoEffect(CerebralJson.Element(mapped), context);
        }
        ValidateSchema(arguments, descriptor.InputSchema);
        var response = await CallProviderAsync(server, "pao", arguments, ct, () =>
        {
            RequireCurrentRegistration();
            attempt.Dispatch("mcp." + server.Id, context.EffectId);
        });
        if (deferred)
            return await AwaitDeferredPaoAsync(response, server, descriptor.InputSchema, attempt,
                context.EffectId!, mapped["op"]!.GetValue<string>(), RequireCurrentRegistration, ct);
        // Only the provider's complete pre-submission failure receipt permits a native equivalent.
        if (TryPaoReceipt(response, out var r) && r.GetProperty("ok").ValueKind == JsonValueKind.False &&
            PaoEvidence(r, attempt.ProviderId).Outcome == ToolExecutionOutcome.Rejected &&
            (context.EffectId is null || PaoEvidence(r, attempt.ProviderId).RequestId == context.EffectId) &&
            r.TryGetProperty("err", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() == "svc_down" &&
            r.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "failed" &&
            r.TryGetProperty("partial", out var partial) && partial.ValueKind == JsonValueKind.False &&
            r.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String && op.GetString() == mapped["op"]!.GetValue<string>())
        {
            attempt.ConfirmNotSubmitted();
            if (call.Name == ToolPolicy.RangeEditToolName)
                return ToolPolicy.Limit("The range-edit provider is unavailable; no operation was submitted and no fallback writer was used.", false) with
                { Evidence = new(ToolExecutionOutcome.Rejected, "cerebral", context.EffectId, Partial: false) };
            _diagnostics[server.Id] = "PAO execution service is unavailable; native equivalent used."; return null;
        }
        // A denied, failed, timed-out, or ambiguous admitted operation is never retried through native tools.
        return SemanticResult(ConfirmEffectReceipt(PaoResult(response, attempt.ProviderId), context.EffectId));
    }

    private static string? DeferredPaoOperation(string name, JsonElement arguments)
    {
        if (name == "git.status") return "git_status";
        if (name == "git.diff") return "git_diff";
        if (name != "process.run" || !ToolPolicy.Text(arguments, "executableId").Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return null;
        var argv = ToolPolicy.Strings(arguments, "arguments");
        return argv.FirstOrDefault() switch { "build" => "dotnet_build", "test" => "dotnet_test", _ => null };
    }

    // Receipt attribution must not implicitly opt a mutation into deferred submission.
    private static string? ExpectedPaoOperation(string name, JsonElement arguments) => name switch
    {
        ToolPolicy.RangeEditToolName => "edit", "file.edit" => "replace", "file.write" => "wr",
        _ => DeferredPaoOperation(name, arguments)
    };

    private static bool SupportsDeferredPao(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            properties.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != properties.EnumerateObject().Count() ||
            !properties.TryGetProperty("wait", out var wait) || wait.ValueKind != JsonValueKind.Object ||
            !wait.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "boolean" ||
            wait.TryGetProperty("enum", out var allowed) && (allowed.ValueKind != JsonValueKind.Array || !allowed.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.False)) ||
            !properties.TryGetProperty("op", out var selector) || !selector.TryGetProperty("enum", out var operations) ||
            operations.ValueKind != JsonValueKind.Array || !operations.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "job_stat"))
            return false;
        try { ValidateSchema(CerebralJson.Element(new { op = "job_stat", jid = "schema-only-placeholder" }), schema); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException) { return false; }
        return true;
    }

    private static bool PaoOperationMatches(JsonElement receipt, string operation) =>
        receipt.ValueKind == JsonValueKind.Object &&
        receipt.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == receipt.EnumerateObject().Count() &&
        receipt.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String && op.GetString() == operation;

    private static bool PaoPendingState(string? state) => state is "accepted" or "queued" or "running";

    private static bool ConsistentPaoPendingReceipt(JsonElement receipt, ToolExecutionEvidence evidence) =>
        receipt.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
        evidence.Partial is not true && evidence.ExitCode is null or 0 &&
        (!receipt.TryGetProperty("err", out var error) || error.ValueKind == JsonValueKind.Null) &&
        (!receipt.TryGetProperty("error", out var alternate) || alternate.ValueKind == JsonValueKind.Null);

    private async Task<ToolResult> AwaitDeferredPaoAsync(JsonElement response, McpRegistration server, JsonElement schema,
        ExecutionAttempt attempt, string effectId, string operation, Action requireCurrentRegistration, CancellationToken ct)
    {
        ToolResult Unknown(string message, ToolExecutionEvidence? actual = null) => ToolPolicy.Limit(message, false) with
        { Evidence = actual is null ? attempt.Evidence(ToolExecutionOutcome.Unknown) : actual with { Outcome = ToolExecutionOutcome.Unknown } };
        if (!TryPaoReceipt(response, out var receipt)) return ConfirmEffectReceipt(PaoResult(response, attempt.ProviderId), effectId);
        var evidence = PaoEvidence(receipt, attempt.ProviderId, out var validReceipt);
        // A complete admission refusal has no job to recover. Preserve that truthful rejection,
        // without routing the deferred submission through a native equivalent or a status lookup.
        if (validReceipt && evidence is { Outcome: ToolExecutionOutcome.Rejected, JobId: null } &&
            evidence.RequestId == effectId && PaoOperationMatches(receipt, operation) &&
            receipt.TryGetProperty("partial", out var rejectedPartial) && rejectedPartial.ValueKind == JsonValueKind.False &&
            receipt.TryGetProperty("err", out var rejectedError) && rejectedError.ValueKind == JsonValueKind.String &&
            IsPaoAdmissionError(rejectedError.GetString()))
            return ConfirmEffectReceipt(PaoResult(response, attempt.ProviderId), effectId);
        if (!validReceipt || evidence.RequestId != effectId || evidence.JobId is not { } jobId || !IsReceiptIdentifier(jobId) ||
            !PaoOperationMatches(receipt, operation) || evidence.State is not
                ("accepted" or "queued" or "running" or "done" or "completed" or "succeeded" or "failed" or "error" or "rejected" or "denied" or "cancelled" or "canceled"))
            return Unknown("The deferred PAO acknowledgement did not identify the original request, job and operation.", evidence);
        if (PaoPendingState(evidence.State) && (!ConsistentPaoPendingReceipt(receipt, evidence) ||
            response.TryGetProperty("isError", out var pendingError) && pendingError.ValueKind == JsonValueKind.True))
            return Unknown("The deferred PAO acknowledgement contained contradictory pending execution metadata.", evidence);
        // Retain the acknowledged original identity before any lookup, cancellation or registration check.
        // Status queries have their own provider-generated RID and must never overwrite this evidence.
        attempt.Acknowledge(evidence);
        requireCurrentRegistration();
        if (!PaoPendingState(evidence.State)) return ConfirmEffectReceipt(PaoResult(response, attempt.ProviderId), effectId);
        var query = CerebralJson.Element(new { op = "job_stat", jid = jobId });
        ValidateSchema(query, schema);
        for (var poll = 0; poll < 600; poll++)
        {
            if (poll != 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var status = await CallProviderAsync(server, "pao", query, ct, requireCurrentRegistration);
            requireCurrentRegistration();
            if (!TryPaoReceipt(status, out var wrapper) ||
                status.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True ||
                !PaoOperationMatches(wrapper, "job_stat") ||
                PaoEvidence(wrapper, attempt.ProviderId) is not { Outcome: ToolExecutionOutcome.Succeeded } lookup ||
                !IsReceiptIdentifier(lookup.RequestId) || lookup.RequestId == effectId || !wrapper.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("rec", out var original) || original.ValueKind != JsonValueKind.Object)
                return Unknown("The PAO status lookup did not confirm the original execution receipt.");
            var actual = PaoEvidence(original, attempt.ProviderId, out var validOriginal);
            if (!validOriginal || actual.RequestId != effectId || actual.JobId != jobId || !PaoOperationMatches(original, operation) ||
                PaoPendingState(actual.State) && !ConsistentPaoPendingReceipt(original, actual))
                return Unknown("The PAO status lookup returned a different request, job or operation.");
            attempt.Acknowledge(actual);
            if (!PaoPendingState(actual.State)) return ConfirmEffectReceipt(PaoReceiptResult(original, attempt.ProviderId), effectId);
        }
        return Unknown("The bounded PAO status lookup limit was reached; the acknowledged original job remains unconfirmed.");
    }

    private static async Task<bool> CanPaoPreserveUtf8Async(string path, CancellationToken ct)
    {
        var bytes = await ReadBoundedAsync(path, ct);
        _ = ToolPolicy.Decode(bytes); // Reject unknown legacy bytes instead of guessing an encoding.
        return ToolPolicy.TextEncoding(bytes).CodePage == Encoding.UTF8.CodePage &&
            !bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
    }

    private static ToolResult PaoReceiptResult(JsonElement receipt, string providerId)
    {
        receipt = SanitizePaoResponse(receipt, null);
        var evidence = PaoEvidence(receipt, providerId);
        if (receipt.ValueKind != JsonValueKind.Object || !receipt.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return ToolPolicy.Result(new { error = "pao_receipt_missing", message = "Cortex did not return a confirmed application-session PAO receipt." }) with
            { Success = false, Evidence = evidence with { Outcome = ToolExecutionOutcome.Unknown } };
        if (evidence.Outcome == ToolExecutionOutcome.Unknown)
            return ToolPolicy.Result(new { error = "pao_outcome_unconfirmed", message = "PAO has not confirmed a terminal outcome. Query the existing provider receipt before retrying.", receipt }) with
            { Success = false, Evidence = evidence };
        var bounded = ProjectPaoReceipt(receipt);
        return bounded with { Success = bounded.Success && evidence.Outcome == ToolExecutionOutcome.Succeeded,
            Evidence = bounded.Success ? evidence : evidence with { Outcome = ToolExecutionOutcome.Unknown } };
    }

    private static ToolResult PaoResult(JsonElement response, string providerId)
    {
        response = SanitizePaoResponse(response, null);
        if (TryPaoReceipt(response, out var receipt))
        {
            receipt = SanitizePaoResponse(receipt, null);
            var transportFailed = response.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
            if (!transportFailed) return PaoReceiptResult(receipt, providerId);
            var evidence = PaoEvidence(receipt, providerId);
            // MCP isError also marks delivered operation failures. Preserve a complete terminal
            // failure receipt; the caller still fences its request identity against the host effect.
            if (receipt.GetProperty("ok").ValueKind == JsonValueKind.False &&
                evidence.Outcome is ToolExecutionOutcome.Failed or ToolExecutionOutcome.Rejected &&
                evidence.RequestId is not null && evidence.JobId is not null &&
                evidence.State is "failed" or "error" or "rejected" or "denied")
                return PaoReceiptResult(receipt, providerId);
            return ToolPolicy.Result(new { error = "pao_transport_error", receipt }) with
            { Success = false, Evidence = evidence.Outcome == ToolExecutionOutcome.Succeeded ? evidence with { Outcome = ToolExecutionOutcome.Unknown } : evidence };
        }
        if (TryProjectPaoProviderError(response, providerId, out var providerError)) return providerError;
        // A delivered MCP response alone is not evidence that PAO executed the operation successfully.
        var unknown = ExtractReceipt(response) is { } raw ? PaoEvidence(raw, providerId) with { Outcome = ToolExecutionOutcome.Unknown }
            : new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, providerId);
        return ToolPolicy.Result(new { error = "pao_receipt_missing", message = "PAO did not return a confirmed execution receipt. Inspect the provider's retained log; the unverified payload is not a completion result." }) with { Success = false, Evidence = unknown };
    }

    internal static ToolExecutionEvidence PaoEvidence(JsonElement receipt, string providerId) => PaoEvidence(receipt, providerId, out _);

    private static ToolExecutionEvidence PaoEvidence(JsonElement receipt, string providerId, out bool validReceipt)
    {
        validReceipt = false;
        if (receipt.ValueKind != JsonValueKind.Object) return new(ToolExecutionOutcome.Unknown, providerId);
        var valid = receipt.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == receipt.EnumerateObject().Count();
        string? Identifier(string name, int maximum)
        {
            if (!receipt.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || text.Length > maximum ||
                text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.' or ':')))
            { valid = false; return null; }
            return text;
        }
        int? ExitCode(JsonElement source)
        {
            int? code = null;
            foreach (var name in new[] { "exitCode", "exit_code", "exit" })
            {
                if (!source.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || code is { } prior && prior != number)
                    valid = false;
                else code = number;
            }
            return code;
        }
        var requestId = Identifier("rid", 256); var jobId = Identifier("jid", 256); var state = Identifier("state", 64);
        bool? partial = null;
        if (receipt.TryGetProperty("partial", out var partialValue))
        {
            if (partialValue.ValueKind is JsonValueKind.True or JsonValueKind.False) partial = partialValue.GetBoolean();
            else valid = false;
        }
        var providerExit = ExitCode(receipt);
        int? processExit = null;
        var timedOut = false;
        if (receipt.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            processExit = ExitCode(data);
            foreach (var name in new[] { "timed_out", "timedOut" })
                if (data.TryGetProperty(name, out var timeout))
                {
                    if (timeout.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) valid = false;
                    else timedOut |= timeout.GetBoolean();
                }
        }
        var hasError = receipt.TryGetProperty("err", out var error) && error.ValueKind != JsonValueKind.Null;
        if (receipt.TryGetProperty("error", out var alternateError) && alternateError.ValueKind != JsonValueKind.Null)
        { hasError = true; if (error.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) error = alternateError; }
        var errorCode = hasError && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        if (receipt.TryGetProperty("op", out var operation) && operation.ValueKind != JsonValueKind.String) valid = false;
        var outcome = ToolExecutionOutcome.Unknown;
        if (valid && !timedOut && receipt.TryGetProperty("ok", out var ok) && (ok.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            if (!ok.GetBoolean())
            {
                if (partial != true && IsPaoAdmissionError(errorCode) && (state is null or "failed" or "error" or "rejected" or "denied")) outcome = ToolExecutionOutcome.Rejected;
                else if (state is "failed" or "error") outcome = ToolExecutionOutcome.Failed;
                else if (state is "cancelled" or "canceled") outcome = ToolExecutionOutcome.Cancelled;
                else if (partial != true && (state is "rejected" or "denied" || state is null && IsPaoAdmissionError(errorCode))) outcome = ToolExecutionOutcome.Rejected;
            }
            else if (!hasError && (providerExit is null or 0) && partial != true && (state is "done" or "completed" or "succeeded"))
                // A successful PAO transport can still report a failed child build/test process.
                outcome = processExit is not null and not 0 ? ToolExecutionOutcome.Failed : ToolExecutionOutcome.Succeeded;
        }
        validReceipt = valid && !timedOut;
        return new(outcome, providerId, requestId, jobId, state, partial, processExit ?? providerExit);
    }

    private static bool IsPaoAdmissionError(string? code) => code is "pao_arguments_invalid" or "invalid_arguments" or
        "user_dispatch_required" or "permission_denied" or "access_denied" or "svc_down" or "operation_not_allowed";

    private static bool IsPaoGateway(McpRegistration server, string name) =>
        (server.Id.Equals("pao", StringComparison.OrdinalIgnoreCase) || server.Id.Contains("cortex", StringComparison.OrdinalIgnoreCase)) &&
        name.Equals($"mcp.{server.Id}.pao", StringComparison.Ordinal);

    private bool TryMapDotnet(JsonElement args, ToolExecutionContext context, JsonObject mapped)
    {
        var executableId = ToolPolicy.Text(args, "executableId");
        if (!executableId.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            !_settings().Executables.Any(item => item.Id.Equals(executableId, StringComparison.Ordinal))) return false;
        var argv = ToolPolicy.Strings(args, "arguments");
        if (argv.Length < 1 || argv[0] is not ("build" or "test")) return false;
        var cwd = ToolPolicy.PathInScope(ToolPolicy.Text(args, "workingDirectory"), context, true);
        var sourceArgumentIndex = argv.Length > 1 && !argv[1].StartsWith("-", StringComparison.Ordinal) ? 1 : -1;
        var sourceArgument = sourceArgumentIndex == 1 ? argv[1] : cwd;
        var source = Path.IsPathFullyQualified(sourceArgument) ? sourceArgument : Path.Combine(cwd, sourceArgument);
        try { source = ToolPolicy.PathInScope(source, context, ScopedPathAccess.Read, true); }
        catch (FileNotFoundException error) when (IsMissingDotnetProjectFile(error.FileName))
        {
            throw new FileNotFoundException(MissingDotnetProjectFileDiagnostic(error, context), error.FileName, error);
        }
        if (Directory.Exists(source))
        {
            var candidates = new[] { "*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj" }
                .SelectMany(pattern => Directory.EnumerateFiles(source, pattern, SearchOption.TopDirectoryOnly))
                .Distinct(ToolPolicy.PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .OrderBy(path => path, ToolPolicy.PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToArray();
            source = candidates.Length switch
            {
                1 => candidates[0],
                0 => throw new FileNotFoundException(MissingDotnetProjectDiagnostic(source, context), source),
                _ => throw new ArgumentException("More than one .NET project or solution exists in the selected working directory. Supply the exact project or solution path.")
            };
        }
        // Discovery of an admitted directory does not admit a more-specific child grant.
        source = ToolPolicy.PathInScope(source, context, ScopedPathAccess.Read, true);
        // The project or solution is consumed as input; default build outputs still require
        // Write authority in its containing directory, independently of the writable cwd.
        _ = ToolPolicy.PathInScope(Path.GetDirectoryName(source)!, context, ScopedPathAccess.Write, true);
        mapped["op"] = DeferredPaoOperation("process.run", args);
        mapped["src"] = source; mapped["cwd"] = cwd;
        mapped["tmo"] = ToolPolicy.Number(args, "timeoutSeconds", 30, 1, 600);
        var remaining = new List<string>();
        for (var index = sourceArgumentIndex == 1 ? 2 : 1; index < argv.Length; index++)
        {
            if (argv[index] is "-c" or "--configuration")
            {
                if (++index >= argv.Length) throw new ArgumentException("dotnet configuration requires a value.");
                mapped["cfg"] = argv[index]; continue;
            }
            remaining.Add(argv[index]);
        }
        if (remaining.Count > 0) mapped["arg"] = JsonSerializer.SerializeToNode(remaining);
        return true;
    }

    private static bool IsMissingDotnetProjectFile(string? path) => Path.GetExtension(path) is { } extension &&
        (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase));

    private static string MissingDotnetProjectFileDiagnostic(FileNotFoundException failure, ToolExecutionContext context)
    {
        const int maximumHints = 4;
        var missing = failure.FileName!; // PathInScope supplies this only after root and reparse admission.
        var parent = Path.GetDirectoryName(missing);
        if (parent is null) return failure.Message;
        try
        {
            var admittedParent = ToolPolicy.PathInScope(parent, context, ScopedPathAccess.Read, true);
            if (!Directory.Exists(admittedParent)) return failure.Message;
            var comparer = ToolPolicy.PathComparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var projects = new List<string>();
            foreach (var pattern in new[] { "*.csproj", "*.sln", "*.slnx" })
            {
                foreach (var candidate in Directory.EnumerateFiles(admittedParent, pattern, SearchOption.TopDirectoryOnly).Take(maximumHints))
                {
                    var admitted = ToolPolicy.PathInScope(candidate, context, ScopedPathAccess.Read, true);
                    if (!File.Exists(admitted)) continue;
                    var name = Path.GetFileName(admitted);
                    if (name.Length is > 0 and <= 128 && !name.Any(char.IsControl)) projects.Add(name);
                }
            }
            var hints = projects.Distinct(comparer).OrderBy(name => name, comparer).Take(maximumHints).ToArray();
            return hints.Length == 0
                ? failure.Message + " No safe matching .csproj, .sln, or .slnx basename could be listed from its admitted parent; verify the exact project filename. Nothing was executed."
                : failure.Message + " Some project-file basenames in its admitted parent (up to 4): " +
                  string.Join(", ", hints.Select(name => JsonSerializer.Serialize(name))) +
                  ". Use the intended basename to correct the filename segment of the originally supplied path, then retry with that exact path. Nothing was executed.";
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or ArgumentException)
        {
            // Optional recovery metadata can never widen admission or replace the original refusal.
            return failure.Message;
        }
    }

    private static string MissingDotnetProjectDiagnostic(string directory, ToolExecutionContext context)
    {
        const string reason = "No .NET project or solution exists directly in the selected working directory.";
        var projects = new List<string>();
        // Suggestions are bounded, read-only, and never turn an ambiguous request into execution.
        // Scope and reparse validation still apply to each child before any file is advertised.
        foreach (var child in Directory.EnumerateDirectories(directory).Take(32))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                var admitted = ToolPolicy.PathInScope(child, context, ScopedPathAccess.Read, true);
                foreach (var file in Directory.EnumerateFiles(admitted).Take(32))
                {
                    var extension = Path.GetExtension(file);
                    if (extension is not (".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx")) continue;
                    projects.Add(ToolPolicy.PathInScope(file, context, ScopedPathAccess.Read, true));
                    if (projects.Count == 4) break;
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or ArgumentException)
            {
                // An unavailable optional hint cannot authorize or execute the original request.
            }
            if (projects.Count == 4) break;
        }
        return projects.Count == 0
            ? reason + " Supply its exact project or solution path; use file.list to locate it."
            : reason + " Available child projects: " + string.Join("; ", projects) +
              ". Put the chosen exact path immediately after 'build' or 'test' in arguments. Keep the existing project target framework; do not invent framework or plugin identity overrides. No build was dispatched.";
    }

    private static bool TryProjectPaoProviderError(JsonElement response, string providerId, out ToolResult result)
    {
        result = default!;
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("isError", out var failed) || failed.ValueKind != JsonValueKind.True)
            return false;
        string? text = null;
        if (response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            text = content.EnumerateArray().Select(block => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > 4096) text = text[..4096] + "…";
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            string? Field(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var code = Field("code") ?? Field("error") ?? Field("err") ?? "pao_provider_error";
            var message = Field("message") ?? Field("detail") ?? Field("reason") ?? "PAO rejected the typed operation.";
            result = ToolPolicy.Result(new { error = code, message }) with { Success = false,
                Evidence = new(IsPaoAdmissionError(code) ? ToolExecutionOutcome.Rejected : ToolExecutionOutcome.Unknown, providerId) };
            return true;
        }
        catch (JsonException)
        {
            result = ToolPolicy.Result(new { error = "pao_provider_error", message = text }) with { Success = false,
                Evidence = new(ToolExecutionOutcome.Unknown, providerId) };
            return true;
        }
    }

    private static ToolResult ProjectPaoReceipt(JsonElement receipt)
    {
        if (receipt.GetProperty("ok").ValueKind != JsonValueKind.True) return ToolPolicy.Result(receipt);
        // Range mutations need the original bounded RID/JID for exact semantic proof and
        // recovery. Existing status-only operation compaction remains unchanged.
        if (PaoOperationMatches(receipt, "edit")) return ToolPolicy.Result(receipt);
        if (!receipt.TryGetProperty("op", out var operation) || operation.GetString() != "read_lines" ||
            !receipt.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
            entries.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String))
            return CompactSuccessfulPaoReceipt(receipt);

        var content = string.Join('\n', entries.EnumerateArray().Select(item => item.GetProperty("text").GetString()));
        var projected = ToolPolicy.Result(new
        {
            path = data.TryGetProperty("src", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString() : null,
            sha256 = data.TryGetProperty("sha", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null,
            startLine = data.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var first) ? first : 1,
            totalLines = data.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var count) ? count : entries.GetArrayLength(),
            content
        });
        var hasMore = data.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
        var sourceTruncated = data.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True;
        return projected with { Truncated = projected.Truncated || hasMore || sourceTruncated };
    }

    private static ToolResult CompactSuccessfulPaoReceipt(JsonElement receipt)
    {
        if (receipt.TryGetProperty("partial", out var partial) && partial.ValueKind == JsonValueKind.True)
            return ToolPolicy.Result(receipt);
        var compact = new JsonObject { ["ok"] = true };
        if (receipt.TryGetProperty("op", out var operation) && operation.ValueKind == JsonValueKind.String)
            compact["op"] = operation.GetString();
        if (receipt.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
            compact["data"] = JsonNode.Parse(data.GetRawText());
        return ToolPolicy.Result(compact);
    }

    private static bool TryPaoReceipt(JsonElement response, out JsonElement receipt)
    {
        receipt = default;
        if (response.ValueKind != JsonValueKind.Object ||
            response.TryGetProperty("isError", out var error) && error.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        if (ExtractReceipt(response) is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        if (value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.EnumerateObject().Count()) return false;
        if (ok.ValueKind == JsonValueKind.True && value.TryGetProperty("err", out var issue) && issue.ValueKind != JsonValueKind.Null)
            return false;
        // Conflicting structured/text copies are not two independent confirmations. Preserve neither as success.
        if (response.TryGetProperty("structuredContent", out _) && response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        using var duplicate = JsonDocument.Parse(text.GetString()!);
                        if (duplicate.RootElement.ValueKind == JsonValueKind.Object && duplicate.RootElement.TryGetProperty("ok", out _) &&
                            !JsonElement.DeepEquals(value, duplicate.RootElement)) return false;
                    }
                    catch (JsonException) { }
                }
        receipt = value; return true;
    }

    private static JsonElement? ExtractReceipt(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        if (result.TryGetProperty("structuredContent", out var structured)) return structured;
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                { try { using var document = JsonDocument.Parse(text.GetString()!); return document.RootElement.Clone(); } catch (JsonException) { } }
        return null;
    }

    private static JsonElement ValidatePao(JsonElement args, ToolExecutionContext context, bool reconciling = false)
    {
        var op = ToolPolicy.Text(args, "op");
        var capability = op switch { "rd" or "read_lines" or "stat" or "ls" or "find" or "search" or "sha" => "filesystem.read", "wr" or "edit" or "replace" or "mkdir" => "filesystem.write", "git_status" or "git_diff" => "git.read", "run" or "pwsh" or "dotnet_build" or "dotnet_test" => "process.execute", "help" or "diag" or "tool_ls" => "diagnostics", _ => throw new UnauthorizedAccessException("PAO operation is not admitted by this agent adapter.") };
        if (!context.Profile.Capabilities.Contains(capability, StringComparer.Ordinal)) throw new UnauthorizedAccessException($"PAO operation requires '{capability}'.");
        if (args.TryGetProperty("ws", out _) || args.TryGetProperty("root", out _)) throw new UnauthorizedAccessException("Workspace aliases and privileged root codes are not admitted through agent PAO calls.");
        var node = JsonNode.Parse(args.GetRawText())!.AsObject();
        var pathAccess = capability is "filesystem.read" or "git.read" ? ScopedPathAccess.Read : ScopedPathAccess.Write;
        foreach (var field in new[] { "src", "dst", "cwd" })
            if (args.TryGetProperty(field, out var path))
                node[field] = ToolPolicy.PathInScope(path.GetString()!, context,
                    field == "src" && capability == "process.execute" ? ScopedPathAccess.Read : pathAccess);
        if (op is "dotnet_build" or "dotnet_test" && node["src"] is JsonValue project)
        {
            var source = project.GetValue<string>();
            // Read admission for the immutable project file cannot lend output authority.
            _ = ToolPolicy.PathInScope(Directory.Exists(source) ? source : Path.GetDirectoryName(source)!,
                context, ScopedPathAccess.Write, !reconciling);
        }
        if (op is "rd" or "read_lines" or "stat" or "ls" or "find" or "search" or "sha" or "pwsh" or "dotnet_build" or "dotnet_test" && node["src"] is null) throw new ArgumentException("PAO operation requires an explicit scoped src.");
        if (op is "wr" or "edit" or "replace" or "mkdir" && node["dst"] is null) throw new ArgumentException("PAO operation requires an explicit scoped dst.");
        // Status lookup retains grants and path scope, but must not reapply write-admission
        // preconditions that a successful original operation may already have changed.
        if (!reconciling && op is ("wr" or "edit" or "replace") && File.Exists(node["dst"]!.GetValue<string>()) && node["sha"] is null) throw new ArgumentException("PAO file replacement requires an explicit preimage sha.");
        if (op is "git_status" or "git_diff" or "run" or "pwsh" or "dotnet_build" or "dotnet_test") node["cwd"] ??= ToolPolicy.PathInScope(context.WorkingDirectory ?? throw new ArgumentException("PAO execution requires a scoped working directory."), context, pathAccess, !reconciling);
        if (op is "git_status" or "git_diff")
        {
            string[] allowed = op == "git_status" ? ["--porcelain=v1", "--short", "--branch", "--untracked-files=normal"] : ["--stat", "--name-only", "--cached", "--no-ext-diff", "--no-textconv"];
            var argv = ToolPolicy.Strings(args, "arg");
            if (argv.Any(x => !allowed.Contains(x, StringComparer.Ordinal))) throw new UnauthorizedAccessException("Read-only PAO Git argument is not admitted.");
            if (op == "git_diff") node["arg"] = JsonSerializer.SerializeToNode(argv.Concat(["--no-ext-diff", "--no-textconv"]).Distinct());
        }
        return CerebralJson.Element(node);
    }

    private static JsonElement SanitizePaoResponse(JsonElement response, string? credential)
    {
        var node = JsonNode.Parse(response.GetRawText());
        void Visit(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                foreach (var key in obj.Select(x => x.Key).ToArray())
                {
                    if (key.Equals("root", StringComparison.OrdinalIgnoreCase) || key.Equals("rootCredential", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("cerebralRoot", StringComparison.OrdinalIgnoreCase) || key.Equals("cerebralRootCredential", StringComparison.OrdinalIgnoreCase))
                    { obj.Remove(key); continue; }
                    if (obj[key] is JsonValue value && !string.IsNullOrEmpty(credential) && value.TryGetValue<string>(out var text) && text.Contains(credential, StringComparison.Ordinal))
                        obj[key] = text.Replace(credential, "[private application credential removed]", StringComparison.Ordinal);
                    else Visit(obj[key]);
                }
            }
            else if (current is JsonArray array) foreach (var item in array) Visit(item);
        }
        Visit(node);
        return CerebralJson.Element(node);
    }

    private static void ValidateRegistration(McpRegistration server)
    {
        PortableRegistrationPolicy.RequireReady(server);
        if (server.CortexNativeRequired && server.CortexNative is null)
            throw new InvalidOperationException("This Cortex registration requires native owner-session enrollment; it cannot fall back to MCP.");
        if (string.IsNullOrWhiteSpace(server.Id) || server.Id.Length > 64 || server.Id.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('_' or '-'))) throw new ArgumentException("MCP server id must use letters, digits, hyphen, or underscore.");
        if (server.CortexNative is { } native)
        {
            native.Validate();
            if (!string.IsNullOrEmpty(server.Url) || server.Arguments is null || server.Arguments.Count != 0 || server.ViaUserConnector != (native.Relay is not null))
                throw new ArgumentException("Native Cortex requires an explicit pipe binding without stdio arguments or an HTTP endpoint.");
            return;
        }
        if (!server.ViaUserConnector && !string.IsNullOrEmpty(server.Url)) throw new InvalidOperationException("Direct network MCP is not configured by this stdio adapter.");
    }

    internal static void ValidateSchema(JsonElement arguments, JsonElement schema)
    {
        // The server remains the authoritative validator. Enforce discovered top-level shape before dispatch.
        if (schema.TryGetProperty("required", out var required)) foreach (var item in required.EnumerateArray()) if (!arguments.TryGetProperty(item.GetString()!, out _)) throw new ArgumentException($"Missing tool argument '{item.GetString()}'.");
        if (!schema.TryGetProperty("properties", out var properties)) return;
        foreach (var property in arguments.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out var rule))
            {
                if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False) throw new ArgumentException($"Unknown tool argument '{property.Name}'.");
                continue;
            }
            if (rule.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                var valid = type.GetString() switch { "string" => property.Value.ValueKind == JsonValueKind.String, "integer" => property.Value.TryGetInt64(out _), "number" => property.Value.ValueKind == JsonValueKind.Number, "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False, "array" => property.Value.ValueKind == JsonValueKind.Array, "object" => property.Value.ValueKind == JsonValueKind.Object, _ => true };
                if (!valid) throw new ArgumentException($"Invalid type for '{property.Name}'.");
            }
            if (rule.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(x => x.GetRawText() == property.Value.GetRawText())) throw new ArgumentException($"Invalid value for '{property.Name}'.");
        }
    }

    private async Task<JsonElement> ConnectorRequestAsync(McpRegistration server, string action, string? tool, JsonElement? arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PortableRegistrationPolicy.RequireReady(server);
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip)) throw new InvalidOperationException("User connector requires an explicit loopback HTTP URL.");
        if (string.IsNullOrWhiteSpace(_settings().AccessKey)) throw new InvalidOperationException("User connector requires a configured access key.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(65));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, "/mcp")); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings().AccessKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new { serverId = server.Id, expectedExecutionFingerprint = ConnectorProtocol.ExecutionFingerprint(server), action, tool, arguments }, CerebralJson.Options), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _connectorHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException($"User connector denied request ({(int)response.StatusCode}); no alternate adapter will be used.");
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity) throw new McpRemoteException("User connector MCP request rejected; no alternate adapter will be used.");
            if (!response.IsSuccessStatusCode) throw new IOException($"User connector rejected request ({(int)response.StatusCode}).");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token); using var buffer = new MemoryStream();
            var block = new byte[4096]; int read;
            while ((read = await stream.ReadAsync(block, timeout.Token)) != 0) { if (buffer.Length + read > ToolPolicy.MaxInput) throw new IOException("Connector response exceeds 1 MiB."); buffer.Write(block, 0, read); }
            using var doc = JsonDocument.Parse(buffer.ToArray()); return doc.RootElement.Clone();
        }
        catch (HttpRequestException ex) { throw new IOException("User connector is disconnected or unavailable.", ex); }
    }

    public static Task<int> RunConnectorAsync(string[] args, CancellationToken cancellationToken = default) => UserSessionConnector.RunAsync(args, cancellationToken);
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _nativeClientGate.WaitAsync();
        try
        {
            foreach (var entry in _nativeCortexClients.Values) await entry.Client.DisposeAsync();
            _nativeCortexClients.Clear();
        }
        finally { _nativeClientGate.Release(); }
        await _applicationAuthorization.DisposeAsync(); _connectorHttp.Dispose(); _lifetime.Dispose();
    }
}
