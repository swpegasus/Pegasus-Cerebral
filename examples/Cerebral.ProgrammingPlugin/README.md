# Pegasus Programming — desktop companion

Turn a request into a project-bound development workflow with an explicit build target, required checks and visible acceptance receipt. The panel also browses durable milestones, work items, decisions and verification records.

| Component | Published identity |
| --- | --- |
| Desktop package | `pegasus.programming`, manifest version **1.1.3** |
| Required service module | `pegasus.programming`, manifest version **1.6.13** in [Cerebral.Programming](../../src/Cerebral.Programming/) |
| Exact Develop workflow selected by this source | `pegasus.programming:develop` **1.3.21** |

These are separate package/workflow versions. The desktop DLL alone does not supply the service commands, development engine or model.

## Build and install

Use Windows x64, the .NET 10 SDK and the repository dependencies described in [BUILD](../../docs/BUILD.md). From the repository root:

```powershell
dotnet build .\examples\Cerebral.ProgrammingPlugin\Cerebral.ProgrammingPlugin.csproj -c Release
dotnet build .\src\Cerebral.Programming\Cerebral.Programming.csproj -c Release
```

The desktop project normally references the source desktop SDK. Advanced compatibility builds can set `ProgrammingDesktopSdkPath` to an exact compatible `Cerebral.Desktop.Plugins.dll`; the project checks that reference. Do not use an arbitrary SDK to bypass a host mismatch.

With the default SDK output layout, the native DLL and `desktop-plugin.json` are under `bin/Release/net10.0-windows/` in this example. The service output is separate under its own project. Keep its complete dependency/notices payload, including SQLite and Roslyn dependencies.

In Configuration → **Desktop plugins**, choose **Install…**, select the built desktop manifest/package, review installation disabled, then select the package and **Manage → Enable → Go**. Refresh or reopen Main if its Programming view is not active. This desktop change does not require restarting the Worker.

The service component uses the **service package** lifecycle: stage the service manifest, inspect the staged identity/revision, activate after active workflow pins drain, then perform the required normal Worker restart and inspect the loaded inventory. Configuration → **Service extensions** is an inventory; **Package commands** opens the advanced owner command console. It is not a desktop Install dialog. Follow [PLUGINS](../../docs/PLUGINS.md); do not hand-copy DLLs into an installed immutable generation.

## Use it

1. Open the **Programming** panel in Main. **Start project** records a name, absolute source root and purpose for a durable plan.
2. Select the intended project. Enter the request in Main's composer, then choose **Develop** or **Develop request**.
3. Select an existing `.csproj`, `.sln` or `.slnx` inside that source root. The dialog defaults to **Release** with **Build** selected; **Test** and **Run** are optional. Run requires a project, not a solution.
4. For a desktop-plugin project, the advanced output controls can request **Verify and present** with a relative built `desktop-plugin.json`. This requires Build and does not silently install/enable the result.
5. Check the request and accepted-workflow fields. **Open accepted workflow** navigates to a confirmed matching run. The composer is cleared only after a matching acceptance and only if the captured draft is still current.

**Cancel submission** cancels local preparation/acknowledgement waiting. After dispatch, cancellation or a lost reply may leave an unconfirmed accepted run. Inspect retained Work before another submission; this plugin does not resend automatically or cancel already accepted work merely because its panel closes.

## State, tools and permissions

Plans and records live in the service-owned SQLite store; source/build files remain in the selected project folder. The Configuration **Programming** page reports module/project status. This desktop source does not provide a separate global preferences editor.

The supplied Programming engineer/reviewer templates declare needed capabilities and tool names; installing the plugin does not grant them. Development can read/write files, execute processes and use Git/network tools only through the host's configured profile and approval boundaries. Use a source root you intend to expose to that work.

## Troubleshooting

- **Module unavailable / unknown programming command:** confirm the service package is loaded on the connected Worker, not just the desktop package.
- **Workflow edition unavailable:** this source requests the exact 1.3.21 edition. Inspect the service catalog; do not assume an arbitrary newer edition is interchangeable.
- **Target outside source root / no checks selected:** choose a real supported target inside the bound root and at least one check.
- **Unconfirmed acceptance:** inspect the recorded request and Work. Do not equate a button click or composer change with successful execution.
- **Build succeeds but behavior fails:** select appropriate test/run checks; Build alone is not application-behavior evidence.

Source entry points: [ProgrammingPlugin.cs](ProgrammingPlugin.cs), [desktop manifest](desktop-plugin.json), [workflow identity](../../src/Cerebral.Programming/ProgrammingWorkflowIdentity.cs), [service project](../../src/Cerebral.Programming/Cerebral.Programming.csproj).

## Preview and license

These instructions describe the published source. This documentation pass did not restore, compile, install or run the plugin; it is not a fresh-checkout or installed-runtime qualification. Use a host built from a compatible SDK and retain integrity/permission checks when diagnosing failures.

First-party code is covered by the [Pegasus Community License 1.0](../../LICENSE); third-party components retain their own terms. Created by Shayne Winter with development assistance from OpenAI Codex and Pegasus Cerebral. See [plugin lifecycle and permissions](../../docs/PLUGINS.md) and [building Cerebral](../../docs/BUILD.md).
