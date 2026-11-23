# Pegasus Cerebral

**A Windows workspace for AI research and development.**

Cerebral brings conversation, planning, agent execution, models, context and tools into a modular desktop environment. Use it to explore an idea, work through a technical problem, or develop a project with selected references and bounded agent assistance.

**Development preview: `0.2.22-preview.20261122`.** This release publishes a source baseline for developers and early experimenters. It is a work in progress; a clean-checkout build of this exact snapshot has not been verified.

[Product website](https://cerebral.shaynewinter.com/) · [Build guide](docs/BUILD.md) · [Release notes](RELEASE-NOTES.md)

## One workspace, distinct roles

- **Main** is the human-facing conversation. Talk through an idea, ask a question or request work.
- **Coordinator** plans and delegates work when the task calls for it, then brings results back.
- **Worker agents** carry out bounded assignments. They are distinct from the **Worker process**, which owns runtime services and execution on a machine.

A conversation need not become a tool operation. Selected tools, profile permissions and approvals determine what execution is available; an agent's description of an action is not evidence that it happened.

## Connect the pieces you need

Cerebral is designed around replaceable components: local model backends, desktop and service plugins, context providers, workflow definitions and tools. Keep a small active surface for a focused experiment, or combine components for a larger project.

For example, you might discuss a home-office plan in Main, attach selected measurements and notes, then ask a coordinator to break a requested check into smaller assignments. Review returned results alongside the conversation.

[Pegasus Cortex](https://github.com/swpegasus/Pegasus-Cortex/releases/tag/v2.2026.11.22.03.54.A) is a separate companion for selected context, durable records, tasks and tool connections. Configure credentials, model locations and external connections yourself. Nothing in this source preview supplies access to someone else's accounts or data.

## What this release contains

This is a **source-only development preview**, with baseline defaults and the included first-party plugin source. Books is excluded. Model weights, native runtime binaries, installers, private development history, personal configuration and security keys are not part of this source release.

The build guide separates source compilation from model/runtime setup. Plugin availability and compatibility still need to be checked for the particular build and host.

## Hardware and context

Model choice, quantization, backend, context length and concurrency all affect capacity. A CUDA-capable GPU is optional; GPU acceleration needs a compatible backend and driver.

A system with 16 GB of GPU memory and at least 32 GB of system RAM is a starter target for capacity planning, not a promise of a particular slot count or context size. Use hardware-aware capacity checks and adjust to the work. A roughly 100K coordinator context is a soft planning target where it fits; it is not a minimum for Main or every worker. This preview makes no eight-slot × 100K guarantee on a 32 GB system.

## Preview boundaries

Workflows and integrations remain experimental. About and navigation changes have isolated development-build and fixture evidence; that does not establish an installed update or a clean-checkout pass for this complete snapshot. This source release does not provide a supported Cerebral installer. See the release notes before building.

## License and credits

Created by **Shayne Winter**, with development assistance from **OpenAI Codex and Pegasus Cerebral**.

First-party material identified under the **Pegasus Community License 1.0** is governed by the included [LICENSE](LICENSE). Genuine personal use and eligible organizations below US$10 million in combined group revenue and receipts qualify for community permission. Use for or on behalf of a group at or above the threshold requires a separate written agreement signed by Shayne Winter before that use.

Third-party libraries, models, runtimes, artwork and other separately licensed components retain their own terms and notices. The project license does not automatically apply to your data or outputs.
