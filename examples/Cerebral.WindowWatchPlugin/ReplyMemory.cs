namespace Cerebral.WindowWatchPlugin;

internal sealed class ReplyMemory
{
    private readonly Queue<string> replies = new();
    private const int MaximumCharactersPerReply = 1200;

    internal string Snapshot() => string.Join("\n\n", replies);

    internal void Add(string reply, int limit)
    {
        if (limit is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(limit));
        replies.Enqueue(reply.Length > MaximumCharactersPerReply ? reply[..MaximumCharactersPerReply] : reply);
        Trim(limit);
    }

    internal void Trim(int limit)
    {
        if (limit is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(limit));
        while (replies.Count > limit) replies.Dequeue();
    }

    internal void Clear() => replies.Clear();
}
