#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PUBLIC_DIR="${REPOSITORY_ROOT}/site/public"

if [[ ! -d "${PUBLIC_DIR}" ]]; then
    echo "Site output directory does not exist: ${PUBLIC_DIR}" >&2
    echo "Run scripts/build-site.sh first." >&2
    exit 1
fi

status=0

resolve_target() {
    local source_file="$1"
    local url="$2"
    local path="${url%%#*}"
    path="${path%%\?*}"

    if [[ -z "${path}" ]]; then
        return 0
    fi

    case "${path}" in
        http://*|https://*|mailto:*|tel:*)
            return 0
            ;;
        /livereload.js)
            return 0
            ;;
    esac

    local target
    if [[ "${path}" == /* ]]; then
        target="${PUBLIC_DIR}${path}"
    else
        target="$(dirname "${source_file}")/${path}"
    fi

    target="$(realpath -m "${target}")"

    if [[ "${path}" == */ ]]; then
        target="${target}/index.html"
    elif [[ -d "${target}" ]]; then
        target="${target}/index.html"
    fi

    if [[ ! -e "${target}" ]]; then
        echo "Broken site link: ${source_file#${REPOSITORY_ROOT}/} -> ${url}" >&2
        status=1
    fi
}

while IFS= read -r -d '' file; do
    while IFS= read -r match; do
        url="${match#*=}"
        url="${url#\"}"
        url="${url%\"}"
        resolve_target "${file}" "${url}"
    done < <(grep -Eo '(href|src)="[^"]+"' "${file}" || true)
done < <(find "${PUBLIC_DIR}" -name '*.html' -print0)

while IFS= read -r -d '' file; do
    if grep -Eq '&amp;#(34|39);' "${file}"; then
        echo "Escaped quote entity leaked into site output: ${file#${REPOSITORY_ROOT}/}" >&2
        status=1
    fi

    if grep -Eq '<pre><code class="language-(stark|toml)">&#39;</code></pre>' "${file}"; then
        echo "Embedded code sample collapsed to a lone quote: ${file#${REPOSITORY_ROOT}/}" >&2
        status=1
    fi
done < <(find "${PUBLIC_DIR}" -name '*.html' -print0)

exit "${status}"
