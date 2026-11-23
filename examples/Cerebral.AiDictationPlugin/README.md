# Pegasus AI Dictation

Pegasus AI Dictation **1.2.8** (`pegasus.ai-dictation`) transcribes microphone audio locally with NVIDIA NeMo-Speech.cpp and a selected speech model. It provides completed phrases after pauses, one-off **Record / Stop / Play / Insert** review, and optional live words using a separate streaming model. Dictation inserts editable text; it never submits chat messages.

## Requirements and installation

Use a compatible Windows x64 Cerebral host, a microphone accessible to desktop applications, .NET 10, the package's [native helper](../Cerebral.AiDictation.NativeHost/README.md), and the external CPU runtime/model described below. Recognition does not require a Chat agent, GPU slot or cloud speech account. Optional grammar correction is a separate model-backed feature.

In Configuration, open **Plugins & tools > Desktop plugins**, choose **Install…** and select the built `desktop-plugin.json`. Select **Pegasus AI Dictation**, then **Manage > Enable > Go** if needed. The manifest requests first-install enablement; existing catalog settings still apply. The package shares the exclusive `dictation.input` group with [Windows Dictation](../Cerebral.DictationPlugin/README.md), with higher declared priority. Check the catalog's actual active provider rather than expecting both to mount together.

Select **Manage > Configure… > Go** for **AI Dictation**, or use the microphone control's settings action when supported by the host. See [PLUGINS](../../docs/PLUGINS.md) for catalog/package updates.

## Obtain the runtime and model

The GitHub source tree contains no runtime binaries or model weights. Download external assets only from their official publisher, retain their notices, and use directories you control outside immutable plugin generations.

| Component | Source and exact artifact |
|---|---|
| CPU runtime | [NVIDIA NeMo-Speech.cpp v0.1.0](https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.1.0), asset [nemo-speech-0.1.0-windows-x86_64-cpu.zip](https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.1.0/nemo-speech-0.1.0-windows-x86_64-cpu.zip) |
| Default model | [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3), revision `541d1f99c6b0c3cd0b11a95167540bb8edefd82b`, file [parakeet-tdt-0.6b-v3.q8_0.gguf](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3/resolve/541d1f99c6b0c3cd0b11a95167540bb8edefd82b/parakeet-tdt-0.6b-v3.q8_0.gguf) |

The default GGUF is **713,975,456 bytes**, SHA-256:

~~~text
e3880d0aaaaf2c308ea2c35016b2b895c423eb3fda924c1b463d1c19b7f4d32e
~~~

Verify a downloaded file with PowerShell `Get-FileHash -Algorithm SHA256 -LiteralPath <downloaded-file>`; replace the placeholder with your chosen path. The runtime [SOURCE.json](Licensing/NeMo-Speech.cpp-0.1.0/SOURCE.json) records its exact release asset and expected archive size; do not invent a checksum that is not recorded there.

Extract the complete runtime archive, retaining `bin/nemo-speech.exe`, `nemo_speech_asr_c.dll` and all companion DLLs. Select that executable and the GGUF in AI Dictation settings. The executable's directory supplies the native DLL search location. Microsoft Visual C++ components have separate prerequisites/redistribution terms; the NeMo Apache license does not cover them.

Alternatively, the host's configured **ToolsDirectory** and **ModelsDirectory** support this layout:

~~~text
<ToolsDirectory>/AI-Dictation/NeMo-Speech/bin/nemo-speech.exe
<ToolsDirectory>/AI-Dictation/NeMo-Speech/bin/nemo_speech_asr_c.dll
<ToolsDirectory>/AI-Dictation/NeMo-Speech/bin/<companion DLLs>
<ModelsDirectory>/AI-Dictation/parakeet-tdt-0.6b-v3.q8_0.gguf
~~~

Use **Find installed runtime and model**, review the resolved fields, and **Save preferences**. These are host-configured resource roots, not instructions to add files inside an installed catalog generation. The resource manifest maps prepared package resources to those roots; a source build alone does not populate them.

**Show live words while speaking** is off by default. It requires the separately obtained Nemotron English streaming artifact at `<ModelsDirectory>/AI-Dictation/Nemotron-English/nemotron-speech-streaming-en-0.6b.q8_0.gguf`. That optional asset is not part of this source distribution or the Parakeet license grant. The helper checks its exact size/hash; install it only with its own applicable terms and matching host/package support. Without it, leave live words off and use offline phrase/one-off recognition.

## Set up the microphone

In AI Dictation settings:

1. Choose **Microphone input** or **Windows default input**, then **Test microphone**. **Refresh inputs** discovers currently available devices.
2. Set software boost only if needed. The default is **0 dB**; the control allows 0–24 dB. **Automatically normalize quiet recordings** defaults on, using a **5-second** calibration sample. Hardware gain remains a Windows/driver setting.
3. Choose the runtime/model paths and retain accurate model ID, revision, source and license fields.
4. **Save preferences** applies edits. **Load saved preferences** reloads central saved values; **Prepare existing local preferences** previews legacy local settings without silently applying them.

Default Auto and live words are off, quiet-time detection is **1.5 seconds**, controls are docked, external Windows destinations are off, and grammar correction is off. Saved Auto is not permission to activate the microphone on launch.

## Dictate and review

Use **Start/Stop** in the compact microphone control for the Main composer, or **AI Dictate** beside a supported question-answer field. **Auto** explicitly arms phrase-mode capture for the selected destination. It keeps the offline model warm while completed phrases are transcribed in order.

Open the floating controls from the microphone/menu action, or **Ctrl+Alt+D** when that shortcut can be registered. Opening controls does not start capture. One-off **Record**, **Stop**, **Play** and **Insert** let you inspect a recording/transcript before insertion.

Start pins the destination and cursor. Confirmed insertions advance that cursor; user edits, cursor changes, changed chat/field identity or unavailable targets stop further writes. If insertion is unconfirmed, inspect the destination before recovery—do not assume retry is safe. In the controls' options you can opt into eligible Windows text destinations, docking, or grammar correction with an exact existing local `dictation.grammar` helper profile. Unsupported grammar correction preserves the recognized words and reports the limitation.

Capture is bounded to two minutes. Phrase mode limits pending audio and stops when it cannot safely keep up. Interrupted run audio and one-off recordings can remain for **Play** until discard/shutdown; successfully applied automatic recordings and temporary phrase files are removed. Cleanup failures are reported. Treat the plugin-owned capture directory as sensitive local audio storage.

## Build and troubleshoot

From the repository root on Windows:

~~~powershell
dotnet build examples/Cerebral.AiDictationPlugin/Cerebral.AiDictationPlugin.csproj -c Release
~~~

The `net10.0-windows` project references current Contracts/Core/Desktop.Plugins/Speech sources and the x64 NativeHost project. It copies the private helper into `NativeSpeech/`, plus manifests and licensing material. This project does not define an alternate binary-SDK selector. Keep the complete package and its resource declarations; see [BUILD](../../docs/BUILD.md).

A missing-helper error needs a coherent package repair/update. Missing-native-SDK errors require the complete matching runtime, not just `nemo-speech.exe`. A zero meter needs microphone/device/privacy investigation before raising gain. Missing model or unavailable live words is not fixed by loading a chat model. Source-preview build instructions do not establish microphone, native-runtime or model execution on your machine.

## License and source notes

First-party code uses the [Pegasus Community License 1.0](../../LICENSE). Preserve [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), the complete [NeMo license/vendor tree](Licensing/NeMo-Speech.cpp-0.1.0/LICENSE), and Parakeet's [CC BY 4.0 license](Licensing/Parakeet-TDT-0.6b-v3/LICENSE.txt), [attribution](Licensing/Parakeet-TDT-0.6b-v3/ATTRIBUTION.txt) and [model card](Licensing/Parakeet-TDT-0.6b-v3/MODEL-CARD.md). External model and Microsoft runtime rights remain separate.

[AiDictationSettings.cs](AiDictationSettings.cs) defines defaults/resource lookup; [AiDictationPlugin.cs](AiDictationPlugin.cs) defines settings and capture setup; [NativeSpeechProvider.cs](NativeSpeechProvider.cs) defines helper/runtime admission; [DictationReviewView.cs](DictationReviewView.cs) defines review/options.
