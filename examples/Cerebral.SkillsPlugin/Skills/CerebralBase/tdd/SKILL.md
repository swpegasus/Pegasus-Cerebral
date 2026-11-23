---
name: Test-driven changes
description: Use focused behavior tests for consequential changes when test execution is authorized.
author: Matt Pocock; adapted for Pegasus Cerebral
license: MIT
origin: https://github.com/mattpocock/skills/blob/abeeaf95660ba9e8b7c753eb38f441ca707a8823/skills/engineering/tdd/SKILL.md
assets: LICENSE.txt
---

# Test-driven changes

Use tests where they protect consequential behavior. Read the project's testing instructions and reuse an existing public or domain interface that reaches the real failure pattern.

Choose a small behavior slice and an independent expected result. A useful test can fail on the bug, survives an implementation refactor, and exposes an important contract. Avoid assertions that merely repeat the implementation or replace a real integration claim with a stub.

When authorized, observe the focused test fail for the intended reason, implement the smallest coherent change, then observe it pass. Keep the fixture deterministic and its temporary files confined. Reuse the completed evidence; broaden checks only for a new failure or unresolved shared risk.

Cerebral build, runtime, model, service, network and installed-state holds remain binding. Do not execute tests while held or start extra agents by following this document. Prepare source or a verification proposal and label it unrun until the owning route is released.

Use current project vocabulary and existing abstractions. Do not add a new dependency, provider, service, schema or tool permission merely to create a test interface. Review maintainability and cleanup after behavior passes. Report fixture results separately from real provider, renderer, network, model and installed acceptance.

Adapted from Matt Pocock's skills for Pegasus Cerebral. Upstream revision abeeaf95660ba9e8b7c753eb38f441ca707a8823. See LICENSE.txt for the complete MIT notice. This adaptation is not an upstream endorsement.
