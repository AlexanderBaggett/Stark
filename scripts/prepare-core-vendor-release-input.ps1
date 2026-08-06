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

    [Parameter(Mandatory = $true)]
    [string] $ToolchainDir,

    [Parameter(Mandatory = $true)]
    [string] $ContributionManifestPath,

    [string] $CompilerProject = "src/compiler.csproj",

    [string] $VendorCatalogPath = "eng/release/vendor-packages.json",

    [string] $TargetManifestPath = "eng/release/targets.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$recipePath = "scripts/prepare-core-vendor-release-input.ps1"

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

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $property = $Object.PSObject.Properties |
        Where-Object { $_.Name -ceq $Name } |
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
        Where-Object { $_.Name -ceq $Name } |
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

function Sort-ObjectsOrdinalByProperty {
    param(
        [object[]] $Values = @(),

        [Parameter(Mandatory = $true)]
        [string] $PropertyName
    )

    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($value in $Values) {
        $items.Add($value)
    }
    $items.Sort([System.Comparison[object]] {
        param($left, $right)

        $leftFound = $false
        $rightFound = $false
        $leftValue = $null
        $rightValue = $null
        if ($left -is [System.Collections.IDictionary]) {
            $leftFound = $left.Contains($PropertyName)
            if ($leftFound) {
                $leftValue = $left[$PropertyName]
            }
        } else {
            $leftProperty = $left.PSObject.Properties[$PropertyName]
            $leftFound = $null -ne $leftProperty
            if ($leftFound) {
                $leftValue = $leftProperty.Value
            }
        }
        if ($right -is [System.Collections.IDictionary]) {
            $rightFound = $right.Contains($PropertyName)
            if ($rightFound) {
                $rightValue = $right[$PropertyName]
            }
        } else {
            $rightProperty = $right.PSObject.Properties[$PropertyName]
            $rightFound = $null -ne $rightProperty
            if ($rightFound) {
                $rightValue = $rightProperty.Value
            }
        }
        if (-not $leftFound -or -not $rightFound) {
            throw "Ordinal sort value is missing required property '$PropertyName'."
        }

        return [StringComparer]::Ordinal.Compare(
            [string]$leftValue,
            [string]$rightValue)
    })

    return $items.ToArray()
}

function Sort-StringsOrdinal {
    param(
        [string[]] $Values = @()
    )

    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($value in $Values) {
        $items.Add($value)
    }
    $items.Sort([StringComparer]::Ordinal)
    return $items.ToArray()
}

function Write-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Value,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $json = ($Value | ConvertTo-Json -Depth 40).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText(
        $Path,
        $json + "`n",
        [System.Text.UTF8Encoding]::new($false))
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSha256
    )

    if ($ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Expected SHA-256 '$ExpectedSha256' is not a lowercase 64-digit hexadecimal digest."
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Pinned Vendor release input '$Path' does not exist."
    }

    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    if ($actual -cne $ExpectedSha256) {
        throw "SHA-256 mismatch for pinned Vendor release input '$Path'. Expected $ExpectedSha256, got $actual."
    }
}

function Get-PortableRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $rootFullPath = [System.IO.Path]::GetFullPath($Root)
    $pathFullPath = [System.IO.Path]::GetFullPath($Path)
    $relativePath = [System.IO.Path]::GetRelativePath($rootFullPath, $pathFullPath).Replace('\', '/')
    if ([System.IO.Path]::IsPathRooted($relativePath) `
        -or $relativePath -eq ".." `
        -or $relativePath.StartsWith("../", [StringComparison]::Ordinal) `
        -or $relativePath.Split('/') -contains "..") {
        throw "$Label '$pathFullPath' is outside '$rootFullPath'."
    }

    return $relativePath
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
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    $relativePath = $fullPath.Substring($pathRoot.Length)
    $currentPath = $pathRoot
    foreach ($segment in $relativePath.Split(
        [System.IO.Path]::DirectorySeparatorChar,
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            continue
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release output path '$fullPath' traverses symbolic link or reparse point '$currentPath'."
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

    $filesystemRoot = [System.IO.Path]::TrimEndingDirectorySeparator(
        [System.IO.Path]::GetPathRoot($candidate))
    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    if ([string]::Equals($candidate, $filesystemRoot, $comparison)) {
        throw "Output Vendor root '$candidate' cannot be a filesystem root."
    }

    $artifactsRoot = Join-Path $repositoryRoot "artifacts"
    if (Test-IsSameOrDescendantPath -Path $candidate -Root $repositoryRoot) {
        if (-not (Test-IsSameOrDescendantPath -Path $candidate -Root $artifactsRoot) `
            -or [string]::Equals(
                $candidate,
                [System.IO.Path]::TrimEndingDirectorySeparator($artifactsRoot),
                $comparison)) {
            throw "Output Vendor root '$candidate' must be a child of '$artifactsRoot', never the repository or a source/worktree path."
        }
    }

    $protectedPaths = @(
        $repositoryRoot,
        (Join-Path $repositoryRoot "vendor"),
        (Join-Path $repositoryRoot "vendor/src"),
        [Environment]::CurrentDirectory,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($protectedPath in $protectedPaths) {
        if (Test-IsSameOrDescendantPath -Path $protectedPath -Root $candidate) {
            throw "Output Vendor root '$candidate' is or contains protected path '$protectedPath'."
        }
    }
}

function Assert-PortablePackagePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or [System.IO.Path]::IsPathRooted($Path) `
        -or $Path.Contains('\', [StringComparison]::Ordinal) `
        -or $Path -eq ".." `
        -or $Path.StartsWith("../", [StringComparison]::Ordinal) `
        -or $Path.Split('/') -contains "..") {
        throw "$Label '$Path' is not a portable package-relative path."
    }
}

function New-FileDescriptor {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [ValidateSet("documentation", "header", "license", "native-source", "provenance", "runtime-library", "static-library")]
        [string] $Kind
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Contribution file '$Path' does not exist."
    }

    $file = Get-Item -LiteralPath $Path
    return [ordered]@{
        kind = $Kind
        path = Get-PortableRelativePath -Root $Root -Path $file.FullName -Label "contribution file"
        bytes = [int64]$file.Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }
}

function New-PlainFileDescriptor {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Contribution file '$Path' does not exist."
    }

    $file = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = Get-PortableRelativePath -Root $Root -Path $file.FullName -Label "contribution file"
        bytes = [int64]$file.Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
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
        throw "Required Vendor release input '$Source' was not found."
    }

    Assert-NoReparsePointPath -Path $Destination
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Remove-OwnedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $rootFullPath = [System.IO.Path]::GetFullPath($Root)
    $pathFullPath = [System.IO.Path]::GetFullPath($Path)
    Assert-NoReparsePointPath -Path $pathFullPath
    $relativePath = Get-PortableRelativePath -Root $rootFullPath -Path $pathFullPath -Label "package-owned path"
    if ($relativePath -eq ".") {
        throw "A contributor must never replace the shared output Vendor root '$rootFullPath'."
    }

    if (Test-Path -LiteralPath $pathFullPath) {
        Remove-Item -LiteralPath $pathFullPath -Recurse -Force
    }
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

    if ($hostOperatingSystem -cne $OperatingSystem -or $hostArchitecture -cne $Architecture) {
        throw "Core Vendor release input '$AssetSuffix' must be built on $OperatingSystem-$Architecture; this host is $hostOperatingSystem-$hostArchitecture. Native source release packages are qualified only on their matching host."
    }
}

function Invoke-PackageInspection {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageImagePath,

        [Parameter(Mandatory = $true)]
        [string] $OutputPath
    )

    $arguments = @(
        "run",
        "--project", $compilerProjectPath,
        "--no-restore",
        "--",
        "inspect-pkg", $PackageImagePath,
        "--format", "json",
        "-o", $OutputPath
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Stage0 failed to inspect package image '$PackageImagePath'."
    }

    return (Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json)
}

function Get-NativeMetadataValues {
    param(
        [object] $NativeDependencies,
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($null -eq $NativeDependencies) {
        return @()
    }

    return @(Get-ArrayValues -Value (Get-OptionalProperty -Object $NativeDependencies -Name $Name) |
        ForEach-Object { [string]$_ })
}

function Assert-NativeMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Inspection,

        [Parameter(Mandatory = $true)]
        [string] $PackageId,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSource,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedIncludeDirectory,

        [string[]] $ExpectedLibraries = @(),

        [string[]] $ExpectedLinkArguments = @()
    )

    $nativeDependencies = Get-RequiredProperty -Object $Inspection -Name "NativeDependencies"
    if ($null -eq $nativeDependencies) {
        throw "Generated package '$PackageId' is missing native dependency metadata."
    }

    $sources = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "Sources")
    $includeDirectories = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "IncludeDirectories")
    $libraryDirectories = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "LibraryDirectories")
    $libraries = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "Libraries")
    $linkArguments = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "LinkArguments")
    $pkgConfigPackages = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "PkgConfigPackages")

    if (($sources -join "`n") -cne $ExpectedSource) {
        throw "Generated package '$PackageId' native sources '$($sources -join ', ')' do not equal '$ExpectedSource'."
    }
    if (($includeDirectories -join "`n") -cne $ExpectedIncludeDirectory) {
        throw "Generated package '$PackageId' include directories '$($includeDirectories -join ', ')' do not equal '$ExpectedIncludeDirectory'."
    }
    if ($libraryDirectories.Count -ne 0) {
        throw "Generated package '$PackageId' unexpectedly advertises native library directories: $($libraryDirectories -join ', ')."
    }
    if ($pkgConfigPackages.Count -ne 0) {
        throw "Generated release package '$PackageId' must not depend on pkg-config."
    }

    foreach ($path in @($sources) + @($includeDirectories) + @($libraryDirectories)) {
        Assert-PortablePackagePath -Path $path -Label "generated package '$PackageId' native path"
    }

    foreach ($library in $ExpectedLibraries) {
        if ($libraries -cnotcontains $library) {
            throw "Generated package '$PackageId' lost required native library '$library'."
        }
    }
    $knownCompilerInferredLibraries = @("m", "ws2_32", "synchronization", "ntdll")
    foreach ($library in $libraries) {
        if ($ExpectedLibraries -cnotcontains $library -and $knownCompilerInferredLibraries -cnotcontains $library) {
            throw "Generated package '$PackageId' unexpectedly advertises native library '$library'."
        }
    }

    if (($linkArguments -join "`n") -cne ($ExpectedLinkArguments -join "`n")) {
        throw "Generated package '$PackageId' native link arguments do not preserve the catalog order. Expected '$($ExpectedLinkArguments -join ' ')', got '$($linkArguments -join ' ')'."
    }

    return [ordered]@{
        sources = [object[]]$sources
        includeDirectories = [object[]]$includeDirectories
        libraryDirectories = [object[]]$libraryDirectories
        libraries = [object[]]$libraries
        linkArguments = [object[]]$linkArguments
        pkgConfigPackages = [object[]]$pkgConfigPackages
    }
}

function Assert-PackageInspection {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Inspection,

        [Parameter(Mandatory = $true)]
        [string] $PackageId,

        [Parameter(Mandatory = $true)]
        [string] $LibraryFileName,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSource,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedIncludeDirectory,

        [string[]] $ExpectedLibraries = @(),

        [string[]] $ExpectedLinkArguments = @()
    )

    if ([string](Get-RequiredProperty -Object $Inspection -Name "RootModule") -cne $PackageId) {
        throw "Generated package root is '$($Inspection.RootModule)', expected '$PackageId'."
    }
    if ([string](Get-RequiredProperty -Object $Inspection -Name "LibraryFileName") -cne $LibraryFileName) {
        throw "Generated package '$PackageId' names library '$($Inspection.LibraryFileName)', expected '$LibraryFileName'."
    }

    $target = Get-RequiredProperty -Object $Inspection -Name "Target"
    if ($null -eq $target -or [string](Get-RequiredProperty -Object $target -Name "Triple") -cne $TargetTriple) {
        throw "Generated package '$PackageId' does not preserve exact target triple '$TargetTriple'."
    }
    $profile = Get-RequiredProperty -Object $Inspection -Name "BuildProfile"
    if ($null -eq $profile -or [string](Get-RequiredProperty -Object $profile -Name "Name") -cne "release") {
        throw "Generated package '$PackageId' does not preserve the release build profile."
    }

    $moduleNames = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $Inspection -Name "Modules") |
        ForEach-Object { [string](Get-RequiredProperty -Object $_ -Name "ModuleName") })
    $moduleNames = @(Sort-StringsOrdinal -Values $moduleNames)
    if (($moduleNames -join "`n") -cne $PackageId) {
        throw "Generated package '$PackageId' owns unexpected modules: $($moduleNames -join ', ')."
    }

    $identity = Get-RequiredProperty -Object $Inspection -Name "Identity"
    if ($null -eq $identity -or [string](Get-RequiredProperty -Object $identity -Name "PackageId") -cne $PackageId) {
        throw "Generated package '$PackageId' is missing its package identity."
    }
    $dependencies = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $identity -Name "Dependencies"))
    $dependencyIds = @($dependencies |
        ForEach-Object { [string](Get-RequiredProperty -Object $_ -Name "PackageId") })
    $dependencyIds = @(Sort-StringsOrdinal -Values $dependencyIds)
    if (($dependencyIds -join "`n") -cne "System") {
        throw "Generated package '$PackageId' must depend only on the staged System package; got '$($dependencyIds -join ', ')'."
    }

    $native = Assert-NativeMetadata `
        -Inspection $Inspection `
        -PackageId $PackageId `
        -ExpectedSource $ExpectedSource `
        -ExpectedIncludeDirectory $ExpectedIncludeDirectory `
        -ExpectedLibraries $ExpectedLibraries `
        -ExpectedLinkArguments $ExpectedLinkArguments

    return [ordered]@{
        modules = [object[]]$moduleNames
        native = $native
        identity = [ordered]@{
            packageId = [string](Get-RequiredProperty -Object $identity -Name "PackageId")
            apiHash = [string](Get-RequiredProperty -Object $identity -Name "ApiHash")
            contentHash = [string](Get-RequiredProperty -Object $identity -Name "ContentHash")
            dependencies = [object[]]@($dependencies | ForEach-Object {
                [ordered]@{
                    packageId = [string](Get-RequiredProperty -Object $_ -Name "PackageId")
                    apiHash = [string](Get-RequiredProperty -Object $_ -Name "ApiHash")
                    contentHash = [string](Get-RequiredProperty -Object $_ -Name "ContentHash")
                }
            })
        }
    }
}

$compilerProjectPath = Resolve-RepositoryPath -Path $CompilerProject
$vendorCatalogFullPath = Resolve-RepositoryPath -Path $VendorCatalogPath
$targetManifestFullPath = Resolve-RepositoryPath -Path $TargetManifestPath
$outputRoot = Resolve-RepositoryPath -Path $OutputVendorRoot
$stdlibPackageRoot = Resolve-RepositoryPath -Path $StdlibPackageDir
$toolchainPath = Resolve-RepositoryPath -Path $ToolchainDir
$contributionPath = Resolve-RepositoryPath -Path $ContributionManifestPath

Assert-SafeOutputRoot -Path $outputRoot
Assert-NoReparsePointPath -Path $contributionPath
if (Test-IsSameOrDescendantPath -Path $contributionPath -Root $outputRoot) {
    throw "Contribution manifest '$contributionPath' must be outside shared OutputVendorRoot '$outputRoot'."
}

foreach ($requiredFile in @($compilerProjectPath, $vendorCatalogFullPath, $targetManifestFullPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required release input '$requiredFile' does not exist."
    }
}
foreach ($requiredDirectory in @($stdlibPackageRoot, $toolchainPath)) {
    if (-not (Test-Path -LiteralPath $requiredDirectory -PathType Container)) {
        throw "Required release input directory '$requiredDirectory' does not exist."
    }
}

$targetManifest = Get-Content -LiteralPath $targetManifestFullPath -Raw | ConvertFrom-Json
if ([int](Get-RequiredProperty -Object $targetManifest -Name "schemaVersion") -ne 1) {
    throw "Unsupported release target manifest schema."
}
$targetMatches = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $targetManifest -Name "targets") |
    Where-Object { [string](Get-RequiredProperty -Object $_ -Name "id") -ceq $AssetSuffix })
if ($targetMatches.Count -ne 1) {
    throw "Release target manifest must contain exactly one '$AssetSuffix' entry."
}
$targetEntry = $targetMatches[0]
$manifestTargetTriple = [string](Get-RequiredProperty -Object $targetEntry -Name "targetTriple")
if ($TargetTriple.Trim() -cne $manifestTargetTriple) {
    throw "Target triple '$TargetTriple' does not match '$AssetSuffix' manifest triple '$manifestTargetTriple'."
}
$targetOperatingSystem = [string](Get-RequiredProperty -Object $targetEntry -Name "operatingSystem")
$targetArchitecture = [string](Get-RequiredProperty -Object $targetEntry -Name "architecture")
Assert-MatchingHost -OperatingSystem $targetOperatingSystem -Architecture $targetArchitecture

$vendorCatalog = Get-Content -LiteralPath $vendorCatalogFullPath -Raw | ConvertFrom-Json
if ([int](Get-RequiredProperty -Object $vendorCatalog -Name "schemaVersion") -ne 1) {
    throw "Unsupported Vendor package catalog schema."
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$stagedSourceRoot = Join-Path $outputRoot "src"
$targetDist = Join-Path $outputRoot (Join-Path "dist" $AssetSuffix)
Assert-NoReparsePointPath -Path $stagedSourceRoot
Assert-NoReparsePointPath -Path $targetDist
New-Item -ItemType Directory -Force -Path $stagedSourceRoot | Out-Null
New-Item -ItemType Directory -Force -Path $targetDist | Out-Null

$workRoot = Join-Path $repositoryRoot (Join-Path "artifacts/core-vendor-work" (Join-Path $AssetSuffix ([Guid]::NewGuid().ToString("N"))))
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

try {
$systemImages = @(Get-ChildItem -LiteralPath $stdlibPackageRoot -File -Recurse -Filter "*.starkpkg" | Sort-Object FullName)
if ($systemImages.Count -eq 0) {
    throw "Staged standard-library directory '$stdlibPackageRoot' contains no package image."
}
$systemInspections = @()
foreach ($systemImage in $systemImages) {
    $inspectionPath = Join-Path $workRoot ("system-" + $systemImage.Name + ".json")
    $inspection = Invoke-PackageInspection -PackageImagePath $systemImage.FullName -OutputPath $inspectionPath
    if ([string](Get-RequiredProperty -Object $inspection -Name "RootModule") -ceq "System") {
        $systemInspections += [pscustomobject]@{ Image = $systemImage; Inspection = $inspection }
    }
}
if ($systemInspections.Count -ne 1) {
    throw "Staged standard-library directory '$stdlibPackageRoot' must contain exactly one System package; found $($systemInspections.Count)."
}
$systemInspection = $systemInspections[0].Inspection
$systemTarget = Get-RequiredProperty -Object $systemInspection -Name "Target"
$systemProfile = Get-RequiredProperty -Object $systemInspection -Name "BuildProfile"
if ($null -eq $systemTarget -or [string](Get-RequiredProperty -Object $systemTarget -Name "Triple") -cne $TargetTriple) {
    throw "Staged System package target does not match '$TargetTriple'."
}
if ($null -eq $systemProfile -or [string](Get-RequiredProperty -Object $systemProfile -Name "Name") -cne "release") {
    throw "Staged System package must be built with --package-profile release."
}
$systemIdentity = Get-RequiredProperty -Object $systemInspection -Name "Identity"

$packageDefinitions = @(
    [pscustomobject]@{
        Id = "Vendor.STB.Image"
        SourcePath = "vendor/src/Vendor/STB/Image.stark"
        StagedSourcePath = "src/Vendor/STB/Image.stark"
        ImplementationPath = "vendor/StbImageImplementation.c"
        ImplementationFile = "StbImageImplementation.c"
        NativeSlug = "stb"
        LibraryStem = "VendorSTBImage"
    },
    [pscustomobject]@{
        Id = "Vendor.Miniaudio"
        SourcePath = "vendor/src/Vendor/Miniaudio.stark"
        StagedSourcePath = "src/Vendor/Miniaudio.stark"
        ImplementationPath = "vendor/MiniaudioImplementation.c"
        ImplementationFile = "MiniaudioImplementation.c"
        NativeSlug = "miniaudio"
        LibraryStem = "VendorMiniaudio"
    },
    [pscustomobject]@{
        Id = "Vendor.Cgltf"
        SourcePath = "vendor/src/Vendor/Cgltf.stark"
        StagedSourcePath = "src/Vendor/Cgltf.stark"
        ImplementationPath = "vendor/CgltfImplementation.c"
        ImplementationFile = "CgltfImplementation.c"
        NativeSlug = "cgltf"
        LibraryStem = "VendorCgltf"
    }
)

$catalogPackages = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $vendorCatalog -Name "packages"))
$packageContributions = @()
foreach ($definition in $packageDefinitions) {
    $catalogMatches = @($catalogPackages | Where-Object {
        [string](Get-RequiredProperty -Object $_ -Name "id") -ceq $definition.Id
    })
    if ($catalogMatches.Count -ne 1) {
        throw "Vendor catalog must contain exactly one '$($definition.Id)' package."
    }
    $package = $catalogMatches[0]
    if ([string](Get-RequiredProperty -Object $package -Name "buildRecipe") -cne $recipePath) {
        throw "Vendor package '$($definition.Id)' must name '$recipePath' as its release build recipe."
    }

    $targetSupport = Get-RequiredProperty -Object $package -Name "targetSupport"
    $support = [string](Get-RequiredProperty -Object $targetSupport -Name $AssetSuffix)
    if ($support -cne "required-source-build") {
        throw "Vendor package '$($definition.Id)' target '$AssetSuffix' must be a required-source-build; catalog value is '$support'."
    }

    $sourceFiles = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $package -Name "sourceFiles"))
    if ($sourceFiles.Count -eq 0) {
        throw "Vendor package '$($definition.Id)' has no pinned source-file hashes."
    }
    foreach ($sourceFile in $sourceFiles) {
        $sourcePath = Resolve-RepositoryPath -Path ([string](Get-RequiredProperty -Object $sourceFile -Name "path"))
        Assert-Sha256 `
            -Path $sourcePath `
            -ExpectedSha256 ([string](Get-RequiredProperty -Object $sourceFile -Name "sha256"))
    }

    $stagedPackageSource = Join-Path $outputRoot $definition.StagedSourcePath
    $stagedImplementation = Join-Path $targetDist $definition.ImplementationFile
    $stagedNativeRoot = Join-Path $targetDist (Join-Path "native" $definition.NativeSlug)
    $libraryFileName = if ($targetOperatingSystem -ceq "windows") {
        "$($definition.LibraryStem).lib"
    } else {
        "lib$($definition.LibraryStem).a"
    }
    $libraryPath = Join-Path $targetDist $libraryFileName
    $packageImagePath = [System.IO.Path]::ChangeExtension($libraryPath, ".starkpkg")

    foreach ($ownedPath in @(
        $stagedPackageSource,
        $stagedImplementation,
        $stagedNativeRoot,
        $libraryPath,
        $packageImagePath
    )) {
        Remove-OwnedPath -Root $outputRoot -Path $ownedPath
    }

    Copy-RequiredFile `
        -Source (Resolve-RepositoryPath -Path $definition.SourcePath) `
        -Destination $stagedPackageSource
    Copy-RequiredFile `
        -Source (Resolve-RepositoryPath -Path $definition.ImplementationPath) `
        -Destination $stagedImplementation
    New-Item -ItemType Directory -Force -Path $stagedNativeRoot | Out-Null

    $nativeArtifactInputs = @(
        [pscustomobject]@{ Path = $stagedImplementation; Kind = "native-source" }
    )
    foreach ($sourceFile in $sourceFiles) {
        $sourcePath = Resolve-RepositoryPath -Path ([string](Get-RequiredProperty -Object $sourceFile -Name "path"))
        $destination = Join-Path $stagedNativeRoot ([System.IO.Path]::GetFileName($sourcePath))
        Copy-RequiredFile -Source $sourcePath -Destination $destination
        $nativeArtifactInputs += [pscustomobject]@{ Path = $destination; Kind = "header" }
    }

    $sourceDirectory = Split-Path -Parent (Resolve-RepositoryPath -Path ([string](Get-RequiredProperty -Object $sourceFiles[0] -Name "path")))
    $versionSource = Join-Path $sourceDirectory "VERSION.md"
    $versionDestination = Join-Path $stagedNativeRoot "VERSION.md"
    Copy-RequiredFile -Source $versionSource -Destination $versionDestination
    $nativeArtifactInputs += [pscustomobject]@{ Path = $versionDestination; Kind = "documentation" }

    $licensePaths = @()
    foreach ($licenseEvidencePath in (Get-ArrayValues -Value (Get-RequiredProperty -Object $package -Name "licenseEvidencePaths"))) {
        $licenseSource = Resolve-RepositoryPath -Path ([string]$licenseEvidencePath)
        if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) {
            throw "License evidence '$licenseSource' for '$($definition.Id)' does not exist."
        }
        $licenseDestination = Join-Path $stagedNativeRoot ("LICENSE." + [System.IO.Path]::GetFileName($licenseSource))
        Copy-RequiredFile -Source $licenseSource -Destination $licenseDestination
        $licensePaths += $licenseDestination
        $nativeArtifactInputs += [pscustomobject]@{ Path = $licenseDestination; Kind = "license" }
    }
    if ($licensePaths.Count -eq 0) {
        throw "Vendor package '$($definition.Id)' must stage explicit license evidence."
    }

    $systemLinkFacts = Get-RequiredProperty -Object $package -Name "systemLinkFacts"
    $declaredSystemLinks = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $systemLinkFacts -Name $targetOperatingSystem) |
        ForEach-Object { [string]$_ })
    $nativeLibraries = if ($targetOperatingSystem -ceq "macos") { @() } else { @($declaredSystemLinks) }
    $nativeLinkArguments = @()
    if ($targetOperatingSystem -ceq "macos") {
        foreach ($framework in $declaredSystemLinks) {
            $nativeLinkArguments += @("-framework", $framework)
        }
    }

    $compilerArguments = @(
        "run",
        "--project", $compilerProjectPath,
        "--no-restore",
        "--",
        $stagedPackageSource,
        "--emit-lib",
        "--no-stark-path",
        "-I", $stagedSourceRoot,
        "-I", $stdlibPackageRoot,
        "-o", $libraryPath,
        "--target", $TargetTriple,
        "--package-profile", "release",
        "--toolchain-dir", $toolchainPath,
        "--native-source", $stagedImplementation,
        "--native-include-dir", $stagedNativeRoot
    )
    foreach ($library in $nativeLibraries) {
        $compilerArguments += @("--native-library", $library)
    }
    foreach ($linkArgument in $nativeLinkArguments) {
        $compilerArguments += @("--native-link-arg", $linkArgument)
    }

    & dotnet @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Stage0 failed to build release package '$($definition.Id)' for '$TargetTriple'."
    }
    if (-not (Test-Path -LiteralPath $libraryPath -PathType Leaf)) {
        throw "Stage0 did not emit '$libraryPath'."
    }
    if (-not (Test-Path -LiteralPath $packageImagePath -PathType Leaf)) {
        throw "Stage0 did not emit '$packageImagePath'."
    }

    $inspectionPath = Join-Path $workRoot ($definition.LibraryStem + ".starkpkg.json")
    $inspection = Invoke-PackageInspection -PackageImagePath $packageImagePath -OutputPath $inspectionPath
    $validatedInspection = Assert-PackageInspection `
        -Inspection $inspection `
        -PackageId $definition.Id `
        -LibraryFileName $libraryFileName `
        -ExpectedSource $definition.ImplementationFile `
        -ExpectedIncludeDirectory ("native/" + $definition.NativeSlug) `
        -ExpectedLibraries $nativeLibraries `
        -ExpectedLinkArguments $nativeLinkArguments

    $sourceInputDescriptors = @($sourceFiles | ForEach-Object {
        $path = Resolve-RepositoryPath -Path ([string](Get-RequiredProperty -Object $_ -Name "path"))
        $file = Get-Item -LiteralPath $path
        [ordered]@{
            path = Get-PortableRelativePath -Root $repositoryRoot -Path $file.FullName -Label "pinned source"
            bytes = [int64]$file.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }
    })
    $sourceInputDescriptors = @(Sort-ObjectsOrdinalByProperty `
        -Values $sourceInputDescriptors `
        -PropertyName "path")
    $implementationSource = Get-Item -LiteralPath (Resolve-RepositoryPath -Path $definition.ImplementationPath)
    $starkSource = Get-Item -LiteralPath (Resolve-RepositoryPath -Path $definition.SourcePath)
    $versionSourceFile = Get-Item -LiteralPath $versionSource

    $provenanceValue = [ordered]@{
        schemaVersion = 1
        packageId = $definition.Id
        version = [string](Get-RequiredProperty -Object $package -Name "version")
        sourceIdentity = [string](Get-RequiredProperty -Object $package -Name "sourceIdentity")
        upstreamUrl = [string](Get-RequiredProperty -Object $package -Name "upstreamUrl")
        license = [string](Get-RequiredProperty -Object $package -Name "license")
        buildRecipe = $recipePath
        target = [ordered]@{
            id = $AssetSuffix
            targetTriple = $TargetTriple
            operatingSystem = $targetOperatingSystem
            architecture = $targetArchitecture
            packageProfile = "release"
        }
        stagedSystemIdentity = [ordered]@{
            packageId = [string](Get-RequiredProperty -Object $systemIdentity -Name "PackageId")
            apiHash = [string](Get-RequiredProperty -Object $systemIdentity -Name "ApiHash")
            contentHash = [string](Get-RequiredProperty -Object $systemIdentity -Name "ContentHash")
            imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $systemInspections[0].Image.FullName).Hash.ToLowerInvariant()
        }
        sourceInputs = [object[]]$sourceInputDescriptors
        starkSource = [ordered]@{
            path = Get-PortableRelativePath -Root $repositoryRoot -Path $starkSource.FullName -Label "Stark source"
            bytes = [int64]$starkSource.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $starkSource.FullName).Hash.ToLowerInvariant()
        }
        implementationSource = [ordered]@{
            path = Get-PortableRelativePath -Root $repositoryRoot -Path $implementationSource.FullName -Label "native implementation source"
            bytes = [int64]$implementationSource.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $implementationSource.FullName).Hash.ToLowerInvariant()
        }
        versionEvidence = [ordered]@{
            path = Get-PortableRelativePath -Root $repositoryRoot -Path $versionSourceFile.FullName -Label "version evidence"
            bytes = [int64]$versionSourceFile.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $versionSourceFile.FullName).Hash.ToLowerInvariant()
        }
        declaredSystemLinkFacts = [object[]]$declaredSystemLinks
        emittedPackageFacts = $validatedInspection
    }
    $provenancePath = Join-Path $stagedNativeRoot "PROVENANCE.json"
    Write-DeterministicJson -Value $provenanceValue -Path $provenancePath

    # The include directory is package-owned and recursively inventoried by SDK
    # assembly. Keep the contribution artifact set exactly equal to that scan plus
    # the separately declared native source, including license and provenance files.
    $nativeArtifactInputs += [pscustomobject]@{ Path = $provenancePath; Kind = "provenance" }

    $artifactDescriptors = @($nativeArtifactInputs | ForEach-Object {
        New-FileDescriptor -Root $outputRoot -Path $_.Path -Kind $_.Kind
    })
    $artifactDescriptors = @(Sort-ObjectsOrdinalByProperty `
        -Values $artifactDescriptors `
        -PropertyName "path")
    $licenseDescriptors = @($licensePaths | ForEach-Object {
        New-PlainFileDescriptor -Root $outputRoot -Path $_
    })
    $licenseDescriptors = @(Sort-ObjectsOrdinalByProperty `
        -Values $licenseDescriptors `
        -PropertyName "path")
    $provenanceDescriptor = New-PlainFileDescriptor -Root $outputRoot -Path $provenancePath

    $packageContributions += [ordered]@{
        id = $definition.Id
        version = [string](Get-RequiredProperty -Object $package -Name "version")
        sourceIdentity = [string](Get-RequiredProperty -Object $package -Name "sourceIdentity")
        target = [ordered]@{
            id = $AssetSuffix
            targetTriple = $TargetTriple
        }
        package = [ordered]@{
            rootModule = $definition.Id
            image = Get-PortableRelativePath -Root $outputRoot -Path $packageImagePath -Label "package image"
            imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $packageImagePath).Hash.ToLowerInvariant()
            library = Get-PortableRelativePath -Root $outputRoot -Path $libraryPath -Label "package library"
            librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $libraryPath).Hash.ToLowerInvariant()
            modules = [object[]]@(Sort-StringsOrdinal -Values @($validatedInspection.modules))
        }
        nativePayload = [ordered]@{
            artifacts = [object[]]$artifactDescriptors
            licenseFiles = [object[]]$licenseDescriptors
        }
        provenance = $provenanceDescriptor
    }
}

$packageContributions = @(Sort-ObjectsOrdinalByProperty `
    -Values $packageContributions `
    -PropertyName "id")
$contribution = [ordered]@{
    schemaVersion = 1
    targetId = $AssetSuffix
    targetTriple = $TargetTriple
    packages = [object[]]$packageContributions
}
Write-DeterministicJson -Value $contribution -Path $contributionPath

Write-Host "Prepared STB Image, Miniaudio, and cgltf release package contributions for '$AssetSuffix'."
Write-Host "Contribution manifest: $contributionPath"
} finally {
    if (Test-Path -LiteralPath $workRoot) {
        $workParent = Join-Path $repositoryRoot (Join-Path "artifacts/core-vendor-work" $AssetSuffix)
        if (-not (Test-IsSameOrDescendantPath -Path $workRoot -Root $workParent)) {
            throw "Refusing to clean unexpected core Vendor work root '$workRoot'."
        }
        Assert-NoReparsePointPath -Path $workRoot
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
