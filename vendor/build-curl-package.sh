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
  --native-source "${script_dir}/CurlEasyBinding.c"
)

if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists libcurl; then
  native_args+=(--native-pkg-config libcurl)
else
  if [[ -z "${CURL_INCLUDE_DIR:-}" || -z "${CURL_LIBRARY_DIR:-}" ]]; then
    echo "libcurl is not visible to pkg-config on this machine." >&2
    echo "Either install libcurl development files and set PKG_CONFIG_PATH, or set CURL_INCLUDE_DIR and CURL_LIBRARY_DIR." >&2
    echo "Example: CURL_INCLUDE_DIR=/usr/include CURL_LIBRARY_DIR=/usr/lib bash vendor/build-curl-package.sh" >&2
    exit 1
  fi

  if [[ ! -f "${CURL_INCLUDE_DIR}/curl/curl.h" ]]; then
    echo "CURL_INCLUDE_DIR does not look like a libcurl include directory because curl/curl.h was not found." >&2
    exit 1
  fi

  native_args+=(
    --native-include-dir "${CURL_INCLUDE_DIR}"
    --native-library-dir "${CURL_LIBRARY_DIR}"
    --native-library curl
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/Curl.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${vendor_dist}/libVendorCurl.a" \
  "${native_args[@]}"

echo "Built ${vendor_dist}/libVendorCurl.a"
echo "Built ${vendor_dist}/libVendorCurl.starkpkg"
