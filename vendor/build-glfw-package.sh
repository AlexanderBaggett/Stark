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

if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists glfw3; then
  native_args=(--native-pkg-config glfw3)
else
  if [[ -z "${GLFW_INCLUDE_DIR:-}" || -z "${GLFW_LIBRARY_DIR:-}" ]]; then
    echo "GLFW is not visible to pkg-config as glfw3." >&2
    echo "Either install GLFW development files and set PKG_CONFIG_PATH, or set GLFW_INCLUDE_DIR and GLFW_LIBRARY_DIR." >&2
    echo "Example: GLFW_INCLUDE_DIR=/usr/include GLFW_LIBRARY_DIR=/usr/lib bash vendor/build-glfw-package.sh" >&2
    exit 1
  fi

  if [[ ! -f "${GLFW_INCLUDE_DIR}/GLFW/glfw3.h" ]]; then
    echo "GLFW_INCLUDE_DIR does not look like a GLFW include directory because GLFW/glfw3.h was not found." >&2
    exit 1
  fi

  native_args=(
    --native-include-dir "${GLFW_INCLUDE_DIR}"
    --native-library-dir "${GLFW_LIBRARY_DIR}"
    --native-library glfw
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/GLFW.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${vendor_dist}/libVendorGLFW.a" \
  --native-source "${script_dir}/GlfwEventBridge.c" \
  "${native_args[@]}"

echo "Built ${vendor_dist}/libVendorGLFW.a"
echo "Built ${vendor_dist}/libVendorGLFW.starkpkg"
