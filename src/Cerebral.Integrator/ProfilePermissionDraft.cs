using System.ComponentModel;
using System.Runtime.CompilerServices;
using Cerebral.Core;
using Cerebral.PermissionUi;

namespace Cerebral.Integrator;

// Local UI model only. Discovery describes choices; no constructor/refresh grants access.
internal sealed record ProfilePermissionCatalog(IReadOnlyList<ToolDescriptor> Tools, bool Complete, string Status,
    IReadOnlyList<string>? RegisteredCapabilities = null, IReadOnlyDictionary<string, string>? CapabilityDescriptions = null);
internal sealed class ProfilePermissionDraft
{
    internal const string ExactOptInTool = "file.edit-range";
    private readonly List<string> tools;
    private readonly List<string> capabilities;
    internal ProfilePermissionDraft(IEnumerable<string> tools, IEnumerable<string> capabilities)
    {
        this.tools = Copy(tools); this.capabilities = Copy(capabilities);
    }
    private static List<string> Copy(IEnumerable<string> values)
    {
        var result = values.ToList();
        if (result.Count > 1024 || result.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256 || x.Any(char.IsControl)))
            throw new ArgumentException("Saved permission IDs are invalid or exceed the supported bounds.");
        return result;
    }
    internal IReadOnlyList<string> Tools => tools.AsReadOnly();
    internal IReadOnlyList<string> Capabilities => capabilities.AsReadOnly();
    internal bool AllTools => tools.Contains("*", StringComparer.Ordinal);
    internal bool ExplicitTool(string id) => tools.Contains(id, StringComparer.Ordinal);
    internal bool Capability(string id) => capabilities.Contains(id, StringComparer.Ordinal);
    internal bool AllowsTool(string id) => ExplicitTool(id) || id != ExactOptInTool && AllTools;
    internal bool Effective(ToolDescriptor tool) => AllowsTool(tool.Name) && Capability(tool.Capability);
    internal void SetTool(string id, bool allowed) => Set(tools, id, allowed);
    internal void SetCapability(string id, bool allowed) => Set(capabilities, id, allowed);
    private static void Set(List<string> list, string id, bool allowed)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl))
            throw new ArgumentException("A valid exact permission ID is required.");
        if (allowed) { if (!list.Contains(id, StringComparer.Ordinal)) { if (list.Count == 1024) throw new ArgumentException("At most 1024 grants are supported."); list.Add(id); } }
        else list.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal));
    }
    internal void Restore(ProfilePermissionDraft value)
    {
        tools.Clear(); tools.AddRange(value.tools);
        capabilities.Clear(); capabilities.AddRange(value.capabilities);
    }
    internal ProfilePermissionDraft Copy() => new(tools, capabilities);
    internal bool Same(ProfilePermissionDraft other) => tools.SequenceEqual(other.tools, StringComparer.Ordinal) &&
        capabilities.SequenceEqual(other.capabilities, StringComparer.Ordinal);
}

internal sealed class ProfilePermissionChoice : INotifyPropertyChanged
{
    private readonly ProfilePermissionDraft draft;
    private readonly Action changed;
    internal readonly bool IsCapability;
    internal readonly ToolDescriptor? Descriptor;
    private readonly bool knownCapability;
    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string SearchText => DisplayName + " " + Id + " " + Description;
    public string AutomationName => (IsCapability ? "Allow capability " : "Allow tool ") + DisplayName + " (" + Id + ")";
    public bool CanChange => IsCapability || Id == ProfilePermissionDraft.ExactOptInTool || !draft.AllTools;
    public bool Allow
    {
        get => IsCapability ? draft.Capability(Id) : draft.AllowsTool(Id);
        set
        {
            if (!CanChange || value == Allow) return;
            if (IsCapability) draft.SetCapability(Id, value); else draft.SetTool(Id, value);
            changed();
        }
    }
    public string EffectiveText => IsCapability
        ? (Allow ? knownCapability ? "Granted to this profile" : "Saved grant · availability unknown" : "Not granted")
        : Descriptor is null ? (draft.AllowsTool(Id) ? "Saved grant · availability unknown" : "Not granted")
        : !draft.AllowsTool(Id) ? "Not granted"
        : !draft.Capability(Descriptor.Capability) ? "Needs capability"
        : draft.AllTools && Id != ProfilePermissionDraft.ExactOptInTool ? "Allowed through *" : "Allowed by profile";
    public string Details => Description + "\n\nExact ID: " + Id + (Descriptor is null ? "" : "\nRequired capability: " + Descriptor.Capability) +
        (IsCapability ? "" : "\n\nAllowance is not a readiness guarantee. Tool, path, package, machine and current revocation checks still apply.") +
        (Id == ProfilePermissionDraft.ExactOptInTool ? "\nThis tool requires an exact opt-in; wildcard * does not include it." : "");
    internal ProfilePermissionChoice(ProfilePermissionDraft draft, string id, bool capability,
        ToolDescriptor? descriptor, Action changed, bool knownCapability = true, string? capabilityDescription = null)
    {
        this.draft = draft; Id = id; IsCapability = capability; Descriptor = descriptor; this.changed = changed; this.knownCapability = knownCapability;
        DisplayName = PermissionLabels.Name(id, capability);
        Description = descriptor?.Description ?? capabilityDescription ?? (capability
            ? knownCapability ? "Controls this capability for every chat using this profile. Tool allowance is checked separately." : "This exact saved capability is not described by the current catalog. Its saved value is retained until you explicitly remove it."
            : "This exact saved tool is not described by the current catalog. Its saved value is retained until you explicitly remove it.");
    }
    internal void Refresh()
    {
        foreach (var name in new[] { nameof(Allow), nameof(CanChange), nameof(EffectiveText) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

