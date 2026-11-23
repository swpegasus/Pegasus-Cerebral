using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cerebral.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cerebral.Programming;

/// <summary>Syntax evidence only. The file editor and compiler retain their own admission and proof.</summary>
internal static partial class ProgrammingSourceStructure
{
    internal const string Name = "program.source.structure";
    private const int MaximumSourceBytes = 32 * 1024;
    private const int MaximumOutputBytes = 4096;
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static void ValidateArguments(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Structure arguments must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not ("path" or "expectedSha256" or "focusLine" or "proposedEdit"))
                throw new ArgumentException("Structure arguments contain duplicate or unexpected fields.");
        if (!args.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(path.GetString()) || Encoding.UTF8.GetByteCount(path.GetString()!) > 1024 ||
            !string.Equals(Path.GetExtension(path.GetString()), ".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Structure inspection requires one .cs path of at most 1024 UTF-8 bytes.");
        if (!args.TryGetProperty("expectedSha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
            hash.GetString() is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit))
            throw new ArgumentException("Structure inspection requires the current whole-file expectedSha256 from file.read.");
        if (args.TryGetProperty("focusLine", out var focus) &&
            (focus.ValueKind != JsonValueKind.Number || !focus.TryGetInt32(out var line) || line is < 1 or > 1_000_000))
            throw new ArgumentException("focusLine must be a positive one-based physical line at most 1000000.");
        if (args.TryGetProperty("proposedEdit", out var proposal)) _ = ProgrammingEditProposal.Read(proposal);
    }

    internal static async Task<ToolResult> InspectAsync(JsonElement args, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ValidateArguments(args);
        if (!context.Profile.Capabilities.Contains("filesystem.read", StringComparer.Ordinal) ||
            !context.Profile.AllowedTools.Any(name => name is "*" or Name))
            throw new UnauthorizedAccessException("Source structure inspection requires its tool grant and filesystem.read capability.");
        cancellationToken.ThrowIfCancellationRequested();
        var source = await ScopedFilePolicy.ReadTextAsync(args.GetProperty("path").GetString()!, context,
            MaximumSourceBytes, cancellationToken);
        if (!source.Sha256.Equals(args.GetProperty("expectedSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The source preimage changed. Read the current file and use its new SHA-256 before structure inspection.");
        var focusLine = args.TryGetProperty("focusLine", out var focus) ? focus.GetInt32() : 1;
        var proposal = args.TryGetProperty("proposedEdit", out var draft) ? ProgrammingEditProposal.Read(draft) : null;
        var content = Describe(source, focusLine, proposal, cancellationToken);
        return new ToolResult(true, content)
        {
            Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.programming.structure",
                context.EffectId, State: "inspected", Partial: false)
        };
    }

    private static string Describe(ScopedTextFile source, int focusLine, ProgrammingEditProposal? proposal, CancellationToken cancellationToken)
    {
        var physical = PhysicalLines(source.Text);
        // The terminal empty read segment is a position, never another editable physical line.
        if (focusLine > Math.Max(1, physical.Count))
            throw new ArgumentException("focusLine is outside the current file's physical lines.");
        var options = new CSharpParseOptions(LanguageVersion.CSharp14, DocumentationMode.Parse, SourceCodeKind.Regular);
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source.Text, Encoding.UTF8), options,
            source.Path, cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var diagnostics = tree.GetDiagnostics(cancellationToken).ToArray();
        var errors = diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error);
        var missing = root.DescendantTokens(descendIntoTrivia: true).Any(token => token.IsMissing);
        var recovered = errors != 0 || missing || root.ContainsSkippedText;
        var conditional = root.DescendantTrivia(descendIntoTrivia: true)
            .Any(trivia => trivia.IsKind(SyntaxKind.IfDirectiveTrivia));
        // Re-encoding and matching the WHOLE raw-byte hash proves UTF-8/no-BOM without a second,
        // racing file read. StoredBytes alone cannot identify the original encoding.
        var utf8 = Encoding.UTF8.GetBytes(source.Text);
        var rangeFormat = !source.Text.Contains('\r') &&
            Convert.ToHexString(SHA256.HashData(utf8)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase);
        var proposedEdit = proposal is null ? null : DescribeProposal(source, physical, root, options, proposal, cancellationToken);
        var nodes = root.DescendantNodes().Where(IsScope).ToArray();
        var ids = nodes.Select((node, index) => (node, id: "n" + index)).ToDictionary(item => item.node, item => item.id);
        var position = physical.Count == 0 ? 0 : physical[focusLine - 1].Start;
        if (physical.Count != 0)
            while (position < physical[focusLine - 1].End && char.IsWhiteSpace(source.Text[position])) position++;
        var scopes = nodes.Where(node => node.Span.Start <= position && position < node.Span.End).ToArray();
        var focusIds = scopes.Select(node => ids[node]).ToHashSet(StringComparer.Ordinal);
        // Inner scopes first survive the budget before a distant sibling. Omitted ancestors and
        // declarations are counted; absence from this bounded page is not proof of nonexistence.
        var priority = scopes.Reverse().Concat(nodes.Where(node => node is MemberDeclarationSyntax && !focusIds.Contains(ids[node]))
            .OrderBy(node => Math.Abs((long)node.SpanStart - position))).Distinct().ToArray();
        var views = priority.Take(24).Select(node => View(node, ids, source.Text, physical, recovered, conditional, rangeFormat)).ToList();
        var diagnosticViews = diagnostics.Take(4).Select(item => new
        {
            id = item.Id, severity = item.Severity.ToString(),
            at = Position(item.Location.SourceSpan.Start, physical),
            message = Bound(item.GetMessage(System.Globalization.CultureInfo.InvariantCulture), 120)
        }).ToArray();
        string Render() => JsonSerializer.Serialize(new
        {
            schema = "program.source.structure/1", path = source.Path, sha256 = source.Sha256,
            parser = new { name = "Roslyn.CSharp", version = typeof(CSharpSyntaxTree).Assembly.GetName().Version!.ToString(),
                language = "CSharp14", kind = "Regular", documentation = "Parse", symbols = Array.Empty<string>() },
            positions = "Physical lines and UTF-16 columns are 1-based; end is exclusive. No #line mapping.",
            limits = "Syntax only; no semantic, build, source-read or edit credit. Missing declarations cannot be inferred from omitted nodes. Draft boundaries describe the preimage and lexical fragment only; reconsider the exact proposal before resubmitting. Reinspect after edits.",
            physicalLines = physical.Count, focusLine, rangeEditFormat = rangeFormat,
            recovered, conditionalCompilation = conditional, syntaxErrors = errors, proposedEdit,
            diagnostics = diagnosticViews, omittedDiagnostics = diagnostics.Length - diagnosticViews.Length,
            focusScopeIds = scopes.Where(node => views.Any(view => view.Id == ids[node])).Select(node => ids[node]),
            omittedFocusScopes = scopes.Count(node => views.All(view => view.Id != ids[node])),
            omittedNodes = nodes.Count(node => node is MemberDeclarationSyntax || focusIds.Contains(ids[node])) - views.Count,
            nodes = views.OrderBy(view => view.Start.Line).ThenBy(view => view.Start.Column)
        }, Json);
        var content = Render();
        while (Encoding.UTF8.GetByteCount(content) > MaximumOutputBytes && views.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            views.RemoveAt(views.Count - 1);
            content = Render();
        }
        if (Encoding.UTF8.GetByteCount(content) > MaximumOutputBytes)
            throw new InvalidDataException("Structure metadata exceeds 4 KiB. Use a shorter scoped path.");
        return content;
    }

    private static NodeView View(SyntaxNode node, Dictionary<SyntaxNode, string> ids, string text,
        List<PhysicalLine> lines, bool recovered, bool conditional, bool rangeFormat)
    {
        var parent = node.Parent;
        while (parent is not null && !ids.ContainsKey(parent)) parent = parent.Parent;
        var body = Body(node);
        var headerEnd = body.Open.RawKind != 0 ? body.Open.SpanStart : node switch
        {
            FileScopedNamespaceDeclarationSyntax space => space.SemicolonToken.SpanStart,
            BaseMethodDeclarationSyntax method when method.ExpressionBody is { } arrow => arrow.ArrowToken.SpanStart,
            LocalFunctionStatementSyntax method when method.ExpressionBody is { } arrow => arrow.ArrowToken.SpanStart,
            PropertyDeclarationSyntax property when property.ExpressionBody is { } arrow => arrow.ArrowToken.SpanStart,
            IndexerDeclarationSyntax indexer when indexer.ExpressionBody is { } arrow => arrow.ArrowToken.SpanStart,
            _ => node.Span.End
        };
        var header = Bound(string.Join(" ", text[node.SpanStart..Math.Min(headerEnd, node.Span.End)]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), 120);
        var start = Position(node.SpanStart, lines);
        var end = Position(node.Span.End, lines);
        var reason = node is not MemberDeclarationSyntax || node is BaseNamespaceDeclarationSyntax ? "scope-only" :
            recovered ? "recovered-file" : conditional ? "conditional-options-unknown" :
            !rangeFormat ? "requires-utf8-no-bom-lf" : node.ContainsDirectives ? "contains-directives" : "";
        LineRange? edit = null;
        if (reason.Length == 0)
        {
            var last = Position(Math.Max(node.SpanStart, node.Span.End - 1), lines).Line;
            var count = last - start.Line + 1;
            var prefix = text[lines[start.Line - 1].Start..node.SpanStart];
            var suffix = text[node.Span.End..lines[last - 1].End];
            if (prefix.Any(ch => !char.IsWhiteSpace(ch)) || suffix.Any(ch => !char.IsWhiteSpace(ch)))
                reason = "shares-line-with-other-text";
            else if (count > 512) reason = "exceeds-editor-512-lines";
            else edit = new(start.Line, count);
        }
        return new(ids[node], parent is null ? null : ids[parent], node.Kind().ToString(), header, start, end,
            body.Open.RawKind == 0 || body.Open.IsMissing ? null : Position(body.Open.SpanStart, lines),
            body.Close.RawKind == 0 || body.Close.IsMissing ? null : Position(body.Close.SpanStart, lines),
            edit, edit is null ? reason : "isolated-node-span-excludes-trivia");
    }

    private static (SyntaxToken Open, SyntaxToken Close) Body(SyntaxNode node) => node switch
    {
        TypeDeclarationSyntax type => (type.OpenBraceToken, type.CloseBraceToken),
        EnumDeclarationSyntax type => (type.OpenBraceToken, type.CloseBraceToken),
        NamespaceDeclarationSyntax space => (space.OpenBraceToken, space.CloseBraceToken),
        BaseMethodDeclarationSyntax method when method.Body is { } block => (block.OpenBraceToken, block.CloseBraceToken),
        LocalFunctionStatementSyntax method when method.Body is { } block => (block.OpenBraceToken, block.CloseBraceToken),
        AccessorDeclarationSyntax accessor when accessor.Body is { } block => (block.OpenBraceToken, block.CloseBraceToken),
        BasePropertyDeclarationSyntax property when property.AccessorList is { } accessors => (accessors.OpenBraceToken, accessors.CloseBraceToken),
        WhileStatementSyntax loop when loop.Statement is BlockSyntax block => (block.OpenBraceToken, block.CloseBraceToken),
        ForStatementSyntax loop when loop.Statement is BlockSyntax block => (block.OpenBraceToken, block.CloseBraceToken),
        ForEachStatementSyntax loop when loop.Statement is BlockSyntax block => (block.OpenBraceToken, block.CloseBraceToken),
        IfStatementSyntax condition when condition.Statement is BlockSyntax block => (block.OpenBraceToken, block.CloseBraceToken),
        DoStatementSyntax loop when loop.Statement is BlockSyntax block => (block.OpenBraceToken, block.CloseBraceToken),
        UsingStatementSyntax statement when statement.Statement is BlockSyntax block => (block.OpenBraceToken, block.CloseBraceToken),
        LockStatementSyntax statement when statement.Statement is BlockSyntax block => (block.OpenBraceToken, block.CloseBraceToken),
        TryStatementSyntax statement => (statement.Block.OpenBraceToken, statement.Block.CloseBraceToken),
        CatchClauseSyntax statement => (statement.Block.OpenBraceToken, statement.Block.CloseBraceToken),
        SwitchStatementSyntax statement => (statement.OpenBraceToken, statement.CloseBraceToken),
        BlockSyntax block => (block.OpenBraceToken, block.CloseBraceToken),
        _ => default
    };

    private static bool IsScope(SyntaxNode node) => node is MemberDeclarationSyntax or LocalFunctionStatementSyntax or
        BlockSyntax or AccessorDeclarationSyntax or AnonymousFunctionExpressionSyntax or
        ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or
        IfStatementSyntax or SwitchStatementSyntax or TryStatementSyntax or CatchClauseSyntax or UsingStatementSyntax or LockStatementSyntax;

    private static List<PhysicalLine> PhysicalLines(string text)
    {
        var lines = new List<PhysicalLine>();
        for (var start = 0; start < text.Length;)
        {
            var end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
            var next = end;
            if (next < text.Length && text[next++] == '\r' && next < text.Length && text[next] == '\n') next++;
            lines.Add(new(start, end, next));
            start = next;
        }
        return lines;
    }

    private static SourcePosition Position(int offset, List<PhysicalLine> lines)
    {
        if (lines.Count == 0) return new(1, 1);
        var low = 0; var high = lines.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (lines[middle].Start <= offset) low = middle; else high = middle - 1;
        }
        if (offset == lines[^1].Next && lines[^1].Next > lines[^1].End) return new(lines.Count + 1, 1);
        return new(low + 1, offset - lines[low].Start + 1);
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum)] + "…";

    private sealed record PhysicalLine(int Start, int End, int Next);
    private sealed record SourcePosition(int Line, int Column);
    private sealed record LineRange(int StartLine, int LineCount);
    private sealed record NodeView(string Id, string? ParentId, string Kind, string Header, SourcePosition Start,
        SourcePosition End, SourcePosition? OpenBrace, SourcePosition? CloseBrace, LineRange? EditRange, string RangeStatus);
}
