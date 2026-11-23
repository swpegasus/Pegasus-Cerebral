# Personality service plugin 1.2.3

## Per-chat selection (source candidate; qualification pending)

Main uses owner-only `personality.chat.get` / `personality.chat.save`, schema 1. The host's stable
`ConversationId` is the selection key. A missing record is revision 0 / No personality. Global
preferences remain the shared editable-definition and legacy transfer authority; their enabled and
selected fields do not enable or select a personality in any chat. There is no implicit migration,
global fallback, lead-instruction cleanup, Cortex lookup, or profile import during capture.

Get requires `{schemaVersion:1, conversationId}`. Its closed reply contains `schemaVersion`,
`conversationId`, `revision`, `enabled`, `baseSelection`, `bluntMode`, `bluntPin`, `availability`,
`diagnostic`, `effectiveSource`, `catalogToken`, `catalog`. Catalog rows contain only
`kind,id,revision,sha256,name,availability,diagnostic`. Kinds are `packaged`, `custom`, `private`.
Pins are qualified by kind, ID, positive revision and exact base-text SHA; only an unprepared current
custom catalog row has null revision. Names retain the existing 80 UTF-16 / 320 UTF-8 allowance.
The catalog is bounded to 32 rows and the reply to 64 KiB. Tokens exclude all chat selections.

Active Save requires `{schemaVersion:1,conversationId,expectedRevision,enabled:true,baseSelection,
bluntMode,catalogToken}`. `baseSelection` is the exact catalog kind/ID/revision/SHA tuple. Explicit Save
may prepare that one unprepared custom definition. Packaged choices (the original two plus seven
linked public templates) use no editable/custom retention slots. Blunt is a separately versioned
modifier, never a persona. One effective source renders the selected base plus optional modifier.
Base text stays within 1024 UTF-8 bytes / 120 words; the actual serialized effective provider result
including wrappers and escaping must fit 2048 bytes. Composition is rejected rather than truncated.

No personality Save requires only `{schemaVersion:1,conversationId,expectedRevision,enabled:false,
bluntMode:false}`. It preserves the remembered base pin, clears both injections and their effective
source, and works without a readable catalog. Every chat has independent monotonic CAS. Identical
fresh-CAS saves are unchanged; changes in another chat neither invalidate this CAS nor its catalog.

Save replies contain `schemaVersion,conversationId,outcome,changed,state,diagnostic`. `committed`
means publication and exact readback at expected revision + 1, `changed:true`, with the full Get view.
`unchanged` means `changed:false` and confirmed current view. `rejected` means the chat was not
published (`changed:false`); an unused immutable custom preparation may remain. `uncertain` means
publication was attempted but not confirmed (`changed:null`). Lost/malformed transport replies after
issue are also uncertain to the caller. Reload and reconcile; do not replay mutations automatically.
Cancellation is checked before publication; after publication the result cannot claim cancellation
prevented the commit. Diagnostic codes include chat-revision-conflict, catalog-changed,
catalog-unavailable, selection-unavailable, source-capacity, effective-style-too-large,
cancelled-before-commit and commit-unconfirmed. No private bodies or paths enter diagnostics.

`personality-chat-state.v1.json` is plugin-owned user data, separately leased and atomically replaced.
It retains up to 2048 chat records and 512 exact effective sources within 4 MiB, without eviction.
Missing read sidecars are not recreated. Qualified immutable history preserves base and modifier
text/hash separately so accepted work can resolve earlier versions without current definitions.
New `pegasus.personality.chat-seed` payloads contain identity and exact pins only. Validate/restore
never consult live chat/global selections. The unchanged 1.2.2 envelope, original seed and v4 readers
remain available; a new seed is never substituted for accepted old state. History is ReferenceOnly,
with the existing Optional contribution and budget-omission semantics.

Private imports are explicit owner operations. `personality.private.get {schemaVersion:1}` returns
`schemaVersion,libraryRevision,entries` (latest metadata/provenance only). Import requires
`{schemaVersion:1,expectedLibraryRevision,definition:{id,name,text},provenance:{namespace,recordType,
recordId,revision,sha256}}`. Provenance is a bounded owner-supplied `knowledge.user` knowledge
coordinate/revision/source-body hash, not a lookup or authorization claim. It is distinct from the
compact definition text's hash. Import uses the same outcome/changed/diagnostic rules, and returns
`libraryRevision` plus the metadata-only catalog `entry` on confirmed results. Private definitions
consume the existing combined retained custom 8 identities / 32 versions; both writers use the legacy
retention lease to enforce the combined cap. Ordinary global definitions still have their existing
separate 8-current-definition bound, and may be unprepared at retained capacity. No cap was raised.
Private definitions, provenance, chat mappings and effective sources are excluded from ordinary
portable/complete settings exports. There is no implicit private export or public Pegasus default.

The existing service `--successor` fixture (also exposed as `--chat`) now covers this design with
deterministic scoped settings and owned files. Source changes alone do not establish compilation,
installed Worker/model acceptance or runtime voice behavior. The dated evidence below remains
historical at its original tier.

## Historical 1.2.2 evidence and retained legacy commands

This optional service package owns central editable communication-style preferences and immutable retained sources. It implements IServicePlugin, IServiceCommandExtension, IWorkflowModule, IServiceWorkflowContextModule and IPluginCompleteSettingsPolicy. It supplies no model tools, inference provider, grants or learning/voice runtime.

Current module status and ownership are in [the component entry](../../../docs/component-chats/personality_voice.md), refreshed2026-10-22. Service1.2.2 rebinds the unchanged settings/provider/seed contracts to Root's exact Main SDK and includes the history-full diagnostic correction. Its service and focused probe builds passed with zero warnings/errors; the first probe passed25 assertions against Main and25 against Worker in separate exact assembly contexts. [Verification](../../../build/Plugins/SettingsPresets/personality-normal-sdk-verification.json) is in-process SDK/package compatibility with a deterministic scoped settings host, not normal-application acceptance. Service DLL SHA256:c95595eb36159d13eea0105c29c23eb16830e9988c606a08240b3f803d16f455.

Current owner checkpoint is documentation/continuity reconciliation only. Root reports the previous Main and Worker closed after one recovery while host integration proceeds; the preserved Dictation 1.1.3 crash is a separate module incident. The qualified Persona pair is not relabelled as normally adopted. This pass changes no SDK, product source/binary, settings or runtime state and adds no prerequisite or approval gate to Root's existing integration work.

The historical1.2.1 [current-source transfer](../../../docs/component-chats/personality-current-source-transfer.md), [225-assertion service verification](../../../build/Plugins/SettingsPresets/personality-current-transfer-verification.json) and [handoff](../../../build/Plugins/SettingsPresets/personality-current-transfer-handoff.json) retain their original135e7ae5 DLL. Separate historical packets cover native250, actual private Worker/pipe312 and the Root-accepted13-mode/194-label model-free provider/Engine fixture. Plugins' new [native1.2.2 pair packet](../../../build/Plugins/SettingsPresets/personality-native-completion-handoff.json) passed256 assertions against servicec955 and the exact Main SDK after a native capacity fix; that includes250 repeated native/service fixture assertions plus six new capacity assertions. Keep the50 SDK checks,256 native checks and historical packets separate; none proves installed/default activation, inference or voice.

## Central preferences

The global editable-definition and legacy preference authority is pegasus.personality/global/null, SettingsVersion2:

```json
{"enabled":false,"selectedPresetId":"cerebral","customPresets":[]}
```

customPresets contains only current editable {id,name,text}, not historical versions. Bounds:8 definitions, unique canonical IDs/no built-in shadowing,32KiB/depth4 values, name80 UTF-16 units/320 UTF-8 bytes, text1024 UTF-8 bytes/120words and existing2048-byte serialized provider resolution. Valid missing desired custom IDs remain unavailable preferences. Unknown/duplicate fields, invalid Unicode and unsupported schemas refuse.

Complete/local admission/export includes all3 declared fields. Complete [] clears current editable definitions only. Portable built-in export is the selection pair with one omission and preserves destination definitions; custom selection exports{} with3 omissions and preserves all destination values. Empty portable input preserves absent destination as pending/refused rather than manufacturing defaults. Explicit incoming schema1 pair conversion is supported; destination shape is not inferred. Callbacks are pure and do not prepare/migrate/activate anything.

Private source history, accepted pins, effects/receipts, learning evidence and recordings are domain data outside complete settings. Free-form text requires ordinary review; nonsecret declaration is not automatic secret detection or product publication.

## Current owner routes

| Operation | Input |
| --- | --- |
| personality.preferences.get / personality.settings | {} |
| personality.preferences.save | schemaVersion2, expectedRevision, values |
| personality.sources.get | {} |
| personality.source.prepare | schemaVersion1, expectedSettingsRevision, expectedStoreRevision, expectedStoreSha256 |
| personality.list | {} |
| personality.get | id; optional positive revision |
| personality.preferences.transfer.get | {} |
| personality.preferences.transfer.save | schemaVersion2, expectedRevision, full values; no pending ID |
| personality.preferences.transfer.prepare | schemaVersion1, expectedRevision, expectedStoreRevision, expectedStoreSha256 |
| personality.preferences.pending.prepare | schemaVersion1, expectedRevision, pendingId, expectedPurpose, expectedStoreRevision, expectedStoreSha256 |
| personality.preferences.pending.apply | schemaVersion1, expectedRevision, pendingId, expectedPurpose, pendingSha256, valuesSha256, expectedSourceId, expectedSourceRevision, expectedSourceSha256 |

Preferences get returns schema2 central revision/state/values and provider metadata without a migration-readiness diagnostic. Save uses existing scoped central CAS. Source get returns private store CAS/retention. Prepare explicitly materializes only the selected current definition under central+private revisions, verifies central revision afterward and retains unused appends on conflict. It can run while disabled and does not clean instructions or attest lifecycle readiness. Ordinary preference routes use scoped Get/Save and refuse while complete/local pending inputs remain; they do not bypass, drain or relabel them.

Purpose-aware transfer uses the existing optional IScopedPluginSettingsTransfer. Get returns envelope2/state/pendingPurposes. Transfer Save is the no-pending-ID local edit/disable lane and preserves pending inputs. Current Prepare prepares only current active schema2 values while pending remains. Pending Prepare validates one authoritative retained purpose/document through package policy, returns canonical values/digests and prepares the selected source without changing active preferences or consuming the retained entry. Apply requires exact pins and consumes only that selected entry. Source triple fields are all-null only for an admitted disabled source-free result. See [the closed transfer schemas](../../../docs/component-chats/personality-purpose-transfer-adoption.md) and [additive current-source preparation](../../../docs/component-chats/personality-current-source-transfer.md).

The 1.2.2 native editor retains these transfer routes, exercised in its separate 256-assertion fixture; the earlier 1.2.1 packet remains historical. Portable empty input preserves original full destination values, including enabled: legacy cleanup may disable/prepare/remove the recognized section, but restores original full values before refreshed selected Apply. These are acknowledged/re-read operations, not one cross-store transaction. Unknown outcomes require reconciliation, not replay.

Old personality.save/select/admission.capture/admission.restore are absent from Operations and explicitly refuse before effects. PersonalityAdmissionEngine's old command-based CaptureAsync and the default1.0.2 test entry are retained historical bridges, not supported current entrypoints. The active host-only contribution reuses the immutable codec without reviving those commands.

## Immutable sources and context

The existing personality-context.json96KiB schema/source IDs remain readable; original combined preferences are dormant, never a live fallback. At most8 historical custom identities/32 versions; no auto-eviction/deletion/import/migration/history export. Current preferences can remain valid while preparation reports capacity failure. Exact ID/name/text matches reuse a source; ID-only matches do not. At capacity, prepared styles and accepted sources remain usable; another revision needs a separately supported history migration. Removing editable definitions would not remove retained accepted-work history.

The descriptor uses existing pegasus.personality:context.resolve v1, kind personality.behavior, contribution pegasus.personality:style, one Optional user message/priority0/BeforeCurrentInput and2048-byte result. Sources remain personality/v1/<id>/<revision>/<lowercaseSHA256>. Normal workflow normalization/receipts apply.

Historical 1.2.2 capture read central preferences and an already prepared exact source. Its context-seed envelope remains readable and records central revision/digest around unchanged seed-v1; inner settingsRevision/hash retain original source-store meaning. Current capture uses the per-chat source described above and never writes/repairs/migrates. Source reads open existing lock sidecars without recreating them; initialization creates owned sidecars. Missing/busy/corrupt storage is explicit unavailability. No style bodies cross the optional generic acceptance boundary.

Validate checks winning private codec/digest/template and identity/profile/origin read-only, without current selection or a migration callback. Restore uses final accepted target and preserves exact template except context/exclusion revisions. Current selection/definition changes do not replace an accepted source. Caller Kind/Id determine authority; DisplayName is ignored.

## Legacy and readiness

The package declares original v4 extension slots only and reads the closed original status/reason/seed/digest plus instruction/migration witness formats. Historical Ready preserves exact source/inner digest; other statuses retain absence/unavailability. Corrupt Ready refuses. No capture, dispatch, reconciliation or canonical history write occurs.

A private witness cannot populate a missing accepted host header. Unknown accepted InstallationId remains null and history-only; live Ready execution requires known accepted header equal to current host. Omitted legacy stays no-provider. Core handles closed v2/v3 absence and generic lifecycle/generation/grants; it decodes no personality data.

Clean/no old writer requires no retirement certificate or cleanup. A preference file alone is optional import data. Real legacy writers need existing exact disable/drain completion; real recognized lead regions need owner CAS cleanup/readback. Malformed/uncertain cases preserve data. The overbroad prototype observer requirement is removed. Ordinary capture relies on prepared sources plus central revision and existing generic checks; actual migration remains an explicit owner flow. Historical v4 MigrationPending stays Blocked and cannot restore as Ready. This service performs no general lead-instruction/writer inspection and makes no general clean-state claim. No new common SPI or domain decoder in Core is needed for the ordinary path.

## SDK compatibility and focused reproduction

Root closed the model window and released only focused compatibility work against cohortc4a0f320429d58522b0e9ca10bc035466b572c0a02538c8a46fea6160338ae83. CerebralSdkDirectory now verifies exact Main Core441acbbeb0ea32ca07955d82632a88c2fc3332167d3e0822851de811b06c1098 and Contracts25ecb90dd4de31e013f2043fba3e4fae636f457e57ce5f47e416f1e0df92358a, retaining SpecificVersion0.2.22 references. Worker Coree0872c123c7f9732f753d40a8585b9d29ce284ee6486fd0eaa91a59565b4e675 is distinct and was checked in a separate context. Equal assembly versions alone do not prove content/ABI compatibility. No new public contract or weakened guard was needed; normal activation/model/voice remain held.

Role correction: Worker, Main and helper Client is `1ad417aaa47e0cf8b8005d7f36d6316bc140a9f3cd905f84cbf7983cfd97e5f6`. Client `d3d28849b9fb0cc109a95a68852f28c74e0952dab97ca304e187202093fa7e05` belongs only to Configuration's distinct Coree087/Desktope906/Desktop.Plugins18ad closure. The earlier Worker Client discrepancy is withdrawn; it requires no SDK copying, rebuild or extra qualification gate. Native 1.2.2 was qualified with Main Desktop.Pluginsf8d0. Its actual mapped Configuration18ad pane check is part of routine normal adoption, not a new Persona source/build/approval prerequisite.

The focused qualification uses build/Plugins/SettingsPresets/PersonalityNormalSdkChecks.csproj with explicit Main SDK and service-candidate references, fixed normal-sdk outputs and a separate intermediate directory. The runner accepts no arguments, verifies its fixed released cohort before/after, reloads itself/service into separate Main/Worker contexts, and refuses existing report/fixture collisions. Its deterministic host rejects all settings writes. It creates two lock sidecars and retains them for review. The first service restore invocation failed before compilation because of combined arguments; its log remains separate from the corrected successful restore/build.

Do not point the whole historical service-tests project at Main: it also imports Engine, which is absent from Main's declared closure. The earlier compiled1.2.1 full package entry is **--transfer <owned-directory>**; **--successor** is its earlier117-check subset. The default1.0.2/231 entry exercises retired commands and is historical, not a current smoke-test instruction. No historical225/250/312 suite was rebuilt by this service compatibility work; Plugins reran its native fixture only because native capacity behavior changed. Preserve failed/unknown evidence and inspect fixture collisions before any new execution.

The [bounded1.2.2 handoff](../../../build/Plugins/SettingsPresets/personality-normal-sdk-handoff.json) supplies exact service/native payload pins, build/probe results, static PDB/source joins and the Root-owned next boundary. No learning/index/voice provider, service setup or private profile retrieval is part of this fixture. Normal settings/import/Conversation/model acceptance and observing a new package generation remain separately released work. A service binary generation currently requires affected-host restart. Reuse existing component outputs and preserve historical evidence/data; no new rollback graphs or automatic activation.

The sealed handoff remains SHA-256 `c7b3f28c17ac03b0ef4fd16f1d3ad6120c01638ffffc3d51f21bbe048093d294`. This README and the component entry have advanced after sealing; their two creation-time pins are historical documentation, not current file hashes. The other 34 advertised inputs and all payload/test packets remain unchanged. Do not reseal the packet or rerun completed fixtures to reconcile prose.

Normal context delivery remains pending: the prepared public `stepLimit: 2` plan would reach a retained provider receipt and stop before consumer merge; it has not run and does not prove final Engine composition or model delivery. The current settings/editor features have real backing commands. General synthesis/Books, opt-in Cortex learning/index transfer, per-chat adaptation and TTS/voice remain intentional unimplemented dependencies, with no working controls or placeholder success claimed. Cortex tools are unavailable in this owner runtime; Root may record the exact-task, Coordinator-attributed continuity update. Documentation completion does not wait for host integration.
