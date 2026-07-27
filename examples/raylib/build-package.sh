#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
raylib_dist="${script_dir}/dist"
compiler_path="${STARK_COMPILER:-${repo_root}/stark}"

if [[ -x "${compiler_path}" ]]; then
  compiler_cmd=("${compiler_path}")
else
  compiler_cmd=(dotnet run --project "${repo_root}/src/compiler.csproj" --)
fi

cd "${repo_root}"
mkdir -p "${raylib_dist}"

if [[ -n "${RAYLIB_SRC_DIR:-}" ]]; then
  if [[ ! -d "${RAYLIB_SRC_DIR}" ]]; then
    echo "RAYLIB_SRC_DIR must point to Raylib's src directory." >&2
    echo "Example: RAYLIB_SRC_DIR=/tmp/stark-raylib/raylib-6.0/src bash examples/breakout/run-raylib.sh" >&2
    exit 1
  fi

  if [[ ! -f "${RAYLIB_SRC_DIR}/raylib.h" ]]; then
    echo "RAYLIB_SRC_DIR does not look like a Raylib src directory because raylib.h was not found." >&2
    echo "Example: RAYLIB_SRC_DIR=/tmp/stark-raylib/raylib-6.0/src bash examples/breakout/run-raylib.sh" >&2
    exit 1
  fi

  native_args=(
    --native-include-dir "${RAYLIB_SRC_DIR}"
    --native-library-dir "${RAYLIB_SRC_DIR}"
    --native-library raylib
    --native-library GL
    --native-library m
    --native-library pthread
    --native-library dl
    --native-library rt
    --native-library X11
    --native-library Xrandr
    --native-library Xi
    --native-library Xcursor
    --native-library Xinerama
  )
else
  if ! command -v pkg-config >/dev/null 2>&1 || ! pkg-config --exists raylib; then
    echo "Raylib is not visible to pkg-config on this machine." >&2
    echo "Either install raylib.pc and set PKG_CONFIG_PATH, or point RAYLIB_SRC_DIR at a local Raylib src directory." >&2
    echo "Example: RAYLIB_SRC_DIR=/tmp/stark-raylib/raylib-6.0/src bash examples/breakout/run-raylib.sh" >&2
    exit 1
  fi

  mapfile -t native_args < "${script_dir}/Raylib.package.args"
fi

"${compiler_cmd[@]}" "${script_dir}/Raylib.stark" \
  --emit-lib \
  -I "${script_dir}" \
  -o "${raylib_dist}/libRaylibStark.a" \
  "${native_args[@]}"

echo "Built ${raylib_dist}/libRaylibStark.a"
echo "Built ${raylib_dist}/libRaylibStark.starkpkg"
