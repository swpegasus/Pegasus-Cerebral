using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Programming;
using Cerebral.Tools;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

/// <summary>Deterministic producer and scoped-file fixtures; no model generation or sink mutation is executed.</summary>
internal static class ProgrammingSourceGenerationTests
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-program-source-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "cerebral-program-source-outside-" + Guid.NewGuid().ToString("N") + ".cs");
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Programming source generation: " + message);
            checks++;
        }

        try
        {
            var sourcePath = Path.Combine(root, "Target.cs");
            var referencePath = Path.Combine(root, "Contract.cs");
            const string sourceText = "namespace Fixture;\ninternal static class Target { public static int Value => 1; }\n";
            const string referenceText = "namespace Fixture;\ninternal interface ITarget { int Value { get; } }\n";
            await File.WriteAllTextAsync(sourcePath, sourceText, new UTF8Encoding(false));
            await File.WriteAllTextAsync(referencePath, referenceText, new UTF8Encoding(false));
            await File.WriteAllTextAsync(outside, "internal static class Outside { }\n", new UTF8Encoding(false));
            var sourceHash = Hash(await File.ReadAllBytesAsync(sourcePath));

            using var plugin = new ProgrammingServicePlugin();
            plugin.Initialize(new ReviewPluginTestHost(Path.Combine(root, "PluginData"), AppContext.BaseDirectory));
            var profile = new AgentProfile
            {
                Id = "program-source-fixture", AllowedRoots = [root],
                AllowedTools = ["program.source.generate", "file.read", "file.write"],
                Capabilities = ["filesystem.read", "filesystem.write"]
            };
            var context = new ToolExecutionContext(profile, "program-source-session", "program-source-job", root)
                { EnforceProfileScope = true };
            await using var dispatcher = new ToolDispatcher(() => new HostSettings(), plugin.ToolExtensions);
            Task<ToolResult> Call(object arguments, ToolExecutionContext? execution = null, CancellationToken token = default) =>
                dispatcher.ExecuteAsync(new("program.source.generate", CerebralJson.Element(arguments)), execution ?? context, token);

            var descriptor = (await dispatcher.ListAsync(profile, default)).Single(tool => tool.Name == "program.source.generate");
            Check(descriptor.Capability == "filesystem.write" && descriptor.ModelVisible,
                "producer is discoverable to ordinary model catalogs through the existing write capability");
            Check(descriptor.PrerequisiteTools.SequenceEqual(new[] { "file.read", "file.write" }),
                "producer keeps exact read and validated sink schemas available without executing them");
            Check(descriptor.InputSchema.GetProperty("properties").GetProperty("instructions").GetProperty("maxLength").GetInt32() == 8192 &&
                  descriptor.InputSchema.GetProperty("properties").GetProperty("referencePaths").GetProperty("maxItems").GetInt32() == 8,
                "published schema exposes the bounded instructions and narrow reference list");
            VerifyServicePackageLoading(root, Check);

            var prepared = await Call(new
            {
                path = sourcePath,
                expectedSha256 = sourceHash,
                instructions = "Implement the requested cohesive correction while preserving the namespace and public contract.",
                referencePaths = new[] { referencePath }
            }, execution: context with { EffectId = "prepared-source-effect" });
            Check(prepared.Success && prepared.Generation is not null,
                "valid exact source and reference evidence produces a generation plan");
            Check(prepared.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ProviderId: "cerebral.programming.source",
                    RequestId: "prepared-source-effect", State: "prepared", Partial: false },
                "preparation provides its own exact successful evidence without claiming future generation or write completion");
            ConversationHarnessTests.VerifyProviderGenerationHandoff(prepared, Check);
            var generation = prepared.Generation!;
            ToolGenerationPlan.Validate(generation);
            Check(generation.SinkToolName == "file.write" && generation.ContentProperty == "content" &&
                  generation.MaxTokens == 4096 && generation.MaxAttempts == 2 && !generation.CompleteAssignment,
                "plan uses the bounded raw response and revision-checked file.write continuation");
            Check(prepared.RequiredSuccessfulTools?.SequenceEqual(new[] { "file.write" }) == true,
                "the prepared replacement requires this staged file.write rather than an earlier successful write");
            Check(generation.SinkArguments.GetProperty("path").GetString() == Path.GetFullPath(sourcePath) &&
                  generation.SinkArguments.GetProperty("collision").GetString() == "replace" &&
                  generation.SinkArguments.GetProperty("expectedSha256").GetString() == sourceHash &&
                  !generation.SinkArguments.TryGetProperty("content", out _),
                "sink arguments bind the exact canonical target and preimage without model-authored content");
            Check(TargetEvidence(generation.Prompt) == "1 | namespace Fixture;\n2 | internal static class Target { public static int Value => 1; }\n3 | " &&
                   generation.Prompt.Contains(referenceText, StringComparison.Ordinal) &&
                   generation.Prompt.Contains("Path: " + sourcePath, StringComparison.Ordinal) &&
                   generation.Prompt.Contains("SHA-256: " + sourceHash, StringComparison.Ordinal) &&
                   generation.Prompt.Contains("untrusted technical evidence", StringComparison.Ordinal) &&
                   Encoding.UTF8.GetByteCount(generation.Prompt) < 96 * 1024,
                "prompt preserves readable exact source and reference evidence with an explicit authority boundary");
            using (var receipt = JsonDocument.Parse(prepared.Content))
                Check(receipt.RootElement.GetProperty("prepared").GetBoolean() &&
                      receipt.RootElement.GetProperty("referenceCount").GetInt32() == 1 &&
                      receipt.RootElement.GetProperty("sha256").GetString() == sourceHash,
                    "preparation receipt exposes compact exact coordinates without generated code");
            Check(await File.ReadAllTextAsync(sourcePath) == sourceText,
                "successful preparation leaves the target source unchanged");

            Check(generation.Prompt.Contains("Evidence-only line coordinates", StringComparison.Ordinal) &&
                  generation.Prompt.Contains("Never include these prefixes in replacement text", StringComparison.Ordinal) &&
                  generation.Prompt.Contains("Source text lines: 3 ", StringComparison.Ordinal),
                "target coordinates are explicitly evidence-only and include the terminal empty text line");

            const string mixedSource = "first\r\n\u03b2eta\r\U0001F642gamma\n12 | literal\r\n";
            var mixedPath = Path.Combine(root, "Mixed.cs");
            var mixedBytes = Encoding.UTF8.GetBytes(mixedSource);
            await File.WriteAllBytesAsync(mixedPath, mixedBytes);
            var mixedHash = Hash(mixedBytes);
            var mixed = await Call(new { path = mixedPath, expectedSha256 = mixedHash,
                instructions = "Preserve the current source.", referencePaths = new[] { referencePath } });
            Check(mixed.Success && mixed.Generation is not null &&
                  TargetEvidence(mixed.Generation.Prompt) ==
                      "1 | first\r\n2 | \u03b2eta\r3 | \U0001F642gamma\n4 | 12 | literal\r\n5 | ",
                "numbered target evidence preserves CRLF, lone CR, LF, Unicode and literal coordinate-like source text exactly");
            Check(mixed.Generation!.Prompt.Contains("Source text lines: 5 ", StringComparison.Ordinal) &&
                  mixed.Generation.Prompt.Contains(referenceText, StringComparison.Ordinal) &&
                  mixed.Generation.SinkArguments.GetProperty("expectedSha256").GetString() == mixedHash &&
                  mixed.Generation.SinkArguments.GetProperty("path").GetString() == Path.GetFullPath(mixedPath) &&
                  (await File.ReadAllBytesAsync(mixedPath)).SequenceEqual(mixedBytes),
                "numbering leaves raw reference text, canonical sink coordinates, preimage and actual source bytes unchanged");

            foreach (var item in new[]
            {
                (Text: "", Evidence: "1 | "),
                (Text: "\u03b2\U0001F642", Evidence: "1 | \u03b2\U0001F642")
            })
            {
                await File.WriteAllTextAsync(mixedPath, item.Text, new UTF8Encoding(false));
                var singleHash = Hash(await File.ReadAllBytesAsync(mixedPath));
                var single = await Call(new { path = mixedPath, expectedSha256 = singleHash,
                    instructions = "Preserve the current source." });
                Check(single.Success && single.Generation is not null &&
                      TargetEvidence(single.Generation.Prompt) == item.Evidence &&
                      single.Generation.Prompt.Contains("Source text lines: 1 ", StringComparison.Ordinal),
                    "empty and unterminated Unicode targets each retain exactly one numbered text line");
            }

            var denseText = new string('\n', 500);
            await File.WriteAllTextAsync(mixedPath, denseText, new UTF8Encoding(false));
            var dense = await Call(new { path = mixedPath, expectedSha256 = Hash(await File.ReadAllBytesAsync(mixedPath)),
                instructions = "Preserve the current source." });
            var densePrompt = dense.Generation!.Prompt;
            var headerStart = densePrompt.IndexOf("Source text lines:", StringComparison.Ordinal);
            var headerEnd = densePrompt.IndexOf("Source begins on the next line:", headerStart, StringComparison.Ordinal);
            var metadataBytes = Encoding.UTF8.GetByteCount(densePrompt[headerStart..headerEnd]) +
                Encoding.UTF8.GetByteCount(TargetEvidence(densePrompt)) - Encoding.UTF8.GetByteCount(denseText);
            Check(dense.Success && densePrompt.Contains("Source text lines: 501 ", StringComparison.Ordinal) &&
                  !densePrompt.Contains("Line numbering omitted:", StringComparison.Ordinal) && metadataBytes <= 4 * 1024,
                "all count/format metadata and every target prefix stay within the 4 KiB optional overhead allowance");

            var rawText = string.Concat(Enumerable.Repeat("x\r\n", 1200));
            await File.WriteAllTextAsync(mixedPath, rawText, new UTF8Encoding(false));
            var rawHash = Hash(await File.ReadAllBytesAsync(mixedPath));
            var raw = await Call(new { path = mixedPath, expectedSha256 = rawHash,
                instructions = "Preserve the current source.", referencePaths = new[] { referencePath } });
            Check(raw.Success && raw.Generation is not null && TargetEvidence(raw.Generation.Prompt) == rawText &&
                  raw.Generation.Prompt.Contains("Line numbering omitted:", StringComparison.Ordinal) &&
                  raw.Generation.Prompt.Contains("Source text lines: 1201 ", StringComparison.Ordinal) &&
                  raw.Generation.Prompt.Contains(referenceText, StringComparison.Ordinal) &&
                  Encoding.UTF8.GetByteCount(raw.Generation.Prompt) <= 96 * 1024,
                "coordinate overhead degrades to an explicitly labeled complete raw target rather than truncating real source or references");
            Check(raw.Generation!.SinkArguments.GetProperty("expectedSha256").GetString() == rawHash &&
                  (await File.ReadAllBytesAsync(mixedPath)).SequenceEqual(Encoding.UTF8.GetBytes(rawText)),
                "raw fallback retains the original preimage and does not execute a source mutation");
            VerifyPromptBoundary(Check);

            var relative = await Call(new
            {
                path = "Target.cs", expectedSha256 = sourceHash,
                instructions = "Preserve the current source and return one complete replacement.",
                referencePaths = new[] { "Contract.cs" }
            });
            Check(relative.Success && relative.Generation!.SinkArguments.GetProperty("path").GetString() == Path.GetFullPath(sourcePath),
                "relative inputs use the same explicit working-directory resolution as file tools");

            var stale = await Call(new
            {
                path = sourcePath, expectedSha256 = new string('0', 64), instructions = "Replace the source."
            });
            Check(!stale.Success && stale.Generation is null && stale.Content.Contains("preimage changed", StringComparison.OrdinalIgnoreCase) &&
                  await File.ReadAllTextAsync(sourcePath) == sourceText,
                "stale target hash is actionable and cannot produce a plan or mutate source");

            var escaped = await Call(new
            {
                path = outside, expectedSha256 = Hash(await File.ReadAllBytesAsync(outside)), instructions = "Replace the source."
            });
            Check(!escaped.Success && escaped.Content.Contains("outside the profile's allowed roots", StringComparison.Ordinal) &&
                  await File.ReadAllTextAsync(outside) == "internal static class Outside { }\n",
                "out-of-root source is rejected without disclosing or changing its contents");
            var traversal = await Call(new
            {
                path = Path.Combine(root, "folder", "..", "Target.cs"), expectedSha256 = sourceHash, instructions = "Replace the source."
            });
            Check(!traversal.Success && traversal.Content.Contains("Parent traversal", StringComparison.Ordinal),
                "parent traversal retains the existing file-tool rejection");

            var writeOnly = context with
            {
                Profile = new AgentProfile
                {
                    Id = "write-only", AllowedRoots = [root], AllowedTools = ["program.source.generate", "file.write"],
                    Capabilities = ["filesystem.write"]
                }
            };
            var missingRead = await Call(new { path = sourcePath, expectedSha256 = sourceHash, instructions = "Replace." }, writeOnly);
            Check(!missingRead.Success && missingRead.Content.Contains("both filesystem.read and filesystem.write", StringComparison.Ordinal),
                "producer requires the existing read capability in addition to its catalog write capability");
            var missingSink = context with
            {
                Profile = new AgentProfile
                {
                    Id = "no-sink", AllowedRoots = [root], AllowedTools = ["program.source.generate"],
                    Capabilities = ["filesystem.read", "filesystem.write"]
                }
            };
            var deniedSink = await Call(new { path = sourcePath, expectedSha256 = sourceHash, instructions = "Replace." }, missingSink);
            Check(!deniedSink.Success && deniedSink.Content.Contains("exact file.write sink tool grant", StringComparison.Ordinal),
                "producer refuses a plan when the validated sink is not explicitly granted");

            var oversizedInstructions = await Call(new
            {
                path = sourcePath, expectedSha256 = sourceHash, instructions = new string('é', 5000)
            });
            Check(!oversizedInstructions.Success && oversizedInstructions.Content.Contains("8,192-byte UTF-8", StringComparison.Ordinal),
                "runtime enforces the UTF-8 instruction bound beyond schema character counting");

            var largeSourcePath = Path.Combine(root, "Large.cs");
            await File.WriteAllTextAsync(largeSourcePath, new string('s', 32 * 1024 + 1), new UTF8Encoding(false));
            var largeSource = await Call(new
            {
                path = largeSourcePath, expectedSha256 = Hash(await File.ReadAllBytesAsync(largeSourcePath)), instructions = "Replace."
            });
            Check(!largeSource.Success && largeSource.Content.Contains("32,768-byte UTF-8 limit", StringComparison.Ordinal),
                "target source exceeding 32 KiB is rejected before plan creation");

            var referenceOne = Path.Combine(root, "ReferenceOne.cs");
            var referenceTwo = Path.Combine(root, "ReferenceTwo.cs");
            await File.WriteAllTextAsync(referenceOne, new string('a', 20 * 1024), new UTF8Encoding(false));
            await File.WriteAllTextAsync(referenceTwo, new string('b', 20 * 1024), new UTF8Encoding(false));
            var largeReferences = await Call(new
            {
                path = sourcePath, expectedSha256 = sourceHash, instructions = "Replace.",
                referencePaths = new[] { referenceOne, referenceTwo }
            });
            Check(!largeReferences.Success && largeReferences.Content.Contains("32 KiB aggregate", StringComparison.Ordinal),
                "reference files exceeding 32 KiB in aggregate are rejected with a recovery hint");
            var targetAsReference = await Call(new
            {
                path = sourcePath, expectedSha256 = sourceHash, instructions = "Replace.", referencePaths = new[] { sourcePath }
            });
            Check(!targetAsReference.Success && targetAsReference.Content.Contains("must not also be supplied", StringComparison.Ordinal),
                "target cannot be duplicated into reference context");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancellationObserved = false;
            try
            {
                await Call(new { path = sourcePath, expectedSha256 = sourceHash, instructions = "Replace." }, token: cancelled.Token);
            }
            catch (OperationCanceledException) { cancellationObserved = true; }
            Check(cancellationObserved, "cancellation remains observable before producer filesystem work");
            Check(await File.ReadAllTextAsync(sourcePath) == sourceText,
                "all producer success and rejection fixtures preserve the target source bytes");

            try
            {
                var linked = Path.Combine(root, "Linked.cs");
                File.CreateSymbolicLink(linked, outside);
                var linkedResult = await Call(new
                {
                    path = linked, expectedSha256 = Hash(await File.ReadAllBytesAsync(outside)), instructions = "Replace."
                });
                Check(!linkedResult.Success && linkedResult.Content.Contains("symbolic links", StringComparison.Ordinal),
                    "producer shares the file-tool reparse-point rejection");
                File.Delete(linked);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Console.WriteLine("SKIP: program.source.generate symlink fixture unavailable: " + error.GetType().Name);
            }
            return checks;
        }
        finally
        {
            if (File.Exists(outside)) File.Delete(outside);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string TargetEvidence(string prompt)
    {
        const string begin = "Source begins on the next line:";
        var start = prompt.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("The target source evidence has no explicit beginning.");
        start += begin.Length + Environment.NewLine.Length;
        var end = prompt.IndexOf(Environment.NewLine + "End target source evidence.", start, StringComparison.Ordinal);
        if (end < start) throw new InvalidOperationException("The target source evidence has no explicit end.");
        return prompt[start..end];
    }

    private static void VerifyPromptBoundary(Action<bool, string> check)
    {
        // Synthetic formatter-only pressure: real tool admission above still enforces the
        // 8 KiB objective and scoped reads. No oversized objective reaches a model or sink.
        var type = typeof(ProgrammingServicePlugin).Assembly.GetType("Cerebral.Programming.ProgrammingSourceGeneration", throwOnError: true)!;
        var formatter = type.GetMethod("PreparePrompt", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var referenceType = type.GetNestedType("SourceReference", System.Reflection.BindingFlags.NonPublic)!;
        var references = Array.CreateInstance(referenceType, 0);
        var text = string.Concat(Enumerable.Repeat("x\n", 100));
        var bytes = Encoding.UTF8.GetBytes(text);
        var source = new ScopedTextFile("Target.cs", text, Hash(bytes), bytes.Length, bytes.Length);
        string Format(string instructions) => (string)formatter.Invoke(null, [instructions, source, references])!;
        var small = Format("x");
        var instructions = new string('i', 96 * 1024 - Encoding.UTF8.GetByteCount(small) + 1 + 128);
        var fallback = Format(instructions);
        check(fallback.Contains("Line numbering omitted:", StringComparison.Ordinal) &&
              TargetEvidence(fallback) == text && Encoding.UTF8.GetByteCount(fallback) <= 96 * 1024,
            "whole-prompt pressure drops only optional numbering and preserves every target byte under the existing 96 KiB limit");
        var rejected = false;
        try { _ = Format(instructions + new string('i', 4096)); }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidDataException)
        { rejected = true; }
        check(rejected,
            "an oversized required raw prompt still fails closed rather than clipping source or expanding the prompt bound");
    }

    private static void VerifyServicePackageLoading(string root, Action<bool, string> check)
    {
        var projectRoot = ProjectRoot();
        var sourceManifest = Path.Combine(projectRoot, "src", "Cerebral.Programming", ServicePluginRuntime.ManifestFileName);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(sourceManifest));
        var manifest = manifestDocument.RootElement;
        check(manifest.GetProperty("id").GetString() == "pegasus.programming" &&
              manifest.GetProperty("version").GetString() == "1.6.12" &&
              manifest.GetProperty("assembly").GetString() == Path.GetFileName(typeof(ProgrammingServicePlugin).Assembly.Location),
            "current service manifest truthfully identifies the Programming package and public capability version");

        var package = FindBundledProgrammingPackage(projectRoot);
        var repository = Directory.GetParent(package)!.FullName;
        check(Hash(File.ReadAllBytes(Path.Combine(package, "Cerebral.Programming.dll"))) ==
              Hash(File.ReadAllBytes(typeof(ProgrammingServicePlugin).Assembly.Location)) &&
              File.ReadAllText(Path.Combine(package, ServicePluginRuntime.ManifestFileName)) == File.ReadAllText(sourceManifest),
            "Worker build copied the current Programming assembly and manifest into its bundled service repository");
        using var runtime = new ServicePluginRuntime(repository, Path.Combine(root, "ServiceData"), new EventHub());
        var status = runtime.Status.Single(item => item.Id == "pegasus.programming");
        check(status.Loaded && status.Version == "1.6.12" && status.Tools.Contains("program.source.generate", StringComparer.Ordinal),
            "real service-package loading publishes program.source.generate from the current manifest and assembly");
        var tools = runtime.ToolExtensions.SelectMany(extension => extension.Tools)
            .Where(tool => tool.Name == "program.source.generate").ToArray();
        check(tools.Length == 1 && tools[0].Capability == "filesystem.write" &&
              tools[0].PrerequisiteTools.SequenceEqual(new[] { "file.read", "file.write" }),
            "loaded service package retains the producer capability and prerequisite schemas");
    }

    private static string FindBundledProgrammingPackage(string projectRoot)
    {
        var loadedHash = Hash(File.ReadAllBytes(typeof(ProgrammingServicePlugin).Assembly.Location));
        static IEnumerable<string> ManifestsBelow(string root) => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, ServicePluginRuntime.ManifestFileName, SearchOption.AllDirectories)
                .Where(path => string.Equals(Directory.GetParent(path)?.Name, "pegasus.programming", StringComparison.Ordinal))
                .Take(32)
            : [];

        var roots = new List<string>();
        for (var cursor = new DirectoryInfo(AppContext.BaseDirectory); cursor is not null; cursor = cursor.Parent)
            roots.Add(Path.Combine(cursor.FullName, "Cerebral.Worker"));
        roots.Add(Path.Combine(projectRoot, "src", "Cerebral.Worker", "bin"));
        foreach (var manifestPath in roots.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(ManifestsBelow))
        {
            var package = Directory.GetParent(manifestPath)!.FullName;
            var assembly = Path.Combine(package, "Cerebral.Programming.dll");
            if (!File.Exists(assembly) || Hash(File.ReadAllBytes(assembly)) != loadedHash) continue;
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (manifest.RootElement.GetProperty("version").GetString() == "1.6.12") return package;
        }
        throw new FileNotFoundException("The current Worker build does not contain the current Pegasus Programming 1.6.12 package.");
    }

    private static string ProjectRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var cursor = new DirectoryInfo(start); cursor is not null; cursor = cursor.Parent)
                if (File.Exists(Path.Combine(cursor.FullName, "Pegasus.Cerebral.slnx"))) return cursor.FullName;
        }
        throw new FileNotFoundException("Pegasus.Cerebral.slnx was not found above the test process.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
