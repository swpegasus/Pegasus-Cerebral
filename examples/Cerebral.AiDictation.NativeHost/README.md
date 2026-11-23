# AI Dictation native speech helper

`Cerebral.AiDictation.NativeHost` is the private **Windows x64** console helper used by [AI Dictation](../Cerebral.AiDictationPlugin/README.md). It isolates the native NeMo-Speech C API and its OpenMP lifetime from the Main desktop process.

This project is not a separately installable plugin: it has no catalog manifest, desktop controls, listening network socket or Windows service. The project inherits the product version from `Directory.Build.props`.

## Build and package

From the repository root, using the SDK selected by `global.json`:

~~~powershell
dotnet build examples/Cerebral.AiDictation.NativeHost/Cerebral.AiDictation.NativeHost.csproj -c Release
~~~

It targets `net10.0`, sets `PlatformTarget=x64`, and builds an apphost. The AI Dictation plugin references this project and copies these four files into its own `NativeSpeech/` directory:

- `Cerebral.AiDictation.NativeHost.exe`
- `Cerebral.AiDictation.NativeHost.dll`
- `Cerebral.AiDictation.NativeHost.runtimeconfig.json`
- `Cerebral.AiDictation.NativeHost.deps.json`

Building [the parent plugin](../Cerebral.AiDictationPlugin/README.md#build) normally builds/copies the helper too. Do not install it through Desktop plugins or move native runtime DLLs into the Main application directory. Use the host package workflow described in [PLUGINS](../../docs/PLUGINS.md).

The helper is framework-dependent: the matching .NET runtime must be available. Native speech DLLs and model weights are separate external assets; building this project does not download or redistribute them.

## Runtime contract

The parent plugin starts the helper with a runtime-directory path, model path, `offline` or `streaming`, and a bounded silence interval. These are implementation arguments, not an interactive transcription CLI.

Communication uses a version-1 length-prefixed protocol on private standard input/output. Operations are `begin`, `audio`, `finish`, and acknowledged `close`; request numbers must increase. Audio is mono 16 kHz PCM16, with a two-minute recording bound. The helper confines CPU affinity to at most two available logical processors and uses Below Normal process priority. Native calls remain on one synchronous owner thread.

The streaming mode additionally requires the exact compiled model size/hash. A different model renamed to the expected filename is not accepted. Closing the process releases native libraries; unexpected EOF or an unacknowledged close is a failure, not proof of completed transcription. Lost input must not be replayed automatically.

## Troubleshooting and privacy

“The private CPU speech helper is missing” means the parent package lacks its `NativeSpeech` payload. Repair/update the entire plugin package rather than copying an unrelated helper executable. A native runtime error can also mean missing NeMo companion DLLs, an incompatible CPU runtime, a missing model, or separate Microsoft Visual C++ runtime prerequisites. Use the parent settings diagnostics.

Audio and transcripts pass through this local child process. It has no Cerebral conversation or submission authority and no network transport of its own. The parent plugin owns microphone capture, temporary recordings, review, insertion and cleanup. A successful compile does not prove native-library, microphone or model compatibility.

## License and source notes

First-party helper code is covered by the [Pegasus Community License 1.0](../../LICENSE). Preserve the parent plugin's [third-party notices](../Cerebral.AiDictationPlugin/THIRD-PARTY-NOTICES.md) when distributing runtime assets; Apache licensing of NeMo does not replace Microsoft or model terms.

[Program.cs](Program.cs), [DictationNativeProtocol.cs](../Cerebral.AiDictationPlugin/DictationNativeProtocol.cs), and [NemoNativeAsr.cs](../Cerebral.AiDictationPlugin/NemoNativeAsr.cs) define the process/protocol/native boundary.
