using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.ConversationTitlePlugin;

internal sealed record TitlePackageInput(int SchemaVersion, string SourceInputSha256, string Excerpt);
internal sealed record TitleInputSnapshot(SourceBoundWorkflowSourceFacts Source, TitleSeed Seed, string WorkKey);

internal static class TitleSourceBoundInput
{
    internal static JsonElement PackageValues(string hash, string excerpt) =>
        JsonSerializer.SerializeToElement(new TitlePackageInput(TitlePackageIdentity.PackageInputVersion, hash, excerpt), CerebralJson.Options);

    internal static TitleInputSnapshot Read(WorkflowRun run)
    {
        TitlePolicy.Bounded(run.Submission.Input.GetRawText(), WorkflowContract.MaximumStateBytes);
        TitlePolicy.ExactFields(run.Submission.Input, "schemaVersion", "source", "packageInput");
        var sourceJson = run.Submission.Input.GetProperty("source");
        TitlePolicy.ExactFields(sourceJson, "schemaVersion", "ownerBinding", "packageId", "packageVersion", "packageGeneration",
            "sourceDefinitionId", "sourceDefinitionVersion", "sourceConversationId", "sourceProfileId", "sourceRequestId",
            "sourceEventId", "sourceEventSequence", "sourceInputSha256", "sourceProjectId", "sourceTitle", "sourceTitleRevision",
            "settingsRevision", "selectedProfileId", "selectedProfileRevision");
        TitlePolicy.ExactFields(run.Submission.Input.GetProperty("packageInput"), "schemaVersion", "sourceInputSha256", "excerpt");
        var input = run.Submission.Input.Deserialize<SourceBoundWorkflowInput>(CerebralJson.Options)
            ?? throw new ArgumentException("The source-bound title input is missing.");
        var source = input.Source ?? throw new ArgumentException("The public source projection is missing.");
        var package = input.PackageInput.Deserialize<TitlePackageInput>(CerebralJson.Options)
            ?? throw new ArgumentException("The title excerpt is missing.");
        if (input.SchemaVersion != SourceBoundWorkflowInputContract.SchemaVersion ||
            source.SchemaVersion != SourceBoundWorkflowInputContract.SchemaVersion || package.SchemaVersion != TitlePackageIdentity.PackageInputVersion ||
            source.PackageId != TitlePackageIdentity.PackageId || source.PackageVersion != TitlePackageIdentity.PackageVersion ||
            source.SourceDefinitionId != ConversationWorkflowContract.DefinitionId || string.IsNullOrWhiteSpace(source.SourceDefinitionVersion) ||
            source.SourceEventSequence < 1 || source.SourceTitleRevision < 0 || source.SettingsRevision < 1 || source.SelectedProfileRevision < 1 ||
            package.SourceInputSha256 != source.SourceInputSha256 || string.IsNullOrWhiteSpace(package.Excerpt))
            throw new ArgumentException("The title source projection or excerpt edition is invalid.");
        TitlePolicy.Identifier(source.PackageGeneration, 128);
        TitlePolicy.Bounded(source.SourceDefinitionVersion, 256);
        // A source profile is not the selected inference profile and need not use its
        // legacy ASCII private-reference grammar. Reuse the exact source-ID validator.
        _ = new TitlePreferences(false, null, null, [source.SourceProfileId]);
        TitlePolicy.Bounded(package.Excerpt, TitlePolicy.MaximumExcerptBytes);
        var seed = new TitleSeed(1, source.OwnerBinding, source.SourceConversationId, source.SourceRequestId,
            source.SourceEventId, source.SourceInputSha256, package.Excerpt, source.SourceTitle, 0,
            source.SourceProjectId, source.SelectedProfileId, source.SelectedProfileRevision, source.SettingsRevision);
        TitlePolicy.Validate(seed);
        var key = TitlePolicy.WorkKey(seed);
        if (run.Submission.DefinitionId != TitlePackageIdentity.DefinitionId || run.Submission.DefinitionVersion != TitlePackageIdentity.DefinitionVersion ||
            run.Id != run.Submission.RequestId || run.Id != TitlePolicy.WorkRequestId(seed) ||
            run.Submission.ConversationId != TitlePolicy.WorkConversationId(seed) || run.Submission.ProfileId != source.SelectedProfileId ||
            run.Submission.ProjectId != source.SourceProjectId || run.Submission.UserText != TitlePolicy.WorkUserText ||
            run.Submission.AssignmentText is not null || run.Submission.ParentRunId is not null || run.Submission.AcceptedContent.Count != 0 ||
            run.Submission.Caller is not { Kind: EventActorKind.Service, Id: TitlePackageIdentity.PackageId })
            throw new ArgumentException("The title workflow does not match its admitted source-bound work.");
        // Public facts are validated values, NEVER the private host authority marker.
        // Every real effect still requires Core's independently saved marker/joins.
        return new(source, seed, key);
    }

    internal static JsonElement Schema()
    {
        object Text(int max) => new { type = "string", minLength = 1, maxLength = max };
        // Use the host's declared schema subset. Exact lowercase hash validation
        // and source equality still run in Read/TitlePolicy.Validate before effects.
        var sourceProperties = new Dictionary<string, object>
        {
            ["schemaVersion"] = new { @enum = new[] { SourceBoundWorkflowInputContract.SchemaVersion } },
            ["ownerBinding"] = Text(256), ["packageId"] = new { @enum = new[] { TitlePackageIdentity.PackageId } },
            ["packageVersion"] = new { @enum = new[] { TitlePackageIdentity.PackageVersion } }, ["packageGeneration"] = Text(128),
            ["sourceDefinitionId"] = new { @enum = new[] { ConversationWorkflowContract.DefinitionId } }, ["sourceDefinitionVersion"] = Text(256),
            ["sourceConversationId"] = Text(128), ["sourceProfileId"] = Text(128), ["sourceRequestId"] = Text(128), ["sourceEventId"] = Text(128),
            ["sourceEventSequence"] = new { type = "integer", minimum = 1 },
            ["sourceInputSha256"] = new { type = "string", minLength = 64, maxLength = 64 },
            ["sourceProjectId"] = new { oneOf = new object[] { Text(128), new { type = "null" } } },
            ["sourceTitle"] = new { type = "string", maxLength = ConversationRenameContract.MaximumTitleScalars },
            ["sourceTitleRevision"] = new { type = "integer", minimum = 0 }, ["settingsRevision"] = new { type = "integer", minimum = 1 },
            ["selectedProfileId"] = Text(128), ["selectedProfileRevision"] = new { type = "integer", minimum = 1 }
        };
        return JsonSerializer.SerializeToElement(new
        {
            type = "object", additionalProperties = false, required = new[] { "schemaVersion", "source", "packageInput" },
            properties = new
            {
                schemaVersion = new { @enum = new[] { SourceBoundWorkflowInputContract.SchemaVersion } },
                source = new { type = "object", additionalProperties = false, properties = sourceProperties, required = sourceProperties.Keys.ToArray() },
                packageInput = new
                {
                    type = "object", additionalProperties = false, required = new[] { "schemaVersion", "sourceInputSha256", "excerpt" },
                    properties = new
                    {
                        schemaVersion = new { @enum = new[] { TitlePackageIdentity.PackageInputVersion } },
                        sourceInputSha256 = new { type = "string", minLength = 64, maxLength = 64 },
                        excerpt = new { type = "string", minLength = 1, maxLength = TitlePolicy.MaximumExcerptBytes }
                    }
                }
            }
        }, CerebralJson.Options);
    }
}
