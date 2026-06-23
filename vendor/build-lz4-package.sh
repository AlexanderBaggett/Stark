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

if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists liblz4; then
  native_args=(--native-pkg-config liblz4)
else
  if [[ -z "${LZ4_INCLUDE_DIR:-}" || -z "${LZ4_LIBRARY_DIR:-}" ]]; then
    echo "LZ4 is not visible to pkg-config as liblz4." >&2
    echo "Either install liblz4 development files and set PKG_CONFIG_PATH, or set LZ4_INCLUDE_DIR and LZ4_LIBRARY_DIR." >&2
    echo "Example: LZ4_INCLUDE_DIR=/usr/include LZ4_LIBRARY_DIR=/usr/lib bash vendor/build-lz4-package.sh" >&2
    exit 1
  fi

  if [[ ! -f "${LZ4_INCLUDE_DIR}/lz4.h" ]]; then
    echo "LZ4_INCLUDE_DIR does not look like an LZ4 include directory because lz4.h was not found." >&2
    exit 1
  fi

  native_args=(
    --native-include-dir "${LZ4_INCLUDE_DIR}"
    --native-library-dir "${LZ4_LIBRARY_DIR}"
    --native-library lz4
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/LZ4.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${vendor_dist}/libVendorLZ4.a" \
  "${native_args[@]}"

echo "Built ${vendor_dist}/libVendorLZ4.a"
echo "Built ${vendor_dist}/libVendorLZ4.starkpkg"
