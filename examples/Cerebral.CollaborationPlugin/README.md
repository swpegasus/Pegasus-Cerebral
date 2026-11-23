# Pegasus Collaboration

A removable Main sidebar for inspecting and directing durable tasks between chats while the normal conversation stays visible.

**Desktop package:** `pegasus.collaboration`, manifest version **1.0.0**. It uses the host's existing collaboration service; it does not create a new service, scheduler or worker runtime.

## Build and enable

Requires Windows x64, .NET 10 and the source `Cerebral.Desktop.Plugins` SDK.

```powershell
dotnet build .\examples\Cerebral.CollaborationPlugin\Cerebral.CollaborationPlugin.csproj -c Release
```

From the repository root, this produces the native DLL and copied `desktop-plugin.json` in the project's default `bin/Release/net10.0-windows/` output. The host supplies its compatible SDK assembly.

Use Configuration → **Desktop plugins → Install…**, select the built manifest/package and review the disabled install. Select it, choose **Manage → Enable**, then **Go**. Refresh or reopen Main if the **Collaboration** sidebar has not appeared. This is a native UI package; no Worker restart is required merely to load its view. The connected Worker must already support the collaboration commands.

## Inspect and direct a task

1. Open **Collaboration** and **Refresh**. The panel requests the recent bounded task list, up to 40 entries.
2. Select a task to inspect its identity/state, events and available bounded result preview. A result preview can be incomplete.
3. **Open source chat** and **Open worker chat** use the host's workspace navigation. They are disabled when that navigation bridge is unavailable.
4. Use **Send steering** for a bounded instruction, up to 16,000 characters. It targets the selected task with its expected revision.
5. **Cancel**, **Reconcile** and **Adopt result** are available only for applicable task states. Reconcile appears for `ReconcileRequired`; adoption requires a completed, unadopted result with the target-run revision.

This panel has no “create coordinator” action. A real task must first be admitted by the host's workflow/collaboration route. An empty list is not evidence that a coordinator or worker is executing.

## State and privacy

Task state and results are service-owned and durable. The selection, steering draft and displayed previews are view state, with no plugin-specific saved preferences in this source. Disabling the sidebar does not delete task history or automatically cancel its work.

The plugin uses structured `collaboration.list/events/result/steer/cancel/reconcile/adopt` operations. Every mutation carries the selected task's expected revision; adoption also binds its attempt and result revision. The host remains responsible for authority. Reading a result does not grant it new tools or adopt it into another chat automatically.

The sidebar displays source/worker identifiers, instructions, activity and result content to the local user. Treat that content as task data, not new permission.

## Troubleshooting

- **No panel:** inspect the desktop package's enabled/error state and its active Main generation.
- **Unknown collaboration operation:** check the connected Worker version and service connection.
- **Button disabled:** inspect task status and host navigation availability; not every state can be cancelled, reconciled or adopted.
- **Revision conflict or lost reply:** Refresh and inspect the selected task before another action. Do not infer that a failed acknowledgement proves no change occurred.
- **Result truncated:** open the relevant chat/workflow for the complete context; the sidebar preview is intentionally bounded.

See [CollaborationPlugin.cs](CollaborationPlugin.cs) for the mount, button state rules and command arguments, and [desktop-plugin.json](desktop-plugin.json) for package identity.

## Preview and license

These instructions describe the published source. This documentation pass did not restore, compile, install or run the plugin; it is not a fresh-checkout or installed-runtime qualification. Use a host built from a compatible SDK and retain integrity/permission checks when diagnosing failures.

First-party code is covered by the [Pegasus Community License 1.0](../../LICENSE); third-party components retain their own terms. Created by Shayne Winter with development assistance from OpenAI Codex and Pegasus Cerebral. See [plugin lifecycle and permissions](../../docs/PLUGINS.md) and [building Cerebral](../../docs/BUILD.md).
