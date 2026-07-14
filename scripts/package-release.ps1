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

function Assert-PortablePathSegment {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Value,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$') {
        throw "$Name '$Value' is not a portable single path segment."
    }
}

function Test-IsSameOrDescendantPath {
    param([string] $Path, [string] $Root)

    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    $rootPath = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Root))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    return [string]::Equals($candidate, $rootPath, $comparison) `
        -or $candidate.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Assert-NoReparsePointPath {
    param([string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    $currentPath = $rootPath
    foreach ($segment in $fullPath.Substring($rootPath.Length).Split(
        [System.IO.Path]::DirectorySeparatorChar,
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $segment
        $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and
            (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Release output path '$fullPath' traverses symbolic link or reparse point '$currentPath'."
        }
    }
}

Assert-PortablePathSegment -Value $Version -Name "Version"
Assert-PortablePathSegment -Value $AssetSuffix -Name "Asset suffix"
Assert-PortablePathSegment -Value $LlvmVersion -Name "LLVM version"

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

function Restore-ToolchainHardLinks {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ToolchainRoot
    )

    $manifestPath = Join-Path $ToolchainRoot "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $aliasProperty = $manifest.PSObject.Properties["hardlinkAliases"]
    if ($null -eq $aliasProperty) {
        return
    }

    $aliases = @($aliasProperty.Value)
    if ($aliases.Count -eq 0) {
        return
    }

    if ($IsWindows) {
        throw "Toolchain manifest requests hard-link aliases on Windows, which is not configured."
    }

    foreach ($alias in $aliases) {
        $relativePath = [string]$alias.path
        $targetRelativePath = [string]$alias.target
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [string]::IsNullOrWhiteSpace($targetRelativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            [System.IO.Path]::IsPathRooted($targetRelativePath)) {
            throw "Toolchain manifest contains an invalid hard-link alias."
        }

        $path = [System.IO.Path]::GetFullPath((Join-Path $ToolchainRoot $relativePath))
        $targetPath = [System.IO.Path]::GetFullPath((Join-Path $ToolchainRoot $targetRelativePath))
        if (-not (Test-IsSameOrDescendantPath -Path $path -Root $ToolchainRoot) -or
            -not (Test-IsSameOrDescendantPath -Path $targetPath -Root $ToolchainRoot)) {
            throw "Toolchain hard-link alias '$relativePath' escapes '$ToolchainRoot'."
        }

        Assert-NoReparsePointPath -Path $path
        Assert-NoReparsePointPath -Path $targetPath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            -not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
            throw "Toolchain hard-link alias '$relativePath' or target '$targetRelativePath' is missing."
        }

        $pathFile = Get-Item -LiteralPath $path
        $targetFile = Get-Item -LiteralPath $targetPath
        if ($pathFile.Length -ne $targetFile.Length -or
            -not [string]::Equals(
                (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash,
                (Get-FileHash -Algorithm SHA256 -LiteralPath $targetPath).Hash,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Toolchain hard-link alias '$relativePath' is not byte-identical to '$targetRelativePath'."
        }

        Remove-Item -LiteralPath $path -Force
        & /bin/ln $targetPath $path
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to restore toolchain hard-link alias '$relativePath'."
        }
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
2. Add the extracted archive's bin directory to PATH.
3. Open a new shell.
4. Run stark doctor.
5. Compile or check a Stark program.

macOS/Linux shell example:

    export PATH="/path/to/__ARCHIVE_ROOT__/bin:$PATH"
    stark doctor

Windows PowerShell example:

    $env:Path = "C:\path\to\__ARCHIVE_ROOT__\bin;$env:Path"
    stark doctor

No Stark environment variable is required for ordinary use. STARK_PATH,
STARK_SDK_ROOT, --sdk-root, STARK_TOOLCHAIN_DIR, --toolchain-dir, --linker,
and --archiver are advanced developer overrides.

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
- bin/stark[.exe] command and compiler runtime support files
- sdk.json runtime SDK manifest
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
        [string] $CompilerRelativePath,
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
            compiler = $CompilerRelativePath
            sdk = "sdk.json"
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
    $outputPath = [System.IO.Path]::GetFullPath($OutputDir)
} else {
    $outputPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDir))
}

Assert-NoReparsePointPath -Path $outputPath
$repositoryArtifactsRoot = Join-Path $repositoryRoot "artifacts"
if ((Test-IsSameOrDescendantPath -Path $outputPath -Root $repositoryRoot) -and
    -not (Test-IsSameOrDescendantPath -Path $outputPath -Root $repositoryArtifactsRoot)) {
    throw "Release output '$outputPath' must be under the repository artifacts directory or outside the repository."
}

$assetBase = "stark-$Version-$AssetSuffix"
$stageParent = Join-Path $outputPath "stage"
$stageRoot = Join-Path $stageParent $assetBase
$stageMarkerPath = "$stageRoot.stark-stage-marker"

if (-not (Test-IsSameOrDescendantPath -Path $stageRoot -Root $outputPath)) {
    throw "Release stage '$stageRoot' escapes output root '$outputPath'."
}

if (Test-Path -LiteralPath $stageRoot) {
    Assert-NoReparsePointPath -Path $stageRoot
    Assert-NoReparsePointPath -Path $stageMarkerPath
    if (-not (Test-Path -LiteralPath $stageMarkerPath -PathType Leaf) -or
        -not [string]::Equals(
            (Get-Content -LiteralPath $stageMarkerPath -Raw).Trim(),
            $assetBase,
            [StringComparison]::Ordinal)) {
        throw "Existing release stage '$stageRoot' is not owned by this packaging invocation; refusing recursive replacement."
    }

    Assert-NoReparsePointPath -Path $stageRoot
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}

Assert-NoReparsePointPath -Path $stageRoot
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
Assert-NoReparsePointPath -Path $stageMarkerPath
[System.IO.File]::WriteAllText($stageMarkerPath, $assetBase, [System.Text.UTF8Encoding]::new($false))

$compilerBinRoot = Join-Path $stageRoot "bin"
Copy-TreeFiltered -Source $publishPath -Destination $compilerBinRoot -ExcludedDirectoryNames @()
$commandName = Normalize-CompilerCommand -Root $compilerBinRoot
$compilerRelativePath = "bin/$commandName"

$stdlibRoot = Join-Path $stageRoot "stdlib"
Copy-OptionalFile -Source (Join-Path $repositoryRoot "stdlib/Stark.toml") -Destination (Join-Path $stdlibRoot "Stark.toml")
Copy-OptionalTree -Source (Join-Path $repositoryRoot "stdlib/src") -Destination (Join-Path $stdlibRoot "src")
Copy-OptionalTree -Source (Join-Path $repositoryRoot "stdlib/templates") -Destination (Join-Path $stdlibRoot "templates")
$stdlibDistRoot = Join-Path $stdlibRoot "dist"
$stdlibTargetDist = Join-Path $stdlibDistRoot $AssetSuffix
Copy-TreeFiltered -Source $stdlibPackagePath -Destination $stdlibTargetDist -ExcludedDirectoryNames @()

Copy-TreeFiltered -Source $vendorRootPath -Destination (Join-Path $stageRoot "vendor")

$toolchainRelativePath = ""
if ($null -ne $toolchainSourcePath) {
    $toolchainRelativePath = "toolchain/llvm-$LlvmVersion"
    $stagedToolchainRoot = Join-Path $stageRoot $toolchainRelativePath
    Copy-TreeFiltered -Source $toolchainSourcePath -Destination $stagedToolchainRoot -ExcludedDirectoryNames @()
    Restore-ToolchainHardLinks -ToolchainRoot $stagedToolchainRoot
}

& (Join-Path $PSScriptRoot "assemble-sdk-manifest.ps1") `
    -SdkRoot $stageRoot `
    -CompilerPath (Join-Path $compilerBinRoot $commandName) `
    -StdlibDist $stdlibDistRoot `
    -VendorDist (Join-Path $stageRoot "vendor/dist") `
    -Version $Version `
    -AssetSuffix $AssetSuffix `
    -TargetTriple $TargetTriple

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
    -CompilerRelativePath $compilerRelativePath `
    -ToolchainRelativePath $toolchainRelativePath `
    -StdlibArtifacts (Get-FileManifest -Root (Join-Path $stdlibRoot "dist")) `
    -VendorArtifacts (Get-FileManifest -Root (Join-Path $stageRoot "vendor/dist"))

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

if ($ArchiveKind -eq "zip") {
    $archivePath = Join-Path $outputPath "$assetBase.zip"
    Assert-NoReparsePointPath -Path $archivePath
    if (Test-Path -LiteralPath $archivePath) {
        Assert-NoReparsePointPath -Path $archivePath
        Remove-Item -LiteralPath $archivePath -Force
    }

    Assert-NoReparsePointPath -Path $archivePath
    Compress-Archive -LiteralPath $stageRoot -DestinationPath $archivePath -Force
} else {
    $archivePath = Join-Path $outputPath "$assetBase.tar.gz"
    Assert-NoReparsePointPath -Path $archivePath
    if (Test-Path -LiteralPath $archivePath) {
        Assert-NoReparsePointPath -Path $archivePath
        Remove-Item -LiteralPath $archivePath -Force
    }

    Assert-NoReparsePointPath -Path $archivePath
    tar -czf $archivePath -C $stageParent $assetBase
}

$archiveFileName = Split-Path -Leaf $archivePath
$checksum = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
$checksumPath = "$archivePath.sha256"
Assert-NoReparsePointPath -Path $checksumPath
Set-Content -LiteralPath $checksumPath -Value "$checksum  $archiveFileName" -Encoding ascii

if ($env:GITHUB_OUTPUT) {
    "archive_path=$archivePath" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "checksum_path=$checksumPath" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

Write-Host "Packaged $archivePath"
Write-Host "Wrote $checksumPath"
