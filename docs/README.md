# Cerebral documentation

Cerebral is a Windows workspace for conversation, models, project context and bounded agent work. This documentation describes the published **0.2.22 source preview** and its implemented plugin source. It does not imply that every component has a packaged download or has been qualified together.

## Start here

| Guide | What it covers |
|---|---|
| [Build from source](BUILD.md) | Windows/.NET prerequisites, project order, Books exclusion and strict plugin SDK references |
| [First setup](SETUP.md) | A separate source-preview data folder, local model startup and Configuration navigation |
| [Models and capacity](MODELS.md) | Model/runtime downloads, exact file layout, context and inference concurrency |
| [Use the workspace](USAGE.md) | Main, Coordinator, Workers, chats, projects, composer, activity and settings |
| [Plugin directory and installation](PLUGINS.md) | All 22 implemented plugin families and the different desktop/service/provider lifecycles |
| [Data and permissions](DATA-AND-PERMISSIONS.md) | Storage, references, tool permissions, external providers and uncertain results |
| [Troubleshooting](TROUBLESHOOTING.md) | Build, startup, chat readiness, plugins, model capacity and slow generation |
| [Licensing and redistribution](LICENSING.md) | First-party terms, third-party notices, models, runtimes and public packaging |
| [Release notes](../RELEASE-NOTES.md) | What is shipped and what remains unverified |

**Main** is the human conversation. A **Coordinator** plans and delegates requested work; **worker agents** carry out assignments. The **Worker process** is the host that owns services, model execution and storage on a machine. A logical worker agent is not another Windows process or automatically another GPU inference slot.

## Plugin developers

Each package's README identifies its actual project, version, SDK properties, payload and limitations. Read that guide before attempting a build: some examples deliberately require a particular admitted SDK and will reject an incompatible reference.

The [plugin directory](PLUGINS.md) also links the shared [desktop/provider definitions](../examples/Shared/README.md), [hosted provider protocol](../examples/Cerebral.HostedProviderProtocol/README.md) and [AI Dictation native helper](../examples/Cerebral.AiDictation.NativeHost/README.md). These are supporting source, not independent installable plugins.

## Evidence and availability

The documentation was checked against this public source, manifests and project files. It is not a new clean-checkout build, hardware fit test or end-to-end qualification of all plugins. Follow the package-specific limitations and keep personal settings, credentials and application data out of source control.

[Project homepage](https://cerebral.shaynewinter.com/) · [Source repository](https://github.com/swpegasus/Pegasus-Cerebral) · [Preview downloads](https://github.com/swpegasus/Pegasus-Cerebral/releases/tag/v0.2.22-preview.20261122)
