using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed class PluginControlTextIdentity
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
    }
    private static readonly ConditionalWeakTable<TextBox, PluginControlTextIdentity> PluginControlTextIdentities = new();

    private FrameworkElement SelectedPluginControlView(string id)
    {
        if (desktopSidebarTabs.SelectedItem is not TabItem { Tag: string selected } || selected != id)
            throw new InvalidOperationException("Open the exact plugin sidebar before inspecting or invoking its controls.");
        var panel = desktopPlugins?.Panels.SingleOrDefault(item => item.PluginId + "." + item.Id == id) ??
            throw new InvalidOperationException("The selected plugin sidebar is no longer loaded.");
        if (!panel.View.IsVisible)
            throw new InvalidOperationException("The selected plugin view is hidden.");
        return panel.View;
    }

    // Traverse only the selected plugin's own view. The bounded set must be complete before
    // resolving a button so truncation can never hide an ambiguous duplicate name.
    private static IReadOnlyList<DependencyObject> PluginControlNodes(FrameworkElement view)
    {
        var pending = new Queue<DependencyObject>(); pending.Enqueue(view);
        var nodes = new List<DependencyObject>();
        while (pending.TryDequeue(out var node))
        {
            if (nodes.Count == 1024) throw new InvalidOperationException("The plugin control tree exceeds the diagnostic bound.");
            nodes.Add(node);
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                pending.Enqueue(VisualTreeHelper.GetChild(node, index));
        }
        return nodes;
    }

    private static string PluginButtonName(Button button) => new ButtonAutomationPeer(button).GetName();

    private static Button FindPluginControlButton(FrameworkElement view, string name)
    {
        var buttons = PluginControlNodes(view).OfType<Button>().Where(button => PluginButtonName(button) == name).ToArray();
        return buttons.Length == 1 ? buttons[0] : throw new InvalidOperationException(
            "The selected plugin view must contain exactly one button with that name; found " + buttons.Length + ".");
    }

    private static string PluginControlTextFieldId(TextBox field) =>
        PluginControlTextIdentities.GetValue(field, _ => new()).Id;

    private static (TextBox Field, IValueProvider Provider) PreparePluginControlTextEdit(
        FrameworkElement view, PluginControlTextEdit input)
    {
        _ = PluginControlTextEdits.Copy("setText", input.FieldId, input.ExpectedText, input.Text);
        var fields = PluginControlNodes(view).OfType<TextBox>()
            .Where(field => PluginControlTextFieldId(field) == input.FieldId).ToArray();
        if (fields.Length != 1)
            throw new InvalidOperationException("The selected plugin view no longer contains that exact inspected editable field.");
        var field = fields[0];
        if (!field.IsVisible || !field.IsEnabled || field.IsReadOnly)
            throw new InvalidOperationException("The exact inspected plugin field is hidden, disabled, or read-only.");
        if (!string.Equals(field.Text, input.ExpectedText, StringComparison.Ordinal))
            throw new InvalidOperationException("The exact inspected plugin field text changed. Inspect again before setting it.");
        var provider = new TextBoxAutomationPeer(field).GetPattern(PatternInterface.Value) as IValueProvider ??
            throw new InvalidOperationException("The exact inspected plugin field has no native editable value pattern.");
        if (provider.IsReadOnly)
            throw new InvalidOperationException("The exact inspected plugin field is read-only.");
        return (field, provider);
    }

    private static Cerebral.Core.PluginControlSnapshot PluginControlSnapshot(string id, FrameworkElement view)
    {
        var nodes = PluginControlNodes(view);
        var texts = new List<string>(); var remaining = 4096; var limited = false;
        // Persistence assertions require visible state; hidden labels cannot satisfy them.
        foreach (var text in nodes.OfType<TextBlock>().Where(text => text.IsVisible))
        {
            if (texts.Count == 20 || remaining == 0) { limited = true; break; }
            var take = Math.Min(text.Text.Length, Math.Min(remaining, 2048));
            texts.Add(text.Text[..take]); remaining -= take; limited |= take != text.Text.Length;
        }
        var allButtons = nodes.OfType<Button>().ToArray();
        var buttons = allButtons.Take(20).Select(button =>
        {
            var name = PluginButtonName(button); limited |= name.Length > 256;
            return new PluginControlButton(name[..Math.Min(name.Length, 256)], button.IsEnabled, button.IsVisible);
        }).ToArray();
        var allFields = nodes.OfType<TextBox>().ToArray();
        var fields = new List<PluginControlTextField>(); var fieldCharacters = 0;
        foreach (var field in allFields.Take(8))
        {
            var remainingFieldCharacters = PluginControlTextEdits.MaximumCharacters - fieldCharacters;
            if (remainingFieldCharacters == 0) { limited = true; break; }
            var take = Math.Min(field.Text.Length, remainingFieldCharacters);
            if (take > 0 && take < field.Text.Length && char.IsHighSurrogate(field.Text[take - 1])) take--;
            var fieldLimited = take != field.Text.Length;
            fields.Add(new(PluginControlTextFieldId(field), field.Text[..take], field.IsEnabled,
                field.IsVisible, field.IsReadOnly, fieldLimited));
            fieldCharacters += take; limited |= fieldLimited;
        }
        limited |= allFields.Length > fields.Count;
        return new(id, texts, buttons, limited || allButtons.Length > 20) { Fields = fields };
    }
}
