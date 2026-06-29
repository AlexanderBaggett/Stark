#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BINDING_DIR="$ROOT_DIR/selfhost/Compiler/Binding"

DATA_MODULES=(
  Diagnostics
  Declarations
  Scopes
  References
  TypeResolution
  ExpressionTypeHelpers
  GenericUseSites
  ModuleResolution
  DeclarationPrelude
  TokenHelpers
  AttributeHelpers
  FfiAbiHelpers
  LayoutHelpers
  LayoutTypeInfo
  TypedModuleSymbols
  TypedDeclarations
  TypedMembers
  TypedLocals
  TypedGenerics
  TypedGenericInstantiations
  CallableCandidates
  ReceiverCandidates
  FunctionEffects
  TraitConformance
  AssociatedTypes
  Copyability
  ThreadSafety
  SignatureHelpers
  ReceiverTokenHelpers
)

FORBIDDEN_IMPORT='^[[:space:]]*import[[:space:]]+Compiler\.Binding\.(LinkNameValidation|InlineLayoutValidation|EnumValidation|ExpressionValidation|LayoutValidation|CLayoutAggregates|CAbiBoundaries|ExportedSurfaces|ControlFlowValidation|RecursionValidation|BecomeValidation|LawValidation|FunctionKindValidation|DestructorValidation|ConstructorValidation|Ownership[A-Za-z]*|AssemblyBinding|BindingPipeline)([[:space:]]|$)'

status=0
for module in "${DATA_MODULES[@]}"; do
  path="$BINDING_DIR/$module.stark"
  if [[ ! -f "$path" ]]; then
    echo "missing Binding data module: $path" >&2
    status=1
    continue
  fi

  matches="$(grep -nE "$FORBIDDEN_IMPORT" "$path" || true)"
  if [[ -n "$matches" ]]; then
    echo "forbidden validation/pipeline import in $path:" >&2
    echo "$matches" >&2
    status=1
  fi
done

exit "$status"
