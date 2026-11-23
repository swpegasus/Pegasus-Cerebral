namespace Cerebral.Core;

/// <summary>Explicit local component selection; null removes the projector. This does not establish model capability.</summary>
public sealed record ConfigureModelComponentRequest(string Id, string? VisionProjectorPath);

public static class ModelComponentConfiguration
{
    public static string? NormalizePath(string? value)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32768 || value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) || value.Split(['/', '\\']).Contains("..", StringComparer.Ordinal))
            throw new ArgumentException("The image projector requires an absolute file path without parent traversal.");
        if (OperatingSystem.IsWindows() && (value.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            value.StartsWith("\\\\.\\", StringComparison.Ordinal) || value.Skip(2).Contains(':') ||
            value.Any(character => character is '<' or '>' or '"' or '|' or '?' or '*')))
            throw new ArgumentException("The image projector path cannot use device syntax, alternate streams or invalid characters.");
        var path = Path.GetFullPath(value);
        if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select an explicit GGUF image projector component.");
        return path;
    }
}
