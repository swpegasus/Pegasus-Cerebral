# Cerebral Help

Cerebral Help mounts a native **Help** tab in Main's sidebar. Package `pegasus.help` is **0.3.0**, defaults to disabled and belongs to the `pegasus.help` exclusive group.

The current implementation is a passive navigation and reference map. The view explicitly labels **tutorials/images pending** and **assistant connection pending**. Browsing pages and following internal references changes the Help view only; it does not execute instructions, connect an assistant, send a chat, inspect profiles, subscribe to host events or start model work. Describing a workflow in a catalog is not proof that Help automates it.

## Build and install

From the repository root on Windows with the SDK in `global.json`:

```powershell
dotnet build examples/Cerebral.HelpPlugin/Cerebral.HelpPlugin.csproj -c Release
```

The default build references the source Desktop Plugins SDK. To use an existing matching SDK instead:

```powershell
$sdk = (Resolve-Path src/Cerebral.Desktop.Plugins/bin/Release/net10.0-windows).Path
dotnet build examples/Cerebral.HelpPlugin/Cerebral.HelpPlugin.csproj -c Release "-p:CerebralSdkDirectory=$sdk"
```

That directory must already contain compatible `Cerebral.Desktop.Plugins.dll`; the raw reference does not build it. Do not mix SDK generations or copy the host SDK into the plugin package.

Default output is `examples/Cerebral.HelpPlugin/bin/Release/net10.0-windows/`. An explicit after-build target copies `desktop-plugin.json` beside `Cerebral.HelpPlugin.dll`. Install and enable through the native catalog described in [Plugin installation](../../docs/PLUGINS.md). Being present in a source or bundled output is not the same as being enabled.

See [Build](../../docs/BUILD.md) for prerequisites. These are source instructions, not a clean-build, visual or installed-runtime qualification.

## Use

Open **Help** in Main's sidebar. Choose a page in the navigation list; at narrow widths, use the compact **Help page** selector. Internal links navigate to valid reference sections. Missing targets produce a reference error instead of dispatching host commands.

Use the main [Setup](../../docs/SETUP.md) and plugin guides for installation. The Help catalogs include planned surfaces and content placeholders, so follow the visible pending labels rather than assuming all described material is implemented.

## Settings, privacy and troubleshooting

This plugin saves no preferences, reads no user database and maintains no data store. Selection and expansion are view state. It needs no Cortex account, API key, local model, model download or extra inference runtime. It uses WPF/theme resources and the host Desktop Plugins SDK.

If the tab is absent, check native catalog enablement and sidebar mount support. If a reference fails, report the broken Help link; enabling model access or changing privileges cannot supply a missing target. The package stays passive when Main is disconnected from its Worker.

First-party code follows the repository [LICENSE](../../LICENSE). Entry points are [HelpPlugin.cs](HelpPlugin.cs) and [HelpView.cs](HelpView.cs); `Help*Catalog.cs` files define content. Existing source fixtures are in [Cerebral.HelpPlugin.Tests](../../tests/Cerebral.HelpPlugin.Tests/).
