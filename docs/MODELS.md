# Models, runtimes and inference capacity

The public source contains **no model weights or native inference binaries**. Download those separately under their publishers' licenses, then register their paths in Cerebral. A model is not licensed by Cerebral merely because Cerebral can load it.

## Baseline chat model

The source [starter-model.json](../setup/starter-model.json) describes **Qwen3.5-9B Q5_K_M GGUF**. The model's publisher pages identify the applicable Apache-2.0 license:

- [Qwen3.5-9B-Q5_K_M.gguf](https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/blob/main/Qwen3.5-9B-Q5_K_M.gguf) — chat model.
- [mmproj-BF16.gguf](https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/blob/main/mmproj-BF16.gguf) — matching model-family vision projector, needed only for a compatible vision setup.

Use files from a matching publisher revision and verify their published hashes. A moving `main` download is not evidence that an older pinned acquisition checksum still matches. Keep the publisher's model card/license with any permitted redistributed weights.

A sample layout, chosen by you:

```text
C:\AI\Models\Qwen3.5-9B\
    Qwen3.5-9B-Q5_K_M.gguf
    mmproj-BF16.gguf
C:\AI\Runtimes\llama\
    llama-server.exe
    llama-fit-params.exe       (when supplied by the chosen runtime)
    ...matching runtime DLLs...
```

These are example user-controlled folders, not hard-coded installation requirements.

## Native chat runtime

Obtain a compatible Windows build from [llama.cpp releases](https://github.com/ggml-org/llama.cpp/releases). Choose the CPU or GPU backend appropriate for the machine and keep that build's DLLs together. CUDA acceleration also requires an appropriate NVIDIA driver; the C# source alone does not supply one.

Cerebral's current runtime integration uses fit-related arguments including `--fit on`, `--fit-target` and `--fit-ctx`. An arbitrary older `llama-server` can therefore be incompatible even if it loads GGUF elsewhere. Use a runtime that accepts the actual launch arguments and model architecture.

When present beside the server, `llama-fit-params.exe` supports native fit estimation. Without it, the source has a conservative GGUF estimate path. A vision projector adds memory that a standalone text-only estimate may not fully account for; actual server loading and observed reserves remain important.

In Configuration, review **Advanced → Folders & runtime** for runtime paths and **Compute & agents → Models** for model registration. Registration and loading are separate operations. The source startup in [First setup](SETUP.md) can perform initial registration and loading explicitly.

## Context, slots and queues are different limits

- **Context tokens** cover the input/history, instructions, references and response allowance for a request.
- **Response tokens** cap generated output; they are not tokens per second.
- **Inference slots / parallel requests** describe backend request capacity for the model.
- **Host job limits and queues** can restrict admitted concurrent work independently of backend slots.
- **Logical worker agents** can wait on dependencies or tools and need not all be generating at once.

Increasing the configured slot count does not prove that simultaneous requests are being dispatched. Increasing context for every slot can consume enough GPU/system memory to slow generation or prevent loading. Splitting layers or cache across GPU and RAM has a different performance profile from a fully GPU-resident configuration.

The starter profile uses automatic sizing and **one parallel request**; the starter metadata's recommended context is **16,384**. These are baseline defaults, not a copy of an individual developer's tuning.

A 16 GB GPU and at least 32 GB RAM is a capacity-planning target, not a guarantee of eight large contexts. A roughly 100K Coordinator context is a **soft target** where it fits, not a hard minimum. Main can often use a smaller conversation window, while a technical Coordinator or complex worker may benefit from more context. There is no automatic rule that a Coordinator receives the sum of every worker's context.

## Tune in small steps

1. Select the exact machine/model in Configuration.
2. Review **Details / more… → Runtime and chat settings** and the role profiles that use it.
3. Start from a configuration that loads successfully with reserve memory.
4. Change one important capacity dimension at a time, then inspect actual resource and generation metrics.
5. Increase concurrency only when multiple requests are ready and memory permits. Keep accepted or uncertain work intact when changing routes.

A successful idle load is not a throughput benchmark or proof that eight full windows can run concurrently. Similarly, a displayed `out tok/s` is a generation rate, not the response budget. See [Troubleshooting](TROUBLESHOOTING.md) for slow-generation diagnosis.

## Other model types

AI Dictation uses a separate speech runtime and speech model; it does not use the chat GGUF. Follow the [AI Dictation acquisition/layout guide](../examples/Cerebral.AiDictationPlugin/README.md). Windows Dictation uses a different Windows speech integration.

OpenAI/Anthropic provider source is an explicit developer integration with account, transport and credential-resolution prerequisites; see the [plugin directory](PLUGINS.md). It is not enabled by downloading the local GGUF.
