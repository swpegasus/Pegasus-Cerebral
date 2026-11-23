using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

/// <summary>Actual native package/runtime loading with deterministic in-process transport and offscreen WPF views.</summary>
internal static class ConversationSettingsRuntimeTests
{
    private static int checks;
    private static List<RetiredPackage>? cleanupRoots;

    public static async Task<int> RunAsync()
    {
        var roots = new List<RetiredPackage>();
        cleanupRoots = roots;
        // The runtime dictionary excludes rejected entries. Capture every fixture assembly load
        // using weak references only, including rejection/reopen contexts, for truthful diagnostics.
        AssemblyLoadEventHandler loaded = (_, arguments) => TrackFixtureAssembly(roots, arguments.LoadedAssembly);
        AppDomain.CurrentDomain.AssemblyLoad += loaded;
        var casesPassed = false;
        try
        {
            await RunCasesAsync();
            casesPassed = true;
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= loaded;
            cleanupRoots = null;
            // A failed behavior assertion retains precedence over cleanup diagnostics. Successful behavior
            // is insufficient for unload acceptance when fixture package files are still locked.
            await CleanupRetiredPackagesAsync(roots);
            if (casesPassed && roots.Count != 0)
                throw new InvalidOperationException("Conversation-settings behavior completed " + checks +
                    " checks, but collectible-context/file cleanup failed for " + roots.Count +
                    " fixture packages. The full lane failed. See bounded mode/weak-reference diagnostics.");
        }
        await VerifyExitRetirementChildAsync("terminal-failure");
        return checks;
    }

    public static async Task<string> RunExitRetirementOnlyAsync()
    {
        checks = 0;
        var heldSaveChecks = await VerifyExitRetirementChildAsync("held-save");
        var terminalFailureChecks = await VerifyExitRetirementChildAsync("terminal-failure");
        return JsonSerializer.Serialize(new
        {
            checks = checks + heldSaveChecks + terminalFailureChecks,
            childCases = 2,
            tier = "isolated-native-plugin-runtime-with-deterministic-transport-and-process-exit-cleanup",
            inProcessCollectibleUnloadVerified = false,
            fullConversationSettingsLaneEvaluated = false
        });
    }

    private static async Task<int> VerifyExitRetirementChildAsync(string mode)
    {
        if (mode is not ("held-save" or "terminal-failure"))
            throw new ArgumentOutOfRangeException(nameof(mode));
        var root = Path.Combine(Path.GetTempPath(), "Cerebral.DesktopPluginExitRetirement-" + mode + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--desktop-plugin-exit-retirement-child");
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(root);
        using var child = Process.Start(start) ?? throw new IOException("The private retirement fixture child did not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        Exception? failure = null;
        var validatedChecks = 0;
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var stdout = await output; var stderr = await errors;
            if (child.ExitCode != 0)
                throw new InvalidOperationException("Private retirement " + mode + " child failed: " + stderr);
            using var result = JsonDocument.Parse(stdout);
            var actual = result.RootElement;
            var childChecks = actual.GetProperty("checks").GetInt32();
            Check(actual.GetProperty("mode").GetString() == mode && childChecks > 0 &&
                  (mode == "held-save"
                      ? actual.GetProperty("retirementSettled").GetBoolean() && actual.GetProperty("pluginDisposed").GetBoolean()
                      : actual.GetProperty("terminalFailure").GetBoolean() &&
                        !actual.GetProperty("pluginDisposed").GetBoolean() && actual.GetProperty("ownerRetained").GetBoolean()),
                "Private " + mode + " retirement result did not confirm its exact contract.");
            validatedChecks = childChecks;
        }
        catch (Exception error) { failure = error; }
        Exception? drainFailure = null;
        try
        {
            // A soft timeout is a failed check, not permission to abandon the owned child.
            // Its private watchdog exits naturally; retain both readers through actual EOF.
            if (!child.HasExited) await child.WaitForExitAsync();
            await Task.WhenAll(output, errors);
        }
        catch (Exception error) { drainFailure = error; }
        if (!child.HasExited || !output.IsCompletedSuccessfully || !errors.IsCompletedSuccessfully)
            throw new InvalidOperationException("Private retirement child or output streams have no confirmed terminal state; its exact fixture root was preserved.",
                failure is null ? drainFailure : drainFailure is null ? failure : new AggregateException(failure, drainFailure));
        Exception? cleanupFailure = null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("Cerebral.DesktopPluginExitRetirement-" + mode + "-", StringComparison.Ordinal))
                throw new IOException("The exact private retirement fixture child/root cleanup boundary was not verified.");
            CerebralPluginPackage.RejectLinks(full);
            Directory.Delete(full, true);
        }
        catch (Exception error) { cleanupFailure = error; }
        if (drainFailure is not null)
            failure = failure is null ? drainFailure : new AggregateException(failure, drainFailure);
        if (failure is not null && cleanupFailure is not null) throw new AggregateException(failure, cleanupFailure);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanupFailure is not null) throw cleanupFailure;
        return validatedChecks;
    }

    public static async Task RunExitRetirementChildAsync(string mode, string root)
    {
        if (mode is not ("held-save" or "terminal-failure"))
            throw new ArgumentOutOfRangeException(nameof(mode));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var name = Path.GetFileName(full);
        var prefix = "Cerebral.DesktopPluginExitRetirement-" + mode + "-";
        if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidOperationException("The private retirement fixture root is not its exact owned temp directory.");
        CerebralPluginPackage.RejectLinks(full);
        checks = 0;
        if (mode == "held-save")
        {
            await CheckExitRetirementHeldSaveAsync(full);
            Console.WriteLine(JsonSerializer.Serialize(new { mode, checks, retirementSettled = true, pluginDisposed = true }));
            return;
        }
        using var fixture = Fixture.Create("dispose-throws", root: full);
        _ = await fixture.Contribution.OpenAsync(Context(), default);
        fixture.Runtime.Dispose();
        var retirement = fixture.Runtime.ObserveRetirementAsync();
        var failed = false;
        try { await retirement.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Desktop plugin native cleanup failed;", StringComparison.Ordinal))
        { failed = true; }
        Check(failed && fixture.Count("plugin-dispose") == 0 && fixture.Count("dispose:1") == 1 &&
              fixture.Runtime.Status.Any(item => item.Id == fixture.PluginId &&
                  item.Error?.Contains("Native cleanup failed", StringComparison.Ordinal) == true),
            "Terminal retirement failure was not retained by the exact native owner.");
        Console.WriteLine(JsonSerializer.Serialize(new { mode, checks, terminalFailure = true, pluginDisposed = false, ownerRetained = true }));
    }

    private static async Task CleanupRetiredPackagesAsync(List<RetiredPackage> roots)
    {
        for (var attempt = 0; attempt < 3 && roots.Count != 0; attempt++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            CollectRetiredContexts();
            foreach (var root in roots.ToArray())
            {
                try
                {
                    var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path));
                    var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                    if (!directory.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        !Path.GetFileName(directory).StartsWith("Cerebral.ConversationSettingsRuntime-", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine("Refused cleanup outside the exact fixture-owned temporary package: " + directory);
                        continue;
                    }
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                    roots.Remove(root);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 2)
                        Console.Error.WriteLine("Retained isolated fixture package after three collectible-context cleanup attempts: " +
                            root.Path + " (mode=" + root.Mode + "; " + root.Retention() + "; " +
                            WpfCacheRetention(root) + "; " + RelativeError(root, error) +
                            "). Unload/filesystem cleanup failed.");
                }
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void TrackFixtureAssembly(List<RetiredPackage> roots, Assembly assembly)
    {
        if (assembly.IsDynamic) return;
        var location = assembly.Location;
        foreach (var root in roots)
            if (location.StartsWith(root.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                root.CaptureAssembly(assembly, location);
                return;
            }
    }

    private static string RelativeError(RetiredPackage root, Exception error)
    {
        var message = error.Message.Replace(root.Path, "<fixture>", StringComparison.OrdinalIgnoreCase);
        // Only fixture-relative paths belong in diagnostics; other absolute paths are irrelevant.
        message = Regex.Replace(message, @"(?:[A-Za-z]:[\\/]|\\\\)[^'""\r\n]*", "<external-path>");
        message = message.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', '?');
        if (message.Length > 512) message = message[..512];
        return error.GetType().Name + "; HResult=0x" + error.HResult.ToString("X8") + "; message=" + message;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string WpfCacheRetention(RetiredPackage root)
    {
        // Inspect already existing fields only: no schema/cache getter, namespace discovery,
        // assembly resolution, Lazy.Value initialization, cache removal, or prewarming.
        try
        {
            var matches = new List<string>();
            var layout = new List<string>();
            var inspected = 0;
            var framework = typeof(System.Windows.Markup.XamlReader).Assembly;
            foreach (var lazyName in new[] { "_bamlSharedContext", "_xamlSharedContext", "_xamlV3SharedContext" })
            {
                var lazyField = typeof(System.Windows.Markup.XamlReader)
                    .GetField(lazyName, BindingFlags.Static | BindingFlags.NonPublic);
                if (lazyField is null) { layout.Add(lazyName + ":missing-field"); continue; }
                var lazy = lazyField.GetValue(null);
                if (lazy is null) { layout.Add(lazyName + ":null"); continue; }
                var created = lazy.GetType().GetProperty("IsValueCreated")?.GetValue(lazy);
                layout.Add(lazyName + ":" + lazy.GetType().Name + "/created=" + (created?.ToString() ?? "unsupported"));
                if (created is not true) continue;
                var context = lazy.GetType().GetProperty("Value")!.GetValue(lazy)!;
                foreach (var cacheName in new[] { "_xmlnsInfo", "_xmlnsInfoForUnreferencedAssemblies" })
                {
                    FieldInfo? field = null;
                    for (Type? type = context.GetType(); type is not null && field is null; type = type.BaseType)
                        field = type.GetField(cacheName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    InspectCache(lazyName + "." + cacheName, field?.GetValue(context), field is not null);
                }
            }

            // The shared WPF source also maintains a process-static assembly-name cache
            // independently of XamlReader contexts. Its ordinary Assembly keys are strong.
            // Resolve only helper types from assemblies already loaded in this process.
            foreach (var candidate in new[]
            {
                (Assembly: typeof(DependencyObject).Assembly, TypeName: "MS.Internal.WindowsBase.SafeSecurityHelper"),
                (Assembly: typeof(System.Windows.Media.Visual).Assembly, TypeName: "MS.Internal.PresentationCore.SafeSecurityHelper"),
                (Assembly: AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => value.GetName().Name == "System.Xaml"),
                    TypeName: "System.Xaml.SafeSecurityHelper")
            })
            {
                var helper = candidate.Assembly?.GetType(candidate.TypeName, throwOnError: false);
                var field = helper?.GetField("_assemblies", BindingFlags.Static | BindingFlags.NonPublic);
                InspectCache(candidate.TypeName + "._assemblies", field?.GetValue(null), field is not null);
            }
            return "wpfVersion=" + framework.GetName().Version +
                "; wpfCachesInspected=" + inspected + "; wpfCacheLayout=" + string.Join(",", layout) +
                "; wpfStrongAssemblyMatches=" + (matches.Count == 0 ? "none" : string.Join(",", matches));

            void InspectCache(string name, object? cache, bool fieldExists)
            {
                if (cache is null) { layout.Add(name + (fieldExists ? ":null" : ":missing-field")); return; }
                if (cache.GetType().GetProperty("Keys")?.GetValue(cache) is not System.Collections.IEnumerable keys)
                { layout.Add(name + ":" + cache.GetType().Name + "/unsupported-keys"); return; }
                inspected++;
                var count = 0;
                var examined = 0;
                foreach (var key in keys)
                {
                    if (++examined > 4096) break;
                    // Count exact strong Assembly keys only, not WeakReference/WeakRefKey targets.
                    if (key is Assembly assembly && !assembly.IsDynamic &&
                        assembly.Location.StartsWith(root.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        count++;
                }
                layout.Add(name + ":" + cache.GetType().Name + "/keys=" + Math.Min(examined, 4096) +
                    (examined > 4096 ? "/truncated" : ""));
                if (count != 0) matches.Add(name + ":" + count);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return "wpfCacheProbeFailed=" + error.GetType().Name + "/0x" + error.HResult.ToString("X8");
        }
    }

    private sealed record LoadedAssembly(WeakReference Assembly, WeakReference? Context, string RelativePath,
        bool Collectible, bool Dynamic, bool Default);

    private sealed class RetiredPackage(string path, string mode)
    {
        public string Path { get; } = path;
        public string Mode { get; } = mode;
        public WeakReference? Runtime { get; set; }
        private readonly List<WeakReference> owners = [];
        private readonly List<WeakReference> plugins = [];
        private readonly List<WeakReference> contexts = [];
        private readonly List<LoadedAssembly> assemblies = [];

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void CaptureAssembly(Assembly assembly, string location)
        {
            var context = AssemblyLoadContext.GetLoadContext(assembly);
            assemblies.Add(new(new WeakReference(assembly, trackResurrection: true),
                context is null ? null : new WeakReference(context, trackResurrection: true),
                location[(Path.Length + 1)..], context?.IsCollectible ?? false, assembly.IsDynamic,
                ReferenceEquals(context, AssemblyLoadContext.Default)));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Capture(DesktopPluginRuntime runtime)
        {
            Runtime ??= new WeakReference(runtime);
            var dictionary = (System.Collections.IDictionary)typeof(DesktopPluginRuntime)
                .GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
            if (dictionary["fixture.conversation-settings"] is not { } owner) return;
            owners.Add(new WeakReference(owner));
            plugins.Add(new WeakReference(owner.GetType().GetProperty("Plugin")!.GetValue(owner)!));
            contexts.Add(new WeakReference(owner.GetType().GetProperty("Context")!.GetValue(owner)!, trackResurrection: true));
        }

        public string Retention() => "runtimeAlive=" + (Runtime?.IsAlive ?? false) +
            "; ownerTracked=" + owners.Count +
            "; ownerAlive=" + owners.Any(item => item.IsAlive) +
            "; pluginAlive=" + plugins.Any(item => item.IsAlive) +
            "; assemblyLoads=" + assemblies.Count +
            "; assemblyAlive=" + assemblies.Any(item => item.Assembly.IsAlive) +
            "; contextTracked=" + assemblies.Count(item => item.Context is not null) +
            "; contextAlive=" + (contexts.Any(item => item.IsAlive) || assemblies.Any(item => item.Context?.IsAlive == true)) +
            "; collectibleLoads=" + assemblies.Count(item => item.Collectible) +
            "; dynamicLoads=" + assemblies.Count(item => item.Dynamic) +
            "; defaultLoads=" + assemblies.Count(item => item.Default) +
            "; assemblyPaths=" + string.Join(",", assemblies.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Take(4));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CollectRetiredContexts()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    private static async Task<int> RunCasesAsync()
    {
        checks = 0;
        foreach (var mode in new[] { "wrong-version", "many", "duplicate", "bad-id", "bad-title", "null-entry", "null-factory", "null-collection" })
        {
            using var fixture = Fixture.Create(mode);
            Check(!fixture.Runtime.Status.Single().Loaded && fixture.Runtime.ConversationSettings.Count == 0 &&
                !fixture.Events.Any(value => value.StartsWith("open:", StringComparison.Ordinal)), "Invalid descriptor rejected before editor construction: " + mode);
        }
        using (var fixture = Fixture.Create("normal", DesktopPluginSurface.Configuration))
            Check(fixture.Runtime.Status.Single().Loaded && fixture.Runtime.ConversationSettings.Count == 0,
                "Configuration does not reinterpret a per-chat contribution as global settings.");

        using (var fixture = Fixture.Create("normal"))
        {
            Check(fixture.Runtime.Status.Single().Loaded && fixture.Runtime.ConversationSettings.Count == 1 &&
                fixture.Events.All(value => !value.StartsWith("open:", StringComparison.Ordinal) && !value.StartsWith("view:", StringComparison.Ordinal)),
                "Loading validates only lazy descriptors and constructs no editor/view.");
            using var availability = new CancellationTokenSource();
            var context = Context(availability.Token);
            var editor = await fixture.Contribution.OpenAsync(context, default);
            Check(fixture.Events.Contains("open:1:sidebar-target:profile-target:project-target:C:\\fixture-root") &&
                fixture.Binding.SessionId != context.ConversationId, "Factory receives exact immutable sidebar target independently of selected chat.");
            Check(ReferenceEquals(editor.View, editor.View) && editor.View.Parent is null, "Guarded editor retains its stable unparented view.");
            var second = await fixture.Contribution.OpenAsync(context, default);
            Check(!ReferenceEquals(editor.View, second.View), "Repeated opens return independent fresh editors and views.");
            Check((await editor.SaveAsync()).State == DesktopOperationState.Completed && fixture.SaveCalls == 1,
                "A confirmed valid receipt is returned from exactly one explicit plugin save.");
            editor.Cancel(); editor.Dispose(); editor.Cancel();
            second.Dispose();
            Check(fixture.Count("cancel:1") == 1 && fixture.Count("dispose:1") == 1 &&
                fixture.Count("cancel:2") == 1 && fixture.Count("dispose:2") == 1, "Cancel/Dispose are idempotent and release every draft once.");
            Check((await editor.SaveAsync()).State == DesktopOperationState.Rejected && fixture.SaveCalls == 1,
                "Retired editor cannot dispatch a fresh save.");
        }
        foreach (var mode in new[] { "parented-view", "changing-view", "null-view" })
        {
            using var fixture = Fixture.Create(mode);
            Check(await RejectsOpen(fixture.Contribution, Context()) &&
                fixture.Count("cancel:1") == 1 && fixture.Count("dispose:1") == 1 && fixture.SaveCalls == 0,
                "Malformed factory view is isolated and cleaned before any save: " + mode);
        }
        using (var fixture = Fixture.Create("reuse-editor"))
        {
            var editor = await fixture.Contribution.OpenAsync(Context(), default);
            Check(await RejectsOpen(fixture.Contribution, Context()) && fixture.Count("dispose:1") == 0,
                "A reused editor is rejected without destroying the first valid editor.");
            Check((await editor.SaveAsync()).State == DesktopOperationState.Completed, "First editor remains usable after duplicate editor rejection.");
            editor.Dispose();
        }
        using (var fixture = Fixture.Create("reuse-view"))
        {
            var first = await fixture.Contribution.OpenAsync(Context(), default);
            Check(await RejectsOpen(fixture.Contribution, Context()) && fixture.Count("dispose:2") == 1,
                "A new editor that reuses a prior view is rejected and its draft cleaned.");
            first.Dispose();
        }
        using (var fixture = Fixture.Create("mutating-view"))
        {
            var editor = await fixture.Contribution.OpenAsync(Context(), default);
            Check((await editor.SaveAsync()).State == DesktopOperationState.Rejected && fixture.SaveCalls == 0,
                "Changing View after opening fails before dispatch.");
            editor.Dispose();
        }
        foreach (var mode in new[] { "uncertain", "bad-state", "long-message", "bad-request", "bad-job", "null-receipt", "save-throws" })
        {
            using var fixture = Fixture.Create(mode);
            var editor = await fixture.Contribution.OpenAsync(Context(), default);
            var result = await editor.SaveAsync();
            Check(result.State == DesktopOperationState.Uncertain && (await editor.SaveAsync()).State == DesktopOperationState.Uncertain &&
                fixture.SaveCalls == 1, "Lost/malformed/uncertain receipt never replays a dispatched draft: " + mode);
            editor.Dispose();
        }
        foreach (var mode in new[] { "accepted", "rejected", "failed" })
        {
            using var fixture = Fixture.Create(mode);
            var editor = await fixture.Contribution.OpenAsync(Context(), default);
            var expected = mode switch { "accepted" => DesktopOperationState.Accepted, "rejected" => DesktopOperationState.Rejected, _ => DesktopOperationState.Failed };
            Check((await editor.SaveAsync()).State == expected, "Valid receipt keeps its actual disposition: " + mode);
            if (mode == "accepted")
                Check((await editor.SaveAsync()).State == DesktopOperationState.Rejected && fixture.SaveCalls == 1,
                    "Accepted asynchronous work has no settlement contract and cannot dispatch another save from the same draft.");
            editor.Dispose();
        }
        using (var fixture = Fixture.Create("cancel-throws"))
        {
            var editor = await fixture.Contribution.OpenAsync(Context(), default);
            editor.Dispose();
            Check(fixture.Count("cancel:1") == 1 && fixture.Count("dispose:1") == 1 &&
                fixture.Reports.Any(value => value.Contains("Settings discard failed", StringComparison.Ordinal)),
                "Discard failure is reported without skipping editor Dispose.");
        }
        using (var fixture = Fixture.Create("cancel-callback-throws"))
        {
            var first = await fixture.Contribution.OpenAsync(Context(), default);
            var second = await fixture.Contribution.OpenAsync(Context(), default);
            first.Cancel(); first.Dispose(); second.Cancel(); second.Dispose();
            Check(fixture.Count("throwing-cancellation:1") == 1 && fixture.Count("throwing-cancellation:2") == 1 &&
                fixture.Count("dispose:1") == 1 && fixture.Count("dispose:2") == 1 &&
                fixture.Reports.Count(value => value.Contains("Settings cancellation failed", StringComparison.Ordinal)) == 2,
                "Throwing plugin token callbacks are isolated and cannot prevent either editor's final cleanup.");
        }
        using (var fixture = Fixture.Create("report-throws"))
        {
            var editor = await fixture.Contribution.OpenAsync(Context(), default);
            editor.Dispose();
            Check(fixture.Count("cancel:1") == 1 && fixture.Count("dispose:1") == 1,
                "A throwing observational status sink cannot prevent final editor cleanup.");
        }
        using (var fixture = Fixture.Create("normal"))
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Check(await RejectsOpen(fixture.Contribution, Context(cancelled.Token)) && fixture.CountPrefix("open:") == 0,
                "Unavailable owner context is rejected before factory dispatch.");
            Check(await RejectsOpen(fixture.Contribution, Context() with { ConversationId = "bad\0id" }) && fixture.CountPrefix("open:") == 0,
                "Malformed captured target is rejected before factory dispatch.");
        }
        using (var fixture = Fixture.Create("normal"))
        {
            var old = fixture.Contribution;
            var editor = await old.OpenAsync(Context(), default);
            var package = fixture.Catalog.List().Single();
            fixture.Runtime.ReopenExact(package.Id, package.Directory, package.Version);
            Check(await RejectsOpen(old, Context()) && (await editor.SaveAsync()).State == DesktopOperationState.Rejected &&
                fixture.SaveCalls == 0 && fixture.Runtime.ConversationSettings.Count == 1,
                "Same-package reopen invalidates captured factory and editor by exact LoadedPlugin identity.");
            editor.Dispose();
        }
        using (var fixture = Fixture.Create("normal"))
        {
            var editor = await fixture.Contribution.OpenAsync(Context(), default);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check((await editor.SaveAsync(cancelled.Token)).State == DesktopOperationState.Rejected && fixture.SaveCalls == 0,
                "Caller cancellation before dispatch creates no plugin mutation.");
            editor.Dispose();
        }
        using (var fixture = Fixture.Create("save-hold"))
        {
            var first = await fixture.Contribution.OpenAsync(Context(), default);
            var second = await fixture.Contribution.OpenAsync(Context(), default);
            var firstSave = first.SaveAsync(); var secondSave = second.SaveAsync();
            await fixture.WaitFor("save:1"); await fixture.WaitFor("save:2");
            Check(!ReferenceEquals(firstSave, secondSave) && fixture.SaveCalls == 2,
                "Different editors retain independent save serialization and neither blocks the other.");
            fixture.ReleaseSave();
            Check((await firstSave).State == DesktopOperationState.Completed && (await secondSave).State == DesktopOperationState.Completed,
                "Independent saves preserve their own confirmed receipts.");
            first.Dispose(); second.Dispose();
        }
        using (var fixture = Fixture.Create("save-hold"))
        {
            var editor = await fixture.Contribution.OpenAsync(Context(), default);
            var first = editor.SaveAsync(); var duplicate = editor.SaveAsync();
            Check(ReferenceEquals(first, duplicate), "Concurrent Save calls share one independently serialized operation.");
            await fixture.WaitFor("save:1");
            Check(fixture.SaveCalls == 1, "Duplicate Save does not invoke plugin a second time.");
            editor.Cancel(); editor.Dispose();
            Check(fixture.Count("cancel:1") == 0 && fixture.Count("dispose:1") == 0 && fixture.SaveCancellationObserved,
                "Cancel signals the pending save and defers discard/disposal until settlement.");
            fixture.ReleaseSave();
            Check((await first).State == DesktopOperationState.Uncertain, "Cancellation after dispatch cannot imply a confirmed rollback.");
            await fixture.WaitFor("dispose:1");
            Check(fixture.Count("cancel:1") == 1 && fixture.Count("dispose:1") == 1 &&
                (await editor.SaveAsync()).State == DesktopOperationState.Uncertain && fixture.SaveCalls == 1,
                "Settled uncertain draft cleans once and cannot replay.");
        }
        using (var fixture = Fixture.Create("save-hold"))
        {
            var editor = await fixture.Contribution.OpenAsync(Context(), default);
            var save = editor.SaveAsync();
            await fixture.WaitFor("save:1");
            fixture.Runtime.Disable(fixture.PluginId);
            Check(fixture.Runtime.ConversationSettings.Count == 0 && fixture.SaveCancellationObserved &&
                fixture.Count("dispose:1") == 0 && fixture.Count("plugin-dispose") == 0,
                "Unload removes availability immediately but cannot race in-flight editor/plugin disposal.");
            fixture.ReleaseSave();
            Check((await save).State == DesktopOperationState.Uncertain, "Late save receipt from unloaded owner cannot appear confirmed.");
            await fixture.WaitFor("plugin-dispose");
            Check(fixture.Events.IndexOf("dispose:1") < fixture.Events.IndexOf("plugin-dispose") &&
                fixture.Count("dispose:1") == 1 && fixture.Count("plugin-dispose") == 1,
                "Unload finishes editor cleanup before entry Dispose, exactly once.");
        }
        await CheckExitRetirementHeldSaveAsync();
        using (var fixture = Fixture.Create("save-hold"))
        {
            using var availability = new CancellationTokenSource();
            var editor = await fixture.Contribution.OpenAsync(Context(availability.Token), default);
            var save = editor.SaveAsync();
            await fixture.WaitFor("save:1"); availability.Cancel();
            Check(fixture.SaveCancellationObserved && fixture.Count("dispose:1") == 0,
                "A revoked dialog/connection binding cancels save availability without disposing its pending editor.");
            fixture.ReleaseSave();
            Check((await save).State == DesktopOperationState.Uncertain && (await editor.SaveAsync()).State == DesktopOperationState.Uncertain &&
                fixture.SaveCalls == 1, "A reply after captured binding revocation cannot confirm or replay a save.");
            await fixture.WaitFor("dispose:1");
        }
        using (var fixture = Fixture.Create("open-hold"))
        {
            var contribution = fixture.Contribution;
            var open = contribution.OpenAsync(Context(), default);
            await fixture.WaitFor("open-hold");
            fixture.Runtime.Disable(fixture.PluginId);
            Check(fixture.OpenCancellationObserved && fixture.Count("plugin-dispose") == 0,
                "Unload cancels pending lazy factory and waits before plugin Dispose.");
            fixture.ReleaseOpen();
            Check(await RejectsTask(open), "Factory reply after unload cannot publish an editor.");
            await fixture.WaitFor("plugin-dispose");
            Check(fixture.Count("cancel:1") == 1 && fixture.Count("dispose:1") == 1 &&
                fixture.Events.IndexOf("dispose:1") < fixture.Events.IndexOf("plugin-dispose"),
                "Late factory editor is discarded/disposed before unload finishes.");
        }
        using (var fixture = Fixture.Create("open-hold"))
        {
            using var availability = new CancellationTokenSource();
            var open = fixture.Contribution.OpenAsync(Context(availability.Token), default);
            await fixture.WaitFor("open-hold"); availability.Cancel(); fixture.ReleaseOpen();
            Check(await RejectsTask(open) && fixture.Count("dispose:1") == 1 && fixture.SaveCalls == 0,
                "Revoked captured target cannot publish a late factory editor.");
        }
        return checks;
    }

    private static async Task CheckExitRetirementHeldSaveAsync(string? root = null)
    {
        using var fixture = Fixture.Create("save-hold", root: root);
        var editor = await fixture.Contribution.OpenAsync(Context(), default);
        var save = editor.SaveAsync();
        await fixture.WaitFor("save:1");
        fixture.Runtime.Dispose();
        var retirement = fixture.Runtime.ObserveRetirementAsync();
        Check(!retirement.IsCompleted && fixture.Count("dispose:1") == 0 && fixture.Count("plugin-dispose") == 0,
            "Exit retirement waits for a held native settings save while the UI dispatcher remains live.");
        var timedOut = false;
        try { await retirement.WaitAsync(TimeSpan.Zero); }
        catch (TimeoutException) { timedOut = true; }
        Check(timedOut && !retirement.IsCompleted && fixture.Count("plugin-dispose") == 0,
            "An observation timeout does not finish or replay native retirement.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var canceledObservation = false;
        try { await retirement.WaitAsync(cancelled.Token); }
        catch (OperationCanceledException) { canceledObservation = true; }
        Check(canceledObservation && !retirement.IsCompleted && fixture.Count("plugin-dispose") == 0,
            "Canceling one observer leaves the exact native retirement pending.");
        fixture.ReleaseSave();
        Check((await save).State == DesktopOperationState.Uncertain,
            "The late save reply remains uncertain after retirement cancellation.");
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!Dispatcher.CurrentDispatcher.HasShutdownStarted &&
              fixture.Events.IndexOf("dispose:1") < fixture.Events.IndexOf("plugin-dispose") &&
              fixture.Count("dispose:1") == 1 && fixture.Count("plugin-dispose") == 1,
            "Native editor and plugin cleanup settle once before the dispatcher may exit.");
    }

    private static DesktopConversationSettingsContext Context(CancellationToken token = default) =>
        new("sidebar-target", "Exact sidebar chat", "profile-target", "project-target", "C:\\fixture-root", token);
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Conversation-settings runtime: " + message);
        checks++;
    }
    private static async Task<bool> RejectsOpen(DesktopHostedConversationSettings contribution, DesktopConversationSettingsContext context)
    {
        try { (await contribution.OpenAsync(context, default)).Dispose(); return false; }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException) { return true; }
    }
    private static async Task<bool> RejectsTask(Task<IDesktopConversationSettingsEditor> task)
    {
        try { (await task).Dispose(); return false; }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { return true; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        private readonly RetiredPackage evidence;
        private DesktopPluginRuntime? runtime;
        private readonly TaskCompletionSource openRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource saveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PluginId { get; } = "fixture.conversation-settings";
        public List<string> Events { get; } = [];
        public List<string> Reports { get; } = [];
        public DesktopPluginCatalog Catalog { get; }
        public DesktopPluginRuntime Runtime => runtime ?? throw new ObjectDisposedException(nameof(Fixture));
        public DesktopConversationBinding Binding { get; } = new("fixture-connection", "currently-selected-chat");
        public int SaveCalls { get; private set; }
        public bool SaveCancellationObserved { get; private set; }
        public bool OpenCancellationObserved { get; private set; }
        public DesktopHostedConversationSettings Contribution => Runtime.ConversationSettings.Single();

        private Fixture(string mode, DesktopPluginSurface surface, string? exactRoot)
        {
            root = exactRoot ?? Path.Combine(Path.GetTempPath(), "Cerebral.ConversationSettingsRuntime-" + Guid.NewGuid().ToString("N"));
            evidence = new RetiredPackage(root, mode);
            cleanupRoots?.Add(evidence);
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            var assembly = typeof(ConversationSettingsFixturePlugin).Assembly;
            File.Copy(assembly.Location, Path.Combine(source, "Fixture.dll"));
            File.WriteAllText(Path.Combine(source, DesktopPluginCatalog.ManifestFileName), JsonSerializer.Serialize(new DesktopPluginManifest
            {
                Id = PluginId, Name = "Conversation settings fixture", Assembly = "Fixture.dll",
                EntryType = typeof(ConversationSettingsFixturePlugin).FullName!, Version = "1.0.0"
            }, Cerebral.Core.CerebralJson.Options));
            Catalog = new DesktopPluginCatalog(Path.Combine(root, "installed"));
            Catalog.Install(Path.Combine(source, DesktopPluginCatalog.ManifestFileName)); Catalog.SetEnabled(PluginId, true);
            var draft = new DesktopDraftSnapshot(Binding, 0, "");
            var host = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher,
                () => new(Binding, mode, null, null, false), () => draft,
                (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), (_, _) => Done(), report =>
                {
                    Reports.Add(report);
                    if (mode == "report-throws") throw new InvalidOperationException("Deliberate observational status failure.");
                },
                surface: surface, pluginDataRoot: Path.Combine(root, "data"), serviceCommand: Command);
            runtime = new DesktopPluginRuntime(Catalog, host, Dispatcher.CurrentDispatcher);
            runtime.LoadEnabled();
            evidence.Capture(runtime);
        }

        public static Fixture Create(string mode, DesktopPluginSurface surface = DesktopPluginSurface.Orchestrator,
            string? root = null) => new(mode, surface, root);
        private static Task<DesktopOperationResult> Done() => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        private static DesktopServiceCommandResult Result() => new(true, JsonSerializer.SerializeToElement(new { }));
        private async Task<DesktopServiceCommandResult> Command(string operation, object arguments, CancellationToken token)
        {
            if (operation == "fixture.settings.attach")
            {
                arguments.GetType().GetProperty("Sink")!.SetValue(arguments, new Action<string>(Events.Add));
                return Result();
            }
            if (operation == "fixture.settings.open-hold")
            {
                Events.Add("open-hold");
                using var cancellation = token.Register(() => OpenCancellationObserved = true);
                await openRelease.Task;
                return Result(); // Deliberately ignores cancellation to exercise a late, uncooperative plugin reply.
            }
            if (operation == "fixture.settings.save")
            {
                SaveCalls++;
                var serial = (int)arguments.GetType().GetProperty("Serial")!.GetValue(arguments)!;
                Events.Add("save:" + serial);
                var mode = (string)arguments.GetType().GetProperty("Mode")!.GetValue(arguments)!;
                if (mode == "save-hold")
                {
                    using var cancellation = token.Register(() => SaveCancellationObserved = true);
                    await saveRelease.Task;
                }
                return Result();
            }
            throw new InvalidOperationException("Fixture rejects unexpected service operation: " + operation);
        }
        public int Count(string value) => Events.Count(item => item == value);
        public int CountPrefix(string value) => Events.Count(item => item.StartsWith(value, StringComparison.Ordinal));
        public void ReleaseOpen() => openRelease.TrySetResult();
        public void ReleaseSave() => saveRelease.TrySetResult();
        public async Task WaitFor(string value)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!Events.Contains(value)) await Task.Delay(1, timeout.Token);
        }
        public void Dispose()
        {
            ReleaseOpen(); ReleaseSave();
            if (runtime is not { } current) return;
            evidence.Capture(current);
            current.Dispose();
            runtime = null;
            // The case can still hold an editor/open-task reference here; the outer runner collects
            // after RunCasesAsync returns and reports which weak owner/context layer remains alive.
        }
    }
}

/// <summary>Compiled fixture entry loaded through the real catalog in a separate collectible plugin context.</summary>
public sealed class ConversationSettingsFixturePlugin : DesktopPluginBase, IDesktopConversationSettingsPlugin
{
    private string mode = "";
    private IDesktopServiceHost service = null!;
    private readonly Signals signals = new();
    private int sequence;
    private IDesktopConversationSettingsEditor? reused;
    private FrameworkElement? sharedView;

    public sealed class Signals { public Action<string>? Sink { get; set; } }
    public override void Initialize(IDesktopPluginHost host)
    {
        base.Initialize(host);
        mode = host.GetConversation().Title;
        service = (IDesktopServiceHost)host;
        _ = service.ExecuteServiceCommandAsync("fixture.settings.attach", signals).GetAwaiter().GetResult();
    }
    public IReadOnlyList<DesktopConversationSettingsContribution> ConversationSettings => mode switch
    {
        "null-collection" => null!,
        "many" => Enumerable.Range(0, 9).Select(index => Contribution("settings-" + index)).ToArray(),
        "duplicate" => [Contribution("same"), Contribution("same")],
        "bad-id" => [Contribution("bad\0id")],
        "bad-title" => [Contribution("settings") with { Title = new string('x', 101) }],
        "wrong-version" => [Contribution("settings") with { ContractVersion = 2 }],
        "null-entry" => [null!],
        "null-factory" => [Contribution("settings") with { OpenAsync = null! }],
        _ => [Contribution("settings")]
    };
    private DesktopConversationSettingsContribution Contribution(string id) => new(id, "Fixture settings", Open);
    private async Task<IDesktopConversationSettingsEditor> Open(DesktopConversationSettingsContext context, CancellationToken token)
    {
        var serial = ++sequence;
        signals.Sink!("open:" + serial + ":" + context.ConversationId + ":" + context.ProfileId + ":" + context.ProjectId + ":" + context.WorkingDirectory);
        if (mode == "open-hold") await service.ExecuteServiceCommandAsync("fixture.settings.open-hold", new { }, token);
        if (mode == "reuse-editor" && reused is not null) return reused;
        FrameworkElement view = mode == "reuse-view" ? sharedView ??= new TextBox() : new TextBox();
        if (mode == "parented-view") new StackPanel().Children.Add(view);
        var editor = new Editor(serial, mode, view, service, signals);
        editor.ObserveAvailability(context.AvailabilityToken);
        if (mode == "reuse-editor") reused = editor;
        return editor;
    }
    public override void Dispose() { signals.Sink?.Invoke("plugin-dispose"); base.Dispose(); }

    private sealed class Editor(int serial, string mode, FrameworkElement view, IDesktopServiceHost service, Signals signals) : IDesktopConversationSettingsEditor
    {
        private int viewReads;
        private CancellationTokenRegistration throwingCallback;
        public void ObserveAvailability(CancellationToken token)
        {
            if (mode == "cancel-callback-throws") throwingCallback = token.Register(() =>
            {
                signals.Sink!("throwing-cancellation:" + serial);
                throw new InvalidOperationException("Deliberate plugin cancellation callback failure.");
            });
        }
        public FrameworkElement View
        {
            get
            {
                signals.Sink!("view:" + serial); viewReads++;
                return mode switch
                {
                    "null-view" => null!,
                    "changing-view" => new TextBox(),
                    "mutating-view" when viewReads > 2 => new TextBox(),
                    _ => view
                };
            }
        }
        public async Task<DesktopOperationResult> SaveAsync(CancellationToken cancellationToken = default)
        {
            await service.ExecuteServiceCommandAsync("fixture.settings.save", new { Serial = serial, Mode = mode }, cancellationToken);
            return mode switch
            {
                "uncertain" => new(DesktopOperationState.Uncertain, "Receipt lost."),
                "bad-state" => new((DesktopOperationState)777),
                "long-message" => new(DesktopOperationState.Completed, new string('x', 2_001)),
                "bad-request" => new(DesktopOperationState.Completed, RequestId: "bad\0request"),
                "bad-job" => new(DesktopOperationState.Completed, JobId: new string('x', 129)),
                "null-receipt" => null!,
                "save-throws" => throw new IOException("Deliberate lost save reply."),
                "accepted" => new(DesktopOperationState.Accepted, "Accepted asynchronous work."),
                "rejected" => new(DesktopOperationState.Rejected, "Rejected by plugin."),
                "failed" => new(DesktopOperationState.Failed, "Confirmed failure."),
                _ => new(DesktopOperationState.Completed, "Saved.")
            };
        }
        public void Cancel()
        {
            signals.Sink!("cancel:" + serial);
            if (mode is "cancel-throws" or "report-throws") throw new InvalidOperationException("Deliberate discard failure.");
        }
        public void Dispose()
        {
            throwingCallback.Dispose();
            signals.Sink!("dispose:" + serial);
            if (mode == "dispose-throws") throw new InvalidOperationException("Deliberate settings editor disposal failure.");
        }
    }
}
