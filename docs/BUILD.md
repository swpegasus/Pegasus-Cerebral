# Building the source preview

Release label: `0.2.22-preview.20261122`.

This guide identifies the intended build entry points. A fresh-checkout restore, build and full test pass of this exact snapshot have **not been verified**. These commands are not an installer and do not update an existing Cerebral installation.

## Prerequisites

- Windows x64.
- .NET 10 SDK, including its Windows desktop build support.
- PowerShell 7 for repository PowerShell tooling.
- Access to the dependencies declared by the checked-in projects and NuGet configuration.
- For optional CUDA acceleration: a compatible NVIDIA GPU, driver and separately acquired model backend/runtime.

Use your own configuration and credentials. Do not copy private development settings, account tokens, machine enrollment material, signing keys or private certificates into a checkout or public package.

## Intended build entry points

From the repository root, the main projects are:

| Component | Project |
| --- | --- |
| Main desktop | `src/Cerebral.Orchestrator/Cerebral.Orchestrator.csproj` |
| Configuration | `src/Cerebral.Integrator/Cerebral.Integrator.csproj` |
| Worker runtime | `src/Cerebral.Worker/Cerebral.Worker.csproj` |

For example, these are the ordinary Main source-build commands:

```powershell
dotnet restore .\src\Cerebral.Orchestrator\Cerebral.Orchestrator.csproj
dotnet build .\src\Cerebral.Orchestrator\Cerebral.Orchestrator.csproj -c Release --no-restore
```

Restore and build the other project paths separately when needed. The project graph supplies its declared dependencies. Books is outside this release; do not use its projects or tests as a prerequisite.

Use a normal developer checkout and ordinary user session. Do not redirect build output into an installed application directory. Do not set installed-framework override properties merely to make a source build pass; that would be a different compatibility qualification.

If restore or build reports a missing package, bundled input or incompatible reference, retain the diagnostic and treat it as an unresolved preview issue. Do not disable integrity or compatibility guards, introduce another developer's credentials, or claim a successful clean build from isolated historical outputs.

## Models and runtimes

Weights and native inference/runtime binaries are acquired separately from their upstream publishers and are not in this source release. Choose files supported by the selected backend, retain their licenses and provenance, and verify publisher-provided checksums where available.

For a GGUF chat backend, select the intended quantized model file in Configuration. Vision additionally requires the matching projector for that exact model/backend combination; a projector is not interchangeable with the chat weights. AI dictation uses a separate speech-recognition model and its own applicable license.

Keep model/runtime files in a user-selected data location and configure the appropriate path through the product's supported setup. Do not manually put files into an immutable plugin package generation or edit its manifest to substitute a model. This guide does not assert an unverified model filename, revision, checksum or universal default directory.

## Capacity and first use

Start with a model and context/concurrency combination that fits the machine, then use the supported hardware-aware capacity checks. Longer contexts and more simultaneous slots compete for memory; CPU/RAM offload changes performance and fit. A 16 GB CUDA-capable GPU and at least 32 GB RAM are a starter target, not a verified eight-slot × 100K preset.

Main conversation, technical coordinator work and individual workers can need different logical budgets. Roughly 100K for a coordinator is a soft target where appropriate, not a mandatory allocation to every request.

For the separate Cortex companion, use its [03.54 release and setup documentation](https://github.com/swpegasus/Pegasus-Cortex/releases/tag/v2.2026.11.22.03.54.A). Configure connections and permissions explicitly. Building Cerebral does not enroll a machine, approve tools, install a service or grant full control.

Retain the root LICENSE and the applicable third-party notices when distributing your work.
