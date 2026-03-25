#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "${tmp_dir}"' EXIT

antlr4 \
  -Dlanguage=CSharp \
  -package Stark.Parsing \
  -visitor \
  -no-listener \
  -o "${tmp_dir}" \
  "${repo_root}/Stark.g4"

install -m 0644 "${tmp_dir}/StarkLexer.cs" "${repo_root}/src/Parsing/StarkLexer.cs"
install -m 0644 "${tmp_dir}/StarkParser.cs" "${repo_root}/src/Parsing/StarkParser.cs"
install -m 0644 "${tmp_dir}/StarkVisitor.cs" "${repo_root}/src/Parsing/StarkVisitor.cs"
install -m 0644 "${tmp_dir}/StarkBaseVisitor.cs" "${repo_root}/src/Parsing/StarkBaseVisitor.cs"
