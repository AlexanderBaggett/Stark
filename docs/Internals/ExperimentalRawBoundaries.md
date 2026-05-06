# Experimental Standard Library Raw Boundaries

Experimental standard-library modules should prefer Stark-owned safe contracts:
slices, `dynamic T`, `init`/`out`, owned text, and runtime buffers.

The canonical stable and experimental boundary inventory lives in
`docs/Internals/StandardLibraryRawPointerBoundaries.md`. This file summarizes
the experimental promotion rule.

Raw pointers remain legitimate in these places:

- `System.Runtime`, `System.Runtime.Platform.*`, syscall shims, OS handles,
  thread handles, socket handles, file handles, and allocator internals.
- Small internal handoff regions that convert a safe slice or text view into
  the platform ABI shape required by runtime calls.
- `System.Experimental.Text` low-level caller-buffer formatting and conversion
  helpers that intentionally interoperate with `Ascii`, `Unicode`, UTF-16
  buffers, and compiler-known text view data functions.

Higher-level experimental modules must not expose raw pointer storage in public
APIs. `System.Experimental.IO.File`, `System.Experimental.FileSystem`,
`System.Experimental.Console`, and `System.Experimental.Net.Tcp` keep raw
handles and raw pointer conversions internal; their public APIs use status
results, text views/owners, byte slices, or runtime buffers.
