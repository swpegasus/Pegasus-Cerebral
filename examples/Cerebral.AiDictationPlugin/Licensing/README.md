# AI Dictation license material

This directory preserves notices for the selected external speech runtime and model. The source preview does not include those binaries/weights. Follow the [AI Dictation acquisition guide](../README.md) and [package notice](../THIRD-PARTY-NOTICES.md).

## Runtime

[NeMo-Speech.cpp-0.1.0](NeMo-Speech.cpp-0.1.0/) contains the supplied [LICENSE](NeMo-Speech.cpp-0.1.0/LICENSE), [NOTICE](NeMo-Speech.cpp-0.1.0/NOTICE), [upstream third-party notice](NeMo-Speech.cpp-0.1.0/THIRD_PARTY_NOTICES.md), acquisition [SOURCE.json](NeMo-Speech.cpp-0.1.0/SOURCE.json) and available vendor license files.

The upstream notice is preserved **unchanged**. Its relative links describe paths in NVIDIA's complete source/submodule tree, including optional features. Some therefore do not resolve inside this smaller notice-only source distribution. They are not claims that those optional source trees or binaries are bundled here. Use the linked upstream repositories and the exact runtime archive's complete `share/licenses/nemo-speech/` tree for the components actually redistributed.

Do not remove a notice because a feature is optional, or assume the runtime's Apache license covers Microsoft runtime DLLs. Preserve the selected archive's corresponding notices and satisfy the applicable Microsoft redistribution terms.

## Speech model

[Parakeet-TDT-0.6b-v3](Parakeet-TDT-0.6b-v3/) contains the [CC BY 4.0 license](Parakeet-TDT-0.6b-v3/LICENSE.txt), [attribution](Parakeet-TDT-0.6b-v3/ATTRIBUTION.txt), [model card](Parakeet-TDT-0.6b-v3/MODEL-CARD.md) and exact source metadata. These terms are independent of Cerebral's first-party license.

This set does not clear the optional Nemotron streaming model or arbitrary replacement assets for redistribution. Acquire their own model cards, licenses and provenance before packaging them.
