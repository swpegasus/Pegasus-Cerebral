# Workspace and Message Actions

Small native tools for the current project and draft: a Workspace sidebar, project details, quote-draft and copy actions, plus a saved plain-text display preference.

**Desktop package:** `pegasus.workspace-actions`, manifest version **1.0.0**.

## Build and enable

Requires Windows x64, .NET 10, the compatible Core and desktop-plugin SDK, and the linked shared package-preference helpers in this repository.

```powershell
dotnet build .\examples\Cerebral.WorkspacePlugin\Cerebral.WorkspacePlugin.csproj -c Release
```

From the repository root, the default output is the project's `bin/Release/net10.0-windows/` directory with its DLL and `desktop-plugin.json`. The host supplies Core and the SDK; preserve the package rather than substituting unrelated host assemblies.

In Configuration → **Desktop plugins**, **Install…** the built manifest/package, review disabled installation, then select **Manage → Enable → Go**. Refresh or reopen Main if its mounts are not visible. A Worker restart is not required merely for these native views.

## Where to use it

- **Workspace** sidebar shows the current conversation title and project path. **Copy project path** copies that path when one is assigned.
- **Project details** contributes the same current-project context to the host's workspace tree.
- **Quote draft** prefixes the current composer lines with Markdown quotation markers. It edits the captured draft through the host's binding/revision check; it does not send it.
- **Copy last answer** copies a complete answer received while this plugin was enabled in the current conversation. It does not retrieve old history.
- **Copy original** on a complete message copies its original content, independent of its display rendering.

The included **Project planning helper** is an agent template. Selecting a template does not grant it tools or file paths.

## Preference and persistence

Open **Workspace**, load the saved preference if requested, edit **Display assistant replies as plain text**, then choose **Save preferences**. The default is **off**. Ticking the checkbox edits a draft; presentation changes only after the central save is acknowledged.

The sole saved value is the global `plainResponses` preference, managed through the host's package settings route. It is not per-chat persona context and does not rewrite stored messages. The plugin does not save draft text, clipboard contents or conversation bodies in that preference.

The cached last answer is session/view memory and is cleared when the conversation binding changes or the plugin is disposed. Truncated messages are not offered as complete copies.

## Permissions, limits and troubleshooting

The package uses native mounts, revision-checked draft replacement and the host clipboard bridge when available, with the Windows clipboard as its legacy fallback. Copying intentionally places text/path data on the system clipboard; review it before pasting elsewhere. No model tool or external network provider is added.

- **No project folder selected:** assign a project in Cerebral; the plugin does not create a folder from this view.
- **No complete answer:** wait for a complete committed reply in this conversation; historical or truncated content is not silently substituted.
- **Preferences unavailable:** check the Worker/package-settings connection, reopen Workspace and load saved state before saving. The plugin does not treat local checkbox state as persisted.
- **Draft changed:** review the composer and retry the intended quote edit against the new draft; no text is submitted automatically.

Source: [WorkspacePlugin.cs](WorkspacePlugin.cs), [WorkspaceSettingsPolicy.cs](WorkspaceSettingsPolicy.cs), [desktop manifest](desktop-plugin.json).

## Preview and license

These instructions describe the published source. This documentation pass did not restore, compile, install or run the plugin; it is not a fresh-checkout or installed-runtime qualification. Use a host built from a compatible SDK and retain integrity/permission checks when diagnosing failures.

First-party code is covered by the [Pegasus Community License 1.0](../../LICENSE); third-party components retain their own terms. Created by Shayne Winter with development assistance from OpenAI Codex and Pegasus Cerebral. See [plugin lifecycle and permissions](../../docs/PLUGINS.md) and [building Cerebral](../../docs/BUILD.md).
