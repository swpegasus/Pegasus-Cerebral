namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    public const string RepeatPreparedVersion = "1.0.7-repeat-prepared.1";
    private enum RepeatPreparedEdition { Enabled }

    private ConversationAgentHarness(RepeatPreparedEdition edition) : this(PreparedInterventionEdition.Enabled)
    {
        useRepeatedSuccessGuard = edition == RepeatPreparedEdition.Enabled;
        Descriptor = Descriptor with { Version = RepeatPreparedVersion };
    }

    /// <summary>Opt-in repeat guard with exact prepared-effect owner recovery.</summary>
    public static ConversationAgentHarness CreateRepeatPreparedEdition() => new(RepeatPreparedEdition.Enabled);
}
