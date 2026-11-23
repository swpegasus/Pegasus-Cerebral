using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.SkillsPlugin;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-skills-fixture-" + Guid.NewGuid().ToString("N")));
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            ScopedFilePolicy.RejectLinks(root); Directory.CreateDirectory(root);
            var task = RunAsync(root);
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult(); // The fixture task is already terminal; production code never waits this way.
            Console.WriteLine($"PASS {checks} focused Skills assertions. Synthetic owner commands and temporary files/ZIPs; no real Worker, model, installed app or user data.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var verified = Path.GetFullPath(root);
            if (!ScopedFilePolicy.IsWithin(verified, temporary) ||
                !Path.GetFileName(verified).StartsWith("cerebral-skills-fixture-", StringComparison.Ordinal))
                throw new IOException("Refusing cleanup outside the exact owned fixture root.");
            ScopedFilePolicy.RejectLinks(verified);
            if (Directory.Exists(verified)) Directory.Delete(verified, recursive: true);
        }
    }

    private static async Task RunAsync(string root)
    {
        Documents();
        ScanLimits(root);
        BundledContent(root);
        Archives(root);
        await ManagerAsync(root);
        await PartialImportAsync(root);
        CompleteOutcomeViewer();
    }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + label);
        checks++;
    }
    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException) { return true; }
    }
    private static async Task<bool> RejectsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException) { return true; }
    }
    private static SkillManifest Manifest(string id = "fixture-one") =>
        new() { Id = id, Name = "Fixture skill", Version = "1.0.0", Description = "A bounded fixture instruction.", Triggers = ["fixture task"] };
    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options);
    private static byte[] Text(string value) => SkillFiles.Utf8.GetBytes(value);
    private const string Markdown = "---\nname: Fixture skill\ndescription: A bounded fixture instruction.\n---\n\nUse only the currently allowed tools.\n";
    private static void Put(string directory, string markdown = Markdown, SkillManifest? manifest = null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "SKILL.md"), Text(markdown));
        File.WriteAllBytes(Path.Combine(directory, "skill.json"), Json(manifest ?? Manifest()));
    }

    private static void Documents()
    {
        SkillFiles.ValidateId(new string('A', 100));
        Check(Rejects(() => SkillFiles.ValidateId(new string('a', 101))) &&
            Rejects(() => SkillFiles.ValidateId("nonascii-界")) && Rejects(() => SkillFiles.ValidateId("a/b")),
            "local identifier preflight matches the Worker 1–100 ASCII grammar, not the code-package grammar");
        var doc = SkillDocument.Parse(Text(Markdown), null, "data/example", _ => throw new InvalidOperationException("No assets expected."));
        var changed = SkillDocument.Parse(Text(Markdown + "Another step.\n"), null, "data/example", _ => []);
        Check(doc.Manifest.Id == changed.Manifest.Id, "SKILL.md-only derived identity does not depend on mutable body");
        Check(doc.Fingerprint != changed.Fingerprint, "body changes invalidate the document fingerprint");
        Check(SkillDocument.Parse(Text(Markdown), Json(Manifest()), "data/a", _ => []).Manifest.Id == "fixture-one",
            "existing canonical manifest identity is preserved");
        Check(Rejects(() => SkillDocument.Parse([0xff], null, "x", _ => [])), "invalid UTF-8 is rejected");
        Check(Rejects(() => SkillDocument.Parse(Text("---\nname: a\nname: b\ndescription: d\n---\nbody"), null, "x", _ => [])),
            "duplicate frontmatter is rejected");
        Check(Rejects(() => SkillDocument.Parse(Text("---\nname: [a, b]\ndescription: d\n---\nbody"), null, "x", _ => [])),
            "unsupported YAML collection is rejected");
        Check(Rejects(() => SkillDocument.Parse(Text("---\nname: &anchor a\ndescription: d\n---\nbody"), null, "x", _ => [])),
            "unsupported YAML alias/tag form is rejected");
        var folded = SkillDocument.Frontmatter("---\nname: 'One'\ndescription: >\n  first line\n  second line\n---\nbody");
        Check(folded["description"] == "first line second line", "documented folded scalar parses");
        var reads = 0;
        Check(Rejects(() => SkillDocument.Parse(Text("---\nname: a\ndescription: d\nassets: ../secret.txt\n---\nbody"),
            Json(Manifest()), "x", _ => { reads++; return []; })) && reads == 0, "asset traversal is rejected before opening an asset");
        Check(Rejects(() => SkillDocument.Parse(Text("---\nname: a\ndescription: d\nassets: run.ps1\n---\nbody"),
            Json(Manifest()), "x", _ => [])), "script asset types are rejected");
        Check(Rejects(() => SkillDocument.Parse(Text(Markdown), Json(Manifest("cerebral.base.fake")), "x", _ => [])),
            "derived base namespace cannot omit full license and attribution");
        Check(Rejects(() => SkillFiles.Relative("notes.txt.")) && Rejects(() => SkillFiles.Relative("CON.txt")),
            "Windows normalization/device collisions are rejected");
        Check(Rejects(() => SkillsPlugin.ValidateBinding(new("role", "Main"))) &&
              Rejects(() => SkillsPlugin.ValidateBinding(new("role", "coordinator"))), "no new Main/coordinator/Worker binding roles are invented");
        Check(SkillsPlugin.ValidateBinding(new("role", "helper")).Target == "helper", "existing helper role remains valid");
        Check(Rejects(() => SkillFiles.Relative("CON .txt")) && Rejects(() => SkillFiles.Relative("COM¹.txt")) &&
            Rejects(() => SkillFiles.Relative("LPT².txt")) && Rejects(() => SkillFiles.Relative("CONIN$.txt")),
            "passive source paths reject the shared Windows device aliases");
        Check(Rejects(() => SkillFiles.Relative(new string('界', 86))), "source segments enforce the shared 255 UTF-8 byte cap");
    }

    private static void ScanLimits(string root)
    {
        var data = Path.Combine(root, "scan-limit-data");
        var catalog = new SkillFolderCatalog(Path.Combine(root, "no-bundled-scan"), data);
        catalog.EnsureEditableRoot();
        Put(Path.Combine(catalog.EditableRoot, "Invalid"),
            "---\nname: [unsupported, yaml]\ndescription: fixture\n---\nbody", Manifest("bad"));
        Put(Path.Combine(catalog.EditableRoot, "Valid"), manifest: Manifest("good"));
        var scan = catalog.Scan(new SkillsIndex(), default);
        Check(scan.Complete && scan.Entries.Any(x => x.Id == "good" && x.Document is not null) &&
            scan.Entries.Any(x => x.Error is not null), "one invalid document remains a visible row without aborting its valid sibling");

        var budgetData = Path.Combine(root, "scan-budget-data");
        var bounded = new SkillFolderCatalog(Path.Combine(root, "no-bundled-budget"), budgetData);
        bounded.EnsureEditableRoot();
        var first = Path.Combine(bounded.EditableRoot, "A");
        var assets = Enumerable.Range(0, 32).Select(i => "a" + i.ToString("00") + ".txt").ToArray();
        Put(first, "---\nname: Budget\ndescription: fixture\nassets: " + string.Join(", ", assets) + "\n---\nbody",
            Manifest("budget-scan"));
        foreach (var asset in assets)
        {
            using var file = new FileStream(Path.Combine(first, asset), FileMode.CreateNew);
            file.SetLength(1024 * 1024); // Bounded sparse fixture, not retained in memory.
        }
        Put(Path.Combine(bounded.EditableRoot, "Z"), manifest: Manifest("must-not-be-read"));
        var limited = bounded.Scan(new SkillsIndex(), default);
        Check(!limited.Complete && limited.Entries.Count == 1 && limited.Entries[0].Document is not null &&
            limited.Error?.Contains("32 MiB", StringComparison.Ordinal) == true,
            "aggregate scan budget stops before reading later sources and reports incomplete");
    }

    private static void BundledContent(string root)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "BundledSkills", "CerebralBase");
        var directories = Directory.GetDirectories(bundled);
        Check(directories.Length == 8, "eight adapted base skills are bundled");
        var rows = directories.Select(directory => new SkillFolderEntry("bundled/CerebralBase/" + Path.GetFileName(directory),
            "bundled", directory, SkillDocument.Read(directory, directory), null)).ToArray();
        foreach (var row in rows)
        {
            var document = row.Document!;
            Check(document.Manifest.Id.StartsWith("cerebral.base.", StringComparison.Ordinal), "base skill has a stable product-scoped ID");
            Check(document.Assets.Any(x => x.Path == "LICENSE.txt" &&
                x.Sha256 == "0e7ac423bf2c6e223b7c5b156f8cf72da49d748e56a1641402c31f22ad07dbb5"), "complete upstream license is a mandatory declared asset");
            Check(document.Content.Contains("abeeaf95660ba9e8b7c753eb38f441ca707a8823", StringComparison.Ordinal) &&
                document.Content.Contains("Matt Pocock", StringComparison.Ordinal), "base instruction retains pinned source and adaptation credit");
        }
        var target = Path.Combine(root, "base-group.zip");
        SkillGroups.Export("Cerebral base", rows, target, null);
        using var group = SkillGroups.Open(target);
        Check(group.Members.Count == 8, "selected base group roundtrips through actual shared bounded archive reader");
        Check(group.Members.All(x => x.Document.Assets.Any(a => a.Path == "LICENSE.txt")), "every exported base member retains its full license");
    }

    private static void Archives(string root)
    {
        var folder = Path.Combine(root, "archive-source");
        Put(folder);
        File.WriteAllText(Path.Combine(folder, ".env"), "UNRELATED_FIXTURE_FILE");
        var row = new SkillFolderEntry("data/example", "data", folder, SkillDocument.Read(folder, "data/example"), null);
        var valid = Path.Combine(root, "valid.zip");
        SkillGroups.Export("Example", [row], valid, null);
        using (var archive = BoundedContentZipArchive.OpenRead(valid))
            Check(archive.Files.All(x => !x.FullName.EndsWith(".env", StringComparison.Ordinal)), "unknown/credential-like files are not exported");

        void Zip(string path, IEnumerable<(string Name, byte[] Bytes, bool Link)> files)
        {
            using var stream = new FileStream(path, FileMode.CreateNew);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name);
                if (file.Link) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var output = entry.Open(); output.Write(file.Bytes);
            }
        }
        var core = new (string, byte[], bool)[]
        {
            ("group.json", Json(new { schemaVersion = 1, groupName = "Example", skills = new[] { new { id = "fixture-one", directory = "skills/fixture-one" } } }), false),
            ("skills/fixture-one/skill.json", Json(Manifest()), false),
            ("skills/fixture-one/SKILL.md", Text(Markdown), false)
        };
        foreach (var (name, extra) in new[]
        {
            ("traversal", ("../bad.txt", Text("bad"), false)),
            ("script", ("skills/fixture-one/run.ps1", Text("bad"), false)),
            ("link", ("skills/fixture-one/link.txt", Text("target"), true)),
            ("normalization", ("skills/fixture-one/notes.txt.", Text("bad"), false)),
            ("directory", ("skills/fixture-one/", Array.Empty<byte>(), false))
        })
        {
            var path = Path.Combine(root, name + ".zip");
            Zip(path, core.Append(extra));
            Check(Rejects(() => { using var rejected = SkillGroups.Open(path); }), "reject group " + name + " before extraction");
        }
        var oversized = Enumerable.Range(0, 17).Select(number =>
        {
            var doc = new SkillDocument(Manifest("budget-" + number), Markdown,
                [new SkillAsset("asset.txt", 1024 * 1024, new string('a', 64))], true, "fixture", "");
            return new SkillFolderEntry("data/budget-" + number, "data",
                Path.Combine(root, "never-created-source-" + number), doc, null);
        }).ToArray();
        var rejectedByBudget = false;
        try { SkillGroups.Export("Over budget", oversized, Path.Combine(root, "must-not-exist.zip"), null); }
        catch (InvalidDataException error) when (error.Message.Contains("16 MiB", StringComparison.Ordinal)) { rejectedByBudget = true; }
        Check(rejectedByBudget && !File.Exists(Path.Combine(root, "must-not-exist.zip")),
            "aggregate export budget is rejected before opening absent sources or creating output");

        var collision = Path.Combine(root, "case-collision.zip");
        Zip(collision, core.Append(("skills/fixture-one/skill.JSON", Text("{}"), false)));
        Check(Rejects(() => { using var rejected = SkillGroups.Open(collision); }), "case-insensitive ZIP collision rejected");
        using (var group = SkillGroups.Open(valid))
        {
            var existing = new SkillSummary("fixture-one", "Existing", "1.0.0", "Existing content", true, false, 7, ["existing"], [new("global")], "other owner");
            Check(SkillGroups.Plan(group, [existing], SkillConflictChoice.Keep).Items.Single().Skip, "keep conflict preserves current skill");
            var separate = SkillGroups.Plan(group, [existing], SkillConflictChoice.SideBySide).Items.Single();
            Check(separate.Manifest.Id == "fixture-one-copy-1" && separate.Existing is null, "side-by-side receives an explicit separate stable ID");
            Check(SkillGroups.Plan(group, [existing], SkillConflictChoice.Replace).Items.Single().Existing?.Revision == 7, "replace plan pins reviewed revision");
            Check(Rejects(() => SkillGroups.Plan(group, [existing with { BuiltIn = true }], SkillConflictChoice.Replace)), "built-in replacement refused");
            var data = Path.Combine(root, "extraction-data"); Directory.CreateDirectory(data);
            group.Extract(data);
            var materialized = group.Materialize(separate, new SkillFolderCatalog(Path.Combine(root, "no-bundled"), data));
            Check(materialized.Entry.Document!.Manifest.Id == "fixture-one-copy-1" &&
                materialized.Files.ContainsKey("SKILL.md"), "reviewed import materializes only its declared content");
            var catalog = new SkillFolderCatalog(Path.Combine(root, "no-bundled"), data);
            var managed = new OwnedSkill { SourceKey = materialized.Entry.Key, ManagedFiles = materialized.Files.ToDictionary(x => x.Key, x => x.Value) };
            File.AppendAllText(Path.Combine(materialized.Entry.Directory, "SKILL.md"), "\nOwner edit.");
            File.WriteAllText(Path.Combine(materialized.Entry.Directory, "unknown.txt"), "Keep me.");
            catalog.RemoveManagedFiles(managed);
            Check(File.Exists(Path.Combine(materialized.Entry.Directory, "SKILL.md")) &&
                File.Exists(Path.Combine(materialized.Entry.Directory, "unknown.txt")), "uninstall preserves modified and unknown files");
        }
    }

    private static async Task ManagerAsync(string root)
    {
        var package = Path.Combine(root, "package-v1");
        var source = Path.Combine(package, "Skills", "Example"); Put(source);
        var data = Path.Combine(root, "manager-data");
        var fake = new OwnerFixture(package, data);
        var plugin = new SkillsPlugin(); plugin.Initialize(fake); plugin.ViewUnloaded();
        try
        {
            await plugin.RefreshAsync();
            var row = plugin.Snapshot().Rows.Single(x => x.Id == "fixture-one");
            Check(fake.InstallCalls == 0 && row.Catalog is null, "discovery never automatically installs or enables");
            await plugin.InstallAsync([row]);
            Check(fake.Records["fixture-one"].Summary is { Enabled: false, Revision: 1 } &&
                fake.Records["fixture-one"].Summary.Bindings.Count == 0, "new install is disabled and unassigned");
            row = plugin.Snapshot().Rows.Single(x => x.Id == "fixture-one");
            await plugin.ConfigureAsync([row], true, new("global"));
            Check(fake.Records["fixture-one"].Summary.Enabled, "explicit owner Enable uses existing scope");
            plugin.ViewLoaded();
            Check(plugin.Snapshot().Watching, "host-lifetime watcher starts for existing selected roots");
            plugin.ViewUnloaded();
            Check(!plugin.Snapshot().Watching && fake.Records["fixture-one"].Summary.Enabled,
                "closing the manager stops its watcher without changing core availability");
            var old = fake.Records["fixture-one"];
            fake.Records["fixture-one"] = old with { Summary = old.Summary with
                { Enabled = false, Revision = old.Summary.Revision + 1, Bindings = [new("profile", "helper-profile")] } };
            File.AppendAllText(Path.Combine(source, "SKILL.md"), "\nChanged source body.");
            await plugin.RefreshAsync();
            Check(!fake.Records["fixture-one"].Summary.Enabled &&
                fake.Records["fixture-one"].Summary.Bindings.Single().Target == "helper-profile", "refresh preserves external owner disablement and bindings");
            row = plugin.Snapshot().Rows.Single(x => x.Id == "fixture-one");
            var calls = fake.InstallCalls;
            await plugin.UninstallAsync([row], true);
            await plugin.RefreshAsync();
            Check(!fake.Records.ContainsKey("fixture-one") && fake.InstallCalls == calls, "bundled Uninstall is not undone by rescan");
            Check(File.Exists(Path.Combine(source, "SKILL.md")), "bundled code payload is never deleted");
        }
        finally { plugin.Dispose(); }

        var package2 = Path.Combine(root, "package-v2");
        Put(Path.Combine(package2, "Skills", "RenamedExample"));
        fake.Package = package2;
        var next = new SkillsPlugin(); next.Initialize(fake); next.ViewUnloaded();
        try
        {
            await next.RefreshAsync();
            var row = next.Snapshot().Rows.Single(x => x.Id == "fixture-one");
            Check(row.State.StartsWith("Excluded", StringComparison.Ordinal) && !fake.Records.ContainsKey("fixture-one"),
                "stable bundled origin/ID suppression survives a changed package path and folder name");
            await next.InstallAsync([row]);
            Check(fake.Records.ContainsKey("fixture-one") && !fake.Records["fixture-one"].Summary.Enabled, "explicit Install clears exclusion but keeps disabled default");
            fake.Records.Remove("fixture-one");
            var count = fake.InstallCalls;
            await next.RefreshAsync();
            Check(!fake.Records.ContainsKey("fixture-one") && fake.InstallCalls == count, "external owner removal is never silently undone");
            row = next.Snapshot().Rows.Single(x => x.Id == "fixture-one");
            await next.InstallAsync([row]);

            fake.Installation = "worker-b"; fake.Generation = "connection-b";
            fake.Records["fixture-one"] = new(new SkillSummary("fixture-one", "Foreign", "1.0.0", "Foreign definition", true,
                false, 1, ["foreign"], [new("global")], "foreign catalog"), "Foreign content");
            var removed = fake.RemoveCalls; var installed = fake.InstallCalls;
            await next.RefreshAsync();
            Check(next.Snapshot().NeedsOwnerReview, "installation switch requires owner review");
            row = next.Snapshot().Rows.Single(x => x.Id == "fixture-one");
            Check(await RejectsAsync(() => next.UninstallAsync([row], true)) && fake.RemoveCalls == removed, "old ownership index cannot remove a foreign catalog before review");
            await next.ReviewConnectionAsync("worker-b");
            row = next.Snapshot().Rows.Single(x => x.Id == "fixture-one");
            Check(await RejectsAsync(() => next.UninstallAsync([row], true)) && fake.RemoveCalls == removed &&
                fake.InstallCalls == installed && fake.Records["fixture-one"].Content == "Foreign content",
                "connection review does not authorize overwriting/removing mismatched source or content");
        }
        finally { next.Dispose(); }

        var lostPackage = Path.Combine(root, "lost-reply-package");
        Put(Path.Combine(lostPackage, "Skills", "Example"), manifest: Manifest("lost-reply"));
        var lost = new OwnerFixture(lostPackage, Path.Combine(root, "lost-data")) { LoseInstallReply = true };
        var uncertain = new SkillsPlugin(); uncertain.Initialize(lost); uncertain.ViewUnloaded();
        try
        {
            await uncertain.RefreshAsync();
            var row = uncertain.Snapshot().Rows.Single(x => x.Id == "lost-reply");
            Check(await RejectsAsync(() => uncertain.InstallAsync([row])), "lost install reply remains an explicit failure");
            var calls = lost.InstallCalls;
            await uncertain.RefreshAsync();
            Check(lost.InstallCalls == calls && uncertain.Snapshot().Rows.Single().State.StartsWith("Installed", StringComparison.Ordinal),
                "refresh confirms matching installed outcome without mutation retry");
            row = uncertain.Snapshot().Rows.Single();
            lost.LoseRemoveReply = true;
            Check(await RejectsAsync(() => uncertain.UninstallAsync([row], true)), "lost removal reply is retained as unconfirmed");
            await uncertain.RefreshAsync();
            Check(!lost.Records.ContainsKey("lost-reply") && lost.InstallCalls == calls &&
                uncertain.Snapshot().Rows.Single().State.StartsWith("Unconfirmed", StringComparison.Ordinal),
                "pending removal is never replayed or counteracted by automatic installation");
        }
        finally { uncertain.Dispose(); }
    }

    private static async Task PartialImportAsync(string root)
    {
        var definitions = Path.Combine(root, "partial-definitions");
        var rows = new List<SkillFolderEntry>();
        foreach (var suffix in new[] { "a", "b", "c" })
        {
            var directory = Path.Combine(definitions, suffix);
            Put(directory, manifest: Manifest("partial-" + suffix));
            rows.Add(new("data/" + suffix, "data", directory, SkillDocument.Read(directory, "data/" + suffix), null));
        }
        var zip = Path.Combine(root, "partial-import.zip");
        SkillGroups.Export("Partial fixture", rows, zip, null);
        using var archive = SkillGroups.Open(zip);
        var package = Path.Combine(root, "partial-package"); Directory.CreateDirectory(package);
        var fake = new OwnerFixture(package, Path.Combine(root, "partial-data"));
        var plugin = new SkillsPlugin(); plugin.Initialize(fake); plugin.ViewUnloaded();
        try
        {
            await plugin.RefreshAsync();
            var review = await plugin.ReviewImportAsync(archive);
            var plan = SkillGroups.Plan(archive, review.Owner.Skills, SkillConflictChoice.Keep);
            fake.FailHealthAfterInstallCalls = 1;
            var report = await plugin.ImportAsync(archive, plan, review.Owner, review.Existing);
            Check(report.Outcomes.Count == 3 && report.Outcomes[0].Contains("installed;", StringComparison.Ordinal) &&
                report.Outcomes[1].Contains(": stopped", StringComparison.Ordinal) &&
                report.Outcomes[2].Contains("not attempted", StringComparison.Ordinal),
                "partial import returns installed/stopped/unattempted item outcomes when later reads fail");
            Check(!string.IsNullOrEmpty(report.FollowupWarning) && report.FollowupWarning.Contains("refresh failed", StringComparison.Ordinal),
                "failed final refresh is a separate warning rather than replacing admitted outcomes");
            Check(fake.InstallCalls == 1 && fake.Records.ContainsKey("partial-a") && !fake.Records.ContainsKey("partial-b"),
                "failed final refresh never retries or replays an item mutation");
        }
        finally { plugin.Dispose(); }
    }

    private static void CompleteOutcomeViewer()
    {
        var outcomes = Enumerable.Range(1, 128).Select(i => "item-" + i.ToString("000") + ": not attempted").ToArray();
        var report = new SkillImportReport(outcomes, "Separate fixture refresh warning");
        var view = (Grid)SkillsManager.CreateOutcomeViewer(report);
        var text = view.Children.OfType<TextBox>().Single();
        var warning = view.Children.OfType<TextBlock>().Single();
        Check(text.Text.Split(Environment.NewLine).Length == 128 && text.Text.Contains("item-001", StringComparison.Ordinal) &&
            text.Text.Contains("item-128", StringComparison.Ordinal), "scrollable outcome viewer retains every admitted bounded item");
        Check(text.IsReadOnly && text.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
            AutomationProperties.GetName(text).Length != 0, "complete outcome list is keyboard-readable and named");
        Check(warning.Text == report.FollowupWarning && warning.Visibility == Visibility.Visible,
            "follow-up failure is shown separately from item outcomes");
    }

    private sealed class OwnerFixture(string package, string data) :
        IDesktopPluginHost, IDesktopPluginEnvironment, IDesktopServiceHost
    {
        internal string Package = package;
        internal string Installation = "worker-a", Generation = "connection-a";
        internal bool LoseInstallReply, LoseRemoveReply;
        internal int InstallCalls, RemoveCalls;
        internal int FailHealthAfterInstallCalls = int.MaxValue;
        internal Dictionary<string, SkillInspection> Records { get; } = new(StringComparer.Ordinal);
        public string DataDirectory => data;
        public string PackageDirectory => Package;
        public DesktopPluginSurface Surface => DesktopPluginSurface.Configuration;
        public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged { add { } remove { } }
        public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted { add { } remove { } }
        public DesktopConversationSnapshot GetConversation() => new(new(Generation, null), "Fixture", null, null, false);
        public DesktopDraftSnapshot GetDraft() => new(new(Generation, null), 0, "");
        public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesktopOperationResult> SteerAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding binding, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void DismissInputGuidance() { }
        public void ReportStatus(string text) { }
        public void RefreshPresentation() { }

        public Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = JsonSerializer.SerializeToElement(arguments, CerebralJson.Options);
            var id = args.TryGetProperty("id", out var identifier) ? identifier.GetString()! : "";
            object result;
            switch (operation)
            {
                case "health":
                    if (InstallCalls >= FailHealthAfterInstallCalls) throw new IOException("Fixture connection unavailable after the first admitted item.");
                    result = new HostInfo("Pegasus Cerebral", "fixture", 1, "Fixture", "fixture", "fixture", true, "unused", false, Installation); break;
                case "skills.list": result = Records.Values.Select(x => x.Summary).ToList(); break;
                case "skills.inspect": result = Records.GetValueOrDefault(id) ?? throw new InvalidOperationException("No fixture skill."); break;
                case "skills.install":
                    var manifest = JsonSerializer.Deserialize<SkillManifest>(args.GetProperty("manifest"), CerebralJson.Options)!;
                    var body = args.GetProperty("content").GetString()!;
                    var previous = Records.GetValueOrDefault(manifest.Id);
                    int? expected = args.TryGetProperty("expectedRevision", out var revision) && revision.ValueKind == JsonValueKind.Number ? revision.GetInt32() : null;
                    if (previous?.Summary.Revision != expected) throw new InvalidOperationException("Fixture revision conflict.");
                    var summary = new SkillSummary(manifest.Id, manifest.Name, manifest.Version, manifest.Description,
                        previous?.Summary.Enabled ?? false, false, (previous?.Summary.Revision ?? 0) + 1,
                        manifest.Triggers.ToArray(), previous?.Summary.Bindings ?? [], args.GetProperty("source").GetString()!);
                    result = Records[manifest.Id] = new(summary, body);
                    InstallCalls++;
                    if (LoseInstallReply) { LoseInstallReply = false; throw new IOException("Fixture reply lost after commit."); }
                    break;
                case "skills.configure":
                    var current = Records[id];
                    if (current.Summary.Revision != args.GetProperty("expectedRevision").GetInt32()) throw new InvalidOperationException("Fixture revision conflict.");
                    result = Records[id] = current with { Summary = current.Summary with
                    {
                        Revision = current.Summary.Revision + 1, Enabled = args.GetProperty("enabled").GetBoolean(),
                        Bindings = JsonSerializer.Deserialize<List<SkillBinding>>(args.GetProperty("bindings"), CerebralJson.Options)!
                    } };
                    break;
                case "skills.remove":
                    if (Records[id].Summary.Revision != args.GetProperty("expectedRevision").GetInt32()) throw new InvalidOperationException("Fixture revision conflict.");
                    Records.Remove(id); RemoveCalls++; result = new { id, removed = true };
                    if (LoseRemoveReply) { LoseRemoveReply = false; throw new IOException("Fixture removal reply lost after commit."); }
                    break;
                default: throw new InvalidOperationException("Unexpected fixture operation: " + operation);
            }
            return Task.FromResult(new DesktopServiceCommandResult(true, JsonSerializer.SerializeToElement(result, CerebralJson.Options)));
        }
    }
}
