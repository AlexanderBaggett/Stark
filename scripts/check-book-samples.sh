#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
BOOK_DIR="${REPOSITORY_ROOT}/site/content/book"
STDLIB_DIR="${REPOSITORY_ROOT}/stdlib/src"

while IFS= read -r -d '' sample; do
    echo "Checking ${sample#${REPOSITORY_ROOT}/}"
    dotnet run --project "${REPOSITORY_ROOT}/src" -- "${sample}" --check -I "${STDLIB_DIR}" >/dev/null
done < <(find "${BOOK_DIR}" -path '*/samples/*.stark' -print0 | sort -z)

while IFS= read -r -d '' sample; do
    echo "Checking rejected example ${sample#${REPOSITORY_ROOT}/}"
    if dotnet run --project "${REPOSITORY_ROOT}/src" -- "${sample}" --check -I "${STDLIB_DIR}" >/dev/null 2>&1; then
        echo "Expected ${sample#${REPOSITORY_ROOT}/} to be rejected, but it compiled."
        exit 1
    fi
done < <(find "${BOOK_DIR}" -path '*/rejected/*.stark' -print0 | sort -z)
