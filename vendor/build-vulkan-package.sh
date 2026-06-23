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

if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists vulkan; then
  native_args=(--native-pkg-config vulkan)
else
  vulkan_include_dir="${VULKAN_INCLUDE_DIR:-}"
  vulkan_library_dir="${VULKAN_LIBRARY_DIR:-}"

  if [[ -n "${VULKAN_SDK:-}" ]]; then
    vulkan_include_dir="${vulkan_include_dir:-${VULKAN_SDK}/include}"
    vulkan_library_dir="${vulkan_library_dir:-${VULKAN_SDK}/lib}"
  fi

  if [[ -z "${vulkan_include_dir}" || -z "${vulkan_library_dir}" ]]; then
    echo "Vulkan is not visible to pkg-config as vulkan." >&2
    echo "Either install Vulkan development files and set PKG_CONFIG_PATH, set VULKAN_SDK, or set VULKAN_INCLUDE_DIR and VULKAN_LIBRARY_DIR." >&2
    echo "Example: VULKAN_INCLUDE_DIR=/usr/include VULKAN_LIBRARY_DIR=/usr/lib bash vendor/build-vulkan-package.sh" >&2
    exit 1
  fi

  if [[ ! -f "${vulkan_include_dir}/vulkan/vulkan.h" ]]; then
    echo "VULKAN_INCLUDE_DIR does not look like a Vulkan include directory because vulkan/vulkan.h was not found." >&2
    exit 1
  fi

  native_args=(
    --native-include-dir "${vulkan_include_dir}"
    --native-library-dir "${vulkan_library_dir}"
    --native-library vulkan
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/Vulkan.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${vendor_dist}/libVendorVulkan.a" \
  "${native_args[@]}"

echo "Built ${vendor_dist}/libVendorVulkan.a"
echo "Built ${vendor_dist}/libVendorVulkan.starkpkg"
