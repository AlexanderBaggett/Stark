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

PACKAGE_IMAGE_DIR="$MIR_DIR/PackageImage"

if [[ ! -d "$PACKAGE_IMAGE_DIR" ]]; then
  echo "missing PackageImage module directory: $PACKAGE_IMAGE_DIR" >&2
  status=1
else
  FOCUSED_FACADE_IMPORT='^[[:space:]]*(export[[:space:]]+)?import[[:space:]]+Compiler\.Mir\.(PackageImage|PackageCodec)([[:space:]]|$)'
  while IFS= read -r path; do
    matches="$(grep -nE "$FOCUSED_FACADE_IMPORT" "$path" || true)"
    if [[ -n "$matches" ]]; then
      echo "focused PackageImage module imports a compatibility facade in $path:" >&2
      echo "$matches" >&2
      status=1
    fi
  done < <(find "$PACKAGE_IMAGE_DIR" -type f -name '*.stark' -print | sort)

  SHARED_FORBIDDEN_IMPORT='^[[:space:]]*(export[[:space:]]+)?import[[:space:]]+(Compiler\.Mir\.PackageImage\.(Builder|Loader|Bridge|Inspection)(\.|[[:space:]]|$)|Compiler\.Mir\.(PackageImage|PackageCodec)([[:space:]]|$))'
  while IFS= read -r path; do
    matches="$(grep -nE "$SHARED_FORBIDDEN_IMPORT" "$path" || true)"
    if [[ -n "$matches" ]]; then
      echo "forbidden operational/facade import in PackageImage Shared module $path:" >&2
      echo "$matches" >&2
      status=1
    fi
  done < <(find "$PACKAGE_IMAGE_DIR/Shared" -type f -name '*.stark' -print | sort)

  MODELS_FORBIDDEN_IMPORT='^[[:space:]]*(export[[:space:]]+)?import[[:space:]]+Compiler\.Mir\.PackageImage\.Shared(\.|[[:space:]]|$)'
  matches="$(grep -nE "$MODELS_FORBIDDEN_IMPORT" "$PACKAGE_IMAGE_DIR/Models.stark" || true)"
  if [[ -n "$matches" ]]; then
    echo "forbidden Shared import in PackageImage model module $PACKAGE_IMAGE_DIR/Models.stark:" >&2
    echo "$matches" >&2
    status=1
  fi
fi

exit "$status"
