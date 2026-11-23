---
name: Write useful agent guidance
description: Write concise instruction documents with clear triggers, progressive disclosure and checkable outcomes.
author: Matt Pocock; adapted for Pegasus Cerebral
license: MIT
origin: https://github.com/mattpocock/skills/blob/abeeaf95660ba9e8b7c753eb38f441ca707a8823/skills/productivity/writing-for-agents/SKILL.md
assets: LICENSE.txt
---

# Write useful agent guidance

Start with the behavior the document should produce. Give it a short, specific description that identifies when it is useful. Keep metadata small so discovering the document does not load its whole body.

Put common steps first and disclose specialized reference only when needed. A reference should explain both what it contains and the condition for reading it. Keep one authoritative statement of a rule, and place its caveats next to it.

Use concrete verbs, existing project vocabulary and checkable completion criteria. Remove repetition and stale restatements of facts that are cheap to inspect. Keep rare branches out of the main path without hiding a required safety or authorization condition.

For Cerebral, skills are passive instruction content. Describe the existing roles, tools and permission routes accurately. Do not turn examples, links, assets or frontmatter into executable commands, automatic delegation, new grants or mandatory external actions. Retain higher-level owner and project instructions.

Use the supported bounded manifest and scalar frontmatter format. Validate descriptions, IDs, versions, references and declared assets before publication. Attribute third-party material and retain its license when adapting it.

Review the document against a concrete intended use. Report only verification actually performed, and leave broader improvements for a separately authorized scope.

Adapted from Matt Pocock's skills for Pegasus Cerebral. Upstream revision abeeaf95660ba9e8b7c753eb38f441ca707a8823. See LICENSE.txt for the complete MIT notice. This adaptation is not an upstream endorsement.
