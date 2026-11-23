# Troubleshooting

Start with the smallest check that distinguishes the failure. Preserve drafts, active work and unknown outcomes. Reinstalling, deleting databases or replaying a request is rarely a useful first diagnostic.

## Build cannot find the SDK, package or asset

Check `dotnet --info` against [global.json](../global.json): the source selects 10.0.401 with compatible 10.0.4xx patch roll-forward. Check that restore used the intended package sources and that the specific plugin's SDK properties/resources are present.

An exact-hash SDK refusal means that project's admitted SDK differs. Follow its [plugin README](PLUGINS.md); do not remove the check. Native speech/runtime resources, local model files and signing assets are not included in the source archive.

The no-Books source mode is supported for source builds, but the installer path currently refuses it. See [Build](BUILD.md).

## Main cannot find Worker or opens the wrong environment

Build Worker, Configuration and Main in the same configuration. Start Main using the explicit `--source-local` command in [First setup](SETUP.md), with the intended data directory. A binary's location is not a storage-isolation setting.

Open Configuration from Main when possible so it inherits the source binding. Do not point a source experiment at an installed production database as a workaround.

## Startup says a control descriptor already exists

The source has guarded stale-descriptor recovery that validates owner, endpoint identity, old process generation and file custody. Live, foreign, malformed, changed or locked descriptors are refused. Recovery is not permission to delete an arbitrary `control.json`.

Keep the startup error and use a normal, owner-matched application exit when possible. Do not delete descriptors, change ACLs or kill a process merely because a filename collides. An older installed version may not have the public source's recovery behavior.

## “Chat support not confirmed” or composer refuses Send

Check the current connection, selected model, loaded state and workflow readiness. A connected Worker alone does not prove that the chosen model/backend supports the selected chat workflow. Inspect the actual error and Activity state.

A workflow edition requiring prerequisites that Main does not produce can fail even though another edition supports ordinary conversation. Do not invent verification recipes or remove prerequisite validation merely to run a greeting.

For the Enter preference: checked means Enter sends and Ctrl+Enter inserts a newline; unchecked reverses those two. Shift+Enter inserts a newline. A disabled composer, active IME composition or unresolved admission problem can still prevent submission.

## Model will not load or runs out of memory

Confirm the model architecture and runtime version/arguments, not only that the filename ends in GGUF. Keep the chosen runtime's DLLs together. A compatible chat GGUF and a compatible vision projector are separate inputs.

Reduce context/concurrency to a fit that leaves reserve memory, then measure. The [model guide](MODELS.md) distinguishes automatic sizing, native slots, host queues and role budgets. A load pass is not proof of sustained throughput with every slot occupied.

## Output rate is low, or only one inference is visible

First distinguish these observations:

| Observation | What to inspect |
|---|---|
| Low `out tok/s` | Actual generation timing, GPU/CPU placement, memory pressure, context length and competing work |
| Short answer | Response budget, finish reason, selected instructions/style and model behavior |
| One active inference | Whether multiple generations are ready, dependency sequencing, host admission and backend availability |
| Many configured slots but few used | Actual native capacity, free/quarantined slots, queue/scheduler limits and model assignments |

A larger context/cache can cause RAM placement or pressure even when loading succeeds. GPU occupancy alone does not prove all model/cache state is GPU-resident. Several logical agents can wait on dependencies rather than generate concurrently. Capture a bounded status/timing sample before deciding which setting to change; do not raise every limit blindly.

## Model says no tools, or Cortex Full control is refused

Read the actual enabled/loaded tools and permission state for the selected workflow/profile. Separate a model's explanation from a measured catalog result.

Cortex has its own authenticated connection/application grants; Cerebral profile permissions are a separate layer. A new or changed connection identity can need its own approved binding. Do not paste keys into prompts, copy credentials between applications, or enable every connection while diagnosing one denial.

A helper list containing analysis/writing/coding does not imply screen/mouse access. Desktop interaction needs an actual configured provider/plugin and its permissions.

## Plugin installed but nothing appears

Inspect package identity/version, compatibility, enablement and the generation actually loaded by the relevant host. A desktop package starts disabled; a service package's desired-active state can still require a normal Worker restart.

Use the right lifecycle: **Desktop plugins** manages desktop installation; **Service extensions** shows inventory and opens package commands. Paired packages need both components where their guide says so. Hosted-provider examples remain unconfigured without developer-supplied transport/profile/credential resolution.

## Operation outcome is unconfirmed

Inspect the existing run/effect and refresh/reconcile if supported. Do not automatically resend a command that might already have changed files, launched a process or contacted a service. A retry can duplicate an effect.

If stopping tracking is explicitly chosen, preserve the chat/history and original unknown result. That is not an acknowledgment that the effect succeeded or that its changes were undone.

## Useful issue reports

Include the source commit/version, Windows and .NET versions, package ID/version, selected backend, non-sensitive capacity settings, exact error type/short message and what you expected. State whether the evidence is a build, fixture, local model run, hosted-provider test or visual observation.

Remove conversation bodies, personal names/paths, credentials, keys, private references, capture/audio data and unrelated logs before publishing. Never attach a whole application data folder.
