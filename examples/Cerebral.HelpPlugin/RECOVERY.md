# Common issues and recovery

This is the source guide for the optional Help recovery candidate, content version 2026-10-26. It is authored reference data, not a live diagnostic, execution policy or recovery executor. The Help package is not required by the engine, updater or normal recovery controls.

## Content and native surface

HelpRecoveryCatalog contains 15 stable issue IDs. Each entry supplies a plain reason, authored severity, conditional blocking rule, supported actions/readbacks, and source paths/symbols. HelpRecoveryView shows one issue at a time in a native selector. Overview and Reference > Diagnostics and recovery link to Common issues; the normal Help page selector can also open it. Documented actions are text. Only Help navigation/selection executes in this view; no host subscription, model, file, provider, settings or control command is invoked.

| Help ID | Meaning |
|---|---|
| help.issue.audit.optional | No auditor selected · optional audit |
| help.issue.audit.required-missing | Required auditor is missing |
| help.issue.audit.unsupported | Auditor does not support this checkpoint |
| help.issue.audit.stale | Auditor changed or returned stale data |
| help.issue.model.not-loaded | Selected model is not loaded |
| help.issue.worker.local-offline | Local Worker is offline or not responding |
| help.issue.worker.remote-offline | Remote Worker or its selected model is unavailable |
| help.issue.route.unavailable | Selected route or capability is unavailable |
| help.issue.resources.paused | Local resources are paused, draining or faulted |
| help.issue.work.held | Work is intentionally held |
| help.issue.tool.missing | Required tool is missing or filtered |
| help.issue.grant.missing | Required tool or capability grant is missing |
| help.issue.path.unavailable | Required path is missing or outside scope |
| help.issue.build.prerequisite | Build SDK, executable or project is unavailable |
| help.issue.effect.unknown | An effect has an unknown outcome |

An action marked SupportedReadOnly describes a permitted read route. OwnerAction describes an existing owner/maintainer contract that may change state and requires normal owner authorization/admission. ScopedAction describes an existing effectful run-scoped action, such as reconciliation, under its stated ownership. Pending means the described generic action or UI is unavailable. These labels establish source contract availability, not runtime installation, current readiness or caller permission. Commands must be discovered/described at the selected runtime before use.

## Audit recovery rules

WorkflowAuditPolicy.Required defaults to false. With an optional policy, missing/unavailable audit alone does not hold the checkpoint: deterministic checks remain authoritative, and ordinary evaluation may continue if other gates pass. It does not mean the advisory audit passed.

Required=true holds every disposition except accepted. Missing adapter, unsupported purpose, changed binding and stale/invalid/late data remain distinct. No Help action disables Required, fabricates an accepted assessment or rewrites a retained run. Accepted policy is read from jobs.workflow.get. Audit results are workflow.audit journal events read through bounded jobs.workflow.events; match event TurnId to the run ID and inspect Result/held. jobs.workflow.receipt is an effect-result reader and is not the audit journal reader.

Actual auditor contribution/admission uses IWorkflowAuditModule/IWorkflowAuditAdapter and ServicePluginRuntime. The existing owner agents.save route can persist the exact profile PluginSettings["cerebral.audit"] selection with normal revision/admission checks. Service package selection and executing composition are separate; normal Worker restart may be required. A profile save affects future admission, not an accepted run policy. Generic held-run policy rebinding remains unsupported. Existing-run recovery must preserve the exact run and its evidence and follow the owning component's supported route.

The optional stock Laya source has a parameter-free owner laya.status command reporting readiness, adapter/package/generation and diagnostics. This is a conditional existing route, not a claim that Laya is installed, admitted or ready. The package can be loaded with an empty auditor list when its INI is disabled/missing or native admission fails. There is no Laya INI/native-admission editor in Configuration. Compute & agents > Agent profiles > row menu > Inspect JSON… is the existing advanced whole-profile editor (IntegratorWindow.EditAgentJson/EditJson), despite its label. It saves through agents.save. There is no dedicated native auditor picker, admitted-descriptor list or accepted-policy rebinding UI. Do not invent a dedicated Settings link. No model/native Laya initialization was run for this Help work.

The native route map also identifies Advanced > Local recovery (Retry local connection, Open Worker log, Open data folder), Compute & agents > Machines (Probe/Edit/Worker settings), Models machine/Add models/Load/default choice, profile Model & routing and Advanced grants, and Tools & MCP executable/argument editing. Scan registers content without loading it. Folders & runtime changes paths without moving files. These are distinct from engine Resume, local resource pause and peer-grant pause. No dedicated native SDK checker/installer exists. See the Configurator-owned source map in docs/component-chats/configurator.md:7; none of these controls was invoked here.

## Bounded in-process reader and optional runtime proposal

The current desktop Help plugin has no registered agent tool, owner reader command or service contribution. Existing Help discovery entries are authored references, not a runtime Help reader. This candidate adds only plugin-owned pure C# APIs:

- List() returns the immutable bounded summaries (maximum 16, currently 15).
- Get(id) requires one exact stable Help ID.
- Read(ids) accepts one to four distinct exact IDs and returns immutable entries in request order. Empty, null, duplicate, unknown, case-alias, malformed or excessive IDs are rejected. A mixed invalid batch returns no partial result. No fuzzy/path lookup or silent truncation occurs.
- SchemaVersion=1 and ContentVersion=2026-10-26 accompany exported qualification data. Each entry has at most four actions and five source refs; individual strings are bounded at 1,600 characters. Nested lists are read-only.

Smallest future optional runtime seam: an optional Help-owned service contribution using the already established IServicePlugin.ToolExtensions and IToolExtension contracts (src/Cerebral.Core/ServicePlugins.cs and Contracts.cs). It could share this pure catalog source and expose a summary read plus exact selected reads, with explicit descriptor/capability/grant and bounded result serialization. It needs no new Core/SDK interface, mandatory host dependency, live status join, model, store or updater hook. Tool names, package wiring, grants, runtime serialization/size policy, lifecycle and acceptance must be reviewed separately. Nothing is registered or staged by this candidate. The compiled fixture export is private evidence, not a production agent endpoint.

## Qualification and remaining acceptance

The private candidate and focused fixture build against Root's accepted Main SDK under build/Core/OwnerQuiescence/LG2-20261023/bin/Cerebral.Orchestrator/release using direct binary references only. All outputs are under the established build/Help/RecoveryCatalogCandidate. Original Help packages, outputs, evidence and complete source/fixture preimages are preserved. Version/default-enable manifest policy is unchanged; installation/adoption remains Root-owned.

The focused checks cover catalog/read bounds and rejection behavior, nested immutability, all issue selections and reason/blocking/action readbacks, both Help navigation entry points, retained map scope, the 30-node/50-edge map, nine tutorial/eight image placeholders and disconnected assistant, with an inert host. Offscreen WPF captures establish only that fixture's interaction/renderer tier. They do not establish production host theme, physical keyboard/screen-reader, live recovery operation, current installed reader exposure, or normal loaded Main acceptance. Actual audit/model/resource/tool/build/effect recovery was not exercised.

Qualification results, original failures/corrections, source/SDK hashes and live gaps are recorded in build/Help/RecoveryCatalogCandidate/recovery-qualification.json. The historical 625 map checks were not rerun or added to the focused count. Current shared application status remains coordinator-owned.
