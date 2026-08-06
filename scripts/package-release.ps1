param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $AssetSuffix,

    [Parameter(Mandatory = $true)]
    [string] $PublishDir,

    [Parameter(Mandatory = $true)]
    [string] $StdlibPackageDir,

    [Parameter(Mandatory = $true)]
    [string] $ManagedLicenseDir,

    [string] $VendorRoot = "vendor",

    [string] $ToolchainDir = "",

    [string] $ReleaseToolsPath = "",

    [string] $DotNetPath = "dotnet",

    [string] $RuntimeIdentifier = "",

    [string] $TargetTriple = "",

    [string] $CommitSha = "",

    [ValidateSet("Release")]
    [string] $BuildConfiguration = "Release",

    [string] $BuildConfigurationSha256 = "",

    [string] $BuildPlanSha256 = "",

    [string] $OutputDir = "artifacts/release",

    [ValidateSet("zip", "targz")]
    [string] $ArchiveKind = "zip",

    [string] $LlvmVersion = "22.1.8",

    [switch] $AllowPlannedTarget
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

function Assert-SafeBackendRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or [System.IO.Path]::IsPathRooted($Path) `
        -or $Path.Contains('\') `
        -or $Path.Contains(':') `
        -or $Path -match '(^|/)\.\.(/|$)' `
        -or $Path -match '(^|/)\.(/|$)') {
        throw "Compiler-private backend manifest path '$Path' is not a safe canonical relative path."
    }
}

function Get-RequiredJsonPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "Compiler-private backend manifest is missing required property '$Name'."
    }

    return $property.Value
}

function Get-BackendManifestArray {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Manifest,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $value = Get-RequiredJsonPropertyValue -Object $Manifest -Name $Name
    if ($null -eq $value) {
        return @()
    }

    return @($value)
}

function Assert-CompilerPrivateBackendEntryClass {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]] $RequiredTools,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]] $RuntimeLibraries,

        [string[]] $CompilerResourceRoots = @()
    )

    if ($RelativePath.StartsWith("include/", [StringComparison]::Ordinal) `
        -or $RelativePath.StartsWith("share/", [StringComparison]::Ordinal) `
        -or $RelativePath.StartsWith("lib/cmake/", [StringComparison]::Ordinal) `
        -or $RelativePath.StartsWith("lib/pkgconfig/", [StringComparison]::Ordinal)) {
        throw "Compiler-private backend manifest includes LLVM development-tree file '$RelativePath'."
    }

    if ($RelativePath -eq ".stark-llvm-toolchain-owner.json" `
        -or $RelativePath.StartsWith("licenses/", [StringComparison]::Ordinal) `
        -or $RelativePath.StartsWith("provenance/", [StringComparison]::Ordinal)) {
        return
    }

    if ($RequiredTools.Contains($RelativePath)) {
        return
    }

    if ($RuntimeLibraries.Contains($RelativePath)) {
        $lower = $RelativePath.ToLowerInvariant()
        $isRuntimeLibrary = ($lower.StartsWith("bin/", [StringComparison]::Ordinal) -and $lower.EndsWith(".dll", [StringComparison]::Ordinal)) `
            -or ($lower.StartsWith("lib/", [StringComparison]::Ordinal) -and ($lower.Contains(".so") -or $lower.EndsWith(".dylib", [StringComparison]::Ordinal)))
        if (-not $isRuntimeLibrary) {
            throw "Compiler-private backend runtime entry '$RelativePath' is not a shared runtime library. Static/import development libraries are forbidden."
        }

        return
    }

    foreach ($resourceRoot in $CompilerResourceRoots) {
        if (-not [string]::Equals($resourceRoot, "lib/clang", [StringComparison]::Ordinal)) {
            throw "Unsupported compiler-resource root '$resourceRoot'; Stage0 permits only the versioned lib/clang runtime tree."
        }

        if ($RelativePath.StartsWith($resourceRoot + "/", [StringComparison]::Ordinal)) {
            return
        }
    }

    throw "Compiler-private backend manifest contains undeclared file '$RelativePath'. Full LLVM development trees must not enter a Stark release."
}

function Assert-CompilerPrivateBackendManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $manifestPath = Join-Path $Root "manifest.json"
    Assert-NoReparsePointPath -Path $manifestPath
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Compiler-private backend '$Root' is missing manifest.json. Whole-tree staging is forbidden."
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    } catch {
        throw "Compiler-private backend manifest '$manifestPath' is invalid JSON: $($_.Exception.Message)"
    }

    if ([int] (Get-RequiredJsonPropertyValue -Object $manifest -Name "schemaVersion") -ne 2 `
        -or -not [string]::Equals(
            [string] (Get-RequiredJsonPropertyValue -Object $manifest -Name "payloadKind"),
            "stark-compiler-private-backend",
            [StringComparison]::Ordinal)) {
        throw "Compiler-private backend manifest '$manifestPath' is not the required schema-2 Stark private-backend manifest."
    }

    $manifestLlvmVersion = [string] (Get-RequiredJsonPropertyValue -Object $manifest -Name "llvmVersion")
    if (-not [string]::Equals($manifestLlvmVersion, $LlvmVersion, [StringComparison]::Ordinal)) {
        throw "Compiler-private backend LLVM version '$manifestLlvmVersion' does not match requested '$LlvmVersion'."
    }

    $manifestAssetSuffix = [string] (Get-RequiredJsonPropertyValue -Object $manifest -Name "assetSuffix")
    if (-not [string]::Equals($manifestAssetSuffix, $AssetSuffix, [StringComparison]::Ordinal)) {
        throw "Compiler-private backend asset '$manifestAssetSuffix' does not match release asset '$AssetSuffix'."
    }

    $acquisitionKind = [string](Get-RequiredJsonPropertyValue -Object $manifest -Name "acquisitionKind")
    $sourceArchive = Get-RequiredJsonPropertyValue -Object $manifest -Name "sourceArchive"
    $sourceArchiveSha256 = [string](Get-RequiredJsonPropertyValue -Object $sourceArchive -Name "sha256")
    if ($sourceArchiveSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Compiler-private backend source archive has an invalid SHA-256."
    }
    $binaryArchive = Get-OptionalJsonPropertyValue -Object $manifest -Name "binaryArchive"
    $sourceBuild = Get-OptionalJsonPropertyValue -Object $manifest -Name "sourceBuild"
    if ($acquisitionKind -ceq "upstream-archive") {
        if ($null -eq $binaryArchive -or $null -ne $sourceBuild -or
            [string](Get-RequiredJsonPropertyValue -Object $binaryArchive -Name "sha256") -cnotmatch '^[0-9a-f]{64}$') {
            throw "Compiler-private upstream backend acquisition metadata is incomplete or inconsistent."
        }
    } elseif ($acquisitionKind -ceq "pinned-source-build") {
        if ($manifestAssetSuffix -cne "macos-x64" -or $null -ne $binaryArchive -or $null -eq $sourceBuild -or
            [string](Get-RequiredJsonPropertyValue -Object $sourceBuild -Name "recipeKind") -cne "pinned-source-build" -or
            [string](Get-RequiredJsonPropertyValue -Object $sourceBuild -Name "configuration") -cne "Release" -or
            [string](Get-RequiredJsonPropertyValue -Object $sourceBuild -Name "optimization") -cne "O3" -or
            [string](Get-RequiredJsonPropertyValue -Object $sourceBuild -Name "lto") -cne "Thin") {
            throw "Compiler-private pinned source-build provenance is incomplete, unoptimized, or inconsistent."
        }
        $appleToolchain = Get-RequiredJsonPropertyValue -Object $sourceBuild -Name "appleToolchain"
        foreach ($name in @("clangSha256", "clangxxSha256")) {
            if ([string](Get-RequiredJsonPropertyValue -Object $appleToolchain -Name $name) -cnotmatch '^[0-9a-f]{64}$') {
                throw "Compiler-private pinned source-build Apple toolchain '$name' is invalid."
            }
        }
        foreach ($name in @("xcodeVersion", "sdkVersion", "clangVersion")) {
            if ([string]::IsNullOrWhiteSpace([string](Get-RequiredJsonPropertyValue -Object $appleToolchain -Name $name))) {
                throw "Compiler-private pinned source-build Apple toolchain '$name' is empty."
            }
        }
    } else {
        throw "Compiler-private backend acquisition kind '$acquisitionKind' is unsupported."
    }

    $allowedToolNames = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]@(
            "clang", "clang++", "ld.lld", "ld64.lld", "lld", "llvm-ar", "llvm-ranlib",
            "clang.exe", "clang++.exe", "lld-link.exe", "lld.exe", "llvm-ar.exe", "llvm-lib.exe", "llvm-ranlib.exe"),
        [StringComparer]::Ordinal)
    $requiredTools = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($toolValue in (Get-BackendManifestArray -Manifest $manifest -Name "requiredTools")) {
        $tool = [string] $toolValue
        Assert-SafeBackendRelativePath -Path $tool
        if (-not $tool.StartsWith("bin/", [StringComparison]::Ordinal) `
            -or -not $allowedToolNames.Contains([System.IO.Path]::GetFileName($tool))) {
            throw "Compiler-private backend tool '$tool' is not in the Stage0 executable allowlist."
        }

        [void] $requiredTools.Add($tool)
    }

    if (-not ($requiredTools.Contains("bin/clang") -or $requiredTools.Contains("bin/clang.exe"))) {
        throw "Compiler-private backend manifest does not include the Stage0 Clang executable."
    }

    $runtimeLibraries = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($runtimeValue in (Get-BackendManifestArray -Manifest $manifest -Name "requiredPatternMatches")) {
        $runtimePath = [string] $runtimeValue
        Assert-SafeBackendRelativePath -Path $runtimePath
        [void] $runtimeLibraries.Add($runtimePath)
    }

    $compilerResourceRoots = @()
    foreach ($resourceValue in (Get-BackendManifestArray -Manifest $manifest -Name "compilerResourceRoots")) {
        $resourceRoot = [string] $resourceValue
        Assert-SafeBackendRelativePath -Path $resourceRoot
        $compilerResourceRoots += $resourceRoot
    }

    $runtimeClosure = Get-RequiredJsonPropertyValue -Object $manifest -Name "runtimeClosure"
    $closureFiles = @(Get-RequiredJsonPropertyValue -Object $runtimeClosure -Name "files")
    $expectedFileCount = [int64] (Get-RequiredJsonPropertyValue -Object $runtimeClosure -Name "fileCount")
    $expectedLogicalBytes = [int64] (Get-RequiredJsonPropertyValue -Object $runtimeClosure -Name "logicalBytes")
    if ($closureFiles.Count -ne $expectedFileCount) {
        throw "Compiler-private backend closure count is $($closureFiles.Count), expected $expectedFileCount."
    }

    $caseSensitivePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $portablePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [int64] $actualLogicalBytes = 0
    foreach ($entry in ($closureFiles | Sort-Object { [string] $_.path })) {
        $relativePath = [string] (Get-RequiredJsonPropertyValue -Object $entry -Name "path")
        Assert-SafeBackendRelativePath -Path $relativePath
        if ([string]::Equals($relativePath, "manifest.json", [StringComparison]::Ordinal)) {
            throw "Compiler-private backend manifest cannot recursively list itself in runtimeClosure.files."
        }

        if (-not $caseSensitivePaths.Add($relativePath) -or -not $portablePaths.Add($relativePath)) {
            throw "Compiler-private backend manifest contains duplicate or case-colliding path '$relativePath'."
        }

        Assert-CompilerPrivateBackendEntryClass `
            -RelativePath $relativePath `
            -RequiredTools $requiredTools `
            -RuntimeLibraries $runtimeLibraries `
            -CompilerResourceRoots $compilerResourceRoots

        $expectedBytes = [int64] (Get-RequiredJsonPropertyValue -Object $entry -Name "bytes")
        $expectedSha256 = [string] (Get-RequiredJsonPropertyValue -Object $entry -Name "sha256")
        if ($expectedBytes -lt 0 -or $expectedSha256 -notmatch '^[0-9a-f]{64}$') {
            throw "Compiler-private backend closure entry '$relativePath' has invalid size or SHA-256 metadata."
        }

        $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $Root $relativePath))
        if (-not (Test-IsSameOrDescendantPath -Path $sourcePath -Root $Root)) {
            throw "Compiler-private backend closure entry '$relativePath' escapes '$Root'."
        }

        Assert-NoReparsePointPath -Path $sourcePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Compiler-private backend closure entry '$relativePath' is missing."
        }

        $sourceFile = Get-Item -LiteralPath $sourcePath -Force
        if ($sourceFile.Length -ne $expectedBytes) {
            throw "Compiler-private backend closure entry '$relativePath' has size $($sourceFile.Length), expected $expectedBytes."
        }

        $actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash.ToLowerInvariant()
        if (-not [string]::Equals($actualSha256, $expectedSha256, [StringComparison]::Ordinal)) {
            throw "Compiler-private backend closure entry '$relativePath' failed SHA-256 validation."
        }

        $actualLogicalBytes += $sourceFile.Length
    }

    if ($actualLogicalBytes -ne $expectedLogicalBytes) {
        throw "Compiler-private backend closure contains $actualLogicalBytes logical bytes, expected $expectedLogicalBytes."
    }

    if (-not $caseSensitivePaths.Contains(".stark-llvm-toolchain-owner.json")) {
        throw "Compiler-private backend closure is missing its portable ownership marker."
    }

    foreach ($requiredTool in $requiredTools) {
        if (-not $caseSensitivePaths.Contains($requiredTool)) {
            throw "Compiler-private backend declares required tool '$requiredTool' but omits it from the hashed runtime closure."
        }
    }

    foreach ($runtimeLibrary in $runtimeLibraries) {
        if (-not $caseSensitivePaths.Contains($runtimeLibrary)) {
            throw "Compiler-private backend declares runtime library '$runtimeLibrary' but omits it from the hashed runtime closure."
        }
    }

    foreach ($resourceRoot in $compilerResourceRoots) {
        $resourcePrefix = $resourceRoot + "/"
        $hasResourceFile = $false
        foreach ($closurePath in $caseSensitivePaths) {
            if ($closurePath.StartsWith($resourcePrefix, [StringComparison]::Ordinal)) {
                $hasResourceFile = $true
                break
            }
        }

        if (-not $hasResourceFile) {
            throw "Compiler-private backend declares compiler-resource root '$resourceRoot' but its hashed runtime closure is empty."
        }
    }

    foreach ($actualFile in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName)) {
        $actualRelativePath = [System.IO.Path]::GetRelativePath($Root, $actualFile.FullName).Replace('\', '/')
        if ($actualRelativePath -eq "manifest.json") {
            continue
        }

        if (-not $caseSensitivePaths.Contains($actualRelativePath)) {
            throw "Compiler-private backend contains untracked file '$actualRelativePath'. Whole-tree staging is forbidden."
        }
    }

    return $manifest
}

function Copy-CompilerPrivateBackendFromManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination
    )

    $manifest = Assert-CompilerPrivateBackendManifest -Root $Source
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $runtimeClosure = Get-RequiredJsonPropertyValue -Object $manifest -Name "runtimeClosure"
    $closureFiles = @(Get-RequiredJsonPropertyValue -Object $runtimeClosure -Name "files")
    foreach ($entry in ($closureFiles | Sort-Object { [string] $_.path })) {
        $relativePath = [string] $entry.path
        $sourcePath = Join-Path $Source $relativePath
        $destinationPath = Join-Path $Destination $relativePath
        Assert-NoReparsePointPath -Path $sourcePath
        Assert-NoReparsePointPath -Path $destinationPath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPath) | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    }

    Copy-Item -LiteralPath (Join-Path $Source "manifest.json") -Destination (Join-Path $Destination "manifest.json") -Force
    Assert-CompilerPrivateBackendManifest -Root $Destination | Out-Null
    return $manifest
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
    $explicitCommit = if ([string]::IsNullOrWhiteSpace($CommitSha)) { $null } else { $CommitSha.Trim().ToLowerInvariant() }
    $githubCommit = if ([string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) { $null } else { $env:GITHUB_SHA.Trim().ToLowerInvariant() }
    if ($null -ne $explicitCommit -and $null -ne $githubCommit -and
        -not [string]::Equals($explicitCommit, $githubCommit, [StringComparison]::Ordinal)) {
        throw "Requested source commit '$explicitCommit' does not match GitHub source commit '$githubCommit'."
    }

    $gitCommit = $null
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $gitOutput = (& git -C $repositoryRoot rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitOutput)) {
            $gitCommit = ([string]$gitOutput).Trim().ToLowerInvariant()
        }
    }
    $commit = if ($null -ne $explicitCommit) {
        $explicitCommit
    } elseif ($null -ne $githubCommit) {
        $githubCommit
    } else {
        $gitCommit
    }
    if ([string]::IsNullOrWhiteSpace($commit) -or $commit -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
        throw "Release packaging requires an exact 40- or 64-digit source commit; none was available."
    }
    if ($null -ne $gitCommit -and -not [string]::Equals($commit, $gitCommit, [StringComparison]::Ordinal)) {
        throw "Release source commit '$commit' does not match checked-out HEAD '$gitCommit'."
    }
    return $commit
}

function Get-SourceIdentity {
    param([Parameter(Mandatory = $true)][string] $Commit)

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "Release packaging requires Git to prove that commit '$Commit' exactly identifies the source tree."
    }
    $statusOutput = @(& git -C $repositoryRoot status --porcelain --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Release packaging could not inspect the tracked source tree for commit '$Commit'."
    }
    $trackedWorkingTreeDirty = @($statusOutput | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -ne 0
    if ($trackedWorkingTreeDirty) {
        throw "Release packaging requires a clean tracked source tree so commit '$Commit' exactly identifies archived source content."
    }
    # Archive metadata must remain a pure function of immutable release inputs.
    # Trigger refs, repository environment variables, and the route by which the
    # commit was selected belong in external workflow/publication evidence. If
    # recorded here, a reviewed candidate and its final-tag rebuild could differ
    # despite using the same commit.
    return [ordered]@{
        commit = $Commit
        commitHashAlgorithm = if ($Commit.Length -eq 40) { "sha1" } else { "sha256" }
        trackedWorkingTreeDirty = $trackedWorkingTreeDirty
    }
}

function Get-OptionalJsonPropertyValue {
    param(
        [object] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
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

function Get-Utf8Sha256 {
    param([Parameter(Mandatory = $true)][string] $Value)

    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant()
}

function Get-ObjectSha256 {
    param([Parameter(Mandatory = $true)][object] $Value)

    $json = ($Value | ConvertTo-Json -Depth 50 -Compress).Replace("`r`n", "`n")
    return Get-Utf8Sha256 -Value $json
}

function Write-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object] $Value,
        [int] $Depth = 60
    )

    $json = ($Value | ConvertTo-Json -Depth $Depth).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText(
        $Path,
        $json + "`n",
        [System.Text.UTF8Encoding]::new($false))
}

function Get-ContentIdentity {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $StageRoot,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $files = @(Get-FileManifest -Root $Root)
    $lines = [System.Collections.Generic.List[string]]::new()
    [int64]$logicalBytes = 0
    foreach ($file in $files) {
        $relativePath = [string]$file.path
        $bytes = [int64]$file.bytes
        $sha256 = [string]$file.sha256
        $logicalBytes += $bytes
        $lines.Add("$relativePath`0$bytes`0$sha256")
    }
    $lines.Sort([StringComparer]::Ordinal)
    $manifestText = if ($lines.Count -eq 0) { "" } else { ($lines.ToArray() -join "`n") + "`n" }
    return [ordered]@{
        label = $Label
        root = [System.IO.Path]::GetRelativePath($StageRoot, $Root).Replace('\', '/')
        fileCount = $files.Count
        logicalBytes = $logicalBytes
        manifestSha256 = Get-Utf8Sha256 -Value $manifestText
    }
}

function Get-FileDescriptor {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release metadata input '$Path' does not exist."
    }
    return [ordered]@{
        path = $RelativePath
        bytes = [int64](Get-Item -LiteralPath $Path).Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    }
}

function Get-JsonFileDescriptor {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )

    $descriptor = Get-FileDescriptor -Path $Path -RelativePath $RelativePath
    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $schemaVersion = Get-OptionalJsonPropertyValue -Object $document -Name "schemaVersion"
    return [ordered]@{
        path = $descriptor.path
        bytes = $descriptor.bytes
        sha256 = $descriptor.sha256
        schemaVersion = if ($null -eq $schemaVersion) { $null } else { [int64]$schemaVersion }
    }
}

function Get-PackageImageFormatVersion {
    param([Parameter(Mandatory = $true)][string] $Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $header = [byte[]]::new(12)
        if ($stream.Read($header, 0, $header.Length) -ne $header.Length -or
            [System.Text.Encoding]::ASCII.GetString($header, 0, 8) -cne "STARKPKG") {
            throw "Package image '$Path' does not have the STARKPKG binary header."
        }
        return [uint32]($header[8] -bor ($header[9] -shl 8) -bor ($header[10] -shl 16) -bor ($header[11] -shl 24))
    } finally {
        $stream.Dispose()
    }
}

function Get-BuildInvocationIdentity {
    param(
        [Parameter(Mandatory = $true)][string] $Commit,
        [Parameter(Mandatory = $true)][object] $ArchiveTool
    )

    $explicitConfigurationSha256 = if ([string]::IsNullOrWhiteSpace($BuildConfigurationSha256)) {
        $null
    } else {
        $BuildConfigurationSha256.Trim().ToLowerInvariant()
    }
    if ($null -ne $explicitConfigurationSha256 -and $explicitConfigurationSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Build configuration SHA-256 '$explicitConfigurationSha256' is invalid."
    }
    $explicitBuildPlanSha256 = if ([string]::IsNullOrWhiteSpace($BuildPlanSha256)) {
        $null
    } else {
        $BuildPlanSha256.Trim().ToLowerInvariant()
    }
    if ($null -ne $explicitBuildPlanSha256 -and $explicitBuildPlanSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Build plan SHA-256 '$explicitBuildPlanSha256' is invalid."
    }

    $identityFacts = [ordered]@{
        schemaVersion = 1
        commit = $Commit
        releaseVersion = $Version
        targetId = $AssetSuffix
        configurationSha256 = $explicitConfigurationSha256
        releasePlanSha256 = $explicitBuildPlanSha256
        archiveTool = $ArchiveTool
    }
    $identityText = @(
        "stark-content-addressed-release-build-v2",
        "commit=$Commit",
        "releaseVersion=$Version",
        "targetId=$AssetSuffix",
        "configurationSha256=$explicitConfigurationSha256",
        "releasePlanSha256=$explicitBuildPlanSha256",
        "releaseToolManifestSha256=$([string]$ArchiveTool.manifest.sha256)",
        "releaseToolImplementation=$([string]$ArchiveTool.implementation)",
        "releaseToolTargetFramework=$([string]$ArchiveTool.targetFramework)",
        "dotnetSdkVersion=$([string]$ArchiveTool.dotnetSdkVersion)",
        "dotnetRuntimeVersion=$([string]$ArchiveTool.dotnetRuntimeVersion)",
        "releaseToolAssemblySha256=$([string]$ArchiveTool.assembly.sha256)"
    ) -join "`n"
    $identitySha256 = Get-Utf8Sha256 -Value ($identityText + "`n")
    return [ordered]@{
        kind = "content-addressed-release-build"
        identity = "sha256:$identitySha256"
        identityFacts = $identityFacts
        configurationSha256 = $explicitConfigurationSha256
        releasePlanSha256 = $explicitBuildPlanSha256
    }
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
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName)) {
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $entries += [ordered]@{
            path = $relativePath
            bytes = $file.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }
    }

    return $entries
}

function Write-ReleaseFileChecksums {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $outputFullPath = [System.IO.Path]::GetFullPath($Path)
    $pathComparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    $lines = @()
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName)) {
        if ([string]::Equals(
            [System.IO.Path]::GetFullPath($file.FullName),
            $outputFullPath,
            $pathComparison)) {
            continue
        }

        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        $lines += "$hash  $relativePath"
    }

    [System.IO.File]::WriteAllLines(
        $outputFullPath,
        [string[]]$lines,
        [System.Text.Encoding]::ASCII)
}

function Write-ReleaseText {
    param(
        [string] $Path,
        [string] $Commit,
        [string] $ToolchainRelativePath,
        [object] $BuildInvocationIdentity,
        [object] $SchemaVersions,
        [string] $ConfigurationSha256
    )

    $runtimeText = if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) { "<unspecified>" } else { $RuntimeIdentifier }
    $targetText = if ([string]::IsNullOrWhiteSpace($TargetTriple)) { "<compiler default>" } else { $TargetTriple }
    $toolchainText = if ([string]::IsNullOrWhiteSpace($ToolchainRelativePath)) { "not bundled by this packaging invocation" } else { $ToolchainRelativePath }
    $buildIdentityText = if ([string]::IsNullOrWhiteSpace([string]$BuildInvocationIdentity.identity)) { "<not supplied>" } else { [string]$BuildInvocationIdentity.identity }

    $releaseText = @"
Stark $Version

Commit: $Commit
Asset: $AssetSuffix
Runtime ID: $runtimeText
Default target triple: $targetText
LLVM version: $LlvmVersion
Compiler-private backend: $toolchainText
Build kind: $($BuildInvocationIdentity.kind)
Build identity: $buildIdentityText
Release configuration SHA-256: $ConfigurationSha256
Release/SDK/package schemas: $($SchemaVersions.releaseMetadata) / $($SchemaVersions.sdkManifest) / $($SchemaVersions.packageImageFormat)

Included roots:
- bin/stark[.exe] command and compiler runtime support files
- sdk.json runtime SDK manifest
- stdlib/
- vendor/
- compiler-private backend closure (at the path above)
- licenses/
- docs/
- examples/
- optional archive-local installer and uninstaller

Run stark doctor --strict after installation to inspect compiler, runtime, target,
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
        [object] $CompilerPrivateBackendMetadata,
        [object] $SourceIdentity,
        [object] $BuildInvocationIdentity,
        [object] $BuildOptions,
        [object] $SchemaVersions,
        [object] $TargetFacts,
        [object] $ConfigurationMetadata,
        [object] $SdkMetadata,
        [object[]] $Packages,
        [object[]] $PackageSchemaFacts,
        [object] $DependenciesMetadata,
        [object] $VendorCatalogMetadata,
        [object] $ContentIdentities,
        [object[]] $Files,
        [object[]] $StdlibArtifacts,
        [object[]] $VendorArtifacts
    )

    $runtimeText = if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) { $null } else { $RuntimeIdentifier }
    $targetText = if ([string]::IsNullOrWhiteSpace($TargetTriple)) { $null } else { $TargetTriple }
    $toolchainPath = if ([string]::IsNullOrWhiteSpace($ToolchainRelativePath)) { $null } else { $ToolchainRelativePath }
    $releaseTargetMetadata = $TargetFacts.release

    $releaseJson = [ordered]@{
        schemaVersion = 2
        releaseVersion = $Version
        starkVersion = $Version
        compilerVersion = $Version
        gitCommit = $Commit
        source = $SourceIdentity
        workflowIdentity = $BuildInvocationIdentity
        buildIdentity = $BuildInvocationIdentity
        buildOptions = $BuildOptions
        configuration = $ConfigurationMetadata
        schemas = $SchemaVersions
        targetFacts = $TargetFacts
        targetId = [string]$releaseTargetMetadata.id
        assetSuffix = $AssetSuffix
        runtimeIdentifier = $runtimeText
        defaultTargetTriple = $targetText
        minimumOs = [string]$releaseTargetMetadata.minimumOs
        minimumOsPolicyStatus = [string](Get-RequiredJsonPropertyValue -Object $targetsManifest -Name "minimumOsPolicyStatus")
        hostPrerequisite = [string]$releaseTargetMetadata.hostPrerequisite
        installerKind = [string]$releaseTargetMetadata.installerKind
        supportTier = [string]$releaseTargetMetadata.supportTier
        privateBackend = [string]$releaseTargetMetadata.privateBackendSelection
        llvmVersion = $LlvmVersion
        archiveKind = $ArchiveKind
        paths = [ordered]@{
            compiler = $CompilerRelativePath
            sdk = "sdk.json"
            stdlib = "stdlib"
            vendor = "vendor"
            compilerPrivateBackend = $toolchainPath
            # Compatibility key consumed by the current archive-relative
            # NativeToolchain resolver. It names the same private backend and
            # does not advertise a general-purpose LLVM SDK.
            toolchain = $toolchainPath
            licenses = "licenses"
            docs = "docs"
        }
        compilerPrivateBackend = $CompilerPrivateBackendMetadata
        sdk = $SdkMetadata
        packages = [object[]]$Packages
        packageSchemaFacts = [object[]]$PackageSchemaFacts
        dependencies = $DependenciesMetadata
        vendorCatalog = $VendorCatalogMetadata
        contentIdentities = $ContentIdentities
        contentChecksumManifest = "release-files.sha256"
        files = $Files
        stdlibArtifacts = $StdlibArtifacts
        vendorArtifacts = $VendorArtifacts
    }

    Write-DeterministicJson -Path $Path -Value $releaseJson -Depth 60
}

$publishPath = Resolve-InputPath -Path $PublishDir -Name "Publish"
$stdlibPackagePath = Resolve-InputPath -Path $StdlibPackageDir -Name "Standard library package"
$managedLicensePath = Resolve-InputPath -Path $ManagedLicenseDir -Name "Managed license evidence"
$vendorRootPath = Resolve-InputPath -Path $VendorRoot -Name "Vendor"
$toolchainSourcePath = Resolve-OptionalInputPath -Path $ToolchainDir -Name "Toolchain"
if ($null -eq $toolchainSourcePath) {
    throw "Release packaging requires a compiler-private backend closure; -ToolchainDir cannot be omitted."
}

$releaseToolsAssembly = @(& (Join-Path $PSScriptRoot "resolve-release-tools.ps1") `
    -RepositoryRoot $repositoryRoot `
    -DotNetPath $DotNetPath `
    -ReleaseToolsPath $ReleaseToolsPath) | Select-Object -Last 1
$releaseToolsProjectPath = Join-Path $repositoryRoot "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj"
$globalJson = Get-Content -LiteralPath (Join-Path $repositoryRoot "global.json") -Raw | ConvertFrom-Json
$dependenciesForToolIdentity = Get-Content -LiteralPath (Join-Path $repositoryRoot "eng/release/dependencies.json") -Raw | ConvertFrom-Json
$managedDependencyForToolIdentity = @($dependenciesForToolIdentity.dependencies | Where-Object { [string]$_.id -ceq "dotnet-stage0-runtime" }) | Select-Object -First 1
if ($null -eq $managedDependencyForToolIdentity -or [string]::IsNullOrWhiteSpace([string]$managedDependencyForToolIdentity.runtimeVersion)) {
    throw "dependencies.json does not declare the managed runtime used by Stark.ReleaseTools."
}
$archiveToolMetadata = [ordered]@{
    manifest = [ordered]@{
        path = "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj"
        schemaVersion = 1
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseToolsProjectPath).Hash.ToLowerInvariant()
    }
    implementation = "Stark.ReleaseTools"
    targetFramework = "net10.0"
    dotnetSdkVersion = [string]$globalJson.sdk.version
    dotnetRuntimeVersion = [string]$managedDependencyForToolIdentity.runtimeVersion
    assembly = [ordered]@{
        bytes = [int64](Get-Item -LiteralPath $releaseToolsAssembly).Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseToolsAssembly).Hash.ToLowerInvariant()
    }
}

$targetsManifestPath = Join-Path $repositoryRoot "eng/release/targets.json"
$targetsManifest = Get-Content -LiteralPath $targetsManifestPath -Raw | ConvertFrom-Json
$targetMatches = @(Get-ArrayValues -Value (Get-RequiredJsonPropertyValue -Object $targetsManifest -Name "targets") |
    Where-Object { [string](Get-RequiredJsonPropertyValue -Object $_ -Name "id") -ceq $AssetSuffix })
if ($targetMatches.Count -ne 1) {
    throw "Release target manifest must contain exactly one '$AssetSuffix' entry."
}
$releaseTarget = $targetMatches[0]
$releaseEnabled = [bool](Get-RequiredJsonPropertyValue -Object $releaseTarget -Name "releaseEnabled")
$supportTier = [string](Get-RequiredJsonPropertyValue -Object $releaseTarget -Name "supportTier")
if ($releaseEnabled -and $AllowPlannedTarget) {
    throw "-AllowPlannedTarget is valid only for a release-disabled planned target."
}
if (-not $releaseEnabled -and (-not $AllowPlannedTarget -or $supportTier -cne "planned")) {
    throw "Release target '$AssetSuffix' is disabled; planned targets require the explicit diagnostic-only -AllowPlannedTarget switch."
}
$targetExpectations = [ordered]@{
    assetSuffix = $AssetSuffix
    runtimeIdentifier = $RuntimeIdentifier
    targetTriple = $TargetTriple
    archiveKind = $ArchiveKind
}
foreach ($expectation in $targetExpectations.GetEnumerator()) {
    $manifestValue = [string](Get-RequiredJsonPropertyValue -Object $releaseTarget -Name $expectation.Key)
    if ([string]::IsNullOrWhiteSpace([string]$expectation.Value) -or
        -not [string]::Equals($manifestValue, [string]$expectation.Value, [StringComparison]::Ordinal)) {
        throw "Release target '$AssetSuffix' $($expectation.Key) '$($expectation.Value)' does not match manifest '$manifestValue'."
    }
}
$privateBackendSelection = [string](Get-RequiredJsonPropertyValue -Object $releaseTarget -Name "privateBackendSelection")
if (-not $privateBackendSelection.StartsWith("llvm-$LlvmVersion/", [StringComparison]::Ordinal)) {
    throw "Release target '$AssetSuffix' private backend '$privateBackendSelection' does not match LLVM '$LlvmVersion'."
}
$architecture = [string](Get-RequiredJsonPropertyValue -Object $releaseTarget -Name "architecture")
if ($architecture -notin @("x64", "arm64")) {
    throw "Release target '$AssetSuffix' architecture '$architecture' violates the 64-bit-only release policy."
}

$dependenciesManifestPath = Join-Path $repositoryRoot "eng/release/dependencies.json"
$dependenciesManifest = Get-Content -LiteralPath $dependenciesManifestPath -Raw | ConvertFrom-Json
$vendorCatalogPath = Join-Path $repositoryRoot "eng/release/vendor-packages.json"
$vendorCatalog = Get-Content -LiteralPath $vendorCatalogPath -Raw | ConvertFrom-Json
$metadataTemplatePath = Join-Path $repositoryRoot "eng/release/release-metadata.template.json"
$metadataTemplate = Get-Content -LiteralPath $metadataTemplatePath -Raw | ConvertFrom-Json
$metadataStaticValues = Get-RequiredJsonPropertyValue -Object $metadataTemplate -Name "staticValues"
if ([int](Get-RequiredJsonPropertyValue -Object $metadataStaticValues -Name "releaseSchemaVersion") -ne 2) {
    throw "Release metadata template must select release.json schema version 2."
}

$releaseConfigurationOutput = @(& (Join-Path $PSScriptRoot "get-release-configuration-identity.ps1") -Root $repositoryRoot)
$releaseConfigurationIdentity = ($releaseConfigurationOutput -join "`n") | ConvertFrom-Json
if ([int](Get-RequiredJsonPropertyValue -Object $releaseConfigurationIdentity -Name "schemaVersion") -ne 1 -or
    [string](Get-RequiredJsonPropertyValue -Object $releaseConfigurationIdentity -Name "identityKind") -cne "stark-release-configuration" -or
    [string](Get-RequiredJsonPropertyValue -Object $releaseConfigurationIdentity -Name "algorithm") -cne "sha256-ordinal-path-size-content-v1") {
    throw "Release configuration identity helper returned an unsupported contract."
}
$packagingConfigurationSha256 = [string](Get-RequiredJsonPropertyValue -Object $releaseConfigurationIdentity -Name "sha256")
$releaseConfiguration = @(Get-ArrayValues -Value (
    Get-RequiredJsonPropertyValue -Object $releaseConfigurationIdentity -Name "files"))
if ($packagingConfigurationSha256 -cnotmatch '^[0-9a-f]{64}$' -or $releaseConfiguration.Count -eq 0) {
    throw "Release configuration identity helper returned an invalid or empty identity."
}
$declaredBuildConfigurationSha256 = if ([string]::IsNullOrWhiteSpace($BuildConfigurationSha256)) {
    $null
} else {
    $BuildConfigurationSha256.Trim().ToLowerInvariant()
}
if ($null -ne $declaredBuildConfigurationSha256 -and
    $declaredBuildConfigurationSha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw "Declared build configuration SHA-256 '$declaredBuildConfigurationSha256' is invalid."
}
if ($null -ne $declaredBuildConfigurationSha256 -and
    -not [string]::Equals(
        $declaredBuildConfigurationSha256,
        $packagingConfigurationSha256,
        [StringComparison]::Ordinal)) {
    throw "Declared build configuration SHA-256 '$declaredBuildConfigurationSha256' does not match release inputs '$packagingConfigurationSha256'."
}
$BuildConfigurationSha256 = $packagingConfigurationSha256

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
$compilerPrivateBackendManifest = $null
if ($null -ne $toolchainSourcePath) {
    # Preserve the existing archive-relative discovery shape while changing
    # its contents and release contract: this directory is an allowlisted,
    # compiler-private backend closure, not a copied LLVM distribution.
    $toolchainRelativePath = "toolchain/llvm-$LlvmVersion"
    $stagedToolchainRoot = Join-Path $stageRoot $toolchainRelativePath
    $compilerPrivateBackendManifest = Copy-CompilerPrivateBackendFromManifest `
        -Source $toolchainSourcePath `
        -Destination $stagedToolchainRoot
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

& (Join-Path $PSScriptRoot "stage-release-repository-content.ps1") `
    -RepositoryRoot $repositoryRoot `
    -StageRoot $stageRoot `
    -ManifestPath (Join-Path $repositoryRoot "eng/release/archive-content.json")
Copy-OptionalFile -Source (Join-Path $repositoryRoot "LICENSE") -Destination (Join-Path $stageRoot "LICENSE")

$licensesRoot = Join-Path $stageRoot "licenses"
New-Item -ItemType Directory -Force -Path (Join-Path $licensesRoot "Stark") | Out-Null
Copy-OptionalFile -Source (Join-Path $repositoryRoot "LICENSE") -Destination (Join-Path $licensesRoot "Stark/LICENSE")
Copy-TreeFiltered -Source $managedLicensePath -Destination (Join-Path $licensesRoot "managed") -ExcludedDirectoryNames @()
Copy-OptionalTree -Source (Join-Path $stageRoot "vendor/licenses") -Destination (Join-Path $licensesRoot "vendor")
Copy-OptionalTree -Source (Join-Path $stageRoot "toolchain/llvm-$LlvmVersion/licenses") -Destination (Join-Path $licensesRoot "LLVM")

$managedLicenseManifestPath = Join-Path $licensesRoot "managed/manifest.json"
if (-not (Test-Path -LiteralPath $managedLicenseManifestPath -PathType Leaf)) {
    throw "Managed license evidence is missing manifest.json."
}
$managedLicenseManifest = Get-Content -LiteralPath $managedLicenseManifestPath -Raw | ConvertFrom-Json
if ([int](Get-RequiredJsonPropertyValue -Object $managedLicenseManifest -Name "schemaVersion") -ne 1 -or
    [string](Get-RequiredJsonPropertyValue -Object $managedLicenseManifest -Name "manifestKind") -cne "stark-managed-license-inventory" -or
    [string](Get-RequiredJsonPropertyValue -Object $managedLicenseManifest -Name "targetId") -cne $AssetSuffix -or
    [string](Get-RequiredJsonPropertyValue -Object $managedLicenseManifest -Name "runtimeIdentifier") -cne $RuntimeIdentifier) {
    throw "Managed license evidence identity does not match release target '$AssetSuffix' / '$RuntimeIdentifier'."
}
$managedLicensePackages = @(Get-ArrayValues -Value (Get-RequiredJsonPropertyValue -Object $managedLicenseManifest -Name "packages"))
if ($managedLicensePackages.Count -ne 3) {
    throw "Managed license evidence must inventory exactly the ANTLR, .NET runtime, and ASP.NET runtime packages."
}
$managedLicenseFileCount = 0
foreach ($managedLicensePackage in $managedLicensePackages) {
    $managedLicenseFileCount += @(Get-ArrayValues -Value (
        Get-RequiredJsonPropertyValue -Object $managedLicensePackage -Name "licenseFiles")).Count
}
if ($managedLicenseFileCount -ne 5) {
    throw "Managed license evidence must inventory exactly five target license/notice files."
}

$commit = Get-CurrentCommit
$sourceIdentity = Get-SourceIdentity -Commit $commit
$buildInvocationIdentity = Get-BuildInvocationIdentity `
    -Commit $commit `
    -ArchiveTool $archiveToolMetadata

$sdkManifestPath = Join-Path $stageRoot "sdk.json"
if (-not (Test-Path -LiteralPath $sdkManifestPath -PathType Leaf)) {
    throw "Release SDK assembly did not emit sdk.json."
}
$sdkManifest = Get-Content -LiteralPath $sdkManifestPath -Raw | ConvertFrom-Json
$sdkSchemaVersion = [int](Get-RequiredJsonPropertyValue -Object $sdkManifest -Name "schemaVersion")
$sdkPackageFormatVersion = [int](Get-RequiredJsonPropertyValue -Object $sdkManifest -Name "packageFormatVersion")
$sdkKind = [string](Get-RequiredJsonPropertyValue -Object $sdkManifest -Name "kind")
$sdkVersion = [string](Get-RequiredJsonPropertyValue -Object $sdkManifest -Name "sdkVersion")
$sdkCompatibility = [string](Get-RequiredJsonPropertyValue -Object $sdkManifest -Name "compilerCompatibility")
if ($sdkSchemaVersion -le 0 -or $sdkPackageFormatVersion -le 0 -or
    $sdkKind -cne "release" -or $sdkVersion -cne $Version) {
    throw "sdk.json does not preserve exact release schema/version facts."
}
$sdkTarget = Get-RequiredJsonPropertyValue -Object $sdkManifest -Name "target"
if ([string](Get-RequiredJsonPropertyValue -Object $sdkTarget -Name "id") -cne $AssetSuffix -or
    [string](Get-RequiredJsonPropertyValue -Object $sdkTarget -Name "llvmTriple") -cne $TargetTriple) {
    throw "sdk.json target facts do not match release target '$AssetSuffix' / '$TargetTriple'."
}

$sdkPackages = @(Get-ArrayValues -Value (Get-RequiredJsonPropertyValue -Object $sdkManifest -Name "packages"))
if ($sdkPackages.Count -eq 0) {
    throw "sdk.json contains no package inventory."
}
$packageSchemaFacts = @()
foreach ($sdkPackage in $sdkPackages) {
    $packageId = [string](Get-RequiredJsonPropertyValue -Object $sdkPackage -Name "id")
    $profile = [string](Get-RequiredJsonPropertyValue -Object $sdkPackage -Name "profile")
    if ($profile -cne "release") {
        throw "SDK package '$packageId' uses profile '$profile'; release archives require release-built packages."
    }
    $imageRelativePath = [string](Get-RequiredJsonPropertyValue -Object $sdkPackage -Name "image")
    Assert-SafeBackendRelativePath -Path $imageRelativePath
    $imagePath = [System.IO.Path]::GetFullPath((Join-Path $stageRoot $imageRelativePath))
    if (-not (Test-IsSameOrDescendantPath -Path $imagePath -Root $stageRoot) -or
        -not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
        throw "SDK package '$packageId' image '$imageRelativePath' is missing or escapes the archive."
    }
    $formatVersion = Get-PackageImageFormatVersion -Path $imagePath
    if ($formatVersion -ne $sdkPackageFormatVersion) {
        throw "SDK package '$packageId' format version '$formatVersion' does not match sdk.json '$sdkPackageFormatVersion'."
    }
    $actualImageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $imagePath).Hash.ToLowerInvariant()
    if ($actualImageSha256 -cne [string](Get-RequiredJsonPropertyValue -Object $sdkPackage -Name "imageSha256")) {
        throw "SDK package '$packageId' image hash does not match sdk.json."
    }
    $packageSchemaFacts += [ordered]@{
        id = $packageId
        image = $imageRelativePath
        formatVersion = [int64]$formatVersion
        imageSha256 = $actualImageSha256
        apiHash = [string](Get-RequiredJsonPropertyValue -Object $sdkPackage -Name "apiHash")
        contentHash = [string](Get-RequiredJsonPropertyValue -Object $sdkPackage -Name "contentHash")
    }
}

$vendorReleaseInputPath = Join-Path $stageRoot "vendor/release-input.json"
if (-not (Test-Path -LiteralPath $vendorReleaseInputPath -PathType Leaf)) {
    throw "Official Vendor tree is missing release-input.json."
}
$vendorReleaseInput = Get-Content -LiteralPath $vendorReleaseInputPath -Raw | ConvertFrom-Json
$vendorReleaseInputSchemaVersion = [int](Get-RequiredJsonPropertyValue -Object $vendorReleaseInput -Name "schemaVersion")
if ($vendorReleaseInputSchemaVersion -ne 2 -or
    [string](Get-RequiredJsonPropertyValue -Object $vendorReleaseInput -Name "manifestKind") -cne "stark-vendor-release-input" -or
    [string](Get-RequiredJsonPropertyValue -Object $vendorReleaseInput -Name "state") -cne "ready") {
    throw "Official Vendor release-input.json must be the ready schema-2 manifest."
}
$vendorReleaseTarget = Get-RequiredJsonPropertyValue -Object $vendorReleaseInput -Name "target"
if ([string](Get-RequiredJsonPropertyValue -Object $vendorReleaseTarget -Name "assetSuffix") -cne $AssetSuffix -or
    [string](Get-RequiredJsonPropertyValue -Object $vendorReleaseTarget -Name "targetTriple") -cne $TargetTriple) {
    throw "Vendor release-input.json target facts do not match '$AssetSuffix' / '$TargetTriple'."
}
$stagedVendorCatalogPath = Join-Path $stageRoot "vendor/catalog/vendor-packages.json"
if (-not (Test-Path -LiteralPath $stagedVendorCatalogPath -PathType Leaf)) {
    throw "Official Vendor tree is missing its staged catalog."
}
$repositoryVendorCatalogSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $vendorCatalogPath).Hash.ToLowerInvariant()
$stagedVendorCatalogSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $stagedVendorCatalogPath).Hash.ToLowerInvariant()
if ($repositoryVendorCatalogSha256 -cne $stagedVendorCatalogSha256) {
    throw "Staged Vendor catalog hash does not match repository release catalog."
}

$compilerContentIdentity = Get-ContentIdentity -Root $compilerBinRoot -StageRoot $stageRoot -Label "compiler-runtime"
$stdlibContentIdentity = Get-ContentIdentity -Root $stdlibRoot -StageRoot $stageRoot -Label "standard-library"
$vendorContentIdentity = Get-ContentIdentity -Root (Join-Path $stageRoot "vendor") -StageRoot $stageRoot -Label "official-vendor"
$backendContentIdentity = if ([string]::IsNullOrWhiteSpace($toolchainRelativePath)) {
    $null
} else {
    Get-ContentIdentity -Root (Join-Path $stageRoot $toolchainRelativePath) -StageRoot $stageRoot -Label "compiler-private-backend"
}
$contentIdentities = [ordered]@{
    compilerRuntime = $compilerContentIdentity
    standardLibrary = $stdlibContentIdentity
    vendor = $vendorContentIdentity
    compilerPrivateBackend = $backendContentIdentity
}

$dependencyEntries = @()
foreach ($dependency in (Get-ArrayValues -Value (Get-RequiredJsonPropertyValue -Object $dependenciesManifest -Name "dependencies"))) {
    $dependencyId = [string](Get-RequiredJsonPropertyValue -Object $dependency -Name "id")
    $selections = @(Get-ArrayValues -Value (Get-RequiredJsonPropertyValue -Object $dependency -Name "selections") |
        Where-Object { [string](Get-RequiredJsonPropertyValue -Object $_ -Name "target") -ceq $AssetSuffix })
    if ($selections.Count -ne 1) {
        throw "Dependency '$dependencyId' must have exactly one '$AssetSuffix' selection."
    }
    $archiveLayout = [string](Get-RequiredJsonPropertyValue -Object $dependency -Name "archiveLayout")
    $dependencyContentIdentity = if ($archiveLayout -ceq "bin") {
        $compilerContentIdentity
    } elseif ($archiveLayout -ceq $toolchainRelativePath -and $null -ne $backendContentIdentity) {
        $backendContentIdentity
    } else {
        $layoutPath = Join-Path $stageRoot $archiveLayout
        if (Test-Path -LiteralPath $layoutPath -PathType Container) {
            Get-ContentIdentity -Root $layoutPath -StageRoot $stageRoot -Label $dependencyId
        } else {
            $null
        }
    }
    $acquisitionManifestDescriptor = $null
    $acquisitionManifest = Get-OptionalJsonPropertyValue -Object $dependency -Name "acquisitionManifest"
    if (-not [string]::IsNullOrWhiteSpace([string]$acquisitionManifest)) {
        Assert-SafeBackendRelativePath -Path ([string]$acquisitionManifest)
        $acquisitionManifestDescriptor = Get-JsonFileDescriptor `
            -Path (Join-Path $repositoryRoot ([string]$acquisitionManifest)) `
            -RelativePath ([string]$acquisitionManifest)
    }
    $dependencyEntries += [ordered]@{
        id = $dependencyId
        kind = [string](Get-RequiredJsonPropertyValue -Object $dependency -Name "kind")
        version = [string](Get-RequiredJsonPropertyValue -Object $dependency -Name "version")
        pinStatus = [string](Get-RequiredJsonPropertyValue -Object $dependency -Name "pinStatus")
        sourceUrl = [string](Get-RequiredJsonPropertyValue -Object $dependency -Name "sourceUrl")
        license = [string](Get-RequiredJsonPropertyValue -Object $dependency -Name "license")
        licenseUrl = [string](Get-RequiredJsonPropertyValue -Object $dependency -Name "licenseUrl")
        sourceSha256 = Get-OptionalJsonPropertyValue -Object $dependency -Name "sourceSha256"
        declarationSha256 = Get-ObjectSha256 -Value $dependency
        selection = $selections[0]
        selectionSha256 = Get-ObjectSha256 -Value $selections[0]
        acquisitionManifest = $acquisitionManifestDescriptor
        contentIdentity = $dependencyContentIdentity
    }
}
$dependenciesMetadata = [ordered]@{
    manifest = Get-JsonFileDescriptor -Path $dependenciesManifestPath -RelativePath "eng/release/dependencies.json"
    selected = [object[]]$dependencyEntries
    managedLicenseInventory = [ordered]@{
        path = "licenses/managed/manifest.json"
        schemaVersion = [int]$managedLicenseManifest.schemaVersion
        manifestKind = [string]$managedLicenseManifest.manifestKind
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $managedLicenseManifestPath).Hash.ToLowerInvariant()
        packageCount = $managedLicensePackages.Count
        licenseFileCount = $managedLicenseFileCount
        declaration = Get-RequiredJsonPropertyValue -Object $managedLicenseManifest -Name "declaration"
    }
}

$vendorReleasePackages = @(Get-ArrayValues -Value (Get-RequiredJsonPropertyValue -Object $vendorReleaseInput -Name "packages"))
$vendorPackageFacts = @()
foreach ($catalogPackage in (Get-ArrayValues -Value (Get-RequiredJsonPropertyValue -Object $vendorCatalog -Name "packages"))) {
    $packageId = [string](Get-RequiredJsonPropertyValue -Object $catalogPackage -Name "id")
    $releaseContributions = @($vendorReleasePackages |
        Where-Object { [string](Get-RequiredJsonPropertyValue -Object $_ -Name "id") -ceq $packageId })
    if ($releaseContributions.Count -ne 1) {
        throw "Vendor release-input must contain exactly one '$packageId' contribution."
    }
    $targetSupport = Get-RequiredJsonPropertyValue -Object $catalogPackage -Name "targetSupport"
    $targetSupportProperty = $targetSupport.PSObject.Properties[$AssetSuffix]
    if ($null -eq $targetSupportProperty -or [string]::IsNullOrWhiteSpace([string]$targetSupportProperty.Value)) {
        throw "Vendor catalog package '$packageId' has no '$AssetSuffix' target support fact."
    }
    $buildRecipe = [string](Get-RequiredJsonPropertyValue -Object $catalogPackage -Name "buildRecipe")
    $acquisitionManifestDescriptor = $null
    $acquisitionManifest = Get-OptionalJsonPropertyValue -Object $catalogPackage -Name "acquisitionManifest"
    if (-not [string]::IsNullOrWhiteSpace([string]$acquisitionManifest)) {
        $acquisitionManifestDescriptor = Get-JsonFileDescriptor `
            -Path (Join-Path $repositoryRoot ([string]$acquisitionManifest)) `
            -RelativePath ([string]$acquisitionManifest)
    }
    $vendorPackageFacts += [ordered]@{
        id = $packageId
        version = [string](Get-RequiredJsonPropertyValue -Object $catalogPackage -Name "version")
        sourceIdentity = [string](Get-RequiredJsonPropertyValue -Object $catalogPackage -Name "sourceIdentity")
        sourceSha256 = Get-OptionalJsonPropertyValue -Object $catalogPackage -Name "sourceSha256"
        targetSupport = [string]$targetSupportProperty.Value
        declarationSha256 = Get-ObjectSha256 -Value $catalogPackage
        releaseContributionSha256 = Get-ObjectSha256 -Value $releaseContributions[0]
        buildRecipe = Get-FileDescriptor `
            -Path (Join-Path $repositoryRoot $buildRecipe) `
            -RelativePath $buildRecipe
        acquisitionManifest = $acquisitionManifestDescriptor
    }
}

$vendorCatalogMetadata = [ordered]@{
    id = [string](Get-RequiredJsonPropertyValue -Object $vendorCatalog -Name "catalogId")
    schemaVersion = [int](Get-RequiredJsonPropertyValue -Object $vendorCatalog -Name "schemaVersion")
    source = Get-JsonFileDescriptor -Path $vendorCatalogPath -RelativePath "eng/release/vendor-packages.json"
    stagedPath = "vendor/catalog/vendor-packages.json"
    stagedSha256 = $stagedVendorCatalogSha256
    releaseInput = [ordered]@{
        path = "vendor/release-input.json"
        schemaVersion = $vendorReleaseInputSchemaVersion
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $vendorReleaseInputPath).Hash.ToLowerInvariant()
        packageCount = $vendorReleasePackages.Count
    }
    selectedPackages = [object[]]$vendorPackageFacts
}

$sdkMetadata = [ordered]@{
    path = "sdk.json"
    sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sdkManifestPath).Hash.ToLowerInvariant()
    schemaVersion = $sdkSchemaVersion
    kind = $sdkKind
    sdkVersion = $sdkVersion
    compilerCompatibility = $sdkCompatibility
    packageFormatVersion = $sdkPackageFormatVersion
}
$schemaVersions = [ordered]@{
    releaseMetadata = 2
    sdkManifest = $sdkSchemaVersion
    packageImageFormat = $sdkPackageFormatVersion
    vendorReleaseInput = $vendorReleaseInputSchemaVersion
    compilerPrivateBackend = if ($null -eq $compilerPrivateBackendManifest) { $null } else { [int](Get-RequiredJsonPropertyValue -Object $compilerPrivateBackendManifest -Name "schemaVersion") }
    targetManifest = [int](Get-RequiredJsonPropertyValue -Object $targetsManifest -Name "schemaVersion")
    dependencyManifest = [int](Get-RequiredJsonPropertyValue -Object $dependenciesManifest -Name "schemaVersion")
    vendorCatalog = [int](Get-RequiredJsonPropertyValue -Object $vendorCatalog -Name "schemaVersion")
    archiveContent = [int](Get-RequiredJsonPropertyValue -Object (Get-Content -LiteralPath (Join-Path $repositoryRoot "eng/release/archive-content.json") -Raw | ConvertFrom-Json) -Name "schemaVersion")
    releaseTools = 1
    releaseMetadataTemplate = [int](Get-RequiredJsonPropertyValue -Object $metadataTemplate -Name "schemaVersion")
}
$targetFacts = [ordered]@{
    release = $releaseTarget
    sdk = $sdkTarget
}
$buildOptions = [ordered]@{
    configuration = $BuildConfiguration
    compilerRuntime = "self-contained"
    packageProfile = "release"
    architecturePolicy = "64-bit-only"
    archiveKind = $ArchiveKind
    assetSuffix = $AssetSuffix
    runtimeIdentifier = $RuntimeIdentifier
    targetTriple = $TargetTriple
    llvmVersion = $LlvmVersion
    archiveContainerTool = $archiveToolMetadata
}
$configurationMetadata = [ordered]@{
    identityKind = [string]$releaseConfigurationIdentity.identityKind
    algorithm = [string]$releaseConfigurationIdentity.algorithm
    sha256 = $packagingConfigurationSha256
    packagingInputsSha256 = $packagingConfigurationSha256
    files = [object[]]$releaseConfiguration
}

$compilerPrivateBackendMetadata = $null
if ($null -ne $compilerPrivateBackendManifest) {
    $backendClosure = Get-RequiredJsonPropertyValue -Object $compilerPrivateBackendManifest -Name "runtimeClosure"
    $backendManifestRelativePath = "$toolchainRelativePath/manifest.json"
    $backendManifestPath = Join-Path $stageRoot $backendManifestRelativePath
    $backendAcquisitionKind = [string](Get-RequiredJsonPropertyValue -Object $compilerPrivateBackendManifest -Name "acquisitionKind")
    if ($backendAcquisitionKind -notin @("upstream-archive", "pinned-source-build")) {
        throw "Compiler-private backend has unsupported acquisition kind '$backendAcquisitionKind'."
    }
    $backendBinaryArchive = Get-OptionalJsonPropertyValue -Object $compilerPrivateBackendManifest -Name "binaryArchive"
    $backendSourceBuild = Get-OptionalJsonPropertyValue -Object $compilerPrivateBackendManifest -Name "sourceBuild"
    if (($backendAcquisitionKind -ceq "upstream-archive" -and
         ($null -eq $backendBinaryArchive -or $null -ne $backendSourceBuild)) -or
        ($backendAcquisitionKind -ceq "pinned-source-build" -and
         ($null -ne $backendBinaryArchive -or $null -eq $backendSourceBuild))) {
        throw "Compiler-private backend acquisition metadata is internally inconsistent."
    }
    $compilerPrivateBackendMetadata = [ordered]@{
        kind = [string] $compilerPrivateBackendManifest.payloadKind
        schemaVersion = [int]$compilerPrivateBackendManifest.schemaVersion
        llvmVersion = [string]$compilerPrivateBackendManifest.llvmVersion
        releaseTag = [string]$compilerPrivateBackendManifest.releaseTag
        acquisitionKind = $backendAcquisitionKind
        path = $toolchainRelativePath
        manifest = $backendManifestRelativePath
        manifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $backendManifestPath).Hash.ToLowerInvariant()
        sourceArchiveSha256 = [string]$compilerPrivateBackendManifest.sourceArchive.sha256
        binaryArchiveSha256 = if ($null -eq $backendBinaryArchive) { $null } else { [string]$backendBinaryArchive.sha256 }
        sourceBuild = $backendSourceBuild
        fileCount = [int64] $backendClosure.fileCount
        logicalBytes = [int64] $backendClosure.logicalBytes
        runtimeClosureManifestSha256 = Get-ObjectSha256 -Value $backendClosure.files
    }
}
Write-ReleaseText `
    -Path (Join-Path $stageRoot "RELEASE.txt") `
    -Commit $commit `
    -ToolchainRelativePath $toolchainRelativePath `
    -BuildInvocationIdentity $buildInvocationIdentity `
    -SchemaVersions $schemaVersions `
    -ConfigurationSha256 $packagingConfigurationSha256
& (Join-Path $PSScriptRoot "stage-release-installers.ps1") `
    -StageRoot $stageRoot `
    -AssetSuffix $AssetSuffix
& (Join-Path $PSScriptRoot "generate-release-docs.ps1") `
    -StageRoot $stageRoot `
    -Version $Version `
    -AssetSuffix $AssetSuffix `
    -PackagedRuntimeIdentifier $RuntimeIdentifier `
    -PackagedTargetTriple $TargetTriple `
    -PackagedArchiveKind $ArchiveKind `
    -PackagedLlvmVersion $LlvmVersion

Write-ReleaseJson `
    -Path (Join-Path $stageRoot "release.json") `
    -Commit $commit `
    -CompilerRelativePath $compilerRelativePath `
    -ToolchainRelativePath $toolchainRelativePath `
    -CompilerPrivateBackendMetadata $compilerPrivateBackendMetadata `
    -SourceIdentity $sourceIdentity `
    -BuildInvocationIdentity $buildInvocationIdentity `
    -BuildOptions $buildOptions `
    -SchemaVersions $schemaVersions `
    -TargetFacts $targetFacts `
    -ConfigurationMetadata $configurationMetadata `
    -SdkMetadata $sdkMetadata `
    -Packages $sdkPackages `
    -PackageSchemaFacts $packageSchemaFacts `
    -DependenciesMetadata $dependenciesMetadata `
    -VendorCatalogMetadata $vendorCatalogMetadata `
    -ContentIdentities $contentIdentities `
    -Files (Get-FileManifest -Root $stageRoot) `
    -StdlibArtifacts (Get-FileManifest -Root (Join-Path $stdlibRoot "dist")) `
    -VendorArtifacts (Get-FileManifest -Root (Join-Path $stageRoot "vendor/dist"))

Write-ReleaseFileChecksums `
    -Root $stageRoot `
    -Path (Join-Path $stageRoot "release-files.sha256")

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$archiveExtension = if ($ArchiveKind -eq "zip") { ".zip" } else { ".tar.gz" }
$archivePath = Join-Path $outputPath "$assetBase$archiveExtension"
Assert-NoReparsePointPath -Path $archivePath
& $DotNetPath $releaseToolsAssembly create-archive `
    --source-root $stageRoot `
    --output $archivePath `
    --kind $ArchiveKind
if ($LASTEXITCODE -ne 0) {
    throw "Deterministic release archive creation failed for '$archivePath'."
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
