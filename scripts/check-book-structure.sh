#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
BOOK_DIR="${REPOSITORY_ROOT}/site/content/book"

shopt -s nullglob
chapters=("${BOOK_DIR}"/[0-9][0-9]-*/index.md)

status=0

fail() {
    echo "$1" >&2
    status=1
}

check_book_link() {
    local chapter="$1"
    local field="$2"
    local url="$3"
    local relative="${chapter#${REPOSITORY_ROOT}/}"
    local path="${url%%#*}"
    path="${path%%\?*}"

    if [[ "${path}" != /book/* ]]; then
        fail "Book chapter ${field} link should stay inside /book/: ${relative} -> ${url}"
        return
    fi

    local slug="${path#/book/}"
    slug="${slug%/}"

    local target
    if [[ -z "${slug}" ]]; then
        target="${BOOK_DIR}/_index.md"
    else
        target="${BOOK_DIR}/${slug}/index.md"
    fi

    if [[ ! -f "${target}" ]]; then
        fail "Book chapter ${field} link target does not exist: ${relative} -> ${url}"
    fi
}

if [[ "${#chapters[@]}" -eq 0 ]]; then
    fail "No numbered book chapters found in ${BOOK_DIR#${REPOSITORY_ROOT}/}."
fi

expected=1
for chapter in "${chapters[@]}"; do
    relative="${chapter#${REPOSITORY_ROOT}/}"
    file_name="$(basename "$(dirname "${chapter}")")"
    number="${file_name:0:2}"
    expected_number="$(printf "%02d" "${expected}")"

    if [[ "${number}" != "${expected_number}" ]]; then
        fail "Book chapter numbering is not contiguous: expected ${expected_number}, found ${relative}."
    fi

    if ! grep -q '^+++$' "${chapter}"; then
        fail "Book chapter is missing TOML front matter delimiters: ${relative}."
    fi

    for field in title weight book_part book_status; do
        if ! grep -q "^${field} = " "${chapter}"; then
            fail "Book chapter is missing front matter field '${field}': ${relative}."
        fi
    done

    if ! grep -q "^title = \"${expected}\\. " "${chapter}"; then
        fail "Book chapter title should start with '${expected}. ': ${relative}."
    fi

    if [[ "${expected}" -eq 1 ]] && grep -q '^prev = ' "${chapter}"; then
        fail "First book chapter should not have previous-chapter navigation: ${relative}."
    elif [[ "${expected}" -gt 1 ]] && ! grep -q '^prev = ' "${chapter}"; then
        fail "Book chapter is missing previous-chapter navigation: ${relative}."
    fi

    if ! grep -q '^next = ' "${chapter}"; then
        fail "Book chapter is missing next navigation: ${relative}."
    fi

    for field in prev next; do
        while IFS= read -r url; do
            check_book_link "${chapter}" "${field}" "${url}"
        done < <(sed -nE "s/^${field} = \"([^\"]+)\"$/\1/p" "${chapter}")
    done

    h1_count="$(grep -Ec '^# [^#]' "${chapter}" || true)"
    if [[ "${h1_count}" -ne 1 ]]; then
        fail "Book chapter should contain exactly one H1 heading, found ${h1_count}: ${relative}."
    fi

    steps=()
    while IFS= read -r step; do
        steps+=("${step}")
    done < <(grep -E '^## Step [0-9]+:' "${chapter}" || true)
    if [[ "${#steps[@]}" -lt 3 ]]; then
        fail "Book chapter should have at least three tutorial steps: ${relative}."
    fi

    step_number=1
    for step in "${steps[@]}"; do
        if [[ ! "${step}" =~ ^##\ Step\ ([0-9]+): ]]; then
            fail "Book chapter has a malformed step heading: ${relative}: ${step}"
            continue
        fi

        if [[ "${BASH_REMATCH[1]}" -ne "${step_number}" ]]; then
            fail "Book chapter step numbering is not contiguous at step ${step_number}: ${relative}."
            break
        fi

        step_number=$((step_number + 1))
    done

    example_count="$(grep -Ec '^```|\{\{< (stark-sample|file-sample) ' "${chapter}" || true)"
    if [[ "${example_count}" -lt 2 ]]; then
        fail "Book chapter should contain at least two examples or sample callouts: ${relative}."
    fi

    if grep -Eiq 'TODO|TBD|placeholder|future chapter|will cover|to be written|coming soon|planned' "${chapter}"; then
        fail "Book chapter contains planning or placeholder language: ${relative}."
    fi

    expected=$((expected + 1))
done

echo "Checked ${#chapters[@]} numbered book chapters."
exit "${status}"
