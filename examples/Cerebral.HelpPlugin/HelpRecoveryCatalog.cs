using System.Collections.ObjectModel;

namespace Cerebral.HelpPlugin;

public enum HelpRecoverySeverity { Information, Warning, Error }
public enum HelpRecoveryAvailability { SupportedReadOnly, OwnerAction, ScopedAction, Pending }
public sealed record HelpRecoveryAction(string Id, string Title, HelpRecoveryAvailability Availability,
    string Route, string Steps, string Readback);
public sealed record HelpRecoverySource(string Path, string Symbol);
public sealed record HelpRecoveryEntry(string Id, string Title, HelpRecoverySeverity Severity, string Reason,
    string BlockingCondition, IReadOnlyList<HelpRecoveryAction> Actions, IReadOnlyList<HelpRecoverySource> Sources);
public sealed record HelpRecoverySummary(string Id, string Title, HelpRecoverySeverity Severity);

/// <summary>
/// Authored source guidance only. No live diagnosis, host calls or executable recovery delegates.
/// Public reads are an in-process plugin seam; no AI tool or SDK operation is registered.
/// </summary>
public static class HelpRecoveryCatalog
{
    public const int SchemaVersion = 1;
    public const string ContentVersion = "2026-10-26";
    public const int MaximumEntries = 16;
    public const int MaximumReadEntries = 4;
    public const int MaximumActions = 4;
    public const int MaximumSources = 5;
    public const int MaximumTextCharacters = 1600;
    public static IReadOnlyList<HelpRecoveryEntry> Entries { get; } = Array.AsReadOnly(new HelpRecoveryEntry[]
    {
        new("help.issue.audit.optional",
            "No auditor selected · optional audit",
            HelpRecoverySeverity.Information,
            "The audit policy is optional. No matching admitted auditor was selected, so an unavailable advisory record can be written while deterministic checks remain authoritative.",
            "This notice alone does not block: Required=false. Other admission checks, effects or holds may still prevent progress.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("inspect",
                    "Check the accepted audit policy",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Owner: jobs.workflow.get {id}; jobs.workflow.events {id,afterSequence,limit,snapshotSequence?}",
                    "Read the exact run and its accepted audit policy. Read bounded journal events through jobs.workflow.events and select workflow.audit records with event TurnId equal to this run ID. Their data carries result and held; effect receipts are a different reader.",
                    "Record accepted Required/AdapterId plus the matching workflow.audit result disposition and held flag. Null policy defaults to Required=false. A notice alone does not prove a hold; a profile edit does not replace the accepted run policy."),
                new("continue",
                    "Continue deterministic checks",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Existing workflow runtime and jobs.workflow.get",
                    "Check the run state and deterministic results. If no independent hold exists, ordinary workflow evaluation can continue without an optional auditor. Do not invent an auditor requirement or claim that an advisory audit passed.",
                    "A later run state or receipt establishes progress. The unavailable audit remains unavailable; no change to Required is needed.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Contracts/Workflows/WorkflowAuditContract.cs", "WorkflowAuditPolicy"),
                new("src/Cerebral.Engine/Workflows/WorkflowAuditPipeline.cs", "ReviewAsync"),
                new("src/Cerebral.Worker/WorkerWorkflows.cs", "ConfiguredAuditPolicy"),
                new("src/Cerebral.Engine/Persistence/WorkflowStore.cs", "workflow.audit")
            })),
        new("help.issue.audit.required-missing",
            "Required auditor is missing",
            HelpRecoverySeverity.Error,
            "Required=true and the selected adapter is absent from the admitted auditor registry, or AdapterId is missing. The checkpoint cannot be accepted.",
            "Required=true and audit disposition is not accepted. Deterministic checks still run but do not replace the required audit.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("inspect",
                    "Check the accepted audit policy",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Owner: jobs.workflow.get {id}; jobs.workflow.events {id,afterSequence,limit,snapshotSequence?}",
                    "Read the exact run and its accepted audit policy. Read bounded journal events through jobs.workflow.events and select workflow.audit records with event TurnId equal to this run ID. Their data carries result and held; effect receipts are a different reader.",
                    "Record accepted Required/AdapterId plus the matching workflow.audit result disposition and held flag. Null policy defaults to Required=false. A notice alone does not prove a hold; a profile edit does not replace the accepted run policy."),
                new("select",
                    "Select an admitted auditor for future work",
                    HelpRecoveryAvailability.OwnerAction,
                    "Compute & agents > Agent profiles > row menu > Inspect JSON… (advanced editor); owner agents.save; reviewed service-package lifecycle",
                    "In the existing advanced profile editor, have the owner select the actual admitted adapter ID in PluginSettings[\"cerebral.audit\"], preserving Required and unrelated fields. The menu says Inspect JSON… but edits/saves the profile. Identify the actual module/descriptor before saving. Reviewed service-package changes may require a normal Worker restart to compose the selected generation.",
                    "Read the saved profile revision/policy and running service generation. Conditional owner laya.status {} reports stock Laya readiness, adapter ID, generation and diagnostics when its package command is loaded. Loaded=true can still export zero auditors. A save or ready package is not an accepted audit and cannot rewrite an accepted run."),
                new("pending",
                    "Auditor selection and existing-run recovery",
                    HelpRecoveryAvailability.Pending,
                    "Pending: dedicated native auditor picker, admitted-descriptor listing, Laya INI/native-admission editor and accepted-policy rebinding",
                    "An advanced whole-profile editor exists, but no dedicated auditor picker or admitted-descriptor list exists in the inspected UI. There is no Laya INI/native-admission editor. Help cannot supply an adapter, rewrite accepted policy or clear a required hold. Ask the owner for an admitted adapter and supported held-run continuation; do not disable Required.",
                    "Keep the held run and its receipts. Completion requires a current matching accepted audit for the checkpoint; a profile save is not that evidence.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Engine/Workflows/WorkflowAuditPipeline.cs", "ReviewAsync"),
                new("src/Cerebral.Worker/ServicePluginRuntime.cs", "IWorkflowAuditModule"),
                new("src/Cerebral.Worker/WorkerWorkflows.cs", "ConfiguredAuditPolicy"),
                new("src/Cerebral.Engine/Persistence/WorkflowStore.cs", "workflow.audit"),
                new("src/Cerebral.Auditing.Laya/LayaStockPlugin.cs", "laya.status")
            })),
        new("help.issue.audit.unsupported",
            "Auditor does not support this checkpoint",
            HelpRecoverySeverity.Error,
            "The admitted descriptor does not list the requested purpose, such as component_evidence_check or coordinator_decision_check.",
            "A nonaccepted audit holds only when Required=true. With an optional policy this is advisory unavailability, not a successful audit.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("inspect",
                    "Check the accepted audit policy",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Owner: jobs.workflow.get {id}; jobs.workflow.events {id,afterSequence,limit,snapshotSequence?}",
                    "Read the exact run and its accepted audit policy. Read bounded journal events through jobs.workflow.events and select workflow.audit records with event TurnId equal to this run ID. Their data carries result and held; effect receipts are a different reader.",
                    "Record accepted Required/AdapterId plus the matching workflow.audit result disposition and held flag. Null policy defaults to Required=false. A notice alone does not prove a hold; a profile edit does not replace the accepted run policy."),
                new("purpose",
                    "Use an adapter that declares the exact purpose",
                    HelpRecoveryAvailability.OwnerAction,
                    "IWorkflowAuditAdapter.Descriptor.Purposes; owner profile save",
                    "Ask the package maintainer to supply an admitted adapter whose descriptor declares the exact purpose requested by this checkpoint. Use the supported service lifecycle and owner profile selection; keep Required unchanged.",
                    "Verify exact ID, package, version, generation, fingerprint and purpose list at composition. Inspect the next audit result for matching bindings and disposition accepted."),
                new("pending",
                    "Auditor selection and existing-run recovery",
                    HelpRecoveryAvailability.Pending,
                    "Pending: dedicated native auditor picker, admitted-descriptor listing, Laya INI/native-admission editor and accepted-policy rebinding",
                    "An advanced whole-profile editor exists, but no dedicated auditor picker or admitted-descriptor list exists in the inspected UI. There is no Laya INI/native-admission editor. Help cannot supply an adapter, rewrite accepted policy or clear a required hold. Ask the owner for an admitted adapter and supported held-run continuation; do not disable Required.",
                    "Keep the held run and its receipts. Completion requires a current matching accepted audit for the checkpoint; a profile save is not that evidence.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Contracts/Workflows/WorkflowAuditContract.cs", "WorkflowAuditDescriptor"),
                new("src/Cerebral.Engine/Workflows/WorkflowAuditPipeline.cs", "ReviewAsync"),
                new("src/Cerebral.Engine/Workflows/WorkflowRuntime.cs", "coordinator_decision_check"),
                new("src/Cerebral.Engine/Persistence/WorkflowStore.cs", "workflow.audit"),
                new("src/Cerebral.Auditing.Laya/LayaStockPlugin.cs", "laya.status")
            })),
        new("help.issue.audit.stale",
            "Auditor changed or returned stale data",
            HelpRecoverySeverity.Error,
            "The descriptor changed after admission, or the returned request, generation, fingerprint, input/proposal hashes or advisory flags do not match this checkpoint. Deadline failure can also prevent acceptance.",
            "Required=true holds every nonaccepted disposition, including unavailable, rejected, stale, timed_out and failed. Optional audit failure does not itself impose that hold.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("inspect",
                    "Check the accepted audit policy",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Owner: jobs.workflow.get {id}; jobs.workflow.events {id,afterSequence,limit,snapshotSequence?}",
                    "Read the exact run and its accepted audit policy. Read bounded journal events through jobs.workflow.events and select workflow.audit records with event TurnId equal to this run ID. Their data carries result and held; effect receipts are a different reader.",
                    "Record accepted Required/AdapterId plus the matching workflow.audit result disposition and held flag. Null policy defaults to Required=false. A notice alone does not prove a hold; a profile edit does not replace the accepted run policy."),
                new("readmit",
                    "Readmit the exact compatible auditor",
                    HelpRecoveryAvailability.OwnerAction,
                    "IWorkflowAuditModule admission; ServicePluginRuntime; supported service lifecycle",
                    "Have the maintainer correct the adapter binding or result contract and use normal package admission/composition. Preserve the old audit and run evidence. A cached accepted audit cannot be reused for a different request or proposal.",
                    "A fresh audit must match its request ID, hashes, generation and fingerprint, remain advisory-only, and be within the checkpoint deadline. Do not relabel stale data accepted."),
                new("pending",
                    "Auditor selection and existing-run recovery",
                    HelpRecoveryAvailability.Pending,
                    "Pending: dedicated native auditor picker, admitted-descriptor listing, Laya INI/native-admission editor and accepted-policy rebinding",
                    "An advanced whole-profile editor exists, but no dedicated auditor picker or admitted-descriptor list exists in the inspected UI. There is no Laya INI/native-admission editor. Help cannot supply an adapter, rewrite accepted policy or clear a required hold. Ask the owner for an admitted adapter and supported held-run continuation; do not disable Required.",
                    "Keep the held run and its receipts. Completion requires a current matching accepted audit for the checkpoint; a profile save is not that evidence.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Engine/Workflows/WorkflowAuditPipeline.cs", "IsCurrent"),
                new("src/Cerebral.Engine/Workflows/WorkflowAuditPipeline.cs", "ReviewAsync"),
                new("src/Cerebral.Worker/ServicePluginRuntime.cs", "WorkflowAuditPipeline"),
                new("src/Cerebral.Engine/Persistence/WorkflowStore.cs", "workflow.audit"),
                new("src/Cerebral.Auditing.Laya/LayaStockPlugin.cs", "laya.status")
            })),
        new("help.issue.model.not-loaded",
            "Selected model is not loaded",
            HelpRecoverySeverity.Error,
            "A configured model entry is not evidence of a resident, compatible runtime. The selected model may be unloaded, missing content or incompatible with its current configuration.",
            "Model execution blocks while the exact selected route cannot prepare or acquire the required model. Help, settings and unrelated capabilities remain usable.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("inspect",
                    "Check the exact selected model",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Owner: models.list {}; models.inspect {id}; models.runtime.status {}",
                    "Read the selected model ID and Worker binding, then inspect its content and runtime state. Keep unknown, unloaded and unsupported states separate.",
                    "The exact selected ID and runtime state must agree. A saved default or a model-list row does not prove residency or capability."),
                new("load",
                    "Load a compatible installed model",
                    HelpRecoveryAvailability.OwnerAction,
                    "Models > selected machine > Load; Use for Pegasus Cerebral changes the reviewed default",
                    "In Models, select the intended machine/model and use Load after resolving content/capability errors. Scan/Add models registers content; it does not load it. Use for Pegasus Cerebral explicitly changes the default and is a separate owner choice. Model loading may consume substantial resources.",
                    "Read models.runtime.status again and check the acknowledged model identity and state. Configuration and actual execution readiness are separate."),
                new("content",
                    "Register or configure reviewed model content",
                    HelpRecoveryAvailability.OwnerAction,
                    "Owner: models.import / models.configure; workers.models.import / workers.models.configure",
                    "Use Models > machine > Add models for the existing model-library import/configure flow on that Worker and its own paths. Discover exact command schemas for agent-driven owner operations. Import/registration and an actual compatible load remain separate steps.",
                    "Read back the saved content identity and configuration, then verify actual runtime state. A registration acknowledgement is not model residency."),
                new("transfer",
                    "Missing binary distribution is not implemented",
                    HelpRecoveryAvailability.Pending,
                    "Pending: missing binary transfer through models.distribute",
                    "models.distribute can verify already-installed exact hashes and report ExistingVerified. It does not transfer missing binaries. Use an existing approved import/transfer path for the target Worker rather than claiming distribution completed.",
                    "Require verified content on the intended Worker. A transfer proposal or ExistingVerified for other content does not establish this model is present.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Worker/CommandDispatcher.cs", "models.runtime.status"),
                new("src/Cerebral.Worker/DefaultModelSwitch.cs", "SwitchDefaultModelCommandAsync"),
                new("src/Cerebral.Worker/ModelDistribution.cs", "ExistingVerified"),
                new("src/Cerebral.Integrator/IntegratorModelWorkers.cs", "models.import")
            })),
        new("help.issue.worker.local-offline",
            "Local Worker is offline or not responding",
            HelpRecoverySeverity.Error,
            "The client has no valid response from the intended local Worker. This differs from a reachable Worker reporting an unloaded model.",
            "Requests requiring that Worker cannot proceed until the normal connection is established and identity is verified.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("health",
                    "Check the existing connection",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Advanced > Local recovery > Open Worker log / Open data folder; health and diagnostics when connected",
                    "Use the existing Local recovery view to inspect the Worker log or data folder for this instance. Once connected, read normal health/diagnostics. Preserve the actual connection failure; changing the selected model does not repair a missing connection.",
                    "A fresh health response establishes reachability at that moment. Inspect model runtime separately; reachability alone does not establish model readiness."),
                new("reconnect",
                    "Retry the normal local connection",
                    HelpRecoveryAvailability.OwnerAction,
                    "Advanced > Local recovery > Retry local connection",
                    "Use the existing retry control after reviewing the reported connection failure. It retries the normal connection; it does not install or reconfigure the Worker service.",
                    "Require fresh connected/health readback for the intended instance, then inspect model runtime separately. A running process alone does not establish the connection."),
                new("repair",
                    "Repair the actual launch or connection failure",
                    HelpRecoveryAvailability.Pending,
                    "Pending in Help: automatic Worker/service repair",
                    "Use the normal installation or launch diagnostics for the reported instance. There is no recovery command available through an unreachable Worker. Help does not start, install, reconfigure or kill a service.",
                    "After the owner resolves the reported condition, reconnect and obtain fresh health and model state. A running process alone is insufficient.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Worker/CommandDispatcher.cs", "health"),
                new("src/Cerebral.Worker/CommandDispatcher.cs", "diagnostics"),
                new("src/Cerebral.Integrator/IntegratorStartup.cs", "Retry local connection")
            })),
        new("help.issue.worker.remote-offline",
            "Remote Worker or its selected model is unavailable",
            HelpRecoverySeverity.Error,
            "The assigned remote route failed identity, owner-access, reachability or model-state validation. A peer token is not a remote owner-management credential.",
            "Only work needing that route blocks. An offline or stale response is not a verified zero-slot count and does not authorize local substitution.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("probe",
                    "Inspect the assigned remote Worker",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Compute & agents > Machines > Remote machine connections > Probe / Edit / Worker settings; owner workers.probe and workers.models reads",
                    "Use the configured owner route for the exact Worker, then inspect the model on that Worker. Report unknown/offline and any identity mismatch exactly; never expose its access key.",
                    "Require a fresh successful probe with the intended Worker identity and a current model readback. Cached configuration does not prove current availability."),
                new("load",
                    "Load the model on the intended Worker",
                    HelpRecoveryAvailability.OwnerAction,
                    "Owner: workers.models.load {workerId,id}",
                    "Once that Worker is reachable and authorized, use its existing model control to load the selected installed model. Do not silently route to a local model or replace credentials.",
                    "Inspect the same Worker/model again. A remote load acknowledgement is distinct from a successful inference."),
                new("remote-repair",
                    "Connection and content repair",
                    HelpRecoveryAvailability.Pending,
                    "Pending in Help: remote host repair or missing-content transfer",
                    "Have the owner resolve the actual remote host, connection, access or content error through that component's supported controls. An unreachable Worker cannot process a model load.",
                    "Re-probe before trusting availability. Unknown retained effects require their own outcome reconciliation.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Worker/CommandDispatcher.cs", "workers.models.load"),
                new("src/Cerebral.Worker/RemoteWorkerManagement.cs", "ConnectAsync"),
                new("src/Cerebral.Worker/RemoteModelRuntimeAdapter.cs", "RemoteModelRuntimeAdapter")
            })),
        new("help.issue.route.unavailable",
            "Selected route or capability is unavailable",
            HelpRecoverySeverity.Error,
            "The current adapter, binding or descriptor cannot provide the requested capability. Saved routing and passive runtime-support reports are different from execution admission.",
            "The requested capability blocks until the exact selected route admits it. An unavailable report does not prove that the model is unloaded.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("descriptor",
                    "Read the permitted exact contract",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "sdk.discover / sdk.describe; tools.list; accepted-workflow cerebral.host:catalog",
                    "Select the exact permitted descriptor or accepted-workflow catalog entry. Check its input schema, required capability and current binding; a filtered or limited list is not installed-package inventory.",
                    "Record the supported operation/version and required capability. Discovery grants no authority and does not prove an execution route is ready."),
                new("runtime-support",
                    "Inspect passive runtime support when exposed",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Owner: models.runtime-support.read; workers.models.runtime-support.read",
                    "Read the current operation schema with sdk.describe first and provide its exact model and installation identity. Preserve not_reported, operation_unavailable, identity_changed and binding_changed as distinct unavailability reasons.",
                    "These passive reads do not load a model, acquire a lease or admit new execution capabilities. Use normal admission for execution."),
                new("selection",
                    "Review the selected binding",
                    HelpRecoveryAvailability.OwnerAction,
                    "Compute & agents > Agent profiles > Edit > Model & routing; owner agents.save / models.default.switch",
                    "Have the owner choose an actually compatible configured route through its existing controls. Preserve accepted work; do not mutate its snapshot to grant a capability retroactively.",
                    "Read back the saved profile/binding and submit new work only through normal admission when needed. An unsupported adapter remains unsupported.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Worker/WorkerWorkflows.cs", "Describe"),
                new("src/Cerebral.Worker/CommandDispatcher.RuntimeSupport.cs", "RuntimeSupportOperations"),
                new("src/Cerebral.Worker/WorkerWorkflowEffects.cs", "ToolAsync")
            })),
        new("help.issue.resources.paused",
            "Local resources are paused, draining or faulted",
            HelpRecoverySeverity.Warning,
            "The owner's local-resource state closes admission during pause, drain or recovery. This differs from an engine work hold.",
            "Work that needs paused local resources waits. Draining cannot resume yet; Faulted stays closed until the reported condition is resolved.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("status",
                    "Read the local-resource state",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Owner: resources.local.status {}",
                    "Read state, detail, active/queued work and model information. Wait for Paused when a drain is in progress. Do not infer resource release from cancellation intent.",
                    "The fresh state and reported counts establish only their stated scope. Offline peers and unknown remote capacity remain unknown."),
                new("resume",
                    "Resume after the pause has settled",
                    HelpRecoveryAvailability.OwnerAction,
                    "Owner: resources.local.resume {}; resources.local.pause {rerouteEligibleWork:bool}",
                    "If the owner intends to resume and state is Paused, use Resume. For Faulted, resolve the reported condition and retry Pause before Resume. Never force Resume through Draining.",
                    "Read resources.local.status again. A failed reload retains closed admission; require Active before claiming that local admission reopened.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Worker/ResourcePause.cs", "GetResourcePauseStatus"),
                new("src/Cerebral.Worker/ResourcePause.cs", "ResumeLocalResourcesAsync"),
                new("examples/Cerebral.ResourcePausePlugin/ResourcePausePlugin.cs", "resources.local.resume")
            })),
        new("help.issue.work.held",
            "Work is intentionally held",
            HelpRecoverySeverity.Warning,
            "An owner intentional hold captures selected work and prevents new admission. A paused engine alone does not identify or clear that marker.",
            "The exact active hold remains until its captured executions settle and the owner resolves the marker. Unknown effects remain separately held.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("status",
                    "Read the exact work-hold marker",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Owner: engine.work-hold.status {}",
                    "Read holdId and the current epoch, generation and revision. Check the captured work and any remaining active execution before deciding whether to resume or cancel.",
                    "Use these fresh comparison fields, not a retained marker. A generic waiting row is not proof of an intentional hold."),
                new("resolve",
                    "Resolve the exact owner hold",
                    HelpRecoveryAvailability.OwnerAction,
                    "Owner: engine.work-hold.resume or engine.work-hold.cancel {holdId,expectedEpoch,expectedGeneration,expectedRevision}",
                    "Use the existing work-hold controls and the exact fresh marker. Resume releases eligible captured work only after settling. Cancel applies to the captured work and leaves the engine paused. Ordinary engine.resume cannot clear this hold.",
                    "Read work-hold status and affected runs afterward. Interrupted or Unknown effects remain reviewable; releasing a marker is not evidence that those effects completed.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Worker/EngineIntentionalWorkHoldCommands.cs", "IntentionalWorkHoldCommandAsync"),
                new("src/Cerebral.Worker/EngineCommands.cs", "engine.resume"),
                new("examples/Cerebral.ResourcePausePlugin/ResourcePauseWorkHold.cs", "engine.work-hold.resume")
            })),
        new("help.issue.tool.missing",
            "Required tool is missing or filtered",
            HelpRecoverySeverity.Error,
            "The requested exact tool is not present in the caller's permitted current catalog. It may be disabled, unavailable, filtered or supplied by an absent provider.",
            "The particular tool call blocks until its exact descriptor and permissions are available. A filtered result does not prove the provider is uninstalled.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("catalog",
                    "Inspect the caller's permitted catalog",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "tools.list {profileId}; accepted-workflow cerebral.host:catalog",
                    "Read the matching tool summary and exact input schema in the current caller scope. Distinguish root tools.list from the structured SDK directory. There is no generic tools.catalog root command.",
                    "Require the exact tool name, capability and current input schema. Preserve the original provider identity from retained receipts where available; mark unreported provider/version metadata unavailable. A descriptor does not grant execution authority."),
                new("provider",
                    "Resolve the actual provider or selection",
                    HelpRecoveryAvailability.OwnerAction,
                    "Owner configuration/provider controls; agents.save for reviewed profile changes",
                    "Have the owning component resolve the disabled or failed provider, or select a supported exact tool. Use normal package/configuration admission. Do not substitute a similarly named tool for a retained effect.",
                    "Re-read the permitted catalog. Existing accepted snapshots do not acquire new descriptors retroactively; a changed tool may require a new submission.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Worker/WorkerWorkflowConversation.cs", "CatalogData"),
                new("src/Cerebral.Worker/CommandDispatcher.cs", "tools.list"),
                new("src/Cerebral.Worker/WorkerWorkflowEffects.cs", "ToolAsync")
            })),
        new("help.issue.grant.missing",
            "Required tool or capability grant is missing",
            HelpRecoverySeverity.Error,
            "The exact tool name and its required capability must both be permitted. Saved accepted grants and current grants are checked together.",
            "The protected call remains refused while either grant is absent or revoked. Help text, a discovered tool or an agent role does not add authority.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("inspect",
                    "Check the exact denied permission",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "tools.list {profileId}; owner agents.list; exact accepted workflow read",
                    "Inspect the refusal and the selected profile's allowed tools and capabilities. file.edit-range specifically requires a literal file.edit-range grant; a wildcard-only grant is insufficient.",
                    "Record the missing tool or capability without expanding it. Current and accepted permissions must both allow execution."),
                new("review",
                    "Ask the owner to review the needed grant",
                    HelpRecoveryAvailability.OwnerAction,
                    "Compute & agents > Agent profiles > Edit > Advanced (roots, tool names, capabilities); owner agents.save",
                    "Explain the smallest permission needed for the task. If the owner approves, save the profile through normal current-revision admission, preserving unrelated settings. Agents must not grant themselves access.",
                    "Read back the saved revision and use normal new-work admission where required. A profile change cannot enlarge an existing accepted snapshot.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Tools/ToolPolicy.cs", "Grant"),
                new("src/Cerebral.Worker/CommandDispatcher.cs", "agents.save"),
                new("src/Cerebral.Worker/WorkerWorkflowEffects.cs", "ToolAsync")
            })),
        new("help.issue.path.unavailable",
            "Required path is missing or outside scope",
            HelpRecoverySeverity.Error,
            "A file, project or output path may not exist, may be on another Worker, or may be outside the accepted read/write scope. Links and traversal do not bypass the path policy.",
            "The affected file/build operation blocks until the exact existing path and its required read or write access are established.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("inspect",
                    "Check the exact path and chat rules",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "Permitted file.inspect; owner conversations.paths.get {id}",
                    "Inspect the reported path only through an already granted file tool. Check the selected Worker and conversation path rules. Read access to project input does not imply write access to build output.",
                    "Confirm the actual path, existence and required access. Do not interpret access denied as absence or copy remote paths onto this machine."),
                new("rules",
                    "Review a narrowly scoped path rule",
                    HelpRecoveryAvailability.OwnerAction,
                    "Owner: conversations.paths.save {id,expectedRevision,grants:[{path,access}],initial?}",
                    "Use the existing chat path-rule editor only after the owner approves the required scope. Supply a current revision and an exact path/access rule. Preserve unrelated grants.",
                    "Read conversations.paths.get again and allow normal admission to apply the rules. Unsupported or unsafe paths remain refused; do not disable validation.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Core/ScopedFilePolicy.cs", "ScopedFilePolicy"),
                new("src/Cerebral.Worker/CommandDispatcher.ConversationPaths.cs", "ConversationPathsCommand"),
                new("src/Cerebral.Tools/ToolDispatcher.cs", "file.inspect")
            })),
        new("help.issue.build.prerequisite",
            "Build SDK, executable or project is unavailable",
            HelpRecoverySeverity.Error,
            "The build may lack a registered executable, an installed compatible SDK, an exact project path or an admitted output directory. These are distinct failures.",
            "Only the build/run operation blocks. A project file alone does not establish the required SDK/toolchain or writable output scope.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("inspect",
                    "Read the build failure and registered tool",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "tools.list; permitted process.run with an exact registered executable ID and separate argv",
                    "Use the actual failure to identify the missing prerequisite. Check the existing executable registration, exact project/solution path and admitted working/output directories. If the registered .NET tool is available and permitted, its --list-sdks read can establish installed SDK versions.",
                    "Record the actual executable/SDK version and explicit project. Bounded filename hints are suggestions; multiple candidates require an exact selection."),
                new("configure",
                    "Review the missing prerequisite",
                    HelpRecoveryAvailability.OwnerAction,
                    "Tools & MCP > registered executable path and separate arguments; normal installation workflow",
                    "Have the owner register or configure the reviewed existing executable, choose the correct project, or install the required SDK through the normal installation process. Help does not install tools or expand grants after a build failure.",
                    "Re-read the configuration. A later focused build must report its real exit code and diagnostics; configuration alone is not a successful build."),
                new("sdk-ui",
                    "Automatic SDK checking or installation",
                    HelpRecoveryAvailability.Pending,
                    "Pending: dedicated native build-SDK checker/installer",
                    "No native SDK checker or installer exists in the inspected Configuration. Read the actual failed receipt and distinguish executable registration, current directory/project, copied public SDK and installed machine SDK causes. Folders & runtime changes paths without moving files.",
                    "Use a supported owner repair for the identified cause before another build. Do not promise a repair button or repeat an unchanged failed command blindly.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Tools/ToolDispatcher.cs", "process.run"),
                new("src/Cerebral.Tools/ToolDispatcher.cs", "RequiresConfiguration"),
                new("src/Cerebral.Integrator/IntegratorWindow.cs", "RequiresConfiguration")
            })),
        new("help.issue.effect.unknown",
            "An effect has an unknown outcome",
            HelpRecoverySeverity.Error,
            "Execution may have crossed the side-effect boundary before a reply or terminal result was recorded. Unknown is neither a clean failure nor confirmed cancellation.",
            "The effect stays held until the original provider and exact retained identity can establish its outcome. Resume cannot safely replay it.",
            Array.AsReadOnly(new HelpRecoveryAction[]
            {
                new("inspect",
                    "Preserve and inspect the exact effect",
                    HelpRecoveryAvailability.SupportedReadOnly,
                    "jobs.workflow.get {id}; jobs.workflow.receipt using the current bounded receipt schema",
                    "Read the accepted run, effect ID/state, command/provider binding and retained receipts. Preserve the evidence and distinguish cleanup/resource state from execution outcome.",
                    "Record the exact Unknown effect and missing evidence. Do not infer success from a closed process or failure from a lost reply."),
                new("reconcile",
                    "Check the existing outcome",
                    HelpRecoveryAvailability.ScopedAction,
                    "Existing workflow Check outcome; jobs.workflow.reconcile {id} under run ownership",
                    "Use the original effect's supported reconciliation path. This can record a recovered result, so it is not a passive read. It requires the exact command version, original provider, current/accepted grants and an applicable recovery adapter. Agents may use only their own admitted run route. Reconciliation may update the run and allow normal continuation.",
                    "Read the effect afterward. If outcome remains Unknown, keep the hold and ask the owner; do not submit a replacement effect. Recent-job scans and substitute providers do not prove the original outcome."),
                new("specialized",
                    "Use a specialized owner recovery only when eligible",
                    HelpRecoveryAvailability.OwnerAction,
                    "Owner: jobs.workflow.receipt-lookup.inspect/confirm; workers.models.recover",
                    "Read the exact descriptor first. Receipt lookup applies only to its eligible preserved effect and requires its exact comparison fields. Remote model recovery requires workerId, id, expectedEngineEpoch, a healthy paused engine and a settled retained-work barrier; it clears a verified model-route fence.",
                    "Read back the exact effect or model state. Clearing a remote model fence leaves retained Unknown workflows unchanged; it does not establish their outcome."),
                new("unsupported",
                    "Provider has no supported recovery adapter",
                    HelpRecoveryAvailability.Pending,
                    "Pending: generic recovery for every effect/provider",
                    "Keep the original effect held and escalate with its exact receipt. Help cannot invent a provider adapter, turn Unknown into Failed, or replay an operation to discover what happened.",
                    "The original outcome remains Unknown until supported exact evidence establishes it. Preserve the run and unresolved work.")
            }),
            Array.AsReadOnly(new HelpRecoverySource[]
            {
                new("src/Cerebral.Worker/CommandDispatcher.cs", "jobs.workflow.reconcile"),
                new("src/Cerebral.Engine/Workflows/WorkflowRuntime.cs", "ReconcileAsync"),
                new("src/Cerebral.Tools/ToolDispatcher.cs", "ReconcileCoreAsync"),
                new("src/Cerebral.Worker/RemoteModelRecovery.cs", "RecoverRemoteModelAsync")
            }))
    });
    private static readonly IReadOnlyDictionary<string, HelpRecoveryEntry> Index =
        new ReadOnlyDictionary<string, HelpRecoveryEntry>(Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal));
    private static readonly IReadOnlyList<HelpRecoverySummary> Summaries = Array.AsReadOnly(Entries
        .Select(entry => new HelpRecoverySummary(entry.Id, entry.Title, entry.Severity)).ToArray());

    static HelpRecoveryCatalog()
    {
        if (Entries.Count is < 1 or > MaximumEntries) throw new InvalidOperationException("Recovery catalog bounds are invalid.");
        foreach (var entry in Entries)
        {
            CheckId(entry.Id); CheckText(entry.Title); CheckText(entry.Reason); CheckText(entry.BlockingCondition);
            if (!Enum.IsDefined(entry.Severity) || entry.Actions.Count is < 1 or > MaximumActions ||
                entry.Sources.Count is < 1 or > MaximumSources ||
                entry.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != entry.Actions.Count)
                throw new InvalidOperationException("Recovery entry bounds or identities are invalid.");
            foreach (var action in entry.Actions)
            {
                CheckText(action.Id); CheckText(action.Title); CheckText(action.Route); CheckText(action.Steps); CheckText(action.Readback);
                if (!Enum.IsDefined(action.Availability)) throw new InvalidOperationException("Unknown recovery availability.");
            }
            foreach (var source in entry.Sources) { CheckText(source.Path); CheckText(source.Symbol); }
        }
    }

    public static IReadOnlyList<HelpRecoverySummary> List() => Summaries;
    public static HelpRecoveryEntry Get(string id)
    {
        CheckId(id);
        return Index.TryGetValue(id, out var entry) ? entry : throw new ArgumentException("Unknown recovery Help ID.", nameof(id));
    }

    // Validate the entire request before returning data; never silently truncate or resolve partial IDs.
    public static IReadOnlyList<HelpRecoveryEntry> Read(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count is < 1 or > MaximumReadEntries) throw new ArgumentException("Read one to four exact recovery Help IDs.", nameof(ids));
        var selected = ids.ToArray();
        if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new ArgumentException("Recovery Help IDs must be distinct.", nameof(ids));
        return Array.AsReadOnly(selected.Select(Get).ToArray());
    }

    private static void CheckId(string? id)
    {
        if (id is null || id.Length > 96 || !id.StartsWith("help.issue.", StringComparison.Ordinal) ||
            id.Length == "help.issue.".Length || id.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')))
            throw new ArgumentException("Use an exact bounded recovery Help ID.", nameof(id));
    }
    private static void CheckText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTextCharacters)
            throw new InvalidOperationException("Recovery text is empty or exceeds its bound.");
    }
}
