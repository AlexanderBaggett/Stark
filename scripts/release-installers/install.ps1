[CmdletBinding()]
param(
    [Alias("Prefix")]
    [string] $Destination = "",

    [switch] $NoPath,

    [switch] $NonInteractive,

    [switch] $DryRun,

    [switch] $Force,

    [switch] $Repair,

    [string] $ArchiveSha256 = "not-provided"
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$sourceRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$receiptName = ".stark-install-receipt.json"
$stagePath = $null
$backupPath = $null
$destinationActivated = $false
$previousDestinationBackedUp = $false

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)] [object] $Object,
        [Parameter(Mandatory = $true)] [string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "release metadata is missing '$Name'."
    }

    return [string]$property.Value
}

function Test-IsWindowsHost {
    try {
        return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)
    } catch {
        return [string]::Equals($env:OS, "Windows_NT", [StringComparison]::OrdinalIgnoreCase)
    }
}

function Get-HostArchitecture {
    try {
        $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    } catch {
        $architecture = if (-not [string]::IsNullOrWhiteSpace($env:PROCESSOR_ARCHITEW6432)) {
            $env:PROCESSOR_ARCHITEW6432
        } else {
            $env:PROCESSOR_ARCHITECTURE
        }
    }

    switch -Regex ($architecture) {
        '^(X64|AMD64|x86_64)$' { return "x64" }
        '^(Arm64|ARM64|aarch64)$' { return "arm64" }
        '^(X86|x86|i[3-6]86)$' { throw "32-bit Windows hosts are not supported." }
        default { throw "Unsupported Windows processor architecture '$architecture'." }
    }
}

function Test-SameOrDescendantPath {
    param([string] $Path, [string] $Root)

    $candidate = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    return [string]::Equals($candidate, $rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Get-SafeReleaseFiles {
    param([Parameter(Mandatory = $true)] [string] $Root)

    $pending = [System.Collections.Generic.Stack[string]]::new()
    $files = New-Object System.Collections.ArrayList
    $pending.Push([System.IO.Path]::GetFullPath($Root))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in (Get-ChildItem -LiteralPath $directory -Force)) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release archive contains unsupported reparse point '$($item.FullName)'."
            }
            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            } else {
                [void]$files.Add($item)
            }
        }
    }

    return @($files)
}

function Get-ReleaseRelativePath {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $Path
    )

    $rootPrefix = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release file '$fullPath' escapes '$Root'."
    }
    return $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
}

function Assert-ReleaseFileChecksums {
    param([Parameter(Mandatory = $true)] [string] $Root)

    $manifestPath = Join-Path $Root "release-files.sha256"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "release-files.sha256 is missing; the release archive is incomplete."
    }
    $manifestItem = Get-Item -LiteralPath $manifestPath -Force
    if (($manifestItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "release-files.sha256 must not be a reparse point."
    }

    $expectedPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in (Get-Content -LiteralPath $manifestPath)) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
            throw "release-files.sha256 contains malformed line '$line'."
        }
        $expectedHash = $Matches[1]
        $relativePath = $Matches[2]
        if ([System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains('\') -or
            $relativePath.Contains(':') -or
            $relativePath -match '(^|/)\.\.?(/|$)' -or
            $relativePath.Contains('//')) {
            throw "release-files.sha256 contains unsafe path '$relativePath'."
        }
        foreach ($segment in ($relativePath -split '/')) {
            if ($segment.EndsWith('.', [StringComparison]::Ordinal) -or
                $segment.EndsWith(' ', [StringComparison]::Ordinal)) {
                throw "release-files.sha256 contains non-canonical Windows path '$relativePath'."
            }
        }
        if ([string]::Equals($relativePath, "release-files.sha256", [StringComparison]::OrdinalIgnoreCase)) {
            throw "release-files.sha256 must not checksum itself."
        }
        if (-not $expectedPaths.Add($relativePath)) {
            throw "release-files.sha256 contains duplicate path '$relativePath'."
        }

        $filePath = [System.IO.Path]::GetFullPath((Join-Path $Root $relativePath))
        $canonicalRelativePath = Get-ReleaseRelativePath -Root $Root -Path $filePath
        if (-not [string]::Equals($canonicalRelativePath, $relativePath, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Release file '$relativePath' is missing or non-canonical."
        }
        $fileItem = Get-Item -LiteralPath $filePath -Force
        if (($fileItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release file '$relativePath' must not be a reparse point."
        }
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash.ToLowerInvariant()
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::Ordinal)) {
            throw "Release file '$relativePath' failed SHA-256 verification."
        }
    }
    if ($expectedPaths.Count -eq 0) {
        throw "release-files.sha256 contains no files."
    }

    foreach ($file in (Get-SafeReleaseFiles -Root $Root)) {
        $relativePath = Get-ReleaseRelativePath -Root $Root -Path $file.FullName
        if ([string]::Equals($relativePath, "release-files.sha256", [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (-not $expectedPaths.Contains($relativePath)) {
            throw "Release archive contains untracked file '$relativePath'."
        }
    }
}

function Invoke-Doctor {
    param([string] $Compiler, [string] $Description)

    & $Compiler doctor --strict
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed 'stark doctor --strict' with exit code $LASTEXITCODE."
    }
}

if ($Force -and $Repair) {
    throw "-Force and -Repair are mutually exclusive."
}
if (-not (Test-IsWindowsHost)) {
    throw "install.ps1 only installs Windows release archives. Use install.sh on macOS or Linux."
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot "release.json") -PathType Leaf)) {
    throw "release.json is missing; run this script from an extracted Stark release."
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot "sdk.json") -PathType Leaf)) {
    throw "sdk.json is missing; the release archive is incomplete."
}
$sourceCompiler = Join-Path $sourceRoot "bin/stark.exe"
if (-not (Test-Path -LiteralPath $sourceCompiler -PathType Leaf)) {
    throw "bin/stark.exe is missing; this is not a Windows Stark release."
}

Assert-ReleaseFileChecksums -Root $sourceRoot

$release = Get-Content -LiteralPath (Join-Path $sourceRoot "release.json") -Raw | ConvertFrom-Json
$version = Get-RequiredProperty -Object $release -Name "starkVersion"
$assetSuffix = Get-RequiredProperty -Object $release -Name "assetSuffix"
if ($version -notmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]*$') {
    throw "Release version '$version' is not a portable version identifier."
}
if ($assetSuffix -notmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]*$') {
    throw "Release asset suffix '$assetSuffix' is invalid."
}
if ($ArchiveSha256 -ne "not-provided" -and $ArchiveSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw "-ArchiveSha256 must be a 64-character hexadecimal SHA-256."
}

$expectedAssetSuffix = "windows-$(Get-HostArchitecture)"
if (-not [string]::Equals($assetSuffix, $expectedAssetSuffix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "This is the '$assetSuffix' archive, but this host needs '$expectedAssetSuffix'."
}

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw "LOCALAPPDATA is not defined; pass -Destination explicitly and repair the Windows user profile."
}
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $env:LOCALAPPDATA "Stark/versions/$version"
}
$Destination = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
$destinationRoot = [System.IO.Path]::GetPathRoot($Destination).TrimEnd('\', '/')
if ([string]::Equals($Destination, $destinationRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install over a drive root."
}
if (Test-SameOrDescendantPath -Path $Destination -Root $sourceRoot) {
    throw "Install destination must not be inside the extracted release."
}
if (Test-SameOrDescendantPath -Path $sourceRoot -Root $Destination) {
    throw "Install destination must not contain the extracted release."
}
if ($Destination -match '[\r\n"%&|<>^!]') {
    throw "Install destination contains characters that cannot be represented safely by the Stark command shim."
}

$receiptPath = Join-Path $Destination $receiptName
if (Test-Path -LiteralPath $Destination) {
    if (-not $Force -and -not $Repair) {
        throw "'$Destination' already exists; use -Repair or -Force only for a receipt-owned Stark installation."
    }
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "'$Destination' already exists and is not a receipt-owned Stark installation."
    }
    $oldReceipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $oldPrefix = Get-RequiredProperty -Object $oldReceipt -Name "Prefix"
    $oldVersion = Get-RequiredProperty -Object $oldReceipt -Name "Version"
    if (-not [string]::Equals([System.IO.Path]::GetFullPath($oldPrefix), $Destination, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The existing receipt does not own '$Destination'."
    }
    if ($Repair -and -not [string]::Equals($oldVersion, $version, [StringComparison]::Ordinal)) {
        throw "-Repair requires the same version (installed '$oldVersion', archive '$version')."
    }
}

$commandRoot = Join-Path $env:LOCALAPPDATA "Stark/bin"
$commandShim = Join-Path $commandRoot "stark.cmd"
$shimOwnerPath = Join-Path $commandRoot ".stark-shim-owner.json"
if ($commandRoot -match '[\r\n"%&|<>^!]') {
    throw "LOCALAPPDATA contains characters that cannot be represented safely by the Stark command shim. Use -NoPath."
}
$previousCommandTarget = $null
$pathManagedBefore = $false
if (-not $NoPath -and (Test-Path -LiteralPath $commandShim)) {
    if (-not (Test-Path -LiteralPath $shimOwnerPath -PathType Leaf)) {
        throw "'$commandShim' already exists and is not Stark-managed. Use -NoPath or move it manually."
    }
    $shimOwner = Get-Content -LiteralPath $shimOwnerPath -Raw | ConvertFrom-Json
    $previousCommandTarget = Get-RequiredProperty -Object $shimOwner -Name "CurrentTarget"
    $pathManagedProperty = $shimOwner.PSObject.Properties["PathManaged"]
    if ($null -ne $pathManagedProperty) {
        $pathManagedBefore = [bool]$pathManagedProperty.Value
    }
    $previousReceipt = Join-Path (Split-Path -Parent (Split-Path -Parent $previousCommandTarget)) $receiptName
    if (-not (Test-Path -LiteralPath $previousReceipt -PathType Leaf)) {
        throw "'$commandShim' names an installation without a valid Stark receipt. Use -NoPath or repair it manually."
    }
}

$userPath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::User)
if ($null -eq $userPath) {
    $userPath = ""
}
$pathEntries = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$pathAlreadyPresent = $false
foreach ($entry in $pathEntries) {
    if ([string]::Equals($entry.Trim().TrimEnd('\', '/'), $commandRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
        $pathAlreadyPresent = $true
        break
    }
}
$pathAdded = -not $NoPath -and -not $pathAlreadyPresent
$newUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $commandRoot } else { "$commandRoot;$userPath" }
if ($pathAdded -and $newUserPath.Length -gt 32767) {
    throw "Adding '$commandRoot' would exceed the supported Windows user PATH length. Use -NoPath and invoke '$Destination\bin\stark.exe' directly."
}

if (-not $NoPath) {
    $existingCommand = Get-Command stark -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $existingCommand -and
        -not [string]::IsNullOrWhiteSpace([string]$existingCommand.Source) -and
        -not (Test-SameOrDescendantPath -Path $existingCommand.Source -Root $commandRoot) -and
        -not (Test-SameOrDescendantPath -Path $existingCommand.Source -Root $sourceRoot)) {
        Write-Warning "A different Stark command currently resolves to '$($existingCommand.Source)'. The installer will prepend '$commandRoot' to the user PATH; already-open terminals may still select the old command."
    }
}

Write-Host "Stark $version installer"
Write-Host "  source:      $sourceRoot"
Write-Host "  destination: $Destination"
Write-Host "  asset:       $assetSuffix"
if ($NoPath) {
    Write-Host "  PATH:        unchanged (-NoPath)"
} else {
    Write-Host "  command:     $commandShim -> $Destination\bin\stark.exe"
}

# This validates sdk.json-owned payload checksums and diagnoses approved host
# prerequisites before the installer changes the machine.
Invoke-Doctor -Compiler $sourceCompiler -Description "Archive preflight"
# Ensure compiler preflight left the verified release tree unchanged before it
# becomes the source of the transactional copy.
Assert-ReleaseFileChecksums -Root $sourceRoot
if ($DryRun) {
    Write-Host "Dry run complete; no files were changed."
    return
}

$parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$parent = (Resolve-Path -LiteralPath $parent).Path
$Destination = Join-Path $parent (Split-Path -Leaf $Destination)
$receiptPath = Join-Path $Destination $receiptName
$stagePath = Join-Path $parent ".stark-install-$version-$PID"
$backupPath = Join-Path $parent ".stark-backup-$version-$PID"
if ((Test-Path -LiteralPath $stagePath) -or (Test-Path -LiteralPath $backupPath)) {
    throw "A temporary Stark install or backup path already exists."
}

$previousShimBytes = $null
$previousOwnerBytes = $null
if (Test-Path -LiteralPath $commandShim -PathType Leaf) {
    $previousShimBytes = [System.IO.File]::ReadAllBytes($commandShim)
}
if (Test-Path -LiteralPath $shimOwnerPath -PathType Leaf) {
    $previousOwnerBytes = [System.IO.File]::ReadAllBytes($shimOwnerPath)
}

function Restore-CommandState {
    if ($NoPath) {
        return
    }

    if ($null -eq $previousShimBytes) {
        Remove-Item -LiteralPath $commandShim -Force -ErrorAction SilentlyContinue
    } else {
        [System.IO.File]::WriteAllBytes($commandShim, $previousShimBytes)
    }
    if ($null -eq $previousOwnerBytes) {
        Remove-Item -LiteralPath $shimOwnerPath -Force -ErrorAction SilentlyContinue
    } else {
        [System.IO.File]::WriteAllBytes($shimOwnerPath, $previousOwnerBytes)
    }
    if ($pathAdded) {
        [Environment]::SetEnvironmentVariable("Path", $userPath, [EnvironmentVariableTarget]::User)
        $restoredProcessPath = @($env:Path -split ';' |
            Where-Object { -not [string]::Equals($_.TrimEnd('\', '/'), $commandRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase) })
        $env:Path = [string]::Join(';', $restoredProcessPath)
    }
}

function Restore-Destination {
    if ($destinationActivated -and (Test-Path -LiteralPath $Destination)) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    if ($previousDestinationBackedUp -and (Test-Path -LiteralPath $backupPath)) {
        Move-Item -LiteralPath $backupPath -Destination $Destination
    }
}

try {
    New-Item -ItemType Directory -Path $stagePath | Out-Null
    foreach ($item in (Get-ChildItem -LiteralPath $sourceRoot -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $stagePath -Recurse -Force
    }

    $stagePrefix = [System.IO.Path]::GetFullPath($stagePath).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $installedFiles = @(
        Get-ChildItem -LiteralPath $stagePath -File -Recurse -Force |
            ForEach-Object {
                $fullName = [System.IO.Path]::GetFullPath($_.FullName)
                if (-not $fullName.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Staged release file '$fullName' escaped '$stagePath'."
                }
                $fullName.Substring($stagePrefix.Length).Replace('\', '/')
            } |
            Sort-Object
    )
    $installedDirectories = @(
        Get-ChildItem -LiteralPath $stagePath -Directory -Recurse -Force |
            ForEach-Object {
                $fullName = [System.IO.Path]::GetFullPath($_.FullName)
                if (-not $fullName.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Staged release directory '$fullName' escaped '$stagePath'."
                }
                $fullName.Substring($stagePrefix.Length).Replace('\', '/')
            } |
            Sort-Object
    )
    $installedFiles += $receiptName
    $receipt = [ordered]@{
        Schema = "stark-install-receipt-v1"
        Version = $version
        AssetSuffix = $assetSuffix
        Prefix = $Destination
        SourceArchiveSha256 = $ArchiveSha256
        SourceContentManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $sourceRoot "release-files.sha256")).Hash.ToLowerInvariant()
        SourceReleaseJsonSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $sourceRoot "release.json")).Hash.ToLowerInvariant()
        InstalledFiles = $installedFiles
        InstalledDirectories = $installedDirectories
        CommandShim = $(if ($NoPath) { $null } else { $commandShim })
        PreviousCommandTarget = $previousCommandTarget
        PathEntry = $(if ($NoPath) { $null } else { $commandRoot })
        PathAdded = $pathAdded
        PathManaged = ($pathAdded -or $pathManagedBefore)
    }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stagePath $receiptName) -Encoding UTF8

    if (Test-Path -LiteralPath $Destination) {
        Move-Item -LiteralPath $Destination -Destination $backupPath
        $previousDestinationBackedUp = $true
    }
    Move-Item -LiteralPath $stagePath -Destination $Destination
    $stagePath = $null
    $destinationActivated = $true

    Invoke-Doctor -Compiler (Join-Path $Destination "bin/stark.exe") -Description "Installed SDK validation"

    if (-not $NoPath) {
        New-Item -ItemType Directory -Force -Path $commandRoot | Out-Null
        $shimText = "@echo off`r`n`"$Destination\bin\stark.exe`" %*`r`n"
        [System.IO.File]::WriteAllText($commandShim, $shimText, [System.Text.Encoding]::ASCII)
        [ordered]@{
            Schema = "stark-command-shim-v1"
            CurrentTarget = "$Destination\bin\stark.exe"
            PathManaged = ($pathAdded -or $pathManagedBefore)
        } | ConvertTo-Json | Set-Content -LiteralPath $shimOwnerPath -Encoding UTF8

        if ($pathAdded) {
            [Environment]::SetEnvironmentVariable("Path", $newUserPath, [EnvironmentVariableTarget]::User)
            if (-not (($env:Path -split ';') -contains $commandRoot)) {
                $env:Path = "$commandRoot;$env:Path"
            }
        }
    }

    if ($previousDestinationBackedUp -and (Test-Path -LiteralPath $backupPath)) {
        Remove-Item -LiteralPath $backupPath -Recurse -Force
        $previousDestinationBackedUp = $false
    }
} catch {
    try { Restore-CommandState } catch { Write-Warning "Could not fully restore the previous command/PATH state: $($_.Exception.Message)" }
    try { Restore-Destination } catch { Write-Warning "Could not fully restore the previous SDK: $($_.Exception.Message)" }
    throw
} finally {
    if ($null -ne $stagePath -and (Test-Path -LiteralPath $stagePath)) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
}

Write-Host "Installed Stark $version in '$Destination'."
if ($NoPath) {
    Write-Host "PATH was not changed. Add '$Destination\bin' manually when desired."
} else {
    Write-Host "This PowerShell process can use Stark immediately. Already-open terminals may need to be restarted."
    Write-Host "Verify with: stark doctor --strict"
}

# The switch is accepted for cross-platform automation consistency. The
# installer has no prompts unless future prerequisite acquisition is added.
$null = $NonInteractive
