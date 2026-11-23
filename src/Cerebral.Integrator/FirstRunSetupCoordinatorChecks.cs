using System.IO;
using Cerebral.Core;

namespace Cerebral.Integrator;

/// <summary>Opt-in composed first-run caller checks. Consent, CLI, native pipe, SDK and model
/// actions are explicit fake callbacks; no installed Cortex, Worker, connector, or model is used.</summary>
internal static class FirstRunSetupCoordinatorChecks
{
    internal static async Task<int> RunAsync()
    {
        var passed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("First-run setup: " + name);
            passed++;
            Console.WriteLine("PASS first-run setup: " + name);
        }

        var serviceRefused = false;
        try { FirstRunSetupCoordinator.RequireDirectOwnerTransport("installed-pipe"); }
        catch (InvalidOperationException) { serviceRefused = true; }
        Check(serviceRefused, "fresh service Worker transport is rejected by the production admission gate");
        FirstRunSetupCoordinator.RequireDirectOwnerTransport("pipe");

        var current = true;
        void RequireCurrent()
        {
            if (!current) throw new InvalidOperationException("original Worker changed");
        }
        var scans = 0; var imports = 0; var downloads = 0;
        current = false;
        foreach (var source in new[] { FirstRunSetupCoordinator.ScanSource, FirstRunSetupCoordinator.ImportSource,
                     FirstRunSetupCoordinator.DownloadSource })
        {
            var sourceRefused = false;
            try
            {
                await FirstRunSetupCoordinator.SelectModelSourceAsync(source,
                    RequireCurrent, () => { scans++; return Task.CompletedTask; },
                    () => { imports++; return Task.CompletedTask; },
                    () => { downloads++; return Task.CompletedTask; });
            }
            catch (InvalidOperationException) { sourceRefused = true; }
            Check(sourceRefused && scans == 0 && imports == 0 && downloads == 0,
                "dialog owner drift refuses " + source + " before a replacement Worker model effect");
        }

        current = true;
        var connects = 0; var refreshes = 0; var loads = 0; var bootstraps = new List<string?>();
        Task Refresh() { refreshes++; return Task.CompletedTask; }
        Task Load() { loads++; return Task.CompletedTask; }
        Task Bootstrap(string? id) { bootstraps.Add(id); return Task.CompletedTask; }
        var serviceRegisters = 0;
        var serviceIssue = await FirstRunSetupCoordinator.OfferOptionalCortexAsync(true, RequireCurrent,
            () =>
            {
                FirstRunSetupCoordinator.RequireDirectOwnerTransport("installed-pipe");
                serviceRegisters++;
                return Task.FromResult(true);
            }, Refresh);
        await FirstRunSetupCoordinator.CommitModelAndAgentsAsync(null, RequireCurrent, Load, Bootstrap);
        Check(serviceIssue is { Length: > 0 } && serviceRegisters == 0 && loads == 1 &&
            bootstraps.Count == 1 && bootstraps[0] is null,
            "installed service Worker degrades to None without a Cortex register or connector effect");
        refreshes = 0; loads = 0; bootstraps.Clear();
        var skipped = await FirstRunSetupCoordinator.OfferOptionalCortexAsync(false, RequireCurrent,
            () => { connects++; return Task.FromResult(true); }, Refresh);
        var inspected = await FirstRunSetupCoordinator.InspectEligibleCortexAsync(
            new[] { "cortex" }, RequireCurrent,
            _ => Task.FromResult(new CortexConnectionReport
            {
                State = "ready", Transport = "cortex-native-pipe",
                NativeIdentity = new(CortexNativeProtocol.Contract, "Pegasus Cortex", "2", "S-1-5-21-fixture",
                    1, "cerebral-fixture", "ready", "sqlite-v2", 4096, 4096, 1, false)
            }));
        await FirstRunSetupCoordinator.CommitModelAndAgentsAsync(inspected.Ready.SingleOrDefault(),
            RequireCurrent, Load, Bootstrap);
        Check(skipped is null && connects == 0 && refreshes == 0 && inspected.Issue is null &&
            inspected.Ready.SequenceEqual(["cortex"]) && loads == 1 && bootstraps.SequenceEqual(["cortex"]),
            "ready saved binding is reused for normal model and agent bootstrap without registration");

        foreach (var mode in new[] { "declined", "unavailable", "pending", "partial" })
        {
            connects = 0; refreshes = 0; loads = 0; bootstraps.Clear();
            async Task<bool> Connect()
            {
                connects++;
                await Task.Yield();
                return mode switch
                {
                    "declined" => false,
                    "unavailable" => throw new InvalidDataException("selected Cortex is unavailable"),
                    "pending" => throw new InvalidOperationException("earlier enrollment is pending"),
                    _ => throw new Cerebral.Client.CortexEnrollmentUncertainException()
                };
            }
            var issue = await FirstRunSetupCoordinator.OfferOptionalCortexAsync(true, RequireCurrent, Connect, Refresh);
            var choices = await FirstRunSetupCoordinator.InspectEligibleCortexAsync(
                Array.Empty<string>(), RequireCurrent,
                _ => throw new InvalidOperationException("no provider should be inspected"));
            await FirstRunSetupCoordinator.CommitModelAndAgentsAsync(choices.Ready.SingleOrDefault(),
                RequireCurrent, Load, Bootstrap);
            Check(issue is { Length: > 0 } && connects == 1 && refreshes == 1 &&
                choices.Ready.Count == 0 && loads == 1 && bootstraps.Count == 1 && bootstraps[0] is null,
                mode + " enrollment still selects None and bootstraps Cerebral once without a second register");
        }

        loads = 0; bootstraps.Clear();
        var failedInspection = await FirstRunSetupCoordinator.InspectEligibleCortexAsync(
            new[] { "cortex" }, RequireCurrent,
            _ => throw new InvalidDataException("synthetic native inspection failure"));
        await FirstRunSetupCoordinator.CommitModelAndAgentsAsync(failedInspection.Ready.SingleOrDefault(),
            RequireCurrent, Load, Bootstrap);
        Check(failedInspection.Issue is { Length: > 0 } && failedInspection.Ready.Count == 0 &&
            loads == 1 && bootstraps.Count == 1 && bootstraps[0] is null,
            "failed saved-provider native inspect is omitted while None still bootstraps normally");

        current = false; loads = 0; bootstraps.Clear(); var commitRefused = false;
        try { await FirstRunSetupCoordinator.CommitModelAndAgentsAsync(null, RequireCurrent, Load, Bootstrap); }
        catch (InvalidOperationException) { commitRefused = true; }
        Check(commitRefused && loads == 0 && bootstraps.Count == 0,
            "owner drift before commit prevents model and agent effects to a replacement Worker");

        current = true; loads = 0; bootstraps.Clear(); commitRefused = false;
        try
        {
            await FirstRunSetupCoordinator.CommitModelAndAgentsAsync(null, RequireCurrent,
                () => { loads++; current = false; return Task.CompletedTask; }, Bootstrap);
        }
        catch (InvalidOperationException) { commitRefused = true; }
        Check(commitRefused && loads == 1 && bootstraps.Count == 0,
            "owner drift after an original model effect prevents agent bootstrap to a replacement Worker");

        current = true; connects = 0; refreshes = 0; var driftRefused = false;
        try
        {
            await FirstRunSetupCoordinator.OfferOptionalCortexAsync(true, RequireCurrent,
                () => { connects++; current = false; throw new InvalidOperationException("original owner changed"); }, Refresh);
        }
        catch (InvalidOperationException) { driftRefused = true; }
        Check(driftRefused && connects == 1 && refreshes == 0,
            "owner drift during optional enrollment aborts instead of masking it as a None fallback");

        Console.WriteLine($"First-run caller coordinator: {passed} deterministic fake-boundary checks passed; no native or model effect.");
        return 0;
    }
}
