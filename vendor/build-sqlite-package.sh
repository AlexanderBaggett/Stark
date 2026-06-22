#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
vendor_dist="${script_dir}/dist"
compiler_path="${STARK_COMPILER:-${repo_root}/stark}"

if [[ -x "${compiler_path}" ]]; then
  compiler_cmd=("${compiler_path}")
else
  compiler_cmd=(dotnet run --project "${repo_root}/src/compiler.csproj" --)
fi

cd "${repo_root}"
mkdir -p "${vendor_dist}"

native_args=(
  --native-source "${script_dir}/SQLiteTextBinding.c"
)

if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists sqlite3; then
  native_args+=(--native-pkg-config sqlite3)
else
  if [[ -z "${SQLITE_INCLUDE_DIR:-}" || -z "${SQLITE_LIBRARY_DIR:-}" ]]; then
    echo "SQLite is not visible to pkg-config on this machine." >&2
    echo "Either install sqlite3.pc and set PKG_CONFIG_PATH, or set SQLITE_INCLUDE_DIR and SQLITE_LIBRARY_DIR." >&2
    echo "Example: SQLITE_INCLUDE_DIR=/usr/include SQLITE_LIBRARY_DIR=/usr/lib bash vendor/build-sqlite-package.sh" >&2
    exit 1
  fi

  if [[ ! -f "${SQLITE_INCLUDE_DIR}/sqlite3.h" ]]; then
    echo "SQLITE_INCLUDE_DIR does not look like a SQLite include directory because sqlite3.h was not found." >&2
    exit 1
  fi

  native_args+=(
    --native-include-dir "${SQLITE_INCLUDE_DIR}"
    --native-library-dir "${SQLITE_LIBRARY_DIR}"
    --native-library sqlite3
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/SQLite.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${vendor_dist}/libVendorSQLite.a" \
  "${native_args[@]}"

echo "Built ${vendor_dist}/libVendorSQLite.a"
echo "Built ${vendor_dist}/libVendorSQLite.starkpkg"
