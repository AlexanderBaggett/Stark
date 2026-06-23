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
  --native-source "${script_dir}/ZlibStreamBinding.c"
)

if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists zlib; then
  native_args+=(--native-pkg-config zlib)
else
  if [[ -z "${ZLIB_INCLUDE_DIR:-}" || -z "${ZLIB_LIBRARY_DIR:-}" ]]; then
    echo "zlib is not visible to pkg-config on this machine." >&2
    echo "Either install zlib development files and set PKG_CONFIG_PATH, or set ZLIB_INCLUDE_DIR and ZLIB_LIBRARY_DIR." >&2
    echo "Example: ZLIB_INCLUDE_DIR=/usr/include ZLIB_LIBRARY_DIR=/usr/lib bash vendor/build-zlib-package.sh" >&2
    exit 1
  fi

  if [[ ! -f "${ZLIB_INCLUDE_DIR}/zlib.h" ]]; then
    echo "ZLIB_INCLUDE_DIR does not look like a zlib include directory because zlib.h was not found." >&2
    exit 1
  fi

  native_args+=(
    --native-include-dir "${ZLIB_INCLUDE_DIR}"
    --native-library-dir "${ZLIB_LIBRARY_DIR}"
    --native-library z
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/Zlib.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${vendor_dist}/libVendorZlib.a" \
  "${native_args[@]}"

echo "Built ${vendor_dist}/libVendorZlib.a"
echo "Built ${vendor_dist}/libVendorZlib.starkpkg"
