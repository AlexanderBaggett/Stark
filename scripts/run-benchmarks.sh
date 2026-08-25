#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
bench_root="${repo_root}/benchmarks"
stdlib_root="${repo_root}/stdlib/src"

runs="${STARK_BENCH_RUNS:-100}"
filter="${STARK_BENCH_FILTER:-}"
target="${STARK_TARGET:-}"
extra_args="${STARK_COMPILER_ARGS:-}"
languages="${STARK_BENCH_LANGUAGES:-stark,c,rust}"
run_timeout_seconds="${STARK_BENCH_TIMEOUT_SECONDS:-30}"
capture_rss="${STARK_BENCH_CAPTURE_RSS:-0}"
rss_poll_interval_seconds="${STARK_BENCH_RSS_POLL_INTERVAL_SECONDS:-0.002}"
c_compiler="${STARK_BENCH_C_COMPILER:-clang}"
rust_compiler="${STARK_BENCH_RUST_COMPILER:-rustc}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
output_dir="${STARK_BENCH_OUTPUT_DIR:-${bench_root}/results}"
results_file="${STARK_BENCH_RESULTS_FILE:-}"
machine_file="${STARK_BENCH_MACHINE_FILE:-}"
baseline_file="${STARK_BENCH_BASELINE_FILE:-}"
regression_checker="${STARK_BENCH_REGRESSION_CHECKER:-${repo_root}/scripts/check-benchmark-regressions.sh}"
c_ratio_adder="${STARK_BENCH_C_RATIO_ADDER:-${repo_root}/scripts/add-benchmark-c-ratios.sh}"
c_flags=(-O3 -DNDEBUG -std=c17)
rust_flags=(-C opt-level=3 -C debug-assertions=no -C overflow-checks=no)

if ! [[ "${runs}" =~ ^[1-9][0-9]*$ ]]; then
  echo "STARK_BENCH_RUNS must be a positive integer." >&2
  exit 2
fi

if ! [[ "${run_timeout_seconds}" =~ ^[0-9]+$ ]]; then
  echo "STARK_BENCH_TIMEOUT_SECONDS must be a non-negative integer." >&2
  exit 2
fi

if [[ "${capture_rss}" != "0" && "${capture_rss}" != "1" ]]; then
  echo "STARK_BENCH_CAPTURE_RSS must be 0 or 1." >&2
  exit 2
fi

if ! [[ "${rss_poll_interval_seconds}" =~ ^([0-9]+([.][0-9]+)?|[.][0-9]+)$ ]]; then
  echo "STARK_BENCH_RSS_POLL_INTERVAL_SECONDS must be a positive number." >&2
  exit 2
fi

if ! awk -v value="${rss_poll_interval_seconds}" 'BEGIN { exit !(value > 0) }'; then
  echo "STARK_BENCH_RSS_POLL_INTERVAL_SECONDS must be greater than zero." >&2
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
portable_mktemp_file() {
  local template="$1"
  local suffix="${2:-}"
  local path

  path="$(mktemp "${template}")"
  if [[ -n "${suffix}" ]]; then
    mv "${path}" "${path}${suffix}"
    path="${path}${suffix}"
  fi

  printf '%s\n' "${path}"
}

if [[ -z "${results_file}" ]]; then
  results_file="$(portable_mktemp_file "${output_dir}/results-${timestamp}.XXXXXX" ".csv")"
fi

if [[ -z "${machine_file}" ]]; then
  machine_file="$(portable_mktemp_file "${output_dir}/machine-${timestamp}.XXXXXX" ".txt")"
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
    printf 'benchmark_timeout_seconds=%s\n' "${run_timeout_seconds}"
    printf 'benchmark_capture_rss=%s\n' "${capture_rss}"
    printf 'benchmark_peak_rss_unit=KiB\n'
    printf 'benchmark_peak_rss_source=Linux /proc VmHWM sampled while each benchmark process runs when STARK_BENCH_CAPTURE_RSS=1; 0 when disabled, unavailable, or process exits before sampling\n'
    printf 'benchmark_stability_column=runtime_spread_pct percent spread calculated as (max_us - min_us) / avg_us * 100\n'
    printf 'benchmark_baseline_file=%s\n' "${baseline_file:-<none>}"
    printf 'benchmark_regression_metric=%s\n' "${STARK_BENCH_REGRESSION_METRIC:-avg_us}"
    printf 'benchmark_require_baseline=%s\n' "${STARK_BENCH_REQUIRE_BASELINE:-0}"
    printf 'benchmark_max_regression_pct=%s\n' "${STARK_BENCH_MAX_REGRESSION_PCT:-10}"
    printf 'benchmark_min_regression_delta=%s\n' "${STARK_BENCH_MIN_REGRESSION_DELTA:-${STARK_BENCH_MIN_REGRESSION_DELTA_US:-50}}"
    printf 'benchmark_max_stark_to_c_ratio=%s\n' "${STARK_BENCH_MAX_STARK_TO_C_RATIO:-<disabled>}"
    printf 'benchmark_max_stark_to_rust_ratio=%s\n' "${STARK_BENCH_MAX_STARK_TO_RUST_RATIO:-<disabled>}"
    printf 'benchmark_ratio_column=c_avg_ratio avg_us divided by same-benchmark C avg_us\n'
    printf 'stark_target=%s\n' "${target:-host-default}"
    printf 'stark_flags=--emit-exe (compiler fixed full-optimization pipeline)\n'
    printf 'stark_compiler_configuration=Release\n'
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

benchmark_label_fields() {
  local benchmark_id="$1"
  local language="$2"

  printf '%s,%s\n' "${benchmark_id}" "${language}"
}

benchmark_group_for_id() {
  local benchmark_id="$1"
  printf '%s\n' "${benchmark_id}"
}

ns_to_us() {
  local ns="$1"
  printf '%s\n' "$(((ns + 500) / 1000))"
}

file_size_bytes() {
  local path="$1"
  if stat -c '%s' "${path}" >/dev/null 2>&1; then
    stat -c '%s' "${path}"
    return
  fi

  stat -f '%z' "${path}"
}

read_process_peak_rss_kib() {
  local pid="$1"
  local status_path="/proc/${pid}/status"

  if [[ ! -r "${status_path}" ]]; then
    printf '0\n'
    return
  fi

  awk '
    $1 == "VmHWM:" {
      print $2
      found = 1
      exit
    }
    $1 == "VmRSS:" && rss == "" {
      rss = $2
    }
    END {
      if (!found) {
        print rss == "" ? 0 : rss
      }
    }
  ' "${status_path}" 2>/dev/null || printf '0\n'
}

poll_process_peak_rss_kib() {
  local pid="$1"
  local peak_path="$2"
  local peak=0

  while [[ -d "/proc/${pid}" ]]; do
    local sample
    sample="$(read_process_peak_rss_kib "${pid}")"
    if [[ "${sample}" =~ ^[0-9]+$ && "${sample}" -gt "${peak}" ]]; then
      peak="${sample}"
      printf '%s\n' "${peak}" > "${peak_path}"
    fi

    sleep "${rss_poll_interval_seconds}" 2>/dev/null || sleep 1
  done
}

read_metric_value() {
  local path="$1"
  local key="$2"

  if [[ ! -f "${path}" ]]; then
    printf '0\n'
    return
  fi

  awk -F= -v key="${key}" '$1 == key { print $2; found = 1; exit } END { if (!found) print 0 }' "${path}"
}

timed_native_benchmarks=()

timed_native_benchmark_seen() {
  local key="$1"
  local timed_native_benchmark

  for timed_native_benchmark in "${timed_native_benchmarks[@]+"${timed_native_benchmarks[@]}"}"; do
    if [[ "${timed_native_benchmark}" == "${key}" ]]; then
      return 0
    fi
  done

  return 1
}

mark_timed_native_benchmark() {
  local key="$1"
  timed_native_benchmarks+=("${key}")
}

last_run_peak_rss_kib=0

emit_captured_benchmark_stderr() {
  local stderr_path="$1"

  if [[ -s "${stderr_path}" ]]; then
    echo "Captured benchmark stderr:" >&2
    sed 's/^/  /' "${stderr_path}" >&2
  fi
}

run_benchmark_executable() {
  local benchmark_id="$1"
  local language="$2"
  local phase="$3"
  local output_path="$4"
  local status
  local stderr_path

  last_run_peak_rss_kib=0
  stderr_path="$(mktemp "${tmp_dir}/benchmark-stderr.XXXXXX")"

  if [[ "${capture_rss}" != "1" ]]; then
    if [[ "${run_timeout_seconds}" -gt 0 ]] && command -v timeout >/dev/null 2>&1; then
      if timeout "${run_timeout_seconds}" "${output_path}" >/dev/null 2>"${stderr_path}"; then
        rm -f "${stderr_path}"
        return 0
      else
        status="$?"
      fi

      if [[ "${status}" -eq 124 ]]; then
        echo "Benchmark ${benchmark_id}/${language} timed out during ${phase} after ${run_timeout_seconds}s." >&2
      else
        echo "Benchmark ${benchmark_id}/${language} exited with status ${status} during ${phase}." >&2
      fi

      emit_captured_benchmark_stderr "${stderr_path}"
      rm -f "${stderr_path}"
      exit "${status}"
    fi

    set +e
    "${output_path}" >/dev/null 2>"${stderr_path}"
    status="$?"
    set -e

    if [[ "${status}" -eq 0 ]]; then
      rm -f "${stderr_path}"
      return 0
    fi

    echo "Benchmark ${benchmark_id}/${language} exited with status ${status} during ${phase}." >&2
    emit_captured_benchmark_stderr "${stderr_path}"
    rm -f "${stderr_path}"
    exit "${status}"
  fi

  local peak_path
  local timeout_path
  local poller_pid=""
  local watchdog_pid=""

  peak_path="$(mktemp "${tmp_dir}/peak-rss.XXXXXX")"
  timeout_path="$(mktemp "${tmp_dir}/timeout.XXXXXX")"
  rm -f "${timeout_path}"
  printf '0\n' > "${peak_path}"

  "${output_path}" >/dev/null 2>"${stderr_path}" &
  local child_pid="$!"

  poll_process_peak_rss_kib "${child_pid}" "${peak_path}" &
  poller_pid="$!"

  if [[ "${run_timeout_seconds}" -gt 0 ]]; then
    (
      sleep "${run_timeout_seconds}"
      if kill -0 "${child_pid}" 2>/dev/null; then
        printf '1\n' > "${timeout_path}"
        kill -TERM "${child_pid}" 2>/dev/null || true
        sleep 1
        kill -KILL "${child_pid}" 2>/dev/null || true
      fi
    ) &
    watchdog_pid="$!"
  fi

  set +e
  wait "${child_pid}"
  status="$?"
  set -e

  if [[ -n "${watchdog_pid}" ]]; then
    kill "${watchdog_pid}" 2>/dev/null || true
    wait "${watchdog_pid}" 2>/dev/null || true
  fi

  if [[ -n "${poller_pid}" ]]; then
    kill "${poller_pid}" 2>/dev/null || true
    wait "${poller_pid}" 2>/dev/null || true
  fi

  last_run_peak_rss_kib="$(cat "${peak_path}" 2>/dev/null || printf '0\n')"
  rm -f "${peak_path}"

  if [[ "${status}" -eq 0 ]]; then
    rm -f "${timeout_path}"
    rm -f "${stderr_path}"
    return 0
  fi

  if [[ -f "${timeout_path}" ]]; then
    rm -f "${timeout_path}"
    echo "Benchmark ${benchmark_id}/${language} timed out during ${phase} after ${run_timeout_seconds}s." >&2
    emit_captured_benchmark_stderr "${stderr_path}"
    rm -f "${stderr_path}"
    exit 124
  fi

  rm -f "${timeout_path}"
  echo "Benchmark ${benchmark_id}/${language} exited with status ${status} during ${phase}." >&2
  emit_captured_benchmark_stderr "${stderr_path}"
  rm -f "${stderr_path}"
  exit "${status}"
}

time_executable() {
  local benchmark_id="$1"
  local language="$2"
  local compile_us="$3"
  local llvm_object_us="$4"
  local link_us="$5"
  local toolchain_us="$6"
  local output_path="$7"

  run_benchmark_executable "${benchmark_id}" "${language}" "warmup" "${output_path}"

  local total_ns=0
  local min_ns=0
  local max_ns=0
  local peak_rss_kib="${last_run_peak_rss_kib}"
  local run
  for ((run = 1; run <= runs; run++)); do
    local run_start
    local run_end
    local elapsed_ns
    run_start="$(date +%s%N)"
    run_benchmark_executable "${benchmark_id}" "${language}" "run ${run}/${runs}" "${output_path}"
    run_end="$(date +%s%N)"
    elapsed_ns="$((run_end - run_start))"
    if [[ "${last_run_peak_rss_kib}" =~ ^[0-9]+$ && "${last_run_peak_rss_kib}" -gt "${peak_rss_kib}" ]]; then
      peak_rss_kib="${last_run_peak_rss_kib}"
    fi
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
  local runtime_spread_pct
  local binary_bytes
  min_us="$(ns_to_us "${min_ns}")"
  avg_us="$(ns_to_us "$((total_ns / runs))")"
  max_us="$(ns_to_us "${max_ns}")"
  runtime_spread_pct="$(awk -v min="${min_us}" -v avg="${avg_us}" -v max="${max_us}" 'BEGIN {
    if (avg <= 0) {
      printf "0.000000"
    } else {
      printf "%.6f", ((max - min) * 100.0) / avg
    }
  }')"
  binary_bytes="$(file_size_bytes "${output_path}")"
  local label_fields
  label_fields="$(benchmark_label_fields "${benchmark_id}" "${language}")"
  emit_row "$(printf '%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s' "${label_fields}" "${runs}" "${compile_us}" "${llvm_object_us}" "${link_us}" "${toolchain_us}" "${binary_bytes}" "${min_us}" "${avg_us}" "${max_us}" "${runtime_spread_pct}" "${peak_rss_kib}")"
}

compile_and_time_stark() {
  local source_path="$1"
  local benchmark_id="$2"
  local output_path="$3"

  local compile_start
  local compile_end
  local compile_us
  local metrics_path="${output_path}.metrics"
  compile_start="$(date +%s%N)"
  local compiler_command=(
    dotnet run -c Release --project "${repo_root}/src" -- \
    "${source_path}" \
    --emit-exe \
    -I "${stdlib_root}" \
    -o "${output_path}" \
    --toolchain-metrics "${metrics_path}"
  )
  if [[ "${#compiler_args[@]}" -gt 0 ]]; then
    compiler_command+=("${compiler_args[@]}")
  fi

  "${compiler_command[@]}" >/dev/null
  compile_end="$(date +%s%N)"
  compile_us="$(ns_to_us "$((compile_end - compile_start))")"
  time_executable \
    "${benchmark_id}" \
    "stark" \
    "${compile_us}" \
    "$(read_metric_value "${metrics_path}" llvm_object_us)" \
    "$(read_metric_value "${metrics_path}" link_us)" \
    "$(read_metric_value "${metrics_path}" toolchain_us)" \
    "${output_path}"
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
  time_executable "${benchmark_id}" "c" "${compile_us}" 0 0 0 "${output_path}"
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
  time_executable "${benchmark_id}" "${language}" "${compile_us}" 0 0 0 "${output_path}"
}

write_machine_metadata "${machine_file}"
echo "Benchmark results: ${results_file}" >&2
echo "Machine metadata: ${machine_file}" >&2

benchmarks=()
while IFS= read -r benchmark; do
  benchmarks+=("${benchmark}")
done < <(find "${bench_root}" -type f -name '*.stark' | sort)
if [[ -n "${filter}" ]]; then
  normalized_filter="${filter//\\//}"
  filtered=()
  for benchmark in "${benchmarks[@]}"; do
    rel_path="${benchmark#"${repo_root}/"}"
    benchmark_id="${rel_path%.stark}"
    benchmark_group="$(benchmark_group_for_id "${benchmark_id}")"
    if [[ "${benchmark}" == *"${filter}"* ||
          "${rel_path}" == *"${normalized_filter}"* ||
          "${benchmark_id}" == *"${normalized_filter}"* ||
          "${benchmark_group}" == *"${normalized_filter}"* ]]; then
      filtered+=("${benchmark}")
    fi
  done
  benchmarks=()
  for benchmark in "${filtered[@]+"${filtered[@]}"}"; do
    benchmarks+=("${benchmark}")
  done
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

emit_row 'benchmark,language,runs,compile_us,llvm_object_us,link_us,toolchain_us,binary_bytes,min_us,avg_us,max_us,runtime_spread_pct,peak_rss_kib'

for source_path in "${benchmarks[@]}"; do
  rel_path="${source_path#"${repo_root}/"}"
  if grep -q '^// stark-bench: compile-only' "${source_path}"; then
    echo "Skipping compile-only benchmark ${rel_path}; compiler tests still validate it lowers successfully." >&2
    continue
  fi

  safe_name="${rel_path//\//_}"
  safe_name="${safe_name%.stark}"
  benchmark_id="${rel_path%.stark}"
  benchmark_group="$(benchmark_group_for_id "${benchmark_id}")"
  native_safe_name="${benchmark_group//\//_}"
  stark_output_path="${tmp_dir}/${safe_name}-stark"
  c_output_path="${tmp_dir}/${native_safe_name}-c"
  rust_output_path="${tmp_dir}/${native_safe_name}-rust"
  if [[ "${OSTYPE:-}" == msys* || "${OSTYPE:-}" == cygwin* || "${OSTYPE:-}" == win32* ]]; then
    stark_output_path="${stark_output_path}.exe"
    c_output_path="${c_output_path}.exe"
    rust_output_path="${rust_output_path}.exe"
  fi

  if language_enabled stark; then
    compile_and_time_stark "${source_path}" "${benchmark_id}" "${stark_output_path}"
  fi

  c_benchmark_key="c|${benchmark_group}"
  if language_enabled c && ! timed_native_benchmark_seen "${c_benchmark_key}"; then
    mark_timed_native_benchmark "${c_benchmark_key}"
    c_source_path="${repo_root}/${benchmark_group}.c"
    if [[ ! -f "${c_source_path}" ]]; then
      echo "Missing C benchmark counterpart for group ${benchmark_group}: ${c_source_path#${repo_root}/}" >&2
      exit 1
    fi
    compile_and_time_c "${c_source_path}" "${benchmark_group}" "${c_output_path}"
  fi

  rust_benchmark_key="rust|${benchmark_group}"
  if language_enabled rust && ! timed_native_benchmark_seen "${rust_benchmark_key}"; then
    mark_timed_native_benchmark "${rust_benchmark_key}"
    rust_source_path="${repo_root}/${benchmark_group}.rs"
    if [[ ! -f "${rust_source_path}" ]]; then
      echo "Missing Rust benchmark counterpart for group ${benchmark_group}: ${rust_source_path#${repo_root}/}" >&2
      exit 1
    fi
    compile_and_time_rust "${rust_source_path}" "${benchmark_group}" "${rust_output_path}"

    shopt -s nullglob
    rust_variant_paths=("${repo_root}/${benchmark_group}".rust-*.rs)
    shopt -u nullglob
    for rust_variant_path in "${rust_variant_paths[@]+"${rust_variant_paths[@]}"}"; do
      rust_variant_name="$(basename "${rust_variant_path}")"
      rust_variant_name="${rust_variant_name#"$(basename "${benchmark_group}").rust-"}"
      rust_variant_name="${rust_variant_name%.rs}"
      if [[ "${rust_variant_name}" == *","* ]]; then
        echo "Rust benchmark variant names must not contain commas: ${rust_variant_path#${repo_root}/}" >&2
        exit 1
      fi

      rust_variant_safe_name="$(printf '%s' "${rust_variant_name}" | tr -c 'A-Za-z0-9_' '_')"
      rust_variant_output_path="${tmp_dir}/${native_safe_name}-rust-${rust_variant_safe_name}"
      if [[ "${OSTYPE:-}" == msys* || "${OSTYPE:-}" == cygwin* || "${OSTYPE:-}" == win32* ]]; then
        rust_variant_output_path="${rust_variant_output_path}.exe"
      fi

      compile_and_time_rust \
        "${rust_variant_path}" \
        "${benchmark_group}" \
        "${rust_variant_output_path}" \
        "rust-${rust_variant_name}"
    done
  fi
done

"${c_ratio_adder}" "${results_file}"
echo "Added c_avg_ratio column using same-benchmark C avg_us baselines." >&2

if [[ -n "${baseline_file}" || "${STARK_BENCH_REQUIRE_BASELINE:-0}" == "1" || -n "${STARK_BENCH_MAX_STARK_TO_C_RATIO:-}" || -n "${STARK_BENCH_MAX_STARK_TO_RUST_RATIO:-}" ]]; then
  regression_args=("${results_file}")
  if [[ -n "${baseline_file}" ]]; then
    regression_args+=("${baseline_file}")
  fi

  "${regression_checker}" "${regression_args[@]}"
fi
