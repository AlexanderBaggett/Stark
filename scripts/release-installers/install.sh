#!/bin/sh

# Installs the complete Stark SDK from an extracted release archive. This
# script is intentionally self-contained and must be shipped at the archive
# root beside release.json and sdk.json.

set -eu

PROGRAM=${0##*/}
SOURCE_ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
NO_PATH=0
NON_INTERACTIVE=0
DRY_RUN=0
FORCE=0
REPAIR=0
PREFIX=
ARCHIVE_SHA256=not-provided
STAGE=
BACKUP=

usage() {
    cat <<'EOF'
Usage: ./install.sh [options]

Options:
  --prefix DIR          Install into DIR instead of the per-user default.
  --no-path             Do not create a command link or edit a shell profile.
  --non-interactive     Never prompt (currently the default behavior as well).
  --dry-run             Validate and print actions without changing files.
  --force               Replace an existing receipt-owned Stark installation.
  --repair              Reinstall the same receipt-owned Stark version.
  --archive-sha256 HASH Record the downloaded archive SHA-256 in the receipt.
  -h, --help            Show this help.

The installer never downloads Stark, System, Vendor, .NET, or LLVM payloads.
Host development prerequisites are diagnosed by `stark doctor --strict` and
must be obtained through the operating system's supported mechanism.
EOF
}

die() {
    printf '%s: %s\n' "$PROGRAM" "$*" >&2
    exit 1
}

note() {
    printf '%s\n' "$*"
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --prefix)
            [ "$#" -ge 2 ] || die "--prefix requires a directory"
            PREFIX=$2
            shift 2
            ;;
        --no-path)
            NO_PATH=1
            shift
            ;;
        --non-interactive)
            NON_INTERACTIVE=1
            shift
            ;;
        --dry-run)
            DRY_RUN=1
            shift
            ;;
        --force)
            FORCE=1
            shift
            ;;
        --repair)
            REPAIR=1
            shift
            ;;
        --archive-sha256)
            [ "$#" -ge 2 ] || die "--archive-sha256 requires a value"
            ARCHIVE_SHA256=$2
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "unknown option '$1' (use --help)"
            ;;
    esac
done

[ "$FORCE" -eq 0 ] || [ "$REPAIR" -eq 0 ] || die "--force and --repair are mutually exclusive"
[ -f "$SOURCE_ROOT/release.json" ] || die "release.json is missing; run this script from an extracted Stark release"
[ -f "$SOURCE_ROOT/sdk.json" ] || die "sdk.json is missing; the release archive is incomplete"
[ -f "$SOURCE_ROOT/bin/stark" ] || die "bin/stark is missing; this is not a Unix Stark release"

json_string() {
    key=$1
    case "$key" in
        starkVersion)
            sed -n 's/^[[:space:]]*"starkVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SOURCE_ROOT/release.json" | sed -n '1p'
            ;;
        assetSuffix)
            sed -n 's/^[[:space:]]*"assetSuffix"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SOURCE_ROOT/release.json" | sed -n '1p'
            ;;
        *)
            die "internal error: unsupported release.json field '$key'"
            ;;
    esac
}

VERSION=$(json_string starkVersion)
ASSET_SUFFIX=$(json_string assetSuffix)
[ -n "$VERSION" ] || die "release.json does not contain starkVersion"
[ -n "$ASSET_SUFFIX" ] || die "release.json does not contain assetSuffix"

case "$VERSION" in
    *[!A-Za-z0-9._+-]*|'') die "release version '$VERSION' is not a portable version identifier" ;;
esac
case "$ASSET_SUFFIX" in
    *[!A-Za-z0-9._+-]*|'') die "release asset suffix '$ASSET_SUFFIX' is invalid" ;;
esac
case "$ARCHIVE_SHA256" in
    not-provided) ;;
    *[!A-Fa-f0-9]*|'') die "--archive-sha256 must be a 64-character hexadecimal SHA-256" ;;
esac
if [ "$ARCHIVE_SHA256" != not-provided ] && [ "${#ARCHIVE_SHA256}" -ne 64 ]; then
    die "--archive-sha256 must be a 64-character hexadecimal SHA-256"
fi

HOST_OS=$(uname -s 2>/dev/null || true)
HOST_MACHINE=$(uname -m 2>/dev/null || true)
case "$HOST_OS" in
    Darwin) HOST_ASSET_OS=macos ;;
    Linux) HOST_ASSET_OS=linux ;;
    *) die "unsupported operating system '$HOST_OS'; use the archive for macOS or Linux" ;;
esac
case "$HOST_MACHINE" in
    x86_64|amd64) HOST_ASSET_ARCH=x64 ;;
    arm64|aarch64) HOST_ASSET_ARCH=arm64 ;;
    i386|i486|i586|i686|x86) die "32-bit hosts are not supported" ;;
    *) die "unsupported processor architecture '$HOST_MACHINE'" ;;
esac
EXPECTED_ASSET_SUFFIX="$HOST_ASSET_OS-$HOST_ASSET_ARCH"
[ "$ASSET_SUFFIX" = "$EXPECTED_ASSET_SUFFIX" ] || die "this is the '$ASSET_SUFFIX' archive, but this host needs '$EXPECTED_ASSET_SUFFIX'"

if [ -z "$PREFIX" ]; then
    if [ "$HOST_ASSET_OS" = macos ]; then
        PREFIX="$HOME/Library/Application Support/Stark/versions/$VERSION"
    else
        PREFIX="${XDG_DATA_HOME:-$HOME/.local/share}/stark/versions/$VERSION"
    fi
fi

case "$PREFIX" in
    /*) ;;
    *) PREFIX="$(pwd -P)/$PREFIX" ;;
esac
while [ "$PREFIX" != / ] && [ "${PREFIX%/}" != "$PREFIX" ]; do
    PREFIX=${PREFIX%/}
done
case "$PREFIX/" in
    *'/../'*|*'/./'*) die "install prefix must not contain '.' or '..' path segments" ;;
esac
[ "$PREFIX" != / ] || die "refusing to install over the filesystem root"
[ "$PREFIX" != "$HOME" ] || die "refusing to install over the home directory"
case "$PREFIX/" in
    "$SOURCE_ROOT/"*) die "install prefix must not be inside the extracted release" ;;
esac
case "$SOURCE_ROOT/" in
    "$PREFIX/"*) die "install prefix must not contain the extracted release" ;;
esac

RECEIPT_NAME=.stark-install-receipt
RECEIPT="$PREFIX/$RECEIPT_NAME"
COMMAND_BIN="$HOME/.local/bin"
COMMAND_LINK="$COMMAND_BIN/stark"
PATH_MARKER_BEGIN='# >>> stark sdk >>>'
PATH_MARKER_END='# <<< stark sdk <<<'
PATH_PROFILE=
PATH_BLOCK_ADDED=no
PATH_PROFILE_EXISTED=no
PATH_BACKUP=
PREVIOUS_COMMAND_TARGET=

read_receipt_value() {
    receipt=$1
    field=$2
    sed -n "s/^${field}=//p" "$receipt" | sed -n '1p'
}

assert_owned_prefix() {
    [ -f "$RECEIPT" ] || die "'$PREFIX' already exists and is not a receipt-owned Stark installation"
    receipt_prefix=$(read_receipt_value "$RECEIPT" prefix)
    receipt_version=$(read_receipt_value "$RECEIPT" version)
    [ "$receipt_prefix" = "$PREFIX" ] || die "the existing receipt does not own '$PREFIX'"
    if [ "$REPAIR" -eq 1 ] && [ "$receipt_version" != "$VERSION" ]; then
        die "--repair requires the same version (installed '$receipt_version', archive '$VERSION')"
    fi
}

if [ -e "$PREFIX" ] || [ -L "$PREFIX" ]; then
    [ "$FORCE" -eq 1 ] || [ "$REPAIR" -eq 1 ] || die "'$PREFIX' already exists; use --repair or --force only for a receipt-owned Stark installation"
    assert_owned_prefix
fi

if [ "$NO_PATH" -eq 0 ]; then
    if [ -e "$COMMAND_LINK" ] || [ -L "$COMMAND_LINK" ]; then
        [ -L "$COMMAND_LINK" ] || die "'$COMMAND_LINK' already exists and is not a Stark-managed symbolic link; use --no-path"
        PREVIOUS_COMMAND_TARGET=$(readlink "$COMMAND_LINK")
        previous_prefix=$(dirname -- "$(dirname -- "$PREVIOUS_COMMAND_TARGET")")
        [ -f "$previous_prefix/$RECEIPT_NAME" ] || die "'$COMMAND_LINK' is not owned by a receipted Stark installation; use --no-path"
    fi

    case ":${PATH-}:" in
        *":$COMMAND_BIN:"*) PATH_PROFILE= ;;
        *)
            case "${SHELL-}" in
                */zsh) PATH_PROFILE="$HOME/.zshrc" ;;
                */bash) PATH_PROFILE="$HOME/.bashrc" ;;
                *) PATH_PROFILE="$HOME/.profile" ;;
            esac
            if [ -f "$PATH_PROFILE" ]; then
                begin_count=$(grep -F -c "$PATH_MARKER_BEGIN" "$PATH_PROFILE" || true)
                end_count=$(grep -F -c "$PATH_MARKER_END" "$PATH_PROFILE" || true)
                if [ "$begin_count" -ne "$end_count" ] || [ "$begin_count" -gt 1 ]; then
                    die "'$PATH_PROFILE' contains a malformed Stark PATH block; repair it manually or use --no-path"
                fi
                if [ "$begin_count" -eq 0 ]; then
                    PATH_BLOCK_ADDED=yes
                    PATH_PROFILE_EXISTED=yes
                fi
            else
                PATH_BLOCK_ADDED=yes
            fi
            ;;
    esac
fi

hash_file() {
    file=$1
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$file" | awk '{print $1}'
    elif command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$file" | awk '{print $1}'
    elif command -v openssl >/dev/null 2>&1; then
        openssl dgst -sha256 "$file" | awk '{print $NF}'
    else
        die "no SHA-256 utility found (expected shasum, sha256sum, or openssl)"
    fi
}

verify_source_path_parents() {
    relative=$1
    parent=${relative%/*}
    [ "$parent" != "$relative" ] || return 0
    current=$SOURCE_ROOT
    old_ifs=$IFS
    IFS=/
    # shellcheck disable=SC2086
    set -- $parent
    IFS=$old_ifs
    for segment in "$@"; do
        current="$current/$segment"
        [ ! -L "$current" ] || return 1
    done
}

verify_release_file_checksums() {
    manifest="$SOURCE_ROOT/release-files.sha256"
    [ -f "$manifest" ] || die "release-files.sha256 is missing; the release archive is incomplete"
    [ ! -L "$manifest" ] || die "release-files.sha256 must not be a symbolic link"

    verified_paths=$(mktemp "${TMPDIR:-/tmp}/stark-verified-paths.XXXXXX")
    actual_paths=$(mktemp "${TMPDIR:-/tmp}/stark-actual-paths.XXXXXX")
    verification_error=
    verified_count=0
    if command -v shasum >/dev/null 2>&1; then
        checksum_checker=shasum
    elif command -v sha256sum >/dev/null 2>&1; then
        checksum_checker=sha256sum
    else
        checksum_checker=individual
    fi

    while IFS= read -r line || [ -n "$line" ]; do
        expected_hash=${line%%  *}
        relative=${line#"$expected_hash  "}
        if [ "$relative" = "$line" ] || [ "${#expected_hash}" -ne 64 ]; then
            verification_error="release-files.sha256 contains malformed line '$line'"
            break
        fi
        case "$expected_hash" in
            *[!0-9a-f]*) verification_error="release-files.sha256 contains invalid SHA-256 '$expected_hash'"; break ;;
        esac
        case "$relative" in
            ''|/*|*'\'*) verification_error="release-files.sha256 contains unsafe path '$relative'"; break ;;
        esac
        case "/$relative/" in
            *'/../'*|*'/./'*|*'//'*) verification_error="release-files.sha256 contains unsafe path '$relative'"; break ;;
        esac
        if [ "$relative" = release-files.sha256 ]; then
            verification_error="release-files.sha256 must not checksum itself"
            break
        fi
        if grep -F -x -e "$relative" "$verified_paths" >/dev/null 2>&1; then
            verification_error="release-files.sha256 contains duplicate path '$relative'"
            break
        fi
        if ! verify_source_path_parents "$relative"; then
            verification_error="release-files.sha256 path '$relative' traverses a symbolic-link directory"
            break
        fi

        source_file="$SOURCE_ROOT/$relative"
        if [ ! -f "$source_file" ]; then
            verification_error="release file '$relative' is missing"
            break
        fi
        if [ "$checksum_checker" = individual ]; then
            actual_hash=$(hash_file "$source_file")
            if [ "$actual_hash" != "$expected_hash" ]; then
                verification_error="release file '$relative' failed SHA-256 verification"
                break
            fi
        fi
        printf '%s\n' "$relative" >> "$verified_paths"
        verified_count=$((verified_count + 1))
    done < "$manifest"

    if [ -z "$verification_error" ] && [ "$verified_count" -eq 0 ]; then
        verification_error="release-files.sha256 contains no files"
    fi

    if [ -z "$verification_error" ] && [ "$checksum_checker" = shasum ]; then
        if ! (cd "$SOURCE_ROOT" && shasum -a 256 -c release-files.sha256 >/dev/null); then
            verification_error="one or more release files failed SHA-256 verification"
        fi
    elif [ -z "$verification_error" ] && [ "$checksum_checker" = sha256sum ]; then
        if ! (cd "$SOURCE_ROOT" && sha256sum -c release-files.sha256 >/dev/null); then
            verification_error="one or more release files failed SHA-256 verification"
        fi
    fi

    if [ -z "$verification_error" ]; then
        (
            cd "$SOURCE_ROOT"
            find . \( -type f -o -type l \) ! -path './release-files.sha256' -print | sed 's#^\./##'
        ) > "$actual_paths"
        while IFS= read -r relative || [ -n "$relative" ]; do
            case "$relative" in
                ''|/*|*'\'*) verification_error="release archive contains unsafe file path '$relative'"; break ;;
            esac
            case "/$relative/" in
                *'/../'*|*'/./'*|*'//'*) verification_error="release archive contains unsafe file path '$relative'"; break ;;
            esac
            if ! grep -F -x -e "$relative" "$verified_paths" >/dev/null 2>&1; then
                verification_error="release archive contains untracked file '$relative'"
                break
            fi
        done < "$actual_paths"
    fi

    rm -f "$verified_paths" "$actual_paths"
    [ -z "$verification_error" ] || die "$verification_error"
}

verify_release_file_checksums
SOURCE_RELEASE_SHA256=$(hash_file "$SOURCE_ROOT/release.json")
SOURCE_CONTENT_MANIFEST_SHA256=$(hash_file "$SOURCE_ROOT/release-files.sha256")
command -v tar >/dev/null 2>&1 || die "tar is required to preserve SDK modes, symbolic links, and hard links"

note "Stark $VERSION installer"
note "  source:      $SOURCE_ROOT"
note "  destination: $PREFIX"
note "  asset:       $ASSET_SUFFIX"
if [ "$NO_PATH" -eq 0 ]; then
    note "  command:     $COMMAND_LINK -> $PREFIX/bin/stark"
else
    note "  PATH:        unchanged (--no-path)"
fi

# This is both an integrity check of sdk.json-owned payloads and a host
# prerequisite check. It is deliberately done before any machine mutation.
if ! "$SOURCE_ROOT/bin/stark" doctor --strict; then
    die "archive preflight failed; repair the archive or the diagnosed host prerequisite before installing"
fi
# Ensure the verified compiler preflight did not alter the release tree before
# it becomes the source of the transactional copy.
verify_release_file_checksums

if [ "$DRY_RUN" -eq 1 ]; then
    note "Dry run complete; no files were changed."
    exit 0
fi

PARENT=$(dirname -- "$PREFIX")
mkdir -p "$PARENT"
PARENT=$(CDPATH= cd -- "$PARENT" && pwd -P)
PREFIX="$PARENT/$(basename -- "$PREFIX")"
RECEIPT="$PREFIX/$RECEIPT_NAME"
STAGE="$PARENT/.stark-install-$VERSION-$$"
BACKUP="$PARENT/.stark-backup-$VERSION-$$"
[ ! -e "$STAGE" ] && [ ! -L "$STAGE" ] || die "temporary install path '$STAGE' already exists"
[ ! -e "$BACKUP" ] && [ ! -L "$BACKUP" ] || die "temporary backup path '$BACKUP' already exists"

cleanup() {
    if [ -n "$STAGE" ] && { [ -e "$STAGE" ] || [ -L "$STAGE" ]; }; then
        rm -rf "$STAGE"
    fi
}
trap cleanup EXIT HUP INT TERM

mkdir "$STAGE"
if ! (cd "$SOURCE_ROOT" && tar -cf - .) | (cd "$STAGE" && tar -xpf -); then
    die "could not copy the SDK while preserving its filesystem layout"
fi
chmod +x "$STAGE/bin/stark" "$STAGE/install.sh" "$STAGE/uninstall.sh" 2>/dev/null || true

if [ "$PATH_BLOCK_ADDED" = yes ]; then
    PATH_BACKUP="$PATH_PROFILE.stark-backup.$(date +%Y%m%d%H%M%S).$$"
fi

{
    printf '%s\n' 'stark-install-receipt-v1'
    printf 'version=%s\n' "$VERSION"
    printf 'asset_suffix=%s\n' "$ASSET_SUFFIX"
    printf 'prefix=%s\n' "$PREFIX"
    printf 'source_archive_sha256=%s\n' "$ARCHIVE_SHA256"
    printf 'source_content_manifest_sha256=%s\n' "$SOURCE_CONTENT_MANIFEST_SHA256"
    printf 'source_release_json_sha256=%s\n' "$SOURCE_RELEASE_SHA256"
    printf 'command_link=%s\n' "$(if [ "$NO_PATH" -eq 0 ]; then printf '%s' "$COMMAND_LINK"; fi)"
    printf 'previous_command_target=%s\n' "$PREVIOUS_COMMAND_TARGET"
    printf 'path_profile=%s\n' "$PATH_PROFILE"
    printf 'path_block_added=%s\n' "$PATH_BLOCK_ADDED"
    printf 'path_profile_existed=%s\n' "$PATH_PROFILE_EXISTED"
    printf 'path_backup=%s\n' "$PATH_BACKUP"
    printf '%s\n' '[files]'
    (
        cd "$STAGE"
        find . \( -type f -o -type l \) ! -path "./$RECEIPT_NAME" -print | LC_ALL=C sort | sed 's#^\./##'
    )
    printf '%s\n' "$RECEIPT_NAME"
    printf '%s\n' '[directories]'
    (
        cd "$STAGE"
        find . -depth -type d ! -path . -print | sed 's#^\./##'
    )
} > "$STAGE/$RECEIPT_NAME"

had_previous=0
if [ -e "$PREFIX" ] || [ -L "$PREFIX" ]; then
    mv "$PREFIX" "$BACKUP"
    had_previous=1
fi

if ! mv "$STAGE" "$PREFIX"; then
    if [ "$had_previous" -eq 1 ]; then
        mv "$BACKUP" "$PREFIX" || true
    fi
    die "could not activate the staged SDK"
fi
STAGE=

rollback_install() {
    if [ -e "$PREFIX" ] || [ -L "$PREFIX" ]; then
        rm -rf "$PREFIX"
    fi
    if [ "$had_previous" -eq 1 ] && { [ -e "$BACKUP" ] || [ -L "$BACKUP" ]; }; then
        mv "$BACKUP" "$PREFIX"
    fi
}

restore_previous_command_link() {
    rm -f "$COMMAND_LINK"
    if [ -n "$PREVIOUS_COMMAND_TARGET" ]; then
        previous_prefix=$(dirname -- "$(dirname -- "$PREVIOUS_COMMAND_TARGET")")
        if [ -x "$PREVIOUS_COMMAND_TARGET" ] && [ -f "$previous_prefix/$RECEIPT_NAME" ]; then
            ln -s "$PREVIOUS_COMMAND_TARGET" "$COMMAND_LINK"
        fi
    fi
}

if ! "$PREFIX/bin/stark" doctor --strict; then
    rollback_install
    die "installed SDK validation failed; the previous installation was restored"
fi

if [ "$NO_PATH" -eq 0 ]; then
    if ! mkdir -p "$COMMAND_BIN"; then
        rollback_install
        die "could not create '$COMMAND_BIN'"
    fi
    command_temp="$COMMAND_LINK.tmp.$$"
    if ! ln -s "$PREFIX/bin/stark" "$command_temp"; then
        rollback_install
        die "could not create the Stark command link"
    fi
    if ! mv -f "$command_temp" "$COMMAND_LINK"; then
        rm -f "$command_temp"
        restore_previous_command_link
        rollback_install
        die "could not activate '$COMMAND_LINK'"
    fi

    if [ "$PATH_BLOCK_ADDED" = yes ]; then
        if [ -f "$PATH_PROFILE" ]; then
            if ! cp -p "$PATH_PROFILE" "$PATH_BACKUP"; then
                restore_previous_command_link
                rollback_install
                die "could not back up '$PATH_PROFILE'"
            fi
        fi
        if ! {
            umask 022
            {
                [ ! -s "$PATH_PROFILE" ] || printf '\n'
                printf '%s\n' "$PATH_MARKER_BEGIN"
                printf '%s\n' 'case ":$PATH:" in'
                printf '%s\n' '  *":$HOME/.local/bin:"*) ;;'
                printf '%s\n' '  *) export PATH="$HOME/.local/bin:$PATH" ;;'
                printf '%s\n' 'esac'
                printf '%s\n' "$PATH_MARKER_END"
            } >> "$PATH_PROFILE"
        }; then
            restore_previous_command_link
            if [ "$PATH_PROFILE_EXISTED" = yes ] && [ -f "$PATH_BACKUP" ]; then
                cp -p "$PATH_BACKUP" "$PATH_PROFILE" || true
            elif [ "$PATH_PROFILE_EXISTED" = no ]; then
                rm -f "$PATH_PROFILE"
            fi
            rollback_install
            die "could not update '$PATH_PROFILE'"
        fi
    fi
fi

if [ "$had_previous" -eq 1 ] && { [ -e "$BACKUP" ] || [ -L "$BACKUP" ]; }; then
    rm -rf "$BACKUP"
fi
BACKUP=
trap - EXIT HUP INT TERM

note "Installed Stark $VERSION in '$PREFIX'."
if [ "$NO_PATH" -eq 0 ]; then
    note "Open a new terminal, or run:"
    note "  export PATH=\"$COMMAND_BIN:\$PATH\""
    note "Then verify with: stark doctor --strict"
else
    note "PATH was not changed. Add '$PREFIX/bin' manually when desired."
fi

# Keep the accepted option visible to shell linters and document that no prompt
# behavior is hidden behind it.
: "$NON_INTERACTIVE"
