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
languages="${STARK_BENCH_LANGUAGES:-stark,c,rust}"
c_compiler="${STARK_BENCH_C_COMPILER:-clang}"
rust_compiler="${STARK_BENCH_RUST_COMPILER:-rustc}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
output_dir="${STARK_BENCH_OUTPUT_DIR:-${bench_root}/results}"
results_file="${STARK_BENCH_RESULTS_FILE:-}"
machine_file="${STARK_BENCH_MACHINE_FILE:-}"
c_flags=(-O3 -DNDEBUG -std=c17)
rust_flags=(-C opt-level=3 -C debug-assertions=no -C overflow-checks=no)

if ! [[ "${runs}" =~ ^[1-9][0-9]*$ ]]; then
  echo "STARK_BENCH_RUNS must be a positive integer." >&2
  exit 2
fi

language_enabled() {
  local language="$1"
  [[ ",${languages}," == *",${language},"* ]]
}

IFS=',' read -r -a selected_languages <<< "${languages}"
for language in "${selected_languages[@]}"; do
  case "${language}" in
    stark | c | rust)
      ;;
    *)
      echo "Unsupported STARK_BENCH_LANGUAGES entry '${language}'. Expected stark, c, and/or rust." >&2
      exit 2
      ;;
  esac
done

if language_enabled c && ! command -v "${c_compiler}" >/dev/null 2>&1; then
  echo "C benchmark compiler '${c_compiler}' was not found." >&2
  exit 2
fi

if language_enabled rust && ! command -v "${rust_compiler}" >/dev/null 2>&1; then
  echo "Rust benchmark compiler '${rust_compiler}' was not found." >&2
  exit 2
fi

mkdir -p "${output_dir}"
if [[ -z "${results_file}" ]]; then
  results_file="$(mktemp "${output_dir}/results-${timestamp}.XXXXXX.csv")"
fi

if [[ -z "${machine_file}" ]]; then
  machine_file="$(mktemp "${output_dir}/machine-${timestamp}.XXXXXX.txt")"
fi

mkdir -p "$(dirname "${results_file}")" "$(dirname "${machine_file}")"
: > "${results_file}"

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/stark-bench-XXXXXX")"
cleanup() {
  rm -rf "${tmp_dir}"
}
trap cleanup EXIT

first_line_or_not_found() {
  local command_name="$1"
  shift

  if command -v "${command_name}" >/dev/null 2>&1; then
    "$@" 2>/dev/null | head -n 1
  else
    printf 'not-found\n'
  fi
}

write_machine_metadata() {
  local path="$1"
  local cpu_model="unknown"
  local memory_kib="unknown"
  local git_commit="unknown"
  local git_dirty_entries="unknown"

  if [[ -r /proc/cpuinfo ]]; then
    cpu_model="$(awk -F: '/model name|Hardware/ { gsub(/^[ \t]+/, "", $2); print $2; exit }' /proc/cpuinfo)"
  fi

  if [[ -r /proc/meminfo ]]; then
    memory_kib="$(awk '/MemTotal/ { print $2 " KiB"; exit }' /proc/meminfo)"
  fi

  if command -v git >/dev/null 2>&1 && git -C "${repo_root}" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    git_commit="$(git -C "${repo_root}" rev-parse HEAD)"
    git_dirty_entries="$(git -C "${repo_root}" status --short | wc -l | tr -d ' ')"
  fi

  {
    printf 'timestamp_utc=%s\n' "${timestamp}"
    printf 'results_file=%s\n' "${results_file}"
    printf 'machine_file=%s\n' "${path}"
    printf 'repository_root=%s\n' "${repo_root}"
    printf 'git_commit=%s\n' "${git_commit}"
    printf 'git_dirty_entries=%s\n' "${git_dirty_entries}"
    printf 'kernel=%s\n' "$(uname -srvmo 2>/dev/null || uname -a)"
    printf 'cpu_model=%s\n' "${cpu_model:-unknown}"
    printf 'cpu_count=%s\n' "$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf 'unknown')"
    printf 'memory=%s\n' "${memory_kib}"
    printf 'dotnet=%s\n' "$(first_line_or_not_found dotnet dotnet --version)"
    printf 'clang=%s\n' "$(first_line_or_not_found clang clang --version)"
    printf 'cc=%s\n' "$(first_line_or_not_found cc cc --version)"
    printf 'rustc=%s\n' "$(first_line_or_not_found rustc rustc --version)"
    printf 'stark_runs=%s\n' "${runs}"
    printf 'timing_unit=microseconds\n'
    printf 'stark_filter=%s\n' "${filter:-<none>}"
    printf 'benchmark_languages=%s\n' "${languages}"
    printf 'stark_target=%s\n' "${target:-host-default}"
    printf 'stark_flags=--emit-exe -O3\n'
    printf 'stark_compiler_args=%s\n' "${extra_args:-<none>}"
    printf 'c_compiler=%s\n' "${c_compiler}"
    printf 'c_flags=%s\n' "${c_flags[*]}"
    printf 'rust_compiler=%s\n' "${rust_compiler}"
    printf 'rust_flags=%s\n' "${rust_flags[*]}"
    printf 'fairness_rules=benchmarks/Fairness.md\n'
  } > "${path}"
}

emit_row() {
  printf '%s\n' "$1"
  printf '%s\n' "$1" >> "${results_file}"
}

ns_to_us() {
  local ns="$1"
  printf '%s\n' "$(((ns + 500) / 1000))"
}

time_executable() {
  local benchmark_id="$1"
  local language="$2"
  local compile_us="$3"
  local output_path="$4"

  "${output_path}" >/dev/null

  local total_ns=0
  local min_ns=0
  local max_ns=0
  local run
  for ((run = 1; run <= runs; run++)); do
    local run_start
    local run_end
    local elapsed_ns
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

  local min_us
  local avg_us
  local max_us
  min_us="$(ns_to_us "${min_ns}")"
  avg_us="$(ns_to_us "$((total_ns / runs))")"
  max_us="$(ns_to_us "${max_ns}")"
  emit_row "$(printf '%s,%s,%s,%s,%s,%s,%s' "${benchmark_id}" "${language}" "${runs}" "${compile_us}" "${min_us}" "${avg_us}" "${max_us}")"
}

compile_and_time_stark() {
  local source_path="$1"
  local benchmark_id="$2"
  local output_path="$3"

  local compile_start
  local compile_end
  local compile_us
  compile_start="$(date +%s%N)"
  dotnet run --project "${repo_root}/src" -- \
    "${source_path}" \
    --emit-exe \
    -O3 \
    -I "${stdlib_root}" \
    -o "${output_path}" \
    "${compiler_args[@]}" >/dev/null
  compile_end="$(date +%s%N)"
  compile_us="$(ns_to_us "$((compile_end - compile_start))")"
  time_executable "${benchmark_id}" "stark" "${compile_us}" "${output_path}"
}

compile_and_time_c() {
  local source_path="$1"
  local benchmark_id="$2"
  local output_path="$3"

  local compile_start
  local compile_end
  local compile_us
  compile_start="$(date +%s%N)"
  "${c_compiler}" "${source_path}" "${c_flags[@]}" -o "${output_path}"
  compile_end="$(date +%s%N)"
  compile_us="$(ns_to_us "$((compile_end - compile_start))")"
  time_executable "${benchmark_id}" "c" "${compile_us}" "${output_path}"
}

compile_and_time_rust() {
  local source_path="$1"
  local benchmark_id="$2"
  local output_path="$3"
  local language="${4:-rust}"
  local crate_name
  crate_name="$(basename "${source_path}")"
  crate_name="${crate_name%.rs}"
  crate_name="$(printf '%s' "${crate_name}" | tr -c 'A-Za-z0-9_' '_')"
  if [[ "${crate_name}" =~ ^[0-9] ]]; then
    crate_name="bench_${crate_name}"
  fi

  local compile_start
  local compile_end
  local compile_us
  compile_start="$(date +%s%N)"
  "${rust_compiler}" --crate-name "${crate_name}" "${source_path}" "${rust_flags[@]}" -o "${output_path}"
  compile_end="$(date +%s%N)"
  compile_us="$(ns_to_us "$((compile_end - compile_start))")"
  time_executable "${benchmark_id}" "${language}" "${compile_us}" "${output_path}"
}

write_machine_metadata "${machine_file}"
echo "Benchmark results: ${results_file}" >&2
echo "Machine metadata: ${machine_file}" >&2

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

emit_row 'benchmark,language,runs,compile_us,min_us,avg_us,max_us'

for source_path in "${benchmarks[@]}"; do
  rel_path="${source_path#"${repo_root}/"}"
  if grep -q '^// stark-bench: compile-only' "${source_path}"; then
    echo "Skipping compile-only benchmark ${rel_path}; compiler tests still validate it lowers successfully." >&2
    continue
  fi

  safe_name="${rel_path//\//_}"
  safe_name="${safe_name%.stark}"
  benchmark_id="${rel_path%.stark}"
  stark_output_path="${tmp_dir}/${safe_name}-stark"
  c_output_path="${tmp_dir}/${safe_name}-c"
  rust_output_path="${tmp_dir}/${safe_name}-rust"
  if [[ "${OSTYPE:-}" == msys* || "${OSTYPE:-}" == cygwin* || "${OSTYPE:-}" == win32* ]]; then
    stark_output_path="${stark_output_path}.exe"
    c_output_path="${c_output_path}.exe"
    rust_output_path="${rust_output_path}.exe"
  fi

  if language_enabled stark; then
    compile_and_time_stark "${source_path}" "${benchmark_id}" "${stark_output_path}"
  fi

  if language_enabled c; then
    c_source_path="${source_path%.stark}.c"
    if [[ ! -f "${c_source_path}" ]]; then
      echo "Missing C benchmark counterpart for ${rel_path}: ${c_source_path#${repo_root}/}" >&2
      exit 1
    fi
    compile_and_time_c "${c_source_path}" "${benchmark_id}" "${c_output_path}"
  fi

  if language_enabled rust; then
    rust_source_path="${source_path%.stark}.rs"
    if [[ ! -f "${rust_source_path}" ]]; then
      echo "Missing Rust benchmark counterpart for ${rel_path}: ${rust_source_path#${repo_root}/}" >&2
      exit 1
    fi
    compile_and_time_rust "${rust_source_path}" "${benchmark_id}" "${rust_output_path}"

    shopt -s nullglob
    rust_variant_paths=("${source_path%.stark}".rust-*.rs)
    shopt -u nullglob
    for rust_variant_path in "${rust_variant_paths[@]}"; do
      rust_variant_name="$(basename "${rust_variant_path}")"
      rust_variant_name="${rust_variant_name#"$(basename "${source_path%.stark}").rust-"}"
      rust_variant_name="${rust_variant_name%.rs}"
      if [[ "${rust_variant_name}" == *","* ]]; then
        echo "Rust benchmark variant names must not contain commas: ${rust_variant_path#${repo_root}/}" >&2
        exit 1
      fi

      rust_variant_safe_name="$(printf '%s' "${rust_variant_name}" | tr -c 'A-Za-z0-9_' '_')"
      rust_variant_output_path="${tmp_dir}/${safe_name}-rust-${rust_variant_safe_name}"
      if [[ "${OSTYPE:-}" == msys* || "${OSTYPE:-}" == cygwin* || "${OSTYPE:-}" == win32* ]]; then
        rust_variant_output_path="${rust_variant_output_path}.exe"
      fi

      compile_and_time_rust \
        "${rust_variant_path}" \
        "${benchmark_id}" \
        "${rust_variant_output_path}" \
        "rust-${rust_variant_name}"
    done
  fi
done
