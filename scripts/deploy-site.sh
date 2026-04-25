#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

: "${STARK_SITE_HOST:?Set STARK_SITE_HOST to the deployment host.}"
: "${STARK_SITE_USER:?Set STARK_SITE_USER to the SSH user.}"
: "${STARK_SITE_REMOTE_DIR:?Set STARK_SITE_REMOTE_DIR to the remote public directory.}"

SSH_PORT="${STARK_SITE_SSH_PORT:-22}"
PUBLIC_DIR="${REPOSITORY_ROOT}/site/public/"

"${REPOSITORY_ROOT}/scripts/build-site.sh"

rsync \
    --archive \
    --compress \
    --delete \
    --human-readable \
    -e "ssh -p ${SSH_PORT}" \
    "${PUBLIC_DIR}" \
    "${STARK_SITE_USER}@${STARK_SITE_HOST}:${STARK_SITE_REMOTE_DIR}/"
