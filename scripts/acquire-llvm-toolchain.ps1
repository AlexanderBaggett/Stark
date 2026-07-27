param(
    [Parameter(Mandatory = $true)]
    [string] $AssetSuffix,

    [string] $ManifestPath = "scripts/llvm-22.1.8-assets.json",

    [string] $OutputDir = "artifacts/toolchain",

    [string] $CacheDir = "artifacts/llvm-cache",

    [string] $CMakePath = "",

    [string] $NinjaPath = "",

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$ownerMarkerName = ".stark-llvm-toolchain-owner.json"

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
    return [string]::Equals($candidate, $rootPath, $comparison) `
        -or $candidate.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Test-PathEntryExists {
    param([string] $Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -ne $item) {
        return $true
    }

    # Test-Path and Get-Item can both report false for a dangling symbolic link.
    # Enumerating its existing parent still exposes the link itself.
    $parent = Split-Path -Parent $Path
    $leaf = Split-Path -Leaf $Path
    if ([string]::IsNullOrWhiteSpace($parent) -or
        -not (Test-Path -LiteralPath $parent -PathType Container)) {
        return $false
    }

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    foreach ($entry in (Get-ChildItem -LiteralPath $parent -Force -ErrorAction SilentlyContinue)) {
        if ([string]::Equals($entry.Name, $leaf, $comparison)) {
            return $true
        }
    }

    return $false
}

function Get-PathEntryIncludingDanglingLink {
    param([string] $Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -ne $item) {
        return $item
    }

    $parent = Split-Path -Parent $Path
    $leaf = Split-Path -Leaf $Path
    if ([string]::IsNullOrWhiteSpace($parent) -or
        -not (Test-Path -LiteralPath $parent -PathType Container)) {
        return $null
    }

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    foreach ($entry in (Get-ChildItem -LiteralPath $parent -Force -ErrorAction SilentlyContinue)) {
        if ([string]::Equals($entry.Name, $leaf, $comparison)) {
            return $entry
        }
    }

    return $null
}

function Assert-NoReparsePointPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [string] $Label = "Path"
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    $currentPath = $rootPath
    foreach ($segment in $fullPath.Substring($rootPath.Length).Split(
        [System.IO.Path]::DirectorySeparatorChar,
        [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $segment
        $item = Get-PathEntryIncludingDanglingLink -Path $currentPath
        if ($null -eq $item) {
            continue
        }

        $linkTypeProperty = $item.PSObject.Properties["LinkType"]
        $linkType = if ($null -eq $linkTypeProperty) { "" } else { [string] $linkTypeProperty.Value }
        $hasReparseLinkType = -not [string]::IsNullOrWhiteSpace($linkType) -and
            -not [string]::Equals($linkType, "HardLink", [StringComparison]::OrdinalIgnoreCase)
        if ((($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) -or $hasReparseLinkType) {
            throw "$Label '$fullPath' traverses symbolic link or reparse point '$currentPath'."
        }
    }
}

function Resolve-ManagedPath {
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

    $fullPath = [System.IO.Path]::GetFullPath($candidate)
    $fileSystemRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if (Test-IsSamePath -Left $fullPath -Right $fileSystemRoot) {
        throw "$Name '$fullPath' cannot be a filesystem root."
    }

    $parentPath = Split-Path -Parent $fullPath
    if (Test-IsSamePath -Left $parentPath -Right $fileSystemRoot) {
        throw "$Name '$fullPath' cannot be a direct child of a filesystem root."
    }

    # A generated directory must never be an ancestor of the checkout. That
    # also protects the user's home directory and every repository parent.
    if (Test-IsSameOrDescendantPath -Path $repositoryRoot -Root $fullPath) {
        throw "$Name '$fullPath' cannot be the repository or one of its ancestors."
    }

    if (Test-IsSameOrDescendantPath -Path $fullPath -Root $repositoryRoot) {
        if (-not (Test-IsSameOrDescendantPath -Path $fullPath -Root $artifactsRoot) -or
            (Test-IsSamePath -Left $fullPath -Right $artifactsRoot)) {
            throw "$Name '$fullPath' is inside the repository but outside its dedicated artifacts subdirectories."
        }
    }

    foreach ($protectedExactPath in @($HOME, [System.IO.Path]::GetTempPath())) {
        if (-not [string]::IsNullOrWhiteSpace($protectedExactPath) -and
            (Test-IsSamePath -Left $fullPath -Right $protectedExactPath)) {
            throw "$Name '$fullPath' cannot be a protected user or temporary root."
        }
    }

    $protectedRoots = @()
    if ($IsWindows) {
        $protectedRoots += @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData)
    } else {
        $protectedRoots += @(
            "/Applications", "/Library", "/System", "/bin", "/boot", "/dev", "/etc",
            "/lib", "/lib64", "/opt", "/private/etc", "/private/var", "/proc", "/sbin",
            "/sys", "/usr", "/var")
    }

    foreach ($protectedRoot in $protectedRoots) {
        if (-not [string]::IsNullOrWhiteSpace($protectedRoot) -and
            (Test-IsSameOrDescendantPath -Path $fullPath -Root $protectedRoot)) {
            throw "$Name '$fullPath' is inside protected system path '$protectedRoot'."
        }
    }

    Assert-NoReparsePointPath -Path $fullPath -Label $Name
    return $fullPath
}

function Get-ContainedChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Child,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $candidate = [System.IO.Path]::GetFullPath((Join-Path $Root $Child))
    if (-not (Test-IsSameOrDescendantPath -Path $candidate -Root $Root) -or
        (Test-IsSamePath -Left $candidate -Right $Root)) {
        throw "$Name '$candidate' must be a child of '$Root'."
    }

    Assert-NoReparsePointPath -Path $candidate -Label $Name
    return $candidate
}

function Write-OwnerMarker {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [string] $Kind,

        [Parameter(Mandatory = $true)]
        [string] $IntendedOutputRoot,

        [Parameter(Mandatory = $true)]
        [string] $Token
    )

    $markerPath = Join-Path $Directory $ownerMarkerName
    Assert-NoReparsePointPath -Path $markerPath -Label "Ownership marker"
    [ordered]@{
        schemaVersion = 1
        kind = $Kind
        intendedOutputRoot = [System.IO.Path]::GetFullPath($IntendedOutputRoot)
        token = $Token
    } | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding utf8
}

function Write-PortableOutputOwnerMarker {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory
    )

    $markerPath = Join-Path $Directory $ownerMarkerName
    Assert-NoReparsePointPath -Path $markerPath -Label "Ownership marker"
    [ordered]@{
        schemaVersion = 1
        kind = "stark-llvm-output"
    } | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding utf8
}

function Assert-OwnedDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [string] $Kind,

        [Parameter(Mandatory = $true)]
        [string] $IntendedOutputRoot,

        [string] $Token = ""
    )

    Assert-NoReparsePointPath -Path $Directory -Label "Managed directory"
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "Refusing to replace '$Directory' because it is not an owned directory."
    }

    $markerPath = Join-Path $Directory $ownerMarkerName
    Assert-NoReparsePointPath -Path $markerPath -Label "Ownership marker"
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Refusing to replace unowned directory '$Directory'; ownership marker '$ownerMarkerName' is missing."
    }

    try {
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    } catch {
        throw "Refusing to replace '$Directory'; ownership marker '$markerPath' is invalid: $($_.Exception.Message)"
    }

    if ([int] $marker.schemaVersion -ne 1 -or
        -not [string]::Equals([string] $marker.kind, $Kind, [StringComparison]::Ordinal)) {
        throw "Refusing to replace '$Directory'; ownership marker '$markerPath' does not match this operation."
    }

    # Completed output markers intentionally omit machine-specific absolute
    # paths and random tokens so packaged toolchains remain reproducible and
    # do not disclose the CI checkout location. Temporary work/stage markers
    # retain both fields and are checked against the current operation token.
    if ([string]::Equals($Kind, "stark-llvm-output", [StringComparison]::Ordinal) -and
        [string]::IsNullOrWhiteSpace($Token)) {
        return
    }

    if (-not (Test-IsSamePath -Left ([string] $marker.intendedOutputRoot) -Right $IntendedOutputRoot) -or
        -not [string]::Equals([string] $marker.token, $Token, [StringComparison]::Ordinal)) {
        throw "Refusing to replace '$Directory'; ownership marker '$markerPath' does not match this operation."
    }
}

function Remove-OwnedDirectory {
    param(
        [Parameter(Mandatory = $true)] [string] $Directory,
        [Parameter(Mandatory = $true)] [string] $Kind,
        [Parameter(Mandatory = $true)] [string] $IntendedOutputRoot,
        [string] $Token = ""
    )

    # Keep this validation adjacent to the destructive operation so a changed
    # path or replacement link cannot silently widen the deletion target.
    Assert-OwnedDirectory -Directory $Directory -Kind $Kind -IntendedOutputRoot $IntendedOutputRoot -Token $Token
    Assert-NoReparsePointPath -Path $Directory -Label "Managed directory"
    Remove-Item -LiteralPath $Directory -Recurse -Force
}

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $candidate = if ([System.IO.Path]::IsPathRooted($Path)) {
        $Path
    } else {
        Join-Path $repositoryRoot $Path
    }

    return [System.IO.Path]::GetFullPath($candidate)
}

function Get-JsonProperty {
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

function Get-SortedUniqueStrings {
    param(
        [object[]] $Values = @()
    )

    $unique = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in $Values) {
        $text = [string] $value
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            [void] $unique.Add($text.Replace('\', '/'))
        }
    }

    [string[]] $result = @($unique)
    [Array]::Sort($result, [StringComparer]::Ordinal)
    return $result
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSha256
    )

    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actual, $ExpectedSha256.ToLowerInvariant(), [StringComparison]::Ordinal)) {
        throw "SHA256 mismatch for '$Path'. Expected $ExpectedSha256, got $actual."
    }
}

function Save-Asset {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Asset,

        [Parameter(Mandatory = $true)]
        [string] $DestinationDirectory
    )

    $name = [string] $Asset.name
    Assert-PortablePathSegment -Value $name -Name "LLVM asset filename"
    Assert-NoReparsePointPath -Path $DestinationDirectory -Label "LLVM asset cache directory"
    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    Assert-NoReparsePointPath -Path $DestinationDirectory -Label "LLVM asset cache directory"

    $url = [string] $Asset.url
    $sha256 = [string] $Asset.sha256
    $destination = Get-ContainedChildPath -Root $DestinationDirectory -Child $name -Name "LLVM cached asset"

    if ($Force -or -not (Test-Path -LiteralPath $destination -PathType Leaf)) {
        Assert-NoReparsePointPath -Path $destination -Label "LLVM cached asset"
        Invoke-WebRequest -Uri $url -OutFile $destination
    }

    Assert-NoReparsePointPath -Path $destination -Label "LLVM cached asset"
    Assert-Sha256 -Path $destination -ExpectedSha256 $sha256
    return $destination
}

function Assert-SafeRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Name,

        [switch] $AllowWildcards
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or [System.IO.Path]::IsPathRooted($Path)) {
        throw "$Name '$Path' must be a non-empty relative path."
    }

    if ($Path -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "$Name '$Path' cannot contain a parent-directory segment."
    }

    if ($Path.Contains(':')) {
        throw "$Name '$Path' cannot contain a drive or alternate-stream separator."
    }

    if (-not $AllowWildcards -and $Path.IndexOfAny([char[]]@('*', '?', '[', ']')) -ge 0) {
        throw "$Name '$Path' cannot contain wildcard characters."
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
            continue
        }

        Copy-Item -LiteralPath $item.FullName -Destination $target -Force
    }
}

function Copy-RelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceRoot,

        [Parameter(Mandatory = $true)]
        [string] $DestinationRoot,

        [Parameter(Mandatory = $true)]
        [string] $RelativePath,

        [bool] $Required
    )

    Assert-SafeRelativePath -Path $RelativePath -Name "LLVM copied path"
    $source = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot $RelativePath))
    if (-not (Test-IsSameOrDescendantPath -Path $source -Root $SourceRoot)) {
        throw "LLVM copied path '$RelativePath' escapes '$SourceRoot'."
    }

    if (-not (Test-Path -LiteralPath $source)) {
        if ($Required) {
            throw "Required LLVM path '$RelativePath' was not found in '$SourceRoot'."
        }

        return @()
    }

    $copySource = $source
    $sourceItem = Get-Item -LiteralPath $source -Force
    $sourceLinkTypeProperty = $sourceItem.PSObject.Properties["LinkType"]
    $sourceLinkType = if ($null -eq $sourceLinkTypeProperty) { "" } else { [string] $sourceLinkTypeProperty.Value }
    $sourceHasLinkType = -not [string]::IsNullOrWhiteSpace($sourceLinkType) -and
        -not [string]::Equals($sourceLinkType, "HardLink", [StringComparison]::OrdinalIgnoreCase)
    if ((($sourceItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) -or
        $sourceHasLinkType) {
        $linkTarget = $sourceItem.ResolveLinkTarget($true)
        if ($null -eq $linkTarget) {
            throw "LLVM copied path '$RelativePath' has an unresolved symbolic-link target."
        }

        $copySource = [System.IO.Path]::GetFullPath($linkTarget.FullName)
        if (-not (Test-IsSameOrDescendantPath -Path $copySource -Root $SourceRoot)) {
            throw "LLVM copied path '$RelativePath' resolves outside '$SourceRoot'."
        }
    }

    $destination = [System.IO.Path]::GetFullPath((Join-Path $DestinationRoot $RelativePath))
    if (-not (Test-IsSameOrDescendantPath -Path $destination -Root $DestinationRoot)) {
        throw "LLVM copied path '$RelativePath' escapes '$DestinationRoot'."
    }

    Assert-NoReparsePointPath -Path $destination -Label "LLVM output path"
    if (Test-Path -LiteralPath $copySource -PathType Container) {
        Copy-DirectoryContents -Source $copySource -Destination $destination
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        # PowerShell's Copy-Item can preserve the source link itself on Unix,
        # even after ResolveLinkTarget selected the canonical file. Materialize
        # tool bytes explicitly so the distributable never depends on archive
        # symlink topology and the verified alias pass can create hard links.
        if (Test-PathEntryExists -Path $destination) {
            Remove-Item -LiteralPath $destination -Force
        }
        [System.IO.File]::Copy($copySource, $destination, $false)
    }

    return @($RelativePath.Replace('\', '/'))
}

function Copy-Pattern {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceRoot,

        [Parameter(Mandatory = $true)]
        [string] $DestinationRoot,

        [Parameter(Mandatory = $true)]
        [string] $Pattern,

        [bool] $Required
    )

    Assert-SafeRelativePath -Path $Pattern -Name "LLVM copied pattern" -AllowWildcards
    # GetFullPath can reject wildcard characters on some Windows runtimes, so
    # use a wildcard-free path only for canonical containment validation.
    $containmentProbe = [regex]::Replace($Pattern, '[*?\[\]]', 'x')
    $canonicalPatternPath = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot $containmentProbe))
    if (-not (Test-IsSameOrDescendantPath -Path $canonicalPatternPath -Root $SourceRoot)) {
        throw "LLVM copied pattern '$Pattern' escapes '$SourceRoot'."
    }

    $patternPath = Join-Path $SourceRoot $Pattern
    $matches = @(Get-ChildItem -Path $patternPath -Force -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) {
        if ($Required) {
            throw "Required LLVM path pattern '$Pattern' matched no files in '$SourceRoot'."
        }

        return @()
    }

    $copied = @()
    foreach ($item in ($matches | Sort-Object FullName)) {
        $relativePath = [System.IO.Path]::GetRelativePath($SourceRoot, $item.FullName).Replace('\', '/')
        $copied += Copy-RelativePath `
            -SourceRoot $SourceRoot `
            -DestinationRoot $DestinationRoot `
            -RelativePath $relativePath `
            -Required $true
    }

    return $copied
}

function Convert-ToVerifiedHardLinkAliases {
    param(
        [Parameter(Mandatory = $true)]
        [string] $DestinationRoot,

        [object[]] $Aliases = @()
    )

    if ($Aliases.Count -eq 0) {
        return @()
    }

    if ($IsWindows) {
        throw "LLVM hard-link alias groups are not configured for Windows acquisition."
    }

    $records = @()
    foreach ($alias in $Aliases) {
        $relativePath = [string](Get-JsonProperty -Object $alias -Name "path")
        $targetRelativePath = [string](Get-JsonProperty -Object $alias -Name "target")
        Assert-SafeRelativePath -Path $relativePath -Name "LLVM hard-link alias path"
        Assert-SafeRelativePath -Path $targetRelativePath -Name "LLVM hard-link target path"

        $path = [System.IO.Path]::GetFullPath((Join-Path $DestinationRoot $relativePath))
        $targetPath = [System.IO.Path]::GetFullPath((Join-Path $DestinationRoot $targetRelativePath))
        if (-not (Test-IsSameOrDescendantPath -Path $path -Root $DestinationRoot) -or
            -not (Test-IsSameOrDescendantPath -Path $targetPath -Root $DestinationRoot)) {
            throw "LLVM hard-link alias '$relativePath' escapes '$DestinationRoot'."
        }

        Assert-NoReparsePointPath -Path $path -Label "LLVM hard-link alias path"
        Assert-NoReparsePointPath -Path $targetPath -Label "LLVM hard-link target path"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            -not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
            throw "LLVM hard-link alias '$relativePath' or target '$targetRelativePath' was not copied."
        }

        $pathFile = Get-Item -LiteralPath $path
        $targetFile = Get-Item -LiteralPath $targetPath
        if ($pathFile.Length -ne $targetFile.Length -or
            -not [string]::Equals(
                (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash,
                (Get-FileHash -Algorithm SHA256 -LiteralPath $targetPath).Hash,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "LLVM alias '$relativePath' is not byte-identical to '$targetRelativePath'."
        }

        Remove-Item -LiteralPath $path -Force
        & /bin/ln $targetPath $path
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to hard-link LLVM alias '$relativePath' to '$targetRelativePath'."
        }

        $records += [ordered]@{
            path = $relativePath.Replace('\', '/')
            target = $targetRelativePath.Replace('\', '/')
        }
    }

    return $records
}

function Get-ExtractedRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ExtractDirectory
    )

    $directories = @(Get-ChildItem -LiteralPath $ExtractDirectory -Directory | Sort-Object Name)
    if ($directories.Count -eq 1) {
        return $directories[0].FullName
    }

    return $ExtractDirectory
}

function Get-OutputFileManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $files = @()
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName)) {
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $files += [ordered]@{
            path = $relativePath
            bytes = $file.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }
    }

    return $files
}

function Test-IsDevelopmentOnlyPattern {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Pattern
    )

    $normalized = $Pattern.Replace('\', '/').ToLowerInvariant()
    return $normalized.StartsWith("include/", [StringComparison]::Ordinal) `
        -or $normalized.StartsWith("share/", [StringComparison]::Ordinal) `
        -or $normalized.Contains("/cmake/") `
        -or $normalized.Contains("/pkgconfig/") `
        -or $normalized.EndsWith(".a", [StringComparison]::Ordinal) `
        -or $normalized.EndsWith(".lib", [StringComparison]::Ordinal)
}

function Assert-CompilerPrivateBackendClosure {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [string[]] $RequiredTools = @(),

        [string[]] $RequiredPatternMatches = @(),

        [string[]] $CompilerResourceRoots = @()
    )

    $exactRuntimePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in @($RequiredTools) + @($RequiredPatternMatches)) {
        [void] $exactRuntimePaths.Add(([string] $path).Replace('\', '/'))
    }

    $normalizedResourceRoots = @(Get-SortedUniqueStrings -Values $CompilerResourceRoots)
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName)) {
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($relativePath -eq $ownerMarkerName `
            -or $relativePath.StartsWith("licenses/", [StringComparison]::Ordinal) `
            -or $relativePath.StartsWith("provenance/", [StringComparison]::Ordinal) `
            -or $exactRuntimePaths.Contains($relativePath)) {
            continue
        }

        $isCompilerResource = $false
        foreach ($resourceRoot in $normalizedResourceRoots) {
            if ($relativePath.StartsWith($resourceRoot + "/", [StringComparison]::Ordinal)) {
                $isCompilerResource = $true
                break
            }
        }

        if ($isCompilerResource) {
            continue
        }

        throw "Compiler-private backend closure contains undeclared file '$relativePath'. Full LLVM development trees must not enter a Stark release."
    }
}

function Write-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [object] $Value,

        [int] $Depth = 10
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    [System.IO.File]::WriteAllText(
        $Path,
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

function ConvertTo-LlvmAssetDescriptor {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Asset
    )

    return [ordered]@{
        name = [string] $Asset.name
        url = [string] $Asset.url
        sha256 = [string] $Asset.sha256
        size = [int64] $Asset.size
        signature = [ordered]@{
            name = [string] $Asset.signature.name
            url = [string] $Asset.signature.url
            sha256 = [string] $Asset.signature.sha256
            size = [int64] $Asset.signature.size
        }
        attestation = [ordered]@{
            name = [string] $Asset.attestation.name
            url = [string] $Asset.attestation.url
            sha256 = [string] $Asset.attestation.sha256
            size = [int64] $Asset.attestation.size
        }
    }
}

. (Join-Path $PSScriptRoot "llvm-source-build.ps1")

Assert-PortablePathSegment -Value $AssetSuffix -Name "Asset suffix"

$manifestFullPath = Resolve-RepositoryPath -Path $ManifestPath
$outputRoot = Resolve-ManagedPath -Path $OutputDir -Name "LLVM output directory"
$cacheRoot = Resolve-ManagedPath -Path $CacheDir -Name "LLVM cache directory"

if ((Test-IsSameOrDescendantPath -Path $outputRoot -Root $cacheRoot) -or
    (Test-IsSameOrDescendantPath -Path $cacheRoot -Root $outputRoot)) {
    throw "LLVM output directory '$outputRoot' and cache directory '$cacheRoot' cannot overlap."
}

if (Test-PathEntryExists -Path $outputRoot) {
    Assert-OwnedDirectory -Directory $outputRoot -Kind "stark-llvm-output" -IntendedOutputRoot $outputRoot
}

if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) {
    throw "LLVM asset manifest '$manifestFullPath' does not exist."
}

$manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json
$platforms = Get-JsonProperty -Object $manifest -Name "platforms"
$platformProperty = $platforms.PSObject.Properties |
    Where-Object { [string]::Equals($_.Name, $AssetSuffix, [StringComparison]::Ordinal) } |
    Select-Object -First 1
if ($null -eq $platformProperty) {
    $supportedSuffixes = @($platforms.PSObject.Properties.Name | Sort-Object)
    throw "Unsupported LLVM asset suffix '$AssetSuffix'. Supported suffixes: $($supportedSuffixes -join ', ')."
}

$platform = $platformProperty.Value
$archiveProperty = $platform.PSObject.Properties["archive"]
$sourceBuildProperty = $platform.PSObject.Properties["sourceBuild"]
if (($null -eq $archiveProperty) -eq ($null -eq $sourceBuildProperty)) {
    throw "LLVM platform '$AssetSuffix' must declare exactly one of archive or sourceBuild."
}
$archive = if ($null -eq $archiveProperty) { $null } else { $archiveProperty.Value }
$sourceBuild = if ($null -eq $sourceBuildProperty) { $null } else { $sourceBuildProperty.Value }
$acquisitionKind = if ($null -eq $sourceBuild) { "upstream-archive" } else { "pinned-source-build" }
$llvmVersion = [string] (Get-JsonProperty -Object $manifest -Name "llvmVersion")

$assetDescriptors = @(
    $manifest.sourceArchive,
    $manifest.sourceArchive.signature,
    $manifest.sourceArchive.attestation)
if ($null -ne $archive) {
    $assetDescriptors += @($archive, $archive.signature, $archive.attestation)
}
foreach ($asset in $assetDescriptors) {
    Assert-PortablePathSegment -Value ([string] $asset.name) -Name "LLVM asset filename"
}

Assert-NoReparsePointPath -Path $cacheRoot -Label "LLVM cache directory"
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
Assert-NoReparsePointPath -Path $cacheRoot -Label "LLVM cache directory"

$assetCacheRoot = Get-ContainedChildPath -Root $cacheRoot -Child $AssetSuffix -Name "LLVM platform cache directory"
New-Item -ItemType Directory -Force -Path $assetCacheRoot | Out-Null
Assert-NoReparsePointPath -Path $assetCacheRoot -Label "LLVM platform cache directory"
$archivePath = $null
$sourceArchivePath = $null
if ($null -ne $archive) {
    $archivePath = Save-Asset -Asset $archive -DestinationDirectory $assetCacheRoot
    Save-Asset -Asset $archive.signature -DestinationDirectory $assetCacheRoot | Out-Null
    Save-Asset -Asset $archive.attestation -DestinationDirectory $assetCacheRoot | Out-Null
} else {
    $sourceArchivePath = Save-Asset -Asset $manifest.sourceArchive -DestinationDirectory $assetCacheRoot
}
Save-Asset -Asset $manifest.sourceArchive.signature -DestinationDirectory $assetCacheRoot | Out-Null
Save-Asset -Asset $manifest.sourceArchive.attestation -DestinationDirectory $assetCacheRoot | Out-Null

$operationToken = [Guid]::NewGuid().ToString("N")
$workRoot = Get-ContainedChildPath -Root $assetCacheRoot -Child ("work-" + $operationToken) -Name "LLVM work directory"
if (Test-PathEntryExists -Path $workRoot) {
    throw "Fresh LLVM work directory '$workRoot' unexpectedly already exists."
}

New-Item -ItemType Directory -Path $workRoot | Out-Null
Write-OwnerMarker -Directory $workRoot -Kind "stark-llvm-work" -IntendedOutputRoot $outputRoot -Token $operationToken
$extractRoot = Join-Path $workRoot "extract"
New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null

$outputParent = Split-Path -Parent $outputRoot
Assert-NoReparsePointPath -Path $outputParent -Label "LLVM output parent"
New-Item -ItemType Directory -Force -Path $outputParent | Out-Null
Assert-NoReparsePointPath -Path $outputParent -Label "LLVM output parent"
$stageRoot = Get-ContainedChildPath `
    -Root $outputParent `
    -Child (".stark-llvm-stage-" + $operationToken) `
    -Name "LLVM output staging directory"
if (Test-PathEntryExists -Path $stageRoot) {
    throw "Fresh LLVM output staging directory '$stageRoot' unexpectedly already exists."
}

New-Item -ItemType Directory -Path $stageRoot | Out-Null
Write-OwnerMarker -Directory $stageRoot -Kind "stark-llvm-output" -IntendedOutputRoot $outputRoot -Token $operationToken

try {
    $extractionInput = if ($null -eq $sourceArchivePath) { $archivePath } else { $sourceArchivePath }
    tar -xf $extractionInput -C $extractRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to extract '$extractionInput'."
    }

    $extractedRoot = Get-ExtractedRoot -ExtractDirectory $extractRoot
    $sourceBuildEvidence = $null
    if ($null -eq $sourceBuild) {
        $payloadRoot = $extractedRoot
    } else {
        $sourceBuildResult = Invoke-LlvmPinnedSourceBuild `
            -SourceBuild $sourceBuild `
            -ExtractedSourceRoot $extractedRoot `
            -WorkRoot $workRoot `
            -CMakePath $CMakePath `
            -NinjaPath $NinjaPath
        $payloadRoot = [string]$sourceBuildResult.PayloadRoot
        $sourceBuildEvidence = $sourceBuildResult.Evidence
    }

    Assert-NoReparsePointPath -Path $stageRoot -Label "LLVM output staging directory"

    # The upstream archive is only an acquisition input. Do not copy its broad
    # bin/lib/include/share roots: Stark publishes a compiler-private runtime,
    # not an LLVM development SDK. Clang's versioned resource directory is the
    # sole recursive runtime root needed by Stage0's textual-LLVM/C compilation
    # path unless a future per-platform manifest explicitly narrows it further.
    $compilerResourceProperty = $platform.PSObject.Properties["compilerResourceRoots"]
    $compilerResourceRootValues = if ($null -eq $compilerResourceProperty) {
        @("lib/clang")
    } else {
        Get-ArrayValues -Value $compilerResourceProperty.Value
    }
    $compilerResourceRoots = @()
    foreach ($root in $compilerResourceRootValues) {
        $compilerResourceRoots += Copy-RelativePath `
            -SourceRoot $payloadRoot `
            -DestinationRoot $stageRoot `
            -RelativePath ([string] $root) `
            -Required $true
    }
    $compilerResourceRoots = @(Get-SortedUniqueStrings -Values $compilerResourceRoots)

    $requiredTools = @()
    foreach ($tool in (Get-ArrayValues -Value $platform.requiredTools)) {
        $requiredTools += Copy-RelativePath -SourceRoot $payloadRoot -DestinationRoot $stageRoot -RelativePath ([string] $tool) -Required $true
    }
    $requiredTools = @(Get-SortedUniqueStrings -Values $requiredTools)

    $requiredPatternMatches = @()
    $excludedDevelopmentPatterns = @()
    foreach ($pattern in (Get-ArrayValues -Value $platform.requiredPatterns)) {
        $patternText = [string] $pattern
        if (Test-IsDevelopmentOnlyPattern -Pattern $patternText) {
            $excludedDevelopmentPatterns += $patternText
            continue
        }

        $requiredPatternMatches += Copy-Pattern `
            -SourceRoot $payloadRoot `
            -DestinationRoot $stageRoot `
            -Pattern $patternText `
            -Required $true
    }
    $requiredPatternMatches = @(Get-SortedUniqueStrings -Values $requiredPatternMatches)
    $excludedDevelopmentPatterns = @(Get-SortedUniqueStrings -Values $excludedDevelopmentPatterns)

    $hardlinkProperty = $platform.PSObject.Properties["hardlinkAliases"]
    $hardlinkAliases = Convert-ToVerifiedHardLinkAliases `
        -DestinationRoot $stageRoot `
        -Aliases (Get-ArrayValues -Value $(if ($null -eq $hardlinkProperty) { $null } else { $hardlinkProperty.Value }))

    $provenanceRoot = Join-Path $stageRoot "provenance"
    New-Item -ItemType Directory -Force -Path $provenanceRoot | Out-Null
    if ($null -ne $archive) {
        Copy-Item -LiteralPath (Join-Path $assetCacheRoot $archive.signature.name) -Destination (Join-Path $provenanceRoot $archive.signature.name) -Force
        Copy-Item -LiteralPath (Join-Path $assetCacheRoot $archive.attestation.name) -Destination (Join-Path $provenanceRoot $archive.attestation.name) -Force
    }
    Copy-Item -LiteralPath (Join-Path $assetCacheRoot $manifest.sourceArchive.signature.name) -Destination (Join-Path $provenanceRoot $manifest.sourceArchive.signature.name) -Force
    Copy-Item -LiteralPath (Join-Path $assetCacheRoot $manifest.sourceArchive.attestation.name) -Destination (Join-Path $provenanceRoot $manifest.sourceArchive.attestation.name) -Force

    $licenseRoot = Join-Path $stageRoot "licenses"
    New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null
    $licenseFiles = @()
    $licenseSourceRoots = @([pscustomobject]@{ Root = $payloadRoot; Prefix = "" })
    if ($null -ne $sourceBuild) {
        $licenseSourceRoots += [pscustomobject]@{ Root = $extractedRoot; Prefix = "source" }
    }
    foreach ($pattern in (Get-ArrayValues -Value $manifest.licenseFilePatterns)) {
        foreach ($licenseSource in $licenseSourceRoots) {
            foreach ($file in (Get-ChildItem -LiteralPath $licenseSource.Root -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like ([string] $pattern) } | Sort-Object FullName)) {
                $sourceRelativePath = [System.IO.Path]::GetRelativePath($licenseSource.Root, $file.FullName).Replace('\', '/')
                $relativePath = if ([string]::IsNullOrWhiteSpace($licenseSource.Prefix)) {
                    $sourceRelativePath
                } else {
                    "$($licenseSource.Prefix)/$sourceRelativePath"
                }
                $destination = Join-Path $licenseRoot $relativePath
                if (-not (Test-IsSameOrDescendantPath -Path $destination -Root $licenseRoot)) {
                    throw "LLVM license file '$($file.FullName)' escapes '$licenseRoot'."
                }

                Assert-NoReparsePointPath -Path $destination -Label "LLVM license output path"
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
                $licenseFiles += $relativePath
            }
        }
    }

    Assert-OwnedDirectory -Directory $stageRoot -Kind "stark-llvm-output" -IntendedOutputRoot $outputRoot -Token $operationToken
    Write-PortableOutputOwnerMarker -Directory $stageRoot

    $licenseFiles = @(Get-SortedUniqueStrings -Values $licenseFiles)
    Assert-CompilerPrivateBackendClosure `
        -Root $stageRoot `
        -RequiredTools $requiredTools `
        -RequiredPatternMatches $requiredPatternMatches `
        -CompilerResourceRoots $compilerResourceRoots

    $runtimeClosureFiles = @(Get-OutputFileManifest -Root $stageRoot)
    [int64] $runtimeClosureBytes = 0
    foreach ($entry in $runtimeClosureFiles) {
        $runtimeClosureBytes += [int64] $entry.bytes
    }

    $toolchainManifest = [ordered]@{
        schemaVersion = 2
        payloadKind = "stark-compiler-private-backend"
        llvmVersion = $llvmVersion
        releaseTag = [string] $manifest.releaseTag
        releaseUrl = [string] $manifest.releaseUrl
        assetSuffix = $AssetSuffix
        runtimeIdentifier = [string] $platform.runtimeIdentifier
        acquisitionKind = $acquisitionKind
        binaryArchive = if ($null -eq $archive) { $null } else { ConvertTo-LlvmAssetDescriptor -Asset $archive }
        sourceArchive = ConvertTo-LlvmAssetDescriptor -Asset $manifest.sourceArchive
        sourceBuild = $sourceBuildEvidence
        compilerResourceRoots = $compilerResourceRoots
        requiredTools = $requiredTools
        requiredPatternMatches = $requiredPatternMatches
        excludedDevelopmentPatterns = $excludedDevelopmentPatterns
        hardlinkAliases = $hardlinkAliases
        licenseFiles = $licenseFiles
        runtimeClosure = [ordered]@{
            fileCount = $runtimeClosureFiles.Count
            logicalBytes = $runtimeClosureBytes
            files = $runtimeClosureFiles
        }
    }

    Write-DeterministicJson `
        -Path (Join-Path $stageRoot "manifest.json") `
        -Value $toolchainManifest `
        -Depth 12

    Assert-OwnedDirectory -Directory $stageRoot -Kind "stark-llvm-output" -IntendedOutputRoot $outputRoot
    Assert-NoReparsePointPath -Path $outputRoot -Label "LLVM output directory"
    if (Test-PathEntryExists -Path $outputRoot) {
        Remove-OwnedDirectory -Directory $outputRoot -Kind "stark-llvm-output" -IntendedOutputRoot $outputRoot
    }

    Assert-NoReparsePointPath -Path $outputRoot -Label "LLVM output directory"
    Move-Item -LiteralPath $stageRoot -Destination $outputRoot
    Assert-OwnedDirectory -Directory $outputRoot -Kind "stark-llvm-output" -IntendedOutputRoot $outputRoot
} finally {
    if (Test-PathEntryExists -Path $stageRoot) {
        Remove-OwnedDirectory -Directory $stageRoot -Kind "stark-llvm-output" -IntendedOutputRoot $outputRoot
    }

    if (Test-PathEntryExists -Path $workRoot) {
        Remove-OwnedDirectory -Directory $workRoot -Kind "stark-llvm-work" -IntendedOutputRoot $outputRoot -Token $operationToken
    }
}

Write-Host "Prepared LLVM $llvmVersion toolchain for $AssetSuffix at $outputRoot"
