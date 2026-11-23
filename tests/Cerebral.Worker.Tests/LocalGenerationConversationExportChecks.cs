using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Workflows;
using Cerebral.Conversation;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Pure export, descriptor and initial-state checks. Frozen source pins separately
/// establish that the retained factory implementations were not edited.</summary>
public static class LocalGenerationConversationExportChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Conversation export: " + name); count++; }
        using var package = new LocalGenerationConversationServicePlugin();
        using var ordinary = new ConversationServicePlugin();
        var exported = package.Harnesses;
        var retained = new (string Version, Func<IAgentHarness> Factory)[]
        {
            ("1.0.1", () => new ConversationAgentHarness()),
            ("1.0.2-generic-context.1", ConversationAgentHarness.CreateContextContributionEdition),
            ("1.0.3-workflow-progress.1", ConversationAgentHarness.CreateWorkflowProgressEdition),
            ("1.0.3-tool-context.1", ConversationAgentHarness.CreateToolContextProjectionEdition),
            ("1.0.4-prepared-intervention.1", ConversationAgentHarness.CreatePreparedInterventionEdition),
            ("1.0.4-tool-catalog-page.1", ConversationAgentHarness.CreateToolCatalogPageEdition)
        };
        var versions = retained.Select(item => item.Version).Append("1.0.5-local-generation.1")
            .Append(ConversationAgentHarness.RepeatedSuccessGuardVersion)
            .Append(ConversationAgentHarness.RepeatPreparedVersion)
            .Append(ConversationAgentHarness.OwnerVerifiedPreparedVersion).ToHashSet(StringComparer.Ordinal);
        Check(exported.Count == 10 &&
            exported.All(item => item.Descriptor.Id == ConversationWorkflowContract.DefinitionId) &&
            exported.Select(item => item.Descriptor.Version).Distinct(StringComparer.Ordinal).Count() == 10 &&
            exported.Select(item => item.Descriptor.Version).ToHashSet(StringComparer.Ordinal).SetEquals(versions),
            "the package exports the exact ten-edition union once");
        var choice = new WorkflowLocalGenerationChoice(1, "local.native.text", 32_768, 256);
        foreach (var item in retained)
        {
            var actual = exported.Single(harness => harness.Descriptor.Version == item.Version);
            var previous = item.Factory();
            Check(previous.Descriptor.Version == item.Version &&
                JsonElement.DeepEquals(CerebralJson.Element(actual.Descriptor), CerebralJson.Element(previous.Descriptor)),
                "retained edition descriptor stays identical: " + item.Version);
            var actualStart = Start(actual, null);
            var previousStart = Start(previous, null);
            Check(actualStart.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:history", CommandVersion: 1 } &&
                JsonElement.DeepEquals(CerebralJson.Element(actualStart), CerebralJson.Element(previousStart)),
                "retained factory preserves its initial state and v1 history request: " + item.Version);
            Check(actual.Descriptor.LocalGenerationConsumer is null &&
                Start(actual, choice).Next is CompleteWorkflow { Success: false },
                "a local choice cannot opt a retained edition into generation v2: " + item.Version);
        }
        var local = exported.Single(item => item.Descriptor.Version == "1.0.5-local-generation.1");
        Check(local.Descriptor.LocalGenerationConsumer ==
            new WorkflowLocalGenerationConsumer(1, "cerebral.host:generate", 2, "local.native.text") &&
            exported.Count(item => item.Descriptor.LocalGenerationConsumer is not null) == 1,
            "only the explicit local edition declares the exact generation-v2 consumer");
        Check(local.Descriptor.ToolReceiptConsumer is { SchemaVersion: 1, Supported: true },
            "the new edition retains its independent receipt consumer");
        var localStart = Start(local, choice);
        Check(localStart.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:history", CommandVersion: 1 } &&
            localStart.State.TryGetProperty("localGenerationV2", out var marker) &&
            marker.GetProperty("runId").GetString() == "conversation-export-check" &&
            JsonElement.DeepEquals(marker.GetProperty("choice"), CerebralJson.Element(choice)),
            "explicit accepted choice is retained without changing history's v1 contract");
        Check(!Start(local, null).State.TryGetProperty("localGenerationV2", out _),
            "new edition without a choice keeps the marker absent");
        var repeat = exported.Single(item => item.Descriptor.Version == ConversationAgentHarness.RepeatedSuccessGuardVersion);
        Check(repeat.Descriptor.LocalGenerationConsumer is null &&
            repeat.Descriptor.ToolReceiptConsumer is null &&
            Start(repeat, null).Next is InvokeWorkflowCommand { CommandId: "cerebral.host:history", CommandVersion: 1 },
            "historical repeat guard retains its unsupported receipt consumer and history entry");
        var combined = exported.Single(item => item.Descriptor.Version == ConversationAgentHarness.RepeatPreparedVersion);
        Check(combined.Descriptor.LocalGenerationConsumer is null &&
            combined.Descriptor.ToolReceiptConsumer is { SchemaVersion: 1, Supported: true } &&
            Start(combined, null).Next is InvokeWorkflowCommand { CommandId: "cerebral.host:history", CommandVersion: 1 },
            "combined edition exports exact prepared-capable receipt policy and retained history entry");
        var ownerVerified = exported.Single(item => item.Descriptor.Version == ConversationAgentHarness.OwnerVerifiedPreparedVersion);
        Check(ownerVerified.Descriptor.LocalGenerationConsumer is null &&
            ownerVerified.Descriptor.ToolReceiptConsumer is { SchemaVersion: 1, Supported: true } &&
            Start(ownerVerified, null).Next is CompleteWorkflow { Success: false },
            "owner-verified edition fails closed without a frozen owner recipe");
        Check(JsonElement.DeepEquals(CerebralJson.Element(repeat.Descriptor),
                CerebralJson.Element(ConversationAgentHarness.CreateRepeatedSuccessGuardEdition().Descriptor)) &&
            JsonElement.DeepEquals(CerebralJson.Element(exported.Single(item =>
                    item.Descriptor.Version == ConversationAgentHarness.PreparedInterventionVersion).Descriptor),
                CerebralJson.Element(ConversationAgentHarness.CreatePreparedInterventionEdition().Descriptor)),
            "older repeat and prepared descriptors remain exact");
        Check(ConversationWorkflowContract.DefinitionVersion == "1.0.1" &&
            ordinary.Harnesses.All(item => item.Descriptor.LocalGenerationConsumer is null),
            "ordinary default contract and previous entry remain unopted");
        return count;
    }

    /// <summary>Pass the exact reviewed source/package manifest; this check performs no file I/O.</summary>
    public static int CheckManifest(JsonElement manifest, string expectedPackageVersion = "1.0.6")
    {
        if (expectedPackageVersion is not ("1.0.6" or "1.0.7" or "1.0.8" or "1.0.9" or "1.0.10" or "1.0.11" or "1.0.12"))
            throw new ArgumentException("Select a reviewed Conversation package version from 1.0.6 through 1.0.12.", nameof(expectedPackageVersion));
        if (manifest.ValueKind != JsonValueKind.Object ||
            manifest.GetProperty("schemaVersion").GetInt32() != 1 ||
            manifest.GetProperty("contract").GetString() != "pegasus.cerebral.service" ||
            manifest.GetProperty("apiVersion").GetInt32() != 1 ||
            manifest.GetProperty("id").GetString() != ConversationWorkflowContract.ModuleId ||
            manifest.GetProperty("version").GetString() != expectedPackageVersion ||
            // Package 1.0.7 retains the earlier entry and definition versions. Its new package
            // version is admitted only by an explicit caller, not by changing historical defaults.
            (expectedPackageVersion == "1.0.6" &&
                manifest.GetProperty("version").GetString() != "1.0.6") ||
            manifest.GetProperty("assembly").GetString() != "Cerebral.Conversation.dll" ||
            manifest.GetProperty("entryType").GetString() != typeof(LocalGenerationConversationServicePlugin).FullName)
            throw new InvalidOperationException("Conversation export: reviewed manifest does not select the exact local-generation package entry.");
        return 1;
    }

    private static WorkflowStep Start(IAgentHarness harness, WorkflowLocalGenerationChoice? choice)
    {
        var submission = new WorkflowSubmission("conversation-export-check", "conversation-export-check-chat", "fixture-profile",
            harness.Descriptor.Id, harness.Descriptor.Version, "Fixture request", null,
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject) { LocalGeneration = choice };
        return harness.Advance(new(new WorkflowRun
        { Id = submission.RequestId, Submission = submission, Status = WorkflowStatus.Running }));
    }
}
