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

    [string] $CacheDir = "artifacts/vendor-cache/glfw",

    [switch] $Force,

    [switch] $AllowPlannedTarget
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$recipePath = "scripts/prepare-glfw-vendor-release-input.ps1"
$packageId = "Vendor.GLFW"
. (Join-Path $PSScriptRoot "invoke-release-download.ps1")

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

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "Required JSON property '$Name' was not found."
    }

    return ,$property.Value
}

function Get-OptionalProperty {
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

    return ,$property.Value
}

function Get-ArrayValues {
    param([object] $Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Sort-StringsOrdinal {
    param([AllowEmptyCollection()][string[]] $Values = @())

    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($value in $Values) {
        $items.Add($value)
    }
    $items.Sort([System.StringComparer]::Ordinal)
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
        return [System.StringComparer]::Ordinal.Compare([string]$leftValue, [string]$rightValue)
    })
    return [object[]]$items.ToArray()
}

function Write-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)][object] $Value,
        [Parameter(Mandatory = $true)][string] $Path,
        [int] $Depth = 40
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $json = ($Value | ConvertTo-Json -Depth $Depth).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Test-IsSameOrDescendantPath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Root
    )

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $canonicalPath = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    $canonicalRoot = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Root))
    return $canonicalPath.Equals($canonicalRoot, $comparison) -or
        $canonicalPath.StartsWith($canonicalRoot + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Get-PortableRelativePath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $canonicalRoot = [System.IO.Path]::GetFullPath($Root)
    $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    $relativePath = [System.IO.Path]::GetRelativePath($canonicalRoot, $canonicalPath).Replace('\', '/')
    if ([System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath -eq ".." -or
        $relativePath.StartsWith("../", [StringComparison]::Ordinal)) {
        throw "$Label '$canonicalPath' escapes root '$canonicalRoot'."
    }
    return $relativePath
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

    $artifactsRoot = Join-Path $repositoryRoot "artifacts"
    if (-not (Test-IsSameOrDescendantPath -Path $Path -Root $artifactsRoot) -or
        [System.IO.Path]::GetFullPath($Path) -eq [System.IO.Path]::GetFullPath($artifactsRoot)) {
        throw "GLFW contributor output '$Path' must be a child of repository artifacts '$artifactsRoot'."
    }
    if (Test-Path -LiteralPath $Path) {
        Assert-NoReparsePointPath -Path $Path
    }
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ExpectedSha256,
        [Nullable[int64]] $ExpectedBytes = $null
    )

    if ($ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Expected SHA-256 '$ExpectedSha256' for '$Path' is invalid."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Pinned input '$Path' does not exist."
    }
    $file = Get-Item -LiteralPath $Path -Force
    if ($null -ne $ExpectedBytes -and $file.Length -ne [int64]$ExpectedBytes) {
        throw "Pinned input '$Path' has $($file.Length) bytes, expected $ExpectedBytes."
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    if ($actual -cne $ExpectedSha256) {
        throw "Pinned input '$Path' failed SHA-256 validation; expected '$ExpectedSha256', got '$actual'."
    }
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination
    )

    Assert-NoReparsePointPath -Path $Source
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required GLFW input '$Source' does not exist."
    }
    Assert-NoReparsePointPath -Path $Destination
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Remove-OwnedPath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    [void](Get-PortableRelativePath -Root $Root -Path $Path -Label "GLFW-owned path")
    if (Test-Path -LiteralPath $Path) {
        Assert-NoReparsePointPath -Path $Path
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Get-TargetHostFacts {
    param([Parameter(Mandatory = $true)][object] $Target)

    $operatingSystem = [string](Get-RequiredProperty -Object $Target -Name "operatingSystem")
    $architecture = [string](Get-RequiredProperty -Object $Target -Name "architecture")
    $hostOperatingSystem = if ($IsWindows) { "windows" } elseif ($IsLinux) { "linux" } elseif ($IsMacOS) { "macos" } else { "unknown" }
    $hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    if ($hostOperatingSystem -cne $operatingSystem -or $hostArchitecture -cne $architecture) {
        throw "GLFW release input '$AssetSuffix' must be prepared on $operatingSystem-$architecture; this host is $hostOperatingSystem-$hostArchitecture."
    }
    return [pscustomobject]@{ OperatingSystem = $operatingSystem; Architecture = $architecture }
}

function Get-ToolPath {
    param(
        [Parameter(Mandatory = $true)][string] $ToolchainRoot,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $fileName = if ($IsWindows) { "$Name.exe" } else { $Name }
    $path = Join-Path $ToolchainRoot "bin/$fileName"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Schema-2 private backend is missing required GLFW build tool '$path'."
    }
    Assert-NoReparsePointPath -Path $path
    return $path
}

function Get-OrDownloadPinnedArchive {
    param(
        [Parameter(Mandatory = $true)][object] $Descriptor,
        [Parameter(Mandatory = $true)][string] $CacheRoot
    )

    $name = [string](Get-RequiredProperty -Object $Descriptor -Name "name")
    $url = [string](Get-RequiredProperty -Object $Descriptor -Name "url")
    $sha256 = [string](Get-RequiredProperty -Object $Descriptor -Name "sha256")
    $bytes = [int64](Get-RequiredProperty -Object $Descriptor -Name "size")
    if ($name -cne [System.IO.Path]::GetFileName($name) -or $url -notmatch '^https://github\.com/glfw/glfw/releases/download/3\.4/') {
        throw "GLFW acquisition descriptor '$name' is not a pinned official 3.4 release asset."
    }
    Assert-NoReparsePointPath -Path $CacheRoot
    $digestCacheRoot = Join-Path $CacheRoot $sha256
    Assert-NoReparsePointPath -Path $digestCacheRoot
    New-Item -ItemType Directory -Force -Path $digestCacheRoot | Out-Null
    $archivePath = Join-Path $digestCacheRoot $name
    Assert-NoReparsePointPath -Path $archivePath
    if ($Force -or -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        $downloadPath = "$archivePath.download-$([Guid]::NewGuid().ToString('N'))"
        try {
            Invoke-ReleaseDownload -Uri $url -OutFile $downloadPath
            Assert-Sha256 -Path $downloadPath -ExpectedSha256 $sha256 -ExpectedBytes $bytes
            Move-Item -LiteralPath $downloadPath -Destination $archivePath -Force
        } finally {
            if (Test-Path -LiteralPath $downloadPath -PathType Leaf) {
                Remove-Item -LiteralPath $downloadPath -Force
            }
        }
    }
    Assert-Sha256 -Path $archivePath -ExpectedSha256 $sha256 -ExpectedBytes $bytes
    return $archivePath
}

function Expand-CheckedZip {
    param(
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][string] $Destination
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $destinationRoot = [System.IO.Path]::GetFullPath($Destination)
    $paths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $entryPath = $entry.FullName
            if ([string]::IsNullOrWhiteSpace($entryPath) -or
                $entryPath.Contains('\', [StringComparison]::Ordinal) -or
                [System.IO.Path]::IsPathRooted($entryPath) -or
                $entryPath.Contains(':', [StringComparison]::Ordinal)) {
                throw "GLFW archive '$ArchivePath' contains unsafe entry '$entryPath'."
            }
            $trimmedPath = $entryPath.TrimEnd('/')
            if ([string]::IsNullOrWhiteSpace($trimmedPath)) {
                throw "GLFW archive '$ArchivePath' contains an empty entry path."
            }
            $segments = @($trimmedPath.Split('/'))
            if (@($segments | Where-Object { $_ -in @("", ".", "..") }).Count -ne 0) {
                throw "GLFW archive '$ArchivePath' contains traversal entry '$entryPath'."
            }
            if (-not $paths.Add($trimmedPath)) {
                throw "GLFW archive '$ArchivePath' contains duplicate or case-colliding entry '$entryPath'."
            }
            $externalAttributes = [BitConverter]::ToUInt32(
                [BitConverter]::GetBytes([int32]$entry.ExternalAttributes),
                0)
            $unixMode = ($externalAttributes -shr 16) -band 0xF000
            if ($unixMode -eq 0xA000) {
                throw "GLFW archive '$ArchivePath' contains symbolic link '$entryPath'."
            }
            $resolved = [System.IO.Path]::GetFullPath((Join-Path $destinationRoot $trimmedPath))
            if (-not (Test-IsSameOrDescendantPath -Path $resolved -Root $destinationRoot)) {
                throw "GLFW archive '$ArchivePath' entry '$entryPath' escapes extraction root."
            }
        }
    } finally {
        $archive.Dispose()
    }

    [System.IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $destinationRoot, $true)
    foreach ($item in (Get-ChildItem -LiteralPath $destinationRoot -Recurse -Force)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Extracted GLFW input contains symbolic link or reparse point '$($item.FullName)'."
        }
    }
}

function Read-BigEndianUInt32 {
    param(
        [Parameter(Mandatory = $true)][byte[]] $Bytes,
        [Parameter(Mandatory = $true)][int] $Offset
    )

    if ($Offset -lt 0 -or $Offset + 4 -gt $Bytes.Length) {
        throw "Truncated Darwin universal archive header."
    }
    return [uint32]((([uint32]$Bytes[$Offset]) -shl 24) -bor
        (([uint32]$Bytes[$Offset + 1]) -shl 16) -bor
        (([uint32]$Bytes[$Offset + 2]) -shl 8) -bor
        ([uint32]$Bytes[$Offset + 3]))
}

function Assert-StaticArchive {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label '$Path' does not exist."
    }
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 8) {
            throw "$Label '$Path' is empty or truncated."
        }
        $magicBytes = [byte[]]::new(8)
        [void]$stream.Read($magicBytes, 0, 8)
        $magic = [System.Text.Encoding]::ASCII.GetString($magicBytes)
        if ($magic -eq "!<thin>`n") {
            throw "$Label '$Path' is a thin archive; release inputs require self-contained archives."
        }
        if ($magic -ne "!<arch>`n") {
            throw "$Label '$Path' is not a static-library archive."
        }
    } finally {
        $stream.Dispose()
    }
}

function Assert-GlfwLinuxBuildHeaders {
    param(
        [Parameter(Mandatory = $true)][string] $ClangPath,
        [Parameter(Mandatory = $true)][string[]] $CompileFlags,
        [Parameter(Mandatory = $true)][string] $ObjectRoot
    )

    $requiredHeaders = @(
        "X11/XKBlib.h",
        "X11/Xatom.h",
        "X11/Xcursor/Xcursor.h",
        "X11/Xlib.h",
        "X11/Xmd.h",
        "X11/Xresource.h",
        "X11/cursorfont.h",
        "X11/extensions/XInput2.h",
        "X11/extensions/Xinerama.h",
        "X11/extensions/Xrandr.h",
        "X11/extensions/shape.h",
        "X11/keysym.h"
    )
    $probePath = Join-Path $ObjectRoot "glfw-x11-header-probe.c"
    $probeSource = (($requiredHeaders | ForEach-Object { "#include <$_>" }) -join "`n") + "`n"
    [System.IO.File]::WriteAllText($probePath, $probeSource, [System.Text.UTF8Encoding]::new($false))

    & $ClangPath @CompileFlags -fsyntax-only $probePath
    if ($LASTEXITCODE -ne 0) {
        throw "GLFW X11 source build requires target-compatible X11 development headers. On Debian or Ubuntu install the compile-time-only 'xorg-dev' package."
    }
}

function Select-DarwinArchiveSlice {
    param(
        [Parameter(Mandatory = $true)][string] $UniversalArchive,
        [Parameter(Mandatory = $true)][string] $OutputArchive,
        [Parameter(Mandatory = $true)][ValidateSet("x64", "arm64")][string] $Architecture
    )

    $bytes = [System.IO.File]::ReadAllBytes($UniversalArchive)
    if ($bytes.Length -lt 48 -or (Read-BigEndianUInt32 -Bytes $bytes -Offset 0) -ne [uint32]3405691582) {
        throw "Pinned GLFW macOS archive '$UniversalArchive' is not the expected big-endian Darwin universal archive."
    }
    $architectureCount = [int](Read-BigEndianUInt32 -Bytes $bytes -Offset 4)
    $expectedCpuType = if ($Architecture -ceq "x64") { [uint32]0x01000007 } else { [uint32]0x0100000C }
    $matches = @()
    for ($index = 0; $index -lt $architectureCount; $index++) {
        $entryOffset = 8 + ($index * 20)
        $cpuType = Read-BigEndianUInt32 -Bytes $bytes -Offset $entryOffset
        $sliceOffset = [uint64](Read-BigEndianUInt32 -Bytes $bytes -Offset ($entryOffset + 8))
        $sliceSize = [uint64](Read-BigEndianUInt32 -Bytes $bytes -Offset ($entryOffset + 12))
        if ($sliceOffset + $sliceSize -gt [uint64]$bytes.Length -or $sliceSize -lt 8) {
            throw "Pinned GLFW macOS archive contains a truncated universal slice."
        }
        if ($cpuType -eq $expectedCpuType) {
            $matches += [pscustomobject]@{ Offset = $sliceOffset; Size = $sliceSize }
        }
    }
    if ($matches.Count -ne 1) {
        throw "Pinned GLFW macOS archive must contain exactly one $Architecture slice; found $($matches.Count)."
    }

    $slice = [byte[]]::new([int]$matches[0].Size)
    [System.Array]::Copy($bytes, [int64]$matches[0].Offset, $slice, 0, $slice.Length)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputArchive) | Out-Null
    [System.IO.File]::WriteAllBytes($OutputArchive, $slice)
    Assert-StaticArchive -Path $OutputArchive -Label "GLFW $Architecture native library"
}

function Invoke-GlfwSourceBuild {
    param(
        [Parameter(Mandatory = $true)][string] $SourceRoot,
        [Parameter(Mandatory = $true)][string] $ObjectRoot,
        [Parameter(Mandatory = $true)][string] $OutputLibrary,
        [Parameter(Mandatory = $true)][string] $BridgeSource,
        [Parameter(Mandatory = $true)][string] $BridgeObject,
        [Parameter(Mandatory = $true)][string] $OperatingSystem,
        [Parameter(Mandatory = $true)][string] $ClangPath,
        [Parameter(Mandatory = $true)][string] $ArchiverPath,
        [Parameter(Mandatory = $true)][string] $RanlibPath
    )

    $commonSources = @(
        "context.c", "init.c", "input.c", "monitor.c", "platform.c", "vulkan.c", "window.c",
        "egl_context.c", "osmesa_context.c", "null_init.c", "null_monitor.c", "null_window.c", "null_joystick.c"
    )
    $platformSources = if ($OperatingSystem -ceq "linux") {
        @(
            "posix_module.c", "posix_time.c", "posix_thread.c", "x11_init.c", "x11_monitor.c",
            "x11_window.c", "xkb_unicode.c", "glx_context.c", "linux_joystick.c", "posix_poll.c"
        )
    } elseif ($OperatingSystem -ceq "windows") {
        @(
            "win32_module.c", "win32_time.c", "win32_thread.c", "win32_init.c", "win32_joystick.c",
            "win32_monitor.c", "win32_window.c", "wgl_context.c"
        )
    } else {
        throw "Source-built GLFW is unsupported for operating system '$OperatingSystem'."
    }

    $targetDefinition = if ($OperatingSystem -ceq "linux") { "_GLFW_X11" } else { "_GLFW_WIN32" }
    $compileFlags = @(
        "--target=$TargetTriple",
        "-std=c99",
        "-O3",
        "-flto=thin",
        "-ffunction-sections",
        "-fdata-sections",
        "-fno-ident",
        "-DNDEBUG",
        "-D$targetDefinition",
        "-I", (Join-Path $SourceRoot "include"),
        "-I", (Join-Path $SourceRoot "src"),
        "-ffile-prefix-map=$SourceRoot=/usr/src/glfw-3.4",
        "-fdebug-prefix-map=$SourceRoot=/usr/src/glfw-3.4",
        "-fmacro-prefix-map=$SourceRoot=/usr/src/glfw-3.4"
    )
    if ($OperatingSystem -ceq "linux") {
        $compileFlags += @("-fPIC", "-pthread", "-D_DEFAULT_SOURCE")
    } else {
        $compileFlags += @("-DUNICODE", "-D_UNICODE", "-D_CRT_SECURE_NO_WARNINGS")
    }

    New-Item -ItemType Directory -Force -Path $ObjectRoot | Out-Null
    if ($OperatingSystem -ceq "linux") {
        Assert-GlfwLinuxBuildHeaders -ClangPath $ClangPath -CompileFlags $compileFlags -ObjectRoot $ObjectRoot
    }
    $objectPaths = @()
    $sourceNames = @($commonSources + $platformSources)
    for ($index = 0; $index -lt $sourceNames.Count; $index++) {
        $sourceName = $sourceNames[$index]
        $sourcePath = Join-Path $SourceRoot "src/$sourceName"
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Pinned GLFW source archive is missing '$sourceName'."
        }
        $objectExtension = if ($OperatingSystem -ceq "windows") { ".obj" } else { ".o" }
        $objectPath = Join-Path $ObjectRoot ("{0:D2}-{1}{2}" -f $index, [System.IO.Path]::GetFileNameWithoutExtension($sourceName), $objectExtension)
        $arguments = @($compileFlags + @("-c", $sourcePath, "-o", $objectPath))
        & $ClangPath @arguments
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $objectPath -PathType Leaf)) {
            throw "Bundled Clang failed to compile pinned GLFW source '$sourceName' for '$TargetTriple'."
        }
        $objectPaths += $objectPath
    }

    $bridgeCompileFlags = @($compileFlags + @(
        "-DGLFW_INCLUDE_NONE",
        "-ffile-prefix-map=$repositoryRoot=/usr/src/stark",
        "-fdebug-prefix-map=$repositoryRoot=/usr/src/stark",
        "-fmacro-prefix-map=$repositoryRoot=/usr/src/stark"
    ))
    & $ClangPath @bridgeCompileFlags -c $BridgeSource -o $BridgeObject
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $BridgeObject -PathType Leaf)) {
        throw "Bundled Clang failed to compile the Stark GLFW event bridge for '$TargetTriple'."
    }
    $objectPaths += $BridgeObject

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputLibrary) | Out-Null
    & $ArchiverPath rcsD $OutputLibrary @objectPaths
    if ($LASTEXITCODE -ne 0) {
        throw "Bundled llvm-ar failed to create deterministic GLFW archive '$OutputLibrary'."
    }
    & $RanlibPath -D $OutputLibrary
    if ($LASTEXITCODE -ne 0) {
        throw "Bundled llvm-ranlib failed to index deterministic GLFW archive '$OutputLibrary'."
    }
    Assert-StaticArchive -Path $OutputLibrary -Label "source-built GLFW native library"
    $archiveMembers = @(& $ArchiverPath t $OutputLibrary)
    if ($LASTEXITCODE -ne 0 -or @($archiveMembers | Where-Object { $_ -ceq [System.IO.Path]::GetFileName($BridgeObject) }).Count -ne 1) {
        throw "Source-built GLFW archive does not contain exactly one precompiled Stark event bridge."
    }
    $recordedCompileFlags = @($compileFlags | ForEach-Object {
        ([string]$_).Replace($SourceRoot, "<glfw-source>").Replace($repositoryRoot, "<checkout>")
    })
    $recordedBridgeCompileFlags = @($bridgeCompileFlags | ForEach-Object {
        ([string]$_).Replace($SourceRoot, "<glfw-source>").Replace($repositoryRoot, "<checkout>")
    })

    return [ordered]@{
        sourceFiles = [object[]]$sourceNames
        compileFlags = [object[]]$recordedCompileFlags
        bridgeCompileFlags = [object[]]$recordedBridgeCompileFlags
        archiveFlags = "rcsD / ranlib -D"
        archiveMembers = [object[]]$archiveMembers
        eventBridgeCompiledIntoNativeArchive = $true
        perApplicationNativeSourceCompilation = $false
        optimizationRationale = "GLFW is a performance-critical native boundary. Release source builds use -O3 and ThinLTO bitcode so qualified Stark release links can optimize across native archive members; function/data sections retain dead-stripping granularity. Deterministic llvm-ar/ranlib modes and prefix maps remove archive timestamps and build-root identity."
    }
}

function Add-GlfwBridgeToNativeArchive {
    param(
        [Parameter(Mandatory = $true)][string] $BridgeSource,
        [Parameter(Mandatory = $true)][string] $BridgeObject,
        [Parameter(Mandatory = $true)][string] $IncludeRoot,
        [Parameter(Mandatory = $true)][string] $NativeArchive,
        [Parameter(Mandatory = $true)][string] $ClangPath,
        [Parameter(Mandatory = $true)][string] $ArchiverPath,
        [Parameter(Mandatory = $true)][string] $RanlibPath
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $BridgeObject) | Out-Null
    $bridgeCompileFlags = @(
        "--target=$TargetTriple",
        "-std=c11",
        "-O3",
        "-ffunction-sections",
        "-fdata-sections",
        "-fno-ident",
        "-DNDEBUG",
        "-DGLFW_INCLUDE_NONE",
        "-I", $IncludeRoot,
        "-ffile-prefix-map=$repositoryRoot=/usr/src/stark",
        "-fdebug-prefix-map=$repositoryRoot=/usr/src/stark",
        "-fmacro-prefix-map=$repositoryRoot=/usr/src/stark"
    )
    & $ClangPath @bridgeCompileFlags -c $BridgeSource -o $BridgeObject
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $BridgeObject -PathType Leaf)) {
        throw "Bundled Clang failed to compile the Stark GLFW event bridge for '$TargetTriple'."
    }

    & $ArchiverPath rD $NativeArchive $BridgeObject
    if ($LASTEXITCODE -ne 0) {
        throw "Bundled llvm-ar failed to append the Stark event bridge to '$NativeArchive'."
    }
    & $RanlibPath -D $NativeArchive
    if ($LASTEXITCODE -ne 0) {
        throw "Bundled llvm-ranlib failed to re-index GLFW archive '$NativeArchive'."
    }
    Assert-StaticArchive -Path $NativeArchive -Label "GLFW native library with precompiled event bridge"
    $archiveMembers = @(& $ArchiverPath t $NativeArchive)
    if ($LASTEXITCODE -ne 0 -or @($archiveMembers | Where-Object { $_ -ceq [System.IO.Path]::GetFileName($BridgeObject) }).Count -ne 1) {
        throw "GLFW native archive does not contain exactly one precompiled Stark event bridge."
    }
    $recordedBridgeCompileFlags = @($bridgeCompileFlags | ForEach-Object {
        ([string]$_).Replace($IncludeRoot, "<package-native-root>").Replace($repositoryRoot, "<checkout>")
    })

    return [ordered]@{
        compileFlags = [object[]]$recordedBridgeCompileFlags
        archiveFlags = "rD / ranlib -D"
        archiveMember = [System.IO.Path]::GetFileName($BridgeObject)
        eventBridgeCompiledIntoNativeArchive = $true
        perApplicationNativeSourceCompilation = $false
    }
}

function New-FileDescriptor {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path,
        [string] $Kind = ""
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Contribution file '$Path' does not exist."
    }
    $file = Get-Item -LiteralPath $Path -Force
    $value = [ordered]@{}
    if (-not [string]::IsNullOrWhiteSpace($Kind)) {
        $value.kind = $Kind
    }
    $value.path = Get-PortableRelativePath -Root $Root -Path $file.FullName -Label "GLFW contribution file"
    $value.bytes = [int64]$file.Length
    $value.sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    return $value
}

function Invoke-PackageInspection {
    param(
        [Parameter(Mandatory = $true)][string] $PackageImage,
        [Parameter(Mandatory = $true)][string] $OutputPath,
        [Parameter(Mandatory = $true)][string] $CompilerProjectPath
    )

    & dotnet run --project $CompilerProjectPath --no-restore -- inspect-pkg $PackageImage --format json -o $OutputPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw "Stage0 failed to inspect GLFW package image '$PackageImage'."
    }
    return (Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json)
}

$compilerProjectPath = Resolve-RepositoryPath -Path $CompilerProject
$vendorCatalogFullPath = Resolve-RepositoryPath -Path $VendorCatalogPath
$targetManifestFullPath = Resolve-RepositoryPath -Path $TargetManifestPath
$outputRoot = Resolve-RepositoryPath -Path $OutputVendorRoot
$stdlibPackageRoot = Resolve-RepositoryPath -Path $StdlibPackageDir
$toolchainRoot = Resolve-RepositoryPath -Path $ToolchainDir
$contributionPath = Resolve-RepositoryPath -Path $ContributionManifestPath
$cacheRoot = Resolve-RepositoryPath -Path $CacheDir
$repositoryPackageSource = Join-Path $repositoryRoot "vendor/src/Vendor/GLFW.stark"
$repositoryBridgeSource = Join-Path $repositoryRoot "vendor/GlfwEventBridge.c"

foreach ($requiredFile in @(
    $compilerProjectPath,
    $vendorCatalogFullPath,
    $targetManifestFullPath,
    $repositoryPackageSource,
    $repositoryBridgeSource,
    (Join-Path $repositoryRoot "tests/fixtures/release/GlfwBundledRuntimeSmoke.stark"))) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required GLFW release input '$requiredFile' does not exist."
    }
}
foreach ($requiredDirectory in @($stdlibPackageRoot, $toolchainRoot)) {
    if (-not (Test-Path -LiteralPath $requiredDirectory -PathType Container)) {
        throw "Required GLFW release input directory '$requiredDirectory' does not exist."
    }
}

Assert-SafeOutputRoot -Path $outputRoot
foreach ($inputPath in @(
    $compilerProjectPath,
    $vendorCatalogFullPath,
    $targetManifestFullPath,
    $stdlibPackageRoot,
    $toolchainRoot,
    $cacheRoot,
    $contributionPath)) {
    Assert-NoReparsePointPath -Path $inputPath
}
if (Test-IsSameOrDescendantPath -Path $contributionPath -Root $outputRoot) {
    throw "GLFW contribution manifest '$contributionPath' must be outside shared OutputVendorRoot '$outputRoot'."
}
if ((Test-IsSameOrDescendantPath -Path $cacheRoot -Root $outputRoot) -or
    (Test-IsSameOrDescendantPath -Path $outputRoot -Root $cacheRoot)) {
    throw "GLFW cache '$cacheRoot' and shared output '$outputRoot' must not overlap."
}
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null

$targetsManifest = Get-Content -LiteralPath $targetManifestFullPath -Raw | ConvertFrom-Json
$targetMatches = @((Get-ArrayValues -Value (Get-RequiredProperty -Object $targetsManifest -Name "targets")) | Where-Object {
    [string](Get-RequiredProperty -Object $_ -Name "assetSuffix") -ceq $AssetSuffix
})
if ($targetMatches.Count -ne 1) {
    throw "Release target manifest must define exactly one '$AssetSuffix' target."
}
$releaseTarget = $targetMatches[0]
$releaseEnabled = [bool](Get-RequiredProperty -Object $releaseTarget -Name "releaseEnabled")
$supportTier = [string](Get-RequiredProperty -Object $releaseTarget -Name "supportTier")
if ([string](Get-RequiredProperty -Object $releaseTarget -Name "targetTriple") -cne $TargetTriple) {
    throw "GLFW target '$AssetSuffix' does not exactly match '$TargetTriple'."
}
if ($releaseEnabled -and $AllowPlannedTarget) {
    throw "GLFW -AllowPlannedTarget is valid only for a release-disabled planned target."
}
if (-not $releaseEnabled -and (-not $AllowPlannedTarget -or $supportTier -cne "planned")) {
    throw "GLFW target '$AssetSuffix' is disabled; planned targets require -AllowPlannedTarget."
}
$hostFacts = Get-TargetHostFacts -Target $releaseTarget

$toolchainManifestPath = Join-Path $toolchainRoot "manifest.json"
if (-not (Test-Path -LiteralPath $toolchainManifestPath -PathType Leaf)) {
    throw "GLFW private backend '$toolchainRoot' has no manifest.json."
}
$toolchainManifest = Get-Content -LiteralPath $toolchainManifestPath -Raw | ConvertFrom-Json
if ([int](Get-RequiredProperty -Object $toolchainManifest -Name "schemaVersion") -ne 2 -or
    [string](Get-RequiredProperty -Object $toolchainManifest -Name "payloadKind") -cne "stark-compiler-private-backend" -or
    [string](Get-RequiredProperty -Object $toolchainManifest -Name "assetSuffix") -cne $AssetSuffix) {
    throw "GLFW private backend is not the matching schema-2 Stark backend closure."
}
$requiredTools = @((Get-ArrayValues -Value (Get-RequiredProperty -Object $toolchainManifest -Name "requiredTools")) | ForEach-Object { [string]$_ })
$expectedToolNames = if ($IsWindows) { @("bin/clang.exe", "bin/llvm-ar.exe", "bin/llvm-ranlib.exe") } else { @("bin/clang", "bin/llvm-ar", "bin/llvm-ranlib") }
foreach ($expectedTool in $expectedToolNames) {
    if ($requiredTools -cnotcontains $expectedTool) {
        throw "GLFW private backend manifest does not declare required tool '$expectedTool'."
    }
}
$clangPath = Get-ToolPath -ToolchainRoot $toolchainRoot -Name "clang"
$archiverPath = Get-ToolPath -ToolchainRoot $toolchainRoot -Name "llvm-ar"
$ranlibPath = Get-ToolPath -ToolchainRoot $toolchainRoot -Name "llvm-ranlib"

$vendorCatalog = Get-Content -LiteralPath $vendorCatalogFullPath -Raw | ConvertFrom-Json
$catalogMatches = @((Get-ArrayValues -Value (Get-RequiredProperty -Object $vendorCatalog -Name "packages")) | Where-Object {
    [string](Get-RequiredProperty -Object $_ -Name "id") -ceq $packageId
})
if ($catalogMatches.Count -ne 1) {
    throw "Official Vendor catalog must contain exactly one '$packageId' entry."
}
$catalogPackage = $catalogMatches[0]
if ([string](Get-RequiredProperty -Object $catalogPackage -Name "buildRecipe") -cne $recipePath) {
    throw "Official Vendor catalog must name '$recipePath' as the GLFW build recipe."
}
if ([string](Get-RequiredProperty -Object $catalogPackage -Name "version") -cne "3.4" -or
    [string](Get-RequiredProperty -Object $catalogPackage -Name "sourceIdentity") -cne "tag:3.4") {
    throw "Official Vendor catalog GLFW version/source identity is not the pinned 3.4 release."
}
$catalogModules = @((Get-ArrayValues -Value (Get-RequiredProperty -Object $catalogPackage -Name "modules")) | ForEach-Object { [string]$_ })
if (($catalogModules -join "`n") -cne "Vendor.GLFW") {
    throw "Official Vendor catalog GLFW module ownership must contain only Vendor.GLFW."
}
$support = [string](Get-RequiredProperty -Object (Get-RequiredProperty -Object $catalogPackage -Name "targetSupport") -Name $AssetSuffix)
$expectedSupport = if ($hostFacts.OperatingSystem -ceq "macos") { "required-binary" } else { "required-source-build" }
if ($support -cne $expectedSupport) {
    throw "Official Vendor catalog GLFW support '$support' does not match required '$expectedSupport' for '$AssetSuffix'."
}

$systemLinkFacts = Get-RequiredProperty -Object $catalogPackage -Name "systemLinkFacts"
$systemLinks = @((Get-ArrayValues -Value (Get-RequiredProperty -Object $systemLinkFacts -Name $hostFacts.OperatingSystem)) | ForEach-Object { [string]$_ })
$expectedSystemLinks = if ($hostFacts.OperatingSystem -ceq "linux") {
    @("pthread", "dl", "rt", "m")
} elseif ($hostFacts.OperatingSystem -ceq "windows") {
    @("gdi32", "user32", "shell32")
} else {
    @("Cocoa", "IOKit", "CoreFoundation")
}
if (($systemLinks -join "`n") -cne ($expectedSystemLinks -join "`n")) {
    throw "Official Vendor catalog GLFW system link facts for '$($hostFacts.OperatingSystem)' are not exact."
}

$sourceInput = Get-RequiredProperty -Object $catalogPackage -Name "sourceInput"
$sourceBuildOptions = Get-RequiredProperty -Object $catalogPackage -Name "sourceBuildOptions"
if ([string](Get-RequiredProperty -Object $sourceBuildOptions -Name "configuration") -cne "release" -or
    [string](Get-RequiredProperty -Object $sourceBuildOptions -Name "optimization") -cne "O3" -or
    [string](Get-RequiredProperty -Object $sourceBuildOptions -Name "lto") -cne "thin" -or
    -not [bool](Get-RequiredProperty -Object $sourceBuildOptions -Name "deterministicArchive") -or
    [string](Get-RequiredProperty -Object $sourceBuildOptions -Name "toolchain") -cne "bundled-llvm" -or
    [string](Get-RequiredProperty -Object $sourceBuildOptions -Name "linuxWindowSystem") -cne "x11-only" -or
    [bool](Get-RequiredProperty -Object $sourceBuildOptions -Name "wayland") -or
    [string](Get-RequiredProperty -Object $sourceBuildOptions -Name "eventBridge") -cne "compiled-into-native-archive" -or
    [bool](Get-RequiredProperty -Object $sourceBuildOptions -Name "perApplicationNativeSourceCompilation")) {
    throw "Official Vendor catalog GLFW source build options are not the required release/O3/ThinLTO/deterministic bundled-LLVM contract."
}

$assetDescriptor = $sourceInput
if ($hostFacts.OperatingSystem -ceq "macos") {
    $binaryMatches = @((Get-ArrayValues -Value (Get-RequiredProperty -Object $catalogPackage -Name "binaryInputs")) | Where-Object {
        [string](Get-RequiredProperty -Object $_ -Name "target") -ceq $AssetSuffix
    })
    if ($binaryMatches.Count -ne 1) {
        throw "Official Vendor catalog must define exactly one $AssetSuffix GLFW binary input."
    }
    $assetDescriptor = $binaryMatches[0]
}

$systemCandidates = @()
foreach ($systemImage in (Get-ChildItem -LiteralPath $stdlibPackageRoot -File -Recurse -Filter "*.starkpkg")) {
    $inspectionPath = Join-Path $cacheRoot ("system-" + [Guid]::NewGuid().ToString("N") + ".json")
    try {
        $inspection = Invoke-PackageInspection -PackageImage $systemImage.FullName -OutputPath $inspectionPath -CompilerProjectPath $compilerProjectPath
        if ([string](Get-RequiredProperty -Object $inspection -Name "RootModule") -ceq "System") {
            $systemCandidates += $inspection
        }
    } finally {
        if (Test-Path -LiteralPath $inspectionPath) {
            Remove-Item -LiteralPath $inspectionPath -Force
        }
    }
}
if ($systemCandidates.Count -ne 1) {
    throw "Staged standard-library directory must contain exactly one System package; found $($systemCandidates.Count)."
}
$systemInspection = $systemCandidates[0]
$systemTarget = Get-RequiredProperty -Object $systemInspection -Name "Target"
$systemProfile = Get-RequiredProperty -Object $systemInspection -Name "BuildProfile"
if ([string](Get-RequiredProperty -Object $systemTarget -Name "Triple") -cne $TargetTriple -or
    [string]::IsNullOrWhiteSpace([string](Get-RequiredProperty -Object $systemTarget -Name "DataLayout")) -or
    [string](Get-RequiredProperty -Object $systemProfile -Name "Name") -cne "release") {
    throw "Staged System package does not preserve exact GLFW target/data-layout/release-profile facts."
}
$systemIdentity = Get-RequiredProperty -Object $systemInspection -Name "Identity"

$operationToken = [Guid]::NewGuid().ToString("N")
$workRoot = Join-Path $cacheRoot "work/$AssetSuffix-$operationToken"
$extractRoot = Join-Path $workRoot "extract"
$objectRoot = Join-Path $workRoot "objects"
$targetDist = Join-Path $outputRoot "dist/$AssetSuffix"
$stagedSourceRoot = Join-Path $outputRoot "src"
$stagedPackageSource = Join-Path $outputRoot "src/Vendor/GLFW.stark"
$stagedBridge = Join-Path $workRoot "GlfwEventBridge.c"
$legacyStagedBridge = Join-Path $targetDist "GlfwEventBridge.c"
$nativeRoot = Join-Path $targetDist "native/glfw"
$nativeLibraryRoot = Join-Path $nativeRoot "lib"
$nativeLibraryFileName = if ($IsWindows) { "glfw3.lib" } else { "libglfw3.a" }
$nativeLibraryPath = Join-Path $nativeLibraryRoot $nativeLibraryFileName
$bridgeObjectFileName = if ($IsWindows) { "stark_glfw_event_bridge.obj" } else { "stark_glfw_event_bridge.o" }
$bridgeObjectPath = Join-Path $objectRoot $bridgeObjectFileName
$packageLibraryFileName = if ($IsWindows) { "VendorGLFW.lib" } else { "libVendorGLFW.a" }
$packageLibraryPath = Join-Path $targetDist $packageLibraryFileName
$packageImagePath = [System.IO.Path]::ChangeExtension($packageLibraryPath, ".starkpkg")
$licenseRoot = Join-Path $outputRoot "licenses/GLFW"
$separateLicensePath = Join-Path $licenseRoot "LICENSE.md"
$runtimeSmokeSourcePath = Join-Path $repositoryRoot "tests/fixtures/release/GlfwBundledRuntimeSmoke.stark"

foreach ($ownedPath in @(
    $stagedPackageSource,
    $legacyStagedBridge,
    $nativeRoot,
    $packageLibraryPath,
    $packageImagePath,
    $licenseRoot)) {
    Remove-OwnedPath -Root $outputRoot -Path $ownedPath
}

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
try {
    $archivePath = Get-OrDownloadPinnedArchive -Descriptor $assetDescriptor -CacheRoot (Join-Path $cacheRoot "downloads")
    Expand-CheckedZip -ArchivePath $archivePath -Destination $extractRoot

    Copy-RequiredFile -Source $repositoryPackageSource -Destination $stagedPackageSource
    Copy-RequiredFile -Source $repositoryBridgeSource -Destination $stagedBridge
    New-Item -ItemType Directory -Force -Path (Join-Path $nativeRoot "GLFW") | Out-Null
    New-Item -ItemType Directory -Force -Path $nativeLibraryRoot | Out-Null

    $nativeBuild = $null
    $extractedLicense = ""
    if ($hostFacts.OperatingSystem -ceq "macos") {
        $archiveRoot = [string](Get-RequiredProperty -Object $assetDescriptor -Name "archiveRoot")
        $binaryRoot = Join-Path $extractRoot $archiveRoot
        Copy-RequiredFile -Source (Join-Path $binaryRoot "include/GLFW/glfw3.h") -Destination (Join-Path $nativeRoot "GLFW/glfw3.h")
        Copy-RequiredFile -Source (Join-Path $binaryRoot "include/GLFW/glfw3native.h") -Destination (Join-Path $nativeRoot "GLFW/glfw3native.h")
        $extractedLicense = Join-Path $binaryRoot "LICENSE.md"
        Copy-RequiredFile -Source $extractedLicense -Destination (Join-Path $nativeRoot "LICENSE.md")
        Select-DarwinArchiveSlice `
            -UniversalArchive (Join-Path $binaryRoot "lib-universal/libglfw3.a") `
            -OutputArchive $nativeLibraryPath `
            -Architecture $hostFacts.Architecture
        $bridgeBuild = Add-GlfwBridgeToNativeArchive `
            -BridgeSource $stagedBridge `
            -BridgeObject $bridgeObjectPath `
            -IncludeRoot $nativeRoot `
            -NativeArchive $nativeLibraryPath `
            -ClangPath $clangPath `
            -ArchiverPath $archiverPath `
            -RanlibPath $ranlibPath
        $nativeBuild = [ordered]@{
            mode = "reviewed-pinned-binary"
            selectedArchitecture = $hostFacts.Architecture
            sourceArchive = [ordered]@{
                name = [string](Get-RequiredProperty -Object $assetDescriptor -Name "name")
                url = [string](Get-RequiredProperty -Object $assetDescriptor -Name "url")
                bytes = [int64](Get-RequiredProperty -Object $assetDescriptor -Name "size")
                sha256 = [string](Get-RequiredProperty -Object $assetDescriptor -Name "sha256")
            }
            eventBridge = $bridgeBuild
            optimizationRationale = "The reviewed upstream GLFW 3.4 macOS release archive is already optimized native code. The contributor selects only its $($hostFacts.Architecture) archive slice, avoiding the unrelated architecture payload while preserving upstream code generation, then compiles the Stark event bridge once at -O3 and appends it deterministically. Applications never rebuild native bridge source."
        }
    } else {
        $stripPrefix = [string](Get-RequiredProperty -Object $sourceInput -Name "stripPrefix")
        $sourceRoot = Join-Path $extractRoot $stripPrefix
        Copy-RequiredFile -Source (Join-Path $sourceRoot "include/GLFW/glfw3.h") -Destination (Join-Path $nativeRoot "GLFW/glfw3.h")
        Copy-RequiredFile -Source (Join-Path $sourceRoot "include/GLFW/glfw3native.h") -Destination (Join-Path $nativeRoot "GLFW/glfw3native.h")
        $extractedLicense = Join-Path $sourceRoot "LICENSE.md"
        Copy-RequiredFile -Source $extractedLicense -Destination (Join-Path $nativeRoot "LICENSE.md")
        $nativeBuild = Invoke-GlfwSourceBuild `
            -SourceRoot $sourceRoot `
            -ObjectRoot $objectRoot `
            -OutputLibrary $nativeLibraryPath `
            -BridgeSource $stagedBridge `
            -BridgeObject $bridgeObjectPath `
            -OperatingSystem $hostFacts.OperatingSystem `
            -ClangPath $clangPath `
            -ArchiverPath $archiverPath `
            -RanlibPath $ranlibPath
        $nativeBuild["sourceArchive"] = [ordered]@{
            name = [string](Get-RequiredProperty -Object $sourceInput -Name "name")
            url = [string](Get-RequiredProperty -Object $sourceInput -Name "url")
            bytes = [int64](Get-RequiredProperty -Object $sourceInput -Name "size")
            sha256 = [string](Get-RequiredProperty -Object $sourceInput -Name "sha256")
        }
    }

    $licenseEvidencePaths = @((Get-ArrayValues -Value (Get-RequiredProperty -Object $catalogPackage -Name "licenseEvidencePaths")) | ForEach-Object { Resolve-RepositoryPath -Path ([string]$_) })
    if ($licenseEvidencePaths.Count -eq 0) {
        throw "Official Vendor catalog GLFW entry has no license evidence path."
    }
    $licenseSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $extractedLicense).Hash.ToLowerInvariant()
    foreach ($evidencePath in $licenseEvidencePaths) {
        if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf) -or
            (Get-FileHash -Algorithm SHA256 -LiteralPath $evidencePath).Hash.ToLowerInvariant() -cne $licenseSha256) {
            throw "Pinned GLFW archive license does not match repository evidence '$evidencePath'."
        }
    }
    Copy-RequiredFile -Source $extractedLicense -Destination $separateLicensePath
    [System.IO.File]::WriteAllText(
        (Join-Path $nativeRoot "VERSION.md"),
        "GLFW 3.4`nsourceIdentity tag:3.4`n",
        [System.Text.UTF8Encoding]::new($false))

    $nativeLibraries = @("glfw3")
    $nativeLinkArguments = @()
    if ($hostFacts.OperatingSystem -ceq "macos") {
        foreach ($framework in $systemLinks) {
            $nativeLinkArguments += @("-framework", $framework)
        }
    } else {
        $nativeLibraries += $systemLinks
    }

    # The transactional orchestrator gives its staging root a fresh GUID. The
    # Stark object records its source path, so compile the verified repository
    # input rather than the byte-identical staged copy; this keeps package
    # bytes stable across contributor output roots. The staged copy remains a
    # shipped source payload and is covered by the aggregate file inventory.
    $compilerArguments = @(
        "run", "--project", $compilerProjectPath, "--no-restore", "--",
        $repositoryPackageSource,
        "--emit-lib",
        "--no-stark-path",
        "-I", $stagedSourceRoot,
        "-I", $stdlibPackageRoot,
        "-o", $packageLibraryPath,
        "--target", $TargetTriple,
        "--package-profile", "release",
        "--toolchain-dir", $toolchainRoot,
        "--native-include-dir", $nativeRoot,
        "--native-library-dir", $nativeLibraryRoot
    )
    foreach ($library in $nativeLibraries) {
        $compilerArguments += @("--native-library", $library)
    }
    foreach ($linkArgument in $nativeLinkArguments) {
        $compilerArguments += @("--native-link-arg", $linkArgument)
    }
    & dotnet @compilerArguments
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $packageLibraryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $packageImagePath -PathType Leaf)) {
        throw "Stage0 failed to build release GLFW package for '$TargetTriple'."
    }
    Assert-StaticArchive -Path $packageLibraryPath -Label "Vendor.GLFW package library"

    $inspectionPath = Join-Path $workRoot "VendorGLFW.starkpkg.json"
    $inspection = Invoke-PackageInspection -PackageImage $packageImagePath -OutputPath $inspectionPath -CompilerProjectPath $compilerProjectPath
    if ([string](Get-RequiredProperty -Object $inspection -Name "RootModule") -cne $packageId -or
        [string](Get-RequiredProperty -Object $inspection -Name "LibraryFileName") -cne $packageLibraryFileName) {
        throw "Generated GLFW package identity/archive name is invalid."
    }
    $modules = @((Get-ArrayValues -Value (Get-RequiredProperty -Object $inspection -Name "Modules")) | ForEach-Object { [string](Get-RequiredProperty -Object $_ -Name "ModuleName") })
    $modules = @(Sort-StringsOrdinal -Values $modules)
    if (($modules -join "`n") -cne "Vendor.GLFW") {
        throw "Generated GLFW package module ownership is '$($modules -join ', ')'."
    }
    $packageTarget = Get-RequiredProperty -Object $inspection -Name "Target"
    $packageProfile = Get-RequiredProperty -Object $inspection -Name "BuildProfile"
    if ([string](Get-RequiredProperty -Object $packageTarget -Name "Triple") -cne $TargetTriple -or
        [string]::IsNullOrWhiteSpace([string](Get-RequiredProperty -Object $packageTarget -Name "DataLayout")) -or
        [string](Get-RequiredProperty -Object $packageProfile -Name "Name") -cne "release") {
        throw "Generated GLFW package does not preserve exact target/data-layout/release-profile facts."
    }
    $identity = Get-RequiredProperty -Object $inspection -Name "Identity"
    if ([string](Get-RequiredProperty -Object $identity -Name "PackageId") -cne $packageId) {
        throw "Generated GLFW package lost explicit package identity."
    }
    $identityDependencies = @(Get-ArrayValues -Value (Get-RequiredProperty -Object $identity -Name "Dependencies"))
    if ($identityDependencies.Count -ne 1 -or
        [string](Get-RequiredProperty -Object $identityDependencies[0] -Name "PackageId") -cne "System" -or
        [string](Get-RequiredProperty -Object $identityDependencies[0] -Name "ApiHash") -cne [string](Get-RequiredProperty -Object $systemIdentity -Name "ApiHash") -or
        [string](Get-RequiredProperty -Object $identityDependencies[0] -Name "ContentHash") -cne [string](Get-RequiredProperty -Object $systemIdentity -Name "ContentHash")) {
        throw "Generated GLFW package dependency identity does not exactly match staged System."
    }

    $nativeDependencies = Get-RequiredProperty -Object $inspection -Name "NativeDependencies"
    $actualSources = @((Get-ArrayValues -Value (Get-OptionalProperty -Object $nativeDependencies -Name "Sources")) | ForEach-Object { [string]$_ })
    $actualIncludes = @((Get-ArrayValues -Value (Get-OptionalProperty -Object $nativeDependencies -Name "IncludeDirectories")) | ForEach-Object { [string]$_ })
    $actualLibraryDirectories = @((Get-ArrayValues -Value (Get-OptionalProperty -Object $nativeDependencies -Name "LibraryDirectories")) | ForEach-Object { [string]$_ })
    $actualLibraries = @((Get-ArrayValues -Value (Get-OptionalProperty -Object $nativeDependencies -Name "Libraries")) | ForEach-Object { [string]$_ })
    $actualLinkArguments = @((Get-ArrayValues -Value (Get-OptionalProperty -Object $nativeDependencies -Name "LinkArguments")) | ForEach-Object { [string]$_ })
    $actualPkgConfig = @(Get-ArrayValues -Value (Get-OptionalProperty -Object $nativeDependencies -Name "PkgConfigPackages"))
    if ($actualSources.Count -ne 0 -or
        ($actualIncludes -join "`n") -cne "native/glfw" -or
        ($actualLibraryDirectories -join "`n") -cne "native/glfw/lib" -or
        ($actualLibraries -join "`n") -cne ($nativeLibraries -join "`n") -or
        ($actualLinkArguments -join "`n") -cne ($nativeLinkArguments -join "`n") -or
        $actualPkgConfig.Count -ne 0) {
        throw "Generated GLFW package native metadata does not exactly preserve SDK-local sources/directories/logical libraries/link arguments."
    }

    $runtimeSmokeFileName = if ($IsWindows) { "glfw-bundled-runtime-smoke.exe" } else { "glfw-bundled-runtime-smoke" }
    $runtimeSmokePath = Join-Path $workRoot $runtimeSmokeFileName
    $runtimeSmokeArguments = @(
        "run", "--project", $compilerProjectPath, "--no-restore", "--",
        $runtimeSmokeSourcePath,
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
        throw "Stage0 failed to link the bundled GLFW runtime smoke for '$TargetTriple'."
    }
    & $runtimeSmokePath
    $runtimeSmokeExitCode = $LASTEXITCODE
    if ($runtimeSmokeExitCode -ne 0) {
        throw "Bundled GLFW runtime smoke failed for '$TargetTriple' with exit code $runtimeSmokeExitCode."
    }

    $provenanceValue = [ordered]@{
        schemaVersion = 1
        packageId = $packageId
        version = "3.4"
        sourceIdentity = "tag:3.4"
        upstreamUrl = [string](Get-RequiredProperty -Object $catalogPackage -Name "upstreamUrl")
        license = [string](Get-RequiredProperty -Object $catalogPackage -Name "license")
        buildRecipe = $recipePath
        target = [ordered]@{
            id = $AssetSuffix
            targetTriple = $TargetTriple
            operatingSystem = $hostFacts.OperatingSystem
            architecture = $hostFacts.Architecture
            packageProfile = "release"
        }
        stagedSystemIdentity = [ordered]@{
            packageId = "System"
            apiHash = [string](Get-RequiredProperty -Object $systemIdentity -Name "ApiHash")
            contentHash = [string](Get-RequiredProperty -Object $systemIdentity -Name "ContentHash")
        }
        nativeBuild = $nativeBuild
        nativeLibrary = New-FileDescriptor -Root $outputRoot -Path $nativeLibraryPath
        systemLinkFacts = [object[]]$systemLinks
        buildHostPrerequisites = [object[]]@(if ($hostFacts.OperatingSystem -ceq "linux") {
            Get-ArrayValues -Value (Get-RequiredProperty -Object $sourceBuildOptions -Name "linuxBuildHostPrerequisites")
        } else {
            "platform SDK headers supplied by the native release runner"
        })
        emittedNativeFacts = [ordered]@{
            sources = [object[]]$actualSources
            includeDirectories = [object[]]$actualIncludes
            libraryDirectories = [object[]]$actualLibraryDirectories
            libraries = [object[]]$actualLibraries
            linkArguments = [object[]]$actualLinkArguments
            pkgConfigPackages = [object[]]$actualPkgConfig
        }
        runtimeSmoke = [ordered]@{
            fixture = "tests/fixtures/release/GlfwBundledRuntimeSmoke.stark"
            exitCode = $runtimeSmokeExitCode
            upstreamVersion = "3.4"
            eventBridgeClearAndDroppedCount = "invoked-and-zero"
        }
        packageIdentity = [ordered]@{
            apiHash = [string](Get-RequiredProperty -Object $identity -Name "ApiHash")
            contentHash = [string](Get-RequiredProperty -Object $identity -Name "ContentHash")
            dependencies = [object[]]@($identityDependencies | ForEach-Object {
                [ordered]@{
                    packageId = [string](Get-RequiredProperty -Object $_ -Name "PackageId")
                    apiHash = [string](Get-RequiredProperty -Object $_ -Name "ApiHash")
                    contentHash = [string](Get-RequiredProperty -Object $_ -Name "ContentHash")
                }
            })
        }
        inputs = [ordered]@{
            starkSource = New-FileDescriptor -Root $repositoryRoot -Path $repositoryPackageSource
            eventBridge = New-FileDescriptor -Root $repositoryRoot -Path $repositoryBridgeSource
            licenseSha256 = $licenseSha256
        }
    }
    $provenancePath = Join-Path $nativeRoot "PROVENANCE.json"
    Write-DeterministicJson -Value $provenanceValue -Path $provenancePath -Depth 20

    $artifactDescriptors = @()
    foreach ($nativeFilePath in (Sort-StringsOrdinal -Values @(
        Get-ChildItem -LiteralPath $nativeRoot -File -Recurse | ForEach-Object { $_.FullName }
    ))) {
        $nativeFile = Get-Item -LiteralPath $nativeFilePath
        $kind = if ($nativeFile.Name -match '(?i)^(LICENSE|LICENCE|COPYING|NOTICE)(\..*)?$') {
            "license"
        } elseif ($nativeFile.Name -ceq "PROVENANCE.json") {
            "provenance"
        } elseif ($nativeFile.Extension -in @(".h", ".hpp")) {
            "header"
        } elseif ($nativeFile.Extension -in @(".a", ".lib")) {
            "static-library"
        } else {
            "documentation"
        }
        $artifactDescriptors += New-FileDescriptor -Root $outputRoot -Path $nativeFile.FullName -Kind $kind
    }
    $artifactDescriptors = @(Sort-ObjectsOrdinalByProperty -Values $artifactDescriptors -PropertyName "path")
    $licenseDescriptors = @(
        (New-FileDescriptor -Root $outputRoot -Path (Join-Path $nativeRoot "LICENSE.md")),
        (New-FileDescriptor -Root $outputRoot -Path $separateLicensePath)
    )
    $licenseDescriptors = @(Sort-ObjectsOrdinalByProperty -Values $licenseDescriptors -PropertyName "path")
    $provenanceDescriptor = New-FileDescriptor -Root $outputRoot -Path $provenancePath

    $packageEntry = [ordered]@{
        id = $packageId
        version = "3.4"
        sourceIdentity = "tag:3.4"
        target = [ordered]@{ id = $AssetSuffix; targetTriple = $TargetTriple }
        package = [ordered]@{
            rootModule = $packageId
            image = Get-PortableRelativePath -Root $outputRoot -Path $packageImagePath -Label "GLFW package image"
            imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $packageImagePath).Hash.ToLowerInvariant()
            library = Get-PortableRelativePath -Root $outputRoot -Path $packageLibraryPath -Label "GLFW package library"
            librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $packageLibraryPath).Hash.ToLowerInvariant()
            modules = [object[]]$modules
        }
        nativePayload = [ordered]@{
            artifacts = [object[]]$artifactDescriptors
            licenseFiles = [object[]]$licenseDescriptors
        }
        provenance = $provenanceDescriptor
    }
    $contribution = [ordered]@{
        schemaVersion = 1
        targetId = $AssetSuffix
        targetTriple = $TargetTriple
        packages = @($packageEntry)
    }
    Write-DeterministicJson -Value $contribution -Path $contributionPath -Depth 18

    Write-Host "Prepared hermetic GLFW 3.4 contribution for '$AssetSuffix'."
    Write-Host "Contribution manifest: $contributionPath"
} finally {
    if (Test-Path -LiteralPath $workRoot) {
        Assert-NoReparsePointPath -Path $workRoot
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
