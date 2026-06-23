param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $AssetSuffix,

    [Parameter(Mandatory = $true)]
    [string] $PublishDir,

    [Parameter(Mandatory = $true)]
    [string] $StdlibPackageDir,

    [string] $VendorRoot = "vendor",

    [string] $ToolchainDir = "",

    [string] $RuntimeIdentifier = "",

    [string] $TargetTriple = "",

    [string] $CommitSha = "",

    [string] $OutputDir = "artifacts/release",

    [ValidateSet("zip", "targz")]
    [string] $ArchiveKind = "zip",

    [string] $LlvmVersion = "22.1.8"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Resolve-InputPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $candidate = if ([System.IO.Path]::IsPathRooted($Path)) {
        $Path
    } else {
        Join-Path $repositoryRoot $Path
    }

    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "$Name path '$candidate' does not exist."
    }

    return (Resolve-Path -LiteralPath $candidate).Path
}

function Resolve-OptionalInputPath {
    param(
        [string] $Path,
        [string] $Name = "Optional"
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $candidate = if ([System.IO.Path]::IsPathRooted($Path)) {
        $Path
    } else {
        Join-Path $repositoryRoot $Path
    }

    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "$Name path '$candidate' does not exist."
    }

    return (Resolve-Path -LiteralPath $candidate).Path
}

function Copy-TreeFiltered {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination,

        [string[]] $ExcludedDirectoryNames = @(".stark", ".git", ".vs", ".vscode", "bin", "obj")
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    foreach ($item in (Get-ChildItem -LiteralPath $Source -Force | Sort-Object Name)) {
        if ($item.PSIsContainer) {
            if ($ExcludedDirectoryNames -contains $item.Name) {
                continue
            }

            Copy-TreeFiltered `
                -Source $item.FullName `
                -Destination (Join-Path $Destination $item.Name) `
                -ExcludedDirectoryNames $ExcludedDirectoryNames
            continue
        }

        Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $Destination $item.Name) -Force
    }
}

function Copy-OptionalTree {
    param(
        [string] $Source,
        [string] $Destination,
        [string[]] $ExcludedDirectoryNames = @(".stark", ".git", ".vs", ".vscode", "bin", "obj")
    )

    if (Test-Path -LiteralPath $Source -PathType Container) {
        Copy-TreeFiltered -Source $Source -Destination $Destination -ExcludedDirectoryNames $ExcludedDirectoryNames
    }
}

function Copy-OptionalFile {
    param(
        [string] $Source,
        [string] $Destination
    )

    if (Test-Path -LiteralPath $Source -PathType Leaf) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
    }
}

function Normalize-CompilerCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $targetName = if ($IsWindows) { "stark.exe" } else { "stark" }
    $targetPath = Join-Path $Root $targetName
    if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
        if (-not $IsWindows) {
            & chmod +x $targetPath
        }

        return $targetName
    }

    $candidateNames = if ($IsWindows) { @("compiler.exe") } else { @("compiler") }
    foreach ($candidateName in $candidateNames) {
        $candidatePath = Join-Path $Root $candidateName
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            Move-Item -LiteralPath $candidatePath -Destination $targetPath -Force
            if (-not $IsWindows) {
                & chmod +x $targetPath
            }

            return $targetName
        }
    }

    throw "Published compiler command was not found in '$Root'. Expected stark, stark.exe, compiler, or compiler.exe."
}

function Get-CurrentCommit {
    if (-not [string]::IsNullOrWhiteSpace($CommitSha)) {
        return $CommitSha.Trim()
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) {
        return $env:GITHUB_SHA
    }

    if (Get-Command git -ErrorAction SilentlyContinue) {
        $gitCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitCommit)) {
            return $gitCommit.Trim()
        }
    }

    return "<unknown>"
}

function Get-FileManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }

    $entries = @()
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName)) {
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $entries += [ordered]@{
            path = $relativePath
            bytes = $file.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }
    }

    return $entries
}

function Write-InstallDocument {
    param(
        [string] $Path,
        [string] $ArchiveRootName
    )

    $installText = @'
# Stark __VERSION__ Installation

1. Extract this archive.
2. Add the extracted archive root to PATH.
3. Open a new shell.
4. Run stark doctor.
5. Compile or check a Stark program.

macOS/Linux shell example:

    export PATH="/path/to/__ARCHIVE_ROOT__:$PATH"
    stark doctor

Windows PowerShell example:

    $env:Path = "C:\path\to\__ARCHIVE_ROOT__;$env:Path"
    stark doctor

No Stark environment variable is required for ordinary use. STARK_PATH,
STARK_TOOLCHAIN_DIR, --toolchain-dir, --linker, and --archiver are
developer overrides.

macOS requires locally installed Xcode or Command Line Tools SDK content. Linux
Stark-owned runtime and standard-library code is syscall-backed and no-libc;
libc and pkg-config diagnostics apply only when selected native or vendor
dependencies require them. Windows uses Stark's current Windows executable
generation path and may require the same SDK/CRT pieces as ordinary compiled
Stark programs.

Do not install Stark through Homebrew, Scoop, apt, npm, or another package
manager. Downloadable relocatable archives are the supported release path.
'@

    $installText = $installText.Replace("__VERSION__", $Version).Replace("__ARCHIVE_ROOT__", $ArchiveRootName)

    Set-Content -LiteralPath $Path -Value $installText -Encoding utf8
}

function Write-ReleaseText {
    param(
        [string] $Path,
        [string] $Commit,
        [string] $ToolchainRelativePath
    )

    $runtimeText = if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) { "<unspecified>" } else { $RuntimeIdentifier }
    $targetText = if ([string]::IsNullOrWhiteSpace($TargetTriple)) { "<compiler default>" } else { $TargetTriple }
    $toolchainText = if ([string]::IsNullOrWhiteSpace($ToolchainRelativePath)) { "not bundled by this packaging invocation" } else { $ToolchainRelativePath }

    $releaseText = @"
Stark $Version

Commit: $Commit
Asset: $AssetSuffix
Runtime ID: $runtimeText
Default target triple: $targetText
LLVM version: $LlvmVersion
Toolchain: $toolchainText

Included roots:
- stark command at archive root
- stdlib/
- vendor/
- licenses/
- docs/

Run stark doctor after installation to inspect compiler, runtime, target,
toolchain, SDK, stdlib, and vendor discovery.
"@

    Set-Content -LiteralPath $Path -Value $releaseText -Encoding utf8
}

function Write-ReleaseJson {
    param(
        [string] $Path,
        [string] $Commit,
        [string] $CommandName,
        [string] $ToolchainRelativePath,
        [object[]] $StdlibArtifacts,
        [object[]] $VendorArtifacts
    )

    $runtimeText = if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) { $null } else { $RuntimeIdentifier }
    $targetText = if ([string]::IsNullOrWhiteSpace($TargetTriple)) { $null } else { $TargetTriple }
    $toolchainPath = if ([string]::IsNullOrWhiteSpace($ToolchainRelativePath)) { $null } else { $ToolchainRelativePath }

    $releaseJson = [ordered]@{
        schemaVersion = 1
        starkVersion = $Version
        compilerVersion = $Version
        gitCommit = $Commit
        assetSuffix = $AssetSuffix
        runtimeIdentifier = $runtimeText
        defaultTargetTriple = $targetText
        llvmVersion = $LlvmVersion
        archiveKind = $ArchiveKind
        paths = [ordered]@{
            compiler = $CommandName
            stdlib = "stdlib"
            vendor = "vendor"
            toolchain = $toolchainPath
            licenses = "licenses"
            docs = "docs"
        }
        stdlibArtifacts = $StdlibArtifacts
        vendorArtifacts = $VendorArtifacts
    }

    $releaseJson | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8
}

$publishPath = Resolve-InputPath -Path $PublishDir -Name "Publish"
$stdlibPackagePath = Resolve-InputPath -Path $StdlibPackageDir -Name "Standard library package"
$vendorRootPath = Resolve-InputPath -Path $VendorRoot -Name "Vendor"
$toolchainSourcePath = Resolve-OptionalInputPath -Path $ToolchainDir -Name "Toolchain"

if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $outputPath = $OutputDir
} else {
    $outputPath = Join-Path $repositoryRoot $OutputDir
}

$assetBase = "stark-$Version-$AssetSuffix"
$stageParent = Join-Path $outputPath "stage"
$stageRoot = Join-Path $stageParent $assetBase

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

Copy-TreeFiltered -Source $publishPath -Destination $stageRoot -ExcludedDirectoryNames @()
$commandName = Normalize-CompilerCommand -Root $stageRoot

$stdlibRoot = Join-Path $stageRoot "stdlib"
Copy-OptionalFile -Source (Join-Path $repositoryRoot "stdlib/Stark.toml") -Destination (Join-Path $stdlibRoot "Stark.toml")
Copy-OptionalTree -Source (Join-Path $repositoryRoot "stdlib/src") -Destination (Join-Path $stdlibRoot "src")
Copy-OptionalTree -Source (Join-Path $repositoryRoot "stdlib/templates") -Destination (Join-Path $stdlibRoot "templates")
Copy-TreeFiltered -Source $stdlibPackagePath -Destination (Join-Path $stdlibRoot "dist") -ExcludedDirectoryNames @()

Copy-TreeFiltered -Source $vendorRootPath -Destination (Join-Path $stageRoot "vendor")

$toolchainRelativePath = ""
if ($null -ne $toolchainSourcePath) {
    $toolchainRelativePath = "toolchain/llvm-$LlvmVersion"
    Copy-TreeFiltered -Source $toolchainSourcePath -Destination (Join-Path $stageRoot $toolchainRelativePath) -ExcludedDirectoryNames @()
}

Copy-OptionalTree -Source (Join-Path $repositoryRoot "docs") -Destination (Join-Path $stageRoot "docs")
Copy-OptionalFile -Source (Join-Path $repositoryRoot "README.md") -Destination (Join-Path $stageRoot "README.md")
Copy-OptionalFile -Source (Join-Path $repositoryRoot "LICENSE") -Destination (Join-Path $stageRoot "LICENSE")

$licensesRoot = Join-Path $stageRoot "licenses"
New-Item -ItemType Directory -Force -Path (Join-Path $licensesRoot "Stark") | Out-Null
Copy-OptionalFile -Source (Join-Path $repositoryRoot "LICENSE") -Destination (Join-Path $licensesRoot "Stark/LICENSE")
Copy-OptionalTree -Source (Join-Path $stageRoot "vendor/licenses") -Destination (Join-Path $licensesRoot "vendor")
Copy-OptionalTree -Source (Join-Path $stageRoot "toolchain/llvm-$LlvmVersion/licenses") -Destination (Join-Path $licensesRoot "LLVM")

$commit = Get-CurrentCommit
Write-InstallDocument -Path (Join-Path $stageRoot "INSTALL.md") -ArchiveRootName $assetBase
Write-ReleaseText -Path (Join-Path $stageRoot "RELEASE.txt") -Commit $commit -ToolchainRelativePath $toolchainRelativePath
Write-ReleaseJson `
    -Path (Join-Path $stageRoot "release.json") `
    -Commit $commit `
    -CommandName $commandName `
    -ToolchainRelativePath $toolchainRelativePath `
    -StdlibArtifacts (Get-FileManifest -Root (Join-Path $stdlibRoot "dist")) `
    -VendorArtifacts (Get-FileManifest -Root (Join-Path $stageRoot "vendor/dist"))

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

if ($ArchiveKind -eq "zip") {
    $archivePath = Join-Path $outputPath "$assetBase.zip"
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Compress-Archive -LiteralPath $stageRoot -DestinationPath $archivePath -Force
} else {
    $archivePath = Join-Path $outputPath "$assetBase.tar.gz"
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    tar -czf $archivePath -C $stageParent $assetBase
}

$archiveFileName = Split-Path -Leaf $archivePath
$checksum = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
$checksumPath = "$archivePath.sha256"
Set-Content -LiteralPath $checksumPath -Value "$checksum  $archiveFileName" -Encoding ascii

if ($env:GITHUB_OUTPUT) {
    "archive_path=$archivePath" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "checksum_path=$checksumPath" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

Write-Host "Packaged $archivePath"
Write-Host "Wrote $checksumPath"
