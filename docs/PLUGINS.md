# Plugin directory and installation

This source preview includes **22 implemented plugin families**. “Implemented” means code and manifests are included; it does not mean 22 binary installers, universal SDK compatibility or a live qualification of every combination. Books is excluded.

Each guide below provides the actual build inputs, output/payload, setup, usage, defaults, privacy and limitations for that family. Versions come from the included manifests, not Cerebral's application version.

## Included packages

| Plugin | Components / manifest version | Main purpose and guide |
|---|---|---|
| AI Dictation | Desktop 1.2.8 | [Local speech model, recording/review and native runtime acquisition](../examples/Cerebral.AiDictationPlugin/README.md) |
| Windows Dictation | Desktop 1.0.8 | [Windows speech input and explicit voice-command opt-in](../examples/Cerebral.DictationPlugin/README.md) |
| Personality | Desktop 1.2.4; service 1.2.3 | [Per-chat public/custom styles, saved revisions and read-only context preview](../examples/Cerebral.PersonalityPlugin/README.md) |
| AutoPrompt | Desktop 1.0.2 | [Optional composer suggestions and guarded draft insertion](../examples/Cerebral.AutoPromptPlugin/README.md) |
| Prompt Review | Desktop 1.0.10 | [Explicit review and guarded draft replacement](../examples/Cerebral.PromptReviewPlugin/README.md) |
| Conversation Titles | Desktop + service 0.2.0 | [Automatic title suggestions with guarded application](../examples/Cerebral.ConversationTitlePlugin/README.md) |
| Goal Card | Desktop 1.0.0 | [Optional journaled goal-card context](../examples/Cerebral.GoalCardPlugin/README.md) |
| Programming | Desktop 1.1.3; service 1.6.13 | [Target/check selection and programming workflow integration](../examples/Cerebral.ProgrammingPlugin/README.md) |
| Collaboration | Desktop 1.0.0 | [Inspect, steer and reconcile existing delegated work](../examples/Cerebral.CollaborationPlugin/README.md) |
| Browser | Service 1.0.2 | [Allowed-domain browsing tools and rendered-browser prerequisites](../examples/Cerebral.BrowserPlugin/README.md) |
| Workspace | Desktop 1.0.0 | [Quote-to-draft, clipboard and workspace actions](../examples/Cerebral.WorkspacePlugin/README.md) |
| Window Watch | Desktop 0.1.2 | [Opt-in capture and separately configured vision](../examples/Cerebral.WindowWatchPlugin/README.md) |
| Review | Desktop + service 1.0.0 | [Durable review questions and receipts](../examples/Cerebral.ReviewPlugin/README.md) |
| Skills | Desktop 0.1.0 | [Passive skill catalog, groups and explicit scope assignment](../examples/Cerebral.SkillsPlugin/README.md) |
| Appearance | Desktop 1.0.0 | [Theme/appearance integration](../examples/Cerebral.AppearanceThemePlugin/README.md) |
| Chat Icons | Desktop 1.0.3 | [Chat icon assets and optional external library](../examples/Cerebral.ChatIconsPlugin/README.md) |
| Metrics | Desktop + service 0.2.0 | [Scalar generation metrics and controlled retention](../examples/Cerebral.MetricsPlugin/README.md) |
| Engine Diagnostics | Service 1.0.0 | [Explicitly activated rolling engine diagnostics](../examples/Cerebral.EngineDiagnosticsPlugin/README.md) |
| Help | Desktop 0.3.0 | [Passive help navigation](../examples/Cerebral.HelpPlugin/README.md) |
| Resource Pause | Desktop 1.1.5 | [Resource availability, engine pause and captured-hold controls](../examples/Cerebral.ResourcePausePlugin/README.md) |
| OpenAI Provider | Service 1.0.0 | [Developer-integrated hosted provider; unconfigured by default](../examples/Cerebral.OpenAiProviderPlugin/README.md) |
| Anthropic Provider | Service 1.0.0 | [Developer-integrated hosted provider; unconfigured by default](../examples/Cerebral.AnthropicProviderPlugin/README.md) |

Supporting source has separate guides: [AI Dictation native helper](../examples/Cerebral.AiDictation.NativeHost/README.md), [HostedProviderProtocol](../examples/Cerebral.HostedProviderProtocol/README.md), and [Shared definitions](../examples/Shared/README.md). They are not independent catalog packages.

## Before building a package

Build/reference the SDK specified by that package's project and README. Some examples accept source project references; others enforce exact installed/raw-reference hashes or a named SDK profile. A mismatched SDK is a compatibility failure, not a reason to delete the guard.

Package only the manifest and payload expected by that project. Do not add host SDK DLLs or personal data unless the package contract explicitly includes them. Paired desktop and service outputs are installed separately and can legitimately have different versions.

## Desktop packages: install and enable in Configuration

1. Connect Configuration to the intended Worker/source environment.
2. Open **Plugins & tools → Desktop plugins**.
3. Use **Install…** and choose the built package/manifest supported by the catalog.
4. Review **Inspect**, package identity, permissions and compatibility.
5. Use **Manage → Enable** and **Go** when ready. A newly installed package starts disabled.
6. Use **Configure…** only when offered by that loaded package.

The same Manage menu includes update, export, disable, remove and refresh operations. Use these supported lifecycle actions instead of replacing DLLs in an immutable generation. An update preserves the package's supported state through the catalog; it is not an in-place overwrite of a loaded assembly.

Main and Configuration can have different loaded generations while an update settles. Inspect actual state and follow any restart/drain instruction. Do not erase pending/unknown operations to force a package transition.

## Service packages: stage, activate and verify on the Worker

**Service extensions** is an inventory with **Refresh**, **Inspect** and **Package commands**. It does not offer the desktop Install/Enable dialog. **Package commands** opens the owner command console.

Use the connected Worker's supported package operations through **Advanced → Command console → Command…**. The operation name and JSON arguments are separate fields.

| Operation | Arguments and effect |
|---|---|
| `service-packages.inspect` | `{ "id": "<package id>" }`; read current revision and staged/desired/observed state |
| `service-packages.stage` | `manifestPath`, `expectedRevision`, optional `expectedId`; validate and copy a candidate into immutable package storage |
| `service-packages.activate` | `id`, `expectedRevision`; select the staged generation as desired |
| `service-packages.deactivate` | `id`, `expectedRevision`; request inactive state under the lifecycle's drain rules |
| `service-packages.remove` | `id`, `expectedRevision`; remove an eligible inactive package; plugin data is preserved |

Replace placeholders with the exact package ID and **fresh revision returned by the preceding inspection**. Do not copy a guessed revision. `manifestPath` must be an absolute `service-plugin.json` path accessible on the selected Worker machine, not merely on a different desktop. `expectedId` checks identity; it is not a full payload fingerprint.

After staging, inspect again before activation. Activation changes **desired** state; it does not hot-load assemblies into a running Worker. Wait for active work to drain and use a normal Worker restart when required. Verify `ObservedLoaded`, `ObservedVersion` and `ObservedGeneration` against the desired state and check `RestartRequired`. A desired-active label alone is not a loaded-package pass.

Use the actual command response if a transition refuses. If an acknowledgment is lost, inspect the current state before deciding what happened; do not blindly stage/activate again. Preserve existing uncertain work and plugin-owned data.

## Hosted providers and external tools

The OpenAI/Anthropic examples' default constructors are deliberately **unconfigured**. A developer must supply the reviewed profile, HTTP transport and credential resolver described in their guides. This preview does not supply a provider-account setup UI or account keys.

Installing a plugin also does not grant every agent its tools. Host admission, profile capabilities/tool selection, filesystem roots, browser domain policy, connection approvals and provider support remain separate. An ordinary desktop extension can provide UI without exposing any model tools.

## Removal, data and redistribution

Disable/deactivate through the supported catalog before removal. A service restart may be required, and removing a package is not a request to erase its stored data. Export only data you intend to share.

Plugin code runs within a host process and its Windows security context; package permissions are not a general operating-system sandbox. See [Data and permissions](DATA-AND-PERMISSIONS.md).

First-party plugins use the [project license](LICENSING.md) where identified. Retain independent third-party notices, including speech runtime/model terms, browser/dependency terms, icon assets and imported skills. No model, browser or proprietary service entitlement is granted by installing a plugin.
