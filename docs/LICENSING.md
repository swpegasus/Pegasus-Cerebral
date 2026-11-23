# Licensing and redistribution

This page is a navigation aid to the included terms, not a replacement for them. Read the [Pegasus Community License 1.0](../LICENSE) and the applicable third-party licenses before use or redistribution.

## First-party Cerebral and plugins

Identified first-party Cerebral and included plugin material is provided under the **Pegasus Community License 1.0**, copyright Shayne Winter.

The license permits genuine personal use and eligible organizations below **US$10 million in combined group revenue and receipts**, subject to its conditions. Use for or on behalf of a group at or above that threshold requires a separate written agreement signed by Shayne Winter before that use. Consult the full license for definitions, aggregation, redistribution, notices and other conditions.

This is a community/source-available license; do not relabel it as MIT, Apache-2.0 or an OSI-approved open-source license. Third-party portions retain their own licenses.

Credits: created by **Shayne Winter**, with development assistance from **OpenAI Codex and Pegasus Cerebral**.

## Third-party components

The root [third-party notice index](../THIRD-PARTY-NOTICES.md) links the included notices. Preserve them with corresponding components. NuGet dependencies, icon/art assets, imported skills, browser dependencies and native runtimes can each impose separate terms.

Important examples:

- [AI Dictation notices](../examples/Cerebral.AiDictationPlugin/THIRD-PARTY-NOTICES.md) distinguish the NeMo-Speech.cpp runtime, Parakeet model and dependencies. A model notice does not mean the weights are bundled.
- [Windows Dictation notices](../examples/Cerebral.DictationPlugin/THIRD-PARTY-NOTICES.md) retain the System.Speech terms.
- [Browser notices](../examples/Cerebral.BrowserPlugin/THIRD-PARTY-NOTICES.md) distinguish architectural references, dependencies and separately supplied browser binaries.
- [Skills notices](../examples/Cerebral.SkillsPlugin/Skills/THIRD-PARTY-NOTICES.md) retain the upstream licenses for imported skill material.
- [Chat icon licensing](../assets/icons/chat-v1/LICENSE.txt) and the adjacent upstream notices govern those assets.
- The [character-library guide](../examples/Cerebral.PersonalityPlugin/docs/final-space-character-library.md) explains that original communication-style summaries do not grant rights to third-party characters, voices, subtitles or media.

## Models and runtime downloads

Models may be distributed when their exact license allows it and the distribution satisfies that license's conditions. There is no universal ban on including a model, and there is no universal permission either.

This source preview deliberately excludes weights and native binaries. The [model guide](MODELS.md) points to the baseline chat-model/projector pages and runtime releases; the [AI Dictation guide](../examples/Cerebral.AiDictationPlugin/README.md) gives its separate speech model acquisition and resource layout. Obtain the exact license/model card for the revision you distribute, include required notices and honor any additional restrictions.

A hosted-provider plugin grants no account entitlement or permission to redistribute a provider's model.

## Preparing a distribution

1. Include the first-party license and copyright/credit notices.
2. Inventory the actual shipped dependencies/assets/models, not only source references.
3. Preserve each required third-party license, notice, attribution and source obligation.
4. Include baseline defaults and clear acquisition/layout instructions for anything omitted.
5. Exclude personal data, private configuration, credentials, private keys and enrollment/session material.
6. Include only explicitly required **public** signing-verification material when distributing signed artifacts; do not ship private signing keys or unrelated trust certificates.
7. State preview limitations and what was actually built/tested.

Books is excluded from this public release. No internal/private plugin, undeveloped stub or absent media/model asset becomes public merely because another package references it.

[Project website](https://cerebral.shaynewinter.com/) · [Public source](https://github.com/swpegasus/Pegasus-Cerebral)
