---
name: Review a change
description: Review an authorized change for requested behavior, correctness, project conventions and material risk.
author: Matt Pocock; adapted for Pegasus Cerebral
license: MIT
origin: https://github.com/mattpocock/skills/blob/abeeaf95660ba9e8b7c753eb38f441ca707a8823/skills/engineering/code-review/SKILL.md
assets: LICENSE.txt
---

# Review a change

Establish the actual comparison and requested behavior from current evidence. Preserve dirty working trees and unrelated edits. Use the existing source or review route; do not invent a Git baseline when the workspace has none.

Assess two perspectives: whether the change implements the request, and whether it is correct and maintainable under the project's conventions. Check the trigger, before/after behavior, scope, input validation, authorization, cancellation, bounded resources, cleanup, compatibility and uncertain outcomes.

Read surrounding callers and tests before assigning a defect. Report actionable findings with an exact file and narrow line reference, the condition that triggers the issue, and its consequence. Separate established defects from suggestions and unverified boundaries. If no spec or runtime evidence exists, say so.

Use existing agent permissions and source ownership. Review work does not authorize implementation, tests, production probes, commits, PR publication or messages to others. Delegate only if current task instructions authorize it; parallel reviewer agents are optional, not a dependency of this skill.

Reuse existing verification and respect build/model/runtime holds. Mocked or deterministic fixtures establish fixture behavior only. Prioritize material issues that affect the requested outcome, without broad unrelated refactoring or stylistic churn.

Deliver a concise assessment with the most consequential findings first, supporting evidence, and any limit that changes how the result can be used.

Adapted from Matt Pocock's skills for Pegasus Cerebral. Upstream revision abeeaf95660ba9e8b7c753eb38f441ca707a8823. See LICENSE.txt for the complete MIT notice. This adaptation is not an upstream endorsement.
