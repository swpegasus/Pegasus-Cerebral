---
name: Diagnose bugs
description: Investigate a specific failure with bounded evidence, a useful reproduction and proportionate verification.
author: Matt Pocock; adapted for Pegasus Cerebral
license: MIT
origin: https://github.com/mattpocock/skills/blob/abeeaf95660ba9e8b7c753eb38f441ca707a8823/skills/engineering/diagnosing-bugs/SKILL.md
assets: LICENSE.txt
---

# Diagnose bugs

Identify the owner's exact symptom, affected path, current version and evidence level. Read relevant source and configuration before forming a conclusion. Protect unrelated work and redact secrets from every diagnostic.

Choose the smallest authorized check that can distinguish this failure from a nearby failure. Prefer an existing focused fixture or typed inspection. For performance, establish a bounded baseline and measure the suspected path. If execution or builds are held, continue source review and clearly label the reproduction unexecuted.

Minimize inputs and dependencies without removing the actual trigger. State falsifiable hypotheses and test the one whose result best separates them. Keep temporary instrumentation bounded and task-owned; remove it after use.

Use Cerebral's current typed tools, grants and admitted paths. Available tools do not authorize extra access. Never replay a business effect, Unknown operation, receipt, workflow or model request merely to reproduce a symptom. Inspect the original outcome through its supported identity instead.

When implementation is authorized, fix the cause and verify the real failure path at an appropriate interface. Report source review, deterministic fixtures, integration, renderer, model and installed behavior at their actual evidence levels. A test that cannot reproduce the reported pattern is a limitation, not proof that the bug is fixed.

Conclude with the supported cause or narrowed uncertainty, what changed, the decisive evidence, and remaining limits. Do not claim runtime success from documentation or source alone.

Adapted from Matt Pocock's skills for Pegasus Cerebral. Upstream revision abeeaf95660ba9e8b7c753eb38f441ca707a8823. See LICENSE.txt for the complete MIT notice. This adaptation is not an upstream endorsement.
