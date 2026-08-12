# Repository Instructions

- Prefer the simplest solution that fully addresses the problem. Avoid unnecessary complexity or machinery.
- Keep fixes strictly scoped to the requested failure. Do not add unrelated gates, policies, validation requirements, workflow inputs, refactors, or hardening unless the user explicitly requests them or they are strictly necessary to correct that failure.
- Do not invent release policies or duplicate an existing release contract across scripts, validators, workflows, and tests. Keep one authoritative implementation for each rule and remove redundant enforcement instead of creating parallel checks that can drift.
- Test observable behavior and supported data contracts. Do not write tests that merely require implementation text, property names, command fragments, or other literals to appear in a script or workflow.
- When an actual release schema or producer/consumer contract must change, trace every producer, consumer, validator, and test that references it, update the complete dependency chain, and run the directly affected tests before publishing the change.
- Treat the full six-target release workflow as an expensive final integration check whose runs take roughly one to two hours. Do not use it to discover deterministic failures that can be reproduced through local inspection or focused tests.
- Before recommending another full release run, reproduce the current failure when possible and pass the smallest set of fast checks that directly covers every changed component. Do not run unrelated long test suites.
- Investigate a directly related failure chain end to end and repair it coherently in one change, but do not expand that work into speculative hardening or adjacent policy changes.
- Keep release iterations focused on meaningful compiler, SDK, platform, packaging, and Vendor integration progress. Prefer deleting redundant machinery and reducing moving parts over adding new process around the build.
- Do not write unit tests that check for specific versions of SDKs, runtimes, compilers, dependencies, or tools. Keep version pins in configuration and test behavior or consistency with that configuration instead.
