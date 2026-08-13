param(
    [Parameter(Mandatory = $true)]
    [string] $SdkRoot,

    [string] $OutputPath = "",

    [string] $PolicyPath = "",

    [string] $ReleaseToolsPath = "",

    [string] $DotNetPath = "dotnet",

    [switch] $AllowUninspectable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-CapturedTool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FileName,

        [string[]] $Arguments = @(),

        [int[]] $AllowedExitCodes = @(0)
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start native dependency inspection tool '$FileName'."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($AllowedExitCodes -notcontains $process.ExitCode) {
        throw "Native dependency inspection tool '$FileName' exited with $($process.ExitCode): $stderr"
    }

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Resolve-RequiredTool {
    param([string[]] $Names)

    foreach ($name in $Names) {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
    }

    throw "Required native dependency inspection tool is missing. Tried: $($Names -join ', ')."
}

function Get-StagedCandidateIdentity {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $TargetId,
        [string] $RequestedReleaseTools = "",
        [string] $RequestedDotNet = "dotnet"
    )

    $dotnet = Resolve-RequiredTool -Names @($RequestedDotNet)
    $inspector = @(& (Join-Path $PSScriptRoot "resolve-release-tools.ps1") `
        -RepositoryRoot (Join-Path $PSScriptRoot "..") `
        -DotNetPath $dotnet `
        -ReleaseToolsPath $RequestedReleaseTools) | Select-Object -Last 1
    $inspection = Invoke-CapturedTool `
        -FileName $dotnet `
        -Arguments @($inspector, "inspect-candidate", "--sdk-root", $Root, "--target-id", $TargetId)
    try {
        $identity = $inspection.Stdout | ConvertFrom-Json -Depth 30
    } catch {
        throw "Staged release candidate identity inspector returned invalid JSON: $($_.Exception.Message)"
    }
    if ($null -eq $identity -or
        [string]$identity.kind -cne "stark-staged-release-validation-subject" -or
        [int]$identity.schemaVersion -ne 1) {
        throw "Staged release candidate identity inspector returned an unsupported identity."
    }
    return $identity
}

function Test-IsInsideRoot {
    param(
        [string] $Path,
        [string] $Root
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Path]::IsPathRooted($Path)) {
        return $false
    }

    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    $rootPath = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Root))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    return [string]::Equals($candidate, $rootPath, $comparison) `
        -or $candidate.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Convert-ToRelativePath {
    param(
        [string] $Path,
        [string] $Root
    )

    return [System.IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Test-SafeLoaderTokenPath {
    param(
        [string] $Path,
        [string] $Token,
        [bool] $AllowExact,
        [string] $OriginDirectory = "",
        [string] $Root = "",
        [switch] $AllowParentTraversal
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path.Contains('\') -or
        $Path.IndexOf([char]0) -ge 0) {
        return $false
    }

    if ([string]::Equals($Path, $Token, [StringComparison]::Ordinal)) {
        if (-not $AllowExact) {
            return $false
        }
        if (-not $AllowParentTraversal) {
            return $true
        }
        return -not [string]::IsNullOrWhiteSpace($OriginDirectory) `
            -and -not [string]::IsNullOrWhiteSpace($Root) `
            -and (Test-IsInsideRoot -Path $OriginDirectory -Root $Root)
    }
    if (-not $Path.StartsWith($Token + "/", [StringComparison]::Ordinal)) {
        return $false
    }

    $suffix = $Path.Substring($Token.Length + 1)
    foreach ($segment in $suffix.Split('/')) {
        if ($segment -eq "" -or
            (-not $AllowParentTraversal -and $segment -in @(".", ".."))) {
            return $false
        }
    }

    if ($AllowParentTraversal) {
        if ([string]::IsNullOrWhiteSpace($OriginDirectory) -or
            [string]::IsNullOrWhiteSpace($Root)) {
            return $false
        }
        $candidate = [System.IO.Path]::GetFullPath((Join-Path $OriginDirectory $suffix))
        return Test-IsInsideRoot -Path $candidate -Root $Root
    }
    return $true
}

function Test-AllowedMacPath {
    param(
        [string] $Path,
        [string] $Root,
        [string] $LoaderOriginDirectory,
        [string] $ExecutableOriginDirectory = ""
    )

    if ((Test-SafeLoaderTokenPath -Path $Path -Token "@rpath" -AllowExact $false) -or
        (Test-SafeLoaderTokenPath -Path $Path -Token "@loader_path" -AllowExact $true `
            -OriginDirectory $LoaderOriginDirectory -Root $Root -AllowParentTraversal) -or
        (Test-SafeLoaderTokenPath -Path $Path -Token "@executable_path" -AllowExact $true `
            -OriginDirectory $ExecutableOriginDirectory -Root $Root -AllowParentTraversal)) {
        return $true
    }
    if (-not [System.IO.Path]::IsPathRooted($Path)) {
        return $false
    }

    $candidate = [System.IO.Path]::GetFullPath($Path)
    return $candidate.StartsWith("/usr/lib/", [StringComparison]::Ordinal) `
        -or $candidate.StartsWith("/System/Library/", [StringComparison]::Ordinal) `
        -or (Test-IsInsideRoot -Path $candidate -Root $Root)
}

function Test-AllowedLinuxSearchPath {
    param(
        [string] $Path,
        [string] $Root,
        [string] $OriginDirectory
    )

    if ((Test-SafeLoaderTokenPath -Path $Path -Token '$ORIGIN' -AllowExact $true `
            -OriginDirectory $OriginDirectory -Root $Root -AllowParentTraversal) -or
        (Test-SafeLoaderTokenPath -Path $Path -Token '${ORIGIN}' -AllowExact $true `
            -OriginDirectory $OriginDirectory -Root $Root -AllowParentTraversal)) {
        return $true
    }
    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path.Contains('\') -or
        $Path.IndexOf([char]0) -ge 0 -or
        -not [System.IO.Path]::IsPathRooted($Path)) {
        return $false
    }

    $candidate = [System.IO.Path]::GetFullPath($Path)
    return $candidate -match '^/(usr/)?lib(32|64|x32)?(/|$)' `
        -or (Test-IsInsideRoot -Path $candidate -Root $Root)
}

function Test-IsWindowsSystemLibrary {
    param([string] $Name)

    if ($Name -match '^(api|ext)-ms-win-[A-Za-z0-9_.-]+\.dll$') {
        return $true
    }

    $systemLibraries = @(
        "advapi32.dll", "bcrypt.dll", "cfgmgr32.dll", "comctl32.dll",
        "comdlg32.dll", "crypt32.dll", "d3d11.dll", "dbghelp.dll",
        "dinput8.dll", "dwmapi.dll", "dxgi.dll", "gdi32.dll",
        "imm32.dll", "iphlpapi.dll", "kernel32.dll", "msvcp140.dll", "msvcrt.dll",
        "ntdll.dll", "ole32.dll", "oleaut32.dll", "opengl32.dll",
        "powrprof.dll", "rpcrt4.dll", "secur32.dll", "setupapi.dll",
        "shell32.dll", "shlwapi.dll", "sspicli.dll", "ucrtbase.dll", "user32.dll",
        "userenv.dll", "version.dll", "vcruntime140.dll",
        "vcruntime140_1.dll", "winmm.dll", "ws2_32.dll"
    )

    return $systemLibraries -contains $Name.ToLowerInvariant()
}

function Test-IsLinuxSystemLibrary {
    param([string] $Name)

    return $Name -match '^(ld-linux[^/]*|lib(c|m|dl|pthread|rt|resolv|util|gcc_s|stdc\+\+|atomic|unwind)\.so(\..*)?)$' `
        -or $Name -in @("libz.so.1", "libxml2.so.2")
}

function Assert-SafePolicyRelativePath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Context
    )

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or [System.IO.Path]::IsPathRooted($Path) `
        -or $Path.Contains('\') `
        -or $Path.Contains(':') `
        -or $Path.IndexOf([char]0) -ge 0 `
        -or $Path -match '(^|/)\.\.?(/|$)') {
        throw "$Context '$Path' is not a safe canonical relative path."
    }
}

function Get-ApprovedNativeRoots {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Native binary policy '$Path' does not exist."
    }
    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ([int]$document.schemaVersion -ne 1) {
        throw "Native binary policy requires archive-content.json schemaVersion 1."
    }
    $roots = @($document.nativeBinaryPolicy.approvedRoots)
    if ($roots.Count -eq 0) {
        throw "Native binary policy has no approved roots."
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($rootValue in $roots) {
        $approvedRoot = [string]$rootValue
        Assert-SafePolicyRelativePath -Path $approvedRoot -Context "Approved native binary root"
        if (-not $seen.Add($approvedRoot)) {
            throw "Native binary policy duplicates or case-collides approved root '$approvedRoot'."
        }
    }
    return @($roots | ForEach-Object { [string]$_ })
}

function Test-IsApprovedNativePayloadPath {
    param(
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [Parameter(Mandatory = $true)][string[]] $ApprovedRoots
    )

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    foreach ($approvedRoot in $ApprovedRoots) {
        if ([string]::Equals($RelativePath, $approvedRoot, $comparison) `
            -or $RelativePath.StartsWith($approvedRoot + "/", $comparison)) {
            return $true
        }
    }
    return $false
}

function Test-IsManagedPortableExecutable {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo] $File)

    $stream = [System.IO.File]::OpenRead($File.FullName)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 128 -or $reader.ReadUInt16() -ne 0x5a4d) {
            return $false
        }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or [int64]$peOffset + 24 -ge $stream.Length) {
            return $false
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            return $false
        }

        $optionalHeader = [int64]$peOffset + 24
        $stream.Position = $optionalHeader
        $magic = $reader.ReadUInt16()
        $dataDirectoryOffset = if ($magic -eq 0x10b) {
            96
        } elseif ($magic -eq 0x20b) {
            112
        } else {
            return $false
        }
        $cliDirectory = $optionalHeader + $dataDirectoryOffset + (14 * 8)
        if ($cliDirectory + 8 -gt $stream.Length) {
            return $false
        }
        $stream.Position = $cliDirectory
        return $reader.ReadUInt32() -ne 0 -and $reader.ReadUInt32() -ne 0
    } catch {
        return $false
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-NativeFileFormat {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo] $File)

    $stream = [System.IO.File]::OpenRead($File.FullName)
    try {
        $header = [byte[]]::new(8)
        $count = $stream.Read($header, 0, $header.Length)
    } finally {
        $stream.Dispose()
    }

    if ($count -ge 4) {
        if ($header[0] -eq 0x7f -and $header[1] -eq 0x45 -and $header[2] -eq 0x4c -and $header[3] -eq 0x46) {
            return "elf"
        }
        if ($header[0] -eq 0x4d -and $header[1] -eq 0x5a) {
            if (Test-IsManagedPortableExecutable -File $File) {
                return $null
            }
            return "pe"
        }

        $magic = ('{0:x2}{1:x2}{2:x2}{3:x2}' -f $header[0], $header[1], $header[2], $header[3])
        if ($magic -in @("feedface", "cefaedfe", "feedfacf", "cffaedfe", "cafebabe", "bebafeca", "cafebabf", "bfbafeca")) {
            return "mach-o"
        }
    }
    if ($count -eq 8 -and [System.Text.Encoding]::ASCII.GetString($header, 0, 8) -ceq "!<arch>`n") {
        return "native-archive"
    }

    $extension = $File.Extension.ToLowerInvariant()
    if ($extension -in @(".a", ".lib")) {
        return "unrecognized-native-archive"
    }
    if ($extension -in @(".o", ".obj")) {
        return "native-object"
    }
    if ($extension -eq ".bc") {
        return "llvm-bitcode"
    }
    if ($extension -eq ".pdb") {
        return "native-debug-data"
    }
    if ($extension -in @(".exe", ".dll", ".dylib") -or $File.Name -match '\.so(\..*)?$') {
        return "unrecognized-native-binary"
    }
    return $null
}

function Get-BundledNativeLibraryNames {
    param(
        [string] $Root,
        [string] $Platform,
        [string[]] $ApprovedRoots
    )

    $comparison = if ($Platform -eq "windows") {
        [StringComparer]::OrdinalIgnoreCase
    } else {
        [StringComparer]::Ordinal
    }
    $names = [System.Collections.Generic.HashSet[string]]::new($comparison)
    $candidateRoots = $ApprovedRoots |
        ForEach-Object { Join-Path $Root $_ } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container }
    foreach ($file in ($candidateRoots |
            ForEach-Object { Get-ChildItem -LiteralPath $_ -File -Recurse -Force } |
            Sort-Object FullName -Unique)) {
        $isNativeLibrary = if ($Platform -eq "windows") {
            $file.Extension -ieq ".dll" -and (Get-NativeFileFormat -File $file) -eq "pe"
        } elseif ($Platform -eq "macos") {
            $file.Name.EndsWith(".dylib", [StringComparison]::Ordinal)
        } else {
            $file.Name -match '\.so(\..*)?$'
        }

        if ($isNativeLibrary) {
            [void] $names.Add($file.Name)
        }
    }

    return ,$names
}

function Get-CandidateFiles {
    param([string] $Root)

    $candidates = @()
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName -Unique)) {
        $format = Get-NativeFileFormat -File $file
        if ($null -eq $format) {
            continue
        }
        $candidates += [pscustomobject]@{
            File = $file
            Format = $format
        }
    }

    return $candidates
}

function Inspect-MacBinary {
    param(
        [System.IO.FileInfo] $File,
        [string] $Root,
        [string] $Otool,

        [System.Collections.Generic.HashSet[string]] $BundledLibraries
    )

    $dependencies = @()
    $rpaths = @()
    $violations = @()
    $loaderOriginDirectory = $File.DirectoryName
    # @executable_path is not relative to a dylib. Without a concrete loading
    # executable its origin cannot be proven, so fail closed for dylibs rather
    # than pretending their own directory is the executable directory.
    $executableOriginDirectory = if ($File.Name.EndsWith(".dylib", [StringComparison]::Ordinal)) {
        ""
    } else {
        $File.DirectoryName
    }
    $lines = (Invoke-CapturedTool -FileName $Otool -Arguments @("-L", $File.FullName)).Stdout `
        -replace "`r", "" -split "`n"
    foreach ($line in ($lines | Select-Object -Skip 1)) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        if ($trimmed -notmatch '\((compatibility|current) version') {
            continue
        }

        $dependency = ($trimmed -split '\s+\(', 2)[0]
        $dependencies += $dependency
        if (-not (Test-AllowedMacPath -Path $dependency -Root $Root `
                -LoaderOriginDirectory $loaderOriginDirectory `
                -ExecutableOriginDirectory $executableOriginDirectory)) {
            $violations += "dependency '$dependency' is outside the SDK and approved macOS system roots"
        } elseif (($dependency.StartsWith("@", [StringComparison]::Ordinal) `
                -or -not [System.IO.Path]::IsPathRooted($dependency)) `
            -and -not $BundledLibraries.Contains([System.IO.Path]::GetFileName($dependency))) {
            $violations += "dependency '$dependency' is SDK-relative but no matching library is bundled"
        } elseif ((Test-IsInsideRoot -Path $dependency -Root $Root) `
            -and -not (Test-Path -LiteralPath $dependency -PathType Leaf)) {
            $violations += "dependency '$dependency' points inside the SDK but the file is missing"
        }
    }

    $loadLines = (Invoke-CapturedTool -FileName $Otool -Arguments @("-l", $File.FullName)).Stdout `
        -replace "`r", "" -split "`n"
    $expectRpath = $false
    foreach ($line in $loadLines) {
        $trimmed = $line.Trim()
        if ($trimmed -eq "cmd LC_RPATH") {
            $expectRpath = $true
            continue
        }

        if ($expectRpath -and $trimmed -match '^path\s+(.+?)\s+\(offset\s+\d+\)$') {
            $rpath = $Matches[1]
            $rpaths += $rpath
            if (-not (Test-AllowedMacPath -Path $rpath -Root $Root `
                    -LoaderOriginDirectory $loaderOriginDirectory `
                    -ExecutableOriginDirectory $executableOriginDirectory)) {
                $violations += "LC_RPATH '$rpath' is outside the SDK and approved macOS system roots"
            }
            $expectRpath = $false
        }
    }

    return [pscustomobject]@{
        Path = Convert-ToRelativePath -Path $File.FullName -Root $Root
        Format = "mach-o"
        Dependencies = @($dependencies | Sort-Object -Unique)
        SearchPaths = @($rpaths | Sort-Object -Unique)
        Violations = $violations
    }
}

function Inspect-LinuxBinary {
    param(
        [System.IO.FileInfo] $File,
        [string] $Root,
        [string] $ReadElf,

        [System.Collections.Generic.HashSet[string]] $BundledLibraries
    )

    $dependencies = @()
    $searchPaths = @()
    $violations = @()
    $lines = (Invoke-CapturedTool -FileName $ReadElf -Arguments @("-d", $File.FullName)).Stdout `
        -replace "`r", "" -split "`n"
    foreach ($line in $lines) {
        if ($line -match '\(NEEDED\).*\[(.+?)\]') {
            $dependency = $Matches[1]
            $dependencies += $dependency
            if ($dependency.Contains("/", [StringComparison]::Ordinal) `
                -and -not (Test-IsInsideRoot -Path $dependency -Root $Root)) {
                $violations += "NEEDED entry '$dependency' contains a path outside the SDK"
            } elseif ($dependency.Contains("/", [StringComparison]::Ordinal) `
                -and -not (Test-Path -LiteralPath $dependency -PathType Leaf)) {
                $violations += "NEEDED entry '$dependency' points inside the SDK but the file is missing"
            } elseif (-not $dependency.Contains("/", [StringComparison]::Ordinal) `
                -and -not $BundledLibraries.Contains($dependency) `
                -and -not (Test-IsLinuxSystemLibrary -Name $dependency)) {
                $violations += "NEEDED library '$dependency' is neither bundled nor in the approved base Linux runtime allowlist"
            }
        }

        if ($line -match '\((RPATH|RUNPATH)\).*\[(.*?)\]') {
            foreach ($searchPath in ($Matches[2] -split ':')) {
                $searchPaths += $searchPath
                if ([string]::IsNullOrWhiteSpace($searchPath)) {
                    $violations += "$($Matches[1]) contains an empty current-directory search entry"
                    continue
                }

                if (-not (Test-AllowedLinuxSearchPath -Path $searchPath -Root $Root `
                        -OriginDirectory $File.DirectoryName)) {
                    $violations += "$($Matches[1]) entry '$searchPath' is neither SDK-relative nor an approved system library root"
                }
            }
        }
    }

    return [pscustomobject]@{
        Path = Convert-ToRelativePath -Path $File.FullName -Root $Root
        Format = "elf"
        Dependencies = @($dependencies | Sort-Object -Unique)
        SearchPaths = @($searchPaths | Sort-Object -Unique)
        Violations = $violations
    }
}

function Inspect-WindowsBinary {
    param(
        [System.IO.FileInfo] $File,
        [string] $Root,
        [string] $Inspector,
        [bool] $UsesDumpbin,
        [System.Collections.Generic.HashSet[string]] $BundledDlls
    )

    $arguments = if ($UsesDumpbin) { @("/DEPENDENTS", $File.FullName) } else { @("-p", $File.FullName) }
    $text = (Invoke-CapturedTool -FileName $Inspector -Arguments $arguments).Stdout
    $dependencies = @()
    $dependencyPattern = if ($UsesDumpbin) {
        '(?im)^\s+([A-Za-z0-9_.+\-]+\.dll)\s*$'
    } else {
        # llvm-objdump uses `DLL Name:` for imports and `DLL name:` for the
        # image's exported module name. Keep this match case-sensitive so an
        # export alias is not mistaken for a missing runtime dependency.
        '(?m)^\s+DLL Name:\s*([A-Za-z0-9_.+\-]+\.[dD][lL][lL])\s*$'
    }
    foreach ($match in [Regex]::Matches($text, $dependencyPattern)) {
        $dependencies += $match.Groups[1].Value
    }

    $dependencies = @($dependencies | Sort-Object -Unique)
    $violations = @()
    foreach ($dependency in $dependencies) {
        if (-not $BundledDlls.Contains($dependency) -and -not (Test-IsWindowsSystemLibrary -Name $dependency)) {
            $violations += "DLL '$dependency' is neither bundled in the SDK nor in the approved Windows system allowlist"
        }
    }

    return [pscustomobject]@{
        Path = Convert-ToRelativePath -Path $File.FullName -Root $Root
        Format = "pe"
        Dependencies = $dependencies
        SearchPaths = @()
        Violations = $violations
    }
}

$rootCandidate = if ([System.IO.Path]::IsPathRooted($SdkRoot)) {
    $SdkRoot
} else {
    Join-Path (Get-Location).Path $SdkRoot
}
if (-not (Test-Path -LiteralPath $rootCandidate -PathType Container)) {
    throw "SDK root '$rootCandidate' does not exist."
}
$root = (Resolve-Path -LiteralPath $rootCandidate).Path

$releasePath = Join-Path $root "release.json"
if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf)) {
    throw "Release SDK '$root' does not contain release.json."
}
$release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
$targetTriple = [string]$release.defaultTargetTriple
$platform = if ($targetTriple -match '(?i)(windows|msvc)') {
    "windows"
} elseif ($targetTriple -match '(?i)(darwin|apple|macos)') {
    "macos"
} elseif ($targetTriple -match '(?i)linux') {
    "linux"
} elseif ($IsWindows) {
    "windows"
} elseif ($IsMacOS) {
    "macos"
} else {
    "linux"
}

$policyCandidate = if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    Join-Path $PSScriptRoot "../eng/release/archive-content.json"
} elseif ([System.IO.Path]::IsPathRooted($PolicyPath)) {
    $PolicyPath
} else {
    Join-Path (Get-Location).Path $PolicyPath
}
$policy = (Resolve-Path -LiteralPath $policyCandidate).Path
$approvedNativeRoots = @(Get-ApprovedNativeRoots -Path $policy)
$candidates = @(Get-CandidateFiles -Root $root)
$bundledLibraries = Get-BundledNativeLibraryNames `
    -Root $root `
    -Platform $platform `
    -ApprovedRoots $approvedNativeRoots
$reports = @()
$uninspectable = @()
$inspectionCandidates = @()
$expectedFormat = if ($platform -eq "macos") {
    "mach-o"
} elseif ($platform -eq "linux") {
    "elf"
} else {
    "pe"
}
foreach ($candidate in $candidates) {
    $relativePath = Convert-ToRelativePath -Path $candidate.File.FullName -Root $root
    $candidateViolations = @()
    $isApproved = Test-IsApprovedNativePayloadPath `
        -RelativePath $relativePath `
        -ApprovedRoots $approvedNativeRoots
    if (-not $isApproved) {
        $candidateViolations += "native artifact format '$($candidate.Format)' is outside approved payload roots: $($approvedNativeRoots -join ', ')"
    }

    $candidatePlatform = if ($candidate.Format -eq "mach-o") {
        "macos"
    } elseif ($candidate.Format -eq "elf") {
        "linux"
    } elseif ($candidate.Format -eq "pe") {
        "windows"
    } else {
        $null
    }
    if ($null -ne $candidatePlatform -and $candidatePlatform -ne $platform) {
        $candidateViolations += "native artifact format '$($candidate.Format)' targets '$candidatePlatform', not release platform '$platform'"
    }

    if ($candidate.Format.StartsWith("unrecognized-native-", [StringComparison]::Ordinal)) {
        $uninspectable += "${relativePath}: file has native artifact extension but unrecognized binary magic ($($candidate.Format))"
    }

    if ($candidateViolations.Count -ne 0 `
        -or $candidate.Format -ne $expectedFormat) {
        $reports += [pscustomobject]@{
            Path = $relativePath
            Format = $candidate.Format
            Dependencies = @()
            SearchPaths = @()
            Violations = $candidateViolations
        }
        continue
    }
    $inspectionCandidates += $candidate.File
}

if ($platform -eq "macos" -and $inspectionCandidates.Count -ne 0) {
    $otool = Resolve-RequiredTool -Names @("otool")
    foreach ($file in $inspectionCandidates) {
        try {
            $reports += Inspect-MacBinary `
                -File $file `
                -Root $root `
                -Otool $otool `
                -BundledLibraries $bundledLibraries
        } catch {
            $uninspectable += "$(Convert-ToRelativePath -Path $file.FullName -Root $root): $($_.Exception.Message)"
        }
    }
} elseif ($platform -eq "linux" -and $inspectionCandidates.Count -ne 0) {
    $readElf = Resolve-RequiredTool -Names @("readelf", "llvm-readelf")
    foreach ($file in $inspectionCandidates) {
        try {
            $reports += Inspect-LinuxBinary `
                -File $file `
                -Root $root `
                -ReadElf $readElf `
                -BundledLibraries $bundledLibraries
        } catch {
            $uninspectable += "$(Convert-ToRelativePath -Path $file.FullName -Root $root): $($_.Exception.Message)"
        }
    }
} elseif ($platform -eq "windows" -and $inspectionCandidates.Count -ne 0) {
    $dumpbin = Get-Command "dumpbin" -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $inspector = if ($null -ne $dumpbin) {
        $dumpbin.Source
    } else {
        Resolve-RequiredTool -Names @("llvm-objdump", "objdump")
    }
    foreach ($file in $inspectionCandidates) {
        try {
            $reports += Inspect-WindowsBinary `
                -File $file `
                -Root $root `
                -Inspector $inspector `
                -UsesDumpbin ($null -ne $dumpbin) `
                -BundledDlls $bundledLibraries
        } catch {
            $uninspectable += "$(Convert-ToRelativePath -Path $file.FullName -Root $root): $($_.Exception.Message)"
        }
    }
}

$violations = @($reports |
    ForEach-Object {
        $path = $_.Path
        $_.Violations | ForEach-Object { "${path}: $_" }
    })
if (-not $AllowUninspectable) {
    $violations += $uninspectable | ForEach-Object { "uninspectable: $_" }
}

$validatedCandidate = if ($violations.Count -eq 0) {
    Get-StagedCandidateIdentity `
        -Root $root `
        -TargetId ([string]$release.assetSuffix) `
        -RequestedReleaseTools $ReleaseToolsPath `
        -RequestedDotNet $DotNetPath
} else {
    $null
}

$result = [ordered]@{
    schemaVersion = 1
    validationScope = if ($violations.Count -eq 0) { "release-candidate" } else { "failed-audit" }
    sdkRoot = Split-Path -Leaf $root
    assetSuffix = [string]$release.assetSuffix
    targetTriple = $targetTriple
    platform = $platform
    nativeBinaryPolicy = [ordered]@{
        path = $policy
        approvedRoots = $approvedNativeRoots
        scannedRoot = "."
    }
    status = if ($violations.Count -eq 0) { "ok" } else { "violations" }
    files = @($reports | Sort-Object Path)
    uninspectable = $uninspectable
    violations = $violations
    validatedCandidate = $validatedCandidate
}
$json = $result | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputCandidate = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
        $OutputPath
    } else {
        Join-Path (Get-Location).Path $OutputPath
    }
    $outputDirectory = Split-Path -Parent $outputCandidate
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }
    Set-Content -LiteralPath $outputCandidate -Value $json -Encoding utf8
}

Write-Output $json
if ($violations.Count -ne 0) {
    throw "Release native dependency audit found $($violations.Count) violation(s)."
}
