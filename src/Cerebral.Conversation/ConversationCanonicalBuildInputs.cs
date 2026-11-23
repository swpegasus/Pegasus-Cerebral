using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    // Input coordinates are derived from the already bounded same-run receipt ledger.
    // There is no separate file-count/project-size cap or recursive filesystem scan.
    private sealed record CanonicalInputFile(string Path, string BaselineSha256, KnownFileRevision? Current);
    private sealed record CanonicalInputs(string RecipeSha256, string AnchorSha256, int AnchorReceiptCount,
        IReadOnlyList<CanonicalInputFile> Files);
    private sealed record LiteralBuildInputs(IReadOnlyList<string> ReferenceRoots, IReadOnlyList<string> Files);
    private static string EvidenceWorkspaceRoot(Input input, PluginBuildCandidate build) =>
        input.WorkingDirectory is { } directory && TryCanonicalAbsolutePath(directory, out var root) && PathWithin(build.Root, root)
            ? root : build.Root;
    private static bool AncestorBuildInput(string path, PluginBuildCandidate build, string scopeRoot) =>
        (Path.GetFileName(path).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)) &&
        PathWithin(path, scopeRoot) && Path.GetDirectoryName(path) is { } parent && PathWithin(build.ProjectFile, parent);
    private static string InputPathKey(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    private static bool ComponentInputPath(PluginBuildCandidate build, string path)
    {
        if (PathEquals(path, build.ProjectFile)) return true;
        if (!PathWithin(path, build.WorkingDirectory)) return false;
        return !Path.GetRelativePath(build.WorkingDirectory, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part =>
                part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }
    private static bool RelatedCanonicalInput(Input input, HarnessState state, PluginBuildCandidate build, string path)
    {
        if (ComponentInputPath(build, path) || AncestorBuildInput(path, build, EvidenceWorkspaceRoot(input, build))) return true;
        var recipe = BuildRecipeSha256(build);
        var project = state.ToolReceipts.LastOrDefault(receipt => receipt.CanonicalProof is { } proof &&
            proof.InputRecipeSha256 == recipe && proof.SourcePath is { } source && PathEquals(source, build.ProjectFile));
        // A later project write invalidates old reference evidence until a complete project read.
        var currentHash = project?.CanonicalProof?.SourceRevision?.Sha256;
        var declaration = currentHash is null ? null : state.ToolReceipts.LastOrDefault(receipt =>
            receipt.CanonicalProof is { ProjectReferenceRoots: not null } proof && proof.InputRecipeSha256 == recipe &&
            proof.SourceRevision?.Sha256 == currentHash && proof.SourcePath is { } source && PathEquals(source, build.ProjectFile));
        return declaration?.CanonicalProof?.ProjectReferenceRoots is { } roots && roots.Any(root => PathWithin(path, root)) ||
            declaration?.CanonicalProof?.LinkedInputPaths is { } linked && linked.Any(file => PathEquals(file, path));
    }
    private static LiteralBuildInputs? ReadLiteralBuildInputs(PluginBuildCandidate build, ToolData data, string scopeRoot)
    {
        try
        {
            using var body = JsonDocument.Parse(data.Content, new JsonDocumentOptions { MaxDepth = 8 });
            var value = body.RootElement;
            if (data.Truncated || !value.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("startLine", out var start) || !start.TryGetInt32(out var line) || line != 1 ||
                !value.TryGetProperty("totalLines", out var total) || !total.TryGetInt32(out var lines)) return null;
            var text = content.GetString()!;
            if (lines <= 0 || text.Count(character => character == '\n') + 1 < lines) return null;
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumBindingPayloadBytes, MaxCharactersFromEntities = 0
            });
            var document = XDocument.Load(reader);
            var roots = new List<string>();
            var files = new List<string>();
            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = (string?)reference.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include) || include.IndexOfAny(['$', '*', '?']) >= 0 ||
                    !TryResolvePath(include, Path.GetDirectoryName(build.ProjectFile), out var path) ||
                    !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !PathWithin(path, scopeRoot)) continue;
                var root = Path.GetDirectoryName(path)!;
                if (!roots.Any(existing => PathEquals(root, existing))) roots.Add(root);
            }
            foreach (var compile in document.Descendants().Where(element => element.Name.LocalName == "Compile"))
            {
                var include = (string?)compile.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include) || include.IndexOfAny(['$', '*', '?', ';']) >= 0 ||
                    !TryResolvePath(include, Path.GetDirectoryName(build.ProjectFile), out var path) || !PathWithin(path, scopeRoot)) continue;
                if (!files.Any(existing => PathEquals(path, existing))) files.Add(path);
            }
            return new(roots.ToArray(), files.ToArray());
        }
        catch (Exception error) when (error is JsonException or XmlException or InvalidOperationException or ArgumentException) { return null; }
    }
    private static bool InputFileReceipt(ToolReceipt receipt) => receipt.Name is
        "file.read" or "file.inspect" or "file.write" or "file.edit" or "file.append" or "file.copy";
    private static Dictionary<string, KnownFileRevision?> InputSnapshot(HarnessState state, PluginBuildCandidate build, int count)
    {
        var files = new Dictionary<string, KnownFileRevision?>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var receipt = state.ToolReceipts[index];
            if (receipt.Outcome == "Rejected") continue;
            var proof = receipt.CanonicalProof;
            if (receipt.Name is "process.run" or "git.run" && proof?.RecipeSha256 is null ||
                receipt.Name is "file.write" or "file.edit" or "file.append" or "file.copy" && proof?.SourcePath is null)
            {
                foreach (var key in files.Keys.ToArray()) files[key] = null;
                continue;
            }
            if (InputFileReceipt(receipt) && proof?.SourcePath is { } path &&
                (PathWithin(path, build.Root) || proof.InputRecipeSha256 == BuildRecipeSha256(build) ||
                 proof.ScopeRoot is { } scope && PathWithin(build.Root, scope) && PathWithin(path, scope)))
                files[InputPathKey(path)] = proof.SourceRevision;
        }
        return files;
    }
    private static LiteralBuildInputs ReferenceRootsAt(HarnessState state, PluginBuildCandidate build,
        Dictionary<string, KnownFileRevision?> files, int count)
    {
        if (!files.TryGetValue(InputPathKey(build.ProjectFile), out var current) || current is null) return new([], []);
        var declaration = state.ToolReceipts.Take(count).LastOrDefault(receipt => receipt.CanonicalProof is
            { ProjectReferenceRoots: not null, SourceRevision: { } revision } proof &&
            proof.SourcePath is { } path && PathEquals(path, build.ProjectFile) && revision.Sha256 == current.Sha256);
        return new(declaration?.CanonicalProof?.ProjectReferenceRoots ?? [], declaration?.CanonicalProof?.LinkedInputPaths ?? []);
    }
    private static CanonicalInputs ReadCanonicalInputs(HarnessState state, PluginBuildCandidate build)
    {
        var recipe = BuildRecipeSha256(build);
        var first = state.ToolReceipts.FirstOrDefault(receipt => receipt.CanonicalProof is
            { InputAnchorSha256: not null, RecipeSha256: { } hash } && hash == recipe)?.CanonicalProof;
        var boundary = first?.InputAnchorReceiptCount ?? state.ToolReceipts.Count;
        var before = InputSnapshot(state, build, boundary);
        var beforeRoots = ReferenceRootsAt(state, build, before, boundary);
        bool Related(string path, LiteralBuildInputs declared) => ComponentInputPath(build, path) ||
            declared.ReferenceRoots.Any(root => PathWithin(path, root)) || declared.Files.Any(file => PathEquals(file, path)) ||
            state.ToolReceipts.Any(receipt => receipt.CanonicalProof is { ScopeRoot: { } scope, SourcePath: { } observed } &&
                PathEquals(observed, path) && AncestorBuildInput(path, build, scope));
        var anchor = AdmissionArgumentHash(Element(new
        {
            recipe, files = before.Where(pair => Related(pair.Key, beforeRoots)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { path = pair.Key, sha256 = pair.Value?.Sha256 }).ToArray()
        }));
        if (first?.InputAnchorSha256 is { } expected && expected != anchor)
            throw new ArgumentException("The canonical-input anchor does not match its original receipt prefix.");
        var after = InputSnapshot(state, build, state.ToolReceipts.Count);
        var currentRoots = ReferenceRootsAt(state, build, after, state.ToolReceipts.Count);
        var files = new List<CanonicalInputFile>();
        foreach (var pair in after.Where(pair => Related(pair.Key, currentRoots)))
        {
            string? baseline = before.TryGetValue(pair.Key, out var old) ? old?.Sha256 : null;
            if (baseline is null)
                foreach (var receipt in state.ToolReceipts.Skip(boundary))
                    if (receipt.Outcome != "Rejected" && InputFileReceipt(receipt) && receipt.CanonicalProof is { SourcePath: { } path } proof &&
                        InputPathKey(path) == pair.Key)
                    {
                        baseline = proof.BeforeSha256 ?? (proof.Created ? "absent" : proof.SourceRevision?.Sha256);
                        if (baseline is not null) break;
                    }
            files.Add(new(pair.Value?.Path ?? pair.Key, baseline ?? "unproved", pair.Value));
        }
        return new(recipe, anchor, boundary, files.ToArray());
    }
    private static string CanonicalInputIdentity(CanonicalInputs inputs) => AdmissionArgumentHash(Element(new
    {
        inputs.AnchorSha256,
        changes = inputs.Files.Where(file => file.Current is not null && file.Current.Sha256 != file.BaselineSha256)
            .OrderBy(file => InputPathKey(file.Path), StringComparer.Ordinal)
            .Select(file => new { path = InputPathKey(file.Path), sha256 = file.Current!.Sha256 }).ToArray()
    }));
    private static KnownFileRevision? CanonicalPrimarySource(CanonicalInputs inputs, string? path) =>
        path is null ? null : inputs.Files.FirstOrDefault(file => PathEquals(file.Path, path))?.Current;
}
