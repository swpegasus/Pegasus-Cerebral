using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.ChatIconsPlugin;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ChatIconsPlugin.Tests;

public static class Program
{
    internal static void RemoveFixtureDirectory(string directory)
    {
        var target = Path.GetFullPath(directory);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(target);
        if (!string.Equals(Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase) ||
            !new[] { "Pegasus.ChatIcons.Tests-", "Pegasus.ChatIcons.PackageTests-", "Cortex-Icon-Consumer-" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            throw new IOException("Unsafe Chat Icons fixture cleanup target.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
    private static int checks;
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--source-install")
            {
                if (args is not [_, var reviewedManifest]) throw new ArgumentException("Supply --source-install and the exact reviewed manifest only.");
                try { SourceChatIconsInstaller.Run(reviewedManifest); return 0; }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"Source Chat Icons admission failed ({error.GetType().Name}); review the printed phase proofs before retrying. Runtime load remains uninspected.");
                    return 1;
                }
            }
            if (args is ["--source-cortex-live"])
            {
                try { SourceCortexConsumerCanary.RunAsync().GetAwaiter().GetResult(); return 0; }
                catch (Exception error)
                {
                    // This opt-in lane never prints arbitrary live service/connection exception material.
                    Console.Error.WriteLine($"Source Cortex consumer canary failed ({error.GetType().Name}); completed per-reference proof above remains partial, and no retry is performed.");
                    return 1;
                }
            }
            if (args is ["--cortex-only"])
            {
                _ = new Application();
                var libraryChecks = CortexIconLibraryChecks.Run();
                Console.WriteLine($"Cortex Chat Icons: {libraryChecks} deterministic resource/cache/preview/export checks passed; no live service, network, app, clipboard, model or deployment.");
                return 0;
            }
            if (args.Length is not (2 or 3)) throw new ArgumentException("Supply pack root and built native plugin manifest.");
            _ = new Application();
            var pack = AssetPack.Load(args[0]);
            Check(pack.Id == ChatIconsPlugin.PluginId && pack.Assets.Count == 11, "Portable namespaced inventory");
            Check(pack.CatalogSha256.Length == 64 && pack.ContentSha256.Length == 64, "Path-independent digests");
            Check(pack.Find("status.success") == pack.Find("pegasus.chat-icons.status.success"), "Compatibility alias");
            Check(pack.Reference(pack.Find("status.success")).Contains("file=svg/"), "Reference maps to actual target");
            var scratch = Path.Combine(Path.GetTempPath(), "Pegasus.ChatIcons.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                var exported = Path.Combine(scratch, "whole");
                pack.Export(exported);
                var relocated = AssetPack.Load(exported);
                Check(relocated.ContentSha256 == pack.ContentSha256, "Relocation and whole-pack content identity");
                Check(File.Exists(Path.Combine(exported, "Licensing", "Fluent-System-Icons-LICENSE.txt")), "Whole export includes license");
                var single = Path.Combine(scratch, "single");
                pack.Export(single, "status.success");
                var one = AssetPack.Load(single);
                Reject(() => ChatIconsPlugin.ValidatePickerPack(one), "Valid individual export is not a complete picker pack");
                Check(one.Assets.Count == 1 && one.Find("status.success").Sha256 == pack.Find("status.success").Sha256, "Individual icon export preserves key/hash");
                Check(File.Exists(Path.Combine(single, "Licensing", "Fluent-System-Icons-NOTICE.txt")), "Individual export includes notices");
                Check(File.ReadAllText(Path.Combine(single, "SOURCE-REFERENCE.txt")).Contains(pack.CatalogSha256), "Individual export retains parent catalog mapping");
                Reject(() => pack.Export(single), "Existing export destination is preserved");
                Reject(() => pack.Find("not-an-icon"), "Unknown exact icon key");
                Corrupt(pack, scratch, "digest", root => File.AppendAllText(Path.Combine(root, one.Assets[0].RelativeFile), "changed"));
                Corrupt(pack, scratch, "path", root => RewriteCatalog(root, "svg/ic_fluent_checkmark_circle_20_regular.svg", "../escaped.svg"));
                Corrupt(pack, scratch, "key-alias-collision", root => RewriteCatalog(root, "alias=chat.attachment", "alias=pegasus.chat-icons.status.success"));
                Corrupt(pack, scratch, "duplicate-alias", root => RewriteCatalog(root, "alias=chat.attachment", "alias=status.success"));
                Corrupt(pack, scratch, "missing-license", root => File.Delete(Path.Combine(root, "Licensing", "Fluent-System-Icons-LICENSE.txt")));
                Corrupt(pack, scratch, "duplicate", root => File.AppendAllText(Path.Combine(root, "catalog.ini"), "\n[pack]\nid=duplicate\n"));
                Corrupt(pack, scratch, "wrong-contract", root => RewriteCatalog(root, "pegasus.asset-pack/1", "pegasus.asset-pack/99"));
                Corrupt(pack, scratch, "oversize", root => File.WriteAllText(Path.Combine(root, "catalog.ini"), new string('x', AssetPack.MaximumCatalogBytes + 1)));
                Corrupt(pack, scratch, "active-svg", root => ReplaceSvg(root, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script><path d=\"M0 0\"/></svg>"));
                Corrupt(pack, scratch, "external-svg", root => ReplaceSvg(root, "<svg xmlns=\"http://www.w3.org/2000/svg\"><path fill=\"url(https://example.invalid/a)\" d=\"M0 0\"/></svg>"));
                Corrupt(pack, scratch, "dtd-svg", root => ReplaceSvg(root, "<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///unrelated\">]><svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0\"/></svg>"));
                Corrupt(pack, scratch, "stylesheet-svg", root => ReplaceSvg(root, "<?xml-stylesheet href=\"https://example.invalid/icon.css\"?><svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0\"/></svg>"));
                Corrupt(pack, scratch, "escaped-css-url-svg", root => ReplaceSvg(root, "<svg xmlns=\"http://www.w3.org/2000/svg\"><path fill=\"u\\72l(https://example.invalid/a.svg#p)\" d=\"M0 0\"/></svg>"));
                var rolesPath = Path.Combine(scratch, "plugin-data", RoleSettings.FileName);
                Check(RoleSettings.Load(rolesPath) == RoleSettings.Defaults && !File.Exists(rolesPath), "Missing first-use role settings are defaults without writes");
                var choices = RoleSettings.Defaults.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                choices["Go"] = "pegasus.chat-icons.status.success";
                RoleSettings.Save(rolesPath, choices);
                Check(RoleSettings.Load(rolesPath)["Go"] == choices["Go"], "Role settings persist exact stable key");
                var beforeRoles = File.ReadAllBytes(rolesPath);
                choices["Go"] = "arbitrary.icon";
                Reject(() => RoleSettings.Save(rolesPath, choices), "Invalid role choice rejects before writing");
                Check(File.ReadAllBytes(rolesPath).SequenceEqual(beforeRoles), "Rejected role save preserves prior settings");
                File.AppendAllText(rolesPath, "Go=pegasus.chat-icons.status.error\\n");
                Reject(() => RoleSettings.Load(rolesPath), "Duplicate role settings rejected");
                Reject(() => pack.Reference(pack.Find("status.success") with { Sha256 = new string('0', 64) }), "Reference cannot forge an asset binding");
                var changedLicense = AssetPack.Load(exported);
                File.AppendAllText(Path.Combine(exported, "Licensing", "Fluent-System-Icons-LICENSE.txt"), "changed");
                Reject(() => changedLicense.Export(Path.Combine(scratch, "changed-license-export")), "License changed after admission rejects export");
                Check(!Directory.Exists(Path.Combine(scratch, "changed-license-export")), "Failed export leaves no false output");
            }
            finally { RemoveFixtureDirectory(scratch); }

            var host = new FakeHost();
            using var plugin = new ChatIconsPlugin();
            plugin.Initialize(host);
            Check(plugin.Panels.Count == 0 && plugin.ComposerActions.Count == 0 && plugin.TextEntryActions.Count == 1, "Only needed SDK contributions");
            Check(plugin.TextEntryActions[0].Id == "icons", "Discoverable text-entry action");
            Check(plugin.ConfigurationPanels.Count == 1, "Role settings use existing configuration contribution");
            var configuration = (StackPanel)plugin.ConfigurationPanels[0].CreateView();
            Check(configuration.VerticalAlignment == VerticalAlignment.Top, "Configuration is top aligned");
            Check(configuration.Children.OfType<ComboBox>().Count() == 5, "Five editable role choices without new SDK contract");
            var entry = new FakeEntry("alpha selected omega", 6, 8);
            var result = ChatIconsPlugin.InsertAssetAsync(host, entry, host.Binding, "✓", default).GetAwaiter().GetResult();
            Check(result.State == DesktopOperationState.Completed && entry.Text == "alpha ✓ omega", "Insertion replaces selection at current cursor");
            Check(entry.SelectionStart == 7 && entry.SelectionLength == 0, "Insertion preserves cursor semantics");
            Check(host.Submissions == 0 && host.DraftReplacements == 0, "Picker never submits or replaces whole draft");
            var old = host.Binding;
            host.Binding = new("new", "another");
            result = ChatIconsPlugin.InsertAssetAsync(host, entry, old, "✗", default).GetAwaiter().GetResult();
            Check(result.State == DesktopOperationState.Rejected && entry.Inserts == 1, "Conversation mismatch prevents stale edit");
            entry.Unavailable.Cancel();
            result = ChatIconsPlugin.InsertAssetAsync(host, entry, host.Binding, "✗", default).GetAwaiter().GetResult();
            Check(result.State == DesktopOperationState.Rejected && entry.Inserts == 1, "Unavailable text entry prevents edit");
            var denied = new FakeEntry("keep", 4, 0) { RejectInsert = true };
            result = ChatIconsPlugin.InsertAssetAsync(host, denied, host.Binding, "✓", default).GetAwaiter().GetResult();
            Check(result.State == DesktopOperationState.Rejected && denied.Text == "keep", "Host limit/rejection remains visible");
            var focusFailure = new FakeEntry("text", 4, 0) { FailFocus = true };
            result = ChatIconsPlugin.InsertAssetAsync(host, focusFailure, host.Binding, "✓", default).GetAwaiter().GetResult();
            Check(result.State == DesktopOperationState.Completed && focusFailure.Text == "text✓" && focusFailure.Inserts == 1, "Committed insertion not reported failed/replayed after focus error");
            Reject(() => ChatIconsPlugin.InsertAssetAsync(host, denied, host.Binding, "✓", new CancellationToken(true)).GetAwaiter().GetResult(), "Cancellation before mutation");

            foreach (var role in new[] { "user", "assistant", "tool" })
            {
                var message = new DesktopMessageSnapshot("same-id", host.Binding, role, "original café\nsecond line", DateTimeOffset.UtcNow);
                var first = plugin.CreateMessagePresentation(message)!;
                var second = plugin.CreateMessagePresentation(message)!;
                Check(first.Replacement is null && first.Decoration is not null, role + " only decoration");
                Check(!ReferenceEquals(first.Decoration, second.Decoration) && first.Decoration!.Parent is null, role + " fresh unparented control");
                Check(message.Text == "original café\nsecond line" && message.Id == "same-id" && message.Role == role, role + " original snapshot unchanged");
                Check(!string.IsNullOrEmpty(AutomationProperties.GetName(first.Decoration!)), role + " accessible name");
                var icon = (Control)first.Decoration!;
                icon.Resources[DesktopThemeResourceKeys.TextBrush] = Brushes.White;
                Check(Equals(icon.Foreground, Brushes.White), role + " resolves light ink");
                icon.Resources[DesktopThemeResourceKeys.TextBrush] = Brushes.Black;
                Check(Equals(icon.Foreground, Brushes.Black), role + " resolves dark ink");
            }
            Check(plugin.CreateMessagePresentation(new("system", host.Binding, "system", "not a guessed status", DateTimeOffset.UtcNow)) is null, "Unknown role is undecorated");
            Check(ChatIconsPlugin.ResolvePackDirectory(null, Path.GetDirectoryName(args[1])) is not null, "Dynamic package resource fallback");
            var identity = NativeDesktopPluginPackage.Inspect(args[1]);
            NativeDesktopPluginPackage.ValidateEntryPoint(identity);
            Check(identity.Id == ChatIconsPlugin.PluginId && identity.EntryType == typeof(ChatIconsPlugin).FullName, "Exact compiled package manifest admitted");
            var packageFixture = Path.Combine(Path.GetTempPath(), "Pegasus.ChatIcons.PackageTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packageFixture);
            try
            {
                var packagePath = args.Length == 3 ? Path.GetFullPath(args[2]) : Path.Combine(packageFixture, "chat-icons.CerebralPlugin");
                var paths = new DesktopProductPaths(
                    Path.Combine(packageFixture, "program"), Path.Combine(packageFixture, "data"),
                    Path.Combine(packageFixture, "user"), Path.Combine(packageFixture, "projects"),
                    Path.Combine(packageFixture, "models"), Path.Combine(packageFixture, "tools"),
                    Path.Combine(packageFixture, "shared"));
                var info = new DesktopPluginInfo(identity.Id, identity.Name, identity.Version, identity.Description, false,
                    Path.GetDirectoryName(args[1])!, args[1], null);
                CerebralPluginPackage.Export(info, packagePath, "none", paths);
                using var prepared = CerebralPluginPackage.Prepare(packagePath);
                Check(prepared.Inspection.Package.SchemaVersion == 2 && prepared.Inspection.Package.Resources?.Count == 1, "Existing container declares one shared resource");
                Check(!Directory.Exists(Path.Combine(Path.GetDirectoryName(prepared.DesktopManifestPath)!, "Resources", "Icons", "chat-v1")), "Shared asset payload is excluded from native plugin install");
                Check(File.Exists(Path.Combine(Path.GetDirectoryName(prepared.DesktopManifestPath)!, "Licensing", "Fluent-System-Icons-LICENSE.txt")), "Native code package retains license copy");
                Check(!File.Exists(Path.Combine(Path.GetDirectoryName(prepared.DesktopManifestPath)!, "Cerebral.Desktop.Plugins.dll")), "Native package never carries a private SDK DLL");
                using var installation = prepared.BeginResourceInstallation(paths);
                installation.Publish();
                var installedPack = AssetPack.Load(Path.Combine(paths.SharedDirectory, "Icons", "chat-v1"));
                Check(installedPack.ContentSha256 == pack.ContentSha256, "Fixture resource install preserves exact shared asset identity");
                Check(ChatIconsPlugin.ResolvePackDirectory(paths, null) == installedPack.Root, "Dynamic resolver selects configured shared root");
                installation.Commit();
            }
            finally { RemoveFixtureDirectory(packageFixture); }
            Console.WriteLine($"Chat Icons: {checks} deterministic catalog/export/native contract checks passed; no live app, clipboard mutation, network, model inference or deployment.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Corrupt(AssetPack pack, string scratch, string name, Action<string> mutation)
    {
        var root = Path.Combine(scratch, name); pack.Export(root); mutation(root);
        Reject(() => AssetPack.Load(root), name + " rejected");
    }
    private static void RewriteCatalog(string root, string oldText, string newText)
    {
        var path = Path.Combine(root, "catalog.ini"); File.WriteAllText(path, File.ReadAllText(path).Replace(oldText, newText));
    }
    private static void ReplaceSvg(string root, string text)
    {
        var pack = AssetPack.Load(root); var asset = pack.Assets[0];
        File.WriteAllText(Path.Combine(root, asset.RelativeFile), text, new UTF8Encoding(false));
        RewriteCatalog(root, asset.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, asset.RelativeFile)))));
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception("Failed: " + name); checks++; }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.Xml.XmlException or KeyNotFoundException or OperationCanceledException) { checks++; return; }
        throw new Exception("Expected rejection: " + name);
    }

    private sealed class FakeEntry(string text, int start, int length) : IDesktopTextEntry, IDesktopTextEntryLifetime, IDesktopTextEntryFocus
    {
        public string Id => "composer";
        public string Purpose => "test";
        public string Text { get; private set; } = text;
        public int SelectionStart { get; private set; } = start;
        public int SelectionLength { get; private set; } = length;
        public CancellationTokenSource Unavailable { get; } = new();
        public CancellationToken AvailabilityToken => Unavailable.Token;
        public bool IsKeyboardFocusWithin => false;
        public bool RejectInsert { get; init; }
        public bool FailFocus { get; init; }
        public int Inserts { get; private set; }
        public Task<DesktopOperationResult> InsertAsync(string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RejectInsert) return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "Host text limit."));
            Text = Text.Remove(SelectionStart, SelectionLength).Insert(SelectionStart, value);
            SelectionStart += value.Length; SelectionLength = 0; Inserts++;
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
        public Task<DesktopOperationResult> FocusAsync(CancellationToken cancellationToken = default)
        {
            if (FailFocus) throw new InvalidOperationException("Fixture focus failure.");
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
    }
    private sealed class FakeHost : IDesktopPluginHost
    {
        public DesktopConversationBinding Binding { get; set; } = new("generation", "session");
        public int Submissions { get; private set; }
        public int DraftReplacements { get; private set; }
        public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged { add { } remove { } }
        public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted { add { } remove { } }
        public DesktopConversationSnapshot GetConversation() => new(Binding, "Test", null, null, false);
        public DesktopDraftSnapshot GetDraft() => new(Binding, 1, "untouched");
        public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update, CancellationToken cancellationToken = default) { DraftReplacements++; return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed)); }
        public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) { Submissions++; return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed)); }
        public Task<DesktopOperationResult> SteerAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding binding, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void DismissInputGuidance() { }
        public void ReportStatus(string text) { }
        public void RefreshPresentation() { }
    }
}
