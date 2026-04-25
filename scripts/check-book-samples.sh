#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

while IFS= read -r -d '' sample; do
    echo "Checking ${sample#${REPOSITORY_ROOT}/}"
    dotnet run --project "${REPOSITORY_ROOT}/src" -- "${sample}" --check >/dev/null
done < <(find "${REPOSITORY_ROOT}/site/assets/book/samples" -name '*.stark' -print0 | sort -z)

while IFS= read -r -d '' sample; do
    echo "Checking stdlib sample ${sample#${REPOSITORY_ROOT}/}"
    dotnet run --project "${REPOSITORY_ROOT}/src" -- "${sample}" --check -I "${REPOSITORY_ROOT}/stdlib/src" >/dev/null
done < <(find "${REPOSITORY_ROOT}/site/assets/book/stdlib-samples" -name '*.stark' -print0 | sort -z)

while IFS= read -r -d '' sample; do
    echo "Checking rejected example ${sample#${REPOSITORY_ROOT}/}"
    if dotnet run --project "${REPOSITORY_ROOT}/src" -- "${sample}" --check >/dev/null 2>&1; then
        echo "Expected ${sample#${REPOSITORY_ROOT}/} to be rejected, but it compiled."
        exit 1
    fi
done < <(find "${REPOSITORY_ROOT}/site/assets/book/negative-samples" -name '*.stark' -print0 | sort -z)
