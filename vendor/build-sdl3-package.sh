#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
vendor_dist="${script_dir}/dist"
compiler_path="${STARK_COMPILER:-${repo_root}/stark}"

if [[ -x "${compiler_path}" ]]; then
  compiler_cmd=("${compiler_path}")
else
  compiler_cmd=(dotnet run --project "${repo_root}/src/compiler.csproj" --)
fi

detect_default_target_triple() {
  local doctor_output
  local doctor_triple

  if doctor_output="$("${compiler_cmd[@]}" doctor 2>/dev/null)"; then
    doctor_triple="$(printf '%s\n' "${doctor_output}" | sed -n 's/^  triple: //p' | head -n 1)"
    if [[ -n "${doctor_triple}" ]]; then
      printf '%s\n' "${doctor_triple}"
      return
    fi
  fi

  if command -v clang >/dev/null 2>&1; then
    clang -dumpmachine
    return
  fi

  local machine
  local system
  local os
  machine="$(uname -m)"
  system="$(uname -s)"

  case "${system}" in
    Linux)
      os="pc-linux-gnu"
      ;;
    Darwin)
      os="apple-darwin"
      ;;
    MINGW*|MSYS*|CYGWIN*)
      os="pc-windows-msvc"
      ;;
    *)
      echo "Unable to infer a target triple for ${system}. Set STARK_TARGET." >&2
      exit 1
      ;;
  esac

  printf '%s-%s\n' "${machine}" "${os}"
}

is_macos_target() {
  case "${target_triple}" in
    *apple-macos*|*apple-darwin*)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

cd "${repo_root}"
target_triple="${STARK_TARGET:-$(detect_default_target_triple)}"
target_dist="${vendor_dist}/${target_triple}"
mkdir -p "${target_dist}"

bundled_sdl3_dir="${target_dist}/native/sdl3"

if is_macos_target \
  && [[ -f "${bundled_sdl3_dir}/SDL3/SDL.h" ]] \
  && [[ -f "${bundled_sdl3_dir}/libSDL3.a" ]]; then
  native_args=(
    --native-include-dir "${bundled_sdl3_dir}"
    --native-library-dir "${bundled_sdl3_dir}"
    --native-library SDL3
    --native-library m
    --native-link-arg -lpthread
    --native-link-arg -framework --native-link-arg CoreMedia
    --native-link-arg -framework --native-link-arg CoreVideo
    --native-link-arg -framework --native-link-arg Cocoa
    --native-link-arg -weak_framework --native-link-arg UniformTypeIdentifiers
    --native-link-arg -framework --native-link-arg IOKit
    --native-link-arg -framework --native-link-arg ForceFeedback
    --native-link-arg -framework --native-link-arg Carbon
    --native-link-arg -framework --native-link-arg CoreAudio
    --native-link-arg -framework --native-link-arg AudioToolbox
    --native-link-arg -framework --native-link-arg AVFoundation
    --native-link-arg -framework --native-link-arg Foundation
    --native-link-arg -framework --native-link-arg GameController
    --native-link-arg -framework --native-link-arg Metal
    --native-link-arg -framework --native-link-arg QuartzCore
    --native-link-arg -weak_framework --native-link-arg CoreHaptics
  )
elif command -v pkg-config >/dev/null 2>&1 && pkg-config --exists sdl3; then
  native_args=(--native-pkg-config sdl3)
else
  if [[ -z "${SDL3_INCLUDE_DIR:-}" || -z "${SDL3_LIBRARY_DIR:-}" ]]; then
    echo "SDL3 is not visible to pkg-config as sdl3." >&2
    echo "Either use the bundled native payload under ${bundled_sdl3_dir}, set PKG_CONFIG_PATH, or set SDL3_INCLUDE_DIR and SDL3_LIBRARY_DIR." >&2
    echo "Example: SDL3_INCLUDE_DIR=/usr/include SDL3_LIBRARY_DIR=/usr/lib bash vendor/build-sdl3-package.sh" >&2
    exit 1
  fi

  if [[ ! -f "${SDL3_INCLUDE_DIR}/SDL3/SDL.h" ]]; then
    echo "SDL3_INCLUDE_DIR does not look like an SDL3 include directory because SDL3/SDL.h was not found." >&2
    exit 1
  fi

  native_args=(
    --native-include-dir "${SDL3_INCLUDE_DIR}"
    --native-library-dir "${SDL3_LIBRARY_DIR}"
    --native-library SDL3
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/SDL3.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${target_dist}/libVendorSDL3.a" \
  --target "${target_triple}" \
  --native-source "${script_dir}/Sdl3Binding.c" \
  "${native_args[@]}"

echo "Built ${target_dist}/libVendorSDL3.a"
echo "Built ${target_dist}/libVendorSDL3.starkpkg"
