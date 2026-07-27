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

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/Cgltf.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${vendor_dist}/libVendorCgltf.a" \
  --native-source "${script_dir}/CgltfImplementation.c" \
  --native-include-dir "${script_dir}/native/cgltf"

echo "Built ${vendor_dist}/libVendorCgltf.a"
echo "Built ${vendor_dist}/libVendorCgltf.starkpkg"
