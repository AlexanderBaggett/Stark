#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat >&2 <<'USAGE'
Usage: scripts/add-benchmark-c-ratios.sh <results.csv>

Adds or refreshes a c_avg_ratio column in a benchmark CSV. The ratio is based on
avg_us for each benchmark id:

  row c_avg_ratio = row avg_us / same-benchmark C avg_us

The C row is therefore 1.000000. Rows without a same-benchmark C result get an
empty ratio.
USAGE
}

results_file="${1:-}"
if [[ -z "${results_file}" || "${results_file}" == "-h" || "${results_file}" == "--help" ]]; then
  usage
  if [[ -z "${results_file}" ]]; then
    exit 2
  fi

  exit 0
fi

if [[ ! -f "${results_file}" ]]; then
  echo "Benchmark results file was not found: ${results_file}" >&2
  exit 2
fi

tmp_file="$(mktemp "${results_file}.ratios.XXXXXX")"
cleanup() {
  rm -f "${tmp_file}"
}
trap cleanup EXIT

awk -F, '
BEGIN {
  OFS = ","
}

function read_header(    i) {
  delete header
  ratio_col = 0
  clean_count = 0
  for (i = 1; i <= NF; i++) {
    header[$i] = i
    if ($i == "c_avg_ratio") {
      ratio_col = i
      continue
    }

    clean_count++
    clean_header[clean_count] = $i
  }

  benchmark_col = header["benchmark"]
  language_col = header["language"]
  avg_col = header["avg_us"]
  if (!benchmark_col || !language_col || !avg_col) {
    printf("Benchmark CSV must contain benchmark, language, and avg_us columns.\n") > "/dev/stderr"
    exit 2
  }
}

function print_clean_header(    i) {
  for (i = 1; i <= clean_count; i++) {
    printf("%s%s", i == 1 ? "" : OFS, clean_header[i])
  }

  printf("%sc_avg_ratio\n", OFS)
}

function print_row_with_ratio(ratio,    i, first) {
  first = 1
  for (i = 1; i <= NF; i++) {
    if (i == ratio_col) {
      continue
    }

    printf("%s%s", first ? "" : OFS, $i)
    first = 0
  }

  printf("%s%s\n", OFS, ratio)
}

FNR == NR {
  if (FNR == 1) {
    read_header()
    next
  }

  if ($language_col == "c" && $avg_col + 0 > 0) {
    c_avg[$benchmark_col] = $avg_col + 0
  }

  next
}

FNR == 1 {
  print_clean_header()
  next
}

{
  ratio = ""
  baseline = 0
  if (($benchmark_col in c_avg) && c_avg[$benchmark_col] > 0) {
    baseline = c_avg[$benchmark_col]
  }

  if (baseline > 0 && $avg_col != "") {
    ratio = sprintf("%.6f", ($avg_col + 0) / baseline)
  }

  print_row_with_ratio(ratio)
}
' "${results_file}" "${results_file}" > "${tmp_file}"

mv "${tmp_file}" "${results_file}"
trap - EXIT
