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

detect_default_target_triple() {
  local doctor_output
  local doctor_triple

  if doctor_output="$("${compiler_cmd[@]}" doctor 2>/dev/null)"; then
    doctor_triple="$(printf '%s\n' "${doctor_output}" | sed -n 's/^  triple: //p' | head -n 1)"
    if [[ -n "${doctor_triple}" ]]; then
      printf '%s\n' "${doctor_triple}"
      return
    fi
  fi

  if command -v clang >/dev/null 2>&1; then
    clang -dumpmachine
    return
  fi

  local machine
  local system
  local os
  machine="$(uname -m)"
  system="$(uname -s)"

  case "${system}" in
    Linux)
      os="pc-linux-gnu"
      ;;
    Darwin)
      os="apple-darwin"
      ;;
    MINGW*|MSYS*|CYGWIN*)
      os="pc-windows-msvc"
      ;;
    *)
      echo "Unable to infer a target triple for ${system}. Set STARK_TARGET." >&2
      exit 1
      ;;
  esac

  printf '%s-%s\n' "${machine}" "${os}"
}

is_macos_target() {
  case "${target_triple}" in
    *apple-macos*|*apple-darwin*)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

cd "${repo_root}"
target_triple="${STARK_TARGET:-$(detect_default_target_triple)}"
target_dist="${vendor_dist}/${target_triple}"
mkdir -p "${target_dist}"

native_args=(
  --native-source "${script_dir}/SQLiteTextBinding.c"
)

bundled_sqlite_dir="${target_dist}/native/sqlite"

if is_macos_target \
  && [[ -f "${bundled_sqlite_dir}/sqlite3.h" ]] \
  && [[ -f "${bundled_sqlite_dir}/libsqlite3.a" ]]; then
  native_args+=(
    --native-include-dir "${bundled_sqlite_dir}"
    --native-library-dir "${bundled_sqlite_dir}"
    --native-library sqlite3
  )
elif command -v pkg-config >/dev/null 2>&1 && pkg-config --exists sqlite3; then
  native_args+=(--native-pkg-config sqlite3)
else
  if [[ -z "${SQLITE_INCLUDE_DIR:-}" || -z "${SQLITE_LIBRARY_DIR:-}" ]]; then
    echo "SQLite is not visible to pkg-config on this machine." >&2
    echo "Either use the bundled native payload under ${bundled_sqlite_dir}, set PKG_CONFIG_PATH, or set SQLITE_INCLUDE_DIR and SQLITE_LIBRARY_DIR." >&2
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
  -o "${target_dist}/libVendorSQLite.a" \
  --target "${target_triple}" \
  "${native_args[@]}"

echo "Built ${target_dist}/libVendorSQLite.a"
echo "Built ${target_dist}/libVendorSQLite.starkpkg"
