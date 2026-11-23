namespace Cerebral.Contracts.Models;

/// <summary>Passive, instance-bound selection data. Only the loaded Worker handler that produced
/// this value can authorize preparation or generation; constructing this record grants nothing.</summary>
public sealed record ModelExecutionPolicy(
    int SchemaVersion, string PolicyId, int PolicyRevision,
    ModelRuntimeSupportBinding Binding, string RuntimeGenerationId,
    string InputKind, bool ReasoningEnabled,
    int MinimumContextTokens, int MaximumContextTokens,
    int MinimumVisibleOutputTokens, int MaximumVisibleOutputTokens,
    string SoftwareProvenance)
{
    public const int CurrentSchemaVersion = 1;
    public const string NativeTextInputKind = "text";

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || PolicyRevision < 1 ||
            !Bounded(PolicyId, 128) || !Bounded(RuntimeGenerationId, 160) ||
            !Bounded(SoftwareProvenance, 160))
            throw new ArgumentException("The model execution policy identity is invalid.");
        if (Binding is null || !Bounded(Binding.WorkerInstallationId, 160) ||
            !Bounded(Binding.BackendId, 128) || !Bounded(Binding.AdapterId, 128) ||
            !Bounded(Binding.AdapterVersion, 128) || !Bounded(Binding.ModelId, 128) ||
            !Bounded(Binding.ConfigurationScopeId, 160) ||
            Binding.ConfigurationSha256 is not { Length: 64 } digest ||
            digest.Any(character => !Uri.IsHexDigit(character)) || !Bounded(Binding.InstanceId, 160))
            throw new ArgumentException("The model execution policy has no exact loaded binding.");
        if (InputKind != NativeTextInputKind || ReasoningEnabled ||
            MinimumContextTokens < 1 || MaximumContextTokens < 1 ||
            MinimumContextTokens > MaximumContextTokens ||
            MinimumVisibleOutputTokens < 1 || MaximumVisibleOutputTokens < 1 ||
            MinimumVisibleOutputTokens > MaximumVisibleOutputTokens)
            throw new ArgumentException("The model execution policy has an unsupported text envelope.");
    }

    private static bool Bounded(string? value, int maximum) => value is { Length: > 0 } &&
        value.Length <= maximum && !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);
}

/// <summary>Optional facet on the selected adapter and genuinely owned prepared lease. Wrappers
/// must forward the exact captured value; it is not a replacement for host admission.</summary>
public interface IModelExecutionPolicyFacet
{
    ModelExecutionPolicy ExecutionPolicy { get; }
}
