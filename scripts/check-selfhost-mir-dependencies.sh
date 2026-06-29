#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MIR_DIR="$ROOT_DIR/selfhost/Compiler/Mir"

CORE_MODULES=(
  Model
  Builder
  Facts
  TextRendering
)

FORBIDDEN_IMPORT='^[[:space:]]*import[[:space:]]+(Compiler\.(Lexing|Parsing|Binding|Typing)(\.|[[:space:]]|$)|Compiler\.Mir\.(LlvmText|LlvmFacts|LlvmInstructions|LlvmBlocks|LlvmControlFlow|LlvmFunctions|LlvmModules|EnumLayout|PackageCodec|PackageImage|AssemblyMetadata|TestSupport)([[:space:]]|$))'

status=0
for module in "${CORE_MODULES[@]}"; do
  path="$MIR_DIR/$module.stark"
  if [[ ! -f "$path" ]]; then
    echo "missing MIR core module: $path" >&2
    status=1
    continue
  fi

  matches="$(grep -nE "$FORBIDDEN_IMPORT" "$path" || true)"
  if [[ -n "$matches" ]]; then
    echo "forbidden frontend/backend/test import in $path:" >&2
    echo "$matches" >&2
    status=1
  fi
done

exit "$status"
