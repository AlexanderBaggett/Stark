#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
OUTPUT_DIR="${REPOSITORY_ROOT}/site/static/reference"
CONTENT_DIR="${REPOSITORY_ROOT}/site/content/reference"

toml_escape() {
    printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

copy_tree() {
    local source_dir="$1"
    local target_dir="$2"
    local relative
    local target

    mkdir -p "${target_dir}"

    while IFS= read -r -d '' source; do
        relative="${source#${source_dir}/}"
        if [[ "${relative}" == "Userfacing/UnsupportedFeatures.md" ]]; then
            continue
        fi

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

write_index() {
    local target="$1"
    local title="$2"
    local weight="$3"
    local body="$4"
    local hidden="${5:-false}"

    mkdir -p "$(dirname "${target}")"
    {
        printf '+++\n'
        printf 'title = "%s"\n' "$(toml_escape "${title}")"
        printf 'weight = %s\n' "${weight}"
        if [[ "${hidden}" == "true" ]]; then
            printf 'geekdocHidden = true\n'
        fi
        printf '+++\n\n'
        printf '%s\n' "${body}"
    } > "${target}"
}

rewrite_reference_links() {
    local section="$1"

    if [[ "${section}" == "language" ]]; then
        sed -E \
            -e 's%\]\((\./)?([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/language/\2/\3)%g' \
            -e 's%\]\(\.\./Userfacing/([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/language/\1/\2)%g' \
            -e 's%\]\(\.\./StandardLibrary/([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/standard-library/\1/\2)%g' \
            -e 's%\]\(\.\./Internals/Roadmap\.md(#[^)]+)?\)%](/roadmap/\1)%g' \
            -e 's%\]\(\.\./Internals/([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/internals/\1/\2)%g'
    elif [[ "${section}" == "internals" ]]; then
        sed -E \
            -e 's%\]\((\./)?Roadmap\.md(#[^)]+)?\)%](/roadmap/\2)%g' \
            -e 's%\]\((\./)?([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/internals/\2/\3)%g' \
            -e 's%\]\(\.\./Userfacing/([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/language/\1/\2)%g' \
            -e 's%\]\(\.\./StandardLibrary/StandardLibraryBaseline\.md(#[^)]+)?\)%](/reference/standard-library/StandardLibraryBaseline/\1)%g' \
            -e 's%\]\(\.\./StandardLibrary/([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/standard-library/\1/\2)%g' \
            -e 's%\]\(\.\./Internals/Roadmap\.md(#[^)]+)?\)%](/roadmap/\1)%g' \
            -e 's%\]\(\.\./Internals/([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/internals/\1/\2)%g'
    else
        sed -E \
            -e 's%\]\((\./)?StandardLibraryBaseline\.md(#[^)]+)?\)%](/reference/standard-library/StandardLibraryBaseline/\2)%g' \
            -e 's%\]\((\./)?([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/standard-library/\2/\3)%g' \
            -e 's%\]\(\.\./Userfacing/([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/language/\1/\2)%g' \
            -e 's%\]\(\.\./StandardLibrary/([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/standard-library/\1/\2)%g' \
            -e 's%\]\(\.\./Internals/Roadmap\.md(#[^)]+)?\)%](/roadmap/\1)%g' \
            -e 's%\]\(\.\./Internals/([A-Za-z0-9_.-]+)\.md(#[^)]+)?\)%](/reference/internals/\1/\2)%g'
    fi
}

write_doc_page() {
    local source="$1"
    local target="$2"
    local weight="$3"
    local section="$4"
    local hidden="${5:-false}"
    local title

    title="$(awk '/^# / { sub(/^# /, ""); print; exit }' "${source}")"
    if [[ -z "${title}" ]]; then
        title="$(basename "${source}" .md)"
    fi

    mkdir -p "$(dirname "${target}")"
    {
        printf '+++\n'
        printf 'title = "%s"\n' "$(toml_escape "${title}")"
        printf 'weight = %s\n' "${weight}"
        if [[ "${hidden}" == "true" ]]; then
            printf 'geekdocHidden = true\n'
        fi
        printf '+++\n\n'
        awk 'NR == 1 && /^# / { next } { print }' "${source}" | rewrite_reference_links "${section}"
    } > "${target}"
}

rm -rf "${OUTPUT_DIR}" "${CONTENT_DIR}"

copy_tree "${REPOSITORY_ROOT}/examples" "${OUTPUT_DIR}/examples"
copy_tree "${REPOSITORY_ROOT}/benchmarks" "${OUTPUT_DIR}/benchmarks"

write_index \
    "${CONTENT_DIR}/_index.md" \
    "Reference" \
    "30" \
    "Rendered language and standard-library reference material generated from the repository docs."

write_index \
    "${CONTENT_DIR}/language/_index.md" \
    "Language Reference" \
    "10" \
    "The source-facing language rules and project model for Stark programmers."

write_index \
    "${CONTENT_DIR}/standard-library/_index.md" \
    "Standard Library Reference" \
    "20" \
    "The public module surface for Stark's current standard library."

write_index \
    "${CONTENT_DIR}/internals/_index.md" \
    "Internals" \
    "30" \
    "Rendered internal design notes linked from public reference pages." \
    "true"

weight=10
for source in \
    "${REPOSITORY_ROOT}/docs/Userfacing/LanguageReference.md" \
    "${REPOSITORY_ROOT}/docs/Userfacing/BorrowerSystem.md" \
    "${REPOSITORY_ROOT}/docs/Userfacing/ModulesAndVisibility.md" \
    "${REPOSITORY_ROOT}/docs/Userfacing/ProjectsAndSolutions.md" \
    "${REPOSITORY_ROOT}/docs/Userfacing/general-idea.md"; do
    target="${CONTENT_DIR}/language/$(basename "${source}")"
    write_doc_page "${source}" "${target}" "${weight}" "language"
    weight=$((weight + 10))
done

weight=10
for source in \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/StandardLibrary.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/StandardLibraryBaseline.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.BitOperations.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Console.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Text.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.IO.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.IO.File.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.IO.Path.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.FileSystem.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Memory.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Collections.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Threading.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Net.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Net.Tcp.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Runtime.Buffer.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Math.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Testing.md" \
    "${REPOSITORY_ROOT}/docs/StandardLibrary/System.Process.md"; do
    target="${CONTENT_DIR}/standard-library/$(basename "${source}")"
    write_doc_page "${source}" "${target}" "${weight}" "standard-library"
    weight=$((weight + 10))
done

weight=10
for source in \
    "${REPOSITORY_ROOT}/docs/Internals/ASMFunctionApproach.md" \
    "${REPOSITORY_ROOT}/docs/Internals/CompilerLoggingDesign.md" \
    "${REPOSITORY_ROOT}/docs/Internals/CompilerPipeline.md" \
    "${REPOSITORY_ROOT}/docs/Internals/DynamicMemoryAllocation.md" \
    "${REPOSITORY_ROOT}/docs/Internals/IntegerRangeEndpointExpressions.md" \
    "${REPOSITORY_ROOT}/docs/Internals/LanguageInternals.md" \
    "${REPOSITORY_ROOT}/docs/Internals/OptimizationPasses.md" \
    "${REPOSITORY_ROOT}/docs/Internals/PackageImage.md" \
    "${REPOSITORY_ROOT}/docs/Internals/StyleGuide.md" \
    "${REPOSITORY_ROOT}/docs/Internals/Website.md"; do
    target="${CONTENT_DIR}/internals/$(basename "${source}")"
    write_doc_page "${source}" "${target}" "${weight}" "internals" "true"
    weight=$((weight + 10))
done

echo "Exported reference sources to ${OUTPUT_DIR#${REPOSITORY_ROOT}/}"
echo "Generated rendered reference pages in ${CONTENT_DIR#${REPOSITORY_ROOT}/}"
