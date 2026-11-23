using System.IO;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

// Private qualification only: isolated named objects and unshown dispatchers; no product window,
// Worker transport, model, installed catalog or normal user-state access.
internal static class ConfigurationInstanceBindingChecks
{
    internal static int Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-managed-instance-" + Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "worker");
        Directory.CreateDirectory(worker);
        var checks = 0;
        void Check(bool success, string label)
        {
            if (!success) throw new InvalidOperationException("Configuration instance: " + label);
            checks++;
            Console.WriteLine("PASS Configuration instance: " + label);
        }

        try
        {
            var endpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.Fixture." + Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"));
            var ownerIdA = new string('a', 64);
            var ownerIdB = new string('b', 64);
            ExistingWorkerDesktopOptions Options(string ui, bool changedWorker = false) =>
                ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", endpoint.PipeName,
                    changedWorker ? Guid.NewGuid().ToString("N") : endpoint.InstanceId, worker, ui, "fixture-installation"])!;

            void Verify(string label, string? ownerId, string? incomingId, bool activationExpected, bool changedWorker = false)
            {
                var ui = Path.Combine(root, label);
                var ownerOptions = Options(ui);
                var incomingOptions = Options(ui, changedWorker);
                var ownerBinding = ConfigurationInstanceBinding.Create(ownerOptions, ownerId);
                var incoming = ConfigurationInstanceBinding.Create(incomingOptions, incomingId);
                Check(ownerBinding.ApplicationId == incoming.ApplicationId &&
                    ownerBinding.ApplicationId == ownerOptions.GetApplicationId("Integrator"),
                    label + ": every owner uses the original UI-root singleton key");
                Check(ownerBinding.Identity.Matches(incoming.Identity) == activationExpected,
                    label + ": requested owner identity distinguishes the complete owner");
                Check(!Directory.Exists(ui), label + ": identity preparation creates no UI state");
                VerifyNamedOwner(ownerBinding, incoming, activationExpected, label, Check);
            }

            var legacy = Options(Path.Combine(root, "legacy-identity"));
            Check(ConfigurationInstanceBinding.Create(legacy, null).Identity.Matches(legacy.ApplicationIdentity),
                "legacy requested identity remains unchanged");
            Check(legacy.GetApplicationId("Integrator") != legacy.GetApplicationId("Orchestrator"),
                "Main and Configuration retain separate component keys");
            Verify("legacy-to-legacy", null, null, true);
            Verify("managed-to-same-managed", ownerIdA, ownerIdA, true);
            Verify("managed-to-other-managed", ownerIdA, ownerIdB, false);
            Verify("legacy-to-managed", null, ownerIdA, false);
            Verify("managed-to-legacy", ownerIdA, null, false);
            Verify("managed-wrong-worker", ownerIdA, ownerIdA, false, changedWorker: true);
            foreach (var invalid in new[] { "", " ", "bad\nidentity", new string('x', 257) })
            {
                var refused = false;
                try { _ = ConfigurationInstanceBinding.Create(legacy, invalid); }
                catch (ArgumentException) { refused = true; }
                Check(refused, "invalid internal managed identity is refused");
            }
            Console.WriteLine($"Configuration instance fixture: {checks} checks passed; no product window or Worker calls.");
            return checks;
        }
        finally
        {
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (!ScopedFilePolicy.IsWithin(full, temp) ||
                !Path.GetFileName(full).StartsWith("cerebral-managed-instance-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup root escaped its owned temporary directory.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static void VerifyNamedOwner(ConfigurationInstanceBinding ownerBinding, ConfigurationInstanceBinding incoming,
        bool activationExpected, string label, Action<bool, string> check)
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var activated = new ManualResetEventSlim();
        var activations = 0;
        var ownerThread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var owner = new DesktopApplicationInstance(ownerBinding.ApplicationId, ownerBinding.Identity);
                if (!owner.IsPrimary) throw new InvalidOperationException("The isolated fixture key already has an owner.");
                owner.Listen(dispatcher, () => { Interlocked.Increment(ref activations); activated.Set(); });
                ready.SetResult(dispatcher);
                Dispatcher.Run();
                completed.SetResult();
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
                completed.TrySetException(error);
            }
        }) { IsBackground = true, Name = "Configuration instance fixture owner" };
        ownerThread.SetApartmentState(ApartmentState.STA);
        ownerThread.Start();
        Dispatcher? ownerDispatcher = null;
        try
        {
            ownerDispatcher = ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            using var contender = new DesktopApplicationInstance(incoming.ApplicationId, incoming.Identity);
            check(!contender.IsPrimary, label + ": second launch cannot become another UI-root writer");
            check(contender.OwnerIsCurrent && contender.IdentityMatches == activationExpected,
                label + ": observed owner identity agrees with the request");
            check(contender.RequestActivation() == activationExpected,
                label + ": normal activation accepts only the matching owner");
            if (activationExpected)
                check(activated.Wait(TimeSpan.FromSeconds(5)) && Volatile.Read(ref activations) == 1,
                    label + ": matching owner receives exactly one activation");
            else
                check(!activated.IsSet && Volatile.Read(ref activations) == 0,
                    label + ": mismatched owner receives no activation");
        }
        finally
        {
            if (ownerDispatcher is not null && !ownerDispatcher.HasShutdownStarted)
                ownerDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            if (!ownerThread.Join(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("The isolated instance owner did not finish cleanup.");
            completed.Task.GetAwaiter().GetResult();
        }
    }
}
