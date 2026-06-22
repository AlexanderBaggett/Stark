# Vendor Library

`Vendor` is Stark's bundled vendor-library root for bindings to established
native libraries. It is separate from `System` so the standard library can stay
small and portable while common native bindings are still available without a
package manager.

## Raylib

Use the bundled Raylib 6.0 binding with:

```stark
import Vendor.Raylib
```

The binding is split into `Vendor.Raylib.Core`, `Vendor.Raylib.Shapes`,
`Vendor.Raylib.Textures`, `Vendor.Raylib.Text`, `Vendor.Raylib.Models`,
`Vendor.Raylib.Audio`, and `Vendor.Raylib.Types`. The root `Vendor.Raylib`
module re-exports those modules.

The public Stark API keeps unsafe FFI contained inside the binding modules.
Pure Raylib symbol aliases use `[LinkName("RaylibSymbol")]` directly, so they
do not pay for or depend on a C rename shim. By-value C-layout aggregates such
as `Vector2`, `Vector3`, `Vector4`, `Rectangle`, and `Color` bind directly
through Stark's C ABI aggregate carrier lowering. Platform C types stay
header-shaped at the FFI boundary; for example `GetFileModTime` returns
`System.C.c_long` internally and widens to the public Stark `i64` wrapper.

Build the package image with:

```bash
bash vendor/build-raylib-package.sh
```

The script uses `pkg-config raylib` when available. If raylib is not visible to
`pkg-config`, set `RAYLIB_SRC_DIR` to a local Raylib `src` directory:

```bash
RAYLIB_SRC_DIR=/path/to/raylib/src bash vendor/build-raylib-package.sh
```

## SQLite

Use the bundled SQLite binding with:

```stark
import Vendor.SQLite
```

The binding exposes safe Stark handles for `Database` and `Statement`, status
and result enums, `Open*` helpers, `Execute`, prepared-statement
`Prepare`/`Step`/`Finalize`, integer/double/null/text binding, and result-column
readers. Raw SQLite handles stay internal. Text entering SQLite is copied with
SQLite's transient lifetime contract; text leaving SQLite is copied into
`System.Text.OwnedAscii`.

Build the package image with:

```bash
bash vendor/build-sqlite-package.sh
```

The script uses `pkg-config sqlite3` when available. If sqlite3 is not visible
to `pkg-config`, set `SQLITE_INCLUDE_DIR` and `SQLITE_LIBRARY_DIR`:

```bash
SQLITE_INCLUDE_DIR=/usr/include SQLITE_LIBRARY_DIR=/usr/lib bash vendor/build-sqlite-package.sh
```
