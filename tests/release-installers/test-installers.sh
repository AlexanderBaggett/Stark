#!/bin/sh

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
REPOSITORY_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd -P)
INSTALLER_ROOT="$REPOSITORY_ROOT/scripts/release-installers"
TEMP_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/stark-installer-tests.XXXXXX")
TEMP_ROOT=$(CDPATH= cd -- "$TEMP_ROOT" && pwd -P)

cleanup() {
    rm -rf "$TEMP_ROOT"
}
trap cleanup EXIT HUP INT TERM

fail() {
    printf 'FAIL: %s\n' "$*" >&2
    exit 1
}

assert_file() {
    [ -f "$1" ] || fail "expected file '$1'"
}

assert_not_exists() {
    [ ! -e "$1" ] && [ ! -L "$1" ] || fail "expected '$1' not to exist"
}

assert_contains() {
    grep -F "$2" "$1" >/dev/null 2>&1 || fail "expected '$1' to contain '$2'"
}

fixture_hash_file() {
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        sha256sum "$1" | awk '{print $1}'
    fi
}

case "$(uname -s)" in
    Darwin) HOST_OS=macos ;;
    Linux) HOST_OS=linux ;;
    *) fail "Unix installer tests require macOS or Linux" ;;
esac
case "$(uname -m)" in
    x86_64|amd64) HOST_ARCH=x64; WRONG_ARCH=arm64 ;;
    arm64|aarch64) HOST_ARCH=arm64; WRONG_ARCH=x64 ;;
    *) fail "unsupported test architecture '$(uname -m)'" ;;
esac
HOST_ASSET="$HOST_OS-$HOST_ARCH"

make_archive() {
    archive_root=$1
    version=$2
    asset_suffix=$3
    mkdir -p "$archive_root/bin" "$archive_root/vendor/dist/$asset_suffix"
    cp "$INSTALLER_ROOT/install.sh" "$archive_root/install.sh"
    cp "$INSTALLER_ROOT/uninstall.sh" "$archive_root/uninstall.sh"
    chmod +x "$archive_root/install.sh" "$archive_root/uninstall.sh"
    cat > "$archive_root/release.json" <<EOF
{
  "schemaVersion": 1,
  "starkVersion": "$version",
  "assetSuffix": "$asset_suffix"
}
EOF
    printf '%s\n' '{"schemaVersion":1}' > "$archive_root/sdk.json"
    cat > "$archive_root/bin/stark" <<'EOF'
#!/bin/sh
if [ "$#" -eq 2 ] && [ "$1" = doctor ] && [ "$2" = --strict ]; then
    exit 0
fi
printf 'unexpected fake Stark invocation: %s\n' "$*" >&2
exit 64
EOF
    chmod +x "$archive_root/bin/stark"
    printf '%s\n' 'fixture vendor payload' > "$archive_root/vendor/dist/$asset_suffix/payload.txt"
    ln "$archive_root/vendor/dist/$asset_suffix/payload.txt" "$archive_root/vendor/dist/$asset_suffix/payload-hardlink.txt"
    ln -s payload.txt "$archive_root/vendor/dist/$asset_suffix/payload-symlink.txt"
    (
        cd "$archive_root"
        find . \( -type f -o -type l \) ! -path './release-files.sha256' -print | LC_ALL=C sort | while IFS= read -r file; do
            relative=${file#./}
            printf '%s  %s\n' "$(fixture_hash_file "$file")" "$relative"
        done
    ) > "$archive_root/release-files.sha256"
}

run_with_home() {
    test_home=$1
    shift
    HOME="$test_home" XDG_DATA_HOME="$test_home/.local/share" SHELL=/bin/zsh PATH=/usr/bin:/bin "$@"
}

# Parse both Unix scripts with the system's portable shell.
/bin/sh -n "$INSTALLER_ROOT/install.sh"
/bin/sh -n "$INSTALLER_ROOT/uninstall.sh"

HOME_ONE="$TEMP_ROOT/home-one"
ARCHIVE_ONE="$TEMP_ROOT/archive-one"
mkdir -p "$HOME_ONE"
printf '%s\n' '# existing profile content' > "$HOME_ONE/.zshrc"
make_archive "$ARCHIVE_ONE" v1.2.3 "$HOST_ASSET"

run_with_home "$HOME_ONE" /bin/sh "$ARCHIVE_ONE/install.sh" --non-interactive --archive-sha256 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
if [ "$HOST_OS" = macos ]; then
    DEFAULT_PREFIX="$HOME_ONE/Library/Application Support/Stark/versions/v1.2.3"
else
    DEFAULT_PREFIX="$HOME_ONE/.local/share/stark/versions/v1.2.3"
fi
assert_file "$DEFAULT_PREFIX/bin/stark"
assert_file "$DEFAULT_PREFIX/.stark-install-receipt"
assert_contains "$DEFAULT_PREFIX/.stark-install-receipt" 'source_archive_sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
assert_contains "$DEFAULT_PREFIX/.stark-install-receipt" 'source_content_manifest_sha256='
[ -L "$HOME_ONE/.local/bin/stark" ] || fail "default install did not create the Stark command link"
[ "$(readlink "$HOME_ONE/.local/bin/stark")" = "$DEFAULT_PREFIX/bin/stark" ] || fail "command link selected the wrong SDK"
[ -L "$DEFAULT_PREFIX/vendor/dist/$HOST_ASSET/payload-symlink.txt" ] || fail "install did not preserve an SDK symbolic link"
[ "$(ls -di "$DEFAULT_PREFIX/vendor/dist/$HOST_ASSET/payload.txt" | awk '{print $1}')" = "$(ls -di "$DEFAULT_PREFIX/vendor/dist/$HOST_ASSET/payload-hardlink.txt" | awk '{print $1}')" ] || fail "install did not preserve an SDK hard link"
[ "$(grep -F -c '# >>> stark sdk >>>' "$HOME_ONE/.zshrc")" -eq 1 ] || fail "PATH block was not added exactly once"
if command -v zsh >/dev/null 2>&1; then
    fresh_zsh_command=$(HOME="$HOME_ONE" ZDOTDIR="$HOME_ONE" SHELL=/bin/zsh PATH=/usr/bin:/bin zsh -lic 'command -v stark')
    [ "$fresh_zsh_command" = "$HOME_ONE/.local/bin/stark" ] || fail "fresh login/interactive Zsh did not resolve the installed Stark command"
    HOME="$HOME_ONE" ZDOTDIR="$HOME_ONE" SHELL=/bin/zsh PATH=/usr/bin:/bin zsh -lic 'stark doctor --strict' || fail "fresh login/interactive Zsh could not run the installed Stark command"
fi

if run_with_home "$HOME_ONE" /bin/sh "$ARCHIVE_ONE/install.sh" >"$TEMP_ROOT/repeat.out" 2>&1; then
    fail "repeated install without --repair unexpectedly succeeded"
fi
assert_contains "$TEMP_ROOT/repeat.out" 'use --repair or --force'

run_with_home "$HOME_ONE" /bin/sh "$ARCHIVE_ONE/install.sh" --repair --non-interactive
[ "$(grep -F -c '# >>> stark sdk >>>' "$HOME_ONE/.zshrc")" -eq 1 ] || fail "repair duplicated the PATH block"

# Uninstall removes inventory-owned files but preserves unrelated additions.
printf '%s\n' 'keep me' > "$DEFAULT_PREFIX/user-file.txt"
mkdir "$DEFAULT_PREFIX/user-empty-directory"
run_with_home "$HOME_ONE" /bin/sh "$DEFAULT_PREFIX/uninstall.sh" --non-interactive
assert_file "$DEFAULT_PREFIX/user-file.txt"
[ -d "$DEFAULT_PREFIX/user-empty-directory" ] || fail "uninstall removed an unrelated empty directory"
assert_not_exists "$DEFAULT_PREFIX/bin/stark"
assert_not_exists "$DEFAULT_PREFIX/.stark-install-receipt"
assert_not_exists "$HOME_ONE/.local/bin/stark"
[ "$(grep -F -c '# >>> stark sdk >>>' "$HOME_ONE/.zshrc" || true)" -eq 0 ] || fail "uninstall left its PATH block behind"

# Custom prefix, spaces, --no-path, and dry-run are side-effect bounded.
HOME_TWO="$TEMP_ROOT/home-two"
ARCHIVE_TWO="$TEMP_ROOT/archive-two"
CUSTOM_PREFIX="$TEMP_ROOT/custom SDK/v2"
DRY_PREFIX="$TEMP_ROOT/dry-run SDK"
mkdir -p "$HOME_TWO"
make_archive "$ARCHIVE_TWO" v2.0.0 "$HOST_ASSET"
run_with_home "$HOME_TWO" /bin/sh "$ARCHIVE_TWO/install.sh" --prefix "$DRY_PREFIX" --no-path --dry-run
assert_not_exists "$DRY_PREFIX"
run_with_home "$HOME_TWO" /bin/sh "$ARCHIVE_TWO/install.sh" --prefix "$CUSTOM_PREFIX" --no-path --non-interactive
assert_file "$CUSTOM_PREFIX/bin/stark"
assert_not_exists "$HOME_TWO/.local/bin/stark"
run_with_home "$HOME_TWO" /bin/sh "$CUSTOM_PREFIX/uninstall.sh" --prefix "$CUSTOM_PREFIX"
assert_not_exists "$CUSTOM_PREFIX"

# The wrong archive is rejected before destination creation.
HOME_THREE="$TEMP_ROOT/home-three"
ARCHIVE_WRONG="$TEMP_ROOT/archive-wrong"
WRONG_PREFIX="$TEMP_ROOT/wrong-install"
mkdir -p "$HOME_THREE"
make_archive "$ARCHIVE_WRONG" v3.0.0 "$HOST_OS-$WRONG_ARCH"
if run_with_home "$HOME_THREE" /bin/sh "$ARCHIVE_WRONG/install.sh" --prefix "$WRONG_PREFIX" --no-path >"$TEMP_ROOT/wrong.out" 2>&1; then
    fail "wrong-architecture installer unexpectedly succeeded"
fi
assert_contains "$TEMP_ROOT/wrong.out" "this host needs '$HOST_ASSET'"
assert_not_exists "$WRONG_PREFIX"

# An unrelated command link/file is detected and never overwritten.
HOME_FOUR="$TEMP_ROOT/home-four"
ARCHIVE_FOUR="$TEMP_ROOT/archive-four"
CONFLICT_PREFIX="$TEMP_ROOT/conflict-install"
mkdir -p "$HOME_FOUR/.local/bin"
printf '%s\n' 'unrelated command' > "$HOME_FOUR/.local/bin/stark"
make_archive "$ARCHIVE_FOUR" v4.0.0 "$HOST_ASSET"
if run_with_home "$HOME_FOUR" /bin/sh "$ARCHIVE_FOUR/install.sh" --prefix "$CONFLICT_PREFIX" >"$TEMP_ROOT/conflict.out" 2>&1; then
    fail "installer overwrote an unrelated Stark command"
fi
assert_contains "$TEMP_ROOT/conflict.out" 'is not a Stark-managed symbolic link'
assert_contains "$HOME_FOUR/.local/bin/stark" 'unrelated command'
assert_not_exists "$CONFLICT_PREFIX"

# The content manifest is authoritative and is checked before any destination
# or PATH mutation.
HOME_FIVE="$TEMP_ROOT/home-five"
ARCHIVE_TAMPERED="$TEMP_ROOT/archive-tampered"
TAMPERED_PREFIX="$TEMP_ROOT/tampered-install"
mkdir -p "$HOME_FIVE"
make_archive "$ARCHIVE_TAMPERED" v5.0.0 "$HOST_ASSET"
printf '%s\n' 'tampered' >> "$ARCHIVE_TAMPERED/vendor/dist/$HOST_ASSET/payload.txt"
if run_with_home "$HOME_FIVE" /bin/sh "$ARCHIVE_TAMPERED/install.sh" --prefix "$TAMPERED_PREFIX" --no-path >"$TEMP_ROOT/tampered.out" 2>&1; then
    fail "installer accepted a checksum-mismatched release file"
fi
assert_contains "$TEMP_ROOT/tampered.out" 'failed SHA-256 verification'
assert_not_exists "$TAMPERED_PREFIX"

HOME_SIX="$TEMP_ROOT/home-six"
ARCHIVE_UNSAFE="$TEMP_ROOT/archive-unsafe"
UNSAFE_PREFIX="$TEMP_ROOT/unsafe-install"
mkdir -p "$HOME_SIX"
make_archive "$ARCHIVE_UNSAFE" v6.0.0 "$HOST_ASSET"
printf '%s  %s\n' '0000000000000000000000000000000000000000000000000000000000000000' '../escape' >> "$ARCHIVE_UNSAFE/release-files.sha256"
if run_with_home "$HOME_SIX" /bin/sh "$ARCHIVE_UNSAFE/install.sh" --prefix "$UNSAFE_PREFIX" --no-path >"$TEMP_ROOT/unsafe.out" 2>&1; then
    fail "installer accepted an unsafe checksum-manifest path"
fi
assert_contains "$TEMP_ROOT/unsafe.out" "unsafe path '../escape'"
assert_not_exists "$UNSAFE_PREFIX"

HOME_SEVEN="$TEMP_ROOT/home-seven"
ARCHIVE_UNTRACKED="$TEMP_ROOT/archive-untracked"
UNTRACKED_PREFIX="$TEMP_ROOT/untracked-install"
mkdir -p "$HOME_SEVEN"
make_archive "$ARCHIVE_UNTRACKED" v7.0.0 "$HOST_ASSET"
printf '%s\n' 'not in release-files.sha256' > "$ARCHIVE_UNTRACKED/untracked.txt"
if run_with_home "$HOME_SEVEN" /bin/sh "$ARCHIVE_UNTRACKED/install.sh" --prefix "$UNTRACKED_PREFIX" --no-path >"$TEMP_ROOT/untracked.out" 2>&1; then
    fail "installer accepted a file absent from the checksum manifest"
fi
assert_contains "$TEMP_ROOT/untracked.out" "untracked file 'untracked.txt'"
assert_not_exists "$UNTRACKED_PREFIX"

# No release installer may acquire the bundled SDK payload from the network.
if grep -E 'Invoke-WebRequest|Start-BitsTransfer|Start-Process[[:space:]].*(winget|choco)|(^|[;&|[:space:]])(curl|wget)[[:space:]]' \
    "$INSTALLER_ROOT/install.sh" "$INSTALLER_ROOT/install.ps1" >/dev/null 2>&1; then
    fail "an installer contains a network acquisition command"
fi

# Parse PowerShell scripts when pwsh is available. macOS release development
# does not require PowerShell, so static contract checks remain the fallback.
if command -v pwsh >/dev/null 2>&1; then
    for powershell_script in "$INSTALLER_ROOT/install.ps1" "$INSTALLER_ROOT/uninstall.ps1" "$REPOSITORY_ROOT/scripts/stage-release-installers.ps1"; do
        pwsh -NoProfile -Command '& {
                param([string] $ScriptPath)
                $tokens = $null
                $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) {
                    $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }
                    exit 1
                }
            }' "$powershell_script"
    done
fi

assert_contains "$INSTALLER_ROOT/install.ps1" '[EnvironmentVariableTarget]::User'
assert_contains "$INSTALLER_ROOT/install.ps1" 'OSArchitecture'
assert_contains "$INSTALLER_ROOT/install.ps1" 'stark-install-receipt-v1'
assert_contains "$INSTALLER_ROOT/uninstall.ps1" 'InstalledFiles'
assert_contains "$REPOSITORY_ROOT/scripts/stage-release-installers.ps1" 'windows-arm64'

printf '%s\n' 'Release installer tests passed.'
