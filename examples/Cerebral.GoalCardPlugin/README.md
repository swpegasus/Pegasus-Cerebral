# Conversation Goal

Conversation Goal is the desktop package `pegasus.goal-card`, manifest version **1.0.0**. It shows the original accepted request and later instructions from the conversation journal, so you can check the task against what you actually asked.

## Install and use

This is a Windows WPF plugin for the Cerebral desktop API, version 1. In Configuration, open **Plugins & tools > Desktop plugins**. Use **Install…** to select the built package's `desktop-plugin.json`, then select **Conversation Goal** and use **Manage > Enable > Go** if it is disabled. The manifest requests first-install enablement, but the catalog's saved state determines whether an existing installation is enabled. See the [plugin guide](../../docs/PLUGINS.md) for package admission and updates.

In Main, expand **Goal · original request and updates** beside the composer, or use the **Goal** workspace action to open a separate viewer.

- **Refresh** reads the current conversation again.
- **Add goal correction** appends `Goal correction: ` to the current draft. Write the correction, then send or steer it yourself.
- An empty chat displays a message until its first request has been accepted.
- The card shows at most eight recent updates. Older instructions remain in chat history, and bounded excerpts are labelled.

The card does not infer a replacement objective, submit a message, change a running task, or maintain a second copy of the goal. Its correction action uses the captured conversation and draft revision; if either changes, inspect the current draft instead of assuming the correction was inserted.

## Build

From the repository root on Windows, with the .NET SDK selected by `global.json`:

~~~powershell
dotnet build examples/Cerebral.GoalCardPlugin/Cerebral.GoalCardPlugin.csproj -c Release
~~~

The project references `src/Cerebral.Desktop.Plugins` and copies its manifest into the output directory. Use the complete output package, retaining license material required by the [root license](../../LICENSE). The manifest package version is independent of the assembly version inherited from the product build. See [BUILD](../../docs/BUILD.md) for the full source-build prerequisites.

## Permissions, privacy and troubleshooting

The enabled plugin reads the current chat through the host's `conversations.goal` service command. It stores no shadow goal and does not call a model or external network service. The displayed request can contain sensitive text already present in that chat; treat the viewer accordingly.

If the card reports that journal access is unavailable, check the Worker connection and compatible host service API. A failed or stale read is shown as unavailable; it is not converted into an empty or successful goal. A successful source build alone does not establish live journal access in a particular installation.

## Source notes

[GoalCardPlugin.cs](GoalCardPlugin.cs) defines the composer mount, separate viewer, bounded journal response and guarded draft correction. [desktop-plugin.json](desktop-plugin.json) defines package identity and defaults.
