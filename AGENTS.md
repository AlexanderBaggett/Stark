# Repository Instructions

- Prefer the simplest solution that fully addresses the problem. Avoid unnecessary complexity or machinery.
- Keep fixes strictly scoped to the requested failure. Do not add unrelated gates, policies, validation requirements, workflow inputs, refactors, or hardening unless the user explicitly requests them or they are strictly necessary to correct that failure.
- Do not invent release policies or duplicate an existing release contract across scripts, validators, workflows, and tests. Keep one authoritative implementation for each rule and remove redundant enforcement instead of creating parallel checks that can drift.
- Test observable behavior and supported data contracts. Do not write tests that merely require implementation text, property names, command fragments, or other literals to appear in a script or workflow.
- When an actual release schema or producer/consumer contract must change, trace every producer, consumer, validator, and test that references it, update the complete dependency chain, and run the directly affected tests before publishing the change.
- Do not write unit tests that check for specific versions of SDKs, runtimes, compilers, dependencies, or tools. Keep version pins in configuration and test behavior or consistency with that configuration instead.
