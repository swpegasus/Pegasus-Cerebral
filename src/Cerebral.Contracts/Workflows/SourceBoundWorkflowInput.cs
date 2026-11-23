using System.Text.Json;

namespace Cerebral.Contracts.Workflows;

public static class SourceBoundWorkflowInputContract
{
    public const int SchemaVersion = 1;
}

/// <summary>Value-only facts for a package-owned workflow schema. The host
/// reconstructs them from its committed source event, current conversation,
/// central settings and selected profile. They convey no authority by themselves.</summary>
public sealed record SourceBoundWorkflowSourceFacts(
    int SchemaVersion, string OwnerBinding,
    string PackageId, string PackageVersion, string PackageGeneration,
    string SourceDefinitionId, string SourceDefinitionVersion,
    string SourceConversationId, string SourceProfileId,
    string SourceRequestId, string SourceEventId, long SourceEventSequence,
    string SourceInputSha256, string? SourceProjectId,
    string SourceTitle, long SourceTitleRevision,
    long SettingsRevision, string SelectedProfileId, long SelectedProfileRevision);

/// <summary>Host-built public Input. The package supplies only PackageInput;
/// it cannot supply Source or set the independent private execution marker.
/// A package may validate these facts and use them to construct its own seed.</summary>
public sealed record SourceBoundWorkflowInput(
    int SchemaVersion, SourceBoundWorkflowSourceFacts Source,
    JsonElement PackageInput);

// The exact title package must version its definition/input schema to accept
// this envelope, derive TitleSeed from Source plus its own excerpt/other values,
// and keep its prompt, output parse, fallback and RenameRequestId policy.
// The host never parses TitleSeed. An owner/agent/service request can supply
// lookalike JSON but cannot set the private WorkerExecutionSnapshot marker.
