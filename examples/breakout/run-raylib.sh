#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
raylib_dir="${repo_root}/examples/raylib"
stdlib_dir="${repo_root}/stdlib/src"
compiler_path="${STARK_COMPILER:-${repo_root}/stark}"

if [[ -x "${compiler_path}" ]]; then
  compiler_cmd=("${compiler_path}")
else
  compiler_cmd=(dotnet run --project "${repo_root}/src/compiler.csproj" --)
fi

cd "${repo_root}"
bash "${raylib_dir}/build-package.sh"

"${compiler_cmd[@]}" "${script_dir}/BreakoutRaylib.stark" \
  --emit-exe \
  -I "${raylib_dir}/dist" \
  -I "${stdlib_dir}" \
  -o "${script_dir}/breakout-raylib" \
  -O0

echo "Built ${script_dir}/breakout-raylib"
echo "Run it with: ${script_dir}/breakout-raylib"
