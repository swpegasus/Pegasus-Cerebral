using System.Text.Json;
using Cerebral.Contracts.Context;

namespace Cerebral.PersonalityServicePlugin;

/// <summary>Original v4 extension reader. Never reads stores, observes today's origin or changes history.</summary>
internal static class PersonalityLegacyContextReader
{
    internal static WorkflowContextSelection Read(WorkflowContextLegacyFacts legacy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (legacy.SnapshotVersion != 4)
            return PersonalityWorkflowContextContribution.Absent(WorkflowContextDisposition.Unsupported, "legacy-snapshot");
        try
        {
            var fragments = legacy.Fragments;
            PersonalityContributionCodec.Boundary(fragments, WorkflowContextContributionContract.MaximumLegacyFragmentUtf8Bytes);
            PersonalityServicePlugin.Fields(fragments, ["personalityAdmission"],
                ["personalityInstructions", "personalityMigration"]);
            var record = fragments.GetProperty("personalityAdmission");
            PersonalityContributionCodec.Boundary(record, 8192);
            PersonalityServicePlugin.Fields(record, ["schemaVersion", "status", "reason", "seed", "seedSha256"]);
            if (record.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException();
            var status = record.GetProperty("status").GetString();
            var reasonValue = record.GetProperty("reason");
            var reason = reasonValue.ValueKind == JsonValueKind.Null ? null : reasonValue.GetString();
            if (reason is not null && (reason.Length is < 1 or > 64 || reason.Any(char.IsControl)))
                throw new InvalidDataException();
            var seed = record.GetProperty("seed");
            var digest = record.GetProperty("seedSha256");
            var witness = fragments.TryGetProperty("personalityInstructions", out var instructions) &&
                instructions.ValueKind != JsonValueKind.Null ? ReadWitness(instructions) : null;
            var hasMigration = fragments.TryGetProperty("personalityMigration", out var migration) &&
                migration.ValueKind != JsonValueKind.Null;
            if (status == "ready")
            {
                if (reason is not null || seed.ValueKind != JsonValueKind.Object || digest.ValueKind != JsonValueKind.String ||
                    witness is null || !hasMigration) throw new InvalidDataException();
                PersonalityServicePlugin.Fields(migration,
                    ["retiredWriterGeneration", "retirementEvidenceId", "instructions", "ownerId"]);
                Hash(migration.GetProperty("retiredWriterGeneration"));
                Identifier(migration.GetProperty("retirementEvidenceId"), 160);
                if (migration.GetProperty("ownerId").GetString() != "owner" ||
                    ReadWitness(migration.GetProperty("instructions")) != witness ||
                    witness.ProfileRevision != legacy.Facts.Accepted.ProfileRevision ||
                    legacy.Facts.Accepted.InstallationId is { } known && witness.InstallationId != known)
                    throw new InvalidDataException();
                // A witness installation is historical consistency data. It cannot populate the host header.
                PersonalityContributionCodec.Boundary(seed, PersonalityAdmission.MaximumSnapshotUtf8Bytes);
                var hash = digest.GetString()!;
                PersonalityAdmissionEngine.RequireHash(hash);
                if (PersonalityContributionCodec.Hash(seed) != hash) throw new InvalidDataException();
                var admission = PersonalityAdmission.Restore(seed,
                    PersonalityContributionCodec.Submission(legacy.Facts.Accepted),
                    PersonalityContributionCodec.Consumer(legacy.Facts.Accepted));
                var selected = new WorkflowContextSelection(WorkflowContextDisposition.Ready, null,
                    seed.Clone(), hash, PersonalityContributionCodec.Template(admission));
                _ = PersonalityContributionCodec.Decode(legacy.Facts.Accepted, selected);
                token.ThrowIfCancellationRequested();
                return selected;
            }
            if (seed.ValueKind != JsonValueKind.Null || digest.ValueKind != JsonValueKind.Null || hasMigration)
                throw new InvalidDataException();
            var disposition = status switch
            {
                "disabled" when reason is null => WorkflowContextDisposition.Omitted,
                "notApplicable" when reason == "scope-excluded" => WorkflowContextDisposition.Omitted,
                "unavailable" when reason is "component-unavailable" or "selection-unavailable" => WorkflowContextDisposition.Unavailable,
                "migrationPending" when reason == "migration-pending" => WorkflowContextDisposition.Blocked,
                "legacy" when reason == "legacy-snapshot" => WorkflowContextDisposition.Unsupported,
                _ => throw new InvalidDataException()
            };
            token.ThrowIfCancellationRequested();
            return PersonalityWorkflowContextContribution.Absent(disposition, reason);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or
            JsonException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("The original personality compatibility state is invalid or unsupported."); }
    }

    private sealed record Witness(string SharedToken, string LeadToken, int ProfileRevision,
        string ProfileInstructionsSha256, string InstallationId);

    private static Witness ReadWitness(JsonElement value)
    {
        PersonalityServicePlugin.Fields(value,
            ["sharedToken", "leadToken", "profileRevision", "profileInstructionsSha256", "installationId"]);
        var revision = value.GetProperty("profileRevision").GetInt32();
        if (revision < 0) throw new InvalidDataException();
        return new(Hash(value.GetProperty("sharedToken")), Hash(value.GetProperty("leadToken")), revision,
            Hash(value.GetProperty("profileInstructionsSha256")), Identifier(value.GetProperty("installationId"), 128));
    }

    private static string Hash(JsonElement value)
    {
        var text = value.GetString();
        PersonalityAdmissionEngine.RequireHash(text);
        return text!;
    }

    private static string Identifier(JsonElement value, int maximum)
    {
        var text = value.GetString();
        if (text is not { Length: > 0 } || text.Length > maximum || text.Any(char.IsControl))
            throw new InvalidDataException();
        return text;
    }
}
