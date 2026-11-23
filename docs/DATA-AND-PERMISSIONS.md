# Data, permissions and execution boundaries

Cerebral combines user conversation, selected references, models and tools. These inputs have different roles. A reference document, tool result, saved memory or model-generated claim does not grant permission to execute an action.

## Choose the data environment deliberately

Installed applications use registered program/data/user/project roots. Source previews should use the explicit, separate `--source-local` directory in [First setup](SETUP.md). Running an executable from a build folder alone does not guarantee isolation.

Within a source data root, desktop connection state is under `Desktop/`, desktop package storage under `Plugins/Desktop/`, and plugin-owned desktop data under `PluginData/`. Other Worker stores and package data follow their host configuration. Project content can live outside this data root and remains independently owned.

Use **Backup & restore** and the relevant package export features where supported. Backups/exports can contain conversation text, settings, paths or plugin data. Review them before sharing. Do not commit a whole data directory to GitHub.

## Instructions, styles and accepted snapshots

Shared and role instructions are host configuration. Optional personality context is a separate contribution. Keeping the human speaker distinct from the assistant does not require selecting a fictional persona.

New accepted requests capture their instruction/configuration context. An instruction change does not silently rewrite an already accepted request or make retrying that same request use a new snapshot. Saved style and preview state similarly differ from proof of model inclusion or an applied result.

Personality's public/custom library and exact saved preview are described in its [guide](../examples/Cerebral.PersonalityPlugin/README.md). Plugin-owned private definitions are plaintext storage, not encrypted by this preview; the native preview refuses private bodies. Do not assume that a metadata label establishes a separately audited human-only authorization boundary.

## Tools and delegation

The effective tool surface can depend on all of these:

- The selected workflow and the model/backend's supported operations.
- Profile capabilities, allowed tool names and configured roots.
- Package admission, enablement and actually loaded service generation.
- External connector identity, connection approval and current grants.
- The accepted request's captured configuration and execution origin.

Having helpers in the catalog is different from having desktop automation or general execution tools. A Coordinator may delegate supported work without having permission to manipulate windows. A model's claim that tools are available—or unavailable—should be checked against actual catalog/activity state when it matters.

Cerebral's Full control settings and Cortex's connection/application authority are separate product boundaries. Do not assume a chat ID is a credential, that a request for more tools grants access automatically, or that one connection approval grants every application permission. This preview does not document a completed universal chat-to-descendants permission bypass.

## Local and remote processing

A local model route can keep inference on the configured machine, but optional hosted providers, remote Workers, browsers, MCP tools, Cortex and other network integrations have their own data paths. “Local model” is not a guarantee that every enabled plugin is offline.

Attachments, selected context, prompt text, generated results and tool arguments can be sent to the configured remote route when that route is used. Configure providers explicitly and keep credentials outside source and package files. Review capture/vision and speech plugins separately: screen pixels and microphone audio are different data sources from typed chat.

## Plugin trust and diagnostics

Plugins execute code in the host's Windows context. Manifest/tool permissions do not turn arbitrary in-process code into an OS sandbox. Review the package source and dependencies before enabling it.

Diagnostic data can contain content. In particular, [Engine Diagnostics](../examples/Cerebral.EngineDiagnosticsPlugin/README.md) can persist diagnostic text with limited masking; it is not a comprehensive privacy filter. [Metrics](../examples/Cerebral.MetricsPlugin/README.md) has a narrower scalar capture contract. Review exact retention and capture defaults in each guide.

## Unknown effects and cancellation

A lost reply does not prove that an effect failed. An external command, file change or network request can complete before its acknowledgment is lost. Preserve the original request, result and unknown status; inspect/reconcile through the supported workflow before retrying.

Cancellation or “stop tracking” does not rewrite the history into success and does not automatically undo external changes. Existing work and new admissions can have different permission snapshots. Do not delete databases, receipts, recovery directories or locks to make the UI appear clear.

## Public sharing checklist

Share baseline source and intended defaults. Exclude conversations, drafts, personal profiles, exports/backups, application databases, capture/audio recordings, private reference material, API keys, tokens, passwords, private signing keys and enrollment/session material.

Do not include machine trust/signing files just because they exist on a developer system. A signed distribution may include the explicitly required public verification certificates and their notices; private keys and unrelated trust material must stay out. This source preview contains no signing/trust material.

See [Licensing](LICENSING.md) for model/runtime and third-party redistribution requirements.
