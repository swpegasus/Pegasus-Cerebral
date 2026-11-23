# Personality service package

The Worker service package `pegasus.personality` **1.2.3** stores communication-style definitions and per-chat selections, retains exact accepted revisions, and supplies optional workflow context. The paired desktop package is **1.2.4**. Use the [main Personality guide](../README.md) for installation, UI, SDK/build instructions, privacy and the [character library](../docs/final-space-character-library.md).

This package contributes **no model tools, execution grants, learning runtime or voice synthesis**. Installing it does not establish that a selected workflow consumes personality context.

## Build and activate

Build `Cerebral.PersonalityServicePlugin.csproj` using the source or exact binary-SDK mode described in the [parent guide](../README.md#build). Its output contains the service DLL and `service-plugin.json`. Keep desktop and service payloads separate.

Follow [service package staging/activation](../../../docs/PLUGINS.md#service-packages-stage-activate-and-verify-on-the-worker). The Configuration Service extensions page is an inventory. Activation selects desired state; inspect the actual loaded generation and restart requirement before claiming it is active. Do not copy DLLs into a running Worker.

## Per-chat contract

The host's stable `ConversationId` is the selection key. Missing state is revision zero and No personality. Global editable-definition preferences do not silently select a style in every chat.

| Operation | Required shape / result |
|---|---|
| `personality.chat.get` | `{schemaVersion:1, conversationId}`; returns the bounded chat state, exact pins, availability and catalog token |
| `personality.chat.save`, enable | `schemaVersion:1, conversationId, expectedRevision, enabled:true, baseSelection, bluntMode, catalogToken` |
| `personality.chat.save`, disable | `schemaVersion:1, conversationId, expectedRevision, enabled:false, bluntMode:false` |

An enabled `baseSelection` is the exact offered kind/ID/revision/SHA-256 tuple. A style name alone is insufficient. Catalog rows have metadata, not private definition bodies. A mode-only change may preserve that chat's current exact retained base; it does not admit arbitrary historical choices for another chat. The current library has 12 templates plus two built-ins; retained older public revisions remain resolvable without silently upgrading saved choices.

Save uses the chat's monotonic revision and current catalog token. Confirmed outcomes distinguish `committed`, `unchanged` and `rejected`. `uncertain`, a malformed reply after dispatch or a lost acknowledgment requires reloading/reconciliation, not automatic replay. Composition is refused rather than truncated.

Bounds include:

- Base text: 120 whitespace-separated words and 1,024 UTF-8 bytes.
- Effective serialized provider resolution: 2,048 bytes, including wrappers/escaping.
- Catalog: 32 rows; chat-get response: 64 KiB.
- Current editable definitions: eight; retained custom/private history has a separate shared identity/version limit.
- Chat state: up to 2,048 chat records and 512 effective sources within a 4 MiB store, without silent eviction.

These are context/storage bounds, not model output-length limits.

## Saved state and accepted context

The service owns `personality-chat-state.v1.json` and exact immutable source history. New accepted context records the selected base and modifier pins. Restore resolves accepted pins; it never substitutes today's chat/global selection for old work.

The contribution is optional reference material, placed before current input. A context budget or consumer can omit it. A saved choice, a successful preview and an accepted contribution are different observations; none alone proves the model followed the style.

Central editable definitions and legacy transfer operations remain separate from chat selection. The source retains older seed/restore readers for accepted history; those are compatibility readers, not instructions to revive obsolete mutation commands or migrate every chat.

## Privacy and export

Custom style text can become model input. Plugin-owned private library/state/history is plaintext storage; it is not an encrypted vault. Owner-facing command names are not a guarantee of an independently audited human-only authorization boundary. This guide intentionally provides no private-import recipe.

Private bodies are unavailable in the native context preview. Private definitions/provenance and accepted source history are excluded from ordinary portable/complete settings exports; do not infer that all backups, OS copies or diagnostics are automatically sanitized. Review free-form editable text before sharing.

## Developer references

- [PersonalityServicePlugin.cs](PersonalityServicePlugin.cs): advertised operations and workflow integration.
- [PersonalityChatContract.cs](PersonalityChatContract.cs) and [PersonalityChatStore.cs](PersonalityChatStore.cs): closed shapes, CAS, outcome and storage behavior.
- [PersonalityContract.cs](PersonalityContract.cs): provider wrapper and bounds.
- [PersonalityChatContributionCodec.cs](PersonalityChatContributionCodec.cs): accepted chat context encoding.

Existing fixture selectors test synthetic/scoped settings and owned storage; they are not a live Worker/model or exact-persona delivery guarantee. The [main guide](../README.md) links the current build mode and host requirements. First-party source uses the [Pegasus Community License 1.0](../../../LICENSE); third-party character/media rights remain separate.
