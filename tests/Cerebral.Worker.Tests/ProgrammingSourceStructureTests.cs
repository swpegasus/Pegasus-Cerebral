using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Real isolated file/parser/plugin-ALC checks; no Worker main, model, active package or project execution.</summary>
public static class ProgrammingSourceStructureTests
{
    public static async Task<int> RunAsync(string current, string host, string packageName = "SourceStructureCandidatePackage")
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Source structure: " + message);
            checks++;
        }
        var scratch = Path.GetFullPath(Path.Combine(current, "artifacts", "SourceStructureFixtures"));
        var parent = Path.GetFullPath(Path.Combine(current, "artifacts")) + Path.DirectorySeparatorChar;
        if (!scratch.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || Directory.Exists(scratch))
            throw new IOException("The exact task-owned fixture directory must be absent.");
        Directory.CreateDirectory(scratch);
        AssemblyLoadContext? alc = null;
        try
        {
            var worker = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(host, "Cerebral.Worker.dll"));
            var loader = worker.GetType("Cerebral.Worker.ServicePluginRuntime+PluginLoadContext", throwOnError: true)!;
            var package = Path.Combine(current, packageName);
            var assemblyPath = Path.Combine(package, "Cerebral.Programming.dll");
            alc = (AssemblyLoadContext)Activator.CreateInstance(loader, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [assemblyPath, package], culture: null)!;
            var assembly = alc.LoadFromAssemblyPath(assemblyPath);
            Check(alc.IsCollectible && AssemblyLoadContext.GetLoadContext(assembly) == alc,
                "candidate loads through the actual frozen host's collectible private loader");
            using var plugin = (IServicePlugin)Activator.CreateInstance(assembly.GetType("Cerebral.Programming.ProgrammingServicePlugin", true)!)!;
            plugin.Initialize(new FixtureHost(package, Path.Combine(scratch, "data")));
            var extension = plugin.ToolExtensions.Single();
            var descriptor = extension.Tools.Single(item => item.Name == "program.source.structure");
            Check(descriptor.Capability == "filesystem.read", "registered tool requires only read capability");
            Check(descriptor.PrerequisiteTools.SequenceEqual(["file.read"]), "registered descriptor retains explicit source-read prerequisite");
            var scope = Path.Combine(scratch, "source");
            Directory.CreateDirectory(scope);
            var path = Path.Combine(scope, "Fixture.cs");
            var profile = new AgentProfile { AllowedRoots = [scope], AllowedTools = ["program.source.structure"], Capabilities = ["filesystem.read"] };
            var context = new ToolExecutionContext(profile, "isolated-structure", "fixture", scope, EffectId: "fixture-effect");
            async Task<ToolResult> Inspect(string text, int focusLine = 1, Encoding? encoding = null)
            {
                await File.WriteAllTextAsync(path, text, encoding ?? new UTF8Encoding(false, true));
                return await extension.ExecuteAsync(new("program.source.structure",
                    CerebralJson.Element(new { path, expectedSha256 = Hash(File.ReadAllBytes(path)), focusLine })), context, CancellationToken.None);
            }
            async Task<JsonElement> Read(string text, int focusLine = 1, Encoding? encoding = null)
            {
                var result = await Inspect(text, focusLine, encoding);
                Check(result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, Partial: false, RequestId: "fixture-effect" },
                    "real scoped parser call returns confirmed read-only evidence; success=" + result.Success + "; evidence=" + JsonSerializer.Serialize(result.Evidence) + "; result=" + result.Content[..Math.Min(512, result.Content.Length)]);
                Check(Encoding.UTF8.GetByteCount(result.Content) <= 4096, "serialized raw tool content respects the 4KiB bound");
                return JsonDocument.Parse(result.Content).RootElement.Clone();
            }
            const string nested = "namespace Demo;\npublic class Outer\n{\n    public class Inner\n    {\n        public void Run()\n        {\n            while (true)\n            {\n                break;\n            }\n        }\n    }\n}\n";
            var view = await Read(nested, 10);
            Check(!view.GetProperty("recovered").GetBoolean() && view.GetProperty("syntaxErrors").GetInt32() == 0, "valid nested fixture parses cleanly");
            Check(Nodes(view).Single(node => node.GetProperty("kind").GetString() == "FileScopedNamespaceDeclaration").GetProperty("header").GetString() == "namespace Demo",
                "namespace signature excludes its descendant declarations");
            var run = Nodes(view).Single(node => node.GetProperty("kind").GetString() == "MethodDeclaration" && node.GetProperty("header").GetString()!.Contains("void Run()", StringComparison.Ordinal));
            Check(run.GetProperty("start").GetProperty("line").GetInt32() == 6 &&
                run.GetProperty("openBrace").GetProperty("line").GetInt32() == 7 &&
                run.GetProperty("closeBrace").GetProperty("line").GetInt32() == 12, "method header/body positions use current physical coordinates");
            Check(Eligible(run) && run.GetProperty("editRange").GetProperty("startLine").GetInt32() == 6 &&
                run.GetProperty("editRange").GetProperty("lineCount").GetInt32() == 7, "complete method range excludes outer class braces");
            var focusIds = view.GetProperty("focusScopeIds").EnumerateArray().Select(item => item.GetString()).ToHashSet();
            Check(Nodes(view).Any(node => focusIds.Contains(node.GetProperty("id").GetString()) && node.GetProperty("kind").GetString() == "WhileStatement"),
                "focus inside the loop reports its containing while scope");
            Check(view.GetProperty("physicalLines").GetInt32() == 14, "a trailing LF adds no editable terminal empty segment");
            Check(view.GetProperty("parser").GetProperty("language").GetString() == "CSharp14" &&
                view.GetProperty("parser").GetProperty("symbols").GetArrayLength() == 0, "pinned parser options are explicit rather than inferred project settings");

            var broken = await Read("class Broken\n{\n    void M()\n    {\n        while (true)\n        {\n            return;\n", 7);
            Check(broken.GetProperty("recovered").GetBoolean() && broken.GetProperty("syntaxErrors").GetInt32() > 0 &&
                Nodes(broken).All(node => !Eligible(node)), "recovered source offers no confident declaration replacement ranges");
            Check(Nodes(broken).Where(node => node.GetProperty("kind").GetString() == "MethodDeclaration")
                .All(node => !node.TryGetProperty("closeBrace", out var close) || close.ValueKind == JsonValueKind.Null),
                "parser-inserted missing braces are not reported as real source braces");
            Check(!broken.GetRawText().Contains("TextMetricsTool", StringComparison.Ordinal), "missing intended declarations are never manufactured");

            var literals = await Read("class Literals\n{\n    string A = \"}\";\n    string B = $\"{{literal}} {1}\";\n    string C = \"\"\"raw { } text\"\"\";\n    /* } { */\n    void M() { }\n}\n", 7);
            Check(!literals.GetProperty("recovered").GetBoolean() && Nodes(literals).Any(node => node.GetProperty("kind").GetString() == "MethodDeclaration" && Eligible(node)),
                "comments, normal/interpolated/raw-string braces do not change member boundaries");
            var sameLine = await Read("class C { void A() {} void B() {} }\n");
            Check(Nodes(sameLine).Where(node => node.GetProperty("kind").GetString() == "MethodDeclaration").All(node => !Eligible(node)),
                "same-line neighboring members cannot become line-only method edits");
            var directives = await Read("#if CHOICE\nclass A {}\n#else\nclass B {}\n#endif\n");
            Check(directives.GetProperty("conditionalCompilation").GetBoolean() && Nodes(directives).All(node => !Eligible(node)),
                "unknown preprocessor configuration disables edit-span eligibility");
            var unicodeLine = "    void M() { var s = \"😀\"; }";
            var unicode = await Read("class Ω\n{\n" + unicodeLine + "\n}\n", 3);
            var unicodeMethod = Nodes(unicode).Single(node => node.GetProperty("kind").GetString() == "MethodDeclaration");
            Check(unicodeMethod.GetProperty("start").GetProperty("column").GetInt32() == 5 &&
                unicodeMethod.GetProperty("end").GetProperty("column").GetInt32() == unicodeLine.Length + 1 && Eligible(unicodeMethod),
                "Unicode source preserves UTF16 columns and UTF8/no-BOM range compatibility");
            foreach (var encoded in new[] { (new UTF8Encoding(true) as Encoding, "class C {}\n"),
                (new UnicodeEncoding(false, true) as Encoding, "class C {}\n"),
                (new UTF8Encoding(false) as Encoding, "class C {}\r\n") })
            {
                var format = await Read(encoded.Item2, 1, encoded.Item1);
                Check(!format.GetProperty("rangeEditFormat").GetBoolean() && Nodes(format).All(node => !Eligible(node)),
                    "BOM, UTF16 and CRLF inputs remain inspectable without advertising unsupported line edits");
            }
            var empty = await Read("");
            Check(empty.GetProperty("physicalLines").GetInt32() == 0 && Nodes(empty).Length == 0, "empty file has no editable physical lines");
            var longMember = await Read("class C\n{\n void M()\n {\n" + new string('\n', 513) + " }\n}\n", 3);
            Check(Nodes(longMember).All(node => !Eligible(node)), "complete declarations over the editor512-line bound are withheld");
            var wide = await Read("class Wide\n{\n" + string.Join("", Enumerable.Range(0, 150).Select(index => $" void M{index}() {{}}\n")) + "}\n", 80);
            Check(wide.GetProperty("omittedNodes").GetInt32() > 0, "wide outlines disclose their bounded omissions");

            async Task<JsonElement> Draft(string source, int startLine, int lineCount, string newText, string? requestPath = null)
            {
                await File.WriteAllTextAsync(path, source, new UTF8Encoding(false, true));
                var before = Hash(File.ReadAllBytes(path));
                var result = await extension.ExecuteAsync(new("program.source.structure", CerebralJson.Element(new
                {
                    path = requestPath ?? path, expectedSha256 = before, focusLine = startLine,
                    proposedEdit = new { startLine, lineCount, newText }
                })), context, CancellationToken.None);
                Check(result.Success && result.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, Partial: false },
                    "real draft inspection returns successful read-only parser evidence: " + result.Content[..Math.Min(256, result.Content.Length)]);
                Check(Encoding.UTF8.GetByteCount(result.Content) <= 4096 && Hash(File.ReadAllBytes(path)) == before,
                    "draft response stays within4KiB and leaves exact source bytes unchanged");
                return JsonDocument.Parse(result.Content).RootElement.Clone();
            }
            const string initializer = "class Draft\n{\n void M()\n {\n  var value = new Box\n  {\n   Value = 1\n  }\n  Use(value);\n }\n}\n";
            var draft = await Draft(initializer, 8, 1, "nextCall();");
            var evidence = draft.GetProperty("proposedEdit");
            Check(draft.GetProperty("recovered").GetBoolean() &&
                evidence.GetProperty("startBoundary").GetProperty("tokenKind").GetString() == "CloseBraceToken" &&
                evidence.GetProperty("replacement").GetProperty("firstTokenKind").GetString() == "IdentifierToken",
                "recovered initializer closing-token versus identifier proposal is exposed without supplying a repair");
            var boundary = evidence.GetProperty("startBoundary");
            Check(boundary.GetProperty("statement").GetProperty("kind").GetString() == "LocalDeclarationStatement" &&
                boundary.GetProperty("statement").GetProperty("rangeStartsInside").GetBoolean() &&
                boundary.GetProperty("initializer").GetProperty("kind").GetString() == "ObjectInitializerExpression" &&
                boundary.GetProperty("initializer").GetProperty("start").GetProperty("line").GetInt32() == 6 &&
                boundary.GetProperty("initializer").GetProperty("end").GetProperty("line").GetInt32() == 8,
                "nearest recovered statement and initializer report physical spans and the internal range boundary");
            Check(evidence.GetProperty("fingerprint").GetProperty("path").GetString() == path &&
                evidence.GetProperty("fingerprint").GetProperty("sourceSha256").GetString() == Hash(Encoding.UTF8.GetBytes(initializer)) &&
                evidence.GetProperty("fingerprint").GetProperty("startLine").GetInt32() == 8 &&
                evidence.GetProperty("fingerprint").GetProperty("lineCount").GetInt32() == 1 &&
                evidence.GetProperty("fingerprint").GetProperty("newTextSha256").GetString() == Hash(Encoding.UTF8.GetBytes("nextCall();")) &&
                !evidence.GetRawText().Contains("nextCall", StringComparison.Ordinal),
                "exact path/preimage/range/replacement hash is returned with metadata rather than raw proposed source");

            var fingerprintType = assembly.GetType("Cerebral.Programming.ProgrammingEditFingerprint", throwOnError: true)!;
            var fingerprint = Activator.CreateInstance(fingerprintType, [path, Hash(Encoding.UTF8.GetBytes(initializer)),
                8, 1, Hash(Encoding.UTF8.GetBytes("nextCall();"))])!;
            var confirms = assembly.GetType("Cerebral.Programming.ProgrammingSourceStructure", throwOnError: true)!
                .GetMethod("ConfirmsProposal", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check((bool)confirms.Invoke(null, [evidence, fingerprint])!,
                "actual private-loader tool output is accepted by the exact production draft-evidence consumer");
            var closing = await Draft(initializer, 8, 1, "}\nint nextValue = 2;");
            var fragment = closing.GetProperty("proposedEdit").GetProperty("replacement");
            Check(fragment.GetProperty("firstTokenKind").GetString() == "CloseBraceToken" &&
                fragment.GetProperty("lastTokenKind").GetString() == "SemicolonToken" &&
                fragment.GetProperty("lfCount").GetInt32() == 1 && !fragment.GetProperty("endsWithLf").GetBoolean(),
                "ParseTokens retains an unmatched leading closing token and exact fragment LF facts");
            var deletion = await Draft(initializer, 8, 1, "");
            Check(deletion.GetProperty("proposedEdit").GetProperty("replacement").GetProperty("firstTokenKind").ValueKind == JsonValueKind.Null &&
                deletion.GetProperty("proposedEdit").GetProperty("replacement").GetProperty("utf8Bytes").GetInt32() == 0,
                "empty deletion is a valid draft with no lexical tokens");
            var newlines = await Draft(initializer, 8, 1, "// comment\r\nvalue;\n");
            fragment = newlines.GetProperty("proposedEdit").GetProperty("replacement");
            Check(fragment.GetProperty("firstTokenKind").GetString() == "IdentifierToken" &&
                fragment.GetProperty("lfCount").GetInt32() == 2 && fragment.GetProperty("crCount").GetInt32() == 1 &&
                fragment.GetProperty("endsWithLf").GetBoolean(),
                "comments are trivia and CR/LF facts are described without editing or normalizing source");
            var proposalType = assembly.GetType("Cerebral.Programming.ProgrammingEditProposal", throwOnError: true)!;
            var makeFingerprint = proposalType.GetMethod("Fingerprint", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object RequestFingerprint(string requestPath, int startLine, int lineCount, string newText)
            {
                var proposal = Activator.CreateInstance(proposalType, [startLine, lineCount, newText])!;
                return makeFingerprint.Invoke(proposal, [requestPath, Hash(File.ReadAllBytes(path))])!;
            }
            bool Accepts(JsonElement produced, object expected) =>
                (bool)confirms.Invoke(null, [produced.GetProperty("proposedEdit"), expected])!;
            JsonElement keywordView = default;
            object? keywordFingerprint = null;
            foreach (var (statement, replacement, kind) in new[] {
                ("return null;", "return", "ReturnKeyword"), ("int value = 1;", "int", "IntKeyword"),
                ("if (true) return null;", "if", "IfKeyword"), ("new object();", "new", "NewKeyword") })
            {
                keywordView = await Draft("class Keywords\n{\n object M()\n {\n  " + statement + "\n }\n}\n", 5, 1, replacement);
                keywordFingerprint = RequestFingerprint(path, 5, 1, replacement);
                var produced = keywordView.GetProperty("proposedEdit");
                Check(produced.GetProperty("startBoundary").GetProperty("tokenKind").GetString() == kind &&
                    produced.GetProperty("replacement").GetProperty("firstTokenKind").GetString() == kind &&
                    produced.GetProperty("replacement").GetProperty("lastTokenKind").GetString() == kind &&
                    Accepts(keywordView, keywordFingerprint),
                    "actual loader/parser keyword evidence is accepted for current and replacement boundaries: " + kind);
            }
            foreach (var kind in new[] { "IdentifierName", "WhitespaceTrivia", "EndOfFileToken", "None", "8508" })
            {
                var invalid = JsonNode.Parse(keywordView.GetRawText())!;
                invalid["proposedEdit"]!["replacement"]!["firstTokenKind"] = kind;
                invalid["proposedEdit"]!["replacement"]!["lastTokenKind"] = kind;
                Check(!Accepts(JsonSerializer.SerializeToElement(invalid), keywordFingerprint!),
                    "keyword admission still rejects nodes, trivia, EOF and noncanonical kind names: " + kind);
            }
            foreach (var alias in new[] {
                path.Replace('\\', '/'),
                Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar + Path.GetFileName(path) })
            {
                var aliasView = await Draft(initializer, 8, 1, "nextCall();", alias);
                var expected = RequestFingerprint(alias, 8, 1, "nextCall();");
                Check((string)fingerprintType.GetProperty("Path")!.GetValue(expected)! == Path.GetFullPath(path) &&
                    aliasView.GetProperty("path").GetString() == Path.GetFullPath(path) &&
                    aliasView.GetProperty("proposedEdit").GetProperty("fingerprint").GetProperty("path").GetString() == Path.GetFullPath(path) &&
                    Accepts(aliasView, expected),
                    "actual scoped producer and request-fingerprint consumer agree for absolute slash/dot path aliases");
            }
            var boundedDraft = await Draft("class Wide\n{\n" + string.Join("", Enumerable.Range(0, 150).Select(index => $" void M{index}() {{}}\n")) + "}\n",
                80, 1, new string('a', 65536));
            Check(boundedDraft.GetProperty("omittedNodes").GetInt32() > 0 &&
                boundedDraft.GetProperty("proposedEdit").GetProperty("replacement").GetProperty("utf8Bytes").GetInt32() == 65536,
                "maximum-size fragment evidence survives bounded outline omissions");

            var currentHash = Hash(File.ReadAllBytes(path));
            async Task<ToolResult> Call(object args, ToolExecutionContext? selected = null, CancellationToken ct = default) =>
                await extension.ExecuteAsync(new("program.source.structure", args is JsonElement raw ? raw : CerebralJson.Element(args)), selected ?? context, ct);
            Check(!(await Call(new { path, expectedSha256 = new string('a', 64) })).Success, "stale source hash is rejected");
            Check(!(await Call(new { path, expectedSha256 = currentHash, extra = true })).Success, "closed argument shape rejects extra fields");
            Check(!(await Call(new { path, expectedSha256 = currentHash, focusLine = 999999 })).Success, "out-of-file focus line is rejected");
            foreach (var proposal in new object[] {
                new { startLine = 0, lineCount = 1, newText = "" },
                new { startLine = 1, lineCount = 513, newText = "" },
                new { startLine = 1, lineCount = 1, newText = new string('x', 65537) },
                new { startLine = 1, lineCount = 1, newText = 1 },
                new { startLine = 1, lineCount = 1, newText = "", extra = true },
                new { startLine = 154, lineCount = 1, newText = "" } })
                Check(!(await Call(new { path, expectedSha256 = currentHash, proposedEdit = proposal })).Success,
                    "malformed, over-limit or out-of-file draft is rejected without source mutation");
            using (var duplicate = JsonDocument.Parse("{\"startLine\":1,\"startLine\":2,\"lineCount\":1,\"newText\":\"\"}"))
                Check(!(await Call(new { path, expectedSha256 = currentHash, proposedEdit = duplicate.RootElement })).Success,
                    "duplicate nested draft properties are rejected");
            using (var invalidUnicode = JsonDocument.Parse("{\"path\":" + JsonSerializer.Serialize(path) +
                ",\"expectedSha256\":" + JsonSerializer.Serialize(currentHash) +
                ",\"proposedEdit\":{\"startLine\":1,\"lineCount\":1,\"newText\":\"\\uD800\"}}"))
                Check(!(await Call(invalidUnicode.RootElement)).Success, "ill-formed replacement Unicode is rejected");
            Check(!(await Call(new { path, expectedSha256 = new string('a', 64),
                proposedEdit = new { startLine = 1, lineCount = 1, newText = "" } })).Success,
                "draft inspection preserves the current source hash requirement");
            var outside = Path.Combine(scratch, "Outside.cs");
            await File.WriteAllTextAsync(outside, "class Outside {}", new UTF8Encoding(false));
            Check(!(await Call(new { path = outside, expectedSha256 = Hash(File.ReadAllBytes(outside)) })).Success, "scope read ceiling rejects outside file");
            var denied = new ToolExecutionContext(new AgentProfile { AllowedRoots = [scope], AllowedTools = ["program.source.structure"], Capabilities = [] }, "test", "test", scope);
            Check(!(await Call(new { path, expectedSha256 = currentHash }, denied)).Success, "missing read capability is rejected");
            var ungranted = new ToolExecutionContext(new AgentProfile { AllowedRoots = [scope], AllowedTools = [], Capabilities = ["filesystem.read"] }, "test", "test", scope);
            Check(!(await Call(new { path, expectedSha256 = currentHash }, ungranted)).Success, "direct extension invocation cannot bypass tool grant");
            Check(!(await Inspect(new string('x', 32769))).Success, "source over the32KiB UTF8 ceiling is rejected");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledObserved = false;
            try { _ = await Call(new { path, expectedSha256 = currentHash,
                proposedEdit = new { startLine = 1, lineCount = 1, newText = "}" } }, ct: cancelled.Token); }
            catch (OperationCanceledException) { cancelledObserved = true; }
            Check(cancelledObserved, "cancellation remains cancellation rather than a successful empty outline");
            var link = Path.Combine(scope, "Link.cs");
            try
            {
                File.CreateSymbolicLink(link, outside);
                Check(!(await Call(new { path = link, expectedSha256 = Hash(File.ReadAllBytes(outside)) })).Success, "scoped source inspection refuses symbolic links");
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.ComponentModel.Win32Exception ||
                error is IOException && (error.HResult & 0xffff) == 1314)
            { Console.WriteLine("UNAVAILABLE symbolic-link fixture: " + error.GetType().Name); }
            finally { if (File.Exists(link)) File.Delete(link); }

            var projected = ToolModelProjection.ReadableProjection("program.source.structure", view.GetRawText());
            Check(projected.Complete && projected.Text == view.GetRawText(), "existing generic plugin projection retains the bounded payload");
            Check(alc.Assemblies.Any(item => item.GetName().Name == "Microsoft.CodeAnalysis.CSharp") &&
                alc.Assemblies.Any(item => item.GetName().Name == "Microsoft.CodeAnalysis") &&
                !alc.Assemblies.Any(item => item.GetName().Name is "Cerebral.Core" or "Cerebral.Contracts"),
                "real frozen-host loader resolves both Roslyn assemblies privately and shares SDK identity");
            Check(!alc.Assemblies.Any(item => item.GetName().Name!.Contains("Analyzers", StringComparison.Ordinal)),
                "compiler/analyzer build assets are not runtime assemblies");
            Console.WriteLine("Isolated frozen-host plugin loader and real parser/file checks passed: " + checks);
        }
        finally
        {
            alc?.Unload();
            // This exact absent-before-run directory was created above beneath the fixed build family.
            Directory.Delete(scratch, recursive: true);
        }
        return checks;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static JsonElement[] Nodes(JsonElement view) => view.GetProperty("nodes").EnumerateArray().ToArray();
    private static bool Eligible(JsonElement node) => node.TryGetProperty("editRange", out var range) && range.ValueKind == JsonValueKind.Object;
    private sealed class FixtureHost(string package, string data) : IServicePluginHost
    {
        public string PluginId => "pegasus.programming";
        public string PackageDirectory => package;
        public string DataDirectory => data;
        public void Publish(string eventType, string? jobId, object? value) { }
    }
}
