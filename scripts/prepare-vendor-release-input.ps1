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

    [Parameter(Mandatory = $true)]
    [string] $ToolchainDir,

    [Parameter(Mandatory = $true)]
    [string] $CMakePath,

    [Parameter(Mandatory = $true)]
    [string] $NinjaPath,

    [string] $TargetManifestPath = "eng/release/targets.json",

    [string] $VendorCatalogPath = "eng/release/vendor-packages.json",

    [string] $RaylibManifestPath = "scripts/raylib-6.0-assets.json",

    [string] $CacheDir = "artifacts/vendor-cache",

    [string] $CompilerProject = "src/compiler.csproj",

    [string] $ValidateContributionManifest = "",

    [string] $ValidationRoot = "",

    [string] $NormalizedContributionOutput = "",

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$manifestKind = "stark-vendor-release-input"
$initialPackageIds = @(
    "Vendor.Cgltf",
    "Vendor.GLFW",
    "Vendor.Miniaudio",
    "Vendor.Raylib",
    "Vendor.Raymath",
    "Vendor.Rlgl",
    "Vendor.SDL3",
    "Vendor.SQLite",
    "Vendor.STB.Image"
)

# Contributor contract (schema 1):
# {
#   "schemaVersion": 1,
#   "targetId": "macos-arm64",
#   "targetTriple": "arm64-apple-macosx11.0.0",
#   "packages": [
#     {
#       "id": "Vendor.Name", "version": "...", "sourceIdentity": "...",
#       "target": { "id": "macos-arm64", "targetTriple": "..." },
#       "package": { "rootModule": "Vendor.Name", "image": "...",
#         "imageSha256": "...", "library": "...", "librarySha256": "...",
#         "modules": ["Vendor.Name"] },
#       "nativePayload": {
#         "artifacts": [{ "kind": "header", "path": "...", "bytes": 1, "sha256": "..." }],
#         "licenseFiles": [{ "path": "...", "bytes": 1, "sha256": "..." }]
#       },
#       "provenance": { "path": "...", "bytes": 1, "sha256": "..." }
#     }
#   ]
# }
# A contributor never writes release-input.json and never deletes the shared
# root. It may replace only paths owned by its declared package(s). This script
# owns root creation/replacement, validates every declared file, and emits the
# deterministic schema-2 aggregate.

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)][object] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($Object -is [System.Collections.IDictionary]) {
        if (-not $Object.Contains($Name)) {
            throw "Required JSON/object property '$Name' was not found."
        }
        return $Object[$Name]
    }

    $property = $Object.PSObject.Properties |
        Where-Object { $_.Name -ceq $Name } |
        Select-Object -First 1
    if ($null -eq $property) {
        throw "Required JSON property '$Name' was not found."
    }

    return $property.Value
}

function Get-ArrayValues {
    param([object] $Value)

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

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)][object] $Object,
        [Parameter(Mandatory = $true)][string[]] $Names,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $actual = @(Get-OrdinalSortedStrings -Values @($Object.PSObject.Properties.Name))
    $expected = @(Get-OrdinalSortedStrings -Values @($Names))
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        throw "$Label has properties [$($actual -join ', ')]; expected exactly [$($expected -join ', ')]."
    }
}

function Test-IsSameOrDescendantPath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Root
    )

    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    $rootPath = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Root))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if ([string]::Equals($candidate, $rootPath, $comparison)) {
        return $true
    }

    return $candidate.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Assert-NoReparsePointPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    $currentPath = $rootPath
    foreach ($segment in $fullPath.Substring($rootPath.Length).Split(
        [System.IO.Path]::DirectorySeparatorChar,
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            continue
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Managed Vendor path '$fullPath' traverses symbolic link or reparse point '$currentPath'."
        }
    }
}

function Assert-SafeManagedRoot {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [switch] $RequireExistingIdentity
    )

    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    Assert-NoReparsePointPath -Path $candidate
    $pathRoot = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetPathRoot($candidate))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if ([string]::Equals($candidate, $pathRoot, $comparison)) {
        throw "Vendor output root '$candidate' cannot be a filesystem root."
    }

    $artifactsRoot = Join-Path $repositoryRoot "artifacts"
    if ((Test-IsSameOrDescendantPath -Path $candidate -Root $repositoryRoot) -and
        (-not (Test-IsSameOrDescendantPath -Path $candidate -Root $artifactsRoot) -or
            [string]::Equals($candidate, [System.IO.Path]::TrimEndingDirectorySeparator($artifactsRoot), $comparison))) {
        throw "Vendor output root '$candidate' must be a child of the repository artifacts directory, not a source/worktree path."
    }

    foreach ($protectedPath in @(
        $repositoryRoot,
        [Environment]::CurrentDirectory,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (Test-IsSameOrDescendantPath -Path $protectedPath -Root $candidate) {
            throw "Vendor output root '$candidate' is or contains protected path '$protectedPath'."
        }
    }

    if ($RequireExistingIdentity) {
        $manifestPath = Join-Path $candidate "release-input.json"
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "Existing Vendor output '$candidate' has no release-input.json ownership manifest."
        }

        try {
            $prior = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $target = Get-RequiredProperty -Object $prior -Name "target"
            if ([int](Get-RequiredProperty -Object $prior -Name "schemaVersion") -ne 2 -or
                -not [string]::Equals([string](Get-RequiredProperty -Object $prior -Name "manifestKind"), $manifestKind, [StringComparison]::Ordinal) -or
                -not [string]::Equals([string](Get-RequiredProperty -Object $target -Name "assetSuffix"), $AssetSuffix, [StringComparison]::Ordinal) -or
                -not [string]::Equals([string](Get-RequiredProperty -Object $target -Name "targetTriple"), $TargetTriple, [StringComparison]::Ordinal)) {
                throw "identity mismatch"
            }
        } catch {
            throw "Existing Vendor output '$candidate' has an invalid or mismatched release-input.json; refusing recursive replacement."
        }
    }
}

function Write-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object] $Value,
        [int] $Depth = 16
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $json = $Value | ConvertTo-Json -Depth $Depth
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Assert-Sha256Text {
    param([Parameter(Mandatory = $true)][string] $Value, [Parameter(Mandatory = $true)][string] $Label)
    if ($Value -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Label must be a lowercase SHA-256 digest."
    }
}

function Assert-PortableArtifact {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [Parameter(Mandatory = $true)][string] $Sha256,
        [Nullable[int64]] $Bytes,
        [Parameter(Mandatory = $true)][string] $Label
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('\', [StringComparison]::Ordinal)) {
        throw "$Label path '$RelativePath' is not a portable Vendor-root-relative path."
    }

    $segments = $RelativePath.Split('/', [StringSplitOptions]::None)
    if ($segments | Where-Object { $_ -eq "" -or $_ -eq "." -or $_ -eq ".." }) {
        throw "$Label path '$RelativePath' contains an empty or traversal segment."
    }

    $absolutePath = [System.IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    if (-not (Test-IsSameOrDescendantPath -Path $absolutePath -Root $Root) -or
        -not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        throw "$Label '$RelativePath' is missing or escapes the Vendor root."
    }

    Assert-NoReparsePointPath -Path $absolutePath
    Assert-Sha256Text -Value $Sha256 -Label "$Label sha256"
    # The backend closure intentionally includes its dot-prefixed ownership
    # marker. PowerShell treats that file as hidden on Unix, so inspect it with
    # -Force just as the reparse-point walk above does.
    $file = Get-Item -LiteralPath $absolutePath -Force
    if ($null -ne $Bytes) {
        $expectedBytes = [int64]$Bytes
        if ($file.Length -ne $expectedBytes) {
            throw "$Label '$RelativePath' size is $($file.Length), expected $expectedBytes."
        }
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $absolutePath).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actualHash, $Sha256, [StringComparison]::Ordinal)) {
        throw "$Label '$RelativePath' hash is $actualHash, expected $Sha256."
    }

    return $file
}

function Get-FileInventory {
    param([Parameter(Mandatory = $true)][string] $Root)

    $files = @()
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force)) {
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($relativePath -eq "release-input.json") {
            continue
        }

        $files += [ordered]@{
            path = $relativePath
            bytes = [int64]$file.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }
    }

    return @(Get-OrdinalSortedObjects -Values $files -PropertyName "path")
}

function Assert-MatchingHost {
    param(
        [Parameter(Mandatory = $true)][string] $OperatingSystem,
        [Parameter(Mandatory = $true)][string] $Architecture
    )

    $hostOperatingSystem = if ($IsWindows) { "windows" } elseif ($IsLinux) { "linux" } elseif ($IsMacOS) { "macos" } else { "unknown" }
    $hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    if (-not [string]::Equals($hostOperatingSystem, $OperatingSystem, [StringComparison]::Ordinal) -or
        -not [string]::Equals($hostArchitecture, $Architecture, [StringComparison]::Ordinal)) {
        throw "Vendor release '$AssetSuffix' must be prepared on $OperatingSystem-$Architecture; this host is $hostOperatingSystem-$hostArchitecture."
    }
}

function Normalize-FileDescriptor {
    param(
        [Parameter(Mandatory = $true)][object] $Descriptor,
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Label,
        [switch] $HasKind
    )

    if ($HasKind) {
        Assert-ExactProperties -Object $Descriptor -Names @("kind", "path", "bytes", "sha256") -Label $Label
    } else {
        Assert-ExactProperties -Object $Descriptor -Names @("path", "bytes", "sha256") -Label $Label
    }

    $path = [string](Get-RequiredProperty -Object $Descriptor -Name "path")
    $sha256 = [string](Get-RequiredProperty -Object $Descriptor -Name "sha256")
    $bytes = [int64](Get-RequiredProperty -Object $Descriptor -Name "bytes")
    [void](Assert-PortableArtifact -Root $Root -RelativePath $path -Sha256 $sha256 -Bytes $bytes -Label $Label)
    if ($HasKind) {
        $kind = [string](Get-RequiredProperty -Object $Descriptor -Name "kind")
        if ($kind -notin @("documentation", "header", "license", "native-source", "provenance", "runtime-library", "static-library")) {
            throw "$Label has unsupported artifact kind '$kind'."
        }

        return [ordered]@{ kind = $kind; path = $path; bytes = $bytes; sha256 = $sha256 }
    }

    return [ordered]@{ path = $path; bytes = $bytes; sha256 = $sha256 }
}

function Normalize-PackageContribution {
    param(
        [Parameter(Mandatory = $true)][object] $Entry,
        [Parameter(Mandatory = $true)][object] $CatalogEntry,
        [Parameter(Mandatory = $true)][string] $Root
    )

    Assert-ExactProperties -Object $Entry -Names @("id", "version", "sourceIdentity", "target", "package", "nativePayload", "provenance") -Label "Vendor package contribution"
    $id = [string](Get-RequiredProperty -Object $Entry -Name "id")
    $version = [string](Get-RequiredProperty -Object $Entry -Name "version")
    $sourceIdentity = [string](Get-RequiredProperty -Object $Entry -Name "sourceIdentity")
    if (-not [string]::Equals($version, [string](Get-RequiredProperty -Object $CatalogEntry -Name "version"), [StringComparison]::Ordinal) -or
        -not [string]::Equals($sourceIdentity, [string](Get-RequiredProperty -Object $CatalogEntry -Name "sourceIdentity"), [StringComparison]::Ordinal)) {
        throw "Vendor contribution '$id' version/source identity does not match the official catalog."
    }

    $targetSupport = Get-RequiredProperty -Object $CatalogEntry -Name "targetSupport"
    $targetSupportState = [string](Get-RequiredProperty -Object $targetSupport -Name $AssetSuffix)
    if (-not $targetSupportState.StartsWith("required-", [StringComparison]::Ordinal)) {
        throw "Vendor contribution '$id' is not release-required for target '$AssetSuffix'."
    }

    $target = Get-RequiredProperty -Object $Entry -Name "target"
    Assert-ExactProperties -Object $target -Names @("id", "targetTriple") -Label "Vendor contribution '$id' target"
    if (-not [string]::Equals([string](Get-RequiredProperty -Object $target -Name "id"), $AssetSuffix, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string](Get-RequiredProperty -Object $target -Name "targetTriple"), $TargetTriple, [StringComparison]::Ordinal)) {
        throw "Vendor contribution '$id' targets a different release."
    }

    $package = Get-RequiredProperty -Object $Entry -Name "package"
    Assert-ExactProperties -Object $package -Names @("rootModule", "image", "imageSha256", "library", "librarySha256", "modules") -Label "Vendor contribution '$id' package"
    $rootModule = [string](Get-RequiredProperty -Object $package -Name "rootModule")
    if (-not [string]::Equals($rootModule, $id, [StringComparison]::Ordinal)) {
        throw "Vendor contribution '$id' package root is '$rootModule'."
    }

    $image = [string](Get-RequiredProperty -Object $package -Name "image")
    $imageSha256 = [string](Get-RequiredProperty -Object $package -Name "imageSha256")
    $library = [string](Get-RequiredProperty -Object $package -Name "library")
    $librarySha256 = [string](Get-RequiredProperty -Object $package -Name "librarySha256")
    [void](Assert-PortableArtifact -Root $Root -RelativePath $image -Sha256 $imageSha256 -Label "Vendor contribution '$id' package image")
    [void](Assert-PortableArtifact -Root $Root -RelativePath $library -Sha256 $librarySha256 -Label "Vendor contribution '$id' package library")
    if (-not $image.EndsWith(".starkpkg", [StringComparison]::Ordinal) -or
        -not ($library.EndsWith(".a", [StringComparison]::Ordinal) -or $library.EndsWith(".lib", [StringComparison]::Ordinal))) {
        throw "Vendor contribution '$id' has unexpected package image/library file names."
    }

    $rawModules = @((Get-ArrayValues -Value (Get-RequiredProperty -Object $package -Name "modules")) | ForEach-Object { [string]$_ })
    if ($rawModules.Count -eq 0 -or
        @($rawModules | Where-Object { $_ -cnotmatch '^Vendor(?:\.[A-Za-z][A-Za-z0-9_]*)+$' }).Count -ne 0) {
        throw "Vendor contribution '$id' contains empty or non-canonical module names."
    }
    $modules = @(Get-OrdinalSortedStrings -Values $rawModules)
    $rawCatalogModules = @((Get-ArrayValues -Value (Get-RequiredProperty -Object $CatalogEntry -Name "modules")) | ForEach-Object { [string]$_ })
    $catalogModules = @(Get-OrdinalSortedStrings -Values $rawCatalogModules)
    if (($modules -join "`n") -cne ($catalogModules -join "`n")) {
        throw "Vendor contribution '$id' modules do not exactly match the official catalog."
    }

    $nativePayload = Get-RequiredProperty -Object $Entry -Name "nativePayload"
    Assert-ExactProperties -Object $nativePayload -Names @("artifacts", "licenseFiles") -Label "Vendor contribution '$id' native payload"
    $artifacts = @()
    foreach ($descriptor in (Get-ArrayValues -Value (Get-RequiredProperty -Object $nativePayload -Name "artifacts"))) {
        $artifacts += Normalize-FileDescriptor -Descriptor $descriptor -Root $Root -Label "Vendor contribution '$id' native artifact" -HasKind
    }
    $artifacts = @(Get-OrdinalSortedObjects -Values $artifacts -PropertyName "path")

    $licenseFiles = @()
    foreach ($descriptor in (Get-ArrayValues -Value (Get-RequiredProperty -Object $nativePayload -Name "licenseFiles"))) {
        $licenseFiles += Normalize-FileDescriptor -Descriptor $descriptor -Root $Root -Label "Vendor contribution '$id' license"
    }
    $licenseFiles = @(Get-OrdinalSortedObjects -Values $licenseFiles -PropertyName "path")
    if ($licenseFiles.Count -eq 0) {
        throw "Vendor contribution '$id' has no staged license evidence."
    }

    $provenance = Get-RequiredProperty -Object $Entry -Name "provenance"
    $normalizedProvenance = Normalize-FileDescriptor -Descriptor $provenance -Root $Root -Label "Vendor contribution '$id' provenance"

    return [ordered]@{
        id = $id
        version = $version
        sourceIdentity = $sourceIdentity
        target = [ordered]@{ id = $AssetSuffix; targetTriple = $TargetTriple }
        package = [ordered]@{
            rootModule = $rootModule
            image = $image
            imageSha256 = $imageSha256
            library = $library
            librarySha256 = $librarySha256
            modules = $modules
        }
        nativePayload = [ordered]@{ artifacts = $artifacts; licenseFiles = $licenseFiles }
        provenance = $normalizedProvenance
    }
}

function Assert-UniquePackageOwnership {
    param([Parameter(Mandatory = $true)][object[]] $Packages)

    $owners = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Packages) {
        $id = [string](Get-RequiredProperty -Object $entry -Name "id")
        $package = Get-RequiredProperty -Object $entry -Name "package"
        $nativePayload = Get-RequiredProperty -Object $entry -Name "nativePayload"
        $provenance = Get-RequiredProperty -Object $entry -Name "provenance"
        $ownedPaths = @(
            [string](Get-RequiredProperty -Object $package -Name "image"),
            [string](Get-RequiredProperty -Object $package -Name "library"),
            [string](Get-RequiredProperty -Object $provenance -Name "path")
        )
        $ownedPaths += @((Get-ArrayValues -Value (Get-RequiredProperty -Object $nativePayload -Name "artifacts")) |
            ForEach-Object { [string](Get-RequiredProperty -Object $_ -Name "path") })
        $ownedPaths += @((Get-ArrayValues -Value (Get-RequiredProperty -Object $nativePayload -Name "licenseFiles")) |
            ForEach-Object { [string](Get-RequiredProperty -Object $_ -Name "path") })

        $localPaths = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($path in $ownedPaths) {
            if ($localPaths.ContainsKey($path)) {
                if (-not [string]::Equals($localPaths[$path], $path, [StringComparison]::Ordinal)) {
                    throw "Vendor package '$id' claims case-colliding artifact paths '$($localPaths[$path])' and '$path'."
                }
                # One file may intentionally serve multiple roles inside the
                # same package (for example artifact + license or artifact +
                # provenance). Cross-package ownership remains exclusive.
                continue
            }
            $localPaths.Add($path, $path)
            if ($owners.ContainsKey($path) -and -not [string]::Equals($owners[$path], $id, [StringComparison]::Ordinal)) {
                throw "Vendor artifact '$path' is claimed by both '$($owners[$path])' and '$id'."
            }
            $owners[$path] = $id
        }
    }
}

$targetManifestFullPath = Resolve-RepositoryPath -Path $TargetManifestPath
$vendorCatalogFullPath = Resolve-RepositoryPath -Path $VendorCatalogPath
$raylibManifestFullPath = Resolve-RepositoryPath -Path $RaylibManifestPath
$outputRoot = Resolve-RepositoryPath -Path $OutputVendorRoot
$stdlibRoot = Resolve-RepositoryPath -Path $StdlibPackageDir
$toolchainRoot = Resolve-RepositoryPath -Path $ToolchainDir
$cacheRoot = Resolve-RepositoryPath -Path $CacheDir
$compilerProjectPath = Resolve-RepositoryPath -Path $CompilerProject
$raylibScript = Join-Path $PSScriptRoot "prepare-raylib-release-input.ps1"
$coreScript = Join-Path $PSScriptRoot "prepare-core-vendor-release-input.ps1"
$glfwScript = Join-Path $PSScriptRoot "prepare-glfw-vendor-release-input.ps1"
$sdl3Script = Join-Path $PSScriptRoot "prepare-sdl3-vendor-release-input.ps1"
$sqliteScript = Join-Path $PSScriptRoot "prepare-sqlite-vendor-release-input.ps1"

if (-not [string]::IsNullOrWhiteSpace($ValidateContributionManifest) -or
    -not [string]::IsNullOrWhiteSpace($ValidationRoot) -or
    -not [string]::IsNullOrWhiteSpace($NormalizedContributionOutput)) {
    if ([string]::IsNullOrWhiteSpace($ValidateContributionManifest) -or
        [string]::IsNullOrWhiteSpace($ValidationRoot)) {
        throw "-ValidateContributionManifest and -ValidationRoot must be provided together."
    }

    $validationManifestPath = Resolve-RepositoryPath -Path $ValidateContributionManifest
    $validationArtifactRoot = Resolve-RepositoryPath -Path $ValidationRoot
    if (-not (Test-Path -LiteralPath $vendorCatalogFullPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $validationManifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $validationArtifactRoot -PathType Container)) {
        throw "Contribution validation requires an existing catalog, manifest, and artifact root."
    }

    $validationCatalog = Get-Content -LiteralPath $vendorCatalogFullPath -Raw | ConvertFrom-Json
    if ([int](Get-RequiredProperty -Object $validationCatalog -Name "schemaVersion") -ne 1) {
        throw "Unsupported Vendor catalog schema."
    }
    $validationCatalogEntries = @{}
    foreach ($entry in (Get-ArrayValues -Value (Get-RequiredProperty -Object $validationCatalog -Name "packages"))) {
        $validationCatalogEntries.Add([string](Get-RequiredProperty -Object $entry -Name "id"), $entry)
    }

    $validationContribution = Get-Content -LiteralPath $validationManifestPath -Raw | ConvertFrom-Json
    Assert-ExactProperties -Object $validationContribution -Names @("schemaVersion", "targetId", "targetTriple", "packages") -Label "Vendor validation contribution"
    if ([int](Get-RequiredProperty -Object $validationContribution -Name "schemaVersion") -ne 1 -or
        -not [string]::Equals([string](Get-RequiredProperty -Object $validationContribution -Name "targetId"), $AssetSuffix, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string](Get-RequiredProperty -Object $validationContribution -Name "targetTriple"), $TargetTriple, [StringComparison]::Ordinal)) {
        throw "Vendor validation contribution has a mismatched schema or target."
    }

    $normalizedFixturePackages = @()
    foreach ($entry in (Get-ArrayValues -Value (Get-RequiredProperty -Object $validationContribution -Name "packages"))) {
        $id = [string](Get-RequiredProperty -Object $entry -Name "id")
        if (-not $validationCatalogEntries.ContainsKey($id)) {
            throw "Vendor validation contribution '$id' is absent from the official catalog."
        }
        $normalizedFixturePackages += Normalize-PackageContribution -Entry $entry -CatalogEntry $validationCatalogEntries[$id] -Root $validationArtifactRoot
    }
    Assert-UniquePackageOwnership -Packages $normalizedFixturePackages
    $normalizedFixturePackages = @(Get-OrdinalSortedObjects -Values $normalizedFixturePackages -PropertyName "id")
    if (-not [string]::IsNullOrWhiteSpace($NormalizedContributionOutput)) {
        $normalizedOutputPath = Resolve-RepositoryPath -Path $NormalizedContributionOutput
        Write-DeterministicJson -Path $normalizedOutputPath -Value ([ordered]@{
            schemaVersion = 1
            targetId = $AssetSuffix
            targetTriple = $TargetTriple
            packages = $normalizedFixturePackages
        })
    }
    Write-Host "Validated $($normalizedFixturePackages.Count) Vendor contribution package(s)."
    return
}

$cmakeExecutable = Resolve-RepositoryPath -Path $CMakePath
$ninjaExecutable = Resolve-RepositoryPath -Path $NinjaPath
foreach ($requiredFile in @(
    $targetManifestFullPath,
    $vendorCatalogFullPath,
    $raylibManifestFullPath,
    $compilerProjectPath,
    $raylibScript,
    $coreScript,
    $glfwScript,
    $sdl3Script,
    $sqliteScript,
    $cmakeExecutable,
    $ninjaExecutable
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required Vendor release input '$requiredFile' was not found."
    }
    Assert-NoReparsePointPath -Path $requiredFile
}
if (-not (Test-Path -LiteralPath $toolchainRoot -PathType Container)) {
    throw "Private compiler backend '$toolchainRoot' does not exist."
}
if (-not (Test-Path -LiteralPath $stdlibRoot -PathType Container)) {
    throw "Staged standard-library root '$stdlibRoot' does not exist."
}

$targetsManifest = Get-Content -LiteralPath $targetManifestFullPath -Raw | ConvertFrom-Json
if ([int](Get-RequiredProperty -Object $targetsManifest -Name "schemaVersion") -ne 1) {
    throw "Unsupported release target manifest schema."
}
$targets = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $targetsManifest -Name "targets"))
$matchingTargets = @($targets | Where-Object {
    [string](Get-RequiredProperty -Object $_ -Name "assetSuffix") -ceq $AssetSuffix
})
if ($matchingTargets.Count -ne 1) {
    throw "Release target manifest does not define exactly one '$AssetSuffix' target."
}
$releaseTarget = $matchingTargets[0]
if (-not [bool](Get-RequiredProperty -Object $releaseTarget -Name "releaseEnabled") -or
    -not [string]::Equals([string](Get-RequiredProperty -Object $releaseTarget -Name "targetTriple"), $TargetTriple, [StringComparison]::Ordinal)) {
    throw "Target '$AssetSuffix' is disabled or does not match requested triple '$TargetTriple'."
}
Assert-MatchingHost -OperatingSystem ([string](Get-RequiredProperty -Object $releaseTarget -Name "operatingSystem")) -Architecture ([string](Get-RequiredProperty -Object $releaseTarget -Name "architecture"))

$toolchainManifestPath = Join-Path $toolchainRoot "manifest.json"
$toolchainOwnerMarkerPath = Join-Path $toolchainRoot ".stark-llvm-toolchain-owner.json"
if (-not (Test-Path -LiteralPath $toolchainManifestPath -PathType Leaf)) {
    throw "Private compiler backend '$toolchainRoot' has no manifest.json."
}
if (-not (Test-Path -LiteralPath $toolchainOwnerMarkerPath -PathType Leaf)) {
    throw "Private compiler backend '$toolchainRoot' has no managed ownership marker."
}
$toolchainOwnerMarker = Get-Content -LiteralPath $toolchainOwnerMarkerPath -Raw | ConvertFrom-Json
Assert-ExactProperties -Object $toolchainOwnerMarker -Names @("schemaVersion", "kind") -Label "Private compiler backend owner marker"
if ([int](Get-RequiredProperty -Object $toolchainOwnerMarker -Name "schemaVersion") -ne 1 -or
    -not [string]::Equals([string](Get-RequiredProperty -Object $toolchainOwnerMarker -Name "kind"), "stark-llvm-output", [StringComparison]::Ordinal)) {
    throw "Private compiler backend '$toolchainRoot' has an invalid managed ownership marker."
}
$toolchainManifest = Get-Content -LiteralPath $toolchainManifestPath -Raw | ConvertFrom-Json
if ([int](Get-RequiredProperty -Object $toolchainManifest -Name "schemaVersion") -ne 2 -or
    -not [string]::Equals([string](Get-RequiredProperty -Object $toolchainManifest -Name "payloadKind"), "stark-compiler-private-backend", [StringComparison]::Ordinal) -or
    -not [string]::Equals([string](Get-RequiredProperty -Object $toolchainManifest -Name "assetSuffix"), $AssetSuffix, [StringComparison]::Ordinal) -or
    -not [string]::Equals([string](Get-RequiredProperty -Object $toolchainManifest -Name "runtimeIdentifier"), [string](Get-RequiredProperty -Object $releaseTarget -Name "runtimeIdentifier"), [StringComparison]::Ordinal)) {
    throw "Private compiler backend '$toolchainRoot' is not the matching schema-2 Stark backend closure."
}

$runtimeClosure = Get-RequiredProperty -Object $toolchainManifest -Name "runtimeClosure"
Assert-ExactProperties -Object $runtimeClosure -Names @("fileCount", "logicalBytes", "files") -Label "Private compiler backend runtime closure"
$declaredBackendPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
[int64]$declaredBackendBytes = 0
foreach ($descriptor in (Get-ArrayValues -Value (Get-RequiredProperty -Object $runtimeClosure -Name "files"))) {
    Assert-ExactProperties -Object $descriptor -Names @("path", "bytes", "sha256") -Label "Private compiler backend file"
    $path = [string](Get-RequiredProperty -Object $descriptor -Name "path")
    $bytes = [int64](Get-RequiredProperty -Object $descriptor -Name "bytes")
    $sha256 = [string](Get-RequiredProperty -Object $descriptor -Name "sha256")
    if (-not $declaredBackendPaths.Add($path)) {
        throw "Private compiler backend declares duplicate file '$path'."
    }
    [void](Assert-PortableArtifact -Root $toolchainRoot -RelativePath $path -Sha256 $sha256 -Bytes $bytes -Label "Private compiler backend file")
    $declaredBackendBytes += $bytes
}
if ($declaredBackendPaths.Count -ne [int](Get-RequiredProperty -Object $runtimeClosure -Name "fileCount") -or
    $declaredBackendBytes -ne [int64](Get-RequiredProperty -Object $runtimeClosure -Name "logicalBytes")) {
    throw "Private compiler backend runtime closure counts do not match its declared files."
}

$actualBackendPaths = @(
    Get-ChildItem -LiteralPath $toolchainRoot -File -Recurse -Force |
        ForEach-Object { [System.IO.Path]::GetRelativePath($toolchainRoot, $_.FullName).Replace('\', '/') } |
        # manifest.json cannot hash itself. The portable ownership marker is a
        # normal member of schema-2 runtimeClosure.files and must participate
        # in this exact-set comparison.
        Where-Object { $_ -ne "manifest.json" }
)
$actualBackendPaths = @(Get-OrdinalSortedStrings -Values $actualBackendPaths)
$sortedDeclaredBackendPaths = @(Get-OrdinalSortedStrings -Values @($declaredBackendPaths))
if (($actualBackendPaths -join "`n") -cne ($sortedDeclaredBackendPaths -join "`n")) {
    throw "Private compiler backend contains missing or untracked files relative to runtimeClosure.files."
}
foreach ($requiredPath in @(
    (Get-ArrayValues -Value (Get-RequiredProperty -Object $toolchainManifest -Name "requiredTools")) +
    (Get-ArrayValues -Value (Get-RequiredProperty -Object $toolchainManifest -Name "requiredPatternMatches")))) {
    if (-not $declaredBackendPaths.Contains([string]$requiredPath)) {
        throw "Private compiler backend required file '$requiredPath' is absent from runtimeClosure.files."
    }
}

$stdlibImages = @(Get-ChildItem -LiteralPath $stdlibRoot -File -Recurse -Filter "*.starkpkg")
if ($stdlibImages.Count -ne 1) {
    throw "Staged standard-library root '$stdlibRoot' must contain exactly one package image; found $($stdlibImages.Count)."
}

Assert-SafeManagedRoot -Path $outputRoot -RequireExistingIdentity:(Test-Path -LiteralPath $outputRoot)
if ((Test-IsSameOrDescendantPath -Path $outputRoot -Root $cacheRoot) -or
    (Test-IsSameOrDescendantPath -Path $cacheRoot -Root $outputRoot)) {
    throw "Vendor output root '$outputRoot' and cache root '$cacheRoot' cannot overlap."
}

$operationToken = [Guid]::NewGuid().ToString("N")
$outputParent = Split-Path -Parent $outputRoot
$outputLeaf = Split-Path -Leaf $outputRoot
$stageRoot = Join-Path $outputParent ".$outputLeaf.vendor-stage-$operationToken"
$backupRoot = Join-Path $outputParent ".$outputLeaf.vendor-backup-$operationToken"
$workRoot = Join-Path $cacheRoot (Join-Path "work" "$AssetSuffix-$operationToken")
$raylibContributionPath = Join-Path $workRoot "raylib.json"
$coreContributionPath = Join-Path $workRoot "core.json"
$glfwContributionPath = Join-Path $workRoot "glfw.json"
$sdl3ContributionPath = Join-Path $workRoot "sdl3.json"
$sqliteContributionPath = Join-Path $workRoot "sqlite.json"

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
try {
    $systemInspectionPath = Join-Path $workRoot "System.starkpkg.json"
    & dotnet run --project $compilerProjectPath --no-restore -- inspect-pkg $stdlibImages[0].FullName --format json -o $systemInspectionPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $systemInspectionPath -PathType Leaf)) {
        throw "Stage0 could not inspect staged System package '$($stdlibImages[0].FullName)'."
    }
    $systemInspection = Get-Content -LiteralPath $systemInspectionPath -Raw | ConvertFrom-Json
    $systemTarget = Get-RequiredProperty -Object $systemInspection -Name "Target"
    $systemProfile = Get-RequiredProperty -Object $systemInspection -Name "BuildProfile"
    if (-not [string]::Equals([string](Get-RequiredProperty -Object $systemInspection -Name "RootModule"), "System", [StringComparison]::Ordinal) -or
        -not [string]::Equals([string](Get-RequiredProperty -Object $systemTarget -Name "Triple"), $TargetTriple, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string](Get-RequiredProperty -Object $systemProfile -Name "Name"), "release", [StringComparison]::Ordinal)) {
        throw "Staged standard-library package must be the release-built System package for '$TargetTriple'."
    }

    New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
    $seedManifest = [ordered]@{
        schemaVersion = 2
        manifestKind = $manifestKind
        state = "building"
        target = [ordered]@{
            id = [string](Get-RequiredProperty -Object $releaseTarget -Name "id")
            assetSuffix = $AssetSuffix
            runtimeIdentifier = [string](Get-RequiredProperty -Object $releaseTarget -Name "runtimeIdentifier")
            targetTriple = $TargetTriple
            operatingSystem = [string](Get-RequiredProperty -Object $releaseTarget -Name "operatingSystem")
            architecture = [string](Get-RequiredProperty -Object $releaseTarget -Name "architecture")
        }
        packages = @()
        files = @()
    }
    Write-DeterministicJson -Path (Join-Path $stageRoot "release-input.json") -Value $seedManifest

    $raylibArguments = @{
        AssetSuffix = $AssetSuffix
        TargetTriple = $TargetTriple
        OutputVendorRoot = $stageRoot
        StdlibPackageDir = $stdlibRoot
        ManifestPath = $raylibManifestFullPath
        CacheDir = (Join-Path $cacheRoot "raylib")
        ToolchainDir = $toolchainRoot
        CompilerProject = $compilerProjectPath
        ContributionManifestPath = $raylibContributionPath
        ContributorMode = $true
        Force = [bool]$Force
    }
    & $raylibScript @raylibArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Raylib Vendor contributor exited with code $LASTEXITCODE."
    }

    $coreArguments = @{
        AssetSuffix = $AssetSuffix
        TargetTriple = $TargetTriple
        OutputVendorRoot = $stageRoot
        StdlibPackageDir = $stdlibRoot
        ToolchainDir = $toolchainRoot
        CompilerProject = $compilerProjectPath
        ContributionManifestPath = $coreContributionPath
    }
    & $coreScript @coreArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Core Vendor contributor exited with code $LASTEXITCODE."
    }

    $glfwArguments = @{
        AssetSuffix = $AssetSuffix
        TargetTriple = $TargetTriple
        OutputVendorRoot = $stageRoot
        StdlibPackageDir = $stdlibRoot
        ToolchainDir = $toolchainRoot
        CompilerProject = $compilerProjectPath
        ContributionManifestPath = $glfwContributionPath
        CacheDir = (Join-Path $cacheRoot "glfw")
        Force = [bool]$Force
    }
    & $glfwScript @glfwArguments
    if ($LASTEXITCODE -ne 0) {
        throw "GLFW Vendor contributor exited with code $LASTEXITCODE."
    }

    $sdl3Arguments = @{
        AssetSuffix = $AssetSuffix
        TargetTriple = $TargetTriple
        OutputVendorRoot = $stageRoot
        StdlibPackageDir = $stdlibRoot
        ToolchainDir = $toolchainRoot
        CMakePath = $cmakeExecutable
        NinjaPath = $ninjaExecutable
        CompilerProject = $compilerProjectPath
        ContributionManifestPath = $sdl3ContributionPath
        CacheDir = (Join-Path $cacheRoot "sdl3")
        Force = [bool]$Force
    }
    & $sdl3Script @sdl3Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "SDL3 Vendor contributor exited with code $LASTEXITCODE."
    }

    $sqliteArguments = @{
        AssetSuffix = $AssetSuffix
        TargetTriple = $TargetTriple
        OutputVendorRoot = $stageRoot
        StdlibPackageDir = $stdlibRoot
        ToolchainDir = $toolchainRoot
        CompilerProject = $compilerProjectPath
        ContributionManifestPath = $sqliteContributionPath
        CacheDir = (Join-Path $cacheRoot "sqlite")
        Force = [bool]$Force
    }
    & $sqliteScript @sqliteArguments
    if ($LASTEXITCODE -ne 0) {
        throw "SQLite Vendor contributor exited with code $LASTEXITCODE."
    }

    $catalog = Get-Content -LiteralPath $vendorCatalogFullPath -Raw | ConvertFrom-Json
    if ([int](Get-RequiredProperty -Object $catalog -Name "schemaVersion") -ne 1) {
        throw "Unsupported Vendor package catalog schema."
    }
    $catalogEntries = @{}
    foreach ($entry in (Get-ArrayValues -Value (Get-RequiredProperty -Object $catalog -Name "packages"))) {
        $catalogId = [string](Get-RequiredProperty -Object $entry -Name "id")
        if ($catalogEntries.ContainsKey($catalogId)) {
            throw "Official Vendor catalog contains duplicate package '$catalogId'."
        }
        $catalogEntries.Add($catalogId, $entry)
    }

    $packages = @()
    $seenPackageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($contributionPath in @(
        $raylibContributionPath,
        $coreContributionPath,
        $glfwContributionPath,
        $sdl3ContributionPath,
        $sqliteContributionPath
    )) {
        if (-not (Test-Path -LiteralPath $contributionPath -PathType Leaf)) {
            throw "Vendor contributor did not emit '$contributionPath'."
        }
        $contribution = Get-Content -LiteralPath $contributionPath -Raw | ConvertFrom-Json
        Assert-ExactProperties -Object $contribution -Names @("schemaVersion", "targetId", "targetTriple", "packages") -Label "Vendor contribution '$contributionPath'"
        if ([int](Get-RequiredProperty -Object $contribution -Name "schemaVersion") -ne 1 -or
            -not [string]::Equals([string](Get-RequiredProperty -Object $contribution -Name "targetId"), $AssetSuffix, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string](Get-RequiredProperty -Object $contribution -Name "targetTriple"), $TargetTriple, [StringComparison]::Ordinal)) {
            throw "Vendor contribution '$contributionPath' has a mismatched schema or target."
        }

        foreach ($entry in (Get-ArrayValues -Value (Get-RequiredProperty -Object $contribution -Name "packages"))) {
            $id = [string](Get-RequiredProperty -Object $entry -Name "id")
            if (-not $seenPackageIds.Add($id)) {
                throw "Vendor package '$id' was contributed more than once."
            }
            if (-not $catalogEntries.ContainsKey($id)) {
                throw "Vendor package '$id' is absent from the official catalog."
            }
            $packages += Normalize-PackageContribution -Entry $entry -CatalogEntry $catalogEntries[$id] -Root $stageRoot
        }
    }

    $actualPackageIds = @(Get-OrdinalSortedStrings -Values @($seenPackageIds))
    $expectedPackageIds = @(Get-OrdinalSortedStrings -Values $initialPackageIds)
    if (($actualPackageIds -join "`n") -cne ($expectedPackageIds -join "`n")) {
        throw "Unified Vendor preparation emitted [$($actualPackageIds -join ', ')]; expected initial release slice [$($initialPackageIds -join ', ')]."
    }
    $packages = @(Get-OrdinalSortedObjects -Values $packages -PropertyName "id")
    Assert-UniquePackageOwnership -Packages $packages

    foreach ($item in (Get-ChildItem -LiteralPath $stageRoot -Recurse -Force)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Vendor contributor emitted symbolic link or reparse point '$($item.FullName)'."
        }
    }

    $stagedCatalogPath = Join-Path $stageRoot "catalog/vendor-packages.json"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stagedCatalogPath) | Out-Null
    Copy-Item -LiteralPath $vendorCatalogFullPath -Destination $stagedCatalogPath -Force
    $catalogRelativePath = [System.IO.Path]::GetRelativePath($stageRoot, $stagedCatalogPath).Replace('\', '/')

    $finalManifest = [ordered]@{
        schemaVersion = 2
        manifestKind = $manifestKind
        state = "ready"
        target = [ordered]@{
            id = [string](Get-RequiredProperty -Object $releaseTarget -Name "id")
            assetSuffix = $AssetSuffix
            runtimeIdentifier = [string](Get-RequiredProperty -Object $releaseTarget -Name "runtimeIdentifier")
            targetTriple = $TargetTriple
            operatingSystem = [string](Get-RequiredProperty -Object $releaseTarget -Name "operatingSystem")
            architecture = [string](Get-RequiredProperty -Object $releaseTarget -Name "architecture")
        }
        catalog = [ordered]@{
            id = [string](Get-RequiredProperty -Object $catalog -Name "catalogId")
            path = $catalogRelativePath
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $stagedCatalogPath).Hash.ToLowerInvariant()
        }
        packages = $packages
        # release-input.json intentionally excludes itself; including its own
        # digest would make the manifest recursively unstable.
        files = Get-FileInventory -Root $stageRoot
    }
    Write-DeterministicJson -Path (Join-Path $stageRoot "release-input.json") -Value $finalManifest -Depth 18

    $movedPriorOutput = $false
    try {
        if (Test-Path -LiteralPath $outputRoot) {
            Move-Item -LiteralPath $outputRoot -Destination $backupRoot
            $movedPriorOutput = $true
        }
        Move-Item -LiteralPath $stageRoot -Destination $outputRoot
    } catch {
        if ($movedPriorOutput -and -not (Test-Path -LiteralPath $outputRoot) -and (Test-Path -LiteralPath $backupRoot)) {
            Move-Item -LiteralPath $backupRoot -Destination $outputRoot
        }
        throw
    }

    if (Test-Path -LiteralPath $backupRoot) {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force
    }
} finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}

Write-Host "Prepared unified Vendor release input for $AssetSuffix at $outputRoot"
Write-Host "Packages: $($initialPackageIds -join ', ')"
