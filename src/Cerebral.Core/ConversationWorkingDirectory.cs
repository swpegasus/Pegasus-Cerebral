using System.Security.Cryptography;
using System.Text;

namespace Cerebral.Core;

public static class ConversationWorkingDirectory
{
    /// <summary>Chooses a stable chat folder without creating filesystem entries.</summary>
    public static string DefaultForChat(string projectsDirectory, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (conversationId.Length > 100 ||
            conversationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new ArgumentException("The conversation identifier is invalid.", nameof(conversationId));
        var unixPath = projectsDirectory.StartsWith('/');
        if ((!unixPath && !Path.IsPathFullyQualified(projectsDirectory)) ||
            projectsDirectory.StartsWith(@"\\", StringComparison.Ordinal) || projectsDirectory.StartsWith("//", StringComparison.Ordinal) ||
            projectsDirectory.Split(['/', '\\']).Any(part => part is "." or ".."))
            throw new ArgumentException("The Projects directory must be an absolute local path.", nameof(projectsDirectory));
        var folder = Guid.TryParseExact(conversationId, "N", out var id) &&
            id.ToString("N") == conversationId ? conversationId :
            "chat-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conversationId))).ToLowerInvariant();
        return unixPath ? projectsDirectory.TrimEnd('/') + "/Source/" + folder :
            Path.Combine(Path.GetFullPath(projectsDirectory), "Source", folder);
    }
}
