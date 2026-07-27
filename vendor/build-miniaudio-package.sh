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
  --native-source "${script_dir}/MiniaudioImplementation.c"
  --native-include-dir "${script_dir}/native/miniaudio"
)

case "$(uname -s)" in
  Linux*)
    native_args+=(
      --native-library pthread
      --native-library m
      --native-library dl
    )
    ;;
  Darwin*)
    native_args+=(
      --native-link-arg "-framework"
      --native-link-arg "CoreAudio"
      --native-link-arg "-framework"
      --native-link-arg "AudioToolbox"
      --native-link-arg "-framework"
      --native-link-arg "CoreFoundation"
    )
    ;;
esac

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/Miniaudio.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${vendor_dist}/libVendorMiniaudio.a" \
  "${native_args[@]}"

echo "Built ${vendor_dist}/libVendorMiniaudio.a"
echo "Built ${vendor_dist}/libVendorMiniaudio.starkpkg"
