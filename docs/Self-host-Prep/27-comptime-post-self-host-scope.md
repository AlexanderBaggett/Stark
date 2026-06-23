# Comptime Post-Self-Host Scope

Status: deferred until after bootstrap.

This document holds the broad `comptime` work that is intentionally not a
self-hosting blocker. It remains a promising language direction, but it should
be implemented in the Stark compiler architecture rather than completed as a
large C#-host-only feature.

## Decision

Post-self-host `comptime` is where Stark may pursue the broader model:

- CTFE as a deterministic subset of ordinary Stark
- broad compile-time branching over explicit program-structure facts
- a stable `System.Compiler`-style structural fact surface
- cross-package preservation for compile-time helper bodies
- parity across type checking, MIR/lowering, imported-template evaluation, and
  backend erasure

This work resumes after the Stark compiler can build itself, unless a concrete
pre-self-host compiler-port blocker is discovered.

Broad CTFE may support value kinds beyond the current range-typed integer
`comptime` generic slice after bootstrap, but only demand-driven. Additional
value kinds must be deterministic, cheap to compare/hash for specialization
identity, representable in package images, and useful to real compiler, stdlib,
or vendor-library code. Do not default to "any Stark value can be a generic
argument." Likely early candidates are `bool`, enum/tag-like constants, and
possibly small text/name constants when package identity remains clean.

## Deferred Work Items

- [ ] Rebuild the broad CTFE evaluator in the self-hosted compiler architecture
      instead of trying to directly port the C# evaluator shape.
- [ ] Define the stable public shape of compile-time structural facts, including
      whether `System.Compiler` remains compiler-known only or becomes a
      documented compile-time-only stdlib facade.
- [ ] Complete evaluator parity for the broad CTFE subset across type checking,
      MIR/lowering, imported-template lowering, diagnostics, and backend
      erasure.
- [ ] Complete package-image and source-bridge preservation for broad CTFE
      helper bodies and structural fact expressions.
- [ ] Finish the remaining structural fact coverage only after concrete
      self-hosted compiler code asks for those facts.
- [ ] Add post-bootstrap CTFE value kinds beyond range-typed integer generics
      only when concrete compiler, stdlib, or vendor-library code needs them.
- [ ] Add broad conformance tests that run against the self-hosted compiler, not
      only the C# host implementation.

## Guardrails

- Do not add runtime reflection.
- Do not introduce hidden allocation, hidden dispatch, or hidden metadata
  objects.
- Do not use `comptime` to replace ordinary runtime compiler work such as file
  I/O, package loading, or process execution.
- Do not treat every useful structural fact as a bootstrap blocker.
- Do not allow heap objects, process/file/environment-dependent values, runtime
  reflection objects, hidden metadata, or hidden allocation into CTFE generic
  identity.
- Prefer ordinary Stark compiler architecture first; add compile-time machinery
  only where it makes invalid states unrepresentable or removes real generated
  runtime work.

## Relationship To Pre-Self-Host Scope

The pre-self-host baseline is defined in
[26-comptime-pre-self-host-scope.md](26-comptime-pre-self-host-scope.md). That
baseline stays maintained, but broad expansion waits until after bootstrap.
