#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
bench_root="${repo_root}/benchmarks"
stdlib_root="${repo_root}/stdlib/src"

runs="${STARK_BENCH_RUNS:-3}"
filter="${STARK_BENCH_FILTER:-}"
target="${STARK_TARGET:-}"
extra_args="${STARK_COMPILER_ARGS:-}"

if ! [[ "${runs}" =~ ^[1-9][0-9]*$ ]]; then
  echo "STARK_BENCH_RUNS must be a positive integer." >&2
  exit 2
fi

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/stark-bench-XXXXXX")"
cleanup() {
  rm -rf "${tmp_dir}"
}
trap cleanup EXIT

mapfile -t benchmarks < <(find "${bench_root}" -type f -name '*.stark' | sort)
if [[ -n "${filter}" ]]; then
  filtered=()
  for benchmark in "${benchmarks[@]}"; do
    if [[ "${benchmark}" == *"${filter}"* ]]; then
      filtered+=("${benchmark}")
    fi
  done
  benchmarks=("${filtered[@]}")
fi

if [[ "${#benchmarks[@]}" -eq 0 ]]; then
  echo "No benchmark sources matched." >&2
  exit 1
fi

compiler_args=()
if [[ -n "${target}" ]]; then
  compiler_args+=("--target" "${target}")
fi

if [[ -n "${extra_args}" ]]; then
  # shellcheck disable=SC2206
  compiler_args+=(${extra_args})
fi

printf 'benchmark,runs,compile_ms,min_ms,avg_ms,max_ms\n'

for source_path in "${benchmarks[@]}"; do
  rel_path="${source_path#"${repo_root}/"}"
  if grep -q '^// stark-bench: compile-only' "${source_path}"; then
    echo "Skipping compile-only benchmark ${rel_path}; compiler tests still validate it lowers successfully." >&2
    continue
  fi

  safe_name="${rel_path//\//_}"
  safe_name="${safe_name%.stark}"
  output_path="${tmp_dir}/${safe_name}"
  if [[ "${OSTYPE:-}" == msys* || "${OSTYPE:-}" == cygwin* || "${OSTYPE:-}" == win32* ]]; then
    output_path="${output_path}.exe"
  fi

  compile_start="$(date +%s%N)"
  dotnet run --project "${repo_root}/src" -- \
    "${source_path}" \
    --emit-exe \
    -I "${stdlib_root}" \
    -o "${output_path}" \
    "${compiler_args[@]}" >/dev/null
  compile_end="$(date +%s%N)"
  compile_ms="$(((compile_end - compile_start) / 1000000))"

  "${output_path}" >/dev/null

  total_ns=0
  min_ns=0
  max_ns=0
  for ((run = 1; run <= runs; run++)); do
    run_start="$(date +%s%N)"
    "${output_path}" >/dev/null
    run_end="$(date +%s%N)"
    elapsed_ns="$((run_end - run_start))"
    total_ns="$((total_ns + elapsed_ns))"
    if [[ "${min_ns}" -eq 0 || "${elapsed_ns}" -lt "${min_ns}" ]]; then
      min_ns="${elapsed_ns}"
    fi
    if [[ "${elapsed_ns}" -gt "${max_ns}" ]]; then
      max_ns="${elapsed_ns}"
    fi
  done

  min_ms="$((min_ns / 1000000))"
  avg_ms="$(((total_ns / runs) / 1000000))"
  max_ms="$((max_ns / 1000000))"
  printf '%s,%s,%s,%s,%s,%s\n' "${rel_path}" "${runs}" "${compile_ms}" "${min_ms}" "${avg_ms}" "${max_ms}"
done
