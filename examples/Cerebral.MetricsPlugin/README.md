# Cerebral Metrics — component candidate

This optional component provides a native Configuration dashboard and a bounded local history of scalar measurements. It is a **source candidate**, not an installed or accepted feature of the running Cerebral application.

The 0.2 source candidate connects the optional `cerebral.service.scalar-generation/1` host facet through one owned, lazy subscription. It collects only instrumented generation scalars after capture is enabled. Core has separately qualified the host seam and required-facet admission at source/CPU fixture tier. This private integration still requires its own focused qualification, a coherent candidate cohort and normal application acceptance; it is not installed in the current private603 runtime.

## Component boundary

| Directory | Responsibility |
| --- | --- |
| `Shared` | Plugin-private observation, query, coverage and settings DTOs, validation and serialization. These are not additions to the public Cerebral SDK. |
| `Service` | Existing Worker service-plugin lifecycle, source adapter boundary, single-writer local history, retention, deduplication, aggregation and bounded service commands. |
| `Desktop` | Native WPF Configuration panel, filters, cards, recorded trends, capacity groups, activity and collection settings. It uses the existing desktop service-command connection. |
| `Tests` | Deterministic source fixtures, actual local-file checks, isolated service/desktop checks, offscreen WPF renders and verification evidence. |

The intended lifecycle lets collection continue with Main and Configuration closed, while the dashboard can open in Configuration independently of Main. That lifecycle must still be exercised in the normal application after host integration and adoption. There is no new Windows service, production dependency, model execution, routing control or resource-admission control in this component.

Coordinator owns the 0.2 private adapter/integration and combined acceptance. The original advisor retains read-only design/review responsibility; its frozen child output remains the 0.1 lifecycle baseline. Core owns the optional public scalar host and required-facet admission. Engine, existing Configuration source and the solution remain outside this private implementation. Metrics is not a prerequisite for unrelated engine or programming work.

## What the history means

The observation schema can retain confirmed generation token counts and sampled provider rates; prepared context and response budgets; observed cache and processed-token metadata; distinct host/provider/workflow timing fields; workflow/tool outcomes; and reported local or remote capacity. Unsupported or absent measurements remain unavailable.

These distinctions are intentional:

- Provider-observed input/output progress is stored separately from confirmed input/output usage, including terminal samples. Progress can be a lower bound; it is not billing, confirmed consumption or a known visible-token total. `ProcessedTokens`, prepared/cache/context/budget counters remain unavailable in this adapter.
- A prepared token budget is not token consumption. A process-lifetime counter is not added again to per-generation records.
- Provider output-token scope can be visible-only, reasoning plus visible, or unspecified. Mixed or unknown scopes must remain apparent; the dashboard does not relabel them as a known visible-token total.
- Provider phase rates are samples. They are not a benchmark, visible rendering time, hardware maximum, energy measurement or decomposition of routing overhead.
- Trends account for recorded observations. A cumulative generation total recorded on one date is not proof that all its tokens were generated on that date. Trend dates use UTC; displayed observation timestamps identify their source time.
- Allocated slots, reported available slots, observed executions, observed inference activity and quarantine are different fields. Local and remote worker/model pools remain separate. Unknown or offline pools are not invented available capacity. Any connection RTT must be labeled connection latency, not total inference or routing cost.
- Re-reading a receipt, refreshing the UI or restarting a collector must not create another tool execution or workflow completion. Dispatch alone is not successful execution.
- Empty history and incomplete coverage do not establish zero use. The source can be unavailable while older recorded history remains readable.

The reference screenshots supplied by the human guided the presentation. Their totals are not imported. Codex subscription limits, credits, Codex skills activity and unsupported provider billing are not available from this component.

## Storage and failure behavior

Capture defaults to off. Enabling capture and loading a service package are separate from connecting a supported source. The status view reports requested capture and actual capture independently.

The initial configurable retention policy is **30 days or 64 MiB**, whichever is reached first. These are storage settings, not model, inference or context limits. Validation currently accepts 1–365 days and 1–256 MiB. Additional record-size, retained-entity and query bounds limit work and memory; a safety bound can shorten coverage and must be visible rather than represented as complete history.

The plugin uses its host-provided private data directory and one root `history.lock`. Production0.2 writes schema2 only to `history.v2.jsonl` and `coverage.v2.json`; it reads `history.jsonl` strictly as legacy schema1 without modifying that journal or `coverage.json`. Compatible `settings.json` keeps the same preference/revision/CAS shape. After actual reader/maintenance retirement releases handles and the root lock, the older package can write its original journal; its view is explicitly legacy-only. Reupgrade strictly reloads both streams and chooses the highest revision once, without merging fields. No live migration or data rewrite is required.

Configured retention applies to the combined visible history and writable v2 maintenance. Expired legacy rows remain untouched and their physical bytes still consume the same storage budget. Hard read bytes, retained distinct entities and record sizes bound both journals together; they are not independent per-file allowances. If immutable legacy data leaves no safe room, coverage reports a storage-budget block and a gap, while compatible owner settings can still increase quota within its existing bounds. Unknown/corrupt/conflicting data stays read-only. V2 compaction preserves its own latest rows even when a higher legacy revision currently wins the combined view. Collection starts at enablement; there is no transcript backfill. Disabling capture detaches the source without deleting history. Maintenance and cancellation belong to the service lifecycle, not the desktop panel.

Stable identities distinguish durable workflow/effect facts from observations scoped to a source generation. Capacity samples retain distinct sample identities. Newer revisions replace the same logical entity rather than adding its cumulative values again. Queries aggregate the matching retained observations; any returned-row or capacity-projection limit must report omissions.

Corrupt, conflicting, truncated, linked or externally oversized history is preserved and reported as unavailable or read-only as appropriate. The component does not repair unknown bytes by silently discarding them. Storage failure must not fail a Cerebral workflow or trigger replay of a model/tool effect. A confirmed settings save must not become an ambiguous failure because later maintenance is cancelled. Retiring sources remain owned until they actually finish; a replacement must not overlap an unretired collector.

## Privacy and command authorization

Only allowlisted numeric fields, fixed state/provenance labels and bounded correlation identifiers belong in observations. Prompt text, model output, reasoning, tool arguments/results, raw debug logs, credentials, arbitrary file paths and raw error bodies are excluded. The existing `engine-debug.jsonl` is not a dashboard input.

The candidate exports no `IToolExtension` and changes no agent grants. Its service operations are:

- `metrics-history.status`
- `metrics-history.query`
- `metrics-history.settings.get`
- `metrics-history.settings.set`

The qualified future Worker restricts `metrics-history.*` to the owner connection, including refusal for an agent with generic administration capability; unrelated `diagnostics.metrics` grants are unchanged. The component exports no agent tool. This is not a new chat/project permission implementation: query filters select data and are not authorization boundaries. Broader AI history access still requires explicit identity/consent integration. The global scalar facet intentionally carries no chat/project/request/run/effect/agent/raw Binding, so this adapter cannot invent those correlations.

The desktop uses the existing captured Configuration owner connection. Connection replacement, cancellation or loss must leave an explicit unavailable/unconfirmed state and retain any displayed rows as historical. It must not crash the Configuration host or automatically repeat a settings write.

## Build and verification evidence

The owned projects use framework assemblies and explicitly pinned Cerebral reference outputs. `MetricsCoreReferencePath` selects the admitted future Core reference; do not accidentally compile against the installed603 SDK or stage reference assemblies as a deployment. Service0.2 declares schema2 `requiredHostFacets: ["cerebral.service.scalar-generation/1"]`. New hosts check the requirement before candidate staging, activation/retirement and plugin loading; old strict hosts reject the unknown manifest field. A matching AssemblyVersion or `plugin.verify` alone does not prove facet support. Service, Shared and Desktop0.2 form a coherent candidate: status/query wire schemas are version2, and Desktop refuses a mismatched service version. The old603 host is unsupported for this adapter.

The authoritative focused results and source graph are recorded under `Tests/evidence/`:

- `final-focused-results.json` — exact commands, results and evidence tiers from the completed focused verification.
- `candidate-graph.json` — frozen file and dependency coordinates for Coordinator review.
- `dashboard-fixture-wide.png` and `dashboard-fixture-narrow.png` — offscreen WPF views using clearly identified test data.
- `dashboard-source-unavailable.png` — the source-unavailable presentation.

Fixture-based collector checks, actual private-file tests, compilation and offscreen WPF rendering are distinct evidence tiers. Synthetic observations and rendered screenshots do not prove actual Worker metrics collection, normal Configuration interaction, remote readiness or real-model performance. Failed or unexecuted checks must remain identified in the results.

## Lifecycle/history review successor

The original packet above is frozen historical evidence. Its 69 declared component pins and exact graph bytes were verified and preserved under `Tests/successor-lifecycle-review/baseline`, including source preimages, outputs, logs, screenshots and the skipped symlink result. Current source includes a separately reviewed lifecycle/history successor; use its report and graph for current source coordinates:

- `Tests/successor-lifecycle-review/successor-report.json`
- `Tests/successor-lifecycle-review/successor-graph.json`
- `Tests/successor-lifecycle-review/source-delta.json`

The successor keeps a retiring capture session owned until its actual pump finishes. Source-detach exceptions cannot bypass independent cleanup or strand the history writer; a primary source-read failure remains primary and cleanup faults are reported separately. A genuinely pending re-enable is reconciled after retirement, while later opt-out, failed-save opt-out and plugin disposal suppress replacement. An ordinary source EOF/read failure does not start an automatic retry loop. History replay now applies the same immutable capacity sample-time check as append and preserves conflicting history as read-only.

A focused selector reproduced the original defects against the preserved original assemblies and then passed 31 assertions against the successor. This is deterministic lifecycle evidence plus actual private-file lock/reopen verification, not normal Worker or Configuration acceptance. The original four-project graph was also compiled once against the successor Shared contract into separate outputs, with zero warnings/errors. The closed original 102-check suite and its renders were not executed again. The original test project now explicitly compiles only `Program.cs`, so archived sources and successor outputs cannot enter its compile glob.

## Remaining integration and acceptance

1. Adopt the qualified private0.2 mapping, observed/confirmed separation, legacy/schema2 storage and required-host-facet package together with a compatible normal host. Source/CPU/composed-object qualification is complete; live collection is pending. Instrumented local/tracked-remote/selected-local generation is the initial scope; direct model calls, compaction/image validation, workflow/tool receipts, capacity and chat/project attribution need their own qualified sources. No owner-command forwarding, name-based route inference or database scraping is used.
2. Review the candidate graph and focused verification, then adopt through Coordinator's normal package and runtime controls.
3. Exercise Configuration with Main closed; collect actual observations with both GUIs closed; reopen Configuration and confirm persisted history, filters, settings and coverage.
4. Verify package removal, connection replacement, capture opt-out and storage-failure behavior in the real host.
5. Exercise a qualified real source/model route and verify exact numeric provenance without reopening closed historical capacity trials merely for availability.
6. Complete any broader agent-history permissions through the owned chat/project consent work.

The separate failed updater case and its recovery remain Coordinator-owned. This component does not launch, retry, reconcile or repair that updater.

## Scalar-host integration qualification (October27,2026)

The Coordinator0.2 packet is qualified at source/build, deterministic source/composition-fixture, actual private-file/old-binary writer and software offscreen-render tiers. The exact source/dependency/binary graph and original executions are recorded in `Tests/scalar-host-integration-review/integration-qualification.json`. These results do not establish normal Worker collection, hardware throughput, interface latency, independent Configuration interaction or production hot upgrade.

The first candidate passed70focused checks but independent review found three additional storage paths. Focused reproduction against that preserved initial0.2 backend produced7failed/6passed assertions. The corrected candidate passes83focused checks and31affected unchanged lifecycle assertions. Atomic quota replacement now publishes the pending record in one durable rewritten image; a later coverage-metadata failure cannot erase or revoke a confirmed record. Legacy gap/drop checkpoints merge without being persisted twice, and final source losses are captured through actual retirement even without a final row or GUI query. The two earlier test-harness compile failures and all original0.1/first0.2 results remain preserved.

Two new software-only offscreen views were inspected at1180and700pixels. They establish fixture layout and distinct labels, not real Configuration/renderer/hardware acceptance. Capture and all normal application settings remain unchanged. Next qualify the actual private package over the real host objects in an isolated deterministic composition fixture, then prepare a coherent future host/plugin adoption and real collection/unload/independent Configuration checks. Scoped AI history consent remains separate.

## Collectible serialization correction

The first reflective0.2 package's actual-host fixture retained its collectible context even after its reader and writer retired. A fresh isolated .NET10.0.13 status-only diagnostic found56strong reflection member-cache entries referencing private package types/members; the plugin/host/history/maintenance objects themselves had been collected. The exact prior failures and cache-root proof are preserved.

Production JSON now uses a closed package-local generated metadata context and exact typed reads/writes, including the same four-field legacy-compatible checkpoint. Service replies and Desktop requests cross the host boundary only as framework-owned JSON. There is no reflection contract fallback, global cache clearing, new dependency, permission change or model limit. Generated metadata retains WEB/enum/null/required-member and strict-schema behavior.

The corrected tuple passes105focused checks and31affected lifecycle assertions. In a separate actual ServicePluginRuntime process fixture, both default-off and status-only packages/contexts were collected, and26combined owner/EventHub/history/retirement/unload checks passed. These are isolated CPU executable/deterministic/private-file results; the package is still not adopted into the running application. Use `Tests/scalar-host-integration-review/serialization-retention-fix/qualification.json` and its exact referenced source/binary/result tuples for this correction. The earlier83/31 packet remains a valid historical data/storage qualification, not this generated-binary tuple.

The sealed original102-check source/output remains historical. The current focused suite has now been refreshed separately for the0.2 typed serializer/status contract:102checks passed,1Windows symlink check was skipped afterIOException, and3software-only offscreen views were inspected. The test project disables implicit file items so archived evidence DLLs cannot become assembly candidates ahead of its explicit SDK references. The first refreshed run exposed that stale-copy problem and remains preserved with its exact resolution trace; the corrected output was hash-checked against the admitted SDK before execution. See `Tests/scalar-host-integration-review/focused-suite-refresh/qualification.json`. No production serializer fallback or private package code changed for this suite refresh. Normal collection, independent Configuration interaction, coherent application adoption, real-model metrics and scoped AI-history access remain separate acceptance steps.
