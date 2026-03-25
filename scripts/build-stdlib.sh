#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"

output_dir="${1:-${repo_root}/stdlib/dist}"
mkdir -p "${output_dir}"

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    library_name="System.lib"
    ;;
  *)
    library_name="libSystem.a"
    ;;
esac

output_path="${output_dir}/${library_name}"

dotnet run --project "${repo_root}/src" -- \
  "${repo_root}/stdlib/src/System.stark" \
  --emit-lib \
  -o "${output_path}"

echo "Standard library package emitted to ${output_dir}"
