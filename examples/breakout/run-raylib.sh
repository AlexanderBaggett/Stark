#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
raylib_dir="${repo_root}/examples/raylib"
raylib_dist="${raylib_dir}/dist"
stdlib_dir="${repo_root}/stdlib/src"
compiler_path="${STARK_COMPILER:-${repo_root}/stark}"

if [[ -x "${compiler_path}" ]]; then
  compiler_cmd=("${compiler_path}")
else
  compiler_cmd=(dotnet run --project "${repo_root}/src/compiler.csproj" --)
fi

cd "${repo_root}"
mkdir -p "${raylib_dist}"

if [[ -n "${RAYLIB_SRC_DIR:-}" ]]; then
  raylib_native_args=(
    --native-source "${raylib_dir}/RaylibNative.c"
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
  mapfile -t raylib_native_args < "${raylib_dir}/Raylib.package.args"
fi

"${compiler_cmd[@]}" "${raylib_dir}/Raylib.stark" \
  --emit-lib \
  -I "${raylib_dir}" \
  -o "${raylib_dist}/libRaylibStark.a" \
  "${raylib_native_args[@]}" \
  -O0

"${compiler_cmd[@]}" "${script_dir}/BreakoutRaylib.stark" \
  --emit-exe \
  -I "${raylib_dist}" \
  -I "${stdlib_dir}" \
  -o "${script_dir}/breakout-raylib" \
  -O0

echo "Built ${script_dir}/breakout-raylib"
echo "Run it with: ${script_dir}/breakout-raylib"
