namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    public const string OwnerVerifiedPreparedVersion = "1.0.8-owner-verified-prepared.1";
    private readonly bool useOwnerVerificationCompletion;
    private enum OwnerVerifiedPreparedEdition { Enabled }

    private ConversationAgentHarness(OwnerVerifiedPreparedEdition edition) : this(RepeatPreparedEdition.Enabled)
    {
        useOwnerVerificationCompletion = edition == OwnerVerifiedPreparedEdition.Enabled;
        Descriptor = Descriptor with { Version = OwnerVerifiedPreparedVersion };
    }

    /// <summary>Opt-in completion policy for a Worker-frozen owner checker recipe.</summary>
    public static ConversationAgentHarness CreateOwnerVerifiedPreparedEdition() =>
        new(OwnerVerifiedPreparedEdition.Enabled);
}
