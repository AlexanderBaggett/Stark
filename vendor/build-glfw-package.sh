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

bundled_glfw_dir="${target_dist}/native/glfw"

if is_macos_target \
  && [[ -f "${bundled_glfw_dir}/GLFW/glfw3.h" ]] \
  && [[ -f "${bundled_glfw_dir}/libglfw3.a" ]]; then
  native_args=(
    --native-include-dir "${bundled_glfw_dir}"
    --native-library-dir "${bundled_glfw_dir}"
    --native-library glfw3
    --native-link-arg -framework --native-link-arg Cocoa
    --native-link-arg -framework --native-link-arg IOKit
    --native-link-arg -framework --native-link-arg CoreFoundation
  )
elif command -v pkg-config >/dev/null 2>&1 && pkg-config --exists glfw3; then
  native_args=(--native-pkg-config glfw3)
else
  if [[ -z "${GLFW_INCLUDE_DIR:-}" || -z "${GLFW_LIBRARY_DIR:-}" ]]; then
    echo "GLFW is not visible to pkg-config as glfw3." >&2
    echo "Either use the bundled native payload under ${bundled_glfw_dir}, set PKG_CONFIG_PATH, or set GLFW_INCLUDE_DIR and GLFW_LIBRARY_DIR." >&2
    echo "Example: GLFW_INCLUDE_DIR=/usr/include GLFW_LIBRARY_DIR=/usr/lib bash vendor/build-glfw-package.sh" >&2
    exit 1
  fi

  if [[ ! -f "${GLFW_INCLUDE_DIR}/GLFW/glfw3.h" ]]; then
    echo "GLFW_INCLUDE_DIR does not look like a GLFW include directory because GLFW/glfw3.h was not found." >&2
    exit 1
  fi

  native_args=(
    --native-include-dir "${GLFW_INCLUDE_DIR}"
    --native-library-dir "${GLFW_LIBRARY_DIR}"
    --native-library glfw
  )
fi

"${compiler_cmd[@]}" "${script_dir}/src/Vendor/GLFW.stark" \
  --emit-lib \
  -I "${script_dir}/src" \
  -I "${repo_root}/stdlib/src" \
  -o "${target_dist}/libVendorGLFW.a" \
  --target "${target_triple}" \
  --native-source "${script_dir}/GlfwEventBridge.c" \
  "${native_args[@]}"

echo "Built ${target_dist}/libVendorGLFW.a"
echo "Built ${target_dist}/libVendorGLFW.starkpkg"
