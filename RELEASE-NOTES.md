# Release notes

## 0.2.22-preview.20261122 — source development preview

This release shares the Cerebral source baseline so developers can inspect, build and explore the Windows AI workspace while development continues.

### Areas to explore

- Main conversation, coordinator planning and bounded worker-agent assignments.
- Local model configuration, context contributions, workflow definitions and selected tools.
- Desktop and service plugin integration.
- Chat navigation, project organization, permissions and runtime status surfaces.
- Shared product information, credits and licensing in About.

These are development surfaces, not a claim that every combination of model, workflow, plugin and machine has passed end-to-end qualification.

### Contents

Source and baseline defaults only. Books, model weights, native runtime binaries, installers, private development history, personal configuration and security keys are excluded. Included third-party notices remain applicable.

### Known limitations

- A clean-checkout build and complete test pass of this exact source snapshot have **not been verified**.
- Recent About/navigation work has isolated build/fixture evidence. That evidence does not prove installation or qualification of the complete public snapshot.
- This release is not a supported Cerebral installer or an automatic upgrade of an existing installation.
- Workflows, delegation and plugin compatibility remain experimental. Preserve uncertain operation outcomes; do not assume that repeating a request is harmless.
- Actual model context and parallel capacity depend on model format, quantization, backend and available GPU/system memory. A 100K coordinator context is a soft target. Eight simultaneous 100K contexts on a 32 GB system are not guaranteed.

See [docs/BUILD.md](docs/BUILD.md) for prerequisites and the intended source build entry points.

### License

Pegasus Community License 1.0 for identified first-party material; independent third-party licenses remain intact. Created by Shayne Winter with development assistance from OpenAI Codex and Pegasus Cerebral.
