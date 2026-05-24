#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
OUTPUT_DIR="${REPOSITORY_ROOT}/site/static/book"
OUTPUT="${OUTPUT_DIR}/stark-book.md"

mkdir -p "${OUTPUT_DIR}"

printf '# The Stark Book\n\n' > "${OUTPUT}"
printf 'Exported from site/content/book for the v1.35 draft.\n\n' >> "${OUTPUT}"

resolve_sample_path() {
    local sample="$1"
    local chapter_dir="$2"
    local path="${chapter_dir}/${sample}"

    if [[ -f "${path}" ]]; then
        printf '%s\n' "${path}"
        return 0
    fi

    path="${REPOSITORY_ROOT}/site/${sample}"

    if [[ -f "${path}" ]]; then
        printf '%s\n' "${path}"
        return 0
    fi

    case "${sample}" in
        static/reference/docs/*)
            path="${REPOSITORY_ROOT}/docs/${sample#static/reference/docs/}"
            ;;
        static/reference/examples/*)
            path="${REPOSITORY_ROOT}/examples/${sample#static/reference/examples/}"
            ;;
    esac

    if [[ -f "${path}" ]]; then
        printf '%s\n' "${path}"
        return 0
    fi

    return 1
}

append_markdown() {
    local file="$1"
    local chapter_dir
    local in_frontmatter=0
    local frontmatter_seen=0
    local sample
    local sample_path
    local language

    chapter_dir="$(dirname "${file}")"

    while IFS= read -r line; do
        if [[ "${frontmatter_seen}" -eq 0 && "${line}" == "+++" ]]; then
            frontmatter_seen=1
            in_frontmatter=1
            continue
        fi

        if [[ "${in_frontmatter}" -eq 1 ]]; then
            if [[ "${line}" == "+++" ]]; then
                in_frontmatter=0
            fi
            continue
        fi

        if [[ "${line}" == "{{< file-sample "* ]]; then
            sample="${line#*\"}"
            sample="${sample%%\"*}"
            language="${line#*\"${sample}\"}"
            language="${language#*\"}"
            language="${language%%\"*}"
            if [[ -z "${language}" || "${language}" == "${line}" ]]; then
                language="text"
            fi

            if ! sample_path="$(resolve_sample_path "${sample}" "${chapter_dir}")"; then
                echo "Missing book sample for export: ${sample}" >&2
                exit 1
            fi

            printf '```%s\n' "${language}" >> "${OUTPUT}"
            cat "${sample_path}" >> "${OUTPUT}"
            printf '\n```\n' >> "${OUTPUT}"
            continue
        fi

        if [[ "${line}" == "{{< stark-sample "* ]]; then
            sample="${line#*\"}"
            sample="${sample%%\"*}"

            if ! sample_path="$(resolve_sample_path "${sample}" "${chapter_dir}")"; then
                echo "Missing book sample for export: ${sample}" >&2
                exit 1
            fi

            printf '```stark\n' >> "${OUTPUT}"
            cat "${sample_path}" >> "${OUTPUT}"
            printf '\n```\n' >> "${OUTPUT}"
            continue
        fi

        printf '%s\n' "${line}" >> "${OUTPUT}"
    done < "${file}"

    printf '\n' >> "${OUTPUT}"
}

append_markdown "${REPOSITORY_ROOT}/site/content/book/_index.md"

if [[ -f "${REPOSITORY_ROOT}/site/content/book/changes.md" ]]; then
    append_markdown "${REPOSITORY_ROOT}/site/content/book/changes.md"
fi

while IFS= read -r -d '' chapter; do
    append_markdown "${chapter}"
done < <(find "${REPOSITORY_ROOT}/site/content/book" -mindepth 2 -maxdepth 2 -path '*/[0-9][0-9]-*/index.md' -print0 | sort -z)

while IFS= read -r -d '' appendix; do
    append_markdown "${appendix}"
done < <(find "${REPOSITORY_ROOT}/site/content/book" -mindepth 2 -maxdepth 2 -path '*/appendix-*/index.md' -print0 | sort -z)

echo "Exported ${OUTPUT#${REPOSITORY_ROOT}/}"
