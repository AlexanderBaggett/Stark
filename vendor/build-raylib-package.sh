#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
vendor_dist="${script_dir}/dist"
compiler_path="${STARK_COMPILER:-${repo_root}/stark}"

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

if [[ -x "${compiler_path}" ]]; then
  compiler_cmd=("${compiler_path}")
else
  compiler_cmd=(dotnet run --project "${repo_root}/src/compiler.csproj" --)
fi

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
compiler_target_args=(--target "${target_triple}")

mkdir -p "${target_dist}"

bundled_raylib_dir="${target_dist}/native/raylib"

if is_macos_target \
  && [[ -f "${bundled_raylib_dir}/raylib.h" ]] \
  && [[ -f "${bundled_raylib_dir}/libraylib.a" ]]; then
  native_args=(
    --native-include-dir "${bundled_raylib_dir}"
    --native-library-dir "${bundled_raylib_dir}"
    --native-library raylib
    --native-link-arg -framework --native-link-arg CoreVideo
    --native-link-arg -framework --native-link-arg IOKit
    --native-link-arg -framework --native-link-arg Cocoa
    --native-link-arg -framework --native-link-arg GLUT
    --native-link-arg -framework --native-link-arg OpenGL
  )
elif [[ -n "${RAYLIB_SRC_DIR:-}" ]]; then
  if [[ ! -d "${RAYLIB_SRC_DIR}" ]]; then
    echo "RAYLIB_SRC_DIR must point to Raylib's src directory." >&2
    echo "Example: RAYLIB_SRC_DIR=/tmp/stark-raylib/raylib-6.0/src bash vendor/build-raylib-package.sh" >&2
    exit 1
  fi

  raylib_src_dir="$(cd "${RAYLIB_SRC_DIR}" && pwd)"

  if [[ ! -f "${raylib_src_dir}/raylib.h" ]]; then
    echo "RAYLIB_SRC_DIR does not look like a Raylib src directory because raylib.h was not found." >&2
    echo "Example: RAYLIB_SRC_DIR=/tmp/stark-raylib/raylib-6.0/src bash vendor/build-raylib-package.sh" >&2
    exit 1
  fi

  packaged_raylib_dir="${target_dist}/native/raylib"
  mkdir -p "${packaged_raylib_dir}"

  if [[ -f "${raylib_src_dir}/libraylib.a" ]]; then
    if [[ "${raylib_src_dir}/libraylib.a" != "${packaged_raylib_dir}/libraylib.a" ]]; then
      cp -f "${raylib_src_dir}/libraylib.a" "${packaged_raylib_dir}/libraylib.a"
    fi
  elif [[ -f "${raylib_src_dir}/libraylib.so" ]]; then
    if [[ "${raylib_src_dir}/libraylib.so" != "${packaged_raylib_dir}/libraylib.so" ]]; then
      cp -f "${raylib_src_dir}/libraylib.so" "${packaged_raylib_dir}/libraylib.so"
    fi
  else
    echo "RAYLIB_SRC_DIR must contain a built Raylib library (libraylib.a or libraylib.so)." >&2
    echo "Build Raylib first, then rerun: RAYLIB_SRC_DIR=${RAYLIB_SRC_DIR} bash vendor/build-raylib-package.sh" >&2
    exit 1
  fi

  if [[ "${raylib_src_dir}/raylib.h" != "${packaged_raylib_dir}/raylib.h" ]]; then
    cp -f "${raylib_src_dir}/raylib.h" "${packaged_raylib_dir}/raylib.h"
  fi
  if [[ "${raylib_src_dir}/raymath.h" != "${packaged_raylib_dir}/raymath.h" ]]; then
    cp -f "${raylib_src_dir}/raymath.h" "${packaged_raylib_dir}/raymath.h"
  fi
  if [[ "${raylib_src_dir}/rlgl.h" != "${packaged_raylib_dir}/rlgl.h" ]]; then
    cp -f "${raylib_src_dir}/rlgl.h" "${packaged_raylib_dir}/rlgl.h"
  fi

  if is_macos_target; then
    native_args=(
      --native-include-dir "${packaged_raylib_dir}"
      --native-library-dir "${packaged_raylib_dir}"
      --native-library raylib
      --native-link-arg -framework --native-link-arg CoreVideo
      --native-link-arg -framework --native-link-arg IOKit
      --native-link-arg -framework --native-link-arg Cocoa
      --native-link-arg -framework --native-link-arg GLUT
      --native-link-arg -framework --native-link-arg OpenGL
    )
  else
    native_args=(
      --native-include-dir "${packaged_raylib_dir}"
      --native-library-dir "${packaged_raylib_dir}"
      --native-library raylib
      --native-library GL
      --native-library m
      --native-library pthread
      --native-library dl
      --native-library rt
      --native-library X11
      --native-library Xrandr
      --native-library Xi
      --native-library Xcursor
      --native-library Xinerama
    )
  fi
else
  if ! command -v pkg-config >/dev/null 2>&1 || ! pkg-config --exists raylib; then
    echo "Raylib is not visible to pkg-config on this machine." >&2
    echo "Either use the bundled native payload under ${bundled_raylib_dir}, set PKG_CONFIG_PATH, or point RAYLIB_SRC_DIR at a local Raylib src directory." >&2
    echo "Example: RAYLIB_SRC_DIR=/tmp/stark-raylib/raylib-6.0/src bash vendor/build-raylib-package.sh" >&2
    exit 1
  fi

  native_args=(
    --native-pkg-config raylib
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/Raylib.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${target_dist}/libVendorRaylib.a" \
  "${compiler_target_args[@]}" \
  "${native_args[@]}"

echo "Built ${target_dist}/libVendorRaylib.a"
echo "Built ${target_dist}/libVendorRaylib.starkpkg"
