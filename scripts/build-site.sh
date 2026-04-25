#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PINNED_VERSION="$(tr -d '[:space:]' < "${REPOSITORY_ROOT}/tools/hugo/VERSION")"
HUGO="${REPOSITORY_ROOT}/tools/hugo/hugo"
SITE_DIR="${REPOSITORY_ROOT}/site"
OUTPUT_DIR="${SITE_DIR}/public"

if [[ ! -x "${HUGO}" ]]; then
    echo "Pinned Hugo binary is missing or not executable: ${HUGO}" >&2
    echo "Install Hugo v${PINNED_VERSION} at tools/hugo/hugo before building the site." >&2
    exit 127
fi

VERSION_OUTPUT="$("${HUGO}" version)"
if [[ "${VERSION_OUTPUT}" != *"v${PINNED_VERSION}"* ]]; then
    echo "Pinned Hugo version mismatch." >&2
    echo "Expected: v${PINNED_VERSION}" >&2
    echo "Actual:   ${VERSION_OUTPUT}" >&2
    exit 1
fi

"${SCRIPT_DIR}/export-reference-sources.sh"
"${SCRIPT_DIR}/export-book.sh"
"${HUGO}" --source "${SITE_DIR}" --destination "${OUTPUT_DIR}" --cleanDestinationDir --minify
