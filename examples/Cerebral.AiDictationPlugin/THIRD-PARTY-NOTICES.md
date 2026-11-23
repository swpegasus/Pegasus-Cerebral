# Third-party notices: AI Dictation

This file covers the listed third-party speech assets and dependencies. First-party Cerebral code is licensed separately. A Cerebral license does not replace the licenses below.

## NVIDIA NeMo-Speech.cpp 0.1.0 CPU runtime

- Source release: https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.1.0
- Selected archive: `nemo-speech-0.1.0-windows-x86_64-cpu.zip`.
- First-party upstream runtime license: Apache-2.0.
- Preserve the complete [Licensing/NeMo-Speech.cpp-0.1.0](Licensing/NeMo-Speech.cpp-0.1.0) subtree: LICENSE, NOTICE, THIRD_PARTY_NOTICES.md and the supplied vendor license files. These files are copied unchanged from the selected publisher runtime.
- The runtime's own `share/licenses/nemo-speech/` tree must also accompany any redistributed runtime resource. See SOURCE.json for the selected public source/archive identity.

The upstream notice describes multiple optional features. It does not establish that every optional dependency is included in this CPU package. A change of runtime archive or features requires its corresponding complete notices and dependency review.

### Microsoft Visual C++ runtime

The native Windows payload includes Microsoft Visual C++ runtime DLLs. They retain separate Microsoft redistribution terms and are not licensed by NeMo's Apache license. The installation kit declares the Microsoft Visual C++ x64 redistributable prerequisite. Distribution of copied Microsoft DLLs still requires the applicable Microsoft redistribution rights and terms:
https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution

## NVIDIA Parakeet TDT 0.6b v3

- Publisher and attribution: NVIDIA.
- Model: `nvidia/parakeet-tdt-0.6b-v3`.
- Revision: `541d1f99c6b0c3cd0b11a95167540bb8edefd82b`.
- Selected artifact: `parakeet-tdt-0.6b-v3.q8_0.gguf`; publisher GGUF used unchanged.
- License: CC BY 4.0, https://creativecommons.org/licenses/by/4.0/.
- Original source: https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3

Full license, original attribution/model card and exact source/modification metadata are in [Licensing/Parakeet-TDT-0.6b-v3](Licensing/Parakeet-TDT-0.6b-v3). Keep these with redistributed model resources and do not impose a first-party license restriction that contradicts the model license. No NVIDIA endorsement is implied.

## Asset boundaries

Ordinary plugin output carries these notices, not speech weights or native runtime binaries. The separate resource-packaging path includes the selected runtime and Parakeet model only when that feature is packaged/selected; their own notices must travel with them.

The optional Nemotron English streaming model is not included or cleared for redistribution by this notice set. It is not covered by Parakeet's CC BY license. Custom or additional models/runtimes require their own provenance and license material.
