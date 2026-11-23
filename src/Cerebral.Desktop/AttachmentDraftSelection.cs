using System.Collections.ObjectModel;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop;

/// <summary>Host-created immutable selection retained with a request before asynchronous input routing.</summary>
public sealed record AttachmentDraftSelection(DesktopConversationBinding Binding, string ProfileId,
    IReadOnlyList<ContentPart> Content, IReadOnlyList<string> ImportIds)
{
    public static AttachmentDraftSelection Freeze(DesktopConversationBinding binding, string profileId,
        IReadOnlyList<AttachmentTransferSnapshot> uploads)
    {
        if (string.IsNullOrWhiteSpace(binding.Generation) || string.IsNullOrWhiteSpace(binding.SessionId) ||
            string.IsNullOrWhiteSpace(profileId) || uploads.Count > 16)
            throw new InvalidOperationException("A bounded attachment selection requires an exact conversation and profile.");
        if (uploads.Any(x => x.Busy || x.Uncertain || x.Attachment is null))
            throw new InvalidOperationException("Every selected attachment must be ready before sending. Retry or remove incomplete imports.");
        if (uploads.Any(x => x.Request.ProfileId != profileId || x.Request.ConversationId != binding.SessionId))
            throw new InvalidOperationException("Selected attachments belong to another conversation or profile.");
        var parts = uploads.Select(x =>
        {
            var original = x.Attachment!;
            var copy = original with { Metadata = original.Metadata is null ? null :
                new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(original.Metadata, StringComparer.Ordinal)) };
            return copy.MediaType.StartsWith("image/", StringComparison.Ordinal)
                ? (ContentPart)new ImageContentPart(copy) : new FileContentPart(copy);
        }).ToArray();
        return new(binding, profileId, Array.AsReadOnly(parts), Array.AsReadOnly(uploads.Select(x => x.Request.RequestId).ToArray()));
    }
}
