using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Tools;

namespace Cerebral.Tools.Tests;

// These checks use production admission/projection and a deterministic local stdio MCP fixture.
// The fixture never writes a target file; its successful receipts are mapping evidence only.
internal static class RangeEditAdapterTests
{
    private const string ToolName = "file.edit-range";
    private const string FixtureArgument = "--range-pao-fixture";
    private const string ProviderJob = "range-fixture-original";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<int> RunAsync()
    {
        var checks = 0;
        var skipped = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Range adapter: " + message);
            checks++;
        }

        await using (var fixture = await Fixture.CreateAsync())
        {
            var descriptors = await fixture.Dispatcher.ListAsync(fixture.Context.Profile, default);
            var descriptor = descriptors.Single(x => x.Name == ToolName);
            var schema = descriptor.InputSchema;
            var properties = schema.GetProperty("properties");
            Check(descriptor.Capability == "filesystem.write", "descriptor requires the write capability");
            Check(schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToHashSet()
                .SetEquals(["path", "expectedSha256", "startLine", "lineCount", "newText"]), "descriptor requires every range/preimage operand");
            Check(schema.GetProperty("additionalProperties").ValueKind == JsonValueKind.False, "semantic schema is closed");
            Check(Bounds(properties.GetProperty("startLine"), 1, 1_000_000) &&
                  Bounds(properties.GetProperty("lineCount"), 1, 512), "descriptor exposes the fixed coordinate bounds");

            var oldGrant = fixture.Context with { Profile = Profile(fixture.WorkRoot, ["file.edit"]) };
            Check(!(await fixture.Dispatcher.ListAsync(oldGrant.Profile, default)).Any(x => x.Name == ToolName),
                "the legacy edit grant does not advertise range editing");
            await RejectAsync(fixture.Arguments(), "legacy edit grant", oldGrant);
            await RejectAsync(fixture.Arguments(), "a partial wildcard is not an exact tool grant",
                fixture.Context with { Profile = Profile(fixture.WorkRoot, ["file.*"]) });
            var missingCapability = fixture.Context with { Profile = Profile(fixture.WorkRoot, [ToolName], ["filesystem.read"]) };
            Check(!(await fixture.Dispatcher.ListAsync(missingCapability.Profile, default)).Any(x => x.Name == ToolName),
                "the literal name without the write capability stays hidden");
            await RejectAsync(fixture.Arguments(), "missing write capability", missingCapability);

            var result = await fixture.CallAsync();
            Check(result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ProviderId: "mcp.pao", JobId: ProviderJob },
                "the literal range grant admits a matched deterministic provider receipt");
            using (var semantic = JsonDocument.Parse(result.Content))
            {
                var data = semantic.RootElement;
                Check(data.GetProperty("path").GetString() == fixture.Target &&
                      data.GetProperty("sha256Before").GetString() == fixture.BeforeHash &&
                      data.GetProperty("sha256").GetString() == fixture.AfterHash &&
                      data.GetProperty("bytes").GetInt32() == fixture.AfterBytes.Length &&
                      data.GetProperty("startLine").GetInt32() == 2 && data.GetProperty("lineCount").GetInt32() == 1 &&
                      data.GetProperty("changed").GetBoolean(), "matched receipt projects only truthful semantic identity and range metadata");
                Check(!data.TryGetProperty("op", out _) && !data.TryGetProperty("enc", out _) &&
                      !data.TryGetProperty("dst", out _), "provider vocabulary stays out of the semantic receipt");
            }
            var mapped = fixture.Calls().Single();
            Check(mapped.GetProperty("op").GetString() == "edit" && mapped.GetProperty("dst").GetString() == fixture.Target &&
                  mapped.GetProperty("line").GetInt32() == 2 && mapped.GetProperty("count").GetInt32() == 1 &&
                  mapped.GetProperty("text").GetString() == "X" && mapped.GetProperty("sha").GetString() == fixture.BeforeHash &&
                  mapped.GetProperty("enc").GetString() == "utf8" && mapped.GetProperty("rid").GetString() == fixture.Context.EffectId &&
                  mapped.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(["op", "dst", "line", "count", "text", "sha", "enc", "rid"]),
                "semantic arguments map exactly to fixed edit plus the host effect RID");
            Check((await File.ReadAllBytesAsync(fixture.Target)).SequenceEqual(fixture.BeforeBytes),
                "successful fixture metadata does not masquerade as a real target mutation");

            var wildcard = fixture.Context with { Profile = Profile(fixture.WorkRoot, ["*"]) };
            var wildcardDescriptors = await fixture.Dispatcher.ListAsync(wildcard.Profile, default);
            Check(!wildcardDescriptors.Any(x => x.Name == ToolName), "a wildcard alone does not advertise the literal-gated range tool");
            Check(wildcardDescriptors.Any(x => x.Name == "file.edit"), "the existing exact-edit tool remains advertised under the wildcard");
            await RejectAsync(fixture.Arguments(), "a wildcard without the literal range grant", wildcard);
            var explicitWildcard = fixture.Context with { Profile = Profile(fixture.WorkRoot, ["*", ToolName]) };
            Check((await fixture.Dispatcher.ListAsync(explicitWildcard.Profile, default)).Any(x => x.Name == ToolName) &&
                  (await fixture.CallAsync(context: explicitWildcard)).Success,
                "a wildcard plus the literal range grant is admitted with write capability and scope");
            Check((await fixture.CallAsync(context: fixture.Context with
                { PathAccess = new(1, [new(fixture.Target, ScopedPathAccess.Write)]) })).Success,
                "an exact writable file grant admits only the intended destination");

            foreach (var key in new[] { "path", "expectedSha256", "startLine", "lineCount", "newText" })
            {
                var missing = fixture.Arguments(); missing.Remove(key);
                await RejectAsync(missing, "missing " + key);
            }
            foreach (var value in new object?[] { null, 7, "stale", new string('a', 63), new string('a', 65), new string('g', 64) })
            {
                var invalid = fixture.Arguments(); invalid["expectedSha256"] = value;
                await RejectAsync(invalid, "malformed whole-file SHA");
            }
            foreach (var value in new object[] { 0, -1, 1_000_001, 1.5, long.MaxValue })
            {
                var invalid = fixture.Arguments(); invalid["startLine"] = value;
                await RejectAsync(invalid, "invalid startLine");
            }
            foreach (var value in new object[] { 0, -1, 513, 1.5, long.MaxValue })
            {
                var invalid = fixture.Arguments(); invalid["lineCount"] = value;
                await RejectAsync(invalid, "invalid lineCount");
            }
            var extra = fixture.Arguments(); extra["op"] = "wr";
            await RejectAsync(extra, "unknown provider-shaped semantic field");
            var wrongType = fixture.Arguments(); wrongType["newText"] = 5;
            await RejectAsync(wrongType, "non-string replacement");
            var stale = fixture.Arguments(); stale["expectedSha256"] = new string('0', 64);
            await RejectAsync(stale, "stale whole-file SHA");
            var terminal = fixture.Arguments(); terminal["startLine"] = 4;
            await RejectAsync(terminal, "the read-only terminal empty segment is not a physical editable line");
            var overrun = fixture.Arguments(); overrun["lineCount"] = 3;
            await RejectAsync(overrun, "a range cannot run past the physical last line");
            foreach (var text in new[] { "B", "B\n", "X\r\n", "X\r", new string('x', 65_537), new string('\u00e9', 32_769) })
            {
                var invalid = fixture.Arguments(); invalid["newText"] = text;
                await RejectAsync(invalid, "normalized no-op, non-LF, or replacement byte limit");
            }

            foreach (var bytes in new[]
            {
                new byte[] { 0xef, 0xbb, 0xbf, 0x41, 0x0a },
                new byte[] { 0xff, 0xfe, 0x41, 0, 0x0a, 0 },
                new byte[] { 0xfe, 0xff, 0, 0x41, 0, 0x0a },
                new byte[] { 0xc3, 0x28 },
                Utf8.GetBytes("A\r\nB\r\nC\r\n"), Utf8.GetBytes("A\nB\r\nC"), Utf8.GetBytes("A\rB"),
                Array.Empty<byte>(), Utf8.GetBytes(new string('z', 1_048_577))
            })
            {
                await fixture.SetTargetAsync(bytes);
                var invalid = fixture.Arguments(); invalid["expectedSha256"] = Hash(bytes);
                await RejectAsync(invalid, "unsupported encoding/BOM/EOL, empty source, or source byte limit");
            }
            await fixture.SetTargetAsync(fixture.BeforeBytes);
            await File.WriteAllTextAsync(fixture.Target, "changed outside the requested line\nB\nC\n", Utf8);
            await RejectAsync(fixture.Arguments(), "a change outside the range still invalidates the whole-file preimage");
            await fixture.SetTargetAsync(fixture.BeforeBytes);

            var readOnly = fixture.Context with
            {
                FullAccess = true,
                PathAccess = new(1, [new(fixture.WorkRoot, ScopedPathAccess.Write), new(fixture.Target, ScopedPathAccess.Read)])
            };
            await RejectAsync(fixture.Arguments(), "the exact RO ceiling overrides RW ancestry and FullAccess", readOnly);
            var outside = fixture.Arguments(); outside["path"] = fixture.Outside;
            await RejectAsync(outside, "an existing out-of-root destination");
            var traversal = fixture.Arguments(); traversal["path"] = Path.Combine(fixture.WorkRoot, "..", "outside.txt");
            await RejectAsync(traversal, "parent traversal");
            var sibling = fixture.Arguments(); sibling["path"] = fixture.PrefixSibling;
            await RejectAsync(sibling, "a root-name prefix is not a directory grant");
            var missingPath = fixture.Arguments(); missingPath["path"] = Path.Combine(fixture.WorkRoot, "absent.txt");
            await RejectAsync(missingPath, "a missing source cannot become a create operation");

            var link = Path.Combine(fixture.WorkRoot, "linked");
            var linked = false;
            try
            {
                Directory.CreateSymbolicLink(link, fixture.Root); linked = true;
                var throughLink = fixture.Arguments(); throughLink["path"] = Path.Combine(link, "outside.txt");
                await RejectAsync(throughLink, "a link ancestor is rejected before provider delivery");
            }
            catch (Exception error) when (!linked && error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                skipped++;
                var detail = error.Message.Replace('\r', ' ').Replace('\n', ' ');
                Console.WriteLine($"SKIP: range adapter symlink fixture unavailable: {error.GetType().Name} (0x{error.HResult:X8}): " +
                    detail[..Math.Min(detail.Length, 256)]);
            }
            finally { if (linked) Directory.Delete(link); }

            fixture.Settings.McpServers.Clear();
            await RejectAsync(fixture.Arguments(), "no provider does not enable a native write fallback");

            async Task RejectAsync(Dictionary<string, object?> arguments, string label, ToolExecutionContext? context = null)
            {
                var before = await File.ReadAllBytesAsync(fixture.Target);
                var writeTime = File.GetLastWriteTimeUtc(fixture.Target);
                var calls = fixture.Calls().Count;
                var rejected = await fixture.CallAsync(arguments, context);
                Check(!rejected.Success && rejected.EffectiveOutcome == ToolExecutionOutcome.Rejected, label + " is rejected");
                Check(fixture.Calls().Count == calls && (await File.ReadAllBytesAsync(fixture.Target)).SequenceEqual(before) &&
                      File.GetLastWriteTimeUtc(fixture.Target) == writeTime, label + " precedes provider delivery and preserves bytes/mtime");
            }
        }

        foreach (var mode in new[] { "foreign-rid", "partial" })
        {
            await using var fixture = await Fixture.CreateAsync(mode: mode);
            var result = await fixture.CallAsync();
            Check(!result.Success && result.EffectiveOutcome == ToolExecutionOutcome.Unknown && fixture.Calls().Count == 1 &&
                  (await File.ReadAllBytesAsync(fixture.Target)).SequenceEqual(fixture.BeforeBytes),
                mode + " transport receipt remains unconfirmed without native replay");
        }

        await using (var fixture = await Fixture.CreateAsync(mode: "unknown"))
        {
            var call = new ToolCall(ToolName, CerebralJson.Element(fixture.Arguments()));
            var pending = await fixture.Dispatcher.ExecuteAsync(call, fixture.Context, default);
            Check(!pending.Success && pending.Evidence is { Outcome: ToolExecutionOutcome.Unknown, JobId: ProviderJob },
                "an admitted Unknown result retains its original provider job");
            File.Delete(fixture.Target);
            var beforeRevokedLookup = fixture.Calls().Count;
            var revoked = fixture.Context with { Profile = Profile(fixture.WorkRoot, ["*"]) };
            var notRecovered = await fixture.Dispatcher.ReconcileAsync(call, revoked, pending.Evidence, default);
            Check(!notRecovered.Success && notRecovered.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
                  fixture.Calls().Count == beforeRevokedLookup && !File.Exists(fixture.Target),
                "revoking the literal grant prevents recovery provider lookup even when the wildcard remains");
            var recovered = await fixture.Dispatcher.ReconcileAsync(call, fixture.Context, pending.Evidence, default);
            Check(recovered.Success && recovered.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, JobId: ProviderJob } &&
                  !File.Exists(fixture.Target), "recovery uses the original receipt without rereading or recreating a changed source");
            var calls = fixture.Calls();
            Check(calls.Count == 2 && calls[0].GetProperty("op").GetString() == "edit" &&
                  calls[1].GetProperty("op").GetString() == "job_stat" && calls[1].GetProperty("jid").GetString() == ProviderJob &&
                  !calls[1].TryGetProperty("rid", out _), "recovery queries only the exact original job and never resubmits the effect RID");
        }

        await using (var fixture = await Fixture.CreateAsync(mode: "recovery-foreign"))
        {
            var evidence = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "mcp.pao", fixture.Context.EffectId, ProviderJob);
            var call = new ToolCall(ToolName, CerebralJson.Element(fixture.Arguments()));
            File.Delete(fixture.Target);
            var recovered = await fixture.Dispatcher.ReconcileAsync(call, fixture.Context, evidence, default);
            Check(!recovered.Success && recovered.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
                  fixture.Calls().Count == 1 && fixture.Calls()[0].GetProperty("op").GetString() == "job_stat" && !File.Exists(fixture.Target),
                "foreign original request identity remains Unknown with no resubmission");
        }

        var maximumSource = Utf8.GetBytes("A\n" + new string('z', 1_048_574));
        await using (var fixture = await Fixture.CreateAsync(before: maximumSource, after: Utf8.GetBytes("X\n" + new string('z', 1_048_574)), line: 1))
            Check((await fixture.CallAsync()).Success, "the exact 1 MiB source boundary is admitted");
        var maximumReplacement = new string('x', 65_536);
        await using (var fixture = await Fixture.CreateAsync(before: Utf8.GetBytes("A"), after: Utf8.GetBytes(maximumReplacement), line: 1, text: maximumReplacement))
            Check((await fixture.CallAsync()).Success, "the exact 64 KiB UTF-8 replacement boundary is admitted");
        await using (var fixture = await Fixture.CreateAsync(before: Utf8.GetBytes("\u03b1\n\u03b2\n\u03b3\n"),
            after: Utf8.GetBytes("\u03b1\n\u03a9\n\u03b3\n"), text: "\u03a9"))
            Check((await fixture.CallAsync()).Success, "strict non-ASCII UTF-8 without a BOM remains supported");
        await using (var fixture = await Fixture.CreateAsync(after: Utf8.GetBytes("A\nC\n"), text: ""))
            Check((await fixture.CallAsync()).Success, "an empty replacement deletes a nonempty physical range");
        await using (var fixture = await Fixture.CreateAsync(before: Utf8.GetBytes(string.Join('\n', Enumerable.Repeat("B", 512))),
            after: Utf8.GetBytes("X"), line: 1, count: 512))
            Check((await fixture.CallAsync()).Success, "the exact 512-line range boundary is admitted");

        ProjectionChecks(Check);
        Console.WriteLine($"Range adapter checks passed ({checks}; {skipped} skipped; isolated files and deterministic local stdio receipts, no installed provider or real provider mutation).");
        return checks;
    }

    private static void ProjectionChecks(Action<bool, string> check)
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "range-projection-metadata-only.txt"));
        var before = Hash(Utf8.GetBytes("A\nB\nC\n"));
        var after = Hash(Utf8.GetBytes("A\nX\nC\n"));
        var receipt = Receipt(path, before, after, 6, 2, 1, 3, 3, "range-effect");
        ToolResult Result(JsonObject value) => new(true, value.ToJsonString())
        { Evidence = new(ToolExecutionOutcome.Succeeded, "mcp.pao", "range-effect", ProviderJob, "done", false) };
        ToolResult Project(ToolResult value) => ProjectReceipt(value, ToolName, path, before, line: 2, count: 1);
        check(Project(Result(receipt)).Success, "the internal range projection accepts a fully bound receipt");
        foreach (var mode in new[] { "dst", "sha_before", "sha", "line", "count", "lines_before", "lines_after", "enc", "bom", "len", "op", "same-sha" })
        {
            var altered = (JsonObject)receipt.DeepClone();
            var data = (JsonObject)altered["data"]!;
            switch (mode)
            {
                case "dst": data[mode] = path + ".foreign"; break;
                case "sha_before": data[mode] = new string('0', 64); break;
                case "sha": data.Remove(mode); break;
                case "line": data[mode] = 1; break;
                case "count": data[mode] = 2; break;
                case "lines_before": data[mode] = 1; break;
                case "lines_after": data[mode] = -1; break;
                case "enc": data[mode] = "utf16"; break;
                case "bom": data[mode] = true; break;
                case "len": data[mode] = -1; break;
                case "op": altered[mode] = "replace"; break;
                case "same-sha": data["sha"] = before; break;
            }
            var result = Project(Result(altered));
            check(!result.Success && result.EffectiveOutcome == ToolExecutionOutcome.Unknown,
                "a mismatched/incomplete admitted " + mode + " receipt cannot establish a range mutation");
        }
        var partial = Result(receipt) with { Evidence = new(ToolExecutionOutcome.Succeeded, "mcp.pao", "range-effect", ProviderJob, "done", true) };
        check(Project(partial).EffectiveOutcome == ToolExecutionOutcome.Unknown, "a partial receipt is not completed range evidence");
        check(Project(Result(receipt) with { Truncated = true }).EffectiveOutcome == ToolExecutionOutcome.Unknown,
            "a truncated receipt is not completed range evidence");
        var rawPartial = (JsonObject)receipt.DeepClone(); rawPartial["partial"] = true;
        check(Project(Result(rawPartial)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
            "a raw partial receipt cannot borrow complete execution evidence");
        var missingPartial = (JsonObject)receipt.DeepClone(); missingPartial.Remove("partial");
        check(Project(Result(missingPartial)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
            "a missing raw partial flag cannot establish a complete receipt");
        var malformedPartial = (JsonObject)receipt.DeepClone(); malformedPartial["partial"] = "false";
        check(Project(Result(malformedPartial)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
            "a string-valued raw partial flag cannot establish a complete receipt");
        var missingState = (JsonObject)receipt.DeepClone(); missingState.Remove("state");
        check(Project(Result(missingState)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
            "a missing raw state cannot borrow the successful evidence state");
        var pendingState = (JsonObject)receipt.DeepClone(); pendingState["state"] = "running";
        check(Project(Result(pendingState)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
            "a pending raw state contradicts successful completed evidence");
        check(Project(Result(pendingState) with
            { Evidence = new(ToolExecutionOutcome.Succeeded, "mcp.pao", "range-effect", ProviderJob, "running", false) })
            .EffectiveOutcome == ToolExecutionOutcome.Unknown, "matching pending raw and evidence states still do not prove completion");
        var contradictoryState = (JsonObject)receipt.DeepClone(); contradictoryState["state"] = "completed";
        check(Project(Result(contradictoryState)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
            "different terminal raw and evidence states do not establish one consistent completion");
        foreach (var mutation in new[] { "err", "error", "exit", "conflicting-exits", "data-exit",
            "timed_out-true", "timed_out-malformed", "timedOut-true", "timedOut-malformed" })
        {
            var raw = (JsonObject)receipt.DeepClone();
            var data = (JsonObject)raw["data"]!;
            switch (mutation)
            {
                case "err": raw["err"] = "fixture_error"; break;
                case "error": raw["error"] = "fixture_error"; break;
                case "exit": raw["exit"] = 7; break;
                case "conflicting-exits": raw["exit"] = 0; raw["exit_code"] = 7; break;
                case "data-exit": data["exitCode"] = 7; break;
                case "timed_out-true": data["timed_out"] = true; break;
                case "timed_out-malformed": data["timed_out"] = "false"; break;
                case "timedOut-true": data["timedOut"] = true; break;
                case "timedOut-malformed": data["timedOut"] = "false"; break;
            }
            var unconfirmed = Project(Result(raw));
            check(!unconfirmed.Success && unconfirmed.EffectiveOutcome == ToolExecutionOutcome.Unknown,
                "raw " + mutation + " cannot borrow the outer successful execution evidence");
        }
        check(Project(Result(receipt) with { Evidence = new(ToolExecutionOutcome.Succeeded, "mcp.pao", "range-effect", ProviderJob, "done") })
            .EffectiveOutcome == ToolExecutionOutcome.Unknown, "range evidence requires explicitly complete provider metadata");
        foreach (var key in new[] { "rid", "jid" })
        {
            var foreign = (JsonObject)receipt.DeepClone(); foreign[key] = "foreign-identity";
            check(Project(Result(foreign)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
                "raw " + key + " must match the admitted execution evidence");
        }
        var unknown = Result(receipt) with { Success = false, Evidence = new(ToolExecutionOutcome.Unknown, "mcp.pao", "range-effect", ProviderJob) };
        check(ReferenceEquals(Project(unknown), unknown), "an Unknown result is preserved instead of promoted by valid-looking data");

        var nonemptyWithoutLines = (JsonObject)receipt.DeepClone();
        nonemptyWithoutLines["data"]!["lines_after"] = 0; nonemptyWithoutLines["data"]!["len"] = 21;
        check(Project(Result(nonemptyWithoutLines)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
            "nonempty output bytes cannot establish zero physical output lines");
        var emptyWithLines = (JsonObject)receipt.DeepClone();
        emptyWithLines["data"]!["lines_after"] = 1; emptyWithLines["data"]!["len"] = 0;
        check(Project(Result(emptyWithLines)).EffectiveOutcome == ToolExecutionOutcome.Unknown,
            "empty output bytes cannot establish a positive physical output line count");

        var legacy = (JsonObject)receipt.DeepClone(); legacy["op"] = "replace"; legacy["data"]!["count"] = 1;
        var edit = ProjectReceipt(Result(legacy), "file.edit", path, before);
        using var legacyPayload = JsonDocument.Parse(edit.Content);
        check(edit.Success && !legacyPayload.RootElement.TryGetProperty("changed", out _),
            "legacy exact-edit projection remains successful without range-only fields");
        legacy["op"] = "wr";
        check(ProjectReceipt(Result(legacy), "file.write", path, before, "replace").Success,
            "legacy replacement-write projection retains matched-preimage semantics");
        foreach (var collision in new[] { "fail", "skip" })
            check(ProjectReceipt(Result(legacy), "file.write", path, before, collision).EffectiveOutcome == ToolExecutionOutcome.Unknown,
                "legacy " + collision + " receipts cannot invent create/skip proof");
    }

    public static async Task<int> RunPaoFixtureAsync()
    {
        var arguments = Environment.GetCommandLineArgs();
        var marker = Array.IndexOf(arguments, FixtureArgument);
        if (marker < 0 || marker + 1 >= arguments.Length) throw new ArgumentException("The range MCP fixture needs its own manifest.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(arguments[marker + 1]));
        var config = manifest.RootElement;
        var audit = config.GetProperty("audit").GetString()!;
        var mode = config.GetProperty("mode").GetString();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            if (!request.TryGetProperty("id", out var id)) continue;
            object result;
            switch (request.GetProperty("method").GetString())
            {
                case "initialize":
                    result = new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "range-readonly-fixture", version = "1" } };
                    break;
                case "tools/list":
                    result = new { tools = new[] { new { name = "pao", description = "Deterministic read-only range adapter fixture", inputSchema = new
                    {
                        type = "object", required = new[] { "op" }, additionalProperties = false,
                        properties = new
                        {
                            op = new { type = "string", @enum = new[] { "edit", "job_stat", "replace", "wr" } },
                            dst = new { type = "string" }, line = new { type = "integer" }, count = new { type = "integer" },
                            text = new { type = "string" }, sha = new { type = "string" }, enc = new { type = "string" },
                            rid = new { type = "string" }, jid = new { type = "string" }, old = new { type = "string" }
                        }
                    } } } };
                    break;
                case "tools/call":
                    var call = request.GetProperty("params");
                    if (call.GetProperty("name").GetString() != "pao") throw new InvalidOperationException("The fixture exposes only pao.");
                    var input = call.GetProperty("arguments");
                    await File.AppendAllTextAsync(audit, input.GetRawText() + "\n", Utf8);
                    var operation = input.GetProperty("op").GetString();
                    var saved = (JsonObject)JsonNode.Parse(config.GetProperty("receipt").GetRawText())!;
                    if (operation == "edit")
                    {
                        var expected = config.GetProperty("expected");
                        foreach (var name in new[] { "op", "dst", "line", "count", "text", "sha", "enc", "rid" })
                            if (!input.TryGetProperty(name, out var actual) || !JsonElement.DeepEquals(actual, expected.GetProperty(name)))
                                throw new InvalidOperationException("Range mapping changed fixed operand: " + name);
                        if (input.EnumerateObject().Count() != 8) throw new InvalidOperationException("Range mapping added an unexpected provider operand.");
                        if (mode == "foreign-rid") saved["rid"] = "foreign-effect";
                        if (mode == "partial") saved["partial"] = true;
                        if (mode == "unknown") saved["state"] = "running";
                    }
                    else if (operation == "job_stat")
                    {
                        if (input.GetProperty("jid").GetString() != ProviderJob || input.EnumerateObject().Count() != 2)
                            throw new InvalidOperationException("Recovery must inspect only the original job without reusing its effect RID.");
                        if (mode == "recovery-foreign") saved["rid"] = "foreign-effect";
                        saved = new JsonObject { ["ok"] = true, ["op"] = "job_stat", ["state"] = "done", ["rid"] = "fixture-status", ["jid"] = "fixture-status-job", ["partial"] = false,
                            ["data"] = new JsonObject { ["rec"] = saved } };
                    }
                    else throw new InvalidOperationException("The read-only fixture cannot execute " + operation);
                    result = new { isError = false, structuredContent = saved, content = Array.Empty<object>() };
                    break;
                default: result = new { }; break;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result }));
        }
        return 0;
    }

    private static bool Bounds(JsonElement field, int minimum, int maximum) => field.GetProperty("type").GetString() == "integer" &&
        field.GetProperty("minimum").GetInt32() == minimum && field.GetProperty("maximum").GetInt32() == maximum;
    private static ToolResult ProjectReceipt(ToolResult result, string tool, string destination, string before,
        string? collision = null, int? line = null, int? count = null)
    {
        var type = typeof(ToolDispatcher).Assembly.GetType("Cerebral.Tools.PaoMutationProjection", throwOnError: true)!;
        var method = type.GetMethod("Project", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, "Project");
        try { return (ToolResult)method.Invoke(null, [result, tool, destination, before, collision, line, count])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static AgentProfile Profile(string root, List<string> tools, List<string>? capabilities = null) => new()
    { AllowedRoots = [root], AllowedTools = tools, Capabilities = capabilities ?? ["filesystem.write"] };
    private static int PhysicalLines(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;
        return bytes.Count(x => x == (byte)'\n') + (bytes[^1] == (byte)'\n' ? 0 : 1);
    }
    private static JsonObject Receipt(string path, string before, string after, int length, int line, int count,
        int linesBefore, int linesAfter, string effect) => new()
    {
        ["ok"] = true, ["op"] = "edit", ["state"] = "done", ["rid"] = effect, ["jid"] = ProviderJob, ["partial"] = false,
        ["data"] = new JsonObject { ["dst"] = path, ["line"] = line, ["count"] = count, ["lines_before"] = linesBefore,
            ["lines_after"] = linesAfter, ["sha_before"] = before, ["sha"] = after, ["enc"] = "utf8", ["bom"] = false, ["len"] = length }
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required string WorkRoot { get; init; }
        public required string Target { get; init; }
        public required string Outside { get; init; }
        public required string PrefixSibling { get; init; }
        public required string Audit { get; init; }
        public required byte[] BeforeBytes { get; init; }
        public required byte[] AfterBytes { get; init; }
        public string BeforeHash => Hash(BeforeBytes);
        public string AfterHash => Hash(AfterBytes);
        public required int Line { get; init; }
        public required int Count { get; init; }
        public required string Text { get; init; }
        public required HostSettings Settings { get; init; }
        public required ToolExecutionContext Context { get; init; }
        public required ToolDispatcher Dispatcher { get; init; }

        public static async Task<Fixture> CreateAsync(string mode = "matched", byte[]? before = null, byte[]? after = null,
            int line = 2, int count = 1, string text = "X")
        {
            var root = Path.Combine(Path.GetTempPath(), "cerebral-range-adapter-" + Guid.NewGuid().ToString("N"));
            var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
            var target = Path.Combine(work, "target.txt");
            var outside = Path.Combine(root, "outside.txt");
            var prefix = Path.Combine(root, "work-sibling"); Directory.CreateDirectory(prefix);
            prefix = Path.Combine(prefix, "target.txt");
            before ??= Utf8.GetBytes("A\nB\nC\n"); after ??= Utf8.GetBytes("A\nX\nC\n");
            await File.WriteAllBytesAsync(target, before);
            await File.WriteAllBytesAsync(outside, before);
            await File.WriteAllBytesAsync(prefix, before);
            var effect = "range-effect-" + Guid.NewGuid().ToString("N");
            var audit = Path.Combine(root, "calls.jsonl");
            var manifestPath = Path.Combine(root, "fixture.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
            {
                audit, mode,
                expected = new { op = "edit", dst = target, line, count, text, sha = Hash(before), enc = "utf8", rid = effect },
                receipt = Receipt(target, Hash(before), Hash(after), after.Length, line, count, PhysicalLines(before), PhysicalLines(after), effect)
            }), Utf8);
            var executable = Environment.ProcessPath;
            if (executable is null || !Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new FileNotFoundException("The range fixture requires the installed dotnet host.", executable);
            var server = new McpRegistration { Id = "pao", Executable = executable, Enabled = true, ViaUserConnector = false,
                Arguments = [typeof(RangeEditAdapterTests).Assembly.Location, FixtureArgument, manifestPath] };
            var settings = new HostSettings { McpServers = [server] };
            var context = new ToolExecutionContext(Profile(work, [ToolName]), "range-tests", "range-tests", work)
            { EffectId = effect, EnforceProfileScope = true };
            return new Fixture { Root = root, WorkRoot = work, Target = target, Outside = outside, PrefixSibling = prefix,
                Audit = audit, BeforeBytes = before, AfterBytes = after, Line = line, Count = count, Text = text,
                Settings = settings, Context = context, Dispatcher = new ToolDispatcher(() => settings) };
        }
        public Dictionary<string, object?> Arguments() => new()
        { ["path"] = Target, ["expectedSha256"] = BeforeHash, ["startLine"] = Line, ["lineCount"] = Count, ["newText"] = Text };
        public Task<ToolResult> CallAsync(Dictionary<string, object?>? arguments = null, ToolExecutionContext? context = null) =>
            Dispatcher.ExecuteAsync(new(ToolName, CerebralJson.Element(arguments ?? Arguments())), context ?? Context, default);
        public async Task SetTargetAsync(byte[] bytes)
        {
            await File.WriteAllBytesAsync(Target, bytes);
            File.SetLastWriteTimeUtc(Target, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        public List<JsonElement> Calls()
        {
            var result = new List<JsonElement>();
            if (!File.Exists(Audit)) return result;
            foreach (var line in File.ReadAllLines(Audit))
            { using var document = JsonDocument.Parse(line); result.Add(document.RootElement.Clone()); }
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            await Dispatcher.DisposeAsync();
            var full = Path.GetFullPath(Root);
            if (!ScopedFilePolicy.IsWithin(full, Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("cerebral-range-adapter-", StringComparison.Ordinal))
                throw new InvalidOperationException("Range fixture cleanup target changed.");
            Directory.Delete(full, recursive: true);
        }
    }
}
