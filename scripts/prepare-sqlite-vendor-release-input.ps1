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

    [string] $TargetManifestPath = "eng/release/targets.json",

    [string] $CacheDir = "artifacts/sqlite-cache",

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$recipePath = "scripts/prepare-sqlite-vendor-release-input.ps1"
$packageId = "Vendor.SQLite"

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
        [Parameter(Mandatory = $true)][object] $Object,
        [Parameter(Mandatory = $true)][string] $Name
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
    param([object] $Value)

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
        [Parameter(Mandatory = $true)][string] $PropertyName
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

        return [StringComparer]::Ordinal.Compare([string]$leftValue, [string]$rightValue)
    })
    return $items.ToArray()
}

function Sort-StringsOrdinal {
    param([string[]] $Values = @())

    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($value in $Values) {
        $items.Add($value)
    }
    $items.Sort([StringComparer]::Ordinal)
    return $items.ToArray()
}

function Write-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)][object] $Value,
        [Parameter(Mandatory = $true)][string] $Path
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $json = ($Value | ConvertTo-Json -Depth 50).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText(
        $Path,
        $json + "`n",
        [System.Text.UTF8Encoding]::new($false))
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

    return $candidate.StartsWith(
        $rootPath + [System.IO.Path]::DirectorySeparatorChar,
        $comparison)
}

function Assert-NoReparsePointPath {
    param([Parameter(Mandatory = $true)][string] $Path)

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
            throw "Release path '$fullPath' traverses symbolic link or reparse point '$currentPath'."
        }
    }
}

function Assert-SafeOutputRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    Assert-NoReparsePointPath -Path $candidate
    $filesystemRoot = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetPathRoot($candidate))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
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

function Get-PortableRelativePath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
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

function Assert-PortablePackagePath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
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

function Remove-OwnedPath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
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

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required SQLite release input '$Source' was not found."
    }
    Assert-NoReparsePointPath -Path $Destination
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Required SQLite source directory '$Source' was not found."
    }
    Assert-NoReparsePointPath -Path $Destination
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in (Get-ChildItem -LiteralPath $Source -Force | Sort-Object Name)) {
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) {
            Copy-DirectoryContents -Source $item.FullName -Destination $target
        } else {
            Copy-RequiredFile -Source $item.FullName -Destination $target
        }
    }
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ExpectedSha256
    )

    if ($ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Expected SHA-256 '$ExpectedSha256' is not a lowercase 64-digit hexadecimal digest."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Pinned SQLite source '$Path' does not exist."
    }

    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    if ($actual -cne $ExpectedSha256) {
        throw "SHA-256 mismatch for pinned SQLite source '$Path'. Expected $ExpectedSha256, got $actual."
    }
}

function Expand-VerifiedZipArchive {
    param(
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][string] $DestinationRoot
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Force -Path $DestinationRoot | Out-Null
    $destinationFullPath = [System.IO.Path]::GetFullPath($DestinationRoot)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $entryPath = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($entryPath) `
                -or $entryPath.StartsWith('/', [StringComparison]::Ordinal) `
                -or $entryPath.Split('/') -contains ".." `
                -or $entryPath.Contains(':', [StringComparison]::Ordinal)) {
                throw "Pinned SQLite archive contains unsafe entry '$entryPath'."
            }

            $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $destinationFullPath $entryPath))
            if (-not (Test-IsSameOrDescendantPath -Path $destinationPath -Root $destinationFullPath)) {
                throw "Pinned SQLite archive entry '$entryPath' escapes extraction root."
            }
            if ([string]::IsNullOrEmpty($entry.Name)) {
                New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
                continue
            }

            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPath) | Out-Null
            $sourceStream = $entry.Open()
            try {
                $destinationStream = [System.IO.File]::Create($destinationPath)
                try {
                    $sourceStream.CopyTo($destinationStream)
                } finally {
                    $destinationStream.Dispose()
                }
            } finally {
                $sourceStream.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
    }
}

function Assert-MatchingHost {
    param(
        [Parameter(Mandatory = $true)][string] $OperatingSystem,
        [Parameter(Mandatory = $true)][string] $Architecture
    )

    $hostOperatingSystem = if ($IsWindows) { "windows" } elseif ($IsLinux) { "linux" } elseif ($IsMacOS) { "macos" } else { "unknown" }
    $hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    if ($hostOperatingSystem -cne $OperatingSystem -or $hostArchitecture -cne $Architecture) {
        throw "SQLite release input '$AssetSuffix' must be built on $OperatingSystem-$Architecture; this host is $hostOperatingSystem-$hostArchitecture."
    }
}

function Assert-NativeObjectTarget {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $TargetId
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 20) {
        throw "SQLite native object '$Path' is too short to contain a target header."
    }

    $matches = switch ($TargetId) {
        "linux-x64" {
            $bytes[0] -eq 0x7f -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x4c -and $bytes[3] -eq 0x46 `
                -and $bytes[4] -eq 2 -and $bytes[5] -eq 1 -and $bytes[18] -eq 0x3e -and $bytes[19] -eq 0
        }
        "linux-arm64" {
            $bytes[0] -eq 0x7f -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x4c -and $bytes[3] -eq 0x46 `
                -and $bytes[4] -eq 2 -and $bytes[5] -eq 1 -and $bytes[18] -eq 0xb7 -and $bytes[19] -eq 0
        }
        "windows-x64" {
            $bytes[0] -eq 0x64 -and $bytes[1] -eq 0x86
        }
        "windows-arm64" {
            $bytes[0] -eq 0x64 -and $bytes[1] -eq 0xaa
        }
        "macos-x64" {
            $bytes[0] -eq 0xcf -and $bytes[1] -eq 0xfa -and $bytes[2] -eq 0xed -and $bytes[3] -eq 0xfe `
                -and $bytes[4] -eq 0x07 -and $bytes[5] -eq 0 -and $bytes[6] -eq 0 -and $bytes[7] -eq 1
        }
        "macos-arm64" {
            $bytes[0] -eq 0xcf -and $bytes[1] -eq 0xfa -and $bytes[2] -eq 0xed -and $bytes[3] -eq 0xfe `
                -and $bytes[4] -eq 0x0c -and $bytes[5] -eq 0 -and $bytes[6] -eq 0 -and $bytes[7] -eq 1
        }
        default { $false }
    }
    if (-not $matches) {
        throw "SQLite native object '$Path' does not match target '$TargetId'."
    }
}

function Assert-StaticLibraryArchive {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string[]] $ExpectedMembers,
        [Parameter(Mandatory = $true)][string] $ArchiverPath
    )

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $header = [byte[]]::new(8)
        if ($stream.Read($header, 0, $header.Length) -ne $header.Length `
            -or [System.Text.Encoding]::ASCII.GetString($header) -cne "!<arch>`n") {
            throw "SQLite native library '$Path' is not an ar archive."
        }
    } finally {
        $stream.Dispose()
    }

    $members = @(& $ArchiverPath t $Path 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect SQLite native archive '$Path': $($members -join [Environment]::NewLine)"
    }
    $memberNames = @($members | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    if (($memberNames -join "`n") -cne ($ExpectedMembers -join "`n")) {
        throw "SQLite native archive '$Path' must contain '$($ExpectedMembers -join ', ')' in deterministic order; found '$($memberNames -join ', ')'."
    }
}

function Invoke-PackageInspection {
    param(
        [Parameter(Mandatory = $true)][string] $PackageImagePath,
        [Parameter(Mandatory = $true)][string] $OutputPath
    )

    & dotnet run --project $compilerProjectPath --no-restore -- inspect-pkg $PackageImagePath --format json -o $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Stage0 failed to inspect package image '$PackageImagePath'."
    }
    return (Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json)
}

function Get-NativeMetadataValues {
    param(
        [object] $NativeDependencies,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($null -eq $NativeDependencies) {
        return @()
    }
    return @(Get-ArrayValues -Value (Get-OptionalProperty -Object $NativeDependencies -Name $Name) |
        ForEach-Object { [string]$_ })
}

function New-FileDescriptor {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)]
        [ValidateSet("documentation", "header", "license", "native-source", "provenance", "runtime-library", "static-library")]
        [string] $Kind
    )

    $file = Get-Item -LiteralPath $Path
    return [ordered]@{
        kind = $Kind
        path = Get-PortableRelativePath -Root $Root -Path $file.FullName -Label "SQLite contribution file"
        bytes = [int64]$file.Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }
}

function New-PlainFileDescriptor {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $file = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = Get-PortableRelativePath -Root $Root -Path $file.FullName -Label "SQLite contribution file"
        bytes = [int64]$file.Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }
}

$compilerProjectPath = Resolve-RepositoryPath -Path $CompilerProject
$runtimeSmokeSourcePath = Join-Path $repositoryRoot "tests/fixtures/release/SQLiteBundledOptionalSmoke.stark"
$vendorCatalogFullPath = Resolve-RepositoryPath -Path $VendorCatalogPath
$targetManifestFullPath = Resolve-RepositoryPath -Path $TargetManifestPath
$outputRoot = Resolve-RepositoryPath -Path $OutputVendorRoot
$stdlibPackageRoot = Resolve-RepositoryPath -Path $StdlibPackageDir
$toolchainPath = Resolve-RepositoryPath -Path $ToolchainDir
$contributionPath = Resolve-RepositoryPath -Path $ContributionManifestPath
$cacheRoot = Resolve-RepositoryPath -Path $CacheDir

Assert-SafeOutputRoot -Path $outputRoot
Assert-NoReparsePointPath -Path $contributionPath
if (Test-IsSameOrDescendantPath -Path $contributionPath -Root $outputRoot) {
    throw "Contribution manifest '$contributionPath' must be outside shared OutputVendorRoot '$outputRoot'."
}
foreach ($requiredFile in @($compilerProjectPath, $runtimeSmokeSourcePath, $vendorCatalogFullPath, $targetManifestFullPath)) {
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
$catalogMatches = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $vendorCatalog -Name "packages") |
    Where-Object { [string](Get-RequiredProperty -Object $_ -Name "id") -ceq $packageId })
if ($catalogMatches.Count -ne 1) {
    throw "Vendor catalog must contain exactly one '$packageId' entry."
}
$package = $catalogMatches[0]
if ([string](Get-RequiredProperty -Object $package -Name "buildRecipe") -cne $recipePath) {
    throw "Vendor package '$packageId' must name '$recipePath' as its release build recipe."
}
$support = [string](Get-RequiredProperty `
    -Object (Get-RequiredProperty -Object $package -Name "targetSupport") `
    -Name $AssetSuffix)
if ($support -cne "required-source-build") {
    throw "Vendor package '$packageId' target '$AssetSuffix' must be a required-source-build; catalog value is '$support'."
}

$sourceUrl = [string](Get-RequiredProperty -Object $package -Name "sourceUrl")
$sourceSha256 = ([string](Get-RequiredProperty -Object $package -Name "sourceSha256")).ToLowerInvariant()
$sourceSize = [int64](Get-RequiredProperty -Object $package -Name "sourceSize")
$sourcePayloadRoot = [string](Get-RequiredProperty -Object $package -Name "sourcePayloadRoot")
$sourceDateEpoch = [int64](Get-RequiredProperty -Object $package -Name "sourceDateEpoch")
$sourceArchiveFiles = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $package -Name "sourceArchiveFiles"))
$compileDefinitions = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $package -Name "compileDefinitions") |
    ForEach-Object { [string]$_ })
$adapterCompileDefinitions = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $package -Name "adapterCompileDefinitions") |
    ForEach-Object { [string]$_ })
if ($sourceSha256 -cnotmatch '^[0-9a-f]{64}$' -or $sourceSize -le 0 -or $sourceArchiveFiles.Count -eq 0 `
    -or $compileDefinitions.Count -eq 0 -or $adapterCompileDefinitions.Count -eq 0) {
    throw "Vendor package '$packageId' has incomplete pinned-source/build policy."
}

$archiveName = [System.IO.Path]::GetFileName(([Uri]$sourceUrl).AbsolutePath)
if ($archiveName -cne "sqlite-amalgamation-3530200.zip") {
    throw "Unexpected SQLite source archive name '$archiveName'."
}
$archiveCacheRoot = Join-Path $cacheRoot $sourceSha256
New-Item -ItemType Directory -Force -Path $archiveCacheRoot | Out-Null
$archivePath = Join-Path $archiveCacheRoot $archiveName
if ($Force -or -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    $downloadPath = "$archivePath.download-$([Guid]::NewGuid().ToString('N'))"
    try {
        Invoke-WebRequest -Uri $sourceUrl -OutFile $downloadPath
        Assert-Sha256 -Path $downloadPath -ExpectedSha256 $sourceSha256
        if ((Get-Item -LiteralPath $downloadPath).Length -ne $sourceSize) {
            throw "SQLite archive size mismatch. Expected $sourceSize bytes."
        }
        Move-Item -LiteralPath $downloadPath -Destination $archivePath -Force
    } finally {
        if (Test-Path -LiteralPath $downloadPath -PathType Leaf) {
            Remove-Item -LiteralPath $downloadPath -Force
        }
    }
}
Assert-Sha256 -Path $archivePath -ExpectedSha256 $sourceSha256
if ((Get-Item -LiteralPath $archivePath).Length -ne $sourceSize) {
    throw "Pinned SQLite archive '$archivePath' has the wrong size. Expected $sourceSize bytes."
}

$workRoot = Join-Path $repositoryRoot (Join-Path "artifacts/sqlite-work" (Join-Path $AssetSuffix ([Guid]::NewGuid().ToString("N"))))
$extractRoot = Join-Path $workRoot "extract"
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
try {
Expand-VerifiedZipArchive -ArchivePath $archivePath -DestinationRoot $extractRoot
$sourceRoot = Join-Path $extractRoot $sourcePayloadRoot
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Pinned SQLite payload root '$sourcePayloadRoot' was not found in '$archiveName'."
}

$verifiedSourceFiles = @()
foreach ($archiveFile in $sourceArchiveFiles) {
    $relativePath = [string](Get-RequiredProperty -Object $archiveFile -Name "path")
    Assert-PortablePackagePath -Path $relativePath -Label "SQLite source archive path"
    $sourcePath = Join-Path $sourceRoot $relativePath
    Assert-Sha256 `
        -Path $sourcePath `
        -ExpectedSha256 (([string](Get-RequiredProperty -Object $archiveFile -Name "sha256")).ToLowerInvariant())
    $expectedSize = [int64](Get-RequiredProperty -Object $archiveFile -Name "bytes")
    if ((Get-Item -LiteralPath $sourcePath).Length -ne $expectedSize) {
        throw "Pinned SQLite source '$relativePath' has the wrong size. Expected $expectedSize bytes."
    }
    $verifiedSourceFiles += [ordered]@{
        path = $relativePath
        bytes = $expectedSize
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash.ToLowerInvariant()
    }
}
$verifiedSourceFiles = @(Sort-ObjectsOrdinalByProperty -Values $verifiedSourceFiles -PropertyName "path")

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$stagedSourceRoot = Join-Path $outputRoot "src"
$stagedVendorRoot = Join-Path $stagedSourceRoot "Vendor"
$stagedSqliteRootSource = Join-Path $stagedVendorRoot "SQLite.stark"
$stagedSqliteModuleDirectory = Join-Path $stagedVendorRoot "SQLite"
$targetDist = Join-Path $outputRoot (Join-Path "dist" $AssetSuffix)
$legacyStagedBindingSource = Join-Path $targetDist "SQLiteTextBinding.c"
$nativeSqliteRoot = Join-Path $targetDist "native/sqlite"
$nativeLibraryFileName = if ($targetOperatingSystem -ceq "windows") { "sqlite3.lib" } else { "libsqlite3.a" }
$nativeLibraryPath = Join-Path $nativeSqliteRoot $nativeLibraryFileName
$starkLibraryFileName = if ($targetOperatingSystem -ceq "windows") { "VendorSQLite.lib" } else { "libVendorSQLite.a" }
$starkLibraryPath = Join-Path $targetDist $starkLibraryFileName
$packageImagePath = [System.IO.Path]::ChangeExtension($starkLibraryPath, ".starkpkg")

foreach ($ownedPath in @(
    $stagedSqliteRootSource,
    $stagedSqliteModuleDirectory,
    $legacyStagedBindingSource,
    $nativeSqliteRoot,
    $starkLibraryPath,
    $packageImagePath
)) {
    Remove-OwnedPath -Root $outputRoot -Path $ownedPath
}
Copy-RequiredFile `
    -Source (Join-Path $repositoryRoot "vendor/src/Vendor/SQLite.stark") `
    -Destination $stagedSqliteRootSource
Copy-DirectoryContents `
    -Source (Join-Path $repositoryRoot "vendor/src/Vendor/SQLite") `
    -Destination $stagedSqliteModuleDirectory
New-Item -ItemType Directory -Force -Path $nativeSqliteRoot | Out-Null
Copy-RequiredFile -Source (Join-Path $sourceRoot "sqlite3.h") -Destination (Join-Path $nativeSqliteRoot "sqlite3.h")
Copy-RequiredFile -Source (Join-Path $sourceRoot "sqlite3ext.h") -Destination (Join-Path $nativeSqliteRoot "sqlite3ext.h")
$licensePath = Join-Path $nativeSqliteRoot "LICENSE.sqlite3.h"
Copy-RequiredFile -Source (Join-Path $sourceRoot "sqlite3.h") -Destination $licensePath

$clangName = if ($targetOperatingSystem -ceq "windows") { "clang.exe" } else { "clang" }
$archiverName = if ($targetOperatingSystem -ceq "windows") { "llvm-ar.exe" } else { "llvm-ar" }
$clangPath = Join-Path $toolchainPath (Join-Path "bin" $clangName)
$archiverPath = Join-Path $toolchainPath (Join-Path "bin" $archiverName)
foreach ($tool in @($clangPath, $archiverPath)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "Compiler-private SQLite build tool '$tool' does not exist."
    }
}

$sqliteObjectFileName = if ($targetOperatingSystem -ceq "windows") { "sqlite3.obj" } else { "sqlite3.o" }
$adapterObjectFileName = if ($targetOperatingSystem -ceq "windows") { "SQLiteTextBinding.obj" } else { "SQLiteTextBinding.o" }
$sqliteObjectPath = Join-Path $workRoot $sqliteObjectFileName
$adapterObjectPath = Join-Path $workRoot $adapterObjectFileName
$adapterSourcePath = Join-Path $repositoryRoot "vendor/SQLiteTextBinding.c"
$normalizedSourceRoot = "/stark/vendor/sqlite/$sourcePayloadRoot"
$commonClangArguments = @(
    "--target=$TargetTriple",
    "-std=c17",
    "-O3",
    "-DNDEBUG",
    "-ffunction-sections",
    "-fdata-sections",
    "-fno-common",
    "-ffile-prefix-map=$sourceRoot=$normalizedSourceRoot"
)
if ($targetOperatingSystem -cne "windows") {
    $commonClangArguments += "-fPIC"
}
if ($targetOperatingSystem -ceq "linux") {
    $commonClangArguments += "-pthread"
} elseif ($targetOperatingSystem -ceq "windows") {
    $commonClangArguments += "-D_CRT_SECURE_NO_WARNINGS=1"
} elseif ($targetOperatingSystem -ceq "macos") {
    $macSdkPath = (& xcrun --sdk macosx --show-sdk-path 2>&1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($macSdkPath)) {
        throw "xcrun could not resolve the required macOS SDK: $macSdkPath"
    }
    $macSdkPath = ([string]$macSdkPath).Trim()
    $macSdkVersion = ((& xcrun --sdk macosx --show-sdk-version 2>&1) | Select-Object -First 1).Trim()
    $commonClangArguments += @("-isysroot", $macSdkPath)
}
foreach ($definition in $compileDefinitions) {
    if ($definition -cnotmatch '^[A-Z][A-Z0-9_]*(?:=[A-Za-z0-9_.+-]+)?$') {
        throw "Unsafe SQLite compile definition '$definition'."
    }
    $commonClangArguments += "-D$definition"
}
$sqliteClangArguments = @($commonClangArguments) + @(
    "-c", (Join-Path $sourceRoot "sqlite3.c"), "-o", $sqliteObjectPath)
$adapterClangArguments = @($commonClangArguments) + @(
    "-ffile-prefix-map=$repositoryRoot=/stark/repository",
    "-I", $sourceRoot
)
foreach ($definition in $adapterCompileDefinitions) {
    if ($definition -cnotmatch '^[A-Z][A-Z0-9_]*(?:=[A-Za-z0-9_.+-]+)?$') {
        throw "Unsafe SQLite adapter compile definition '$definition'."
    }
    $adapterClangArguments += "-D$definition"
}
$adapterClangArguments += @("-c", $adapterSourcePath, "-o", $adapterObjectPath)

$previousSourceDateEpoch = $env:SOURCE_DATE_EPOCH
try {
    $env:SOURCE_DATE_EPOCH = [string]$sourceDateEpoch
    & $clangPath @sqliteClangArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Compiler-private clang failed to build the SQLite amalgamation for '$TargetTriple'."
    }
    & $clangPath @adapterClangArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Compiler-private clang failed to build the bundled SQLite Stark adapter for '$TargetTriple'."
    }
    Assert-NativeObjectTarget -Path $sqliteObjectPath -TargetId $AssetSuffix
    Assert-NativeObjectTarget -Path $adapterObjectPath -TargetId $AssetSuffix
    & $archiverPath rcsD $nativeLibraryPath $sqliteObjectPath $adapterObjectPath
    if ($LASTEXITCODE -ne 0) {
        throw "Compiler-private llvm-ar failed to build '$nativeLibraryPath'."
    }
} finally {
    $env:SOURCE_DATE_EPOCH = $previousSourceDateEpoch
}
Assert-StaticLibraryArchive `
    -Path $nativeLibraryPath `
    -ExpectedMembers @($sqliteObjectFileName, $adapterObjectFileName) `
    -ArchiverPath $archiverPath

$versionText = @"
# SQLite native release payload

- Version: $([string](Get-RequiredProperty -Object $package -Name "version"))
- Source identity: $([string](Get-RequiredProperty -Object $package -Name "sourceIdentity"))
- Source archive: $archiveName
- Source URL: $sourceUrl
- Source SHA-256: $sourceSha256
- Source bytes: $sourceSize
- Target: $TargetTriple
- Optimization: clang -O3, NDEBUG, function/data sections
- Thread safety: serialized (`SQLITE_THREADSAFE=1`)
- Stark adapter: compiled once into the bundled archive with `STARK_SQLITE_BUNDLED_FEATURES=1`; applications do not rebuild it.
- Native library: $nativeLibraryFileName
- License evidence: `LICENSE.sqlite3.h` is an exact copy of the pinned upstream `sqlite3.h`, whose opening comment contains SQLite's public-domain blessing.
"@
$versionPath = Join-Path $nativeSqliteRoot "VERSION.md"
[System.IO.File]::WriteAllText($versionPath, $versionText.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

$systemLinkFacts = Get-RequiredProperty -Object $package -Name "systemLinkFacts"
$declaredSystemLinks = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $systemLinkFacts -Name $targetOperatingSystem) |
    ForEach-Object { [string]$_ })
$compilerArguments = @(
    "run", "--project", $compilerProjectPath, "--no-restore", "--",
    $stagedSqliteRootSource,
    "--emit-lib",
    "--no-stark-path",
    "-I", $stagedSourceRoot,
    "-I", $stdlibPackageRoot,
    "-o", $starkLibraryPath,
    "--target", $TargetTriple,
    "--package-profile", "release",
    "--toolchain-dir", $toolchainPath,
    "--native-include-dir", $nativeSqliteRoot,
    "--native-library-dir", $nativeSqliteRoot,
    "--native-library", "sqlite3"
)
foreach ($library in $declaredSystemLinks) {
    $compilerArguments += @("--native-library", $library)
}
& dotnet @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Stage0 failed to build release package '$packageId' for '$TargetTriple'."
}
foreach ($expectedOutput in @($starkLibraryPath, $packageImagePath)) {
    if (-not (Test-Path -LiteralPath $expectedOutput -PathType Leaf)) {
        throw "Stage0 did not emit expected SQLite package output '$expectedOutput'."
    }
}

$inspectionPath = Join-Path $workRoot "VendorSQLite.starkpkg.json"
$inspection = Invoke-PackageInspection -PackageImagePath $packageImagePath -OutputPath $inspectionPath
if ([string](Get-RequiredProperty -Object $inspection -Name "RootModule") -cne $packageId) {
    throw "Generated SQLite package has the wrong root module."
}
if ([string](Get-RequiredProperty -Object $inspection -Name "LibraryFileName") -cne $starkLibraryFileName) {
    throw "Generated SQLite package has the wrong Stark archive name."
}
$inspectionTarget = Get-RequiredProperty -Object $inspection -Name "Target"
if ($null -eq $inspectionTarget -or [string](Get-RequiredProperty -Object $inspectionTarget -Name "Triple") -cne $TargetTriple) {
    throw "Generated SQLite package does not preserve exact target '$TargetTriple'."
}
$inspectionProfile = Get-RequiredProperty -Object $inspection -Name "BuildProfile"
if ($null -eq $inspectionProfile -or [string](Get-RequiredProperty -Object $inspectionProfile -Name "Name") -cne "release") {
    throw "Generated SQLite package does not preserve release profile facts."
}

$moduleNames = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $inspection -Name "Modules") |
    ForEach-Object { [string](Get-RequiredProperty -Object $_ -Name "ModuleName") })
$moduleNames = @(Sort-StringsOrdinal -Values $moduleNames)
$expectedModules = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $package -Name "modules") |
    ForEach-Object { [string]$_ })
$expectedModules = @(Sort-StringsOrdinal -Values $expectedModules)
if (($moduleNames -join "`n") -cne ($expectedModules -join "`n")) {
    throw "Generated SQLite package modules '$($moduleNames -join ', ')' do not match catalog '$($expectedModules -join ', ')'."
}

$identity = Get-RequiredProperty -Object $inspection -Name "Identity"
if ($null -eq $identity -or [string](Get-RequiredProperty -Object $identity -Name "PackageId") -cne $packageId) {
    throw "Generated SQLite package is missing its package identity."
}
$dependencies = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $identity -Name "Dependencies"))
$dependencyIds = @(Sort-StringsOrdinal -Values @($dependencies |
    ForEach-Object { [string](Get-RequiredProperty -Object $_ -Name "PackageId") }))
if (($dependencyIds -join "`n") -cne "System") {
    throw "Generated SQLite package must depend only on staged System; got '$($dependencyIds -join ', ')'."
}

$nativeDependencies = Get-RequiredProperty -Object $inspection -Name "NativeDependencies"
$nativeSources = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "Sources")
$includeDirectories = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "IncludeDirectories")
$libraryDirectories = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "LibraryDirectories")
$nativeLibraries = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "Libraries")
$nativeLinkArguments = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "LinkArguments")
$pkgConfigPackages = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "PkgConfigPackages")
if ($nativeSources.Count -ne 0 `
    -or ($includeDirectories -join "`n") -cne "native/sqlite" `
    -or ($libraryDirectories -join "`n") -cne "native/sqlite") {
    throw "Generated SQLite package lost package-relative native source/include/library metadata."
}
foreach ($path in @($nativeSources) + @($includeDirectories) + @($libraryDirectories)) {
    Assert-PortablePackagePath -Path $path -Label "generated SQLite native path"
}
if ($pkgConfigPackages.Count -ne 0) {
    throw "Generated SQLite release package must not depend on pkg-config."
}
if ($nativeLinkArguments.Count -ne 0) {
    throw "Generated SQLite release package unexpectedly contains native link arguments: $($nativeLinkArguments -join ' ')."
}
$requiredLibraries = @("sqlite3") + @($declaredSystemLinks)
foreach ($library in $requiredLibraries) {
    if ($nativeLibraries -cnotcontains $library) {
        throw "Generated SQLite package lost required native library '$library'."
    }
}
$knownCompilerInferredLibraries = @("m", "ws2_32", "synchronization", "ntdll")
foreach ($library in $nativeLibraries) {
    if ($requiredLibraries -cnotcontains $library -and $knownCompilerInferredLibraries -cnotcontains $library) {
        throw "Generated SQLite package unexpectedly advertises native library '$library'."
    }
}

$runtimeSmokeFileName = if ($targetOperatingSystem -ceq "windows") {
    "sqlite-bundled-optional-smoke.exe"
} else {
    "sqlite-bundled-optional-smoke"
}
$runtimeSmokePath = Join-Path $workRoot $runtimeSmokeFileName
$runtimeSmokeArguments = @(
    "run", "--project", $compilerProjectPath, "--no-restore", "--",
    $runtimeSmokeSourcePath,
    "--emit-exe",
    "--no-stark-path",
    "-I", $stdlibPackageRoot,
    "-I", $targetDist,
    "--target", $TargetTriple,
    "--toolchain-dir", $toolchainPath,
    "-o", $runtimeSmokePath
)
& dotnet @runtimeSmokeArguments
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $runtimeSmokePath -PathType Leaf)) {
    throw "Stage0 failed to build the bundled SQLite optional-feature runtime smoke for '$TargetTriple'."
}
& $runtimeSmokePath
$runtimeSmokeExitCode = $LASTEXITCODE
if ($runtimeSmokeExitCode -ne 0) {
    throw "Bundled SQLite optional-feature runtime smoke failed for '$TargetTriple' with exit code $runtimeSmokeExitCode."
}

$systemImages = @(Get-ChildItem -LiteralPath $stdlibPackageRoot -File -Recurse -Filter "*.starkpkg" | Sort-Object FullName)
$systemPackages = @()
foreach ($systemImage in $systemImages) {
    $systemInspectionPath = Join-Path $workRoot ("system-" + $systemImage.Name + ".json")
    $systemInspection = Invoke-PackageInspection -PackageImagePath $systemImage.FullName -OutputPath $systemInspectionPath
    if ([string](Get-RequiredProperty -Object $systemInspection -Name "RootModule") -ceq "System") {
        $systemPackages += [pscustomobject]@{ Image = $systemImage; Inspection = $systemInspection }
    }
}
if ($systemPackages.Count -ne 1) {
    throw "Staged standard-library directory must contain exactly one System package; found $($systemPackages.Count)."
}
$systemTarget = Get-RequiredProperty -Object $systemPackages[0].Inspection -Name "Target"
$systemProfile = Get-RequiredProperty -Object $systemPackages[0].Inspection -Name "BuildProfile"
$systemIdentity = Get-RequiredProperty -Object $systemPackages[0].Inspection -Name "Identity"
if ($null -eq $systemTarget -or [string](Get-RequiredProperty -Object $systemTarget -Name "Triple") -cne $TargetTriple `
    -or $null -eq $systemProfile -or [string](Get-RequiredProperty -Object $systemProfile -Name "Name") -cne "release") {
    throw "Staged System package must preserve exact '$TargetTriple' release facts."
}
$identityDependencies = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $identity -Name "Dependencies"))
if ($identityDependencies.Count -ne 1 `
    -or [string](Get-RequiredProperty -Object $identityDependencies[0] -Name "ApiHash") -cne [string](Get-RequiredProperty -Object $systemIdentity -Name "ApiHash") `
    -or [string](Get-RequiredProperty -Object $identityDependencies[0] -Name "ContentHash") -cne [string](Get-RequiredProperty -Object $systemIdentity -Name "ContentHash")) {
    throw "Generated SQLite package does not preserve the staged System API/content identity."
}

$clangVersion = @(& $clangPath --version 2>&1 | Select-Object -First 1)
$provenanceValue = [ordered]@{
    schemaVersion = 1
    packageId = $packageId
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
    sourceArchive = [ordered]@{
        name = $archiveName
        url = $sourceUrl
        bytes = $sourceSize
        sha256 = $sourceSha256
        payloadRoot = $sourcePayloadRoot
        files = [object[]]$verifiedSourceFiles
    }
    nativeBuild = [ordered]@{
        compiler = "toolchain/bin/$clangName"
        compilerVersion = [string]($clangVersion | Select-Object -First 1)
        archiver = "toolchain/bin/$archiverName"
        optimization = "O3"
        ndebug = $true
        functionSections = $true
        dataSections = $true
        lto = $false
        ltoReason = "The SQLite amalgamation is already one translation unit; a machine-code archive avoids coupling installed host linkers to LLVM 22 bitcode while retaining whole-amalgamation O3 optimization."
        deterministicArchive = $true
        sourceDateEpoch = $sourceDateEpoch
        compileDefinitions = [object[]]$compileDefinitions
        adapterCompileDefinitions = [object[]]$adapterCompileDefinitions
        archiveMembers = [object[]]@($sqliteObjectFileName, $adapterObjectFileName)
        adapterCompiledIntoNativeArchive = $true
        perApplicationNativeSourceCompilation = $false
        adapterSourceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $adapterSourcePath).Hash.ToLowerInvariant()
        macosSdkVersion = if ($targetOperatingSystem -ceq "macos") { $macSdkVersion } else { $null }
        library = Get-PortableRelativePath -Root $outputRoot -Path $nativeLibraryPath -Label "SQLite native library"
        librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $nativeLibraryPath).Hash.ToLowerInvariant()
    }
    declaredSystemLinkFacts = [object[]]$declaredSystemLinks
    runtimeSmoke = [ordered]@{
        fixture = "tests/fixtures/release/SQLiteBundledOptionalSmoke.stark"
        exitCode = $runtimeSmokeExitCode
        carray = "available-and-query-verified"
        normalizedSql = "available-and-result-verified"
        statementScanStatus = "available-and-invoked"
        snapshot = "available-and-invoked-with-non-wal-database"
    }
    emittedNativeFacts = [ordered]@{
        sources = [object[]]$nativeSources
        includeDirectories = [object[]]$includeDirectories
        libraryDirectories = [object[]]$libraryDirectories
        libraries = [object[]]$nativeLibraries
        linkArguments = [object[]]$nativeLinkArguments
        pkgConfigPackages = [object[]]$pkgConfigPackages
    }
    stagedSystemIdentity = [ordered]@{
        packageId = [string](Get-RequiredProperty -Object $systemIdentity -Name "PackageId")
        apiHash = [string](Get-RequiredProperty -Object $systemIdentity -Name "ApiHash")
        contentHash = [string](Get-RequiredProperty -Object $systemIdentity -Name "ContentHash")
        imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $systemPackages[0].Image.FullName).Hash.ToLowerInvariant()
    }
    packageIdentity = [ordered]@{
        packageId = [string](Get-RequiredProperty -Object $identity -Name "PackageId")
        apiHash = [string](Get-RequiredProperty -Object $identity -Name "ApiHash")
        contentHash = [string](Get-RequiredProperty -Object $identity -Name "ContentHash")
        modules = [object[]]$moduleNames
    }
}
$provenancePath = Join-Path $nativeSqliteRoot "PROVENANCE.json"
Write-DeterministicJson -Value $provenanceValue -Path $provenancePath

$artifactInputs = @(
    [pscustomobject]@{ Path = (Join-Path $nativeSqliteRoot "sqlite3.h"); Kind = "header" },
    [pscustomobject]@{ Path = (Join-Path $nativeSqliteRoot "sqlite3ext.h"); Kind = "header" },
    [pscustomobject]@{ Path = $licensePath; Kind = "license" },
    [pscustomobject]@{ Path = $nativeLibraryPath; Kind = "static-library" },
    [pscustomobject]@{ Path = $versionPath; Kind = "documentation" },
    [pscustomobject]@{ Path = $provenancePath; Kind = "provenance" }
)
$artifactDescriptors = @($artifactInputs | ForEach-Object {
    New-FileDescriptor -Root $outputRoot -Path $_.Path -Kind $_.Kind
})
$artifactDescriptors = @(Sort-ObjectsOrdinalByProperty -Values $artifactDescriptors -PropertyName "path")
$licenseDescriptor = New-PlainFileDescriptor -Root $outputRoot -Path $licensePath
$provenanceDescriptor = New-PlainFileDescriptor -Root $outputRoot -Path $provenancePath

$packageEntry = [ordered]@{
    id = $packageId
    version = [string](Get-RequiredProperty -Object $package -Name "version")
    sourceIdentity = [string](Get-RequiredProperty -Object $package -Name "sourceIdentity")
    target = [ordered]@{
        id = $AssetSuffix
        targetTriple = $TargetTriple
    }
    package = [ordered]@{
        rootModule = $packageId
        image = Get-PortableRelativePath -Root $outputRoot -Path $packageImagePath -Label "SQLite package image"
        imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $packageImagePath).Hash.ToLowerInvariant()
        library = Get-PortableRelativePath -Root $outputRoot -Path $starkLibraryPath -Label "SQLite Stark library"
        librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $starkLibraryPath).Hash.ToLowerInvariant()
        modules = [object[]]$moduleNames
    }
    nativePayload = [ordered]@{
        artifacts = [object[]]$artifactDescriptors
        licenseFiles = [object[]]@($licenseDescriptor)
    }
    provenance = $provenanceDescriptor
}
$contribution = [ordered]@{
    schemaVersion = 1
    targetId = $AssetSuffix
    targetTriple = $TargetTriple
    packages = [object[]]@($packageEntry)
}
Write-DeterministicJson -Value $contribution -Path $contributionPath

Write-Host "Prepared pinned SQLite $($packageEntry.version) release package contribution for '$AssetSuffix'."
Write-Host "Contribution manifest: $contributionPath"
} finally {
    if (Test-Path -LiteralPath $workRoot) {
        $workParent = Join-Path $repositoryRoot (Join-Path "artifacts/sqlite-work" $AssetSuffix)
        if (-not (Test-IsSameOrDescendantPath -Path $workRoot -Root $workParent)) {
            throw "Refusing to clean unexpected SQLite work root '$workRoot'."
        }
        Assert-NoReparsePointPath -Path $workRoot
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
