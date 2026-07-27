#!/bin/sh

# Removes only files recorded by install.sh. Unrelated files placed below an
# SDK prefix are deliberately left in place, as is the now-nonempty prefix.

set -eu

PROGRAM=${0##*/}
SCRIPT_ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
PREFIX=
DRY_RUN=0
NON_INTERACTIVE=0
RECEIPT_NAME=.stark-install-receipt
PATH_MARKER_BEGIN='# >>> stark sdk >>>'
PATH_MARKER_END='# <<< stark sdk <<<'
RECEIPT_COPY=
FILE_LIST=
DIRECTORY_LIST=

usage() {
    cat <<'EOF'
Usage: ./uninstall.sh [options]

Options:
  --prefix DIR       Uninstall the receipt-owned SDK at DIR.
  --dry-run          Print actions without changing files.
  --non-interactive  Never prompt (currently the default behavior as well).
  -h, --help         Show this help.

When run from an installed SDK, the script selects that SDK automatically.
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
        --dry-run)
            DRY_RUN=1
            shift
            ;;
        --non-interactive)
            NON_INTERACTIVE=1
            shift
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

json_version() {
    sed -n 's/^[[:space:]]*"starkVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SCRIPT_ROOT/release.json" | sed -n '1p'
}

if [ -z "$PREFIX" ] && [ -f "$SCRIPT_ROOT/$RECEIPT_NAME" ]; then
    PREFIX=$SCRIPT_ROOT
elif [ -z "$PREFIX" ]; then
    [ -f "$SCRIPT_ROOT/release.json" ] || die "use --prefix when uninstall.sh is not inside a Stark release or installation"
    VERSION=$(json_version)
    [ -n "$VERSION" ] || die "release.json does not contain starkVersion"
    case "$(uname -s 2>/dev/null || true)" in
        Darwin) PREFIX="$HOME/Library/Application Support/Stark/versions/$VERSION" ;;
        Linux) PREFIX="${XDG_DATA_HOME:-$HOME/.local/share}/stark/versions/$VERSION" ;;
        *) die "unsupported operating system; specify --prefix explicitly" ;;
    esac
fi

case "$PREFIX" in
    /*) ;;
    *) PREFIX="$(pwd -P)/$PREFIX" ;;
esac
while [ "$PREFIX" != / ] && [ "${PREFIX%/}" != "$PREFIX" ]; do
    PREFIX=${PREFIX%/}
done
case "$PREFIX/" in
    *'/../'*|*'/./'*) die "uninstall prefix must not contain '.' or '..' path segments" ;;
esac
[ "$PREFIX" != / ] || die "refusing to operate on the filesystem root"
[ ! -L "$PREFIX" ] || die "refusing to uninstall through symbolic-link prefix '$PREFIX'"

RECEIPT="$PREFIX/$RECEIPT_NAME"
[ -f "$RECEIPT" ] || die "'$PREFIX' does not contain a Stark installation receipt"

read_receipt_value() {
    field=$1
    sed -n "s/^${field}=//p" "$RECEIPT" | sed -n '1p'
}

RECEIPT_PREFIX=$(read_receipt_value prefix)
VERSION=$(read_receipt_value version)
COMMAND_LINK=$(read_receipt_value command_link)
PREVIOUS_COMMAND_TARGET=$(read_receipt_value previous_command_target)
PATH_PROFILE=$(read_receipt_value path_profile)
PATH_BLOCK_ADDED=$(read_receipt_value path_block_added)

[ "$RECEIPT_PREFIX" = "$PREFIX" ] || die "the receipt does not own '$PREFIX'"
[ -n "$VERSION" ] || die "the receipt has no version"
case "$COMMAND_LINK" in
    ''|"$HOME/.local/bin/stark") ;;
    *) die "the receipt contains unsafe command-link path '$COMMAND_LINK'" ;;
esac
case "$PATH_PROFILE" in
    ''|"$HOME/.zshrc"|"$HOME/.bashrc"|"$HOME/.profile") ;;
    *) die "the receipt contains unsafe profile path '$PATH_PROFILE'" ;;
esac
case "$PATH_BLOCK_ADDED" in
    yes|no) ;;
    *) die "the receipt contains invalid path_block_added metadata" ;;
esac
case "$PREVIOUS_COMMAND_TARGET" in
    ''|/*/bin/stark) ;;
    *) die "the receipt contains unsafe previous command target '$PREVIOUS_COMMAND_TARGET'" ;;
esac

PROFILE_BEGIN_COUNT=0
PROFILE_END_COUNT=0
if [ -n "$PATH_PROFILE" ] && [ -f "$PATH_PROFILE" ]; then
    PROFILE_BEGIN_COUNT=$(grep -F -c "$PATH_MARKER_BEGIN" "$PATH_PROFILE" || true)
    PROFILE_END_COUNT=$(grep -F -c "$PATH_MARKER_END" "$PATH_PROFILE" || true)
    if [ "$PROFILE_BEGIN_COUNT" -ne "$PROFILE_END_COUNT" ] || [ "$PROFILE_BEGIN_COUNT" -gt 1 ]; then
        die "'$PATH_PROFILE' contains malformed Stark PATH markers; no files were removed"
    fi
fi

RECEIPT_COPY=$(mktemp "${TMPDIR:-/tmp}/stark-receipt.XXXXXX")
FILE_LIST=$(mktemp "${TMPDIR:-/tmp}/stark-files.XXXXXX")
DIRECTORY_LIST=$(mktemp "${TMPDIR:-/tmp}/stark-directories.XXXXXX")
cleanup() {
    [ -z "$RECEIPT_COPY" ] || rm -f "$RECEIPT_COPY"
    [ -z "$FILE_LIST" ] || rm -f "$FILE_LIST"
    [ -z "$DIRECTORY_LIST" ] || rm -f "$DIRECTORY_LIST"
}
trap cleanup EXIT HUP INT TERM
cp "$RECEIPT" "$RECEIPT_COPY"
sed -n '/^\[files\]$/,/^\[directories\]$/p' "$RECEIPT_COPY" | sed '1d;$d' > "$FILE_LIST"
sed -n '/^\[directories\]$/,$p' "$RECEIPT_COPY" | sed '1d' > "$DIRECTORY_LIST"
[ -s "$FILE_LIST" ] || die "the receipt has no installed-file inventory"

assert_safe_relative_path() {
    relative=$1
    case "$relative" in
        ''|/*) die "the receipt contains unsafe installed path '$relative'" ;;
    esac
    case "/$relative/" in
        *'/../'*|*'/./'*|*'//'*) die "the receipt contains unsafe installed path '$relative'" ;;
    esac
}

assert_no_symlink_parents() {
    relative=$1
    parent=${relative%/*}
    [ "$parent" != "$relative" ] || return 0
    current=$PREFIX
    old_ifs=$IFS
    IFS=/
    # shellcheck disable=SC2086
    set -- $parent
    IFS=$old_ifs
    for segment in "$@"; do
        current="$current/$segment"
        [ ! -L "$current" ] || die "refusing to traverse symbolic-link directory '$current'"
    done
}

while IFS= read -r relative || [ -n "$relative" ]; do
    assert_safe_relative_path "$relative"
    assert_no_symlink_parents "$relative"
done < "$FILE_LIST"
while IFS= read -r relative || [ -n "$relative" ]; do
    assert_safe_relative_path "$relative"
    assert_no_symlink_parents "$relative/receipt-owned-directory"
done < "$DIRECTORY_LIST"

note "Uninstalling Stark $VERSION from '$PREFIX'."
if [ "$DRY_RUN" -eq 1 ]; then
    while IFS= read -r relative || [ -n "$relative" ]; do
        note "Would remove: $PREFIX/$relative"
    done < "$FILE_LIST"
    while IFS= read -r relative || [ -n "$relative" ]; do
        note "Would remove if empty: $PREFIX/$relative"
    done < "$DIRECTORY_LIST"
    if [ -n "$COMMAND_LINK" ]; then
        note "Would remove or restore Stark command link: $COMMAND_LINK"
    fi
    if [ -n "$PATH_PROFILE" ]; then
        note "Would remove the Stark PATH block when no other installed Stark version uses it: $PATH_PROFILE"
    fi
    exit 0
fi

while IFS= read -r relative || [ -n "$relative" ]; do
    target="$PREFIX/$relative"
    if [ -e "$target" ] || [ -L "$target" ]; then
        rm -f "$target"
    fi
done < "$FILE_LIST"

link_still_used=0
if [ -n "$COMMAND_LINK" ] && [ -L "$COMMAND_LINK" ]; then
    current_target=$(readlink "$COMMAND_LINK")
    if [ "$current_target" = "$PREFIX/bin/stark" ]; then
        rm -f "$COMMAND_LINK"
        if [ -n "$PREVIOUS_COMMAND_TARGET" ]; then
            previous_prefix=$(dirname -- "$(dirname -- "$PREVIOUS_COMMAND_TARGET")")
            if [ -x "$PREVIOUS_COMMAND_TARGET" ] && [ -f "$previous_prefix/$RECEIPT_NAME" ]; then
                link_temp="$COMMAND_LINK.tmp.$$"
                ln -s "$PREVIOUS_COMMAND_TARGET" "$link_temp"
                mv -f "$link_temp" "$COMMAND_LINK"
                link_still_used=1
                note "Restored the previous Stark command target '$PREVIOUS_COMMAND_TARGET'."
            fi
        fi
    else
        link_still_used=1
        note "Left '$COMMAND_LINK' unchanged because it now selects another Stark installation."
    fi
elif [ -n "$COMMAND_LINK" ] && [ -e "$COMMAND_LINK" ]; then
    link_still_used=1
    note "Left non-symbolic-link command '$COMMAND_LINK' unchanged."
fi

if [ -n "$PATH_PROFILE" ] && [ -f "$PATH_PROFILE" ] && [ "$link_still_used" -eq 0 ]; then
    if [ "$PROFILE_BEGIN_COUNT" -eq 1 ] && [ "$PROFILE_END_COUNT" -eq 1 ]; then
        profile_temp=$(mktemp "${TMPDIR:-/tmp}/stark-profile.XXXXXX")
        if awk -v begin="$PATH_MARKER_BEGIN" -v end="$PATH_MARKER_END" '
            $0 == begin { inside = 1; next }
            $0 == end && inside { inside = 0; next }
            !inside { print }
            END { if (inside) exit 3 }
        ' "$PATH_PROFILE" > "$profile_temp"; then
            cat "$profile_temp" > "$PATH_PROFILE"
            rm -f "$profile_temp"
        else
            rm -f "$profile_temp"
            die "could not safely remove the PATH block from '$PATH_PROFILE'"
        fi
    fi
fi

# Remove only receipt-owned directories, deepest first, and only while empty.
# User-created files and directories therefore survive uninstall.
while IFS= read -r relative || [ -n "$relative" ]; do
    directory="$PREFIX/$relative"
    if [ -d "$directory" ] && [ ! -L "$directory" ]; then
        rmdir "$directory" 2>/dev/null || true
    fi
done < "$DIRECTORY_LIST"
rmdir "$PREFIX" 2>/dev/null || true

if [ -d "$PREFIX" ]; then
    note "Receipt-owned files were removed; '$PREFIX' remains because it contains unrelated files."
else
    note "Uninstalled Stark $VERSION."
fi

: "$NON_INTERACTIVE"
