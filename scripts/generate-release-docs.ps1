param(
    [Parameter(Mandatory = $true)]
    [string] $StageRoot,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $AssetSuffix,

    [Parameter(Mandatory = $true)]
    [string] $PackagedRuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [string] $PackagedTargetTriple,

    [Parameter(Mandatory = $true)]
    [string] $PackagedArchiveKind,

    [Parameter(Mandatory = $true)]
    [string] $PackagedLlvmVersion
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$stagePath = (Resolve-Path -LiteralPath $StageRoot -ErrorAction Stop).Path
$releaseDocumentationContractScript = Join-Path $PSScriptRoot "release-documentation-contract.ps1"
if (-not (Test-Path -LiteralPath $releaseDocumentationContractScript -PathType Leaf)) {
    throw "Release documentation contract helper '$releaseDocumentationContractScript' is missing."
}
. $releaseDocumentationContractScript

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)] [object] $Object,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $Context
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "$Context is missing required property '$Name'."
    }
    $value = [string]$property.Value
    if ($value -match '[\x00-\x1f]') {
        throw "$Context property '$Name' contains a control character."
    }
    return $value
}

function Get-DependencyProperty {
    param(
        [Parameter(Mandatory = $true)] [object] $Dependencies,
        [Parameter(Mandatory = $true)] [string] $Id,
        [Parameter(Mandatory = $true)] [string] $Name
    )

    $matches = @($Dependencies.dependencies | Where-Object {
        [string]::Equals([string]$_.id, $Id, [StringComparison]::Ordinal)
    })
    if ($matches.Count -ne 1) {
        throw "dependencies.json must contain exactly one '$Id' dependency."
    }
    return (Get-RequiredProperty -Object $matches[0] -Name $Name -Context "dependency '$Id'")
}

function Get-DependencyVersion {
    param(
        [Parameter(Mandatory = $true)] [object] $Dependencies,
        [Parameter(Mandatory = $true)] [string] $Id
    )

    return (Get-DependencyProperty -Dependencies $Dependencies -Id $Id -Name "version")
}

function Assert-ArchiveEntry {
    param(
        [Parameter(Mandatory = $true)] [object] $ArchiveContent,
        [Parameter(Mandatory = $true)] [string] $Id,
        [Parameter(Mandatory = $true)] [string] $Path
    )

    $matches = @($ArchiveContent.entries | Where-Object {
        [string]::Equals([string]$_.id, $Id, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$_.path, $Path, [StringComparison]::Ordinal)
    })
    if ($matches.Count -ne 1) {
        throw "archive-content.json must declare '$Id' at '$Path'."
    }
}

function Expand-Template {
    param(
        [Parameter(Mandatory = $true)] [string] $Template,
        [Parameter(Mandatory = $true)] [hashtable] $Values
    )

    $result = $Template
    for ($round = 0; $round -le $Values.Count; $round++) {
        $before = $result
        foreach ($key in $Values.Keys) {
            $result = $result.Replace("__$key`__", [string]$Values[$key])
        }
        if ([string]::Equals($before, $result, [StringComparison]::Ordinal)) {
            break
        }
    }
    if ($result -match '__[A-Z0-9_]+__') {
        throw "Release documentation template contains unresolved token '$($Matches[0])'."
    }
    return $result.Replace("`r`n", "`n").TrimEnd() + "`n"
}

$targetsPath = Join-Path $repositoryRoot "eng/release/targets.json"
$dependenciesPath = Join-Path $repositoryRoot "eng/release/dependencies.json"
$archiveContentPath = Join-Path $repositoryRoot "eng/release/archive-content.json"
foreach ($manifestPath in @($targetsPath, $dependenciesPath, $archiveContentPath)) {
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Required release manifest '$manifestPath' is missing."
    }
}

$targets = Get-Content -LiteralPath $targetsPath -Raw | ConvertFrom-Json
$dependencies = Get-Content -LiteralPath $dependenciesPath -Raw | ConvertFrom-Json
$archiveContent = Get-Content -LiteralPath $archiveContentPath -Raw | ConvertFrom-Json
$targetMatches = @($targets.targets | Where-Object {
    [string]::Equals([string]$_.assetSuffix, $AssetSuffix, [StringComparison]::Ordinal)
})
if ($targetMatches.Count -ne 1) {
    throw "targets.json must contain exactly one assetSuffix '$AssetSuffix'."
}
$target = $targetMatches[0]

$targetId = Get-RequiredProperty -Object $target -Name "id" -Context "target '$AssetSuffix'"
$operatingSystem = Get-RequiredProperty -Object $target -Name "operatingSystem" -Context "target '$AssetSuffix'"
$architecture = Get-RequiredProperty -Object $target -Name "architecture" -Context "target '$AssetSuffix'"
$runtimeIdentifier = Get-RequiredProperty -Object $target -Name "runtimeIdentifier" -Context "target '$AssetSuffix'"
$targetTriple = Get-RequiredProperty -Object $target -Name "targetTriple" -Context "target '$AssetSuffix'"
$archiveKind = Get-RequiredProperty -Object $target -Name "archiveKind" -Context "target '$AssetSuffix'"
$archiveExtension = Get-RequiredProperty -Object $target -Name "archiveExtension" -Context "target '$AssetSuffix'"
$minimumOs = Get-RequiredProperty -Object $target -Name "minimumOs" -Context "target '$AssetSuffix'"
$hostPrerequisite = Get-RequiredProperty -Object $target -Name "hostPrerequisite" -Context "target '$AssetSuffix'"
$installerKind = Get-RequiredProperty -Object $target -Name "installerKind" -Context "target '$AssetSuffix'"
$supportTier = Get-RequiredProperty -Object $target -Name "supportTier" -Context "target '$AssetSuffix'"
$minimumOsPolicyStatus = Get-RequiredProperty -Object $targets -Name "minimumOsPolicyStatus" -Context "targets.json"

if ($targetId -ne $AssetSuffix -or $architecture -notin @("x64", "arm64")) {
    throw "Target '$AssetSuffix' does not satisfy the stable 64-bit release identity contract."
}
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]*$') {
    throw "Version '$Version' is not a portable release identifier."
}
if ($operatingSystem -eq "windows") {
    if ($installerKind -ne "powershell" -or $archiveKind -ne "zip" -or $archiveExtension -ne ".zip") {
        throw "Windows target '$targetId' has inconsistent archive or installer metadata."
    }
    Assert-ArchiveEntry -ArchiveContent $archiveContent -Id "installer-windows" -Path "install.ps1"
    Assert-ArchiveEntry -ArchiveContent $archiveContent -Id "uninstaller-windows" -Path "uninstall.ps1"
} else {
    if ($installerKind -ne "posix-shell" -or $archiveKind -ne "targz" -or $archiveExtension -ne ".tar.gz") {
        throw "Unix target '$targetId' has inconsistent archive or installer metadata."
    }
    Assert-ArchiveEntry -ArchiveContent $archiveContent -Id "installer-unix" -Path "install.sh"
    Assert-ArchiveEntry -ArchiveContent $archiveContent -Id "uninstaller-unix" -Path "uninstall.sh"
}
Assert-ArchiveEntry -ArchiveContent $archiveContent -Id "readme" -Path "README.md"
Assert-ArchiveEntry -ArchiveContent $archiveContent -Id "install-manual" -Path "INSTALL.md"
Assert-ArchiveEntry -ArchiveContent $archiveContent -Id "release-command-contract" -Path "release-commands.json"
Assert-ArchiveEntry -ArchiveContent $archiveContent -Id "release-file-checksums" -Path "release-files.sha256"

$dotnetRuntimeVersion = Get-DependencyProperty `
    -Dependencies $dependencies `
    -Id "dotnet-stage0-runtime" `
    -Name "runtimeVersion"
$antlrVersion = Get-DependencyVersion -Dependencies $dependencies -Id "antlr4-runtime-standard"
$llvmVersion = Get-DependencyVersion -Dependencies $dependencies -Id "llvm-22.1.8"
if (-not [string]::Equals($PackagedRuntimeIdentifier, $runtimeIdentifier, [StringComparison]::Ordinal) -or
    -not [string]::Equals($PackagedTargetTriple, $targetTriple, [StringComparison]::Ordinal) -or
    -not [string]::Equals($PackagedArchiveKind, $archiveKind, [StringComparison]::Ordinal) -or
    -not [string]::Equals($PackagedLlvmVersion, $llvmVersion, [StringComparison]::Ordinal)) {
    throw "Packaging inputs for '$AssetSuffix' do not match the release target/dependency manifests."
}
$archiveBaseName = "stark-$Version-$AssetSuffix"
$archiveFileName = "$archiveBaseName$archiveExtension"
$archiveChecksumFileName = "$archiveFileName.sha256"
$compilerRelativePath = if ($operatingSystem -eq "windows") { "bin/stark.exe" } else { "bin/stark" }
$quickStartMarker = "stark-release-quick-start-v1"
$quickStartSteps = @(New-ReleaseDocumentationQuickStartSteps -TargetTriple $targetTriple)

if ($operatingSystem -eq "windows") {
    $archiveVerification = @'
```powershell
$expected = ((Get-Content -LiteralPath .\__ARCHIVE_CHECKSUM__ -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash -LiteralPath .\__ARCHIVE_FILE__ -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "Archive SHA-256 mismatch" }
```
'@
    $extractionCommands = @'
```powershell
Expand-Archive .\__ARCHIVE_FILE__ -DestinationPath .
Set-Location .\__ARCHIVE_BASE__
```
'@
    $contentVerification = @'
```powershell
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
Get-Content -LiteralPath .\release-files.sha256 | ForEach-Object {
    if ($_ -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Malformed release checksum line: $_" }
    $expected = $Matches[1]
    $relative = $Matches[2]
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or
        $relative -match '(^|/)\.\.?(/|$)' -or -not $seen.Add($relative)) {
        throw "Unsafe or duplicate release path: $relative"
    }
    $actual = (Get-FileHash -LiteralPath $relative -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "Release SHA-256 mismatch: $relative" }
}

# Optional no-change preflight also rejects untracked files and runs doctor:
.\install.ps1 -DryRun -NoPath -NonInteractive
```

The manual loop verifies every safe, unique path and SHA-256 in
`release-files.sha256`. The optional dry run additionally rejects untracked
files, runs `stark doctor --strict`, and makes no installation or PATH change.
'@
    $permanentPathInstructions = @'
Use the installer for a receipt-owned user PATH entry. For a manual install,
move the complete SDK to its final directory first. Then either:

- open **Edit environment variables for your account**, edit the user `Path`,
  and add the SDK's `bin` directory; or
- run this once in PowerShell after replacing the example path:

```powershell
$starkBin = 'C:\SDKs\Stark\__VERSION__\bin'
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$entries = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($entries -notcontains $starkBin) {
    [Environment]::SetEnvironmentVariable('Path', (($entries + $starkBin) -join ';'), 'User')
}
```

Open a new PowerShell or Command Prompt and run `where.exe stark` followed by
`stark doctor --strict`. Remove exactly that entry to undo a manual PATH setup.
'@
    $vendorVerification = @'
These commands compile and link a representative project for all nine package
images in the seven official Vendor families without launching a window or
opening an audio device:

```powershell
Push-Location .\examples\raylib
try {
    stark .\VendorRaylibSafeApis.stark --emit-exe --target __TARGET_TRIPLE__ -o .\vendor-raylib-verify
} finally { Pop-Location }

$projects = @('glfw', 'sdl3', 'stb-image', 'miniaudio', 'cgltf', 'sqlite')
foreach ($project in $projects) {
    Push-Location ".\examples\$project"
    try { stark build --target __TARGET_TRIPLE__ } finally { Pop-Location }
}
```

The Raylib project imports `Vendor.Raylib`, `Vendor.Raymath`, and `Vendor.Rlgl`.
The remaining projects cover `Vendor.GLFW`, `Vendor.SDL3`, `Vendor.STB.Image`,
`Vendor.Miniaudio`, `Vendor.Cgltf`, and `Vendor.SQLite` respectively.
'@
    $installSummary = @'
```powershell
.\install.ps1
```

The default is a versioned per-user installation at
`%LOCALAPPDATA%\Stark\versions\__VERSION__`, exposed through the user PATH.
Use `-Destination C:\path\to\stark`, `-NoPath`, `-NonInteractive`, `-DryRun`,
`-Repair`, or `-Force` as needed. `-Prefix` is an alias for `-Destination`.

Uninstall this version from the extracted archive or installed SDK:

```powershell
.\uninstall.ps1 -Destination "$env:LOCALAPPDATA\Stark\versions\__VERSION__"
```

The uninstaller also accepts `-Prefix`, `-DryRun`, and `-NonInteractive`.
'@
    $manualInstall = @'
### Optional installer

```powershell
.\install.ps1
.\install.ps1 -Destination "D:\SDKs\Stark\__VERSION__" -NoPath -NonInteractive
.\install.ps1 -DryRun -NoPath
.\install.ps1 -Repair
```

`-Force` replaces only a receipt-owned installation and should be used only
when an intentional upgrade/downgrade cannot use the normal versioned path.
Pass the downloaded archive hash into the receipt when desired:

```powershell
.\install.ps1 -ArchiveSha256 ((Get-FileHash ..\__ARCHIVE_FILE__ -Algorithm SHA256).Hash)
```

The installer updates only the **user** PATH and does not require elevation.
Already-open terminals may need to be restarted.

### Uninstall

```powershell
& "$env:LOCALAPPDATA\Stark\versions\__VERSION__\uninstall.ps1"
# Custom prefix:
.\uninstall.ps1 -Destination "D:\SDKs\Stark\__VERSION__"
# Audit without changes:
.\uninstall.ps1 -Destination "D:\SDKs\Stark\__VERSION__" -DryRun
```
'@
} else {
    $archiveHashCommand = if ($operatingSystem -eq "macos") { "shasum -a 256 -c" } else { "sha256sum -c" }
    $contentHashCommand = if ($operatingSystem -eq "macos") { "shasum -a 256 -c release-files.sha256" } else { "sha256sum -c release-files.sha256" }
    $archiveVerification = @'
```sh
__ARCHIVE_HASH_COMMAND__ __ARCHIVE_CHECKSUM__
```
'@
    $extractionCommands = @'
```sh
tar -xzf __ARCHIVE_FILE__
cd __ARCHIVE_BASE__
```
'@
    $contentVerification = @'
```sh
__CONTENT_HASH_COMMAND__
./install.sh --dry-run --no-path --non-interactive
```

The first command verifies every archive-local file listed in
`release-files.sha256`. The installer dry run additionally rejects unsafe,
duplicate, or untracked paths and runs `stark doctor --strict` without changing
the installation or PATH.
'@
    $shellProfileGuidance = if ($operatingSystem -eq "macos") {
        "Bash login shells usually read ``~/.bash_profile``; Zsh login shells read ``~/.zprofile``. Interactive-only customization can instead use ``~/.bashrc`` or ``~/.zshrc``."
    } else {
        "Bash login shells usually read ``~/.profile`` and interactive shells read ``~/.bashrc``; Zsh login shells read ``~/.zprofile`` and interactive shells read ``~/.zshrc``."
    }
    $permanentPathInstructions = @'
Use the installer for a delimited, receipt-owned PATH entry. For a manual
install, move the complete SDK to its final directory first, replace the example
path below, and add the matching line to the profile your shell actually reads:

```sh
# Bash or Zsh profile:
export PATH="$HOME/SDKs/Stark/__VERSION__/bin:$PATH"

# Fish (run once; Fish stores universal variables itself):
fish_add_path --universal "$HOME/SDKs/Stark/__VERSION__/bin"
```

__SHELL_PROFILE_GUIDANCE__ Open a new shell and run `command -v stark` followed
by `stark doctor --strict`. Remove only the line or Fish path entry you added to
undo a manual setup.
'@
    $vendorVerification = @'
These commands compile and link a representative project for all nine package
images in the seven official Vendor families without launching a window or
opening an audio device:

```sh
(cd examples/raylib && \
  stark VendorRaylibSafeApis.stark --emit-exe --target __TARGET_TRIPLE__ -o vendor-raylib-verify)
for project in glfw sdl3 stb-image miniaudio cgltf sqlite; do
  (cd "examples/$project" && stark build --target __TARGET_TRIPLE__)
done
```

The Raylib project imports `Vendor.Raylib`, `Vendor.Raymath`, and `Vendor.Rlgl`.
The remaining projects cover `Vendor.GLFW`, `Vendor.SDL3`, `Vendor.STB.Image`,
`Vendor.Miniaudio`, `Vendor.Cgltf`, and `Vendor.SQLite` respectively.
'@
    $defaultInstallRoot = if ($operatingSystem -eq "macos") {
        '$HOME/Library/Application Support/Stark/versions/__VERSION__'
    } else {
        '${XDG_DATA_HOME:-$HOME/.local/share}/stark/versions/__VERSION__'
    }
    $installSummary = @'
```sh
./install.sh
```

The default is a versioned per-user installation at
`__DEFAULT_INSTALL_ROOT__`, exposed through `$HOME/.local/bin`.
Use `--prefix /path/to/stark`, `--no-path`, `--non-interactive`, `--dry-run`,
`--repair`, or `--force` as needed.

Uninstall this version from the extracted archive or installed SDK:

```sh
./uninstall.sh --prefix "__DEFAULT_INSTALL_ROOT__"
```

The uninstaller also accepts `--dry-run` and `--non-interactive`.
'@
    $manualInstall = @'
### Optional installer

```sh
./install.sh
./install.sh --prefix "$HOME/SDKs/Stark/__VERSION__" --no-path --non-interactive
./install.sh --dry-run --no-path
./install.sh --repair
```

`--force` replaces only a receipt-owned installation and should be used only
when an intentional upgrade/downgrade cannot use the normal versioned path.
Pass the downloaded archive hash into the receipt when desired:

```sh
./install.sh --archive-sha256 "$(awk '{print $1}' ../__ARCHIVE_CHECKSUM__)"
```

The installer modifies only a selected per-user shell profile, using one
delimited idempotent block. It never rewrites the profile wholesale and never
requires administrator privileges for its default location.

### Uninstall

```sh
"__DEFAULT_INSTALL_ROOT__/uninstall.sh"
# Custom prefix:
./uninstall.sh --prefix "$HOME/SDKs/Stark/__VERSION__"
# Audit without changes:
./uninstall.sh --prefix "$HOME/SDKs/Stark/__VERSION__" --dry-run
```
'@
}

$quickStartMarkdown = ConvertTo-ReleaseDocumentationQuickStartMarkdown `
    -Steps $quickStartSteps `
    -TargetTriple $targetTriple `
    -OperatingSystem $operatingSystem `
    -CompilerRelativePath $compilerRelativePath
$quickStartDocumentBlock = @"
<!-- ${quickStartMarker}:start -->
$quickStartMarkdown
<!-- ${quickStartMarker}:end -->
"@
$quickStartDocumentBlock = $quickStartDocumentBlock.TrimEnd([char[]]"`r`n")

$knownLimitations = @'
- This is the C# Stage0 compiler and remains pre-1.0 software. Keep reproducible
  source/build inputs and do not use it as the sole control for safety-critical
  deployment.
- The minimum-OS policy is `__MINIMUM_OS_POLICY_STATUS__`; use the exact target
  archive on its matching 64-bit host and report older compatible systems as
  qualification data, not as assumed support.
- Final native linkage still needs **__HOST_PREREQUISITE__**. A private LLVM
  backend inside the SDK does not replace that operating-system development
  layer.
- Graphical, input, and audio examples need the corresponding desktop session,
  hardware/device, and normal platform drivers. The commands above prove
  package resolution and native linkage; headless runtime smokes are separate.
- Official Vendor packages never fall back to Homebrew, system package-manager
  copies, `pkg-config`, or repository paths. A missing payload is an SDK defect.
'@

$supportLinks = @'
- Bundled license inventory: [`licenses/`](licenses/)
- Offline SDK documentation: [`docs/`](docs/)
- Source and durable documentation: <https://github.com/AlexanderBaggett/Stark>
- Public releases: <https://github.com/AlexanderBaggett/Stark/releases>
- Bugs and installation problems: <https://github.com/AlexanderBaggett/Stark/issues>
- Security reports: <https://github.com/AlexanderBaggett/Stark/security/policy>
'@

$readmeTemplate = @'
# Stark __VERSION__ — __TARGET_ID__

This is the complete, relocatable Stark SDK for **__TARGET_ID__**. Keep the
extracted directory together; copying only `bin/stark` is not supported.

## Exact release identity

| Fact | Value |
| --- | --- |
| Stark version | `__VERSION__` |
| Target ID / asset suffix | `__TARGET_ID__` |
| Host OS / architecture | `__OPERATING_SYSTEM__` / `__ARCHITECTURE__` (64-bit only) |
| .NET runtime identifier | `__RUNTIME_IDENTIFIER__` |
| Stark target triple | `__TARGET_TRIPLE__` |
| Minimum OS | __MINIMUM_OS__ |
| Minimum-OS policy | `__MINIMUM_OS_POLICY_STATUS__` |
| Host prerequisite | __HOST_PREREQUISITE__ |
| Support tier | `__SUPPORT_TIER__` |
| Archive | `__ARCHIVE_FILE__` |

## Verify the download

Download `__ARCHIVE_FILE__` and its companion
`__ARCHIVE_CHECKSUM__` into the same directory, then run:

__ARCHIVE_VERIFICATION__

## Extract and verify the SDK

__EXTRACTION_COMMANDS__

__CONTENT_VERIFICATION__

## Use directly after extraction

No installer or Stark environment variable is required:

__PATH_COMMANDS__

The exact command/path sequence above is rendered from the canonical `steps`
array in `release-commands.json`. Release qualification executes those same
step objects against the shipped hello source and project.

The PATH change above lasts only for the current terminal. To keep using this
extracted SDK, add its `bin` directory to your shell or terminal PATH.

## Permanent manual PATH setup

__PERMANENT_PATH_INSTRUCTIONS__

## Verify every official Vendor family

__VENDOR_VERIFICATION__

## Optional installer

__INSTALL_SUMMARY__

See [INSTALL.md](INSTALL.md) for the complete extraction-only, installer,
uninstaller, checksum, and troubleshooting instructions.

## What is bundled and what the host supplies

The archive bundles the Stage0 compiler and self-contained .NET __DOTNET_RUNTIME_VERSION__ runtime,
Antlr __ANTLR_VERSION__, the allowlisted compiler-private LLVM
__LLVM_VERSION__ backend, System, the complete official Vendor catalog and
target-native payloads, examples, offline documentation, licenses, checksums,
and the optional installer/uninstaller. It is not a general LLVM/C/C++ SDK.

The host supplies only the platform development layer named above:
**__HOST_PREREQUISITE__**, plus normal operating-system services and drivers.
The installer does not download Stark, .NET, System, Vendor, or backend files.

Official `Vendor.*` packages resolve from `sdk.json` and already carry their
target-native payloads and ordered link facts. They require no `pkg-config`,
`PKG_CONFIG_PATH`, `STARK_PATH`, `-I`, or `-L` ritual. Those mechanisms may
still apply to a project's own custom native dependencies.

## Known limitations

__KNOWN_LIMITATIONS__

## Licenses, documentation, and support

__SUPPORT_LINKS__
'@

$installTemplate = @'
# Install Stark __VERSION__ for __TARGET_ID__

This manual applies only to `__ARCHIVE_FILE__`.

## Target and host requirement

- Runtime identifier: `__RUNTIME_IDENTIFIER__`
- Target triple: `__TARGET_TRIPLE__`
- Minimum OS: __MINIMUM_OS__ (`__MINIMUM_OS_POLICY_STATUS__` policy)
- Architecture: `__ARCHITECTURE__`, 64-bit only
- Required host development layer: **__HOST_PREREQUISITE__**

Install the host prerequisite through the operating system/vendor's official
mechanism. It is deliberately not embedded in the Stark archive. The Stark
compiler, its self-contained .NET runtime, compiler-private LLVM backend,
System, and official Vendor payloads are embedded and must never be recovered
from a package manager during installation.

## 1. Verify and extract

__ARCHIVE_VERIFICATION__

__EXTRACTION_COMMANDS__

Then verify all extracted payloads:

__CONTENT_VERIFICATION__

`release-files.sha256` covers every archive file except itself. The installers
validate safe relative paths, duplicates, every exact SHA-256, and the absence
of untracked files before modifying the machine.

## 2A. Extraction-only use

This is a fully supported installation mode:

__PATH_COMMANDS__

The exact command/path sequence above is rendered from the canonical `steps`
array in `release-commands.json`. Release qualification executes those same
step objects against the shipped hello source and project.

For a permanent manual setup, move the entire extracted directory to a stable
location and add only `<sdk-root>/bin` to PATH. Do not copy the command alone,
and do not set `STARK_HOME`, `STARK_SDK_ROOT`, or `STARK_PATH` for ordinary use.

### Permanent PATH setup

__PERMANENT_PATH_INSTRUCTIONS__

## 2B. Optional versioned installation

__MANUAL_INSTALL__

Both installers are offline for SDK payloads, validate the archive and host
first, copy transactionally, run the installed `stark doctor --strict`, and
write a receipt. Uninstall removes only receipt-owned files and the exact
Stark-managed command/PATH entry.

## Official Vendor packages

Imports such as `Vendor.Raylib`, `Vendor.GLFW`, `Vendor.SDL3`, and the rest of
the official catalog are SDK-owned. Do not install a duplicate native library
or configure `pkg-config`, `PKG_CONFIG_PATH`, `STARK_PATH`, `-I`, or `-L` for
them. A missing official package or native payload is a corrupt/incomplete SDK;
replace the entire archive instead of adding machine-local search paths.

### Vendor build verification

__VENDOR_VERIFICATION__

## Troubleshooting

1. Confirm the selected command (`command -v stark` on Unix or
   `Get-Command stark` in PowerShell).
2. Run `stark doctor --strict` and address only the exact host prerequisite it
   reports.
3. Re-run the installer in dry-run mode to verify every archive checksum.
4. Run `stark examples/hello.stark --check` from the SDK root.
5. For project builds, use `stark build --target __TARGET_TRIPLE__`; for the
   executable project, follow with `stark run --target __TARGET_TRIPLE__`.

Advanced compiler/backend/linker overrides are for compiler development and
custom toolchains. They are not part of the normal release installation.

## Known limitations

__KNOWN_LIMITATIONS__

## Licenses, documentation, and support

__SUPPORT_LINKS__
'@

$values = @{
    VERSION = $Version
    TARGET_ID = $targetId
    OPERATING_SYSTEM = $operatingSystem
    ARCHITECTURE = $architecture
    RUNTIME_IDENTIFIER = $runtimeIdentifier
    TARGET_TRIPLE = $targetTriple
    MINIMUM_OS = $minimumOs
    MINIMUM_OS_POLICY_STATUS = $minimumOsPolicyStatus
    HOST_PREREQUISITE = $hostPrerequisite
    SUPPORT_TIER = $supportTier
    ARCHIVE_FILE = $archiveFileName
    ARCHIVE_CHECKSUM = $archiveChecksumFileName
    ARCHIVE_BASE = $archiveBaseName
    ARCHIVE_VERIFICATION = $archiveVerification
    EXTRACTION_COMMANDS = $extractionCommands
    CONTENT_VERIFICATION = $contentVerification
    PATH_COMMANDS = $quickStartDocumentBlock
    PERMANENT_PATH_INSTRUCTIONS = $permanentPathInstructions
    VENDOR_VERIFICATION = $vendorVerification
    INSTALL_SUMMARY = $installSummary
    MANUAL_INSTALL = $manualInstall
    DEFAULT_INSTALL_ROOT = $(if ($operatingSystem -eq "windows") { "%LOCALAPPDATA%\Stark\versions\$Version" } else { $defaultInstallRoot })
    DOTNET_RUNTIME_VERSION = $dotnetRuntimeVersion
    ANTLR_VERSION = $antlrVersion
    LLVM_VERSION = $llvmVersion
    ARCHIVE_HASH_COMMAND = $(if ($operatingSystem -eq "windows") { "" } else { $archiveHashCommand })
    CONTENT_HASH_COMMAND = $(if ($operatingSystem -eq "windows") { "" } else { $contentHashCommand })
    SHELL_PROFILE_GUIDANCE = $(if ($operatingSystem -eq "windows") { "" } else { $shellProfileGuidance })
    KNOWN_LIMITATIONS = $knownLimitations
    SUPPORT_LINKS = $supportLinks
}

$readmeText = Expand-Template -Template $readmeTemplate -Values $values
$installText = Expand-Template -Template $installTemplate -Values $values
$commandContract = [ordered]@{
    schemaVersion = 1
    kind = "stark-release-quick-start"
    targetId = $targetId
    targetTriple = $targetTriple
    operatingSystem = $operatingSystem
    paths = [ordered]@{
        compiler = $compilerRelativePath
        helloSource = "examples/hello.stark"
        helloProject = "examples/hello"
    }
    documentation = [ordered]@{
        readme = "README.md"
        install = "INSTALL.md"
        quickStartMarker = $quickStartMarker
        quickStartMarkdown = $quickStartMarkdown
    }
    steps = $quickStartSteps
}
$commandContractText = ($commandContract | ConvertTo-Json -Depth 10).Replace("`r`n", "`n").TrimEnd() + "`n"
$encoding = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $stagePath "README.md"), $readmeText, $encoding)
[System.IO.File]::WriteAllText((Join-Path $stagePath "INSTALL.md"), $installText, $encoding)
[System.IO.File]::WriteAllText((Join-Path $stagePath "release-commands.json"), $commandContractText, $encoding)

Write-Host "Generated release-specific README.md, INSTALL.md, and release-commands.json for $targetId."
