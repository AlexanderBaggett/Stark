# Phase 23 - libLLVM Integration

Status: WIP, decision locked.

The self-hosted compiler uses **libLLVM as the primary backend integration**.
Textual LLVM remains a debug and artifact-inspection output, never a compiler
input.

## 1. Locked Decision

The blessed backend shape is:

1. Stark compiler builds LLVM modules through the LLVM C API.
2. libLLVM verifies modules, runs the selected target/pass pipeline, and emits
   object files in-process.
3. Textual LLVM can still be emitted for debugging, golden inspection, diagnostics,
   and stage comparison.
4. Shelling out to `clang`/`lld` remains for final platform linking, native C
   source compilation, SDK/toolchain discovery, and emergency diagnostics.

Do not bind LLVM's C++ API. The C API is the stable FFI boundary.

## 2. Why This Shape

Stark has recently gained the core FFI pieces that make libLLVM plausible:

- explicit `ffi(c)` ABI spelling and ABI-bearing function pointer types,
- target-mapped `System.C` primitive aliases and `c_void`,
- C-compatible aggregate layout and alignment control,
- explicit null-terminated C string design under `System.C`.

Using libLLVM as the only production backend path gives the self-hosted compiler
a faster normal backend, no large text round trips, direct verifier/diagnostic
access, and a cleaner long-term path to target-machine configuration. Keeping
textual LLVM as an artifact preserves inspectability without making `.ll` an
input to compilation.

## 3. Reasonable Defaults

| Area | Default |
|---|---|
| API surface | Bind only the LLVM C API. |
| Versioning | Pin one LLVM major/minor per Stark release. |
| Library form | Prefer bundled dynamic libLLVM for release archives; static linking is a later packaging decision. |
| Normal output | Emit object files in-process through `LLVMTargetMachineEmitToMemoryBuffer` or equivalent C API. |
| Inspection output | Ask libLLVM to print the in-memory module as optional textual LLVM. |
| Link step | Continue using linker tools through the toolchain resolver. |
| Error handling | Convert LLVM failures into ordinary Stark `Result<T, LlvmError>` values. |
| Resource ownership | Every owning LLVM handle has a Stark wrapper with `drop`. |
| Raw access | Raw LLVM refs stay internal to `System.Llvm` / compiler backend wrappers. |
| Binding source | Generate or curate a small checked binding slice; do not hand-write the entire LLVM header surface. |

## 4. Verified FFI Requirements

The following are actually needed for libLLVM, beyond the already-landed ABI,
C primitive alias, and struct-layout work.

| Requirement | Why libLLVM needs it | Roadmap owner |
|---|---|---|
| Implement `System.C` C strings | LLVM C APIs take `const char*` names/triples/features and return owned diagnostic/error strings. | Doc `18` |
| Raw pointer-to-pointer / out-pointer calling patterns | LLVM APIs return values through `T*`/`char**` out parameters, especially error messages and target lookup. | Doc `23` binding layer task |
| Distinct opaque handle wrappers | LLVM uses many incompatible opaque refs that are all pointer-shaped in C. Stark needs `ContextRef`, `ModuleRef`, `BuilderRef`, `TargetMachineRef`, etc. to avoid passing the wrong handle class. | Doc `23` |
| Deterministic `drop`/dispose wrappers | LLVM resources use create/dispose ownership pairs. The binding layer must release contexts, modules, builders, memory buffers, messages, target machines, and pass managers. | Doc `23` |
| Native dynamic library discovery/loading policy | The self-hosted compiler must locate bundled or overridden libLLVM for the selected target/platform. | Toolchain packaging roadmap |
| Native link metadata for libLLVM | The compiler package/build must know which LLVM library and platform system libraries are required. | Toolchain packaging roadmap |
| C enum/flag constant representation | LLVM C APIs use integer enums and bit flags for codegen level, relocation model, code model, verifier actions, and target options. | Doc `23` |

No additional FFI syntax is currently required beyond these items. If an LLVM C API
requires a shape not listed here, add it to this table before expanding the FFI
roadmap.

## 5. Binding Architecture

Add a narrow `System.Llvm` or compiler-internal `Llvm.Native` binding layer. It
should expose typed wrappers over opaque LLVM refs rather than raw pointers.

Example shape:

```stark
module System.Llvm

public struct Context
{
    internal rawmutptr<System.C.c_void> Ref;

    static fn System.Result<Context, LlvmError> Create();

    drop
    {
        // LLVMContextDispose(Ref)
    }
}

public struct Module
{
    internal rawmutptr<System.C.c_void> Ref;

    static fn System.Result<Module, LlvmError> Create(
        borrow Context context,
        System.C.CStr name);

    drop
    {
        // LLVMDisposeModule(Ref)
    }
}
```

Rules:

- LLVM refs are not interchangeable even when they lower to the same raw pointer
  shape.
- Owning wrappers are not implicitly copyable.
- Borrowed wrappers must not outlive the owner that created them.
- `drop` calls the corresponding LLVM dispose function exactly once.
- LLVM-owned strings are converted into Stark text and disposed with the matching
  LLVM message-dispose API.

## 6. Backend Implementation Strategy

### Stage 1 - Direct LLVM Module Construction

The initial libLLVM implementation constructs LLVM modules directly through the
C API. It may cover only a narrow codegen subset at first, but it must not parse
textual LLVM as a bootstrap bridge.

1. Map Stark/ABI types directly to LLVM types.
2. Create functions, globals, blocks, and instructions through the C API.
3. Preserve existing SSA/ABI/lowering validation before LLVM construction.
4. Verify the module through libLLVM.
5. Emit object bytes in-process.
6. Continue offering textual LLVM by asking libLLVM to print the module when an
   inspection artifact is requested.

This proves library discovery, C strings, message ownership, typed opaque
handles, target machine setup, verification, object emission, and diagnostics on
the real architecture immediately.

### Stage 2 - Pass Pipeline And Target Tuning

Add target/pass configuration through the C API:

1. optimization level,
2. relocation model,
3. code model,
4. target CPU/features,
5. verifier policy,
6. object emission options.

## 7. Testing Strategy

- Keep textual LLVM golden tests, but classify them as inspection-artifact tests
  generated from the in-memory LLVM module.
- Add libLLVM smoke tests for context/module creation, direct module construction,
  verifier failure reporting, and object emission.
- Compare object/link/runtime behavior against the current host compiler while the
  host exists.
- Add failure tests for missing libLLVM, wrong LLVM version, unsupported target,
  verifier errors, and owned-message disposal.
- Add package/release tests that compile a program using only bundled libLLVM and
  the bundled/linker toolchain.

## 8. Work Items

### Required FFI Work

- [~] Implement `System.C` C-string helpers from doc `18`, including owned C
      strings, borrowed bounded views, mutable buffers, and foreign-owned string
      wrapper examples. Core `System.C` helpers, `%s` validation, and generic
      foreign-owned copy/dispose helpers have landed; LLVM binding-specific
      owner types remain with the binding layer.
- [x] Implement the LLVM FFI binding support needed by the initial backend
      integration:
      out-pointer usage patterns, `char**` error outputs, opaque-ref outputs,
      typed opaque-handle wrappers, deterministic dispose/drop wrappers for
      owning LLVM resources, C enum/bitflag constants Stark passes, and direct
      function-body construction primitives.

### Backend Work

- [~] Add a narrow `System.Llvm` or compiler-internal LLVM C API binding layer,
      including libLLVM discovery through the toolchain resolver/release layout
      and LLVM version diagnostics. The compiler-internal binding surface,
      required-symbol table, version-check helpers, typed target-machine and
      target-data setup, direct object-emission wrappers, reusable
      function-parameter buffers, module print/verify wrappers, and LLVM-owned
      diagnostic copying into `LlvmResult<T>` have landed; resolver loading
      remains with the backend/toolchain work.
- [~] Add the direct LLVM C API backend path: module construction, object
      emission, verifier/error handling, and optional module printing for
      textual LLVM debug/inspection artifacts.
  - [x] Add typed C API wrappers for target lookup, target-machine creation,
        module target/data-layout, reusable function declarations, object buffer
        emission, verifier diagnostics, module printing, basic blocks, builder
        positioning, integer constants, return terminators, global declarations,
        global-object facts, memory/call construction, ABI/performance fact
        attachments, function parameters, control flow, scalar integer ops,
        compares, selects, and PHI incoming edges.
  - [x] Add libLLVM-linked runtime smoke coverage for direct module
        construction, verifier diagnostics, module printing, and object
        emission.
  - [ ] Wire MIR/SSA backend construction through those wrappers.
- [ ] Keep final linking/toolchain execution through the native toolchain resolver.
- [ ] Add the tests listed in §7 for bindings, diagnostics, direct object
      emission, inspection output, bundled toolchain behavior, and failure
      cases.

## 9. Documentation Work

- [ ] Update user-facing build/toolchain docs for bundled libLLVM and override
      paths.
- [ ] Update compiler-internals docs to describe libLLVM as the primary backend
      and textual LLVM as an inspection artifact.
- [ ] Update FFI docs only for the verified C-string/out-pointer/opaque-handle
      patterns that land.
