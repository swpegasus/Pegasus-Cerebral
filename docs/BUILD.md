# Build Cerebral from source

This is a **Windows source development preview**, not a supported installer. The published snapshot has not had a complete clean-checkout build/test pass. These instructions describe the intended source entry points and identify inputs that are not included.

## Prerequisites

- Windows x64 for Main, Configuration and desktop plugins, which use WPF.
- Git, with a normal user account and a writable checkout.
- The [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). [global.json](../global.json) selects **10.0.401** with `latestPatch` roll-forward: use a compatible **10.0.4xx** SDK patch, not an arbitrary .NET 10 feature band. The Windows SDK distribution includes the matching desktop runtime.
- [PowerShell 7](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows) for repository scripts.
- NuGet access for declared dependencies, or a correctly populated local package cache.

Model weights and native inference/speech/browser runtimes are separate downloads. They are not required simply to inspect the C# source, but native-resource and package projects can require their declared assets. See [model setup](MODELS.md) and the individual [plugin guides](PLUGINS.md).

## Get the public source

```powershell
git clone https://github.com/swpegasus/Pegasus-Cerebral.git
Set-Location Pegasus-Cerebral
dotnet --info
```

Use current `main` for documentation updates. The tag `v0.2.22-preview.20261122` and its original source archive preserve the initial source snapshot; a later documentation commit does not rewrite that tag or archive.

## Build the three applications

Build Worker first, then Configuration and Main with the same configuration. Run these commands from the repository root:

```powershell
dotnet restore .\src\Cerebral.Worker\Cerebral.Worker.csproj
dotnet build .\src\Cerebral.Worker\Cerebral.Worker.csproj -c Release --no-restore -p:CerebralIncludeBooks=false

dotnet restore .\src\Cerebral.Integrator\Cerebral.Integrator.csproj
dotnet build .\src\Cerebral.Integrator\Cerebral.Integrator.csproj -c Release --no-restore -p:CerebralIncludeBooks=false

dotnet restore .\src\Cerebral.Orchestrator\Cerebral.Orchestrator.csproj
dotnet build .\src\Cerebral.Orchestrator\Cerebral.Orchestrator.csproj -c Release --no-restore -p:CerebralIncludeBooks=false
```

| Application | Source project | Usual Release output |
|---|---|---|
| Worker host | `src/Cerebral.Worker/Cerebral.Worker.csproj` | `src/Cerebral.Worker/bin/Release/net10.0/` |
| Configuration | `src/Cerebral.Integrator/Cerebral.Integrator.csproj` | `src/Cerebral.Integrator/bin/Release/net10.0-windows/` |
| Main | `src/Cerebral.Orchestrator/Cerebral.Orchestrator.csproj` | `src/Cerebral.Orchestrator/bin/Release/net10.0-windows/` |

Main's source launcher locates the sibling built applications. Build all three before following [First setup](SETUP.md). Use the explicit `--source-local` launch there; a build output path alone does not select an isolated data root.

These commands are intended build instructions, not a claim that they passed against a fresh machine in this release. If a required SDK, package resource or generated input is missing, diagnose that specific input rather than removing its validation.

## Books exclusion and repository scripts

Books source and the Books plugin are intentionally not published. [Directory.Build.props](../Directory.Build.props) and the build script support `CerebralIncludeBooks=false`. The source defaults to no Books when both Books source projects are absent; a partial Books tree is refused.

The broader repository build entry point is:

```powershell
pwsh -File .\scripts\build.ps1 -IncludeBooks false
```

That script performs a broader pipeline than the three application builds. Review its parameters and prerequisites first. Publishing output also needs the declared runtime/resources. **The installer path explicitly refuses a no-Books build in this snapshot.** This preview does not contain a supported public Cerebral installer, and these instructions do not bypass that restriction.

## Build plugins against the intended SDK

Read the package-specific README in the [plugin directory](PLUGINS.md). There is no single build command that works for every example:

- Some projects reference desktop SDK projects; others require exact DLL paths or an SDK directory.
- Some package projects enforce hash/version profiles. A new local SDK can correctly be rejected by an installed-SDK profile.
- Paired desktop/service packages can have different versions and separate build outputs.
- AI Dictation's native helper and speech resources have additional acquisition and licensing steps.
- The hosted-provider examples need explicit developer integration; merely compiling them does not configure a provider account.

Supply the exact declared reference properties. Do not remove SDK hashes, compatibility checks or manifest requirements to make a build pass. Do not copy DLLs into a running installed application's directory.

## Verification and contributions

Run focused checks for the component you change. Tests using fake transports, synthetic instructions or controlled WPF hosts establish fixture behavior, not a real model/provider/hardware pass. The test programs expose different selectors; inspect their `Program.cs` before choosing one.

Keep generated `bin/`, `obj/`, private data, model weights, native runtimes, credentials and signing material out of commits. Review [data boundaries](DATA-AND-PERMISSIONS.md) and [licensing](LICENSING.md) before sharing artifacts.
