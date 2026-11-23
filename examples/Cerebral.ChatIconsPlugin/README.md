# Pegasus Chat Icons

Pegasus Chat Icons adds an **Icons** text-entry action, message-role decorations and a **Chat Icons** Configuration panel. Package `pegasus.chat-icons` is **1.0.3** in [desktop-plugin.json](desktop-plugin.json). It is a native Windows desktop plugin, with no service component or model of its own.

The picker inserts a Unicode symbol at the cursor or selection. It does not send the message. SVG assets and portable icon references remain assets or references; they are not silently substituted into model input. Decorations leave message IDs, roles, original text and history under host control.

## Build and install

From the repository root on Windows with the SDK in `global.json`:

```powershell
dotnet build examples/Cerebral.ChatIconsPlugin/Cerebral.ChatIconsPlugin.csproj -c Release
```

The project references source Contracts, Core and Desktop Plugins projects, and source-links helpers from [Shared](../Shared/README.md). It has no separate installed-SDK selector. Default output is `examples/Cerebral.ChatIconsPlugin/bin/Release/net10.0-windows/`.

Package the manifest and plugin DLL with `plugin-resources.json`, the complete `Resources/Icons/chat-v1/` tree and `Licensing/`. Catalog, index and referenced SVGs must remain together; copying only the DLL is insufficient. Host SDK assemblies remain host-provided. The source assets are in [assets/icons/chat-v1](../../assets/icons/chat-v1/README.md).

Install and enable using the native catalog workflow in [Plugin installation](../../docs/PLUGINS.md). See [Build](../../docs/BUILD.md) for prerequisites. These commands describe source inputs and default output, not a clean-build or live-install result.

## Use and preferences

Choose **Icons** in a supported text field, browse **Local icons**, and select a symbol to insert. The action rechecks the owning field and conversation. If you navigate away while the picker is open, reopen it in the new field instead of inserting from a retired picker.

The **Chat Icons** Configuration panel manages role and semantic choices. Central preferences use the host's versioned settings service. Built-in mappings supply defaults when no saved selection exists. Local `roles.ini` and `icon-meanings.ini` are legacy inputs; preparation and saving are explicit, not an automatic installation import. Failed or stale reads do not authorize overwriting newer acknowledged choices.

When the service bridge supplies native Cortex asset providers, the picker can also show **Cortex library**. It discovers providers through `cortex.assets.providers` and performs bounded reads through `cortex.assets.read`. Library availability and permissions belong to the configured host. Installing this plugin does not enroll Cortex, grant access, create a library or upload local icons.

Selected library assets can be cached under the plugin-owned `cortex-icons-v2` data directory. A matching digest proves byte integrity, not arbitrary asset rendering safety. Keep user settings and cache content out of source packages and repositories.

## Dependencies and troubleshooting

Bundled Fluent System Icons have their own MIT license and attribution in [LICENSE.txt](../../assets/icons/chat-v1/LICENSE.txt), [UPSTREAM-NOTICE.txt](../../assets/icons/chat-v1/UPSTREAM-NOTICE.txt) and the asset Licensing tree. Preserve those notices. First-party code follows the repository [LICENSE](../../LICENSE). Local picking needs no model weights or API credentials.

For missing resources, restore the complete package tree instead of bypassing admission. If Cortex is unavailable, use local icons; an unavailable library is not an authoritative empty result. If preferences fail, preserve the draft and reload through the panel.

Source: [ChatIconsPlugin.cs](ChatIconsPlugin.cs), [settings](ChatIconsPlugin.Settings.cs), [CortexIconLibrary.cs](CortexIconLibrary.cs) and [CortexIconCache.cs](CortexIconCache.cs). The separate [PackTool](PackTool/Cerebral.IconPackTool.csproj) is an asset-authoring utility, excluded from the desktop plugin's compile items.
