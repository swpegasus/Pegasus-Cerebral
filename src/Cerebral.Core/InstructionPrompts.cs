namespace Cerebral.Core;

/// <summary>One effective shared/role prompt. Empty content is an explicit disabled layer.
/// Sha256 is a source/revision-bound compare-and-swap token; backup content need not retain it.</summary>
public sealed record InstructionPrompt(string Role, string Content)
{
    public string Sha256 { get; init; } = "";
}
