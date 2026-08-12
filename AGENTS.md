# Repository Instructions

- Prefer the simplest solution that fully addresses the problem. Avoid unnecessary complexity or machinery.
- Keep fixes strictly scoped to the requested failure. Do not add unrelated gates, policies, validation requirements, workflow inputs, refactors, or hardening unless the user explicitly requests them or they are strictly necessary to correct that failure.
- Do not write unit tests that check for specific versions of SDKs, runtimes, compilers, dependencies, or tools. Keep version pins in configuration and test behavior or consistency with that configuration instead.
