# Pegasus AI Dictation

AI Dictation supplies local speech input through a native helper and a desktop plugin. Its source is included in this development preview; native runtime binaries, speech models, recordings, recovery audio and personal settings are not included.

## Source components

- `Cerebral.AiDictationPlugin` owns the WPF interface, text-entry lifetime and explicit recording/recovery controls.
- `Cerebral.AiDictation.NativeHost` owns native speech processing in a separate process.
- `examples/Shared` supplies shared text-entry and plugin-host helpers.

The implementation distinguishes completed-phrase dictation from optional native live-word recognition. A compatible host and the corresponding runtime/model assets are required. Opening a panel must not start recording, and dictation does not itself submit a chat message.

Settings and audio belong to the running user's plugin data directory, not this repository. Uncertain insertion and recovery audio require explicit review or discard; source publication does not establish microphone, installed-host or sustained recognition acceptance.

## Models, runtimes and licenses

Speech-recognition models are separate from the conversational GGUF model. This source snapshot includes notices and provenance for the supported speech assets, not their executable or weight payloads:

- [NeMo-Speech.cpp 0.1.0 license and notices](Licensing/NeMo-Speech.cpp-0.1.0/)
- [Parakeet TDT 0.6b v3 license, attribution and model card](Licensing/Parakeet-TDT-0.6b-v3/)
- [Plugin third-party notices](THIRD-PARTY-NOTICES.md)

Obtain compatible assets from the sources identified in those notices and follow their own license terms. Other optional models have separate admission and license requirements; they are not downloaded or bundled by this source release. Do not replace existing model or recovery directories as a shortcut to setup.

## Build and integration

See the root [build guide](../../docs/BUILD.md). The native-host project and shared sources must remain beside this plugin. Some host SDK bindings are intentionally strict and require a compatible built SDK; do not relax their hash or capability checks to make a build pass.

This preview has no clean-checkout or full plugin/runtime qualification claim. First-party source is governed by the root [Pegasus Community License 1.0](../../LICENSE); third-party terms remain independent.
