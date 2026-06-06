# Stark FFI Syntax Design Checklist

Design syntax for each of the following C interop features. Each item is a reminder to settle on concrete syntax that fits Stark's conventions (Allman braces, 4-space indent, PascalCase types/functions, camelCase locals).

## 1. Bitfield Support
- [x] Syntax for declaring bit widths on struct fields (model C `struct { int a : 3; int b : 5; }`)
- [x] Decide how bit layout interacts with sized integer / range primitives
- [x] Specify bit ordering / packing rules (LSB-first vs MSB-first; platform behavior)
- [x] Consider expressing bit layout via a contract rather than masking

## 2. Struct Alignment Control
- [x] `repr(C)`-equivalent for guaranteed field order + C alignment/padding - specified as `[StructLayout(C)]` in [`17-ffi-struct-layout.md`](./17-ffi-struct-layout.md)
- [x] Explicit alignment override (`align(N)`-style) - specified as `Align(N)` in [`17-ffi-struct-layout.md`](./17-ffi-struct-layout.md)
- [x] Packed layout (alignment 1 / no padding) - specified as `Pack(1)` in [`17-ffi-struct-layout.md`](./17-ffi-struct-layout.md)
- [x] Decide attribute vs. keyword vs. modifier syntax (fit existing modifier style) - attributes locked in [`17-ffi-struct-layout.md`](./17-ffi-struct-layout.md)

## 3. ABI / Calling Convention Specifiers
- [x] Syntax to tag extern functions with an ABI (C, stdcall, fastcall, sysv, win64, aapcs, ...) - specified in [`15-ffi-abi.md`](./15-ffi-abi.md)
- [x] Carry calling convention in function pointer types (extend existing fnptr forms) - specified in [`15-ffi-abi.md`](./15-ffi-abi.md)
- [x] Default ABI behavior when unspecified - specified in [`15-ffi-abi.md`](./15-ffi-abi.md)
- [x] Platform-conditional ABI selection - specified in [`15-ffi-abi.md`](./15-ffi-abi.md)

## 4. C Primitive Type Aliases
- [x] Platform-mapped aliases: c_char, c_short, c_int, c_long, c_longlong - specified in [`16-ffi-c-types.md`](./16-ffi-c-types.md)
- [x] Unsigned variants: c_uchar, c_ushort, c_uint, c_ulong, c_ulonglong - specified in [`16-ffi-c-types.md`](./16-ffi-c-types.md)
- [x] c_size_t, c_ptrdiff_t, c_void - specified in [`16-ffi-c-types.md`](./16-ffi-c-types.md)
- [x] Signedness of c_char (platform-dependent) - specified in [`16-ffi-c-types.md`](./16-ffi-c-types.md)
- [x] Map aliases onto Stark's sized primitives per target - specified in [`16-ffi-c-types.md`](./16-ffi-c-types.md)

## 5. Null-Terminated String Interop
- [x] Type for C `char*` / null-terminated strings (distinct from Stark's length-prefixed strings) - specified in [`18-ffi-c-strings.md`](./18-ffi-c-strings.md)
- [x] Conversion: Stark string -> C string (allocation + null termination) - specified in [`18-ffi-c-strings.md`](./18-ffi-c-strings.md)
- [x] Conversion: C string -> Stark string (length scan; validity) - specified in [`18-ffi-c-strings.md`](./18-ffi-c-strings.md)
- [x] Ownership semantics on conversion (who frees - tie into ownership/borrow model) - specified in [`18-ffi-c-strings.md`](./18-ffi-c-strings.md)
- [x] Borrowed vs. owned C-string distinction at the type level - specified in [`18-ffi-c-strings.md`](./18-ffi-c-strings.md)

## 6. libLLVM Binding Patterns
- [x] Decide primary LLVM integration direction - libLLVM through the LLVM C API, specified in [`23-libllvm-integration.md`](./23-libllvm-integration.md)
- [ ] Implement C string helpers required by LLVM `const char*` and owned error-message APIs - tracked in [`18-ffi-c-strings.md`](./18-ffi-c-strings.md) and [`23-libllvm-integration.md`](./23-libllvm-integration.md)
- [ ] Exercise raw out-pointer patterns such as opaque-ref outputs and `char**` error outputs in the binding layer - tracked in [`23-libllvm-integration.md`](./23-libllvm-integration.md)
- [ ] Use distinct Stark opaque-handle wrapper types for LLVM refs - tracked in [`23-libllvm-integration.md`](./23-libllvm-integration.md)
- [ ] Add deterministic `drop`/dispose wrappers for owned foreign resources - tracked in [`23-libllvm-integration.md`](./23-libllvm-integration.md)
- [ ] Document the C enum/bitflag constant pattern used by LLVM bindings - tracked in [`23-libllvm-integration.md`](./23-libllvm-integration.md)

---

## Cross-cutting notes
- [ ] Keep all syntax consistent with Allman braces, 4-space indent, PascalCase/camelCase
- [ ] Decide attribute-style vs. modifier-style uniformly across these features
- [ ] Document each in the stdlib FFI section and the Agent Skills SKILL.md
