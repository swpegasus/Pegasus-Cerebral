# Character-inspired and practical style library

The Personality package contains original communication-style summaries, short descriptions and original demonstration lines. These are text styles for an assistant, not character biographies, episode transcripts, actor voices or speech-generation models.

Use Main's **Personality** selector to choose a style for one chat. **Preview context** reads the exact saved public definition. The shared **Personality** configuration editor can also use a template to fill a custom draft; **Save** is explicit. See the [plugin setup guide](../README.md).

## Definitions in this source

| Style | Definition revision | Intended expression |
|---|---:|---|
| HUE | 2 | Measured ship-AI precision, dry humor, patient skepticism and quiet loyalty; detailed when useful. |
| KVN | 2 | Exuberant confidence, comic self-importance, eager belonging and genuine useful insight. |
| Gary | 2 | Impulsive bravado, absurd comparisons and warm vulnerability, grounded by loyalty and candid help. |
| Gary 2.0 | 2 | A deliberately steadier Gary-inspired assistant adaptation, with disciplined reasoning and fewer tangents. |
| Avocato | 1 | Guarded warmth, tactical clarity, dry humor and concrete protective help. |
| Quinn | 1 | Focused competence, evidence-led skepticism, independence and earned warmth. |
| Lord Commander | 1 | Icy theatrical strategy directed at the problem, without threats or humiliation. |
| Tribore | 1 | Flamboyant confidence, theatrical self-questioning and unexpected resourcefulness; conventional spelling. |
| AVA | 1 | Cool, incisive confidence and acerbic observations with practical purpose. |
| Pragmatic | 1 | Practical recommendations, concrete tradeoffs and proportionate next steps. |
| Analytical | 1 | Evidence, assumptions, alternatives and calibrated uncertainty. |
| Teacher / Coach | 1 | Patient explanations, worked examples and constructive coaching. |

The service also provides **Cerebral** and **Succinct** built-ins: 14 packaged choices in total. Packaged choices do not consume the eight current custom-definition slots. The source's `PersonalityTemplates.LibraryVersion` is a library identifier, separate from each definition revision and the desktop/service package versions.

## Style and identity

Character-inspired styles borrow broad fictional communication traits. They do not claim that the assistant lived the character's biography or that the human's relationships belong to the assistant. Accuracy, current user instructions, required formats and permissions remain unchanged. Humor and theatrical delivery should yield to seriousness and the user's corrections.

The short “Demonstration” lines are original examples of intended expression, not quotations from episodes or fabricated earlier chat messages. Gary 2.0 is an assistant adaptation, not a claim that the series contains a separate character with that name. Tribore's cadence is expressed through wording and rhythm; no vocal imitation or lisp synthesis is provided.

The optional **A\*\*** modifier adds constructive candid disagreement. It does not require profanity or personal abuse, and it cannot make optional style context mandatory.

## Exact saved revisions and preview

`PersonalityTemplates.All` exposes the twelve current templates. `RevisionOne` retains the original seven definitions, and `Exact(id, revision, sha256)` looks up a matching current or retained definition. Service built-ins are separate from those template arrays.

A saved chat keeps its exact base revision/hash. Revising the packaged library does not silently rewrite that chat, and an accepted request retains its captured style. Explicitly selecting a current offered row changes future turns. A mode-only change can preserve that chat's already retained base under the same saved-state checks.

Preview resolves the saved definition and, when enabled, its exact candid modifier. It does not substitute the latest same-name template. It shows original style wording as optional context, not a delivery receipt from a model. Private source bodies are unavailable through this preview; an unknown source is diagnosed without inventing replacement text. **No personality** disables both base and modifier for future turns.

## Size, persistence and model behavior

The service limits each base to **120 whitespace-separated words / 1,024 UTF-8 bytes**, with a separate **2,048-byte serialized provider-response bound**. These limits are not tokenizer measurements, sampling controls or response-length caps.

Descriptions are short UI explanations; they are not additional authority. Actual wording, rendering and selection behavior are defined by [PersonalityTemplates.cs](../PersonalityTemplates.cs), [PersonalityPlugin.Composer.cs](../PersonalityPlugin.Composer.cs) and [the service contract](../Service/PersonalityContract.cs). Saved choice and model application are different: the workflow and context budget may omit optional personality content. No promise of model adherence or voice likeness follows from selecting a style.

## Rights and attribution

The original Cerebral code and style-summary wording are distributed under the [Pegasus Community License 1.0](../../../LICENSE). Final Space and its characters, names, performances, artwork, episodes and subtitles belong to their respective rights holders. Their mention identifies an inspiration and does not imply endorsement or grant rights to redistribute those third-party works.

This directory supplies no subtitle corpus, episode media, character artwork, actor recordings or voice-model weights. Obtain any separate media/model only under its own applicable terms. Preserve third-party notices when distributing external assets.
