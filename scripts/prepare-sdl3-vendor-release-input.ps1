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
    [string] $CMakePath,

    [Parameter(Mandatory = $true)]
    [string] $NinjaPath,

    [Parameter(Mandatory = $true)]
    [string] $ContributionManifestPath,

    [string] $CompilerProject = "src/compiler.csproj",

    [string] $VendorCatalogPath = "eng/release/vendor-packages.json",

    [string] $TargetManifestPath = "eng/release/targets.json",

    [string] $CacheDir = "artifacts/vendor-cache/sdl3",

    [ValidateRange(1, 3600)]
    [int] $WorkLockTimeoutSeconds = 900,

    [switch] $Force,

    [switch] $AllowPlannedTarget
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$recipePath = "scripts/prepare-sdl3-vendor-release-input.ps1"
$packageId = "Vendor.SDL3"
. (Join-Path $PSScriptRoot "sdl3-work-root-lock.ps1")

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
        [object] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }
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

function Sort-StringsOrdinal {
    param([AllowEmptyCollection()][string[]] $Values = @())

    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($value in $Values) {
        $items.Add($value)
    }
    $items.Sort([StringComparer]::Ordinal)
    return [string[]]$items.ToArray()
}

function Sort-ObjectsOrdinalByProperty {
    param(
        [AllowEmptyCollection()][object[]] $Values = @(),
        [Parameter(Mandatory = $true)][string] $PropertyName
    )

    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($value in $Values) {
        $items.Add($value)
    }
    $items.Sort([System.Comparison[object]] {
        param($left, $right)
        $leftValue = if ($left -is [System.Collections.IDictionary]) {
            $left[$PropertyName]
        } else {
            $left.PSObject.Properties[$PropertyName].Value
        }
        $rightValue = if ($right -is [System.Collections.IDictionary]) {
            $right[$PropertyName]
        } else {
            $right.PSObject.Properties[$PropertyName].Value
        }
        return [StringComparer]::Ordinal.Compare([string]$leftValue, [string]$rightValue)
    })
    return [object[]]$items.ToArray()
}

function Write-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)][object] $Value,
        [Parameter(Mandatory = $true)][string] $Path
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $json = ($Value | ConvertTo-Json -Depth 60).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Test-IsSameOrDescendantPath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Root
    )

    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    $rootPath = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Root))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    return [string]::Equals($candidate, $rootPath, $comparison) -or
        $candidate.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar, $comparison)
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
        throw "SDL3 output Vendor root '$candidate' cannot be a filesystem root."
    }
    $artifactsRoot = Join-Path $repositoryRoot "artifacts"
    if (Test-IsSameOrDescendantPath -Path $candidate -Root $repositoryRoot) {
        if (-not (Test-IsSameOrDescendantPath -Path $candidate -Root $artifactsRoot) -or
            [string]::Equals($candidate, [System.IO.Path]::TrimEndingDirectorySeparator($artifactsRoot), $comparison)) {
            throw "SDL3 output Vendor root '$candidate' must be a child of '$artifactsRoot'."
        }
    }
    foreach ($protectedPath in @(
        $repositoryRoot,
        (Join-Path $repositoryRoot "vendor"),
        [Environment]::CurrentDirectory,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (Test-IsSameOrDescendantPath -Path $protectedPath -Root $candidate) {
            throw "SDL3 output Vendor root '$candidate' is or contains protected path '$protectedPath'."
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
    if ([System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath -eq ".." -or
        $relativePath.StartsWith("../", [StringComparison]::Ordinal) -or
        $relativePath.Split('/') -contains "..") {
        throw "$Label '$pathFullPath' escapes '$rootFullPath'."
    }
    return $relativePath
}

function Assert-PortableRelativePath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $segments = @($Path.Split('/'))
    if ([string]::IsNullOrWhiteSpace($Path) -or
        [System.IO.Path]::IsPathRooted($Path) -or
        $Path.Contains('\', [StringComparison]::Ordinal) -or
        $Path.Contains(':', [StringComparison]::Ordinal) -or
        $Path.StartsWith("../", [StringComparison]::Ordinal) -or
        @($segments | Where-Object { $_ -in @("", ".", "..") }).Count -ne 0) {
        throw "$Label '$Path' is not a portable relative path."
    }
}

function Remove-OwnedPath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $relativePath = Get-PortableRelativePath -Root $Root -Path $Path -Label "SDL3-owned path"
    if ($relativePath -eq ".") {
        throw "SDL3 contributor must never replace shared output root '$Root'."
    }
    if (Test-Path -LiteralPath $Path) {
        Assert-NoReparsePointPath -Path $Path
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination
    )

    Assert-NoReparsePointPath -Path $Source
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required SDL3 input '$Source' does not exist."
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

    Assert-NoReparsePointPath -Path $Source
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Required SDL3 source directory '$Source' does not exist."
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in (Get-ChildItem -LiteralPath $Source -Force | Sort-Object Name)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "SDL3 source directory contains symbolic link or reparse point '$($item.FullName)'."
        }
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
        [Parameter(Mandatory = $true)][string] $ExpectedSha256,
        [Nullable[int64]] $ExpectedBytes = $null
    )

    if ($ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Expected SDL3 SHA-256 '$ExpectedSha256' is invalid."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Pinned SDL3 input '$Path' does not exist."
    }
    $file = Get-Item -LiteralPath $Path -Force
    if ($null -ne $ExpectedBytes -and $file.Length -ne [int64]$ExpectedBytes) {
        throw "Pinned SDL3 input '$Path' has $($file.Length) bytes; expected $ExpectedBytes."
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    if ($actual -cne $ExpectedSha256) {
        throw "Pinned SDL3 input '$Path' failed SHA-256 validation; expected '$ExpectedSha256', got '$actual'."
    }
}

function Expand-CheckedTarGzip {
    param(
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][string] $DestinationRoot
    )

    Add-Type -AssemblyName System.Formats.Tar
    New-Item -ItemType Directory -Force -Path $DestinationRoot | Out-Null
    $destinationFullPath = [System.IO.Path]::GetFullPath($DestinationRoot)
    $paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $archiveStream = [System.IO.File]::OpenRead($ArchivePath)
    try {
        $gzipStream = [System.IO.Compression.GZipStream]::new(
            $archiveStream,
            [System.IO.Compression.CompressionMode]::Decompress,
            $false)
        try {
            $reader = [System.Formats.Tar.TarReader]::new($gzipStream, $false)
            try {
                while ($null -ne ($entry = $reader.GetNextEntry())) {
                    $entryPath = ([string]$entry.Name).Replace('\', '/')
                    Assert-PortableRelativePath -Path $entryPath -Label "SDL3 archive entry"
                    if (-not $paths.Add($entryPath)) {
                        throw "Pinned SDL3 archive contains duplicate or case-colliding entry '$entryPath'."
                    }
                    if ($entry.EntryType -notin @(
                        [System.Formats.Tar.TarEntryType]::RegularFile,
                        [System.Formats.Tar.TarEntryType]::V7RegularFile)) {
                        throw "Pinned SDL3 archive entry '$entryPath' has forbidden type '$($entry.EntryType)'."
                    }
                    $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $destinationFullPath $entryPath))
                    if (-not (Test-IsSameOrDescendantPath -Path $destinationPath -Root $destinationFullPath)) {
                        throw "Pinned SDL3 archive entry '$entryPath' escapes extraction root."
                    }
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPath) | Out-Null
                    $destinationStream = [System.IO.File]::Create($destinationPath)
                    try {
                        $entry.DataStream.CopyTo($destinationStream)
                    } finally {
                        $destinationStream.Dispose()
                    }
                }
            } finally {
                $reader.Dispose()
            }
        } finally {
            $gzipStream.Dispose()
        }
    } finally {
        $archiveStream.Dispose()
    }
    if ($paths.Count -lt 2000) {
        throw "Pinned SDL3 source archive is unexpectedly incomplete; extracted only $($paths.Count) files."
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
        throw "SDL3 release input '$AssetSuffix' must be built on $OperatingSystem-$Architecture; this host is $hostOperatingSystem-$hostArchitecture."
    }
}

function Get-PrivateToolPath {
    param(
        [Parameter(Mandatory = $true)][string] $ToolchainRoot,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $fileName = if ($IsWindows) { "$Name.exe" } else { $Name }
    $path = Join-Path $ToolchainRoot (Join-Path "bin" $fileName)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Compiler-private SDL3 build tool '$path' does not exist."
    }
    Assert-NoReparsePointPath -Path $path
    return $path
}

function Assert-ExternalBuildToolVersion {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][ValidateSet("cmake", "ninja")][string] $Kind,
        [Parameter(Mandatory = $true)][string] $ExpectedVersion
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Explicit SDL3 $Kind executable '$Path' does not exist."
    }
    Assert-NoReparsePointPath -Path $Path
    $output = @(& $Path --version 2>&1)
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) {
        throw "Explicit SDL3 $Kind executable '$Path' could not report its version."
    }
    $actualVersion = if ($Kind -ceq "cmake") {
        $firstLine = ([string]$output[0]).Trim()
        if ($firstLine -cnotmatch '^cmake version ([0-9]+\.[0-9]+\.[0-9]+)$') {
            throw "Unexpected CMake version output '$firstLine'."
        }
        $Matches[1]
    } else {
        ([string]$output[0]).Trim()
    }
    if ($actualVersion -cne $ExpectedVersion) {
        throw "SDL3 build requires reviewed $Kind $ExpectedVersion exactly; '$Path' reports '$actualVersion'."
    }
    return [ordered]@{
        path = $Path
        version = $actualVersion
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    }
}

function Assert-StaticArchive {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ArchiverPath
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "SDL3 static library '$Path' does not exist."
    }
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $header = [byte[]]::new(8)
        if ($stream.Read($header, 0, $header.Length) -ne $header.Length) {
            throw "SDL3 static library '$Path' is truncated."
        }
        $magic = [System.Text.Encoding]::ASCII.GetString($header)
        if ($magic -ceq "!<thin>`n") {
            throw "SDL3 static library '$Path' is a thin archive and is not self-contained."
        }
        if ($magic -cne "!<arch>`n") {
            throw "SDL3 static library '$Path' is not an ar archive."
        }
    } finally {
        $stream.Dispose()
    }

    $memberOutput = @(& $ArchiverPath t $Path 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Compiler-private llvm-ar could not inspect SDL3 archive '$Path': $($memberOutput -join [Environment]::NewLine)"
    }
    $members = @($memberOutput | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    if ($members.Count -lt 100) {
        throw "SDL3 archive '$Path' is unexpectedly incomplete; found only $($members.Count) members."
    }
    $adapterMembers = @($members | Where-Object { $_ -match '^Sdl3Binding\.c\.(?:o|obj)$' })
    if ($adapterMembers.Count -ne 1) {
        throw "SDL3 archive must contain exactly one precompiled Sdl3Binding.c object; found '$($adapterMembers -join ', ')'."
    }
    return [ordered]@{
        memberCount = $members.Count
        adapterMember = $adapterMembers[0]
        membersSha256 = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData(
                [System.Text.Encoding]::UTF8.GetBytes(($members -join "`n") + "`n"))).ToLowerInvariant()
    }
}

function Invoke-PackageInspection {
    param(
        [Parameter(Mandatory = $true)][string] $PackageImagePath,
        [Parameter(Mandatory = $true)][string] $OutputPath,
        [Parameter(Mandatory = $true)][string] $CompilerProjectPath
    )

    & dotnet run --project $CompilerProjectPath --no-restore -- inspect-pkg $PackageImagePath --format json -o $OutputPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw "Stage0 failed to inspect SDL3 package image '$PackageImagePath'."
    }
    return (Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json)
}

function Get-NativeMetadataValues {
    param(
        [object] $NativeDependencies,
        [Parameter(Mandatory = $true)][string] $Name
    )

    return @(Get-ArrayValues -Value (Get-OptionalProperty -Object $NativeDependencies -Name $Name) |
        ForEach-Object { [string]$_ })
}

function New-FileDescriptor {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)]
        [ValidateSet("documentation", "header", "license", "provenance", "static-library")]
        [string] $Kind
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "SDL3 contribution file '$Path' does not exist."
    }
    $file = Get-Item -LiteralPath $Path -Force
    return [ordered]@{
        kind = $Kind
        path = Get-PortableRelativePath -Root $Root -Path $file.FullName -Label "SDL3 contribution file"
        bytes = [int64]$file.Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }
}

function New-PlainFileDescriptor {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $file = Get-Item -LiteralPath $Path -Force
    return [ordered]@{
        path = Get-PortableRelativePath -Root $Root -Path $file.FullName -Label "SDL3 contribution file"
        bytes = [int64]$file.Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }
}

function Assert-ExactStringSequence {
    param(
        [AllowEmptyCollection()][string[]] $Actual = @(),
        [AllowEmptyCollection()][string[]] $Expected = @(),
        [Parameter(Mandatory = $true)][string] $Label
    )

    if (($Actual -join "`n") -cne ($Expected -join "`n")) {
        throw "$Label mismatch. Expected '$($Expected -join ', ')'; got '$($Actual -join ', ')'."
    }
}

function Convert-CMakeInterfaceLibraryToFact {
    param([Parameter(Mandatory = $true)][string] $Value)

    if ($Value -cmatch '^\$<LINK_ONLY:\$<LINK_LIBRARY:FRAMEWORK,([A-Za-z0-9_.+-]+)>>$') {
        return "framework:$($Matches[1])"
    }
    if ($Value -cmatch '^\$<LINK_ONLY:\$<LINK_LIBRARY:WEAK_FRAMEWORK,([A-Za-z0-9_.+-]+)>>$') {
        return "weak-framework:$($Matches[1])"
    }
    if ($Value -cmatch '^\$<LINK_ONLY:([A-Za-z0-9_.+-]+)>$') {
        return "library:$($Matches[1])"
    }
    if ($Value -ceq '$<TARGET_NAME:SDL3::Headers>') {
        return "target:SDL3::Headers"
    }
    throw "SDL3 CMake static interface contains unreviewed library expression '$Value'."
}

$compilerProjectPath = Resolve-RepositoryPath -Path $CompilerProject
$vendorCatalogFullPath = Resolve-RepositoryPath -Path $VendorCatalogPath
$targetManifestFullPath = Resolve-RepositoryPath -Path $TargetManifestPath
$outputRoot = Resolve-RepositoryPath -Path $OutputVendorRoot
$stdlibPackageRoot = Resolve-RepositoryPath -Path $StdlibPackageDir
$toolchainRoot = Resolve-RepositoryPath -Path $ToolchainDir
$cmakeExecutable = Resolve-RepositoryPath -Path $CMakePath
$ninjaExecutable = Resolve-RepositoryPath -Path $NinjaPath
$contributionPath = Resolve-RepositoryPath -Path $ContributionManifestPath
$cacheRoot = Resolve-RepositoryPath -Path $CacheDir
$smokeSourcePath = Join-Path $repositoryRoot "tests/fixtures/release/SDL3BundledSmoke.stark"
$adapterSourcePath = Join-Path $repositoryRoot "vendor/Sdl3Binding.c"
$starkSourcePath = Join-Path $repositoryRoot "vendor/src/Vendor/SDL3.stark"

foreach ($requiredFile in @(
    $compilerProjectPath,
    $vendorCatalogFullPath,
    $targetManifestFullPath,
    $smokeSourcePath,
    $adapterSourcePath,
    $starkSourcePath
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required SDL3 release input '$requiredFile' does not exist."
    }
}
foreach ($requiredDirectory in @($stdlibPackageRoot, $toolchainRoot)) {
    if (-not (Test-Path -LiteralPath $requiredDirectory -PathType Container)) {
        throw "Required SDL3 release input directory '$requiredDirectory' does not exist."
    }
}
Assert-SafeOutputRoot -Path $outputRoot
Assert-NoReparsePointPath -Path $contributionPath
if (Test-IsSameOrDescendantPath -Path $contributionPath -Root $outputRoot) {
    throw "SDL3 contribution manifest '$contributionPath' must be outside shared OutputVendorRoot '$outputRoot'."
}
if ((Test-IsSameOrDescendantPath -Path $cacheRoot -Root $outputRoot) -or
    (Test-IsSameOrDescendantPath -Path $outputRoot -Root $cacheRoot)) {
    throw "SDL3 source cache '$cacheRoot' and shared output '$outputRoot' must not overlap."
}

$targetManifest = Get-Content -LiteralPath $targetManifestFullPath -Raw | ConvertFrom-Json
$targetMatches = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $targetManifest -Name "targets") |
    Where-Object { [string](Get-RequiredProperty -Object $_ -Name "id") -ceq $AssetSuffix })
if ($targetMatches.Count -ne 1) {
    throw "Release target manifest must contain exactly one '$AssetSuffix' entry."
}
$targetEntry = $targetMatches[0]
$releaseEnabled = [bool](Get-RequiredProperty -Object $targetEntry -Name "releaseEnabled")
$supportTier = [string](Get-RequiredProperty -Object $targetEntry -Name "supportTier")
if ($releaseEnabled -and $AllowPlannedTarget) {
    throw "SDL3 -AllowPlannedTarget is valid only for a release-disabled planned target."
}
if (-not $releaseEnabled -and (-not $AllowPlannedTarget -or $supportTier -cne "planned")) {
    throw "SDL3 release target '$AssetSuffix' is disabled; planned targets require -AllowPlannedTarget."
}
$manifestTargetTriple = [string](Get-RequiredProperty -Object $targetEntry -Name "targetTriple")
if ($TargetTriple.Trim() -cne $manifestTargetTriple) {
    throw "SDL3 target triple '$TargetTriple' does not match manifest triple '$manifestTargetTriple'."
}
$targetOperatingSystem = [string](Get-RequiredProperty -Object $targetEntry -Name "operatingSystem")
$targetArchitecture = [string](Get-RequiredProperty -Object $targetEntry -Name "architecture")
Assert-MatchingHost -OperatingSystem $targetOperatingSystem -Architecture $targetArchitecture

$toolchainManifestPath = Join-Path $toolchainRoot "manifest.json"
if (-not (Test-Path -LiteralPath $toolchainManifestPath -PathType Leaf)) {
    throw "SDL3 compiler-private backend '$toolchainRoot' has no manifest.json."
}
$toolchainManifest = Get-Content -LiteralPath $toolchainManifestPath -Raw | ConvertFrom-Json
if ([int](Get-RequiredProperty -Object $toolchainManifest -Name "schemaVersion") -ne 2 -or
    [string](Get-RequiredProperty -Object $toolchainManifest -Name "payloadKind") -cne "stark-compiler-private-backend" -or
    [string](Get-RequiredProperty -Object $toolchainManifest -Name "assetSuffix") -cne $AssetSuffix) {
    throw "SDL3 compiler-private backend must be the matching schema-2 Stark backend closure."
}
$requiredToolEntries = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $toolchainManifest -Name "requiredTools") |
    ForEach-Object { [string]$_ })
$privateToolNames = if ($targetOperatingSystem -ceq "windows") {
    @("clang", "clang++", "llvm-ar", "llvm-ranlib", "lld-link")
} elseif ($targetOperatingSystem -ceq "macos") {
    @("clang", "clang++", "llvm-ar", "llvm-ranlib", "ld64.lld")
} else {
    @("clang", "clang++", "llvm-ar", "llvm-ranlib", "ld.lld")
}
$privateToolPaths = [ordered]@{}
foreach ($toolName in $privateToolNames) {
    $relativeToolPath = "bin/" + $(if ($targetOperatingSystem -ceq "windows") { "$toolName.exe" } else { $toolName })
    if ($requiredToolEntries -cnotcontains $relativeToolPath) {
        throw "SDL3 compiler-private backend manifest does not declare '$relativeToolPath'."
    }
    $privateToolPaths[$toolName] = Get-PrivateToolPath -ToolchainRoot $toolchainRoot -Name $toolName
}

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
$targetSupport = Get-RequiredProperty -Object $package -Name "targetSupport"
if ([string](Get-RequiredProperty -Object $targetSupport -Name $AssetSuffix) -cne "required-source-build") {
    throw "Vendor package '$packageId' target '$AssetSuffix' must be a required-source-build."
}

$sourceUrl = [string](Get-RequiredProperty -Object $package -Name "sourceUrl")
$sourceSha256 = ([string](Get-RequiredProperty -Object $package -Name "sourceSha256")).ToLowerInvariant()
$sourceSize = [int64](Get-RequiredProperty -Object $package -Name "sourceSize")
$sourcePayloadRoot = [string](Get-RequiredProperty -Object $package -Name "sourcePayloadRoot")
$sourceDateEpoch = [int64](Get-RequiredProperty -Object $package -Name "sourceDateEpoch")
$sourceArchiveFiles = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $package -Name "sourceArchiveFiles"))
$sourceLicensePath = [string](Get-RequiredProperty -Object $package -Name "sourceLicensePath")
if ($sourceUrl -cne "https://github.com/libsdl-org/SDL/releases/download/release-3.4.10/SDL3-3.4.10.tar.gz" -or
    $sourcePayloadRoot -cne "SDL3-3.4.10" -or
    $sourceSize -le 0 -or
    $sourceDateEpoch -le 0 -or
    $sourceArchiveFiles.Count -lt 3 -or
    $sourceLicensePath -cne "LICENSE.txt") {
    throw "Vendor package '$packageId' has an incomplete or unexpected pinned-source policy."
}

$buildTools = Get-RequiredProperty -Object $package -Name "buildTools"
$cmakeToolPolicy = Get-RequiredProperty -Object $buildTools -Name "cmake"
$ninjaToolPolicy = Get-RequiredProperty -Object $buildTools -Name "ninja"
$cmakeTool = Assert-ExternalBuildToolVersion `
    -Path $cmakeExecutable `
    -Kind "cmake" `
    -ExpectedVersion ([string](Get-RequiredProperty -Object $cmakeToolPolicy -Name "version"))
$ninjaTool = Assert-ExternalBuildToolVersion `
    -Path $ninjaExecutable `
    -Kind "ninja" `
    -ExpectedVersion ([string](Get-RequiredProperty -Object $ninjaToolPolicy -Name "version"))
foreach ($toolPolicy in @($cmakeToolPolicy, $ninjaToolPolicy)) {
    if ([string](Get-RequiredProperty -Object $toolPolicy -Name "acquisition") -cne "workflow-must-provide-reviewed-pinned-binary") {
        throw "SDL3 CMake/Ninja acquisition remains build-driver-owned and must not fall back to ambient PATH discovery."
    }
}

$sourceBuildOptions = Get-RequiredProperty -Object $package -Name "sourceBuildOptions"
if ([string](Get-RequiredProperty -Object $sourceBuildOptions -Name "configuration") -cne "Release" -or
    [string](Get-RequiredProperty -Object $sourceBuildOptions -Name "optimization") -cne "O3" -or
    [string](Get-RequiredProperty -Object $sourceBuildOptions -Name "lto") -cne "thin" -or
    -not [bool](Get-RequiredProperty -Object $sourceBuildOptions -Name "deterministicArchive") -or
    [bool](Get-RequiredProperty -Object $sourceBuildOptions -Name "pkgConfigDiscovery") -or
    -not [bool](Get-RequiredProperty -Object $sourceBuildOptions -Name "adapterCompiledIntoNativeArchive")) {
    throw "SDL3 source-build policy must preserve reviewed Release/O3/ThinLTO/deterministic/no-pkg-config facts."
}
$commonCmakeOptions = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $sourceBuildOptions -Name "commonCmakeOptions") |
    ForEach-Object { [string]$_ })
$targetCmakeOptionsObject = Get-RequiredProperty -Object $sourceBuildOptions -Name "targetCmakeOptions"
$targetCmakeOptions = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $targetCmakeOptionsObject -Name $AssetSuffix) |
    ForEach-Object { [string]$_ })
$cmakeOptions = @($commonCmakeOptions + $targetCmakeOptions)
$optionNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($option in $cmakeOptions) {
    if ($option -cnotmatch '^([A-Z][A-Z0-9_]*)=([A-Za-z0-9_.+-]+)$') {
        throw "Unsafe SDL3 CMake option '$option'."
    }
    if (-not $optionNames.Add($Matches[1])) {
        throw "SDL3 CMake option '$($Matches[1])' is declared more than once."
    }
}

$requiredBuildDefinesObject = Get-RequiredProperty -Object $package -Name "requiredBuildDefines"
$requiredBuildDefines = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $requiredBuildDefinesObject -Name $AssetSuffix) |
    ForEach-Object { [string]$_ })
foreach ($define in $requiredBuildDefines) {
    if ($define -cnotmatch '^(?:SDL|HAVE)_[A-Z0-9_]+$') {
        throw "Unsafe SDL3 required build define '$define'."
    }
}
$nativeLinkFactsObject = Get-RequiredProperty -Object $package -Name "nativeLinkFacts"
$nativeLinkFacts = Get-RequiredProperty -Object $nativeLinkFactsObject -Name $AssetSuffix
$declaredNativeLibraries = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $nativeLinkFacts -Name "libraries") |
    ForEach-Object { [string]$_ })
$declaredNativeLinkArguments = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $nativeLinkFacts -Name "linkArguments") |
    ForEach-Object { [string]$_ })
if ($declaredNativeLibraries.Count -eq 0 -or $declaredNativeLibraries[0] -cne "SDL3") {
    throw "SDL3 native link facts must start with the package-owned SDL3 archive."
}
$cmakeStaticInterfaceObject = Get-RequiredProperty -Object $package -Name "cmakeStaticInterface"
$expectedCmakeStaticInterface = Get-RequiredProperty -Object $cmakeStaticInterfaceObject -Name $AssetSuffix
$expectedCmakeInterfaceLibraries = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $expectedCmakeStaticInterface -Name "libraries") |
    ForEach-Object { [string]$_ })
$expectedCmakeInterfaceOptions = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $expectedCmakeStaticInterface -Name "linkOptions") |
    ForEach-Object { [string]$_ })

$archiveName = [System.IO.Path]::GetFileName(([Uri]$sourceUrl).AbsolutePath)
if ($archiveName -cne "SDL3-3.4.10.tar.gz") {
    throw "Unexpected SDL3 source archive name '$archiveName'."
}
$archiveCacheRoot = Join-Path $cacheRoot $sourceSha256
New-Item -ItemType Directory -Force -Path $archiveCacheRoot | Out-Null
$archivePath = Join-Path $archiveCacheRoot $archiveName
if ($Force -or -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    $downloadPath = "$archivePath.download-$([Guid]::NewGuid().ToString('N'))"
    try {
        Invoke-WebRequest -Uri $sourceUrl -OutFile $downloadPath
        Assert-Sha256 -Path $downloadPath -ExpectedSha256 $sourceSha256 -ExpectedBytes $sourceSize
        Move-Item -LiteralPath $downloadPath -Destination $archivePath -Force
    } finally {
        if (Test-Path -LiteralPath $downloadPath -PathType Leaf) {
            Remove-Item -LiteralPath $downloadPath -Force
        }
    }
}
Assert-Sha256 -Path $archivePath -ExpectedSha256 $sourceSha256 -ExpectedBytes $sourceSize

$workParent = Join-Path $repositoryRoot (Join-Path "artifacts/sdl3-work" $AssetSuffix)
$workRoot = Join-Path $workParent "build"
$workLockPath = Join-Path $workParent ".build.lock"
$workLock = Enter-Sdl3WorkRootLock `
    -LockPath $workLockPath `
    -OwnerLabel "prepare-sdl3-vendor-release-input" `
    -TargetId $AssetSuffix `
    -TargetTriple $TargetTriple `
    -OutputRoot $outputRoot `
    -TimeoutSeconds $WorkLockTimeoutSeconds
try {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-OwnedPath -Root $workParent -Path $workRoot
    }
    $extractRoot = Join-Path $workRoot "extract"
    New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
    Expand-CheckedTarGzip -ArchivePath $archivePath -DestinationRoot $extractRoot
    $sourceRoot = Join-Path $extractRoot $sourcePayloadRoot
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Pinned SDL3 payload root '$sourcePayloadRoot' was not found."
    }
    $verifiedSourceFiles = @()
    foreach ($archiveFile in $sourceArchiveFiles) {
        $relativePath = [string](Get-RequiredProperty -Object $archiveFile -Name "path")
        Assert-PortableRelativePath -Path $relativePath -Label "SDL3 pinned source file"
        $sourceFile = Join-Path $sourceRoot $relativePath
        $expectedBytes = [int64](Get-RequiredProperty -Object $archiveFile -Name "bytes")
        $expectedSha256 = ([string](Get-RequiredProperty -Object $archiveFile -Name "sha256")).ToLowerInvariant()
        Assert-Sha256 -Path $sourceFile -ExpectedSha256 $expectedSha256 -ExpectedBytes $expectedBytes
        $verifiedSourceFiles += [ordered]@{ path = $relativePath; bytes = $expectedBytes; sha256 = $expectedSha256 }
    }
    $verifiedSourceFiles = @(Sort-ObjectsOrdinalByProperty -Values $verifiedSourceFiles -PropertyName "path")
    $upstreamLicensePath = Join-Path $sourceRoot $sourceLicensePath
    if (@($verifiedSourceFiles | Where-Object { $_.path -ceq $sourceLicensePath }).Count -ne 1) {
        throw "SDL3 source license '$sourceLicensePath' must be a pinned sourceArchiveFiles entry."
    }

    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $stagedSourceRoot = Join-Path $outputRoot "src"
    $stagedSourcePath = Join-Path $stagedSourceRoot "Vendor/SDL3.stark"
    $targetDist = Join-Path $outputRoot (Join-Path "dist" $AssetSuffix)
    $nativeRoot = Join-Path $targetDist "native/sdl3"
    $nativeHeaderRoot = Join-Path $nativeRoot "SDL3"
    $nativeLibraryFileName = if ($targetOperatingSystem -ceq "windows") { "SDL3.lib" } else { "libSDL3.a" }
    $nativeLibraryPath = Join-Path $nativeRoot $nativeLibraryFileName
    $starkLibraryFileName = if ($targetOperatingSystem -ceq "windows") { "VendorSDL3.lib" } else { "libVendorSDL3.a" }
    $starkLibraryPath = Join-Path $targetDist $starkLibraryFileName
    $packageImagePath = [System.IO.Path]::ChangeExtension($starkLibraryPath, ".starkpkg")

    foreach ($ownedPath in @(
        $stagedSourcePath,
        $nativeRoot,
        $starkLibraryPath,
        $packageImagePath
    )) {
        Remove-OwnedPath -Root $outputRoot -Path $ownedPath
    }
    Copy-RequiredFile -Source $starkSourcePath -Destination $stagedSourcePath
    Copy-DirectoryContents -Source (Join-Path $sourceRoot "include/SDL3") -Destination $nativeHeaderRoot
    $licensePath = Join-Path $nativeRoot "LICENSE.txt"
    Copy-RequiredFile -Source $upstreamLicensePath -Destination $licensePath

    $wrapperRoot = Join-Path $workRoot "wrapper"
    $buildRoot = Join-Path $workRoot "build"
    New-Item -ItemType Directory -Force -Path $wrapperRoot | Out-Null
    $wrapperText = @'
cmake_minimum_required(VERSION 3.24)
project(StarkBundledSDL3 LANGUAGES C)

add_subdirectory("${SDL3_SOURCE_ROOT}" SDL3)
target_sources(SDL3-static PRIVATE "${STARK_SDL3_ADAPTER_SOURCE}")
file(GENERATE OUTPUT "${CMAKE_BINARY_DIR}/stark-sdl3-interface-libraries.txt" CONTENT "$<TARGET_PROPERTY:SDL3-static,INTERFACE_LINK_LIBRARIES>\n")
file(GENERATE OUTPUT "${CMAKE_BINARY_DIR}/stark-sdl3-interface-options.txt" CONTENT "$<TARGET_PROPERTY:SDL3-static,INTERFACE_LINK_OPTIONS>\n")
'@
    $wrapperPath = Join-Path $wrapperRoot "CMakeLists.txt"
    [System.IO.File]::WriteAllText(
        $wrapperPath,
        $wrapperText.Replace("`r`n", "`n"),
        [System.Text.UTF8Encoding]::new($false))

    $clangPath = [string]$privateToolPaths["clang"]
    $clangxxPath = [string]$privateToolPaths["clang++"]
    $archiverPath = [string]$privateToolPaths["llvm-ar"]
    $ranlibPath = [string]$privateToolPaths["llvm-ranlib"]
    $linkerToolName = if ($targetOperatingSystem -ceq "windows") { "lld-link" } elseif ($targetOperatingSystem -ceq "macos") { "ld64.lld" } else { "ld.lld" }
    $linkerPath = [string]$privateToolPaths[$linkerToolName]

    $normalizedSourceRoot = "/stark/vendor/$sourcePayloadRoot"
    $commonReleaseFlags = @(
        "--target=$TargetTriple",
        "-O3",
        "-DNDEBUG",
        "-flto=thin",
        "-ffunction-sections",
        "-fdata-sections",
        "-fno-ident",
        "-ffile-prefix-map=$sourceRoot=$normalizedSourceRoot",
        "-fdebug-prefix-map=$sourceRoot=$normalizedSourceRoot",
        "-fmacro-prefix-map=$sourceRoot=$normalizedSourceRoot",
        "-ffile-prefix-map=$repositoryRoot=/stark/repository",
        "-fdebug-prefix-map=$repositoryRoot=/stark/repository",
        "-fmacro-prefix-map=$repositoryRoot=/stark/repository"
    )
    if ($targetOperatingSystem -cne "windows") {
        $commonReleaseFlags += "-fPIC"
    } else {
        $commonReleaseFlags += @("-D_CRT_SECURE_NO_WARNINGS=1", "-DUNICODE=1", "-D_UNICODE=1")
    }
    $releaseFlags = $commonReleaseFlags -join " "

    $cmakeArguments = @(
        "-S", $wrapperRoot,
        "-B", $buildRoot,
        "-G", "Ninja",
        "-DCMAKE_MAKE_PROGRAM=$ninjaExecutable",
        "-DSDL3_SOURCE_ROOT=$sourceRoot",
        "-DSTARK_SDL3_ADAPTER_SOURCE=$adapterSourcePath",
        "-DCMAKE_BUILD_TYPE=Release",
        "-DCMAKE_C_COMPILER=$clangPath",
        "-DCMAKE_CXX_COMPILER=$clangxxPath",
        "-DCMAKE_OBJC_COMPILER=$clangPath",
        "-DCMAKE_OBJCXX_COMPILER=$clangxxPath",
        "-DCMAKE_LINKER=$linkerPath",
        "-DCMAKE_AR=$archiverPath",
        "-DCMAKE_RANLIB=$ranlibPath",
        "-DCMAKE_C_COMPILER_TARGET=$TargetTriple",
        "-DCMAKE_CXX_COMPILER_TARGET=$TargetTriple",
        "-DCMAKE_OBJC_COMPILER_TARGET=$TargetTriple",
        "-DCMAKE_OBJCXX_COMPILER_TARGET=$TargetTriple",
        "-DCMAKE_C_FLAGS_RELEASE=$releaseFlags",
        "-DCMAKE_CXX_FLAGS_RELEASE=$releaseFlags",
        "-DCMAKE_OBJC_FLAGS_RELEASE=$releaseFlags",
        "-DCMAKE_OBJCXX_FLAGS_RELEASE=$releaseFlags",
        "-DCMAKE_DISABLE_FIND_PACKAGE_PkgConfig=TRUE",
        "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"
    )
    $macSdkPath = $null
    $macSdkVersion = $null
    if ($targetOperatingSystem -ceq "macos") {
        $macSdkPath = ((& xcrun --sdk macosx --show-sdk-path 2>&1) | Select-Object -First 1)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($macSdkPath)) {
            throw "xcrun could not resolve the required macOS SDK."
        }
        $macSdkPath = ([string]$macSdkPath).Trim()
        $macSdkVersion = ((& xcrun --sdk macosx --show-sdk-version 2>&1) | Select-Object -First 1)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($macSdkVersion)) {
            throw "xcrun could not resolve the required macOS SDK version."
        }
        $macSdkVersion = ([string]$macSdkVersion).Trim()
        $cmakeArguments += @(
            "-DCMAKE_OSX_SYSROOT=$macSdkPath",
            "-DCMAKE_OSX_ARCHITECTURES=$targetArchitecture",
            "-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0"
        )
    }
    foreach ($option in $cmakeOptions) {
        $cmakeArguments += "-D$option"
    }

    $previousSourceDateEpoch = $env:SOURCE_DATE_EPOCH
    $previousZeroArDate = $env:ZERO_AR_DATE
    try {
        $env:SOURCE_DATE_EPOCH = [string]$sourceDateEpoch
        $env:ZERO_AR_DATE = "1"
        & $cmakeExecutable @cmakeArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Pinned CMake failed to configure SDL3 for '$TargetTriple'."
        }

        $configuredBuildHeaders = @(Get-ChildItem -LiteralPath $buildRoot -File -Recurse -Filter "SDL_build_config.h")
        if ($configuredBuildHeaders.Count -ne 1) {
            throw "SDL3 configure must emit exactly one SDL_build_config.h; found $($configuredBuildHeaders.Count)."
        }
        $configuredBuildText = Get-Content -LiteralPath $configuredBuildHeaders[0].FullName -Raw
        foreach ($define in $requiredBuildDefines) {
            if ($configuredBuildText -cnotmatch "(?m)^#define $([Regex]::Escape($define)) 1$") {
                throw "SDL3 configure for '$AssetSuffix' lost required backend define '$define'. Install the target's declared native development prerequisites before building."
            }
        }

        & $cmakeExecutable --build $buildRoot --target SDL3-static --config Release --parallel
        if ($LASTEXITCODE -ne 0) {
            throw "Pinned CMake/Ninja failed to build SDL3 for '$TargetTriple'."
        }
    } finally {
        $env:SOURCE_DATE_EPOCH = $previousSourceDateEpoch
        $env:ZERO_AR_DATE = $previousZeroArDate
    }

    $cachePath = Join-Path $buildRoot "CMakeCache.txt"
    if (-not (Test-Path -LiteralPath $cachePath -PathType Leaf)) {
        throw "SDL3 CMake configuration did not emit CMakeCache.txt."
    }
    $cacheLines = @(Get-Content -LiteralPath $cachePath)
    foreach ($option in $cmakeOptions) {
        $separatorIndex = $option.IndexOf('=')
        $name = $option.Substring(0, $separatorIndex)
        $value = $option.Substring($separatorIndex + 1)
        $matches = @($cacheLines | Where-Object { $_ -match "^$([Regex]::Escape($name)):[^=]+=$([Regex]::Escape($value))$" })
        if ($matches.Count -ne 1) {
            throw "SDL3 CMake cache did not preserve reviewed option '$option'."
        }
    }

    $buildConfigMatches = @(Get-ChildItem -LiteralPath $buildRoot -File -Recurse -Filter "SDL_build_config.h")
    if ($buildConfigMatches.Count -ne 1) {
        throw "SDL3 build must emit exactly one SDL_build_config.h; found $($buildConfigMatches.Count)."
    }
    $buildConfigPath = $buildConfigMatches[0].FullName
    $buildConfigText = Get-Content -LiteralPath $buildConfigPath -Raw
    foreach ($define in $requiredBuildDefines) {
        if ($buildConfigText -cnotmatch "(?m)^#define $([Regex]::Escape($define)) 1$") {
            throw "SDL3 build for '$AssetSuffix' silently lost required backend define '$define'."
        }
    }
    $actualBuildDefines = @([Regex]::Matches($buildConfigText, '(?m)^#define ((?:SDL|HAVE)_[A-Z0-9_]+) 1$') |
        ForEach-Object { $_.Groups[1].Value })
    $actualBuildDefines = @(Sort-StringsOrdinal -Values $actualBuildDefines)

    $cmakeNativeLibraryName = if ($targetOperatingSystem -ceq "windows") { "SDL3-static.lib" } else { "libSDL3.a" }
    $cmakeNativeLibraryMatches = @(Get-ChildItem -LiteralPath $buildRoot -File -Recurse -Filter $cmakeNativeLibraryName)
    if ($cmakeNativeLibraryMatches.Count -ne 1) {
        throw "SDL3 build must emit exactly one '$cmakeNativeLibraryName'; found $($cmakeNativeLibraryMatches.Count)."
    }
    $archiveFacts = Assert-StaticArchive -Path $cmakeNativeLibraryMatches[0].FullName -ArchiverPath $archiverPath
    Copy-RequiredFile -Source $cmakeNativeLibraryMatches[0].FullName -Destination $nativeLibraryPath
    Assert-StaticArchive -Path $nativeLibraryPath -ArchiverPath $archiverPath | Out-Null

    $interfaceLibrariesPath = Join-Path $buildRoot "stark-sdl3-interface-libraries.txt"
    $interfaceOptionsPath = Join-Path $buildRoot "stark-sdl3-interface-options.txt"
    foreach ($interfacePath in @($interfaceLibrariesPath, $interfaceOptionsPath)) {
        if (-not (Test-Path -LiteralPath $interfacePath -PathType Leaf)) {
            throw "SDL3 build did not emit static target interface evidence '$interfacePath'."
        }
    }
    $cmakeInterfaceLibraries = (Get-Content -LiteralPath $interfaceLibrariesPath -Raw).Trim()
    $cmakeInterfaceOptions = (Get-Content -LiteralPath $interfaceOptionsPath -Raw).Trim()
    $actualCmakeInterfaceLibraries = if ([string]::IsNullOrWhiteSpace($cmakeInterfaceLibraries)) {
        @()
    } else {
        @($cmakeInterfaceLibraries.Split(';'))
    }
    $actualCmakeInterfaceOptions = if ([string]::IsNullOrWhiteSpace($cmakeInterfaceOptions)) {
        @()
    } else {
        @($cmakeInterfaceOptions.Split(';'))
    }
    $actualCmakeInterfaceLibraryFacts = @($actualCmakeInterfaceLibraries |
        ForEach-Object { Convert-CMakeInterfaceLibraryToFact -Value $_ })
    Assert-ExactStringSequence `
        -Actual $actualCmakeInterfaceLibraryFacts `
        -Expected $expectedCmakeInterfaceLibraries `
        -Label "SDL3 CMake static interface libraries"
    Assert-ExactStringSequence `
        -Actual $actualCmakeInterfaceOptions `
        -Expected $expectedCmakeInterfaceOptions `
        -Label "SDL3 CMake static interface options"

    $versionText = @"
# SDL3 native release payload

- Version: $([string](Get-RequiredProperty -Object $package -Name "version"))
- Revision: SDL-release-3.4.10-0-g8e37db5e7
- Source identity: $([string](Get-RequiredProperty -Object $package -Name "sourceIdentity"))
- Source URL: $sourceUrl
- Source SHA-256: $sourceSha256
- Source bytes: $sourceSize
- Target: $TargetTriple
- Build: CMake $($cmakeTool.version), Ninja $($ninjaTool.version), compiler-private Clang O3 + ThinLTO
- Feature policy: all SDL subsystems enabled; target-specific native backends are checked against the reviewed catalog; pkg-config discovery is disabled.
- Stark adapter: compiled once into `$nativeLibraryFileName`; applications do not rebuild `Sdl3Binding.c`.
- License: upstream zlib license copied verbatim to `LICENSE.txt` from the verified source archive.
"@
    $versionPath = Join-Path $nativeRoot "VERSION.md"
    [System.IO.File]::WriteAllText(
        $versionPath,
        $versionText.Replace("`r`n", "`n"),
        [System.Text.UTF8Encoding]::new($false))

    $compilerArguments = @(
        "run", "--project", $compilerProjectPath, "--no-restore", "--",
        $stagedSourcePath,
        "--emit-lib",
        "--no-stark-path",
        "-I", $stagedSourceRoot,
        "-I", $stdlibPackageRoot,
        "-o", $starkLibraryPath,
        "--target", $TargetTriple,
        "--package-profile", "release",
        "--toolchain-dir", $toolchainRoot,
        "--native-include-dir", $nativeRoot,
        "--native-library-dir", $nativeRoot
    )
    foreach ($library in $declaredNativeLibraries) {
        $compilerArguments += @("--native-library", $library)
    }
    foreach ($linkArgument in $declaredNativeLinkArguments) {
        $compilerArguments += @("--native-link-arg", $linkArgument)
    }
    & dotnet @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Stage0 failed to build release package '$packageId' for '$TargetTriple'."
    }
    foreach ($expectedOutput in @($starkLibraryPath, $packageImagePath)) {
        if (-not (Test-Path -LiteralPath $expectedOutput -PathType Leaf)) {
            throw "Stage0 did not emit expected SDL3 package output '$expectedOutput'."
        }
    }

    $inspectionPath = Join-Path $workRoot "VendorSDL3.starkpkg.json"
    $inspection = Invoke-PackageInspection `
        -PackageImagePath $packageImagePath `
        -OutputPath $inspectionPath `
        -CompilerProjectPath $compilerProjectPath
    if ([string](Get-RequiredProperty -Object $inspection -Name "RootModule") -cne $packageId -or
        [string](Get-RequiredProperty -Object $inspection -Name "LibraryFileName") -cne $starkLibraryFileName) {
        throw "Generated SDL3 package has the wrong root module or Stark archive name."
    }
    $inspectionTarget = Get-RequiredProperty -Object $inspection -Name "Target"
    $inspectionProfile = Get-RequiredProperty -Object $inspection -Name "BuildProfile"
    if ($null -eq $inspectionTarget -or
        [string](Get-RequiredProperty -Object $inspectionTarget -Name "Triple") -cne $TargetTriple -or
        $null -eq $inspectionProfile -or
        [string](Get-RequiredProperty -Object $inspectionProfile -Name "Name") -cne "release") {
        throw "Generated SDL3 package lost exact '$TargetTriple' release facts."
    }
    $moduleNames = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $inspection -Name "Modules") |
        ForEach-Object { [string](Get-RequiredProperty -Object $_ -Name "ModuleName") })
    $moduleNames = @(Sort-StringsOrdinal -Values $moduleNames)
    $expectedModules = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $package -Name "modules") |
        ForEach-Object { [string]$_ })
    $expectedModules = @(Sort-StringsOrdinal -Values $expectedModules)
    Assert-ExactStringSequence -Actual $moduleNames -Expected $expectedModules -Label "SDL3 package modules"

    $identity = Get-RequiredProperty -Object $inspection -Name "Identity"
    if ($null -eq $identity -or [string](Get-RequiredProperty -Object $identity -Name "PackageId") -cne $packageId) {
        throw "Generated SDL3 package is missing its exact package identity."
    }
    $identityDependencies = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $identity -Name "Dependencies"))
    $dependencyIds = @(Sort-StringsOrdinal -Values @($identityDependencies |
        ForEach-Object { [string](Get-RequiredProperty -Object $_ -Name "PackageId") }))
    Assert-ExactStringSequence -Actual $dependencyIds -Expected @("System") -Label "SDL3 package dependencies"

    $nativeDependencies = Get-RequiredProperty -Object $inspection -Name "NativeDependencies"
    $nativeSources = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "Sources")
    $includeDirectories = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "IncludeDirectories")
    $libraryDirectories = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "LibraryDirectories")
    $nativeLibraries = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "Libraries")
    $nativeLinkArguments = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "LinkArguments")
    $pkgConfigPackages = @(Get-NativeMetadataValues -NativeDependencies $nativeDependencies -Name "PkgConfigPackages")
    Assert-ExactStringSequence -Actual $nativeSources -Expected @() -Label "SDL3 package native sources"
    Assert-ExactStringSequence -Actual $includeDirectories -Expected @("native/sdl3") -Label "SDL3 package include directories"
    Assert-ExactStringSequence -Actual $libraryDirectories -Expected @("native/sdl3") -Label "SDL3 package library directories"
    Assert-ExactStringSequence -Actual $nativeLibraries -Expected $declaredNativeLibraries -Label "SDL3 package native libraries"
    Assert-ExactStringSequence -Actual $nativeLinkArguments -Expected $declaredNativeLinkArguments -Label "SDL3 package native link arguments"
    Assert-ExactStringSequence -Actual $pkgConfigPackages -Expected @() -Label "SDL3 package pkg-config dependencies"
    foreach ($portablePath in @($nativeSources) + @($includeDirectories) + @($libraryDirectories)) {
        Assert-PortableRelativePath -Path $portablePath -Label "generated SDL3 native path"
    }

    $systemImages = @(Get-ChildItem -LiteralPath $stdlibPackageRoot -File -Recurse -Filter "*.starkpkg" | Sort-Object FullName)
    $systemPackages = @()
    foreach ($systemImage in $systemImages) {
        $systemInspectionPath = Join-Path $workRoot ("system-" + $systemImage.Name + ".json")
        $systemInspection = Invoke-PackageInspection `
            -PackageImagePath $systemImage.FullName `
            -OutputPath $systemInspectionPath `
            -CompilerProjectPath $compilerProjectPath
        if ([string](Get-RequiredProperty -Object $systemInspection -Name "RootModule") -ceq "System") {
            $systemPackages += [pscustomobject]@{ Image = $systemImage; Inspection = $systemInspection }
        }
    }
    if ($systemPackages.Count -ne 1) {
        throw "Staged standard-library directory must contain exactly one System package; found $($systemPackages.Count)."
    }
    $systemInspection = $systemPackages[0].Inspection
    $systemTarget = Get-RequiredProperty -Object $systemInspection -Name "Target"
    $systemProfile = Get-RequiredProperty -Object $systemInspection -Name "BuildProfile"
    $systemIdentity = Get-RequiredProperty -Object $systemInspection -Name "Identity"
    if ($null -eq $systemTarget -or
        [string](Get-RequiredProperty -Object $systemTarget -Name "Triple") -cne $TargetTriple -or
        $null -eq $systemProfile -or
        [string](Get-RequiredProperty -Object $systemProfile -Name "Name") -cne "release") {
        throw "Staged System package must preserve exact '$TargetTriple' release facts."
    }
    if ($identityDependencies.Count -ne 1 -or
        [string](Get-RequiredProperty -Object $identityDependencies[0] -Name "ApiHash") -cne [string](Get-RequiredProperty -Object $systemIdentity -Name "ApiHash") -or
        [string](Get-RequiredProperty -Object $identityDependencies[0] -Name "ContentHash") -cne [string](Get-RequiredProperty -Object $systemIdentity -Name "ContentHash")) {
        throw "Generated SDL3 package does not preserve the staged System API/content identity."
    }

    $runtimeSmokeFileName = if ($targetOperatingSystem -ceq "windows") { "sdl3-bundled-smoke.exe" } else { "sdl3-bundled-smoke" }
    $runtimeSmokePath = Join-Path $workRoot $runtimeSmokeFileName
    $runtimeSmokeArguments = @(
        "run", "--project", $compilerProjectPath, "--no-restore", "--",
        $smokeSourcePath,
        "--emit-exe",
        "--no-stark-path",
        "-I", $stdlibPackageRoot,
        "-I", $targetDist,
        "--target", $TargetTriple,
        "--toolchain-dir", $toolchainRoot,
        "-o", $runtimeSmokePath
    )
    & dotnet @runtimeSmokeArguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $runtimeSmokePath -PathType Leaf)) {
        throw "Stage0 failed to build bundled SDL3 runtime smoke for '$TargetTriple'."
    }
    & $runtimeSmokePath
    $runtimeSmokeExitCode = $LASTEXITCODE
    if ($runtimeSmokeExitCode -ne 0) {
        throw "Bundled SDL3 runtime smoke failed for '$TargetTriple' with exit code $runtimeSmokeExitCode."
    }

    $clangVersion = ((& $clangPath --version 2>&1) | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($clangVersion)) {
        throw "Compiler-private Clang could not report its version."
    }
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
            sourceDateEpoch = $sourceDateEpoch
            files = [object[]]$verifiedSourceFiles
        }
        buildTools = [ordered]@{
            cmake = [ordered]@{ version = $cmakeTool.version; sha256 = $cmakeTool.sha256 }
            ninja = [ordered]@{ version = $ninjaTool.version; sha256 = $ninjaTool.sha256 }
            compiler = [ordered]@{
                path = "toolchain/bin/" + [System.IO.Path]::GetFileName($clangPath)
                version = ([string]$clangVersion).Trim()
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $clangPath).Hash.ToLowerInvariant()
            }
            archiver = "toolchain/bin/" + [System.IO.Path]::GetFileName($archiverPath)
            ranlib = "toolchain/bin/" + [System.IO.Path]::GetFileName($ranlibPath)
            linker = "toolchain/bin/" + [System.IO.Path]::GetFileName($linkerPath)
        }
        nativeBuild = [ordered]@{
            configuration = "Release"
            optimization = "O3"
            lto = "thin"
            functionSections = $true
            dataSections = $true
            deterministicArchive = $true
            pkgConfigDiscovery = $false
            commonCmakeOptions = [object[]]$commonCmakeOptions
            targetCmakeOptions = [object[]]$targetCmakeOptions
            requiredBuildDefines = [object[]]$requiredBuildDefines
            actualEnabledBuildDefines = [object[]]$actualBuildDefines
            generatedBuildConfigSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $buildConfigPath).Hash.ToLowerInvariant()
            cmakeStaticInterfaceLibraries = [object[]]$actualCmakeInterfaceLibraryFacts
            cmakeStaticInterfaceRawLibraries = [object[]]$actualCmakeInterfaceLibraries
            cmakeStaticInterfaceOptions = [object[]]$actualCmakeInterfaceOptions
            archiveMemberCount = $archiveFacts.memberCount
            archiveMemberListSha256 = $archiveFacts.membersSha256
            adapterMember = $archiveFacts.adapterMember
            adapterCompiledIntoNativeArchive = $true
            perApplicationNativeSourceCompilation = $false
            adapterSourceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $adapterSourcePath).Hash.ToLowerInvariant()
            library = Get-PortableRelativePath -Root $outputRoot -Path $nativeLibraryPath -Label "SDL3 native library"
            librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $nativeLibraryPath).Hash.ToLowerInvariant()
            macosSdkPathRecorded = if ($null -eq $macSdkPath) { $null } else { [System.IO.Path]::GetFileName($macSdkPath) }
            macosSdkVersion = $macSdkVersion
            optimizationRationale = "SDL3 is a performance-critical platform boundary. O3 and ThinLTO preserve whole-program optimization opportunities through the static archive, while function/data sections retain dead-stripping granularity. Prefix maps, SOURCE_DATE_EPOCH, ZERO_AR_DATE, private llvm-ar/ranlib, and pinned CMake/Ninja inputs remove build-root, timestamp, and ambient-tool drift."
        }
        emittedNativeFacts = [ordered]@{
            sources = [object[]]$nativeSources
            includeDirectories = [object[]]$includeDirectories
            libraryDirectories = [object[]]$libraryDirectories
            libraries = [object[]]$nativeLibraries
            linkArguments = [object[]]$nativeLinkArguments
            pkgConfigPackages = [object[]]$pkgConfigPackages
        }
        runtimeSmoke = [ordered]@{
            fixture = "tests/fixtures/release/SDL3BundledSmoke.stark"
            exitCode = $runtimeSmokeExitCode
            version = "3.4.10"
            events = "initialized-and-synthetic-quit-round-tripped"
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
    $provenancePath = Join-Path $nativeRoot "PROVENANCE.json"
    Write-DeterministicJson -Value $provenanceValue -Path $provenancePath

    $artifactInputs = @(
        [pscustomobject]@{ Path = $licensePath; Kind = "license" },
        [pscustomobject]@{ Path = $nativeLibraryPath; Kind = "static-library" },
        [pscustomobject]@{ Path = $provenancePath; Kind = "provenance" },
        [pscustomobject]@{ Path = $versionPath; Kind = "documentation" }
    )
    foreach ($headerFile in (Get-ChildItem -LiteralPath $nativeHeaderRoot -File -Recurse | Sort-Object FullName)) {
        $artifactInputs += [pscustomobject]@{ Path = $headerFile.FullName; Kind = "header" }
    }
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
        target = [ordered]@{ id = $AssetSuffix; targetTriple = $TargetTriple }
        package = [ordered]@{
            rootModule = $packageId
            image = Get-PortableRelativePath -Root $outputRoot -Path $packageImagePath -Label "SDL3 package image"
            imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $packageImagePath).Hash.ToLowerInvariant()
            library = Get-PortableRelativePath -Root $outputRoot -Path $starkLibraryPath -Label "SDL3 Stark library"
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

    Write-Host "Prepared pinned SDL3 $($packageEntry.version) release package contribution for '$AssetSuffix'."
    Write-Host "Contribution manifest: $contributionPath"
} finally {
    try {
        if (Test-Path -LiteralPath $workRoot) {
            if (-not (Test-IsSameOrDescendantPath -Path $workRoot -Root $workParent)) {
                throw "Refusing to clean unexpected SDL3 work root '$workRoot'."
            }
            Assert-NoReparsePointPath -Path $workRoot
            Remove-Item -LiteralPath $workRoot -Recurse -Force
        }
    } finally {
        Exit-Sdl3WorkRootLock -Lock $workLock
    }
}
