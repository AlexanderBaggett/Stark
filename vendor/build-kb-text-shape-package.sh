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

if command -v pkg-config >/dev/null 2>&1 \
    && pkg-config --exists harfbuzz \
    && pkg-config --exists icu-uc \
    && pkg-config --exists icu-i18n; then
  native_args=(
    --native-pkg-config harfbuzz
    --native-pkg-config icu-uc
    --native-pkg-config icu-i18n
  )
else
  if [[ -z "${HARFBUZZ_INCLUDE_DIR:-}" \
      || -z "${HARFBUZZ_LIBRARY_DIR:-}" \
      || -z "${ICU_INCLUDE_DIR:-}" \
      || -z "${ICU_LIBRARY_DIR:-}" ]]; then
    echo "HarfBuzz and ICU are not visible to pkg-config as harfbuzz, icu-uc, and icu-i18n." >&2
    echo "Either install the development files and set PKG_CONFIG_PATH, or set HARFBUZZ_INCLUDE_DIR, HARFBUZZ_LIBRARY_DIR, ICU_INCLUDE_DIR, and ICU_LIBRARY_DIR." >&2
    echo "Example: HARFBUZZ_INCLUDE_DIR=/usr/include/harfbuzz HARFBUZZ_LIBRARY_DIR=/usr/lib ICU_INCLUDE_DIR=/usr/include ICU_LIBRARY_DIR=/usr/lib bash vendor/build-kb-text-shape-package.sh" >&2
    exit 1
  fi

  if [[ ! -f "${HARFBUZZ_INCLUDE_DIR}/hb.h" ]]; then
    echo "HARFBUZZ_INCLUDE_DIR does not look like a HarfBuzz include directory because hb.h was not found." >&2
    exit 1
  fi

  if [[ ! -f "${ICU_INCLUDE_DIR}/unicode/ubrk.h" ]]; then
    echo "ICU_INCLUDE_DIR does not look like an ICU include directory because unicode/ubrk.h was not found." >&2
    exit 1
  fi

  native_args=(
    --native-include-dir "${HARFBUZZ_INCLUDE_DIR}"
    --native-include-dir "${ICU_INCLUDE_DIR}"
    --native-library-dir "${HARFBUZZ_LIBRARY_DIR}"
    --native-library-dir "${ICU_LIBRARY_DIR}"
    --native-library harfbuzz
    --native-library icui18n
    --native-library icuuc
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/KbTextShape.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${vendor_dist}/libVendorKbTextShape.a" \
  --native-source "${script_dir}/KbTextShapeBinding.c" \
  "${native_args[@]}"

echo "Built ${vendor_dist}/libVendorKbTextShape.a"
echo "Built ${vendor_dist}/libVendorKbTextShape.starkpkg"
