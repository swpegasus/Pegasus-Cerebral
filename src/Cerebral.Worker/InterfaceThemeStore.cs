using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Owner-only saved palette, independent of System/Light/Dark and replaceable plugin packages.</summary>
public sealed class InterfaceThemeStore(StateStore store)
{
    private const string Kind = "settings";
    private const string Id = "appearance-preset";
    private sealed record Saved(long Revision, string? Document);

    public DesktopThemePresetState Get()
    {
        var saved = store.Get<Saved>(Kind, Id);
        if (saved is null) return new(0, DesktopThemePresetPolicy.BlueWhite);
        if (saved.Revision < 0) throw new InvalidDataException("Saved theme revision is invalid.");
        if (saved.Document is null) return new(saved.Revision, DesktopThemePresetPolicy.BlueWhite);
        if (System.Text.Encoding.UTF8.GetByteCount(saved.Document) > DesktopThemePresetPolicy.MaximumUtf8Bytes)
            throw new InvalidDataException("The saved theme exceeds its size limit.");
        using var document = JsonDocument.Parse(saved.Document, new JsonDocumentOptions { MaxDepth = 8 });
        return new(saved.Revision, DesktopThemePresetPolicy.Parse(document.RootElement));
    }

    public (DesktopThemePresetState State, bool Changed) Set(JsonElement input) => store.Locked(() =>
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(["expectedRevision", "preset"]) ||
            !input.GetProperty("expectedRevision").TryGetInt64(out var revision) || revision < 0)
            throw new ArgumentException("Theme changes require exact expectedRevision and preset fields.");
        var current = Get();
        if (current.Revision != revision)
            throw new InvalidOperationException("The theme changed while this copy was open. Reload it before applying; the saved theme was preserved.");
        var proposed = input.GetProperty("preset");
        var preset = proposed.ValueKind == JsonValueKind.Null ? null : DesktopThemePresetPolicy.Parse(proposed);
        var document = preset is null ? null : DesktopThemePresetPolicy.Serialize(preset);
        var previous = store.Get<Saved>(Kind, Id);
        if (previous?.Document == document) return (current, false);
        var next = new Saved(checked(revision + 1), document);
        store.Put(Kind, Id, next);
        return (new DesktopThemePresetState(next.Revision, preset ?? DesktopThemePresetPolicy.BlueWhite), true);
    });

    public DesktopThemePresetState Read(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any())
            throw new ArgumentException("Theme inspection takes no arguments.");
        return Get();
    }
}
