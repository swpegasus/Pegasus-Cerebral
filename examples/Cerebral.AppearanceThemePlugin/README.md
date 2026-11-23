# Portable appearance presets

Pegasus Appearance is a configuration-only desktop plugin. Select it in Configuration → Desktop plugins → Configure. It adds no chat tab, composer button, agent or service component.

Import a `.CerebralTheme` file (or `.json`) to preview its name and colors. Apply validates and selects it through the authenticated Worker owner command. Export reads the current effective preset and saves portable JSON. Restore Blue White clears the custom selection. Existing **System**, **Light** and **Dark** mode preferences remain independent of the preset.

A preset is data, with no executable XAML, resources, scripts, paths or assembly references:

```json
{
  "schemaVersion": 1,
  "id": "blue-white",
  "name": "Blue White",
  "light": { "...all sixteen color tokens...": "#RRGGBB" },
  "dark": { "...all sixteen color tokens...": "#RRGGBB" }
}
```

The complete usable example is `Presets/Blue White.CerebralTheme` in the source tree and `Blue White.CerebralTheme` beside this README in a bundled installation. It is generated from the authoritative Core default policy. Both palettes require exactly these sixteen tokens: `window`, `surface`, `raisedSurface`, `ink`, `muted`, `accent`, `onAccent`, `line`, `hover`, `pressed`, `selection`, `selectionText`, `codeSurface`, `errorBackground`, `errorText`, `errorBorder`.

Files are at most 16 KiB of UTF-8 JSON. The id uses 1–64 lowercase letters, digits, dots, hyphens or underscores and starts with a letter or digit. The name is 1–100 characters. Colors are opaque `#RRGGBB`. Duplicate, missing and unknown fields are rejected. Normal text pairs require at least 4.5:1 contrast against their actual shared surfaces; decorative borders have no text-contrast requirement.

The private versioned owner bridge is `interface.theme.get` / `interface.theme.set`. State contains `revision` and the effective `preset`. Set supplies `expectedRevision` and a preset; null restores the default. A changed revision or invalid import leaves the current selection unchanged. Refresh before retrying. Accepted changes emit `interface.theme.updated`; connected Chat and Configuration update existing brush instances without rebuilding controls. The plugin uses only the public desktop SDK, while Core owns semantic validation and Worker owns persistence and authorization.

Theme files are manually imported and exported. This plugin does not register a Windows file association or execute a theme package.

To transfer a theme, export it from **Pegasus Appearance → Export current preset**, copy that file to the other Cerebral machine, then use **Import preset → Apply imported preset** in the same plugin. The JSON contains no machine paths or installation-specific values. To make another theme, copy the complete example, give it a new `id` and `name`, and edit the light and dark color values; no host rebuild is required.

Version 1 transfers color palettes. Shared WPF templates retain the control layout and behavior, while plugins can extend those styles or use the public theme resource keys. Fonts, spacing, custom control templates and raster branding are not fields in this format. Adding them later requires an explicitly versioned format and a corresponding plugin/host implementation; an imported color preset does not silently claim to replace them. The built-in Blue White palette remains the recovery fallback.
