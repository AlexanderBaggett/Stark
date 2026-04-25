#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
OUTPUT_DIR="${REPOSITORY_ROOT}/site/static/reference"

copy_tree() {
    local source_dir="$1"
    local target_dir="$2"
    local relative
    local target

    mkdir -p "${target_dir}"

    while IFS= read -r -d '' source; do
        relative="${source#${source_dir}/}"
        target="${target_dir}/${relative}"
        mkdir -p "$(dirname "${target}")"
        cp "${source}" "${target}"
    done < <(find "${source_dir}" -type f \( \
        -name '*.md' -o \
        -name '*.stark' -o \
        -name '*.toml' -o \
        -name '*.c' -o \
        -name '*.args' \
    \) -print0)
}

copy_tree "${REPOSITORY_ROOT}/docs" "${OUTPUT_DIR}/docs"
copy_tree "${REPOSITORY_ROOT}/examples" "${OUTPUT_DIR}/examples"

echo "Exported reference sources to ${OUTPUT_DIR#${REPOSITORY_ROOT}/}"
