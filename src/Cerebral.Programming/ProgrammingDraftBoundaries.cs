using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Programming;

internal static partial class ProgrammingSourceStructure
{
    private static ProposedEditView DescribeProposal(ScopedTextFile source, List<PhysicalLine> lines,
        SyntaxNode root, CSharpParseOptions options, ProgrammingEditProposal proposal, CancellationToken cancellationToken)
    {
        var last = (long)proposal.StartLine + proposal.LineCount - 1;
        if (last > lines.Count)
            throw new ArgumentException("proposedEdit exceeds the current file's physical lines.");
        var start = lines[proposal.StartLine - 1].Start;
        var end = lines[(int)last - 1].Next;
        // Lexing preserves unmatched closing tokens which a fragment compilation-unit parser
        // can move into skipped trivia. Neither stream applies or parses a candidate edit.
        var current = Tokens(source.Text, options, start, end, cancellationToken);
        var replacement = Tokens(proposal.NewText, options, 0, proposal.NewText.Length, cancellationToken);
        return new(proposal.Fingerprint(source.Path, source.Sha256),
            Boundary(current.First, root, start, end, lines, cancellationToken),
            Boundary(current.Last, root, start, end, lines, cancellationToken),
            new(replacement.First?.Kind().ToString(), replacement.Last?.Kind().ToString(),
                Encoding.UTF8.GetByteCount(proposal.NewText), proposal.NewText.Count(ch => ch == '\n'),
                proposal.NewText.Count(ch => ch == '\r'), proposal.NewText.EndsWith('\n')));
    }

    private static (SyntaxToken? First, SyntaxToken? Last) Tokens(string text, CSharpParseOptions options,
        int start, int end, CancellationToken cancellationToken)
    {
        SyntaxToken? first = null, last = null;
        foreach (var token in SyntaxFactory.ParseTokens(text, options: options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.SpanStart >= end) break;
            if (token.IsKind(SyntaxKind.EndOfFileToken) || token.Span.End <= start || token.Span.IsEmpty) continue;
            first ??= token;
            last = token;
        }
        return (first, last);
    }

    private static BoundaryView Boundary(SyntaxToken? token, SyntaxNode root, int start, int end,
        List<PhysicalLine> lines, CancellationToken cancellationToken)
    {
        if (token is not { } present) return new(null, null, null, null, null);
        StatementSyntax? statement = null;
        InitializerExpressionSyntax? initializer = null;
        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.SpanStart > present.SpanStart || present.SpanStart >= node.Span.End) continue;
            if (node is StatementSyntax and not BlockSyntax && (statement is null || node.Span.Length < statement.Span.Length))
                statement = (StatementSyntax)node;
            if (node is InitializerExpressionSyntax value && (initializer is null || node.Span.Length < initializer.Span.Length))
                initializer = value;
        }
        UnitView? Unit(SyntaxNode? node) => node is null ? null : new(node.Kind().ToString(),
            Position(node.SpanStart, lines), Position(node.Span.End, lines),
            start > node.SpanStart && start < node.Span.End, end > node.SpanStart && end < node.Span.End);
        return new(present.Kind().ToString(), Position(present.SpanStart, lines), Position(present.Span.End, lines),
            Unit(statement), Unit(initializer));
    }

    // A legacy outline cannot certify preparation for an exact draft. Check the closed,
    // bounded evidence shape as well as its identity before the harness consumes it.
    internal static bool ConfirmsProposal(JsonElement value, ProgrammingEditFingerprint expected)
    {
        bool Shape(JsonElement item, params string[] fields) => item.ValueKind == JsonValueKind.Object &&
            item.EnumerateObject().Count() == fields.Length &&
            item.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields);
        bool Integer(JsonElement item, int minimum, int maximum) => item.ValueKind == JsonValueKind.Number &&
            item.TryGetInt32(out var number) && number >= minimum && number <= maximum;
        bool Boolean(JsonElement item) => item.ValueKind is JsonValueKind.True or JsonValueKind.False;
        bool Token(JsonElement item) => item.ValueKind == JsonValueKind.Null ||
            item.ValueKind == JsonValueKind.String && Enum.TryParse<SyntaxKind>(item.GetString(), out var kind) &&
            kind.ToString() == item.GetString() && kind != SyntaxKind.EndOfFileToken &&
            (kind.ToString().EndsWith("Token", StringComparison.Ordinal) || SyntaxFacts.IsKeywordKind(kind) || SyntaxFacts.IsContextualKeyword(kind));
        bool PositionView(JsonElement item) => Shape(item, "line", "column") &&
            Integer(item.GetProperty("line"), 1, 1_000_001) && Integer(item.GetProperty("column"), 1, MaximumSourceBytes + 1);
        bool Unit(JsonElement item) => item.ValueKind == JsonValueKind.Null ||
            Shape(item, "kind", "start", "end", "rangeStartsInside", "rangeEndsInside") &&
            item.GetProperty("kind").ValueKind == JsonValueKind.String && item.GetProperty("kind").GetString()!.Length is > 0 and <= 80 &&
            PositionView(item.GetProperty("start")) && PositionView(item.GetProperty("end")) &&
            Boolean(item.GetProperty("rangeStartsInside")) && Boolean(item.GetProperty("rangeEndsInside"));
        bool BoundaryViewValid(JsonElement item)
        {
            if (!Shape(item, "tokenKind", "tokenStart", "tokenEnd", "statement", "initializer") || !Token(item.GetProperty("tokenKind"))) return false;
            if (item.GetProperty("tokenKind").ValueKind == JsonValueKind.Null)
                return item.EnumerateObject().All(property => property.Value.ValueKind == JsonValueKind.Null);
            return PositionView(item.GetProperty("tokenStart")) && PositionView(item.GetProperty("tokenEnd")) &&
                Unit(item.GetProperty("statement")) && Unit(item.GetProperty("initializer"));
        }
        if (!Shape(value, "fingerprint", "startBoundary", "endBoundary", "replacement") ||
            !expected.Matches(value.GetProperty("fingerprint")) ||
            !BoundaryViewValid(value.GetProperty("startBoundary")) || !BoundaryViewValid(value.GetProperty("endBoundary"))) return false;
        var replacement = value.GetProperty("replacement");
        return Shape(replacement, "firstTokenKind", "lastTokenKind", "utf8Bytes", "lfCount", "crCount", "endsWithLf") &&
            Token(replacement.GetProperty("firstTokenKind")) && Token(replacement.GetProperty("lastTokenKind")) &&
            (replacement.GetProperty("firstTokenKind").ValueKind == JsonValueKind.Null) ==
                (replacement.GetProperty("lastTokenKind").ValueKind == JsonValueKind.Null) &&
            Integer(replacement.GetProperty("utf8Bytes"), 0, ProgrammingEditProposal.MaximumTextBytes) &&
            Integer(replacement.GetProperty("lfCount"), 0, ProgrammingEditProposal.MaximumTextBytes) &&
            Integer(replacement.GetProperty("crCount"), 0, ProgrammingEditProposal.MaximumTextBytes) &&
            Boolean(replacement.GetProperty("endsWithLf"));
    }

    private sealed record ProposedEditView(ProgrammingEditFingerprint Fingerprint, BoundaryView StartBoundary,
        BoundaryView EndBoundary, FragmentView Replacement);
    private sealed record BoundaryView(string? TokenKind, SourcePosition? TokenStart, SourcePosition? TokenEnd,
        UnitView? Statement, UnitView? Initializer);
    private sealed record UnitView(string Kind, SourcePosition Start, SourcePosition End,
        bool RangeStartsInside, bool RangeEndsInside);
    private sealed record FragmentView(string? FirstTokenKind, string? LastTokenKind, int Utf8Bytes,
        int LfCount, int CrCount, bool EndsWithLf);
}
