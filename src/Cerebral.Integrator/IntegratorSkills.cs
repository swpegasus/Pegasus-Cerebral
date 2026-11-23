using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private sealed record SkillRow(SkillSummary Record)
    {
        public string Name => Record.Name;
        public string Id => Record.Id;
        public string Version => Record.Version;
        public string State => Record.Enabled ? "Enabled" : "Disabled";
        public string Scope => Record.Bindings.Count == 0 ? "Not assigned" : string.Join(", ", Record.Bindings.Select(x =>
            x.Scope == "global" ? "global" : x.Scope + ":" + x.Target));
        public string Source => Record.BuiltIn ? "Built in" : Record.Source;
    }

    private readonly DataGrid skills = Ui.Grid(("Skill", "Name", 2), ("ID", "Id", 1.5), ("Version", "Version", .8),
        ("State", "State", .8), ("Assigned to", "Scope", 2.2), ("Source", "Source", 2));
    private Button? skillConfigure, skillInspect, skillRemove;

    private UIElement SkillsPage()
    {
        skillConfigure = Ui.Button("Configure", () => Run(ConfigureSkill));
        skillInspect = Ui.Button("Inspect", () => Run(InspectSkill));
        skillRemove = Ui.Button("Remove", () => Run(RemoveSkill));
        skills.SelectionChanged += (_, _) => RefreshSkillSelectionActions();
        ServiceStatusChanged += (_, _) => RefreshSkillSelectionActions();
        RefreshSkillSelectionActions();
        return Ui.Page("Instruction skills",
            "Skills are bounded Markdown guidance loaded only for assigned agents and relevant work. They cannot add tools, paths, capabilities, or administrative access.",
            skills, Ui.Button("Install / update package…", () => Run(InstallSkill), true),
            skillConfigure, skillInspect, skillRemove, Ui.Button("Refresh", () => Run(RefreshSkills)));
    }

    private void RefreshSkillSelectionActions()
    {
        var selected = skills.SelectedItem as SkillRow;
        if (skillConfigure is not null) { skillConfigure.IsEnabled = IsConnected && selected is not null; skillConfigure.ToolTip = selected is null ? "Select a skill to configure it." : !IsConnected ? "Connect to the Worker to configure skills." : null; }
        if (skillInspect is not null) { skillInspect.IsEnabled = IsConnected && selected is not null; skillInspect.ToolTip = selected is null ? "Select a skill to inspect it." : !IsConnected ? "Connect to the Worker to inspect skills." : null; }
        if (skillRemove is not null)
        {
            skillRemove.IsEnabled = IsConnected && selected is { Record.BuiltIn: false };
            skillRemove.ToolTip = selected is null ? "Select a skill to remove it." : selected.Record.BuiltIn ? "Built-in skills cannot be removed; configure their scope instead." : !IsConnected ? "Connect to the Worker to remove skills." : null;
        }
    }

    private async Task RefreshSkills()
    {
        var selected = (skills.SelectedItem as SkillRow)?.Id;
        var values = await Get<List<SkillSummary>>("skills.list");
        var rows = values.Select(x => new SkillRow(x)).ToList();
        skills.ItemsSource = rows;
        skills.SelectedItem = rows.FirstOrDefault(x => x.Id == selected);
        RefreshSkillSelectionActions();
    }

    private async Task InstallSkill()
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose a Cerebral skill manifest",
            Filter = "Cerebral skill manifest (skill.json)|skill.json|JSON manifest (*.json)|*.json",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        var manifestBytes = await ReadBoundedFile(picker.FileName, 64 * 1024, "Skill manifest");
        var manifest = JsonSerializer.Deserialize<SkillManifest>(manifestBytes, CerebralJson.Options)
            ?? throw new InvalidDataException("The skill manifest is empty.");
        var markdownPath = Path.Combine(Path.GetDirectoryName(picker.FileName)!, "SKILL.md");
        if (!File.Exists(markdownPath)) throw new FileNotFoundException("The skill package must place SKILL.md beside skill.json.", markdownPath);
        var content = System.Text.Encoding.UTF8.GetString(await ReadBoundedFile(markdownPath, 32 * 1024, "SKILL.md"));
        var existing = (await Get<List<SkillSummary>>("skills.list")).SingleOrDefault(x => x.Id == manifest.Id);
        if (existing?.BuiltIn == true) throw new InvalidOperationException("Built-in skills are updated with Pegasus Cerebral.");
        var review = new FieldsDialog(this, existing is null ? "Install skill" : "Update skill",
            "Review the bounded instruction package. Installation never runs scripts and does not change agent grants. Configure its scope after saving.",
            existing is null ? "Install disabled" : "Update")
            .Add("manifest", "Manifest", Ui.Serialize(manifest), true)
            .Add("content", "SKILL.md", content, true);
        if (review.ShowDialog() != true) return;
        await Command("skills.install", new
        {
            manifest = Ui.Parse<SkillManifest>(review.Raw("manifest")), content = review.Raw("content"),
            source = picker.FileName, expectedRevision = existing?.Revision
        });
        await RefreshSkills(); SetStatus(existing is null ? "Skill installed disabled. Configure its assignment before agents can use it." : "Skill package updated; saved assignments were preserved.");
    }

    private async Task ConfigureSkill()
    {
        var row = Selected<SkillRow>(skills);
        var current = await Get<SkillInspection>("skills.inspect", new { id = row.Id });
        var form = new FieldsDialog(this, "Configure skill · " + current.Summary.Name,
            "Bindings may be global, role, profile, or project. Changes apply to later model generations and never expand permissions.", "Save")
            .Check("enabled", "Enable this skill", current.Summary.Enabled)
            .Add("bindings", "Scope bindings", Ui.Serialize(current.Summary.Bindings), true);
        if (form.ShowDialog() != true) return;
        await Command("skills.configure", new
        {
            id = current.Summary.Id, enabled = form.Checked("enabled"),
            bindings = Ui.Parse<List<SkillBinding>>(form.Raw("bindings")), expectedRevision = current.Summary.Revision
        });
        await RefreshSkills(); SetStatus("Skill configuration saved. New work receives the resolved skill catalog; matching guidance loads only when relevant.");
    }

    private async Task InspectSkill()
    {
        var row = Selected<SkillRow>(skills);
        Ui.ShowText(this, "Skill · " + row.Name, Ui.Serialize(await Get<SkillInspection>("skills.inspect", new { id = row.Id })));
    }

    private async Task RemoveSkill()
    {
        var row = Selected<SkillRow>(skills);
        if (row.Record.BuiltIn) throw new InvalidOperationException("Built-in skills cannot be removed; disable or narrow their bindings instead.");
        if (!Confirm($"Remove skill '{row.Name}'? This removes its saved instruction content and assignments.")) return;
        await Command("skills.remove", new { id = row.Id, expectedRevision = row.Record.Revision });
        await RefreshSkills(); SetStatus("Skill removed.");
    }

    private static async Task<byte[]> ReadBoundedFile(string path, int maximumBytes, string label)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximumBytes) throw new InvalidDataException($"{label} exceeds {maximumBytes / 1024} KiB.");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes); return bytes;
    }
}
