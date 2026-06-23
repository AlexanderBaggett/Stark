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

if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists sdl3; then
  native_args=(--native-pkg-config sdl3)
else
  if [[ -z "${SDL3_INCLUDE_DIR:-}" || -z "${SDL3_LIBRARY_DIR:-}" ]]; then
    echo "SDL3 is not visible to pkg-config as sdl3." >&2
    echo "Either install SDL3 development files and set PKG_CONFIG_PATH, or set SDL3_INCLUDE_DIR and SDL3_LIBRARY_DIR." >&2
    echo "Example: SDL3_INCLUDE_DIR=/usr/include SDL3_LIBRARY_DIR=/usr/lib bash vendor/build-sdl3-package.sh" >&2
    exit 1
  fi

  if [[ ! -f "${SDL3_INCLUDE_DIR}/SDL3/SDL.h" ]]; then
    echo "SDL3_INCLUDE_DIR does not look like an SDL3 include directory because SDL3/SDL.h was not found." >&2
    exit 1
  fi

  native_args=(
    --native-include-dir "${SDL3_INCLUDE_DIR}"
    --native-library-dir "${SDL3_LIBRARY_DIR}"
    --native-library SDL3
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/SDL3.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${vendor_dist}/libVendorSDL3.a" \
  --native-source "${script_dir}/Sdl3Binding.c" \
  "${native_args[@]}"

echo "Built ${vendor_dist}/libVendorSDL3.a"
echo "Built ${vendor_dist}/libVendorSDL3.starkpkg"
