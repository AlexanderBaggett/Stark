param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("linux-x64", "linux-arm64", "windows-x64", "windows-arm64", "macos-x64", "macos-arm64")]
    [string] $TargetId,

    [string] $ManifestPath = "eng/release/build-tools.json",

    [string] $TargetManifestPath = "eng/release/targets.json",

    [string] $OutputDir = "artifacts/release-build-tools",

    [string] $CacheDir = "artifacts/release-build-tool-cache",

    [string] $ReleaseToolsPath = "",

    [string] $DotNetPath = "dotnet",

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$ownerMarkerName = ".stark-release-build-tools-owner.json"

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)][object] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Label
    )

    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return ,$Object[$Name]
        }
        throw "$Label is missing required property '$Name'."
    }
    foreach ($property in $Object.PSObject.Properties) {
        if ($property.Name -ceq $Name) {
            return ,$property.Value
        }
    }
    throw "$Label is missing required property '$Name'."
}

function Get-ArrayValues {
    param([object] $Value)

    if ($null -eq $Value) {
        return @()
    }
    return @($Value)
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)][object] $Object,
        [Parameter(Mandatory = $true)][string[]] $Names,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $actualList = [System.Collections.Generic.List[string]]::new()
    $actualNames = if ($Object -is [System.Collections.IDictionary]) { @($Object.Keys) } else { @($Object.PSObject.Properties.Name) }
    foreach ($name in $actualNames) { $actualList.Add([string]$name) }
    $actualList.Sort([StringComparer]::Ordinal)
    $expectedList = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $Names) { $expectedList.Add($name) }
    $expectedList.Sort([StringComparer]::Ordinal)
    $actual = @($actualList)
    $expected = @($expectedList)
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        throw "$Label has properties [$($actual -join ', ')]; expected exactly [$($expected -join ', ')]."
    }
}

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $candidate = if ([System.IO.Path]::IsPathRooted($Path)) {
        $Path
    } else {
        Join-Path $repositoryRoot $Path
    }
    return [System.IO.Path]::GetFullPath($candidate)
}

function Test-IsSamePath {
    param([string] $Left, [string] $Right)

    $leftPath = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Left))
    $rightPath = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Right))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    return [string]::Equals($leftPath, $rightPath, $comparison)
}

function Test-IsSameOrDescendantPath {
    param([string] $Path, [string] $Root)

    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    $rootPath = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Root))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    return [string]::Equals($candidate, $rootPath, $comparison) -or
        $candidate.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Assert-NoReparsePointPath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    $currentPath = $pathRoot
    foreach ($segment in $fullPath.Substring($pathRoot.Length).Split(
        [System.IO.Path]::DirectorySeparatorChar,
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            continue
        }
        $item = Get-Item -LiteralPath $currentPath -Force
        $linkTypeProperty = $item.PSObject.Properties["LinkType"]
        $linkType = if ($null -eq $linkTypeProperty) { "" } else { [string]$linkTypeProperty.Value }
        if ((($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) -or
            (-not [string]::IsNullOrWhiteSpace($linkType) -and
             -not [string]::Equals($linkType, "HardLink", [StringComparison]::OrdinalIgnoreCase))) {
            throw "$Label '$fullPath' traverses symbolic link or reparse point '$currentPath'."
        }
    }
}

function Assert-ManagedRoot {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    $fileSystemRoot = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetPathRoot($candidate))
    if (Test-IsSamePath -Left $candidate -Right $fileSystemRoot) {
        throw "$Label '$candidate' cannot be a filesystem root."
    }
    if (Test-IsSamePath -Left (Split-Path -Parent $candidate) -Right $fileSystemRoot) {
        throw "$Label '$candidate' cannot be a direct child of a filesystem root."
    }
    if (Test-IsSameOrDescendantPath -Path $repositoryRoot -Root $candidate) {
        throw "$Label '$candidate' cannot contain the repository."
    }
    if (Test-IsSameOrDescendantPath -Path $candidate -Root $repositoryRoot) {
        if (-not (Test-IsSameOrDescendantPath -Path $candidate -Root $artifactsRoot) -or
            (Test-IsSamePath -Left $candidate -Right $artifactsRoot)) {
            throw "$Label '$candidate' must be a child of repository artifacts when it is inside the checkout."
        }
    }
    foreach ($protectedPath in @($HOME, [System.IO.Path]::GetTempPath()) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (Test-IsSamePath -Left $candidate -Right $protectedPath) {
            throw "$Label '$candidate' cannot be a protected user or temporary root."
        }
    }
    Assert-NoReparsePointPath -Path $candidate -Label $Label
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
        $Path -cmatch '[^\x20-\x7e]' -or
        @($segments | Where-Object { $_ -in @("", ".", "..") }).Count -ne 0) {
        throw "$Label '$Path' is not a portable relative path."
    }
    $reservedWindowsNames = @(
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    )
    foreach ($segment in $segments) {
        $dotIndex = $segment.IndexOf('.')
        $stem = if ($dotIndex -lt 0) { $segment } else { $segment.Substring(0, $dotIndex) }
        if ($segment.EndsWith(" ", [StringComparison]::Ordinal) -or
            $segment.EndsWith(".", [StringComparison]::Ordinal) -or
            $reservedWindowsNames -icontains $stem) {
            throw "$Label '$Path' has a Windows-ambiguous or reserved path segment '$segment'."
        }
    }
}

function Get-ContainedPath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [Parameter(Mandatory = $true)][string] $Label
    )

    Assert-PortableRelativePath -Path $RelativePath -Label $Label
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    if (-not (Test-IsSameOrDescendantPath -Path $candidate -Root $Root) -or
        (Test-IsSamePath -Left $candidate -Right $Root)) {
        throw "$Label '$RelativePath' escapes '$Root'."
    }
    return $candidate
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ExpectedSha256,
        [Parameter(Mandatory = $true)][int64] $ExpectedBytes,
        [Parameter(Mandatory = $true)][string] $Label
    )

    if ($ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Label declares invalid lowercase SHA-256 '$ExpectedSha256'."
    }
    if ($ExpectedBytes -le 0) {
        throw "$Label declares invalid byte count '$ExpectedBytes'."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label '$Path' does not exist."
    }
    $actualBytes = (Get-Item -LiteralPath $Path -Force).Length
    if ($actualBytes -ne $ExpectedBytes) {
        throw "$Label '$Path' has $actualBytes bytes; expected $ExpectedBytes."
    }
    $actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    if ($actualSha256 -cne $ExpectedSha256) {
        throw "$Label '$Path' has SHA-256 '$actualSha256'; expected '$ExpectedSha256'."
    }
}

function Get-PinnedArchive {
    param(
        [Parameter(Mandatory = $true)][object] $Asset,
        [Parameter(Mandatory = $true)][string] $CacheRoot,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $name = [string](Get-RequiredProperty -Object $Asset -Name "name" -Label $Label)
    if ($name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$') {
        throw "$Label name '$name' is not a portable path segment."
    }
    Assert-PortableRelativePath -Path $name -Label "$Label name"
    $url = [string](Get-RequiredProperty -Object $Asset -Name "url" -Label $Label)
    if ($url -cnotmatch '^https://github\.com/[A-Za-z0-9_.\-/]+$') {
        throw "$Label URL '$url' is not an approved immutable GitHub release URL."
    }
    $sha256 = [string](Get-RequiredProperty -Object $Asset -Name "sha256" -Label $Label)
    $bytes = [int64](Get-RequiredProperty -Object $Asset -Name "bytes" -Label $Label)
    $digestCacheRoot = Join-Path $CacheRoot $sha256
    $archivePath = Join-Path $digestCacheRoot $name
    Assert-NoReparsePointPath -Path $digestCacheRoot -Label "$Label cache"
    New-Item -ItemType Directory -Force -Path $digestCacheRoot | Out-Null
    Assert-NoReparsePointPath -Path $digestCacheRoot -Label "$Label cache"
    Assert-NoReparsePointPath -Path $archivePath -Label "$Label cached archive"

    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        Assert-Sha256 -Path $archivePath -ExpectedSha256 $sha256 -ExpectedBytes $bytes -Label "$Label cached archive"
        return $archivePath
    }

    $downloadPath = Join-Path $digestCacheRoot ("$name.download-$([Guid]::NewGuid().ToString('N'))")
    try {
        Invoke-WebRequest -Uri $url -OutFile $downloadPath -MaximumRedirection 10
        Assert-Sha256 -Path $downloadPath -ExpectedSha256 $sha256 -ExpectedBytes $bytes -Label "$Label downloaded archive"
        Move-Item -LiteralPath $downloadPath -Destination $archivePath
    } finally {
        if (Test-Path -LiteralPath $downloadPath) {
            Remove-Item -LiteralPath $downloadPath -Force
        }
    }
    return $archivePath
}

function ConvertFrom-ArchiveHelperOutput {
    param(
        [Parameter(Mandatory = $true)][object[]] $Output,
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][int] $ExitCode
    )

    if ($ExitCode -ne 0) {
        throw "$Label failed with exit code $ExitCode`n$($Output -join [Environment]::NewLine)"
    }
    try {
        $closure = ($Output -join "`n") | ConvertFrom-Json
    } catch {
        throw "$Label emitted invalid closure JSON: $($Output -join [Environment]::NewLine)"
    }
    Assert-ExactProperties `
        -Object $closure `
        -Names @("schemaVersion", "fileCount", "logicalBytes", "directoryCount", "symlinkCount", "treeSha256") `
        -Label "$Label closure"
    if ([int](Get-RequiredProperty -Object $closure -Name "schemaVersion" -Label "$Label closure") -ne 1 -or
        [int](Get-RequiredProperty -Object $closure -Name "fileCount" -Label "$Label closure") -le 0 -or
        [int64](Get-RequiredProperty -Object $closure -Name "logicalBytes" -Label "$Label closure") -le 0 -or
        [int](Get-RequiredProperty -Object $closure -Name "directoryCount" -Label "$Label closure") -lt 0 -or
        [int](Get-RequiredProperty -Object $closure -Name "symlinkCount" -Label "$Label closure") -lt 0 -or
        [string](Get-RequiredProperty -Object $closure -Name "treeSha256" -Label "$Label closure") -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Label emitted an invalid build-tool tree closure."
    }
    return ,$closure
}

function Get-BuildToolTreeClosure {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $DotNetExecutable,
        [Parameter(Mandatory = $true)][string] $ReleaseToolsAssembly,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $output = @(& $DotNetExecutable $ReleaseToolsAssembly inventory-tree --root $Root 2>&1)
    $exitCode = $LASTEXITCODE
    return ,(ConvertFrom-ArchiveHelperOutput -Output $output -Label "$Label inventory" -ExitCode $exitCode)
}

function Expand-PinnedArchive {
    param(
        [Parameter(Mandatory = $true)][object] $Asset,
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][string] $DestinationRoot,
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $DotNetExecutable,
        [Parameter(Mandatory = $true)][string] $ReleaseToolsAssembly
    )

    $archiveKind = [string](Get-RequiredProperty -Object $Asset -Name "archiveKind" -Label $Label)
    $declaredExecutable = [string](Get-RequiredProperty -Object $Asset -Name "executable" -Label $Label)
    Assert-PortableRelativePath -Path $declaredExecutable -Label "$Label executable"
    $firstSeparator = $declaredExecutable.IndexOf('/')
    $requiredRoot = if ($firstSeparator -lt 0) { "" } else { $declaredExecutable.Substring(0, $firstSeparator) }
    if ($archiveKind -notin @("zip", "targz")) {
        throw "$Label declares unsupported archive kind '$archiveKind'."
    }
    $output = @(& $DotNetExecutable $ReleaseToolsAssembly extract-archive `
        --archive $ArchivePath `
        --kind $archiveKind `
        --destination $DestinationRoot `
        --required-root $requiredRoot `
        --label $Label 2>&1)
    $exitCode = $LASTEXITCODE
    return ,(ConvertFrom-ArchiveHelperOutput -Output $output -Label "$Label extraction" -ExitCode $exitCode)
}

function Set-ExecutableMode {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ($IsWindows) {
        return
    }
    $mode = [System.IO.UnixFileMode]::UserRead -bor
        [System.IO.UnixFileMode]::UserWrite -bor
        [System.IO.UnixFileMode]::UserExecute -bor
        [System.IO.UnixFileMode]::GroupRead -bor
        [System.IO.UnixFileMode]::GroupExecute -bor
        [System.IO.UnixFileMode]::OtherRead -bor
        [System.IO.UnixFileMode]::OtherExecute
    [System.IO.File]::SetUnixFileMode($Path, $mode)
}

function Get-ToolVersion {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][ValidateSet("cmake", "ninja")][string] $Tool,
        [Parameter(Mandatory = $true)][string] $ExpectedVersion
    )

    $output = @(& $Path --version 2>&1)
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) {
        throw "Pinned $Tool executable '$Path' could not report its version."
    }
    $actualVersion = if ($Tool -ceq "cmake") {
        $firstLine = ([string]$output[0]).Trim()
        if ($firstLine -cnotmatch '^cmake version ([0-9]+\.[0-9]+\.[0-9]+)$') {
            throw "Pinned CMake emitted unexpected version line '$firstLine'."
        }
        $Matches[1]
    } else {
        ([string]$output[0]).Trim()
    }
    if ($actualVersion -cne $ExpectedVersion) {
        throw "Pinned $Tool executable '$Path' reports '$actualVersion'; expected '$ExpectedVersion'."
    }
    return $actualVersion
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][object] $Value,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $json = ($Value | ConvertTo-Json -Depth 30).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function New-SourceDescriptor {
    param(
        [Parameter(Mandatory = $true)][object] $InputObject,
        [Parameter(Mandatory = $true)][string] $Label
    )

    return [ordered]@{
        name = [string](Get-RequiredProperty -Object $InputObject -Name "name" -Label $Label)
        url = [string](Get-RequiredProperty -Object $InputObject -Name "url" -Label $Label)
        bytes = [int64](Get-RequiredProperty -Object $InputObject -Name "bytes" -Label $Label)
        sha256 = [string](Get-RequiredProperty -Object $InputObject -Name "sha256" -Label $Label)
    }
}

function Test-SourceDescriptorEquals {
    param(
        [Parameter(Mandatory = $true)][object] $Actual,
        [Parameter(Mandatory = $true)][object] $Expected
    )

    try {
        Assert-ExactProperties -Object $Actual -Names @("name", "url", "bytes", "sha256") -Label "Build-tool source descriptor"
        foreach ($name in @("name", "url", "sha256")) {
            if ([string](Get-RequiredProperty -Object $Actual -Name $name -Label "Build-tool source descriptor") -cne
                [string](Get-RequiredProperty -Object $Expected -Name $name -Label "Expected build-tool source descriptor")) {
                return $false
            }
        }
        return [int64](Get-RequiredProperty -Object $Actual -Name "bytes" -Label "Build-tool source descriptor") -eq
            [int64](Get-RequiredProperty -Object $Expected -Name "bytes" -Label "Expected build-tool source descriptor")
    } catch {
        return $false
    }
}

function Test-TreeClosureEquals {
    param(
        [Parameter(Mandatory = $true)][object] $Actual,
        [Parameter(Mandatory = $true)][object] $Expected
    )

    try {
        $names = @("schemaVersion", "fileCount", "logicalBytes", "directoryCount", "symlinkCount", "treeSha256")
        Assert-ExactProperties -Object $Actual -Names $names -Label "Actual build-tool tree closure"
        Assert-ExactProperties -Object $Expected -Names $names -Label "Expected build-tool tree closure"
        foreach ($name in @("schemaVersion", "fileCount", "logicalBytes", "directoryCount", "symlinkCount")) {
            if ([int64](Get-RequiredProperty -Object $Actual -Name $name -Label "Actual build-tool tree closure") -ne
                [int64](Get-RequiredProperty -Object $Expected -Name $name -Label "Expected build-tool tree closure")) {
                return $false
            }
        }
        return [string](Get-RequiredProperty -Object $Actual -Name "treeSha256" -Label "Actual build-tool tree closure") -ceq
            [string](Get-RequiredProperty -Object $Expected -Name "treeSha256" -Label "Expected build-tool tree closure")
    } catch {
        return $false
    }
}

function Assert-ExactOutputRootEntries {
    param([Parameter(Mandatory = $true)][string] $Path)

    $actual = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in (Get-ChildItem -LiteralPath $Path -Force)) { $actual.Add($entry.Name) }
    $actual.Sort([StringComparer]::Ordinal)
    $expected = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @($ownerMarkerName, "cmake", "manifest.json", "ninja")) { $expected.Add($name) }
    $expected.Sort([StringComparer]::Ordinal)
    if ((@($actual) -join "`n") -cne (@($expected) -join "`n")) {
        throw "Existing build-tool output contains unexpected root entries [$(@($actual) -join ', ')]."
    }
}

function Assert-OwnedOutput {
    param([Parameter(Mandatory = $true)][string] $Path)

    $markerPath = Join-Path $Path $ownerMarkerName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Existing build-tool output '$Path' has no Stark ownership marker and will not be replaced."
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    Assert-ExactProperties -Object $marker -Names @("schemaVersion", "kind", "targetId") -Label "Build-tool owner marker"
    if ([int](Get-RequiredProperty -Object $marker -Name "schemaVersion" -Label "Build-tool owner marker") -ne 1 -or
        [string](Get-RequiredProperty -Object $marker -Name "kind" -Label "Build-tool owner marker") -cne "stark-release-build-tools" -or
        [string](Get-RequiredProperty -Object $marker -Name "targetId" -Label "Build-tool owner marker") -cne $TargetId) {
        throw "Existing build-tool output '$Path' has an incompatible ownership marker and will not be replaced."
    }
}

function Test-ExistingOutput {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $CMakeVersion,
        [Parameter(Mandatory = $true)][string] $NinjaVersion,
        [Parameter(Mandatory = $true)][object] $ExpectedSources,
        [Parameter(Mandatory = $true)][string] $DotNetExecutable,
        [Parameter(Mandatory = $true)][string] $ReleaseToolsAssembly
    )

    try {
        Assert-OwnedOutput -Path $Path
        Assert-ExactOutputRootEntries -Path $Path
        $outputManifestPath = Join-Path $Path "manifest.json"
        if (-not (Test-Path -LiteralPath $outputManifestPath -PathType Leaf)) {
            throw "manifest.json is missing"
        }
        $outputManifest = Get-Content -LiteralPath $outputManifestPath -Raw | ConvertFrom-Json
        Assert-ExactProperties `
            -Object $outputManifest `
            -Names @("schemaVersion", "purpose", "targetId", "tools", "sources", "redistribution") `
            -Label "Build-tool output manifest"
        if ([int](Get-RequiredProperty -Object $outputManifest -Name "schemaVersion" -Label "Build-tool output manifest") -ne 1 -or
            [string](Get-RequiredProperty -Object $outputManifest -Name "purpose" -Label "Build-tool output manifest") -cne "build-time-only" -or
            [string](Get-RequiredProperty -Object $outputManifest -Name "targetId" -Label "Build-tool output manifest") -cne $TargetId -or
            [string](Get-RequiredProperty -Object $outputManifest -Name "redistribution" -Label "Build-tool output manifest") -cne "not-in-release-sdk") {
            throw "manifest identity, purpose, target, or redistribution policy does not match"
        }
        $sources = Get-RequiredProperty -Object $outputManifest -Name "sources" -Label "Build-tool output manifest"
        Assert-ExactProperties -Object $sources -Names @("cmake", "cmakeChecksumManifest", "ninja") -Label "Build-tool output sources"
        foreach ($sourceName in @("cmake", "cmakeChecksumManifest", "ninja")) {
            if (-not (Test-SourceDescriptorEquals `
                -Actual (Get-RequiredProperty -Object $sources -Name $sourceName -Label "Build-tool output sources") `
                -Expected (Get-RequiredProperty -Object $ExpectedSources -Name $sourceName -Label "Expected build-tool sources"))) {
                throw "source descriptor '$sourceName' does not match the selected pinned input"
            }
        }
        $tools = Get-RequiredProperty -Object $outputManifest -Name "tools" -Label "Build-tool output manifest"
        Assert-ExactProperties -Object $tools -Names @("cmake", "ninja") -Label "Build-tool output tools"
        foreach ($toolSpec in @(
            [pscustomobject]@{ Name = "cmake"; ExpectedVersion = $CMakeVersion },
            [pscustomobject]@{ Name = "ninja"; ExpectedVersion = $NinjaVersion }
        )) {
            $tool = Get-RequiredProperty -Object $tools -Name $toolSpec.Name -Label "Build-tool output tools"
            Assert-ExactProperties -Object $tool -Names @("version", "path", "sha256", "closure") -Label "$($toolSpec.Name) output"
            $relativePath = [string](Get-RequiredProperty -Object $tool -Name "path" -Label "$($toolSpec.Name) output")
            $executable = Get-ContainedPath -Root $Path -RelativePath $relativePath -Label "$($toolSpec.Name) output path"
            if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
                throw "$($toolSpec.Name) executable is missing"
            }
            $expectedSha256 = [string](Get-RequiredProperty -Object $tool -Name "sha256" -Label "$($toolSpec.Name) output")
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $executable).Hash.ToLowerInvariant() -cne $expectedSha256) {
                throw "$($toolSpec.Name) executable failed its output-manifest hash"
            }
            # Existing outputs are immutable inputs to validation. Invoking the
            # tool below proves it is executable without mutating a previously
            # authenticated tree (chmod can also fail on protected CI caches).
            if ((Get-ToolVersion -Path $executable -Tool $toolSpec.Name -ExpectedVersion $toolSpec.ExpectedVersion) -cne $toolSpec.ExpectedVersion) {
                throw "$($toolSpec.Name) executable version does not match"
            }
            $expectedClosure = Get-RequiredProperty -Object $tool -Name "closure" -Label "$($toolSpec.Name) output"
            $actualClosure = Get-BuildToolTreeClosure `
                -Root (Join-Path $Path $toolSpec.Name) `
                -DotNetExecutable $DotNetExecutable `
                -ReleaseToolsAssembly $ReleaseToolsAssembly `
                -Label "$($toolSpec.Name) output"
            if (-not (Test-TreeClosureEquals -Actual $actualClosure -Expected $expectedClosure)) {
                throw "$($toolSpec.Name) extracted tree closure does not match the output manifest"
            }
        }
        return $true
    } catch {
        Write-Warning "Existing build-tool output '$Path' failed closed validation and will be replaced: $($_.Exception.Message)"
        return $false
    }
}

function Assert-BuildToolAssetDescriptor {
    param(
        [Parameter(Mandatory = $true)][object] $Asset,
        [Parameter(Mandatory = $true)][string] $Tool,
        [Parameter(Mandatory = $true)][string] $Version
    )

    Assert-ExactProperties `
        -Object $Asset `
        -Names @("id", "name", "url", "bytes", "sha256", "archiveKind", "executable") `
        -Label "$Tool asset"
    $id = [string](Get-RequiredProperty -Object $Asset -Name "id" -Label "$Tool asset")
    $name = [string](Get-RequiredProperty -Object $Asset -Name "name" -Label "$Tool asset")
    $url = [string](Get-RequiredProperty -Object $Asset -Name "url" -Label "$Tool asset")
    $bytes = [int64](Get-RequiredProperty -Object $Asset -Name "bytes" -Label "$Tool asset")
    $sha256 = [string](Get-RequiredProperty -Object $Asset -Name "sha256" -Label "$Tool asset")
    $archiveKind = [string](Get-RequiredProperty -Object $Asset -Name "archiveKind" -Label "$Tool asset")
    $executable = [string](Get-RequiredProperty -Object $Asset -Name "executable" -Label "$Tool asset")
    if ($id -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or
        $name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$' -or
        $bytes -le 0 -or
        $sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $archiveKind -notin @("zip", "targz")) {
        throw "$Tool asset '$id' has an invalid pinned identity, size, hash, or archive kind."
    }
    Assert-PortableRelativePath -Path $executable -Label "$Tool asset executable"
    $expectedBaseUrl = if ($Tool -ceq "CMake") {
        "https://github.com/Kitware/CMake/releases/download/v$Version"
    } else {
        "https://github.com/ninja-build/ninja/releases/download/v$Version"
    }
    if ($url -cne "$expectedBaseUrl/$name") {
        throw "$Tool asset '$id' URL '$url' is not its exact immutable release URL."
    }
    if (($archiveKind -ceq "zip" -and -not $name.EndsWith(".zip", [StringComparison]::Ordinal)) -or
        ($archiveKind -ceq "targz" -and -not $name.EndsWith(".tar.gz", [StringComparison]::Ordinal))) {
        throw "$Tool asset '$id' archive name does not match kind '$archiveKind'."
    }
}

function Assert-CMakeChecksumEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object] $Asset
    )

    $assetName = [string](Get-RequiredProperty -Object $Asset -Name "name" -Label "CMake asset")
    $assetSha256 = [string](Get-RequiredProperty -Object $Asset -Name "sha256" -Label "CMake asset")
    $entries = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._+\-]{0,127})$') {
            throw "Pinned CMake checksum manifest contains malformed line '$line'."
        }
        if ($entries.ContainsKey($Matches[2])) {
            throw "Pinned CMake checksum manifest repeats asset '$($Matches[2])'."
        }
        $entries.Add($Matches[2], $Matches[1])
    }
    if (-not $entries.ContainsKey($assetName) -or $entries[$assetName] -cne $assetSha256) {
        throw "Pinned CMake checksum manifest does not authenticate '$assetName' as '$assetSha256'."
    }
}

$manifestFullPath = Resolve-RepositoryPath -Path $ManifestPath
$targetManifestFullPath = Resolve-RepositoryPath -Path $TargetManifestPath
$outputRoot = Resolve-RepositoryPath -Path $OutputDir
$cacheRoot = Resolve-RepositoryPath -Path $CacheDir
$releaseToolsProject = Join-Path $repositoryRoot "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj"
Assert-ManagedRoot -Path $outputRoot -Label "Build-tool output root"
Assert-ManagedRoot -Path $cacheRoot -Label "Build-tool cache root"
if ((Test-IsSameOrDescendantPath -Path $outputRoot -Root $cacheRoot) -or
    (Test-IsSameOrDescendantPath -Path $cacheRoot -Root $outputRoot)) {
    throw "Build-tool output root and cache root must not overlap."
}
foreach ($inputPath in @($manifestFullPath, $targetManifestFullPath, $releaseToolsProject)) {
    Assert-NoReparsePointPath -Path $inputPath -Label "Build-tool manifest"
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
        throw "Required build-tool manifest '$inputPath' does not exist."
    }
}
$dotnetCommand = Get-Command $DotNetPath -CommandType Application -ErrorAction Stop | Select-Object -First 1
$dotnetExecutable = $dotnetCommand.Source
$releaseToolsAssembly = @(& (Join-Path $PSScriptRoot "resolve-release-tools.ps1") `
    -RepositoryRoot $repositoryRoot `
    -DotNetPath $dotnetExecutable `
    -ReleaseToolsPath $ReleaseToolsPath) | Select-Object -Last 1

$targetDocument = Get-Content -LiteralPath $targetManifestFullPath -Raw | ConvertFrom-Json
Assert-ExactProperties `
    -Object $targetDocument `
    -Names @("schemaVersion", "architecturePolicy", "minimumOsPolicyStatus", "targets") `
    -Label "Target manifest"
if ([int](Get-RequiredProperty -Object $targetDocument -Name "schemaVersion" -Label "Target manifest") -ne 1 -or
    [string](Get-RequiredProperty -Object $targetDocument -Name "architecturePolicy" -Label "Target manifest") -cne "64-bit-only") {
    throw "Target manifest must use schema 1 and the 64-bit-only architecture policy."
}
$targetMatches = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $targetDocument -Name "targets" -Label "Target manifest") |
    Where-Object { [string](Get-RequiredProperty -Object $_ -Name "id" -Label "Target entry") -ceq $TargetId })
if ($targetMatches.Count -ne 1) {
    throw "Target manifest must contain exactly one '$TargetId' entry; found $($targetMatches.Count)."
}
$target = $targetMatches[0]
Assert-ExactProperties `
    -Object $target `
    -Names @("id", "operatingSystem", "architecture", "runtimeIdentifier", "targetTriple", "githubRunner", "assetSuffix", "archiveKind", "archiveExtension", "compilerExecutable", "standardLibrary", "privateBackendSelection", "installerKind", "minimumOs", "hostPrerequisite", "supportTier", "releaseEnabled") `
    -Label "Target '$TargetId'"
$requiredOperatingSystem = [string](Get-RequiredProperty -Object $target -Name "operatingSystem" -Label "Target '$TargetId'")
$requiredArchitecture = [string](Get-RequiredProperty -Object $target -Name "architecture" -Label "Target '$TargetId'")
$hostOperatingSystem = if ($IsWindows) { "windows" } elseif ($IsLinux) { "linux" } elseif ($IsMacOS) { "macos" } else { "unknown" }
$hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if ($hostOperatingSystem -cne $requiredOperatingSystem -or $hostArchitecture -cne $requiredArchitecture) {
    throw "Release build tools for '$TargetId' must be acquired on $requiredOperatingSystem-$requiredArchitecture; this host is $hostOperatingSystem-$hostArchitecture."
}

$manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json
Assert-ExactProperties -Object $manifest -Names @("schemaVersion", "purpose", "tools", "targets") -Label "Build-tool manifest"
if ([int](Get-RequiredProperty -Object $manifest -Name "schemaVersion" -Label "Build-tool manifest") -ne 1 -or
    [string](Get-RequiredProperty -Object $manifest -Name "purpose" -Label "Build-tool manifest") -cne "build-time-only") {
    throw "Build-tool manifest must use schema 1 and declare build-time-only purpose."
}
$tools = Get-RequiredProperty -Object $manifest -Name "tools" -Label "Build-tool manifest"
Assert-ExactProperties -Object $tools -Names @("cmake", "ninja") -Label "Build-tool tools"
$cmake = Get-RequiredProperty -Object $tools -Name "cmake" -Label "Build-tool tools"
$ninja = Get-RequiredProperty -Object $tools -Name "ninja" -Label "Build-tool tools"
$cmakeProperties = @("version", "releaseUrl", "checksumManifest", "assets")
$ninjaProperties = @("version", "releaseUrl", "assets")
Assert-ExactProperties -Object $cmake -Names $cmakeProperties -Label "CMake manifest"
Assert-ExactProperties -Object $ninja -Names $ninjaProperties -Label "Ninja manifest"
$cmakeVersion = [string](Get-RequiredProperty -Object $cmake -Name "version" -Label "CMake manifest")
$ninjaVersion = [string](Get-RequiredProperty -Object $ninja -Name "version" -Label "Ninja manifest")
if ($cmakeVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
    $ninjaVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
    [string](Get-RequiredProperty -Object $cmake -Name "releaseUrl" -Label "CMake manifest") -cne "https://github.com/Kitware/CMake/releases/tag/v$cmakeVersion" -or
    [string](Get-RequiredProperty -Object $ninja -Name "releaseUrl" -Label "Ninja manifest") -cne "https://github.com/ninja-build/ninja/releases/tag/v$ninjaVersion") {
    throw "Build-tool versions and release URLs must be exact immutable semantic-version tags."
}

$targetMappings = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $manifest -Name "targets" -Label "Build-tool manifest") |
    Where-Object { [string](Get-RequiredProperty -Object $_ -Name "id" -Label "Build-tool target mapping") -ceq $TargetId })
if ($targetMappings.Count -ne 1) {
    throw "Build-tool manifest must contain exactly one '$TargetId' mapping; found $($targetMappings.Count)."
}
$targetMapping = $targetMappings[0]
Assert-ExactProperties -Object $targetMapping -Names @("id", "cmakeAsset", "ninjaAsset") -Label "Build-tool target '$TargetId'"
$cmakeAssetId = [string](Get-RequiredProperty -Object $targetMapping -Name "cmakeAsset" -Label "Build-tool target '$TargetId'")
$ninjaAssetId = [string](Get-RequiredProperty -Object $targetMapping -Name "ninjaAsset" -Label "Build-tool target '$TargetId'")
$cmakeAssets = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $cmake -Name "assets" -Label "CMake manifest") |
    Where-Object { [string](Get-RequiredProperty -Object $_ -Name "id" -Label "CMake asset") -ceq $cmakeAssetId })
$ninjaAssets = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $ninja -Name "assets" -Label "Ninja manifest") |
    Where-Object { [string](Get-RequiredProperty -Object $_ -Name "id" -Label "Ninja asset") -ceq $ninjaAssetId })
if ($cmakeAssets.Count -ne 1 -or $ninjaAssets.Count -ne 1) {
    throw "Build-tool mapping '$TargetId' must resolve exactly one CMake and Ninja asset."
}
$cmakeAsset = $cmakeAssets[0]
$ninjaAsset = $ninjaAssets[0]
Assert-BuildToolAssetDescriptor -Asset $cmakeAsset -Tool "CMake" -Version $cmakeVersion
Assert-BuildToolAssetDescriptor -Asset $ninjaAsset -Tool "Ninja" -Version $ninjaVersion

$cmakeChecksumManifest = Get-RequiredProperty -Object $cmake -Name "checksumManifest" -Label "CMake manifest"
Assert-ExactProperties -Object $cmakeChecksumManifest -Names @("name", "url", "bytes", "sha256") -Label "CMake checksum manifest"
$checksumName = [string](Get-RequiredProperty -Object $cmakeChecksumManifest -Name "name" -Label "CMake checksum manifest")
if ($checksumName -cne "cmake-$cmakeVersion-SHA-256.txt" -or
    [string](Get-RequiredProperty -Object $cmakeChecksumManifest -Name "url" -Label "CMake checksum manifest") -cne "https://github.com/Kitware/CMake/releases/download/v$cmakeVersion/$checksumName") {
    throw "CMake checksum manifest is not the exact pinned upstream release evidence."
}

$expectedSources = [ordered]@{
    cmake = New-SourceDescriptor -InputObject $cmakeAsset -Label "CMake asset"
    cmakeChecksumManifest = New-SourceDescriptor -InputObject $cmakeChecksumManifest -Label "CMake checksum manifest"
    ninja = New-SourceDescriptor -InputObject $ninjaAsset -Label "Ninja asset"
}
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
$checksumManifestPath = Get-PinnedArchive `
    -Asset $cmakeChecksumManifest `
    -CacheRoot (Join-Path $cacheRoot "downloads") `
    -Label "CMake $cmakeVersion checksum manifest"
Assert-CMakeChecksumEvidence -Path $checksumManifestPath -Asset $cmakeAsset

if ((Test-Path -LiteralPath $outputRoot) -and -not $Force) {
    if (Test-ExistingOutput `
        -Path $outputRoot `
        -CMakeVersion $cmakeVersion `
        -NinjaVersion $ninjaVersion `
        -ExpectedSources $expectedSources `
        -DotNetExecutable $dotnetExecutable `
        -ReleaseToolsAssembly $releaseToolsAssembly) {
        Write-Host "Pinned release build tools for $TargetId already exist at $outputRoot"
        return
    }
    Assert-OwnedOutput -Path $outputRoot
}
if (Test-Path -LiteralPath $outputRoot) {
    Assert-OwnedOutput -Path $outputRoot
}

$cmakeArchive = Get-PinnedArchive -Asset $cmakeAsset -CacheRoot (Join-Path $cacheRoot "downloads") -Label "CMake $cmakeVersion"
$ninjaArchive = Get-PinnedArchive -Asset $ninjaAsset -CacheRoot (Join-Path $cacheRoot "downloads") -Label "Ninja $ninjaVersion"

$outputParent = Split-Path -Parent $outputRoot
$outputLeaf = Split-Path -Leaf $outputRoot
New-Item -ItemType Directory -Force -Path $outputParent | Out-Null
$operationToken = [Guid]::NewGuid().ToString("N")
$workRoot = Join-Path $outputParent (".$outputLeaf.work-$operationToken")
$stagedRoot = Join-Path $workRoot "output"
$backupRoot = Join-Path $outputParent (".$outputLeaf.backup-$operationToken")
try {
    New-Item -ItemType Directory -Force -Path $stagedRoot | Out-Null
    [void](Expand-PinnedArchive `
        -Asset $cmakeAsset `
        -ArchivePath $cmakeArchive `
        -DestinationRoot (Join-Path $stagedRoot "cmake") `
        -Label "CMake $cmakeVersion archive" `
        -DotNetExecutable $dotnetExecutable `
        -ReleaseToolsAssembly $releaseToolsAssembly)
    [void](Expand-PinnedArchive `
        -Asset $ninjaAsset `
        -ArchivePath $ninjaArchive `
        -DestinationRoot (Join-Path $stagedRoot "ninja") `
        -Label "Ninja $ninjaVersion archive" `
        -DotNetExecutable $dotnetExecutable `
        -ReleaseToolsAssembly $releaseToolsAssembly)

    $cmakeRelativePath = "cmake/" + [string](Get-RequiredProperty -Object $cmakeAsset -Name "executable" -Label "CMake asset")
    $ninjaRelativePath = "ninja/" + [string](Get-RequiredProperty -Object $ninjaAsset -Name "executable" -Label "Ninja asset")
    $cmakeExecutable = Get-ContainedPath -Root $stagedRoot -RelativePath $cmakeRelativePath -Label "CMake executable"
    $ninjaExecutable = Get-ContainedPath -Root $stagedRoot -RelativePath $ninjaRelativePath -Label "Ninja executable"
    foreach ($executable in @($cmakeExecutable, $ninjaExecutable)) {
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Pinned build-tool archive did not produce expected executable '$executable'."
        }
        Set-ExecutableMode -Path $executable
    }
    [void](Get-ToolVersion -Path $cmakeExecutable -Tool "cmake" -ExpectedVersion $cmakeVersion)
    [void](Get-ToolVersion -Path $ninjaExecutable -Tool "ninja" -ExpectedVersion $ninjaVersion)
    # Capture the closure only after executable-mode normalization so the
    # manifest authenticates the final reusable tree, not a transient archive
    # extraction state.
    $cmakeClosure = Get-BuildToolTreeClosure `
        -Root (Join-Path $stagedRoot "cmake") `
        -DotNetExecutable $dotnetExecutable `
        -ReleaseToolsAssembly $releaseToolsAssembly `
        -Label "CMake $cmakeVersion staged output"
    $ninjaClosure = Get-BuildToolTreeClosure `
        -Root (Join-Path $stagedRoot "ninja") `
        -DotNetExecutable $dotnetExecutable `
        -ReleaseToolsAssembly $releaseToolsAssembly `
        -Label "Ninja $ninjaVersion staged output"

    $outputManifest = [ordered]@{
        schemaVersion = 1
        purpose = "build-time-only"
        targetId = $TargetId
        tools = [ordered]@{
            cmake = [ordered]@{
                version = $cmakeVersion
                path = $cmakeRelativePath
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $cmakeExecutable).Hash.ToLowerInvariant()
                closure = $cmakeClosure
            }
            ninja = [ordered]@{
                version = $ninjaVersion
                path = $ninjaRelativePath
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $ninjaExecutable).Hash.ToLowerInvariant()
                closure = $ninjaClosure
            }
        }
        sources = $expectedSources
        redistribution = "not-in-release-sdk"
    }
    Write-JsonFile -Value $outputManifest -Path (Join-Path $stagedRoot "manifest.json")
    Write-JsonFile -Value ([ordered]@{
        schemaVersion = 1
        kind = "stark-release-build-tools"
        targetId = $TargetId
    }) -Path (Join-Path $stagedRoot $ownerMarkerName)

    $movedOldOutput = $false
    try {
        if (Test-Path -LiteralPath $outputRoot) {
            Move-Item -LiteralPath $outputRoot -Destination $backupRoot
            $movedOldOutput = $true
        }
        Move-Item -LiteralPath $stagedRoot -Destination $outputRoot
        if ($movedOldOutput -and (Test-Path -LiteralPath $backupRoot)) {
            Remove-Item -LiteralPath $backupRoot -Recurse -Force
        }
    } catch {
        if (-not (Test-Path -LiteralPath $outputRoot) -and $movedOldOutput -and (Test-Path -LiteralPath $backupRoot)) {
            Move-Item -LiteralPath $backupRoot -Destination $outputRoot
        }
        throw
    }
} finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
    # A surviving backup means replacement or rollback did not complete. Keep
    # it for explicit recovery; deleting it here would destroy the last known
    # good tool closure after a failed transaction.
}

Write-Host "Acquired pinned release build tools for $TargetId at $outputRoot"
