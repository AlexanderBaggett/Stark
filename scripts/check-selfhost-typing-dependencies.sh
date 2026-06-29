#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TYPING_DIR="$ROOT_DIR/selfhost/Compiler/Typing"

FORBIDDEN_IMPORT='^[[:space:]]*import[[:space:]]+(Compiler\.(Binding|Mir|Ssa|Ir|Llvm|LLVM)(\.|[[:space:]]|$)|Vendor\.LLVM(\.|[[:space:]]|$)|LLVM([[:space:]]|$))'

status=0
while IFS= read -r path; do
  matches="$(grep -nE "$FORBIDDEN_IMPORT" "$path" || true)"
  if [[ -n "$matches" ]]; then
    echo "forbidden backend/validation import in $path:" >&2
    echo "$matches" >&2
    status=1
  fi
done < <(find "$TYPING_DIR" -maxdepth 1 -type f -name '*.stark' | sort)

exit "$status"
