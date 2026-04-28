#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat >&2 <<'USAGE'
Usage: scripts/check-benchmark-regressions.sh <current-results.csv> [baseline-results.csv]

Environment:
  STARK_BENCH_REGRESSION_METRIC       CSV metric to compare. Default: avg_us. Use llvm_object_us, link_us, or toolchain_us for Stark backend gates.
  STARK_BENCH_MAX_REGRESSION_PCT      Allowed same-language regression vs baseline. Default: 10.
  STARK_BENCH_MIN_REGRESSION_DELTA    Minimum absolute delta before a failure. Default: 50.
  STARK_BENCH_REQUIRE_BASELINE        Fail when current rows are missing in the baseline. Default: 0.
  STARK_BENCH_MAX_STARK_TO_C_RATIO    Optional max Stark/C ratio for same-run gates.
  STARK_BENCH_MAX_STARK_TO_RUST_RATIO Optional max Stark/Rust ratio for same-run gates.
USAGE
}

current_file="${1:-}"
baseline_file="${2:-}"
metric="${STARK_BENCH_REGRESSION_METRIC:-avg_us}"
max_regression_pct="${STARK_BENCH_MAX_REGRESSION_PCT:-10}"
min_regression_delta="${STARK_BENCH_MIN_REGRESSION_DELTA:-${STARK_BENCH_MIN_REGRESSION_DELTA_US:-50}}"
require_baseline="${STARK_BENCH_REQUIRE_BASELINE:-0}"
max_stark_to_c_ratio="${STARK_BENCH_MAX_STARK_TO_C_RATIO:-}"
max_stark_to_rust_ratio="${STARK_BENCH_MAX_STARK_TO_RUST_RATIO:-}"

if [[ -z "${current_file}" || "${current_file}" == "-h" || "${current_file}" == "--help" ]]; then
  usage
  if [[ -z "${current_file}" ]]; then
    exit 2
  fi

  exit 0
fi

if [[ ! -f "${current_file}" ]]; then
  echo "Current benchmark results file was not found: ${current_file}" >&2
  exit 2
fi

if [[ -n "${baseline_file}" && ! -f "${baseline_file}" ]]; then
  echo "Baseline benchmark results file was not found: ${baseline_file}" >&2
  exit 2
fi

is_nonnegative_number() {
  [[ "$1" =~ ^[0-9]+([.][0-9]+)?$ ]]
}

is_positive_number() {
  [[ "$1" =~ ^([0-9]+([.][0-9]+)?|[.][0-9]+)$ ]] && awk -v value="$1" 'BEGIN { exit !(value > 0) }'
}

if ! is_nonnegative_number "${max_regression_pct}"; then
  echo "STARK_BENCH_MAX_REGRESSION_PCT must be a non-negative number." >&2
  exit 2
fi

if ! is_nonnegative_number "${min_regression_delta}"; then
  echo "STARK_BENCH_MIN_REGRESSION_DELTA must be a non-negative number." >&2
  exit 2
fi

if [[ "${require_baseline}" != "0" && "${require_baseline}" != "1" ]]; then
  echo "STARK_BENCH_REQUIRE_BASELINE must be 0 or 1." >&2
  exit 2
fi

if [[ "${require_baseline}" == "1" && -z "${baseline_file}" ]]; then
  echo "STARK_BENCH_REQUIRE_BASELINE=1 requires a baseline CSV argument or STARK_BENCH_BASELINE_FILE." >&2
  exit 2
fi

if [[ -n "${max_stark_to_c_ratio}" ]] && ! is_positive_number "${max_stark_to_c_ratio}"; then
  echo "STARK_BENCH_MAX_STARK_TO_C_RATIO must be a positive number." >&2
  exit 2
fi

if [[ -n "${max_stark_to_rust_ratio}" ]] && ! is_positive_number "${max_stark_to_rust_ratio}"; then
  echo "STARK_BENCH_MAX_STARK_TO_RUST_RATIO must be a positive number." >&2
  exit 2
fi

status=0

if [[ -n "${baseline_file}" ]]; then
  if ! awk -F, \
    -v metric="${metric}" \
    -v max_regression_pct="${max_regression_pct}" \
    -v min_delta="${min_regression_delta}" \
    -v require_baseline="${require_baseline}" '
function read_header(    i) {
  delete header
  for (i = 1; i <= NF; i++) {
    header[$i] = i
  }

  benchmark_col = header["benchmark"]
  language_col = header["language"]
  metric_col = header[metric]
  if (!benchmark_col || !language_col || !metric_col) {
    printf("Benchmark CSV must contain benchmark, language, and %s columns.\n", metric) > "/dev/stderr"
    exit 2
  }
}

FNR == 1 {
  read_header()
  next
}

NR == FNR {
  key = $benchmark_col SUBSEP $language_col
  baseline[key] = $metric_col + 0
  next
}

{
  key = $benchmark_col SUBSEP $language_col
  current = $metric_col + 0
  compared++

  if (!(key in baseline)) {
    missing++
    if (require_baseline == "1") {
      printf("MISSING baseline for %s/%s\n", $benchmark_col, $language_col) > "/dev/stderr"
    }
    next
  }

  base = baseline[key]
  delta = current - base
  allowed = base * (1 + (max_regression_pct / 100.0))
  if (delta > min_delta && current > allowed) {
    violations++
    printf("REGRESSION %s/%s %s: current=%s baseline=%s delta=%.0f allowed_pct=%s%% min_delta=%s\n", $benchmark_col, $language_col, metric, current, base, delta, max_regression_pct, min_delta) > "/dev/stderr"
  }
}

END {
  if (require_baseline == "1" && missing > 0) {
    exit 1
  }

  if (violations > 0) {
    exit 1
  }

  printf("Baseline regression check passed: compared=%d missing_baseline=%d metric=%s max_regression=%s%% min_delta=%s\n", compared, missing, metric, max_regression_pct, min_delta)
}
' "${baseline_file}" "${current_file}"; then
    status=1
  fi
fi

if [[ -n "${max_stark_to_c_ratio}" || -n "${max_stark_to_rust_ratio}" ]]; then
  if ! awk -F, \
    -v metric="${metric}" \
    -v min_delta="${min_regression_delta}" \
    -v max_c="${max_stark_to_c_ratio}" \
    -v max_rust="${max_stark_to_rust_ratio}" '
function read_header(    i) {
  delete header
  for (i = 1; i <= NF; i++) {
    header[$i] = i
  }

  benchmark_col = header["benchmark"]
  language_col = header["language"]
  metric_col = header[metric]
  if (!benchmark_col || !language_col || !metric_col) {
    printf("Benchmark CSV must contain benchmark, language, and %s columns.\n", metric) > "/dev/stderr"
    exit 2
  }
}

function check_ratio(benchmark, peer_language, max_ratio,    stark, peer, ratio, delta) {
  if (max_ratio == "") {
    return
  }

  if (!((benchmark SUBSEP "stark") in value) || !((benchmark SUBSEP peer_language) in value)) {
    return
  }

  stark = value[benchmark SUBSEP "stark"]
  peer = value[benchmark SUBSEP peer_language]
  if (peer <= 0) {
    return
  }

  ratio = stark / peer
  delta = stark - peer
  compared++
  if (delta > min_delta && ratio > max_ratio) {
    violations++
    printf("RATIO %s stark/%s %s: stark=%s %s=%s ratio=%.4f max_ratio=%s min_delta=%s\n", benchmark, peer_language, metric, stark, peer_language, peer, ratio, max_ratio, min_delta) > "/dev/stderr"
  }
}

FNR == 1 {
  read_header()
  next
}

{
  benchmark = $benchmark_col
  language = $language_col
  benchmarks[benchmark] = 1
  value[benchmark SUBSEP language] = $metric_col + 0
}

END {
  for (benchmark in benchmarks) {
    check_ratio(benchmark, "c", max_c)
    check_ratio(benchmark, "rust", max_rust)
  }

  if (violations > 0) {
    exit 1
  }

  printf("Same-run C/Rust ratio check passed: compared=%d metric=%s max_stark_to_c=%s max_stark_to_rust=%s min_delta=%s\n", compared, metric, max_c == "" ? "<disabled>" : max_c, max_rust == "" ? "<disabled>" : max_rust, min_delta)
}
' "${current_file}"; then
    status=1
  fi
fi

if [[ -z "${baseline_file}" && -z "${max_stark_to_c_ratio}" && -z "${max_stark_to_rust_ratio}" ]]; then
  echo "No benchmark regression thresholds configured; nothing to check."
fi

exit "${status}"
