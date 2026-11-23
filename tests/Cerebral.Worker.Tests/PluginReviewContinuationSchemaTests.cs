using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Worker;

namespace Cerebral.Worker.Tests;

public static partial class PluginReviewContinuationTests
{
    public static async Task RunSchemaPreflightOnlyAsync()
    {
        await RunSchemaPreflightChecksAsync();
    }

    private static async Task<int> RunSchemaPreflightChecksAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("QA descriptor preflight: " + name);
            checks++;
        }
        foreach (var variant in new[] { "description", "schema", "missing", "duplicate" })
        {
            using var f = await Fixture.CreateAsync(remoteModel: true);
            var presentation = f.Present(f.Origin.Id);
            if (variant == "missing") f.Tools.MissingName = PluginControlToolExtension.ToolName;
            else if (variant == "duplicate") f.Tools.DuplicateName = PluginControlToolExtension.ToolName;
            else f.Tools.Transform = tool => tool.Name != PluginControlToolExtension.ToolName ? tool : variant == "description"
                ? tool with { Description = "A newer native contract." }
                : tool with { InputSchema = CerebralJson.Element(new { type = "object", properties = new { expectedTexts = new { type = "array" } } }) };
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null && f.RemoteOwner.Calls.Count == 0 && f.Tools.ExecuteCount == 0,
                variant + " mandatory contract failure blocks before expensive remote/model work or QA admission");
            Check(f.Store.List<object>("plugin-visual-assessment").Count == 0,
                variant + " mandatory contract failure reserves no visual assessment");
        }
        foreach (var hintsOnly in new[] { false, true })
        {
            using var f = await Fixture.CreateAsync(acceptedProfile: profile => profile.AllowedTools = ["*"]);
            var original = f.Origin.Submission.SettingsSnapshot;
            var presentation = f.Present(f.Origin.Id);
            if (hintsOnly) f.Tools.Transform = tool => tool with { PrerequisiteTools = ["plugin.contract"] };
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var marker = f.Read(presentation.Id).PluginTests!;
            Check(marker is { Status: "admitted", RunId: not null } &&
                System.Text.Json.JsonElement.DeepEquals(f.Workflows.Store.Get(marker.RunId!).Submission.SettingsSnapshot, original) && f.Tools.ExecuteCount == 0,
                (hintsOnly ? "hint-only" : "matching wildcard-granted") + " descriptors admit without replacing the frozen snapshot");
            var beforeRecovery = f.Tools.ListCount;
            f.Tools.BeforeListAsync = (_, _, _) => throw new InvalidOperationException("Recovery must not query current tools.");
            var recovered = await f.Workflows.SubmitPluginTestsAsync(f.Read(presentation.Id), default);
            Check(recovered.Id == marker.RunId && f.Tools.ListCount == beforeRecovery,
                "an already admitted exact run recovers before any catalog access");
        }
        using (var f = await Fixture.CreateAsync(exposeTools: false))
        {
            var presentation = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "a missing dispatcher fails closed for a new admission");
        }
        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var initialCount = f.Tools.ListCount;
            f.Tools.BeforeListAsync = async (count, _, token) =>
            {
                if (count != initialCount + 1) return;
                entered.TrySetResult(true); await release.Task.WaitAsync(token);
            };
            var processing = f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = f.Store.Get<AgentProfile>("agent", f.Profile.Id)!;
            current.AllowedTools.Remove(PluginControlToolExtension.ToolName); f.Store.Put("agent", current.Id, current);
            release.TrySetResult(true); await processing;
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "grant revocation during an unlocked catalog await is rechecked before admission");
        }
        using (var f = await Fixture.CreateAsync(remoteModel: true))
        {
            var presentation = f.Present(f.Origin.Id);
            f.RemoteOwner.WaitForRelease = true;
            var processing = f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            await f.RemoteOwner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Tools.Transform = tool => tool.Name == PluginControlToolExtension.ToolName ? tool with { Description = "Changed during remote preparation." } : tool;
            f.RemoteOwner.Release.TrySetResult(true); await processing;
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "a fresh post-await descriptor proof blocks a changed contract before the final commit");
        }
        foreach (var lateFailure in new[] { false, true })
        {
            using var f = await Fixture.CreateAsync(remoteModel: true);
            var presentation = f.Present(f.Origin.Id);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var initialCount = f.Tools.ListCount;
            f.Tools.BeforeListAsync = async (count, _, token) =>
            {
                if (count != initialCount + 1) return;
                entered.TrySetResult(true); await release.Task.WaitAsync(token);
                if (lateFailure) throw new InvalidOperationException("A late catalog failure after exact competing admission.");
            };
            var first = f.Workflows.SubmitPluginTestsAsync(presentation, default, f.RemoteModels);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var competing = await f.Workflows.SubmitPluginTestsAsync(presentation, default, f.RemoteModels);
            var remoteCalls = f.RemoteOwner.Calls.Count;
            f.Tools.MissingName = PluginControlToolExtension.ToolName;
            release.TrySetResult(true);
            var recovered = await first;
            Check(recovered.Id == competing.Id && f.RemoteOwner.Calls.Count == remoteCalls &&
                f.Workflows.Store.CountActive() == 1,
                "exact concurrent admission wins over a late " + (lateFailure ? "failed" : "missing") + " catalog without extra remote preparation");
        }
        Console.WriteLine("QA descriptor preflight: " + checks + " SQLite/admission checks passed; catalog and remote owner are deterministic fakes, no native/model/provider execution.");
        return checks;
    }
}
