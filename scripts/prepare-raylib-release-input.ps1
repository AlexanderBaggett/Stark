param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("linux-x64", "linux-arm64", "windows-x64", "windows-arm64", "macos-x64", "macos-arm64")]
    [string] $AssetSuffix,

    [Parameter(Mandatory = $true)]
    [string] $TargetTriple,

    [Parameter(Mandatory = $true)]
    [string] $OutputVendorRoot,

    [Parameter(Mandatory = $true)]
    [string] $StdlibPackageDir,

    [string] $ManifestPath = "scripts/raylib-6.0-assets.json",

    [string] $CacheDir = "artifacts/raylib-cache",

    [string] $ToolchainDir = "",

    [string] $CompilerProject = "src/compiler.csproj",

    [string] $VendorCatalogPath = "eng/release/vendor-packages.json",

    [string] $ContributionManifestPath = "",

    [switch] $ContributorMode,

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "invoke-release-download.ps1")

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Test-IsSameOrDescendantPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetFullPath($Path))
    $rootPath = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetFullPath($Root))
    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }

    if ([string]::Equals($candidate, $rootPath, $comparison)) {
        return $true
    }

    $rootPrefix = $rootPath + [System.IO.Path]::DirectorySeparatorChar
    return $candidate.StartsWith($rootPrefix, $comparison)
}

function Assert-NoReparsePointPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    $relativePath = $fullPath.Substring($rootPath.Length)
    $currentPath = $rootPath
    foreach ($segment in $relativePath.Split(
        [System.IO.Path]::DirectorySeparatorChar,
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            continue
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Output vendor root '$fullPath' traverses symbolic link or reparse point '$currentPath'."
        }
    }
}

function Assert-SafeOutputRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [switch] $SharedContributor
    )

    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetFullPath($Path))
    Assert-NoReparsePointPath -Path $candidate
    $pathRoot = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetPathRoot($candidate))
    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    if ([string]::Equals($candidate, $pathRoot, $comparison)) {
        throw "Output vendor root '$candidate' cannot be a filesystem root."
    }

    $artifactsRoot = Join-Path $repositoryRoot "artifacts"
    $insideRepository = Test-IsSameOrDescendantPath -Path $candidate -Root $repositoryRoot
    $insideArtifacts = Test-IsSameOrDescendantPath -Path $candidate -Root $artifactsRoot
    if ($insideRepository -and (-not $insideArtifacts -or
        [string]::Equals(
            $candidate,
            [System.IO.Path]::TrimEndingDirectorySeparator($artifactsRoot),
            $comparison))) {
        throw "Output vendor root '$candidate' must be a child of the repository artifacts directory, not a source/worktree path."
    }

    $protectedPaths = @(
        $repositoryRoot,
        [Environment]::CurrentDirectory,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($protectedPath in $protectedPaths) {
        if (Test-IsSameOrDescendantPath -Path $protectedPath -Root $candidate) {
            throw "Output vendor root '$candidate' is or contains protected path '$protectedPath'."
        }
    }

    if (Test-Path -LiteralPath $candidate) {
        $priorManifest = Join-Path $candidate "release-input.json"
        if (-not (Test-Path -LiteralPath $priorManifest -PathType Leaf)) {
            throw "Existing output vendor root '$candidate' is not a recognized Stark Vendor release-input directory. Remove it explicitly or choose a fresh path."
        }

        try {
            $prior = Get-Content -LiteralPath $priorManifest -Raw | ConvertFrom-Json
            $isLegacyRaylib = [int]$prior.schemaVersion -eq 1 -and
                $null -ne $prior.raylib -and
                [string]::Equals(
                    [string]$prior.raylib.assetSuffix,
                    $AssetSuffix,
                    [StringComparison]::Ordinal)
            $isUnifiedVendor = [int]$prior.schemaVersion -eq 2 -and
                [string]::Equals(
                    [string]$prior.manifestKind,
                    "stark-vendor-release-input",
                    [StringComparison]::Ordinal) -and
                $null -ne $prior.target -and
                [string]::Equals(
                    [string]$prior.target.assetSuffix,
                    $AssetSuffix,
                    [StringComparison]::Ordinal) -and
                [string]::Equals(
                    [string]$prior.target.targetTriple,
                    $TargetTriple,
                    [StringComparison]::Ordinal)
            if (-not $isUnifiedVendor -and ($SharedContributor -or -not $isLegacyRaylib)) {
                throw "manifest identity mismatch"
            }
        } catch {
            throw "Existing output vendor root '$candidate' has an invalid or mismatched release-input.json; refusing to write package artifacts."
        }
    }
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($Object -is [System.Collections.IDictionary]) {
        if (-not $Object.Contains($Name)) {
            throw "Required JSON/object property '$Name' was not found."
        }
        return $Object[$Name]
    }

    $property = $Object.PSObject.Properties |
        Where-Object { $_.Name -eq $Name } |
        Select-Object -First 1
    if ($null -eq $property) {
        throw "Required JSON property '$Name' was not found."
    }

    return $property.Value
}

function Get-OptionalProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $property = $Object.PSObject.Properties |
        Where-Object { $_.Name -eq $Name } |
        Select-Object -First 1
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-ArrayValues {
    param(
        [object] $Value
    )

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Array]) {
        return @($Value)
    }

    return @($Value)
}

function Get-OrdinalSortedStrings {
    param([object[]] $Values = @())

    $set = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    $caseInsensitive = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @($Values)) {
        if (-not $caseInsensitive.Add([string]$value) -or -not $set.Add([string]$value)) {
            throw "Duplicate or case-colliding ordinal string '$value'."
        }
    }
    return @($set)
}

function Get-OrdinalSortedObjects {
    param(
        [object[]] $Values = @(),
        [Parameter(Mandatory = $true)][string] $PropertyName
    )

    $map = [System.Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    $caseInsensitive = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @($Values)) {
        $key = [string](Get-RequiredProperty -Object $value -Name $PropertyName)
        if (-not $caseInsensitive.Add($key) -or $map.ContainsKey($key)) {
            throw "Duplicate or case-colliding ordinal $PropertyName '$key'."
        }
        $map.Add($key, $value)
    }
    return @($map.Values)
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSha256
    )

    if ($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Expected SHA-256 '$ExpectedSha256' is not a 64-digit hexadecimal digest."
    }

    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actual, $ExpectedSha256.ToLowerInvariant(), [StringComparison]::Ordinal)) {
        throw "SHA-256 mismatch for '$Path'. Expected $ExpectedSha256, got $actual."
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $sourceItems = @(Get-OrdinalSortedObjects -Values @(Get-ChildItem -LiteralPath $Source -Force) -PropertyName "Name")
    foreach ($item in $sourceItems) {
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) {
            Copy-DirectoryContents -Source $item.FullName -Destination $target
        } else {
            Copy-Item -LiteralPath $item.FullName -Destination $target -Force
        }
    }
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required Raylib release input '$Source' was not found."
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Assert-MatchingHost {
    param(
        [Parameter(Mandatory = $true)]
        [string] $OperatingSystem,

        [Parameter(Mandatory = $true)]
        [string] $Architecture
    )

    $hostOperatingSystem = if ($IsWindows) {
        "windows"
    } elseif ($IsLinux) {
        "linux"
    } elseif ($IsMacOS) {
        "macos"
    } else {
        "unknown"
    }

    $hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    if (-not [string]::Equals($hostOperatingSystem, $OperatingSystem, [StringComparison]::Ordinal) -or
        -not [string]::Equals($hostArchitecture, $Architecture, [StringComparison]::Ordinal)) {
        throw "Raylib release package '$AssetSuffix' must be built on $OperatingSystem-$Architecture, but this host is $hostOperatingSystem-$hostArchitecture. Cross-host package advertisement is intentionally unsupported."
    }
}

function Assert-RelativePackagePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    if ([System.IO.Path]::IsPathRooted($Path) -or
        $Path.Contains('..', [StringComparison]::Ordinal) -or
        $Path.Contains('\', [StringComparison]::Ordinal)) {
        throw "Generated Raylib package $Label '$Path' is not a portable SDK-relative path."
    }
}

function Get-FileManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $files = @()
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse)) {
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $files += [ordered]@{
            path = $relativePath
            bytes = $file.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }
    }

    return @(Get-OrdinalSortedObjects -Values $files -PropertyName "path")
}

function Write-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [object] $Value,

        [int] $Depth = 12
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $json = $Value | ConvertTo-Json -Depth $Depth
    [System.IO.File]::WriteAllText(
        $Path,
        $json + "`n",
        [System.Text.UTF8Encoding]::new($false))
}

function New-FileDescriptor {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Path,

        [string] $Kind = "file"
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Raylib release artifact '$Path' does not exist."
    }

    $file = Get-Item -LiteralPath $Path
    return [ordered]@{
        kind = $Kind
        path = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        bytes = [int64]$file.Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }
}

$manifestFullPath = Resolve-RepositoryPath -Path $ManifestPath
$outputRoot = Resolve-RepositoryPath -Path $OutputVendorRoot
$cacheRoot = Resolve-RepositoryPath -Path $CacheDir
$compilerProjectPath = Resolve-RepositoryPath -Path $CompilerProject
$stdlibPackageRoot = Resolve-RepositoryPath -Path $StdlibPackageDir
$vendorCatalogFullPath = Resolve-RepositoryPath -Path $VendorCatalogPath
$contributionManifestFullPath = if ([string]::IsNullOrWhiteSpace($ContributionManifestPath)) {
    $null
} else {
    Resolve-RepositoryPath -Path $ContributionManifestPath
}
$toolchainPath = if ([string]::IsNullOrWhiteSpace($ToolchainDir)) {
    $null
} else {
    Resolve-RepositoryPath -Path $ToolchainDir
}

if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) {
    throw "Raylib asset manifest '$manifestFullPath' does not exist."
}

if (-not (Test-Path -LiteralPath $compilerProjectPath -PathType Leaf)) {
    throw "Stage0 compiler project '$compilerProjectPath' does not exist."
}

if ($ContributorMode -and $null -eq $contributionManifestFullPath) {
    throw "Raylib contributor mode requires -ContributionManifestPath. The unified orchestrator owns release-input.json."
}

if (-not $ContributorMode -and $null -ne $contributionManifestFullPath) {
    throw "-ContributionManifestPath is valid only with -ContributorMode."
}

if (-not $ContributorMode -and -not (Test-Path -LiteralPath $vendorCatalogFullPath -PathType Leaf)) {
    throw "Vendor package catalog '$vendorCatalogFullPath' does not exist."
}

if ($null -ne $contributionManifestFullPath -and
    (Test-IsSameOrDescendantPath -Path $contributionManifestFullPath -Root $outputRoot)) {
    throw "Raylib contribution manifest '$contributionManifestFullPath' must be outside the shared Vendor artifact root."
}

if (-not (Test-Path -LiteralPath $stdlibPackageRoot -PathType Container) `
    -or @(Get-ChildItem -LiteralPath $stdlibPackageRoot -File -Recurse -Filter "*.starkpkg").Count -eq 0) {
    throw "Staged standard-library package directory '$stdlibPackageRoot' does not contain a package image. Raylib release packages must consume the staged System package identity, not repository source."
}

if ($null -ne $toolchainPath -and -not (Test-Path -LiteralPath $toolchainPath -PathType Container)) {
    throw "Toolchain directory '$toolchainPath' does not exist."
}

$manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json
if ([int] (Get-RequiredProperty -Object $manifest -Name "schemaVersion") -ne 1) {
    throw "Unsupported Raylib asset manifest schema version."
}

$platforms = Get-RequiredProperty -Object $manifest -Name "platforms"
$platform = Get-RequiredProperty -Object $platforms -Name $AssetSuffix
$archive = Get-RequiredProperty -Object $platform -Name "archive"
$expectedTargetTriple = [string] (Get-RequiredProperty -Object $platform -Name "targetTriple")
if (-not [string]::Equals($TargetTriple.Trim(), $expectedTargetTriple, [StringComparison]::Ordinal)) {
    throw "Target triple '$TargetTriple' does not match the pinned '$AssetSuffix' Raylib package target '$expectedTargetTriple'."
}

Assert-MatchingHost `
    -OperatingSystem ([string] (Get-RequiredProperty -Object $platform -Name "hostOperatingSystem")) `
    -Architecture ([string] (Get-RequiredProperty -Object $platform -Name "hostArchitecture"))

$assetCacheRoot = Join-Path $cacheRoot $AssetSuffix
New-Item -ItemType Directory -Force -Path $assetCacheRoot | Out-Null
$archiveName = [string] (Get-RequiredProperty -Object $archive -Name "name")
$archivePath = Join-Path $assetCacheRoot $archiveName
if ($Force -or -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    $downloadPath = "$archivePath.download-$([Guid]::NewGuid().ToString('N'))"
    try {
        Invoke-ReleaseDownload `
            -Uri ([string] (Get-RequiredProperty -Object $archive -Name "url")) `
            -OutFile $downloadPath
        Assert-Sha256 `
            -Path $downloadPath `
            -ExpectedSha256 ([string] (Get-RequiredProperty -Object $archive -Name "sha256"))
        Move-Item -LiteralPath $downloadPath -Destination $archivePath -Force
    } finally {
        Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
    }
}

Assert-Sha256 `
    -Path $archivePath `
    -ExpectedSha256 ([string] (Get-RequiredProperty -Object $archive -Name "sha256"))

$expectedArchiveSize = [int64] (Get-RequiredProperty -Object $archive -Name "size")
$actualArchiveSize = (Get-Item -LiteralPath $archivePath).Length
if ($actualArchiveSize -ne $expectedArchiveSize) {
    throw "Raylib archive size mismatch for '$archivePath'. Expected $expectedArchiveSize bytes, got $actualArchiveSize."
}

$workRoot = Join-Path $assetCacheRoot "work"
if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}

$extractRoot = Join-Path $workRoot "extract"
New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
if ($archiveName.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force
} else {
    & tar -xzf $archivePath -C $extractRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to extract '$archivePath'."
    }
}

$payloadRoot = Join-Path $extractRoot ([string] (Get-RequiredProperty -Object $archive -Name "payloadRoot"))
if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) {
    throw "Pinned Raylib payload root '$payloadRoot' was not found after extraction."
}

$repositoryVendorRoot = Join-Path $repositoryRoot "vendor"
Assert-SafeOutputRoot -Path $outputRoot -SharedContributor:$ContributorMode

if (-not $ContributorMode) {
    if (Test-Path -LiteralPath $outputRoot) {
        Remove-Item -LiteralPath $outputRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
} elseif (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
    throw "Unified Vendor output root '$outputRoot' does not exist. The orchestrator must create and mark it before invoking contributors."
}

$stagedVendorSourceRoot = Join-Path $outputRoot "src/Vendor"
$ownedSourceFile = Join-Path $stagedVendorSourceRoot "Raylib.stark"
$ownedSourceDirectory = Join-Path $stagedVendorSourceRoot "Raylib"
$ownedRaymathSourceFile = Join-Path $stagedVendorSourceRoot "Raymath.stark"
$ownedRlglSourceFile = Join-Path $stagedVendorSourceRoot "Rlgl.stark"
foreach ($ownedPath in @($ownedSourceFile, $ownedSourceDirectory, $ownedRaymathSourceFile, $ownedRlglSourceFile)) {
    if (Test-Path -LiteralPath $ownedPath) {
        Remove-Item -LiteralPath $ownedPath -Recurse -Force
    }
}

Copy-RequiredFile `
    -Source (Join-Path $repositoryVendorRoot "src/Vendor/Raylib.stark") `
    -Destination $ownedSourceFile
Copy-DirectoryContents `
    -Source (Join-Path $repositoryVendorRoot "src/Vendor/Raylib") `
    -Destination $ownedSourceDirectory
Copy-RequiredFile `
    -Source (Join-Path $repositoryVendorRoot "src/Vendor/Raymath.stark") `
    -Destination $ownedRaymathSourceFile
Copy-RequiredFile `
    -Source (Join-Path $repositoryVendorRoot "src/Vendor/Rlgl.stark") `
    -Destination $ownedRlglSourceFile

$targetDist = Join-Path $outputRoot (Join-Path "dist" $AssetSuffix)
$nativeRaylibRoot = Join-Path $targetDist "native/raylib"
$raylibLicenseRoot = Join-Path $outputRoot "licenses/Raylib"
foreach ($ownedDirectory in @($nativeRaylibRoot, $raylibLicenseRoot)) {
    if (Test-Path -LiteralPath $ownedDirectory) {
        Remove-Item -LiteralPath $ownedDirectory -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path $nativeRaylibRoot | Out-Null
New-Item -ItemType Directory -Force -Path $raylibLicenseRoot | Out-Null

foreach ($header in @("raylib.h", "raymath.h", "rlgl.h")) {
    Copy-RequiredFile `
        -Source (Join-Path $payloadRoot (Join-Path "include" $header)) `
        -Destination (Join-Path $nativeRaylibRoot $header)
}

$nativeLibraryFile = [string] (Get-RequiredProperty -Object $platform -Name "nativeLibraryFile")
Copy-RequiredFile `
    -Source (Join-Path $payloadRoot ([string] (Get-RequiredProperty -Object $platform -Name "staticLibrary"))) `
    -Destination (Join-Path $nativeRaylibRoot $nativeLibraryFile)
Copy-RequiredFile -Source (Join-Path $payloadRoot "LICENSE") -Destination (Join-Path $nativeRaylibRoot "LICENSE")
Copy-RequiredFile -Source (Join-Path $payloadRoot "LICENSE") -Destination (Join-Path $raylibLicenseRoot "LICENSE")
Copy-RequiredFile -Source (Join-Path $payloadRoot "README.md") -Destination (Join-Path $nativeRaylibRoot "README.md")
Copy-RequiredFile -Source (Join-Path $payloadRoot "CHANGELOG") -Destination (Join-Path $nativeRaylibRoot "CHANGELOG")

$versionText = @"
# Raylib native release input

- Version: $([string] $manifest.raylibVersion)
- Release tag: $([string] $manifest.releaseTag)
- Release page: $([string] $manifest.releaseUrl)
- Asset: $archiveName
- Asset URL: $([string] $archive.url)
- SHA-256: $([string] $archive.sha256)
- License: $([string] $manifest.license)
- Stark target: $expectedTargetTriple
"@
Set-Content -LiteralPath (Join-Path $nativeRaylibRoot "VERSION.md") -Value $versionText -Encoding utf8

$starkLibraryFile = [string] (Get-RequiredProperty -Object $platform -Name "starkLibraryFile")
$starkLibraryPath = Join-Path $targetDist $starkLibraryFile
$packageImagePath = [System.IO.Path]::ChangeExtension($starkLibraryPath, ".starkpkg")
foreach ($ownedArtifact in @($starkLibraryPath, $packageImagePath)) {
    if (Test-Path -LiteralPath $ownedArtifact) {
        Remove-Item -LiteralPath $ownedArtifact -Force
    }
}

$compilerArguments = @(
    "run",
    "--project", $compilerProjectPath,
    "--no-restore",
    "--",
    $ownedSourceFile,
    "--emit-lib",
    # Release inputs must be closed over staged package identities. A developer's
    # STARK_PATH may contain repository source that would otherwise shadow System.
    "--no-stark-path",
    "-I", (Join-Path $outputRoot "src"),
    "-I", $stdlibPackageRoot,
    "-o", $starkLibraryPath,
    "--target", $expectedTargetTriple,
    "--package-profile", "release",
    "--native-include-dir", $nativeRaylibRoot,
    "--native-library-dir", $nativeRaylibRoot
)

if ($null -ne $toolchainPath) {
    $compilerArguments += @("--toolchain-dir", $toolchainPath)
}

foreach ($library in (Get-ArrayValues -Value $platform.libraries)) {
    $compilerArguments += @("--native-library", [string] $library)
}

foreach ($argument in (Get-ArrayValues -Value $platform.linkArguments)) {
    $compilerArguments += @("--native-link-arg", [string] $argument)
}

& dotnet @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Stage0 failed to build the Raylib package for '$expectedTargetTriple'."
}

if (-not (Test-Path -LiteralPath $starkLibraryPath -PathType Leaf)) {
    throw "Stage0 did not emit the expected Stark Raylib archive '$starkLibraryPath'."
}

if (-not (Test-Path -LiteralPath $packageImagePath -PathType Leaf)) {
    throw "Stage0 did not emit the expected Raylib package image '$packageImagePath'."
}

$inspectionPath = Join-Path $workRoot "VendorRaylib.starkpkg.json"
& dotnet run --project $compilerProjectPath --no-restore -- inspect-pkg $packageImagePath --format json -o $inspectionPath
if ($LASTEXITCODE -ne 0) {
    throw "Stage0 failed to inspect the generated Raylib package image '$packageImagePath'."
}

$inspection = Get-Content -LiteralPath $inspectionPath -Raw | ConvertFrom-Json
if (-not [string]::Equals([string] $inspection.RootModule, "Vendor.Raylib", [StringComparison]::Ordinal)) {
    throw "Generated Raylib package root is '$($inspection.RootModule)', expected 'Vendor.Raylib'."
}

if (-not [string]::Equals([string] $inspection.LibraryFileName, $starkLibraryFile, [StringComparison]::Ordinal)) {
    throw "Generated Raylib package library is '$($inspection.LibraryFileName)', expected '$starkLibraryFile'."
}

if ($null -eq $inspection.Target -or
    -not [string]::Equals([string] $inspection.Target.Triple, $expectedTargetTriple, [StringComparison]::Ordinal)) {
    throw "Generated Raylib package does not preserve target triple '$expectedTargetTriple'."
}

if ($null -eq $inspection.BuildProfile -or
    -not [string]::Equals([string] $inspection.BuildProfile.Name, "release", [StringComparison]::Ordinal)) {
    throw "Generated Raylib package does not preserve the release build profile."
}

$moduleNames = @($inspection.Modules | ForEach-Object { [string] $_.ModuleName })
$foreignModules = @($moduleNames | Where-Object { -not $_.StartsWith("Vendor.", [StringComparison]::Ordinal) })
if ($foreignModules.Count -ne 0) {
    throw "Generated Raylib package owns non-Vendor modules: $($foreignModules -join ', '). Package ownership must be repaired before release packaging."
}

$ownedModules = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($moduleName in $moduleNames) {
    $ownedModules.Add($moduleName) | Out-Null
}

$missingVendorImports = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
foreach ($module in @($inspection.Modules)) {
    $sourceSurface = Get-OptionalProperty -Object $module -Name "SourceSurface"
    if ($null -eq $sourceSurface) {
        continue
    }

    $imports = Get-OptionalProperty -Object $sourceSurface -Name "Imports"
    foreach ($import in @(Get-ArrayValues -Value $imports)) {
        $importName = [string] $import.ModuleName
        if ($importName.StartsWith("Vendor.", [StringComparison]::Ordinal) -and
            -not $ownedModules.Contains($importName)) {
            $missingVendorImports.Add($importName) | Out-Null
        }
    }
}

if ($missingVendorImports.Count -ne 0) {
    throw "Generated Raylib package imports unavailable Vendor modules: $($missingVendorImports -join ', '). Stage those packages or include the modules in the Raylib package before advertising it."
}

$nativeDependencies = $inspection.NativeDependencies
if ($null -eq $nativeDependencies) {
    throw "Generated Raylib package is missing native dependency metadata."
}

foreach ($path in (Get-ArrayValues -Value $nativeDependencies.IncludeDirectories)) {
    Assert-RelativePackagePath -Path ([string] $path) -Label "include directory"
}

foreach ($path in (Get-ArrayValues -Value $nativeDependencies.LibraryDirectories)) {
    Assert-RelativePackagePath -Path ([string] $path) -Label "library directory"
}

$pkgConfigPackages = Get-OptionalProperty -Object $nativeDependencies -Name "PkgConfigPackages"
if (@(Get-ArrayValues -Value $pkgConfigPackages).Count -ne 0) {
    throw "Generated Raylib release package must not depend on pkg-config."
}

# Package ownership is namespace-rooted: Vendor.Raylib owns only itself and
# Vendor.Raylib.*. Vendor.Raymath and Vendor.Rlgl therefore remain separate
# package identities, even though all three bindings come from the pinned
# Raylib upstream release. Build the siblings against the just-staged Raylib
# package so their dependency identity and native link closure flow transitively
# without duplicating Raylib's native payload.
$isWindowsTarget = $AssetSuffix.StartsWith("windows-", [StringComparison]::Ordinal)
$siblingDefinitions = @(
    [pscustomobject]@{
        Id = "Vendor.Raymath"
        SourceName = "Raymath.stark"
        LibraryStem = "VendorRaymath"
        LicenseName = "Raymath"
        ExpectedDependencies = @("System", "Vendor.Raylib")
        ExpectedNativeLibraries = if ($isWindowsTarget) { @() } else { @("m") }
    },
    [pscustomobject]@{
        Id = "Vendor.Rlgl"
        SourceName = "Rlgl.stark"
        LibraryStem = "VendorRlgl"
        LicenseName = "Rlgl"
        ExpectedDependencies = @("Vendor.Raylib")
        ExpectedNativeLibraries = @()
    }
)
$siblingPackageBuilds = @()
foreach ($definition in $siblingDefinitions) {
    $siblingLibraryFile = if ($isWindowsTarget) {
        "$($definition.LibraryStem).lib"
    } else {
        "lib$($definition.LibraryStem).a"
    }
    $siblingSourcePath = Join-Path $stagedVendorSourceRoot $definition.SourceName
    $siblingLibraryPath = Join-Path $targetDist $siblingLibraryFile
    $siblingPackageImagePath = [System.IO.Path]::ChangeExtension($siblingLibraryPath, ".starkpkg")
    foreach ($ownedArtifact in @($siblingLibraryPath, $siblingPackageImagePath)) {
        if (Test-Path -LiteralPath $ownedArtifact) {
            Remove-Item -LiteralPath $ownedArtifact -Force
        }
    }

    $siblingCompilerArguments = @(
        "run",
        "--project", $compilerProjectPath,
        "--no-restore",
        "--",
        $siblingSourcePath,
        "--emit-lib",
        "--no-stark-path",
        "-I", $targetDist,
        "-I", $stdlibPackageRoot,
        "-o", $siblingLibraryPath,
        "--target", $expectedTargetTriple,
        "--package-profile", "release"
    )
    if ($null -ne $toolchainPath) {
        $siblingCompilerArguments += @("--toolchain-dir", $toolchainPath)
    }

    & dotnet @siblingCompilerArguments
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $siblingLibraryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $siblingPackageImagePath -PathType Leaf)) {
        throw "Stage0 failed to build sibling Raylib package '$($definition.Id)' for '$expectedTargetTriple'."
    }

    $siblingInspectionPath = Join-Path $workRoot ($definition.LibraryStem + ".starkpkg.json")
    & dotnet run --project $compilerProjectPath --no-restore -- inspect-pkg $siblingPackageImagePath --format json -o $siblingInspectionPath
    if ($LASTEXITCODE -ne 0) {
        throw "Stage0 failed to inspect sibling Raylib package '$($definition.Id)'."
    }
    $siblingInspection = Get-Content -LiteralPath $siblingInspectionPath -Raw | ConvertFrom-Json
    $siblingModules = @($siblingInspection.Modules | ForEach-Object { [string]$_.ModuleName })
    if (-not [string]::Equals([string]$siblingInspection.RootModule, $definition.Id, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$siblingInspection.LibraryFileName, $siblingLibraryFile, [StringComparison]::Ordinal) -or
        $siblingModules.Count -ne 1 -or
        -not [string]::Equals($siblingModules[0], $definition.Id, [StringComparison]::Ordinal) -or
        $null -eq $siblingInspection.Target -or
        -not [string]::Equals([string]$siblingInspection.Target.Triple, $expectedTargetTriple, [StringComparison]::Ordinal) -or
        $null -eq $siblingInspection.BuildProfile -or
        -not [string]::Equals([string]$siblingInspection.BuildProfile.Name, "release", [StringComparison]::Ordinal)) {
        throw "Sibling Raylib package '$($definition.Id)' does not preserve its exact module, archive, target, and release-profile identity."
    }

    $identity = Get-OptionalProperty -Object $siblingInspection -Name "Identity"
    $identityDependencies = if ($null -eq $identity) { @() } else {
        @(Get-ArrayValues -Value (Get-OptionalProperty -Object $identity -Name "Dependencies"))
    }
    $dependencyPackageIds = @($identityDependencies | ForEach-Object { [string]$_.PackageId })
    $sortedDependencyPackageIds = @(Get-OrdinalSortedStrings -Values $dependencyPackageIds)
    $expectedDependencyPackageIds = @(Get-OrdinalSortedStrings -Values $definition.ExpectedDependencies)
    if (($sortedDependencyPackageIds -join "`n") -cne ($expectedDependencyPackageIds -join "`n")) {
        throw "Sibling Raylib package '$($definition.Id)' dependency identities [$($sortedDependencyPackageIds -join ', ')] do not exactly match direct imported package identities [$($expectedDependencyPackageIds -join ', ')]."
    }

    $siblingNativeDependencies = Get-OptionalProperty -Object $siblingInspection -Name "NativeDependencies"
    if ($null -ne $siblingNativeDependencies) {
        foreach ($propertyName in @("Sources", "IncludeDirectories", "LibraryDirectories", "LinkArguments", "PkgConfigPackages")) {
            if (@(Get-ArrayValues -Value (Get-OptionalProperty -Object $siblingNativeDependencies -Name $propertyName)).Count -ne 0) {
                throw "Sibling Raylib package '$($definition.Id)' duplicates direct native metadata '$propertyName'; it must use Vendor.Raylib transitively."
            }
        }
    }
    $actualNativeLibraries = if ($null -eq $siblingNativeDependencies) { @() } else {
        @((Get-ArrayValues -Value (Get-OptionalProperty -Object $siblingNativeDependencies -Name "Libraries")) | ForEach-Object { [string]$_ })
    }
    $actualNativeLibraries = @(Get-OrdinalSortedStrings -Values $actualNativeLibraries)
    $expectedNativeLibraries = @(Get-OrdinalSortedStrings -Values $definition.ExpectedNativeLibraries)
    if (($actualNativeLibraries -join "`n") -cne ($expectedNativeLibraries -join "`n")) {
        throw "Sibling Raylib package '$($definition.Id)' compiler-inferred logical native libraries [$($actualNativeLibraries -join ', ')] do not match expected imported/intrinsic facts [$($expectedNativeLibraries -join ', ')]."
    }

    $siblingLicenseRoot = Join-Path $outputRoot ("licenses/" + $definition.LicenseName)
    if (Test-Path -LiteralPath $siblingLicenseRoot) {
        Remove-Item -LiteralPath $siblingLicenseRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $siblingLicenseRoot | Out-Null
    $siblingLicensePath = Join-Path $siblingLicenseRoot "LICENSE"
    Copy-RequiredFile -Source (Join-Path $payloadRoot "LICENSE") -Destination $siblingLicensePath

    $siblingProvenancePath = Join-Path $siblingLicenseRoot "PROVENANCE.json"
    $siblingProvenance = [ordered]@{
        schemaVersion = 1
        packageId = $definition.Id
        raylibVersion = [string]$manifest.raylibVersion
        sourceIdentity = "tag:$([string]$manifest.releaseTag)"
        releaseUrl = [string]$manifest.releaseUrl
        license = [string]$manifest.license
        target = [ordered]@{
            id = $AssetSuffix
            targetTriple = $expectedTargetTriple
            packageProfile = "release"
        }
        dependencyPackageIds = $sortedDependencyPackageIds
        compilerInferredNativeLibraries = $actualNativeLibraries
        package = [ordered]@{
            rootModule = [string]$siblingInspection.RootModule
            image = [System.IO.Path]::GetRelativePath($outputRoot, $siblingPackageImagePath).Replace('\', '/')
            imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $siblingPackageImagePath).Hash.ToLowerInvariant()
            library = [System.IO.Path]::GetRelativePath($outputRoot, $siblingLibraryPath).Replace('\', '/')
            librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $siblingLibraryPath).Hash.ToLowerInvariant()
        }
    }
    Write-DeterministicJson -Path $siblingProvenancePath -Value $siblingProvenance -Depth 10

    $siblingPackageBuilds += [pscustomobject]@{
        Definition = $definition
        Inspection = $siblingInspection
        Modules = $siblingModules
        LibraryPath = $siblingLibraryPath
        PackageImagePath = $siblingPackageImagePath
        LicensePath = $siblingLicensePath
        ProvenancePath = $siblingProvenancePath
    }
}

$provenance = [ordered]@{
    schemaVersion = 1
    raylibVersion = [string] $manifest.raylibVersion
    releaseTag = [string] $manifest.releaseTag
    releaseUrl = [string] $manifest.releaseUrl
    releaseApiUrl = [string] $manifest.releaseApiUrl
    license = [string] $manifest.license
    assetSuffix = $AssetSuffix
    runtimeIdentifier = [string] $platform.runtimeIdentifier
    targetTriple = $expectedTargetTriple
    sourceAsset = [ordered]@{
        name = $archiveName
        url = [string] $archive.url
        sha256 = [string] $archive.sha256
        size = [int64] $archive.size
    }
    package = [ordered]@{
        rootModule = [string] $inspection.RootModule
        image = [System.IO.Path]::GetRelativePath($outputRoot, $packageImagePath).Replace('\', '/')
        imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $packageImagePath).Hash.ToLowerInvariant()
        library = [System.IO.Path]::GetRelativePath($outputRoot, $starkLibraryPath).Replace('\', '/')
        librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $starkLibraryPath).Hash.ToLowerInvariant()
        modules = @(Get-OrdinalSortedStrings -Values $moduleNames)
    }
    nativePayload = [ordered]@{
        library = [System.IO.Path]::GetRelativePath($outputRoot, (Join-Path $nativeRaylibRoot $nativeLibraryFile)).Replace('\', '/')
        librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $nativeRaylibRoot $nativeLibraryFile)).Hash.ToLowerInvariant()
        license = [System.IO.Path]::GetRelativePath($outputRoot, (Join-Path $raylibLicenseRoot "LICENSE")).Replace('\', '/')
    }
}

$provenancePath = Join-Path $nativeRaylibRoot "PROVENANCE.json"
Write-DeterministicJson -Path $provenancePath -Value $provenance -Depth 8

$nativeArtifacts = @()
foreach ($file in (Get-ChildItem -LiteralPath $nativeRaylibRoot -File -Recurse)) {
    $kind = if ($file.Name -match '(?i)^(LICENSE|LICENCE|COPYING|NOTICE)(\..*)?$') {
        "license"
    } elseif ($file.Name -eq "PROVENANCE.json") {
        "provenance"
    } elseif ($file.Extension -in @(".h", ".hpp")) {
        "header"
    } elseif ($file.Extension -in @(".a", ".lib")) {
        "static-library"
    } elseif ($file.Extension -in @(".dll", ".dylib") -or $file.Name -match '(?i)\.so(?:\..+)?$') {
        "runtime-library"
    } else {
        "documentation"
    }

    $nativeArtifacts += New-FileDescriptor -Root $outputRoot -Path $file.FullName -Kind $kind
}

$licenseFiles = @()
foreach ($licensePath in @(
    (Join-Path $nativeRaylibRoot "LICENSE"),
    (Join-Path $raylibLicenseRoot "LICENSE"))) {
    $licenseDescriptor = New-FileDescriptor -Root $outputRoot -Path $licensePath -Kind "license"
    $licenseFiles += [ordered]@{
        path = [string]$licenseDescriptor.path
        bytes = [int64]$licenseDescriptor.bytes
        sha256 = [string]$licenseDescriptor.sha256
    }
}
$licenseFiles = @(Get-OrdinalSortedObjects -Values $licenseFiles -PropertyName "path")
$provenanceDescriptor = New-FileDescriptor -Root $outputRoot -Path $provenancePath -Kind "provenance"
$packageEntry = [ordered]@{
    id = "Vendor.Raylib"
    version = [string]$manifest.raylibVersion
    sourceIdentity = "tag:$([string]$manifest.releaseTag)"
    target = [ordered]@{
        id = $AssetSuffix
        targetTriple = $expectedTargetTriple
    }
    package = [ordered]@{
        rootModule = [string]$inspection.RootModule
        image = [System.IO.Path]::GetRelativePath($outputRoot, $packageImagePath).Replace('\', '/')
        imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $packageImagePath).Hash.ToLowerInvariant()
        library = [System.IO.Path]::GetRelativePath($outputRoot, $starkLibraryPath).Replace('\', '/')
        librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $starkLibraryPath).Hash.ToLowerInvariant()
        modules = @(Get-OrdinalSortedStrings -Values $moduleNames)
    }
    nativePayload = [ordered]@{
        artifacts = @(Get-OrdinalSortedObjects -Values $nativeArtifacts -PropertyName "path")
        licenseFiles = @($licenseFiles)
    }
    provenance = [ordered]@{
        path = [string]$provenanceDescriptor.path
        bytes = [int64]$provenanceDescriptor.bytes
        sha256 = [string]$provenanceDescriptor.sha256
    }
}
$packageEntries = @($packageEntry)
foreach ($siblingBuild in $siblingPackageBuilds) {
    $siblingLicenseDescriptor = New-FileDescriptor -Root $outputRoot -Path $siblingBuild.LicensePath -Kind "license"
    $siblingProvenanceDescriptor = New-FileDescriptor -Root $outputRoot -Path $siblingBuild.ProvenancePath -Kind "provenance"
    $packageEntries += [ordered]@{
        id = [string]$siblingBuild.Definition.Id
        version = [string]$manifest.raylibVersion
        sourceIdentity = "tag:$([string]$manifest.releaseTag)"
        target = [ordered]@{
            id = $AssetSuffix
            targetTriple = $expectedTargetTriple
        }
        package = [ordered]@{
            rootModule = [string]$siblingBuild.Inspection.RootModule
            image = [System.IO.Path]::GetRelativePath($outputRoot, $siblingBuild.PackageImagePath).Replace('\', '/')
            imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $siblingBuild.PackageImagePath).Hash.ToLowerInvariant()
            library = [System.IO.Path]::GetRelativePath($outputRoot, $siblingBuild.LibraryPath).Replace('\', '/')
            librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $siblingBuild.LibraryPath).Hash.ToLowerInvariant()
            modules = @(Get-OrdinalSortedStrings -Values $siblingBuild.Modules)
        }
        nativePayload = [ordered]@{
            artifacts = @()
            licenseFiles = @([ordered]@{
                path = [string]$siblingLicenseDescriptor.path
                bytes = [int64]$siblingLicenseDescriptor.bytes
                sha256 = [string]$siblingLicenseDescriptor.sha256
            })
        }
        provenance = [ordered]@{
            path = [string]$siblingProvenanceDescriptor.path
            bytes = [int64]$siblingProvenanceDescriptor.bytes
            sha256 = [string]$siblingProvenanceDescriptor.sha256
        }
    }
}
$packageEntries = @(Get-OrdinalSortedObjects -Values $packageEntries -PropertyName "id")

if ($ContributorMode) {
    $contribution = [ordered]@{
        schemaVersion = 1
        targetId = $AssetSuffix
        targetTriple = $expectedTargetTriple
        packages = @($packageEntries)
    }
    Write-DeterministicJson -Path $contributionManifestFullPath -Value $contribution -Depth 12
} else {
    $catalog = Get-Content -LiteralPath $vendorCatalogFullPath -Raw | ConvertFrom-Json
    $stagedCatalogPath = Join-Path $outputRoot "catalog/vendor-packages.json"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stagedCatalogPath) | Out-Null
    Copy-Item -LiteralPath $vendorCatalogFullPath -Destination $stagedCatalogPath -Force
    $catalogRelativePath = [System.IO.Path]::GetRelativePath($outputRoot, $stagedCatalogPath).Replace('\', '/')
    $releaseInputManifest = [ordered]@{
        schemaVersion = 2
        manifestKind = "stark-vendor-release-input"
        state = "ready"
        target = [ordered]@{
            id = $AssetSuffix
            assetSuffix = $AssetSuffix
            runtimeIdentifier = [string]$platform.runtimeIdentifier
            targetTriple = $expectedTargetTriple
            operatingSystem = [string]$platform.hostOperatingSystem
            architecture = [string]$platform.hostArchitecture
        }
        catalog = [ordered]@{
            id = [string](Get-RequiredProperty -Object $catalog -Name "catalogId")
            path = $catalogRelativePath
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $stagedCatalogPath).Hash.ToLowerInvariant()
        }
        packages = @($packageEntries)
        # release-input.json intentionally excludes itself: a manifest cannot
        # contain its own stable cryptographic digest.
        files = @(Get-FileManifest -Root $outputRoot | Where-Object { $_.path -ne "release-input.json" })
    }
    Write-DeterministicJson -Path (Join-Path $outputRoot "release-input.json") -Value $releaseInputManifest -Depth 14
}

if ($ContributorMode) {
    Write-Host "Contributed pinned Raylib $($manifest.raylibVersion) release input for $AssetSuffix at $contributionManifestFullPath"
} else {
    Write-Host "Prepared pinned Raylib $($manifest.raylibVersion) standalone release input for $AssetSuffix at $outputRoot"
}
Write-Host "Generated $packageImagePath"
Write-Host "Generated $starkLibraryPath"
