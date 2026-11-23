---
name: Design maintainable code
description: Design cohesive modules with small stable interfaces, explicit failure behavior and useful test boundaries.
author: Matt Pocock; adapted for Pegasus Cerebral
license: MIT
origin: https://github.com/mattpocock/skills/blob/abeeaf95660ba9e8b7c753eb38f441ca707a8823/skills/engineering/codebase-design/SKILL.md
assets: LICENSE.txt
---

# Design maintainable code

Inspect the actual callers, invariants and failure paths. Put related behavior behind a small interface so callers learn less and changes remain local.

Include ordering, cancellation, resource bounds, cleanup, compatibility and uncertain outcomes in the contract. Return useful results and diagnostics. Accept existing dependencies through established composition points; introduce an abstraction only for a real variation or substantial shared behavior.

Ask what complexity would return to callers if this module disappeared. Reuse helpers that express the same policy. Keep unrelated concerns separate and avoid pass-through layers, speculative options and near-duplicate implementations.

For Cerebral, preserve Main/coordinator/Worker responsibilities and use existing SDK contributions, typed owner commands and allowed tool routes. A plugin's files and instruction text do not expand agent authority. Review a public SDK or shared contract change with its owner before implementation.

Choose interfaces that allow meaningful behavior verification and honest failure handling. Preserve receipt identity and uncertain outcomes rather than hiding them behind a successful-looking adapter. Keep cleanup deterministic; do not infer that requesting unload proves resource termination.

Present the proposed behavior, narrow source ownership, important tradeoffs and verification limits in plain language. Exercise the authorized verification only when its execution gate is open.

Adapted from Matt Pocock's skills for Pegasus Cerebral. Upstream revision abeeaf95660ba9e8b7c753eb38f441ca707a8823. See LICENSE.txt for the complete MIT notice. This adaptation is not an upstream endorsement.
