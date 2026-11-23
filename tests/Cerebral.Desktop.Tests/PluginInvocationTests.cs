using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

/// <summary>Real compiled native callbacks and Windows catalog locks with an injected command transport.
/// The owned child exits before fixture cleanup; this is not a claim that WPF unloads every assembly.</summary>
internal static class PluginInvocationTests
{
    private static int checks;
    private static readonly List<Assembly> retainedGenerationEvidence = [];
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); checks++; }

    public static string Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Cerebral.PluginInvocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(Environment.ProcessPath!) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--plugin-invocation-child"); start.ArgumentList.Add(root);
        using var child = Process.Start(start) ?? throw new IOException("The invocation fixture child did not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            if (!child.WaitForExit(45_000))
            {
                child.Kill(entireProcessTree: true);
                if (!child.WaitForExit(5_000)) throw new IOException("The exact owned fixture child did not exit.");
                throw new TimeoutException("The native invocation fixture exceeded its bounded runtime.");
            }
            var text = output.GetAwaiter().GetResult(); var error = errors.GetAwaiter().GetResult();
            if (child.ExitCode != 0) throw new InvalidOperationException("Native invocation child failed: " + text + error);
            Check(text.Contains("native callback checks passed", StringComparison.Ordinal), "The child reported its actual checks.");
            return text.Trim() + "; confirmed child exit and owned fixture cleanup";
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5_000); }
            var target = Path.GetFullPath(root);
            if (!child.HasExited || Path.GetDirectoryName(target) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
                throw new IOException("The exact child/root cleanup boundary could not be verified.");
            CerebralPluginPackage.RejectLinks(target);
            Directory.Delete(target, true);
        }
    }

    public static void RunChild(string root)
    {
        var frame = new DispatcherFrame();
        Exception? failure = null;
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { await RunCases(root); }
            catch (Exception error) { failure = error; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        if (failure is not null) throw new InvalidOperationException("Native callback fixture failed.", failure);
        Console.WriteLine($"{checks} native callback checks passed; real compiled plugin/dispatcher/file locks; command transport injected; ALC collection not asserted");
    }

    private static async Task RunCases(string root)
    {
        Check(typeof(DesktopPluginRuntime).GetMethod(nameof(DesktopPluginRuntime.LoadEnabled), Type.EmptyTypes) is not null,
            "The original parameterless host refresh entry remains binary-compatible.");
        foreach (var lane in new[] { "action", "mount-action", "text-action", "mount-text", "submission", "input", "prepare", "command", "workspace", "settings-open", "settings-save" })
        {
            using var fixture = new Fixture(Path.Combine(root, lane), lane);
            var callback = await fixture.Start(lane);
            await fixture.WaitStarts(1);
            var selected = fixture.Runtime.GetLoadedPackage(Fixture.Id)!.Value;
            var blocked = false;
            try { fixture.Catalog.SetEnabled(Fixture.Id, false); } catch (IOException) { blocked = true; }
            Check(blocked && fixture.Catalog.List().Single().Enabled, lane + ": actual mutation is excluded without changing enablement.");
            blocked = false;
            try { fixture.Runtime.ValidateCanReopenExact(Fixture.Id, selected.Directory, selected.Version); }
            catch (InvalidOperationException) { blocked = true; }
            Check(blocked && fixture.Count("dispose") == 0, lane + ": reopen preflight preserves the running instance.");
            fixture.Runtime.Disable(Fixture.Id);
            Check(fixture.Cancellations > 0, lane + ": lifetime cancellation reaches the callback.");
            Check(fixture.Runtime.GetLoadedPackage(Fixture.Id) is null && fixture.Count("dispose") == 0,
                lane + ": detach occurs immediately but disposal waits for the actual callback.");
            fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 1 && fixture.Runtime.Status.Single().Error?.Contains("Stopping", StringComparison.Ordinal) == true,
                lane + ": refresh cannot load a second instance before drain.");
            var starts = fixture.Starts;
            if (lane is not "settings-open" and not "settings-save")
            {
                var staleRejected = await Rejected(() => fixture.InvokeCaptured());
                Check(staleRejected && fixture.Starts == starts, lane + ": a captured old host delegate cannot reenter native code.");
            }
            fixture.Release();
            if (lane == "settings-save")
            {
                var result = await fixture.Save!;
                Check(result.State == DesktopOperationState.Uncertain, "A dispatched retired settings save remains uncertain.");
            }
            else Check(await Rejected(() => callback), lane + ": a late reply cannot be published as success.");
            await WaitUntil(() => fixture.Count("dispose") == 1);
            Check(fixture.Events().IndexOf("callback-return") < fixture.Events().IndexOf("dispose"), lane + ": callback returns before native Dispose.");
            fixture.Catalog.SetEnabled(Fixture.Id, false);
            fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 1, lane + ": publication resumes after drain and retains explicit disabled state.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "parallel"), "action"))
        {
            var first = await fixture.Start("action"); var second = fixture.InvokeCaptured();
            await fixture.WaitStarts(2);
            fixture.Runtime.Disable(Fixture.Id);
            fixture.ReleaseOne();
            Check(await Rejected(() => first), "The first concurrent retired result is rejected.");
            Check(fixture.Count("dispose") == 0, "One completed callback cannot dispose an instance with another callback pending.");
            var blocked = false;
            try { fixture.Catalog.SetEnabled(Fixture.Id, false); } catch (IOException) { blocked = true; }
            Check(blocked, "The final active callback retains publication exclusion.");
            fixture.Release();
            Check(await Rejected(() => second), "The second concurrent retired result is rejected.");
            await WaitUntil(() => fixture.Count("dispose") == 1);
        }

        using (var fixture = new Fixture(Path.Combine(root, "fault"), "action"))
        {
            var callback = await fixture.Start("action"); await fixture.WaitStarts(1);
            fixture.Release(fail: true);
            Check(await Rejected(() => callback), "A native fault is surfaced rather than presented as completion.");
            fixture.Catalog.SetEnabled(Fixture.Id, false);
            fixture.Runtime.LoadEnabled();
            Check(fixture.Count("dispose") == 1, "A fault releases its callback lease and permits ordinary disable.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "dependency"), "action", dependency: true))
        {
            var callback = await fixture.Start("action"); await fixture.WaitStarts(1);
            fixture.Runtime.Disable(Fixture.ProviderId);
            Check(fixture.Count("dispose") == 0 && fixture.ProviderCount("dispose") == 0,
                "Retiring a provider detaches its dependent but waits for dependent callback cleanup.");
            fixture.Release(); Check(await Rejected(() => callback), "The dependent's retired result is rejected.");
            await WaitUntil(() => fixture.ProviderCount("dispose") == 1);
            Check(fixture.Order.IndexOf(Fixture.Id + ":dispose-return") < fixture.Order.IndexOf(Fixture.ProviderId + ":dispose-enter"),
                "The shared actual observation proves dependent Dispose returned before provider Dispose entered.");
        }

        foreach (var lane in new[] { "render", "decoration", "presentation" })
        {
            using var fixture = new Fixture(Path.Combine(root, lane), lane);
            var view = fixture.Runtime.Present(new("message", fixture.Binding, "assistant", "fixture", DateTimeOffset.UtcNow));
            Check(view is null, lane + ": a reentrant retirement cannot return a stale presentation.");
            await WaitUntil(() => fixture.Count("dispose") == 1);
            var events = fixture.Events();
            Check(events.IndexOf("render-after-disable") < events.IndexOf("dispose"),
                lane + ": synchronous native code finishes before its reentrant retirement disposes it.");
            fixture.Catalog.SetEnabled(Fixture.Id, false); fixture.Runtime.LoadEnabled();
        }

        using (var fixture = new Fixture(Path.Combine(root, "normal-reopen"), "action"))
        {
            var selected = fixture.Runtime.GetLoadedPackage(Fixture.Id)!.Value;
            fixture.Runtime.ValidateCanReopenExact(Fixture.Id, selected.Directory, selected.Version);
            Check(fixture.Runtime.ReopenExact(Fixture.Id, selected.Directory, selected.Version).InstanceRecreated,
                "An idle exact instance still reopens normally.");
            Check(fixture.Count("initialize") == 2 && fixture.Count("dispose") == 1, "Idle reopen creates exactly one fresh native instance.");
        }


        using (var fixture = new Fixture(Path.Combine(root, "success"), "action"))
        {
            var callback = await fixture.Start("action"); await fixture.WaitStarts(1);
            fixture.Release(); await callback;
            Check(fixture.Count("callback-return") == 1 && fixture.Count("dispose") == 0,
                "An ordinary successful callback keeps the active native instance.");
            fixture.Catalog.SetEnabled(Fixture.Id, false); fixture.Runtime.LoadEnabled();
            Check(fixture.Count("dispose") == 1, "Successful settlement frees publication and permits disable.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "pre-cancel"), "action"))
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check(await Rejected(() => fixture.Runtime.ComposerActions.Single().ExecuteAsync(cancelled.Token)) && fixture.Starts == 0,
                "A pre-cancelled operation never enters native callback code.");
            fixture.Catalog.SetEnabled(Fixture.Id, false); fixture.Runtime.LoadEnabled();
            Check(fixture.Count("dispose") == 1, "Pre-dispatch cancellation releases its catalog observation.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "saved-disabled"), "action"))
        {
            var oldAction = fixture.Runtime.ComposerActions.Single();
            fixture.Catalog.SetEnabled(Fixture.Id, false);
            Check(await Rejected(() => oldAction.ExecuteAsync(CancellationToken.None)) && fixture.Starts == 0,
                "Saved enablement is checked before native entry, even before watcher refresh.");
            fixture.Runtime.LoadEnabled();
            Check(fixture.Count("dispose") == 1, "Explicit refresh retires the saved-disabled instance.");
        }


        foreach (var lane in new[] { "action-cancel-reentry", "action-dispose-reentry" })
        {
            using var fixture = new Fixture(Path.Combine(root, lane), lane, dependency: true);
            var callback = await fixture.Start("action"); await fixture.WaitStarts(1);
            fixture.Runtime.Disable(Fixture.Id);
            Check(fixture.ProviderCount("dispose") == 0, lane + ": reentry cannot dispose the provider before dependent native work returns.");
            fixture.Release(); Check(await Rejected(() => callback), lane + ": retired output is rejected.");
            await WaitUntil(() => fixture.ProviderCount("dispose") == 1);
            Check(fixture.Order.IndexOf(Fixture.Id + ":dispose-return") < fixture.Order.IndexOf(Fixture.ProviderId + ":dispose-enter"),
                lane + ": dependent Dispose returns before provider Dispose enters under reentry.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "dispose-failure"), "action-dispose-throw", dependency: true))
        {
            fixture.Runtime.Disable(Fixture.ProviderId);
            await WaitUntil(() => fixture.Runtime.Status.Count == 2 && fixture.Runtime.Status.All(item => item.Error?.Contains("Native cleanup failed", StringComparison.Ordinal) == true));
            Check(fixture.Count("dispose") == 1 && fixture.ProviderCount("dispose") == 0,
                "Failed dependent native cleanup blocks provider cleanup.");
            var denied = false;
            try { fixture.Catalog.SetEnabled(Fixture.Id, false); } catch (IOException) { denied = true; }
            Check(denied, "Failed retirement retains a real catalog reader and excludes subsequent publication.");
            fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 1 && fixture.Count("dispose") == 1, "Failed cleanup is not retried or replaced.");
            fixture.Runtime.Dispose();
            using var another = fixture.AnotherRuntime(); another.LoadEnabled();
            Check(fixture.Count("initialize") == 1 && fixture.ProviderCount("initialize") == 1 &&
                another.Status.All(item => !item.Loaded), "Recreating the runtime cannot forget failed consumer/provider retirement.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "partial-failure"), "initialize-fail-dispose-throw", expectFailure: true))
        {
            await WaitUntil(() => fixture.Count("dispose") == 1);
            Check(fixture.Count("initialize") == 1 && fixture.Count("dispose") == 1 && fixture.Runtime.GetLoadedPackage(Fixture.Id) is null,
                "Partial initialization cleanup is real and remains unpublished.");
            fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 1 && fixture.Count("dispose") == 1 &&
                fixture.Runtime.Status.Single().Error?.Contains("Native cleanup failed", StringComparison.Ordinal) == true,
                "Failed partial-load cleanup blocks native retry.");
            using var another = fixture.AnotherRuntime(); another.LoadEnabled();
            Check(fixture.Count("initialize") == 1 && another.Status.Single().Loaded == false,
                "Partial-load quarantine survives a new runtime on the same catalog.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "progress-dispose"), "action", disposeDuringLoad: true))
            Check(fixture.Count("initialize") == 0 && fixture.Runtime.GetLoadedPackage(Fixture.Id) is null,
                "Synchronous loading progress disposal prevents construction and publication.");

        using (var fixture = new Fixture(Path.Combine(root, "initialize-dispose"), "initialize-dispose-runtime"))
        {
            await WaitUntil(() => fixture.Count("dispose") == 1);
            Check(fixture.Count("initialize") == 1 && fixture.Runtime.GetLoadedPackage(Fixture.Id) is null,
                "Disposal during initialization cleans the unpublished native instance after the callback returns.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "runtime-recreate"), "action"))
        {
            var callback = await fixture.Start("action"); await fixture.WaitStarts(1);
            fixture.Runtime.Dispose();
            using var another = fixture.AnotherRuntime(); another.LoadEnabled();
            Check(fixture.Count("initialize") == 1 && another.GetLoadedPackage(Fixture.Id) is null,
                "Runtime recreation cannot overlap an old retiring callback.");
            fixture.Release(); Check(await Rejected(() => callback), "Retired callback remains rejected after runtime disposal.");
            await WaitUntil(() => fixture.Count("dispose") == 1);
            another.LoadEnabled();
            Check(fixture.Count("initialize") == 2, "Another runtime can load a fresh instance after confirmed cleanup.");
        }


        using (var fixture = new Fixture(Path.Combine(root, "view-dispose"), "view-dispose-runtime", expectFailure: true))
        {
            await WaitUntil(() => fixture.Count("dispose") == 1);
            Check(fixture.Count("initialize") == 1 && fixture.Runtime.GetLoadedPackage(Fixture.Id) is null,
                "Disposal during a native view factory cannot publish the instance.");
            Check(fixture.Count("second-view") == 0, "A cancelled constructor never calls the next native view factory.");
            Check(fixture.Events().IndexOf("view-return") < fixture.Events().IndexOf("dispose"),
                "The native view factory returns before partial-load native cleanup.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "loaded-progress-dispose"), "action", disposeWhenLoaded: true))
        {
            await WaitUntil(() => fixture.Count("dispose") == 1);
            Check(fixture.Count("initialize") == 1 && fixture.Runtime.GetLoadedPackage(Fixture.Id) is null,
                "Disposal during the loaded progress notification retires the just-published instance exactly once.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "busy-retirement"), "action"))
        {
            using (var writer = DesktopPluginPublicationLease.TryAcquire(fixture.Catalog.RootDirectory, true))
            {
                Check(writer is not null, "The real publisher owns the test root.");
                fixture.Runtime.Disable(Fixture.Id);
                Check(fixture.Count("dispose") == 0, "Native Dispose is deferred while an external publication lease is held.");
            }
            await WaitUntil(() => fixture.Count("dispose") == 1);
            Check(fixture.Count("dispose") == 1, "Deferred native cleanup resumes after publication releases.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "invalid-retirement-store"), "action"))
        {
            File.WriteAllText(Path.Combine(fixture.Catalog.RootDirectory, ".settings", ".catalog-publication.lock"), "invalid");
            fixture.Runtime.Dispose();
            Check(fixture.Count("dispose") == 0 && fixture.Runtime.Status.Single().Error?.Contains("Native cleanup failed", StringComparison.Ordinal) == true,
                "Malformed lease storage exposes failed retirement without unwinding host shutdown or entering native Dispose.");
        }


        foreach (var lane in new[] { "initialize-retire-provider", "view-retire-provider" })
        {
            using var fixture = new Fixture(Path.Combine(root, lane), lane, dependency: true, expectFailure: true);
            await WaitUntil(() => fixture.ProviderCount("dispose") == 1);
            Check(fixture.Runtime.GetLoadedPackage(Fixture.Id) is null && fixture.Count("dispose") == 1,
                lane + ": changing the resolved provider prevents publication of its constructing consumer.");
            Check(fixture.Order.IndexOf(Fixture.Id + ":dispose-return") < fixture.Order.IndexOf(Fixture.ProviderId + ":dispose-enter"),
                lane + ": partial consumer cleanup completes before provider native disposal enters.");
            var events = fixture.Events();
            Check(events.IndexOf(lane.StartsWith("initialize", StringComparison.Ordinal) ? "initialize-return" : "view-return") < events.IndexOf("dispose"),
                lane + ": the constructing native callback returns before its cleanup.");
        }
        using (var fixture = new Fixture(Path.Combine(root, "progress-retire-provider"), "action", dependency: true,
            expectFailure: true, retireProviderDuringLoad: true))
        {
            await WaitUntil(() => fixture.ProviderCount("dispose") == 1);
            Check(fixture.Count("initialize") == 0 && fixture.Runtime.GetLoadedPackage(Fixture.Id) is null,
                "A loading progress callback that retires the resolved provider prevents native construction.");
        }
        using (var fixture = new Fixture(Path.Combine(root, "getter-dispose"), "getter-dispose-runtime", expectFailure: true))
        {
            await WaitUntil(() => fixture.Count("dispose") == 1);
            Check(fixture.Count("actions-read") == 0 && fixture.Runtime.GetLoadedPackage(Fixture.Id) is null,
                "Cancellation inside a contribution getter stops subsequent native getters.");
        }
        foreach (var lane in new[] { "settings-editor-dispose-throw", "settings-open-invalid-dispose-throw" })
        {
            using var fixture = new Fixture(Path.Combine(root, lane), lane, dependency: true);
            if (lane == "settings-editor-dispose-throw")
            {
                var callback = await fixture.Start("settings-save"); await fixture.WaitStarts(1);
                fixture.Runtime.Disable(Fixture.ProviderId); fixture.Release(); await callback;
            }
            else
            {
                Check(await Rejected(async () => { await fixture.Start("settings-save"); }),
                    "A malformed native settings view rejects the opening.");
                fixture.Runtime.Disable(Fixture.ProviderId);
            }
            await WaitUntil(() => fixture.Runtime.Status.Count == 2 &&
                fixture.Runtime.Status.All(item => item.Error?.Contains("Native cleanup failed", StringComparison.Ordinal) == true));
            Check(fixture.Count("editor-dispose") == 1 && fixture.Count("dispose") == 0 && fixture.ProviderCount("dispose") == 0,
                lane + ": failed editor cleanup protects both the root plugin and its provider.");
            var denied = false;
            try { fixture.Catalog.SetEnabled(Fixture.Id, false); } catch (IOException) { denied = true; }
            Check(denied, lane + ": failed editor disposal retains the real catalog reader.");
            fixture.Runtime.Dispose();
            using var another = fixture.AnotherRuntime(); another.LoadEnabled();
            Check(fixture.Count("initialize") == 1 && fixture.Count("editor-dispose") == 1 && another.Status.All(item => !item.Loaded),
                lane + ": a new runtime cannot retry failed editor cleanup or recreate its owner.");
        }


        using (var fixture = new Fixture(Path.Combine(root, "stable-dependencies"), "action", dependency: true))
        {
            for (var refresh = 0; refresh < 12; refresh++) fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 1 && fixture.ProviderCount("initialize") == 1,
                "Repeated unchanged refresh preserves both native consumer and provider instances.");
            fixture.RefreshProvider();
            var before = fixture.Starts;
            var action = fixture.Runtime.ComposerActions.Single();
            Check(await Rejected(() => action.ExecuteAsync(CancellationToken.None)) && fixture.Starts == before,
                "A selected provider change rejects an old consumer callback before its own selection changes.");
            fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 2 && fixture.ProviderCount("initialize") == 2,
                "One reviewed provider generation change reconstructs its bound consumer exactly once.");
            for (var refresh = 0; refresh < 12; refresh++) fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 2 && fixture.ProviderCount("initialize") == 2,
                "The new resolved provider remains stable on subsequent refresh.");
        }
        using (var fixture = new Fixture(Path.Combine(root, "optional-availability"), "action", dependency: true,
            optionalDependency: true, providerEnabled: false))
        {
            for (var refresh = 0; refresh < 12; refresh++) fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 1 && fixture.ProviderCount("initialize") == 0,
                "An absent optional provider does not recreate its native consumer.");
            fixture.Catalog.SetEnabled(Fixture.ProviderId, true); fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 2 && fixture.ProviderCount("initialize") == 1,
                "A newly available optional provider reconstructs its consumer within the same refresh.");
            fixture.Catalog.SetEnabled(Fixture.ProviderId, false); fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 3 && fixture.ProviderCount("dispose") == 1,
                "Removing a bound optional provider reconstructs its consumer with an absent binding.");
            fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 3, "The absent binding remains stable after removal.");
        }
        using (var fixture = new Fixture(Path.Combine(root, "optional-incompatible"), "action", dependency: true,
            optionalDependency: true, dependencyVersion: "2.0.0"))
        {
            fixture.Runtime.Disable(Fixture.ProviderId);
            Check(fixture.Count("dispose") == 0, "An incompatible optional provider was never bound and cannot retire its consumer.");
            fixture.Runtime.LoadEnabled(); fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 1, "An incompatible optional provider does not recreate the consumer.");
        }


        using (var fixture = new Fixture(Path.Combine(root, "same-directory-budget"), "action"))
        {
            var selected = fixture.Runtime.GetLoadedPackage(Fixture.Id)!.Value;
            for (var generation = 1; generation < DesktopPluginLoadLedger.MaximumPerPlugin; generation++)
                Check(fixture.Runtime.ReopenExact(Fixture.Id, selected.Directory, selected.Version).InstanceRecreated,
                    "A reserved exact reopen admits a fresh native generation.");
            var before = fixture.Count("dispose");
            Check(DesktopPluginLoadLedger.Shared.Counts(fixture.Catalog.RootDirectory, Fixture.Id).Generations == 8,
                "Same-directory native loads remain counted while their actual assemblies are retained.");
            var capacityRejected = false;
            try { fixture.Runtime.ValidateCanReopenExact(Fixture.Id, selected.Directory, selected.Version); }
            catch (InvalidOperationException) { capacityRejected = true; }
            Check(capacityRejected, "Read-only exact-reopen preflight reports the per-plugin lifetime limit.");
            capacityRejected = false;
            try { fixture.Runtime.ReopenExact(Fixture.Id, selected.Directory, selected.Version); }
            catch (InvalidOperationException) { capacityRejected = true; }
            Check(capacityRejected && fixture.Count("dispose") == before && fixture.Runtime.GetLoadedPackage(Fixture.Id) is not null,
                "Rejected exact reopen preserves the working instance before detach.");
            capacityRejected = false;
            try { using var ticket = fixture.Runtime.ReserveForPublication(fixture.ConsumerManifestPath, true); }
            catch (InvalidOperationException) { capacityRejected = true; }
            Check(capacityRejected && fixture.Catalog.List().Single().Directory == selected.Directory,
                "Same-process publication admission rejects before immutable selection changes.");
            fixture.Runtime.LoadEnabled();
            Check(fixture.Count("initialize") == 8, "An unchanged refresh at the limit requires no new native credit.");
            fixture.Runtime.Dispose();
            using var another = fixture.AnotherRuntime();
            capacityRejected = false;
            try { another.LoadEnabled(); } catch (InvalidOperationException) { capacityRejected = true; }
            Check(capacityRejected && fixture.Count("initialize") == 8,
                "Recreating the runtime cannot reset observed process lifetime charges.");
        }
        using (var fixture = new Fixture(Path.Combine(root, "rejected-native-budget"), "initialize-fail", expectFailure: true))
        {
            await WaitUntil(() => fixture.Count("dispose") == 1);
            for (var generation = 1; generation < DesktopPluginLoadLedger.MaximumPerPlugin; generation++)
            {
                fixture.Runtime.LoadEnabled();
                var expected = generation + 1;
                await WaitUntil(() => fixture.Count("dispose") == expected);
            }
            Check(fixture.Count("initialize") == 8 && fixture.Count("dispose") == 8,
                "Partial native initialization is cleaned but remains charged while assemblies are retained.");
            var capacityRejected = false;
            try { fixture.Runtime.LoadEnabled(); } catch (InvalidOperationException) { capacityRejected = true; }
            Check(capacityRejected && fixture.Count("initialize") == 8, "Repeated rejected construction is bounded before a ninth assembly load.");
        }
        using (var fixture = new Fixture(Path.Combine(root, "publication-ticket"), "action", dependency: true))
        {
            fixture.RefreshProviderSource();
            using var ticket = fixture.Runtime.ReserveForPublication(fixture.ProviderManifestPath, true);
            var count = DesktopPluginLoadLedger.Shared.Counts(fixture.Catalog.RootDirectory);
            Check(count.Reserved == 2, "Publication reserves the provider and exact affected consumer closure.");
            fixture.Catalog.RefreshReviewed(fixture.ProviderManifestPath);
            fixture.Runtime.LoadEnabled(ticket);
            Check(fixture.Count("initialize") == 2 && fixture.ProviderCount("initialize") == 2 &&
                DesktopPluginLoadLedger.Shared.Counts(fixture.Catalog.RootDirectory).Reserved == 0,
                "Published immutable fingerprints bind and convert the exact closure credits into native generations.");
            ticket.Dispose();
            Check(DesktopPluginLoadLedger.Shared.Counts(fixture.Catalog.RootDirectory).Generations == 4,
                "Disposing a consumed publication ticket cannot refund native assembly generations.");
        }
        using (var fixture = new Fixture(Path.Combine(root, "changed-ticket"), "action"))
        {
            var plan = new DesktopPluginLoadPlan(Fixture.Id, fixture.Catalog.List().Single().Directory, "1.0.0",
                NativeDesktopPluginPackage.ComputeFingerprint(fixture.Catalog.List().Single().ManifestPath));
            using var ticket = DesktopPluginLoadLedger.Shared.Reserve(fixture.Catalog.RootDirectory, [plan]);
            var capacityRejected = false;
            try { ticket.Bind(Path.Combine(root, "wrong-root"), [plan]); } catch (InvalidOperationException) { capacityRejected = true; }
            Check(capacityRejected, "A reservation cannot be used against another catalog root.");
            capacityRejected = false;
            try { ticket.Bind(fixture.Catalog.RootDirectory, [plan with { Fingerprint = "changed" }]); } catch (InvalidOperationException) { capacityRejected = true; }
            Check(capacityRejected, "A changed exact file-set fingerprint rejects the whole admission plan.");
            ticket.Dispose(); ticket.Dispose();
            Check(DesktopPluginLoadLedger.Shared.Counts(fixture.Catalog.RootDirectory).Reserved == 0,
                "Unused reservations return their credits exactly once.");
            capacityRejected = false;
            try { ticket.Bind(fixture.Catalog.RootDirectory, [plan]); } catch (InvalidOperationException) { capacityRejected = true; }
            Check(capacityRejected, "A disposed reservation cannot be reused.");
        }

        using (var fixture = new Fixture(Path.Combine(root, "copy-fingerprint"), "action"))
        {
            var selected = fixture.Runtime.GetLoadedPackage(Fixture.Id)!.Value;
            using var ticket = fixture.Runtime.ReserveForPublication(fixture.ConsumerManifestPath, true);
            var fingerprint = ticket.CandidateFingerprint(Fixture.Id);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.ConsumerManifestPath)!, "changed.txt"), "source changed after review");
            var refused = false;
            try { fixture.Catalog.RefreshReviewed(fixture.ConsumerManifestPath, fingerprint); }
            catch (IOException) { refused = true; }
            Check(refused && fixture.Catalog.List().Single().Directory == selected.Directory && fixture.Count("dispose") == 0,
                "Copy-time fingerprint mismatch preserves both original code selection and active instance.");
            var fresh = new DesktopPluginCatalog(Path.Combine(root, "new-fingerprint-catalog"));
            refused = false;
            try { fresh.Install(fixture.ConsumerManifestPath, fingerprint); } catch (IOException) { refused = true; }
            Check(refused && fresh.List().Count == 0, "A changed reviewed source cannot publish a first installation either.");
        }
        using (var fixture = new Fixture(Path.Combine(root, "revocation-at-limit"), "action", dependency: true, optionalDependency: true))
        {
            var selected = fixture.Runtime.GetLoadedPackage(Fixture.Id)!.Value;
            for (var generation = 1; generation < DesktopPluginLoadLedger.MaximumPerPlugin; generation++)
                fixture.Runtime.ReopenExact(Fixture.Id, selected.Directory, selected.Version);
            var notifications = 0;
            fixture.Runtime.Changed += (_, _) => notifications++;
            fixture.Catalog.SetEnabled(Fixture.ProviderId, false);
            var refused = false;
            try { fixture.Runtime.LoadEnabled(); } catch (InvalidOperationException) { refused = true; }
            Check(refused && fixture.Runtime.GetLoadedPackage(Fixture.ProviderId) is null &&
                fixture.Runtime.GetLoadedPackage(Fixture.Id) is null && fixture.Runtime.ComposerActions.Count == 0,
                "Owner provider revocation detaches native contributions even when a null-provider replacement is over quota.");
            Check(notifications > 0 && fixture.Runtime.Status.Any(item => item.Error?.Contains("Exit and restart", StringComparison.Ordinal) == true),
                "A quota-limited revoked refresh notifies the host and exposes its restart diagnostic.");
        }

        await CheckAdmissionRaces(root);

        // The async lease is intentionally not thread-affine. Exercise its release on a real pool thread.
        var leaseRoot = Path.Combine(root, "cross-thread");
        var lifetime = new DesktopPluginInvocationLifetime();
        var scope = lifetime.Enter(DesktopPluginPublicationLease.TryAcquireIndependentReader(leaseRoot)!);
        var drained = lifetime.Retire();
        Check(!drained.IsCompleted && lifetime.ActiveCount == 1, "Retirement seals admission while one scope remains.");
        await Task.Run(scope.Dispose); scope.Dispose();
        Check(drained.IsCompleted && lifetime.ActiveCount == 0, "Cross-thread release drains once; repeated Dispose is harmless.");
        using (var writer = DesktopPluginPublicationLease.TryAcquire(leaseRoot, true))
            Check(writer is not null, "Cross-thread release frees the actual OS lock.");
        var rejectedScope = DesktopPluginPublicationLease.TryAcquireIndependentReader(leaseRoot)!;
        var rejected = false;
        try { lifetime.Enter(rejectedScope); } catch (InvalidOperationException) { rejected = true; }
        using (var writer = DesktopPluginPublicationLease.TryAcquire(leaseRoot, true))
            Check(rejected && writer is not null, "A sealed lifetime rejects admission and releases the rejected observation.");
    }


    private static async Task CheckAdmissionRaces(string root)
    {
        var ledger = DesktopPluginLoadLedger.Shared;
        var plan = new DesktopPluginLoadPlan("budget.race", root, "1.0.0", "reservation-only");
        var perKey = Path.Combine(root, "per-key-budget");
        var held = new List<DesktopPluginLoadReservation>();
        try
        {
            for (var slot = 0; slot < DesktopPluginLoadLedger.MaximumPerPlugin - 1; slot++) held.Add(ledger.Reserve(perKey, [plan]));
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Attempt(perKey, plan))));
            Check(results.Count(item => item is not null) == 1, "Two runtimes racing the last per-key credit admit exactly one reservation.");
            foreach (var result in results) result?.Dispose();
        }
        finally { foreach (var item in held) item.Dispose(); }
        var before = ledger.Counts();
        var remaining = DesktopPluginLoadLedger.MaximumPerProcess - before.Generations - before.Reserved - 1;
        using (var filler = ledger.Reserve(Path.Combine(root, "global-filler"),
            Enumerable.Range(0, remaining).Select(index => plan with { Id = "budget.fill-" + index }).ToArray()))
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
                Task.Run(() => Attempt(Path.Combine(root, "global-race-" + index), plan))));
            Check(results.Count(item => item is not null) == 1, "Two different catalog roots racing the last global credit admit exactly one reservation.");
            foreach (var result in results) result?.Dispose();
        }
        Check(ledger.Counts().Reserved == before.Reserved, "Both race lanes release all unused credits without changing observed generations.");
        DesktopPluginLoadReservation? Attempt(string catalogRoot, DesktopPluginLoadPlan candidate)
        { try { return ledger.Reserve(catalogRoot, [candidate]); } catch (InvalidOperationException) { return null; } }
    }

    private static async Task<bool> Rejected(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or IOException or InvalidDataException) { return true; }
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class Entry : IDesktopTextEntry
    {
        public string Id => "composer"; public string Purpose => "fixture"; public string Text => "fixture";
        public int SelectionStart => 0; public int SelectionLength => 0;
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Id = "invocation.fixture";
        internal const string ProviderId = "invocation.provider";
        private readonly string root;
        private readonly List<TaskCompletionSource> releases = [];
        private readonly Entry entry = new();
        private Func<Task>? captured;
        private IDesktopConversationSettingsEditor? editor;
        internal Task<DesktopOperationResult>? Save;
        public List<string> Order { get; } = [];
        public int Starts { get; private set; }
        public int Cancellations { get; private set; }
        public DesktopPluginCatalog Catalog { get; }
        public DesktopPluginRuntime Runtime { get; }
        public DesktopConversationBinding Binding { get; } = new("fixture-generation", "saved-chat");

        public Fixture(string root, string lane, bool dependency = false, bool disposeDuringLoad = false, bool expectFailure = false, bool disposeWhenLoaded = false, bool retireProviderDuringLoad = false,
            bool optionalDependency = false, bool providerEnabled = true, string dependencyVersion = "1.0.0")
        {
            this.root = root;
            Catalog = new(Path.Combine(root, "catalog"));
            if (dependency) Install(ProviderId, [], providerEnabled);
            Install(Id, dependency ? [new() { Id = ProviderId, ExactVersion = dependencyVersion, Optional = optionalDependency }] : []);
            var draft = new DesktopDraftSnapshot(Binding, 0, "");
            Task<DesktopOperationResult> Done() => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
            var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
                () => new(Binding, lane, null, null, false), () => draft,
                (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { },
                pluginDataRoot: Path.Combine(root, "data"), serviceCommand: Command);
            Runtime = new(Catalog, host, Dispatcher.CurrentDispatcher);
            if (disposeDuringLoad || disposeWhenLoaded) Runtime.LoadProgress += (_, progress) => { if (progress.State == (disposeWhenLoaded ? "loaded" : "loading")) Runtime.Dispose(); };
            if (retireProviderDuringLoad) Runtime.LoadProgress += (_, progress) =>
            { if (progress.Id == Id && progress.State == "loading") Runtime.Disable(ProviderId); };
            Runtime.LoadEnabled();
            if (!disposeDuringLoad && !disposeWhenLoaded && !expectFailure && lane != "initialize-dispose-runtime") Check(Runtime.Status.All(item => item.Loaded), lane + ": the compiled native fixture actually loads.");
        }

        public string ConsumerManifestPath => Path.Combine(root, Id, DesktopPluginCatalog.ManifestFileName);
        public string ProviderManifestPath => Path.Combine(root, ProviderId, DesktopPluginCatalog.ManifestFileName);
        public void RefreshProvider() => Catalog.RefreshReviewed(ProviderManifestPath);
        public void RefreshProviderSource() => File.WriteAllText(Path.Combine(root, ProviderId, "reviewed.txt"), "reviewed fixture generation");

        private void Install(string id, List<DesktopPluginDependency> dependencies, bool enabled = true)
        {
            var source = Path.Combine(root, id); Directory.CreateDirectory(source);
            File.Copy(typeof(InvocationFixturePlugin).Assembly.Location, Path.Combine(source, "Fixture.dll"));
            File.WriteAllText(Path.Combine(source, DesktopPluginCatalog.ManifestFileName), JsonSerializer.Serialize(new DesktopPluginManifest {
                SchemaVersion = 2, Id = id, Name = "Invocation fixture", Version = "1.0.0", Assembly = "Fixture.dll",
                EntryType = typeof(InvocationFixturePlugin).FullName!, Dependencies = dependencies
            }, CerebralJson.Options));
            Catalog.Install(Path.Combine(source, DesktopPluginCatalog.ManifestFileName)); Catalog.SetEnabled(id, enabled);
        }

        private async Task<DesktopServiceCommandResult> Command(string operation, object args, CancellationToken token)
        {
            if (operation == "fixture.attach")
            {
                retainedGenerationEvidence.Add(args.GetType().Assembly); // Deliberate roots prove no Unload-only refund; child exit owns cleanup.
                args.GetType().GetProperty("Retire")!.SetValue(args, new Action(() => Runtime.Disable(Id)));
                args.GetType().GetProperty("RetireProvider")!.SetValue(args, new Action(() => Runtime.Disable(ProviderId)));
                args.GetType().GetProperty("DisposeRuntime")!.SetValue(args, new Action(() => Runtime.Dispose()));
                args.GetType().GetProperty("Observe")!.SetValue(args, new Action<string>(Order.Add));
                return new(true, JsonSerializer.SerializeToElement(new { }));
            }
            if (operation != "fixture.callback") throw new InvalidOperationException("Unexpected fixture command.");
            Starts++;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releases.Add(release);
            using var registration = token.Register(() => Cancellations++);
            await release.Task; // Deliberately ignores cancellation to test the host's late-result boundary.
            return new(true, JsonSerializer.SerializeToElement(new { }));
        }

        public async Task<Task> Start(string lane)
        {
            var request = new DesktopInputRequest(new(Binding, "fixture", null, null, false), new(Binding, "request", "fixture"), false);
            captured = null;
            // Capture actual host delegates now; later tests must not resolve a replacement's contributions.
            if (lane == "action") { var action = Runtime.ComposerActions.Single(); captured = () => action.ExecuteAsync(CancellationToken.None); }
            if (lane == "mount-action") { var action = Runtime.ActionsFor(DesktopMountSlots.WorkspaceActions).Single(); captured = () => action.ExecuteAsync(CancellationToken.None); }
            if (lane is "text-action" or "mount-text") { var action = Runtime.TextEntryActions.Single(); captured = () => action.ExecuteAsync(entry, CancellationToken.None); }
            if (lane == "submission") { var item = Runtime.TextEntrySubmissionPreparations.Single(); captured = () => item.PrepareAsync(entry, CancellationToken.None); }
            if (lane is "input" or "prepare") { var item = Runtime.InputModes.Single(); captured = lane == "input" ? () => item.InterceptAsync(request, CancellationToken.None) : () => item.PrepareAsync!(CancellationToken.None); }
            if (lane == "command") { var item = Runtime.ComposerCommands.Single(); captured = () => item.ExecuteAsync(request, CancellationToken.None); }
            if (lane == "workspace") { var item = Runtime.ConversationWorkspaces.Single(); captured = () => item.CreateViewAsync(
                new(new(Binding, "fixture", null, null, false), new(Binding.SessionId!, Id, "workspace", "resource", 1, DateTimeOffset.UtcNow)), CancellationToken.None); }
            if (captured is not null) return captured();
            var settings = Runtime.ConversationSettings.Single();
            var context = new DesktopConversationSettingsContext(Binding.SessionId!, "fixture", "profile", null, null, CancellationToken.None);
            if (lane == "settings-open") return settings.OpenAsync(context, CancellationToken.None);
            editor = await settings.OpenAsync(context, CancellationToken.None);
            Save = editor.SaveAsync();
            return Save;
        }

        public DesktopPluginRuntime AnotherRuntime()
        {
            Task<DesktopOperationResult> Done() => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
            var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
                () => new(Binding, "action", null, null, false), () => new(Binding, 0, ""),
                (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), _ => { },
                pluginDataRoot: Path.Combine(root, "data"), serviceCommand: Command);
            return new(Catalog, host, Dispatcher.CurrentDispatcher);
        }
        public Task InvokeCaptured() => captured!();
        public Task WaitStarts(int expected) => WaitUntil(() => Starts == expected);
        public void ReleaseOne() => releases.First(item => !item.Task.IsCompleted).TrySetResult();
        public void Release(bool fail = false)
        { foreach (var item in releases) { if (fail) item.TrySetException(new IOException("Deliberate native fixture failure.")); else item.TrySetResult(); } }
        public List<string> Events() => ReadEvents(Id);
        private List<string> ReadEvents(string id)
        {
            var path = Path.Combine(root, "data", id, "events.txt");
            return File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        }
        public int Count(string value) => Events().Count(item => item == value);
        public int ProviderCount(string value) => ReadEvents(ProviderId).Count(item => item == value);
        public void Dispose() { Release(); editor?.Dispose(); Runtime.Dispose(); }
    }
}

/// <summary>One native fixture chooses only the contribution under test; empty collections stay empty.</summary>
public sealed class InvocationFixturePlugin : DesktopPluginBase, IDesktopMountPlugin, IDesktopTextEntryPlugin,
    IDesktopTextEntrySubmissionPlugin, IDesktopInputModePlugin, IDesktopComposerCommandPlugin,
    IDesktopConversationWorkspacePlugin, IDesktopConversationSettingsPlugin
{
    public sealed class Signals
    {
        public Action? Retire { get; set; }
        public Action? RetireProvider { get; set; }
        public Action? DisposeRuntime { get; set; }
        public Action<string>? Observe { get; set; }
    }
    private readonly Signals signals = new();
    private string lane = "";
    private string id = "";
    private string events = "";
    private IDesktopServiceHost service = null!;
    public override void Initialize(IDesktopPluginHost host)
    {
        base.Initialize(host);
        var data = ((IDesktopPluginEnvironment)host).DataDirectory;
        Directory.CreateDirectory(data); events = Path.Combine(data, "events.txt");
        id = Path.GetFileName(data);
        lane = data.EndsWith("invocation.provider", StringComparison.Ordinal) ? "provider" : host.GetConversation().Title;
        service = (IDesktopServiceHost)host;
        service.ExecuteServiceCommandAsync("fixture.attach", signals).GetAwaiter().GetResult();
        Emit("initialize");
        if (lane == "initialize-dispose-runtime") signals.DisposeRuntime!();
        if (lane == "initialize-retire-provider") signals.RetireProvider!();
        if (lane is "initialize-fail" or "initialize-fail-dispose-throw") throw new IOException("Deliberate initialization failure.");
        Emit("initialize-return");
    }
    private void Emit(string value) => File.AppendAllText(events, value + "\n");
    private async Task Wait(CancellationToken token)
    {
        using var reentry = lane == "action-cancel-reentry" ? token.Register(() => signals.RetireProvider!()) : default;
        await service.ExecuteServiceCommandAsync("fixture.callback", new { }, token);
        Emit("callback-return");
    }
    public override IReadOnlyList<DesktopPanel> Panels
    {
        get
        {
            if (lane == "getter-dispose-runtime") { signals.DisposeRuntime!(); return []; }
            return lane is "view-dispose-runtime" or "view-retire-provider"
                ? [new("view", "Fixture view", () => {
                    if (lane == "view-dispose-runtime") signals.DisposeRuntime!(); else signals.RetireProvider!();
                    Emit("view-return"); return new TextBlock();
                  }), new("second", "Unused view", () => { Emit("second-view"); return new TextBlock(); })] : [];
        }
    }
    public override IReadOnlyList<DesktopAction> ComposerActions
    {
        get { Emit("actions-read"); return lane.StartsWith("action", StringComparison.Ordinal) ? [new("action", "Fixture action", Wait)] : []; }
    }
    public IReadOnlyList<DesktopTextEntryAction> TextEntryActions =>
        lane == "text-action" ? [new("text", "Fixture text", (_, token) => Wait(token))] : [];
    public IReadOnlyList<IDesktopMountContribution> MountContributions => lane switch {
        "mount-action" => [new DesktopMountAction(new("mount", DesktopMountSlots.WorkspaceActions), "Fixture mount", Wait)],
        "mount-text" => [new DesktopMountTextEntryAction(new("mount", DesktopMountSlots.AnswerActions), "Fixture text", (_, token) => Wait(token))],
        "render" => [new DesktopMountMessageRenderer(new("render", DesktopMountSlots.MessageRenderers), Render)],
        "decoration" => [new DesktopMountMessageDecoration(new("decoration", DesktopMountSlots.MessageDecorations), Render)],
        _ => []
    };
    public async Task<DesktopOperationResult> PrepareSubmissionAsync(IDesktopTextEntry entry, CancellationToken token)
    { if (lane == "submission") await Wait(token); return new(DesktopOperationState.Completed); }
    public IReadOnlyList<DesktopInputMode> InputModes => lane is "input" or "prepare"
        ? [new("input", "Fixture input", async (_, token) => { await Wait(token); return new(DesktopInputInterceptionState.Pass); }) {
            PrepareAsync = async token => { await Wait(token); return "ready"; }
        }] : [];
    public IReadOnlyList<DesktopComposerCommand> ComposerCommands => lane == "command"
        ? [new("command", async (_, token) => { await Wait(token); return new(DesktopComposerCommandState.Pass); })] : [];
    public IReadOnlyList<DesktopConversationWorkspace> ConversationWorkspaces => lane == "workspace"
        ? [new("workspace", "Fixture workspace", async (_, token) => { await Wait(token); return new TextBlock(); })] : [];
    public IReadOnlyList<DesktopConversationSettingsContribution> ConversationSettings => lane.StartsWith("settings-", StringComparison.Ordinal)
        ? [new("settings", "Fixture settings", async (_, token) => { if (lane == "settings-open") await Wait(token); return new Editor(this); })] : [];
    public override DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) =>
        lane == "presentation" ? new(Render(message)) : null;
    private FrameworkElement Render(DesktopMessageSnapshot message)
    { signals.Retire!(); Emit("render-after-disable"); return new TextBlock(); }
    public override void Dispose()
    {
        signals.Observe?.Invoke(id + ":dispose-enter");
        Emit("dispose");
        if (lane == "action-dispose-reentry") signals.RetireProvider!();
        if (lane is "action-dispose-throw" or "initialize-fail-dispose-throw")
            throw new IOException("Deliberate native Dispose failure.");
        base.Dispose();
        signals.Observe?.Invoke(id + ":dispose-return");
    }
    private sealed class Editor : IDesktopConversationSettingsEditor
    {
        private readonly InvocationFixturePlugin owner;
        public Editor(InvocationFixturePlugin owner)
        {
            this.owner = owner;
            if (owner.lane == "settings-open-invalid-dispose-throw") new StackPanel().Children.Add(View);
        }
        public FrameworkElement View { get; } = new TextBox();
        public async Task<DesktopOperationResult> SaveAsync(CancellationToken token = default)
        { await owner.Wait(token); return new(DesktopOperationState.Completed); }
        public void Cancel() { }
        public void Dispose()
        {
            owner.Emit("editor-dispose");
            if (owner.lane is "settings-editor-dispose-throw" or "settings-open-invalid-dispose-throw")
                throw new IOException("Deliberate native editor Dispose failure.");
        }
    }
}
