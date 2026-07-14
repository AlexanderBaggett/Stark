param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("linux-x64", "windows-x64", "macos-arm64")]
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

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

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
        [string] $Path
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
            throw "Existing output vendor root '$candidate' is not a recognized Raylib release-input directory. Remove it explicitly or choose a fresh path."
        }

        try {
            $prior = Get-Content -LiteralPath $priorManifest -Raw | ConvertFrom-Json
            if ([int]$prior.schemaVersion -ne 1 -or
                -not [string]::Equals(
                    [string]$prior.raylib.assetSuffix,
                    $AssetSuffix,
                    [StringComparison]::Ordinal)) {
                throw "manifest identity mismatch"
            }
        } catch {
            throw "Existing output vendor root '$candidate' has an invalid or mismatched release-input.json; refusing recursive replacement."
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
    foreach ($item in (Get-ChildItem -LiteralPath $Source -Force | Sort-Object Name)) {
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
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName)) {
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $files += [ordered]@{
            path = $relativePath
            bytes = $file.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }
    }

    return $files
}

$manifestFullPath = Resolve-RepositoryPath -Path $ManifestPath
$outputRoot = Resolve-RepositoryPath -Path $OutputVendorRoot
$cacheRoot = Resolve-RepositoryPath -Path $CacheDir
$compilerProjectPath = Resolve-RepositoryPath -Path $CompilerProject
$stdlibPackageRoot = Resolve-RepositoryPath -Path $StdlibPackageDir
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
    Invoke-WebRequest `
        -Uri ([string] (Get-RequiredProperty -Object $archive -Name "url")) `
        -OutFile $archivePath
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
Assert-SafeOutputRoot -Path $outputRoot

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$stagedVendorSourceRoot = Join-Path $outputRoot "src/Vendor"
Copy-RequiredFile `
    -Source (Join-Path $repositoryVendorRoot "src/Vendor/Raylib.stark") `
    -Destination (Join-Path $stagedVendorSourceRoot "Raylib.stark")
Copy-DirectoryContents `
    -Source (Join-Path $repositoryVendorRoot "src/Vendor/Raylib") `
    -Destination (Join-Path $stagedVendorSourceRoot "Raylib")

$targetDist = Join-Path $outputRoot (Join-Path "dist" $AssetSuffix)
$nativeRaylibRoot = Join-Path $targetDist "native/raylib"
$raylibLicenseRoot = Join-Path $outputRoot "licenses/Raylib"
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
$compilerArguments = @(
    "run",
    "--project", $compilerProjectPath,
    "--no-restore",
    "--",
    (Join-Path $repositoryVendorRoot "src/Vendor/Raylib.stark"),
    "--emit-lib",
    # Release inputs must be closed over staged package identities. A developer's
    # STARK_PATH may contain repository source that would otherwise shadow System.
    "--no-stark-path",
    "-I", (Join-Path $repositoryVendorRoot "src"),
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

$packageImagePath = [System.IO.Path]::ChangeExtension($starkLibraryPath, ".starkpkg")
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
        modules = @($moduleNames | Sort-Object)
    }
    nativePayload = [ordered]@{
        library = [System.IO.Path]::GetRelativePath($outputRoot, (Join-Path $nativeRaylibRoot $nativeLibraryFile)).Replace('\', '/')
        librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $nativeRaylibRoot $nativeLibraryFile)).Hash.ToLowerInvariant()
        license = [System.IO.Path]::GetRelativePath($outputRoot, (Join-Path $raylibLicenseRoot "LICENSE")).Replace('\', '/')
    }
}

$provenancePath = Join-Path $nativeRaylibRoot "PROVENANCE.json"
$provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $provenancePath -Encoding utf8

$releaseInputManifest = [ordered]@{
    schemaVersion = 1
    targetTriple = $expectedTargetTriple
    raylib = $provenance
    files = Get-FileManifest -Root $outputRoot
}
$releaseInputManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $outputRoot "release-input.json") -Encoding utf8

Write-Host "Prepared pinned Raylib $($manifest.raylibVersion) release input for $AssetSuffix at $outputRoot"
Write-Host "Generated $packageImagePath"
Write-Host "Generated $starkLibraryPath"
