# Pegasus Appearance

Pegasus Appearance is a native Windows desktop plugin for importing, previewing, applying and exporting shared Cerebral color presets. Package `pegasus.appearance` is **1.0.0** in [desktop-plugin.json](desktop-plugin.json); its assembly version follows repository build defaults.

It supplies the **Appearance** Configuration panel. It does not run a model, change chat content or introduce a service plugin. Presets contain both light and dark palettes; the normal **System**, **Light** and **Dark** appearance modes remain separate choices.

## Build and install

Use Windows and the .NET SDK selected by `global.json`. From the repository root in PowerShell:

```powershell
dotnet build examples/Cerebral.AppearanceThemePlugin/Cerebral.AppearanceThemePlugin.csproj -c Release
```

The project references the source Desktop Plugins SDK project; it has no `CerebralSdkDirectory` override. Default output is `examples/Cerebral.AppearanceThemePlugin/bin/Release/net10.0-windows/`, including the DLL and `desktop-plugin.json`. An explicit after-build target copies the manifest. Host SDK assemblies remain host-provided.

The project also copies this README and any `Presets/*.CerebralTheme` files that exist. This snapshot does not include a preset collection in that directory; **Restore Blue White** uses the host's built-in preset.

Use the supported native catalog workflow in [Plugin installation](../../docs/PLUGINS.md) to install and enable the package. Building an output directory does not install it. See [Build](../../docs/BUILD.md) and [Setup](../../docs/SETUP.md) for host prerequisites. These are source instructions, not a clean-build or installed-runtime test result.

## Use

1. Open the **Appearance** Configuration panel and use **Refresh** to read the saved preset.
2. Choose **Import preset…** for a `.CerebralTheme` or `.json` file. Import displays a candidate and swatches; it does not save or apply the candidate.
3. Review the preview, then choose **Apply imported preset**.
4. Use **Export current preset…** to save the effective saved preset, or **Restore Blue White** to return to the built-in palette.

The panel requires the structured service bridge. Reads use `interface.theme.get`; changes use `interface.theme.set` with the observed revision. If another editor changes the theme, refresh and review rather than repeatedly applying an old revision. The host owns persistence and shared-control updates; the plugin has no separate theme database.

## Files, privacy and troubleshooting

Portable theme files are JSON bounded to **16 KiB**. Invalid or oversized input is refused. Export writes to the selected file and uses the save dialog's overwrite confirmation. Review a theme before sharing it; a preset is data, not an executable extension.

If actions are unavailable, check the host connection and plugin enablement. An unconfirmed save is not proof that the theme changed: read the current preset before deciding what to do next. Shared theme changes do not necessarily affect every custom-rendered surface.

No API key, model weights or extra inference runtime is required. First-party code follows the repository [LICENSE](../../LICENSE); imported themes retain their own provenance and terms.

Source: [AppearanceThemePlugin.cs](AppearanceThemePlugin.cs) contains the panel, revision checks and portable reader; [the project](Cerebral.AppearanceThemePlugin.csproj) defines build inputs.
