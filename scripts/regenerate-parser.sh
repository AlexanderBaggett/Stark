#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
tmp_dir="$(mktemp -d)"
antlr_version="4.13.1"
antlr_url="https://repo.maven.apache.org/maven2/org/antlr/antlr4/4.13.1/antlr4-4.13.1-complete.jar"
antlr_sha256="bc13a9c57a8dd7d5196888211e5ede657cb64a3ce968608697e4f668251a8487"
java_command="${JAVA:-java}"

if [[ -n "${ANTLR4_JAR:-}" ]]; then
  antlr_jar="${ANTLR4_JAR}"
else
  cache_dir="${ANTLR4_CACHE_DIR:-${XDG_CACHE_HOME:-${HOME}/.cache}/stark/antlr}"
  mkdir -p "${cache_dir}"
  antlr_jar="${cache_dir}/antlr4-${antlr_version}-complete.jar"
fi

cleanup_paths=("${tmp_dir}")
cleanup() {
  rm -rf "${cleanup_paths[@]}"
}
trap cleanup EXIT

sha256_file() {
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  elif command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    echo "Parser regeneration requires shasum or sha256sum to verify the ANTLR generator." >&2
    return 1
  fi
}

if [[ ! -f "${antlr_jar}" ]]; then
  if [[ -n "${ANTLR4_JAR:-}" ]]; then
    echo "ANTLR4_JAR does not name a file: ${antlr_jar}" >&2
    exit 1
  fi
  if ! command -v curl >/dev/null 2>&1; then
    echo "Parser regeneration requires curl to acquire the pinned ANTLR ${antlr_version} generator." >&2
    exit 1
  fi

  download_path="$(mktemp "${cache_dir}/.antlr4-${antlr_version}.XXXXXX")"
  cleanup_paths+=("${download_path}")
  curl --fail --location --retry 3 --output "${download_path}" "${antlr_url}"
  download_sha256="$(sha256_file "${download_path}")"
  if [[ "${download_sha256}" != "${antlr_sha256}" ]]; then
    echo "ANTLR generator checksum mismatch: expected ${antlr_sha256}, got ${download_sha256}." >&2
    exit 1
  fi
  mv -f "${download_path}" "${antlr_jar}"
fi

actual_sha256="$(sha256_file "${antlr_jar}")"
if [[ "${actual_sha256}" != "${antlr_sha256}" ]]; then
  echo "ANTLR generator checksum mismatch: expected ${antlr_sha256}, got ${actual_sha256}." >&2
  exit 1
fi

if [[ "${java_command}" == */* ]]; then
  if [[ ! -x "${java_command}" ]]; then
    echo "JAVA does not name an executable: ${java_command}" >&2
    exit 1
  fi
elif ! command -v "${java_command}" >/dev/null 2>&1; then
  echo "Parser regeneration requires Java. Set JAVA to a Java executable if it is not on PATH." >&2
  exit 1
fi

cd "${repo_root}"
"${java_command}" -jar "${antlr_jar}" \
  -Dlanguage=CSharp \
  -package Stark.Parsing \
  -visitor \
  -no-listener \
  -o "${tmp_dir}" \
  Stark.g4

install -m 0644 "${tmp_dir}/StarkLexer.cs" "${repo_root}/src/Parsing/StarkLexer.cs"
install -m 0644 "${tmp_dir}/StarkParser.cs" "${repo_root}/src/Parsing/StarkParser.cs"
install -m 0644 "${tmp_dir}/StarkVisitor.cs" "${repo_root}/src/Parsing/StarkVisitor.cs"
install -m 0644 "${tmp_dir}/StarkBaseVisitor.cs" "${repo_root}/src/Parsing/StarkBaseVisitor.cs"
