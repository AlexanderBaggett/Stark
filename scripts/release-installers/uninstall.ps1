[CmdletBinding()]
param(
    [Alias("Prefix")]
    [string] $Destination = "",

    [switch] $DryRun,

    [switch] $NonInteractive
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$scriptRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$receiptName = ".stark-install-receipt.json"

function Test-IsWindowsHost {
    try {
        return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)
    } catch {
        return [string]::Equals($env:OS, "Windows_NT", [StringComparison]::OrdinalIgnoreCase)
    }
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)] [object] $Object,
        [Parameter(Mandatory = $true)] [string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "Installation receipt is missing '$Name'."
    }
    return $property.Value
}

function Test-SamePath {
    param([string] $Left, [string] $Right)
    return [string]::Equals(
        [System.IO.Path]::GetFullPath($Left).TrimEnd('\', '/'),
        [System.IO.Path]::GetFullPath($Right).TrimEnd('\', '/'),
        [StringComparison]::OrdinalIgnoreCase)
}

if (-not (Test-IsWindowsHost)) {
    throw "uninstall.ps1 only uninstalls Windows Stark SDKs. Use uninstall.sh on macOS or Linux."
}
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw "LOCALAPPDATA is not defined; pass -Destination and repair the Windows user profile."
}

if ([string]::IsNullOrWhiteSpace($Destination) -and
    (Test-Path -LiteralPath (Join-Path $scriptRoot $receiptName) -PathType Leaf)) {
    $Destination = $scriptRoot
} elseif ([string]::IsNullOrWhiteSpace($Destination)) {
    $releasePath = Join-Path $scriptRoot "release.json"
    if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf)) {
        throw "Pass -Destination when uninstall.ps1 is not inside a Stark release or installation."
    }
    $release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
    $version = [string](Get-RequiredProperty -Object $release -Name "starkVersion")
    $Destination = Join-Path $env:LOCALAPPDATA "Stark/versions/$version"
}

$Destination = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
$driveRoot = [System.IO.Path]::GetPathRoot($Destination).TrimEnd('\', '/')
if ([string]::Equals($Destination, $driveRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to operate on a drive root."
}
$destinationItem = Get-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
if ($null -ne $destinationItem -and
    (($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw "Refusing to uninstall through reparse-point prefix '$Destination'."
}

$receiptPath = Join-Path $Destination $receiptName
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
    throw "'$Destination' does not contain a Stark installation receipt."
}
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
$schema = [string](Get-RequiredProperty -Object $receipt -Name "Schema")
if ($schema -ne "stark-install-receipt-v1") {
    throw "Unsupported Stark installation receipt schema '$schema'."
}
$version = [string](Get-RequiredProperty -Object $receipt -Name "Version")
$receiptPrefix = [string](Get-RequiredProperty -Object $receipt -Name "Prefix")
if (-not (Test-SamePath -Left $receiptPrefix -Right $Destination)) {
    throw "The receipt does not own '$Destination'."
}
$installedFiles = @(Get-RequiredProperty -Object $receipt -Name "InstalledFiles")
if ($installedFiles.Count -eq 0) {
    throw "The receipt has no installed-file inventory."
}

$commandRoot = Join-Path $env:LOCALAPPDATA "Stark/bin"
$expectedCommandShim = Join-Path $commandRoot "stark.cmd"
$shimOwnerPath = Join-Path $commandRoot ".stark-shim-owner.json"
$commandShimProperty = $receipt.PSObject.Properties["CommandShim"]
$commandShim = if ($null -eq $commandShimProperty) { $null } else { [string]$commandShimProperty.Value }
if (-not [string]::IsNullOrWhiteSpace($commandShim) -and -not (Test-SamePath -Left $commandShim -Right $expectedCommandShim)) {
    throw "The receipt contains unsafe command-shim path '$commandShim'."
}
$pathEntryProperty = $receipt.PSObject.Properties["PathEntry"]
$pathEntry = if ($null -eq $pathEntryProperty) { $null } else { [string]$pathEntryProperty.Value }
if (-not [string]::IsNullOrWhiteSpace($pathEntry) -and -not (Test-SamePath -Left $pathEntry -Right $commandRoot)) {
    throw "The receipt contains unsafe PATH entry '$pathEntry'."
}

$prefixWithSeparator = $Destination + [System.IO.Path]::DirectorySeparatorChar
$ownedPaths = New-Object System.Collections.Generic.List[string]
$ownedDirectories = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$null = $ownedDirectories.Add($Destination)
foreach ($entryObject in $installedFiles) {
    $relativePath = [string]$entryObject
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        [System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.Contains('\') -or
        $relativePath -match '(^|/)\.\.?(/|$)') {
        throw "The receipt contains unsafe installed path '$relativePath'."
    }

    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $Destination $relativePath))
    if (-not $fullPath.StartsWith($prefixWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The receipt path '$relativePath' escapes '$Destination'."
    }

    $relativeParent = Split-Path -Parent $relativePath
    $current = $Destination
    if (-not [string]::IsNullOrWhiteSpace($relativeParent)) {
        foreach ($segment in ($relativeParent -split '/')) {
            $current = Join-Path $current $segment
            $currentItem = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
            if ($null -ne $currentItem -and
                (($currentItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Refusing to traverse reparse-point directory '$current'."
            }
            $null = $ownedDirectories.Add($current)
        }
    }
    $ownedPaths.Add($fullPath)
}

$installedDirectoriesProperty = $receipt.PSObject.Properties["InstalledDirectories"]
if ($null -ne $installedDirectoriesProperty) {
    foreach ($directoryObject in @($installedDirectoriesProperty.Value)) {
        $relativeDirectory = [string]$directoryObject
        if ([string]::IsNullOrWhiteSpace($relativeDirectory) -or
            [System.IO.Path]::IsPathRooted($relativeDirectory) -or
            $relativeDirectory.Contains('\') -or
            $relativeDirectory -match '(^|/)\.\.?(/|$)') {
            throw "The receipt contains unsafe installed directory '$relativeDirectory'."
        }
        $fullDirectory = [System.IO.Path]::GetFullPath((Join-Path $Destination $relativeDirectory))
        if (-not $fullDirectory.StartsWith($prefixWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The receipt directory '$relativeDirectory' escapes '$Destination'."
        }

        $current = $Destination
        foreach ($segment in ($relativeDirectory -split '/')) {
            $current = Join-Path $current $segment
            $currentItem = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
            if ($null -ne $currentItem -and
                (($currentItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Refusing to traverse reparse-point directory '$current'."
            }
            $null = $ownedDirectories.Add($current)
        }
    }
}

Write-Host "Uninstalling Stark $version from '$Destination'."
if ($DryRun) {
    foreach ($path in $ownedPaths) {
        Write-Host "Would remove: $path"
    }
    if (-not [string]::IsNullOrWhiteSpace($commandShim)) {
        Write-Host "Would remove or restore Stark command shim: $commandShim"
    }
    return
}

foreach ($path in $ownedPaths) {
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path -Force
        if ($item.PSIsContainer -and
            (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)) {
            throw "Receipt-owned file path '$path' was replaced by a directory; refusing recursive removal."
        }
        Remove-Item -LiteralPath $path -Force
    }
}

$shimStillUsed = $false
$receiptPathManagedProperty = $receipt.PSObject.Properties["PathManaged"]
$receiptPathAddedProperty = $receipt.PSObject.Properties["PathAdded"]
$pathManaged = if ($null -ne $receiptPathManagedProperty) {
    [bool]$receiptPathManagedProperty.Value
} elseif ($null -ne $receiptPathAddedProperty) {
    [bool]$receiptPathAddedProperty.Value
} else {
    $false
}
if (-not [string]::IsNullOrWhiteSpace($commandShim) -and
    (Test-Path -LiteralPath $commandShim -PathType Leaf) -and
    (Test-Path -LiteralPath $shimOwnerPath -PathType Leaf)) {
    $owner = Get-Content -LiteralPath $shimOwnerPath -Raw | ConvertFrom-Json
    $currentTarget = [string](Get-RequiredProperty -Object $owner -Name "CurrentTarget")
    $pathManagedProperty = $owner.PSObject.Properties["PathManaged"]
    if ($null -ne $pathManagedProperty) {
        $ownerPathManaged = [bool]$pathManagedProperty.Value
        $pathManaged = $pathManaged -or $ownerPathManaged
    }

    $installedTarget = Join-Path $Destination "bin/stark.exe"
    if (Test-SamePath -Left $currentTarget -Right $installedTarget) {
        Remove-Item -LiteralPath $commandShim -Force
        Remove-Item -LiteralPath $shimOwnerPath -Force

        $previousProperty = $receipt.PSObject.Properties["PreviousCommandTarget"]
        $previousTarget = if ($null -eq $previousProperty) { $null } else { [string]$previousProperty.Value }
        if (-not [string]::IsNullOrWhiteSpace($previousTarget) -and
            $previousTarget -notmatch '[\r\n"%&|<>^!]' -and
            $previousTarget.EndsWith("\bin\stark.exe", [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $previousTarget -PathType Leaf)) {
            $previousPrefix = Split-Path -Parent (Split-Path -Parent $previousTarget)
            if (Test-Path -LiteralPath (Join-Path $previousPrefix $receiptName) -PathType Leaf) {
                $shimText = "@echo off`r`n`"$previousTarget`" %*`r`n"
                [System.IO.File]::WriteAllText($commandShim, $shimText, [System.Text.Encoding]::ASCII)
                [ordered]@{
                    Schema = "stark-command-shim-v1"
                    CurrentTarget = $previousTarget
                    PathManaged = $pathManaged
                } | ConvertTo-Json | Set-Content -LiteralPath $shimOwnerPath -Encoding UTF8
                $shimStillUsed = $true
                Write-Host "Restored the previous Stark command target '$previousTarget'."
            }
        }
    } else {
        $shimStillUsed = $true
        Write-Host "Left '$commandShim' unchanged because it now selects another Stark installation."
    }
} elseif (-not [string]::IsNullOrWhiteSpace($commandShim) -and (Test-Path -LiteralPath $commandShim)) {
    $shimStillUsed = $true
    Write-Host "Left command '$commandShim' unchanged because its ownership marker is missing."
}

if (-not $shimStillUsed -and $pathManaged -and -not [string]::IsNullOrWhiteSpace($pathEntry)) {
    $userPath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::User)
    $remainingUserEntries = @($userPath -split ';' | Where-Object {
        -not [string]::Equals($_.Trim().TrimEnd('\', '/'), $pathEntry.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)
    })
    [Environment]::SetEnvironmentVariable("Path", [string]::Join(';', $remainingUserEntries), [EnvironmentVariableTarget]::User)
    $remainingProcessEntries = @($env:Path -split ';' | Where-Object {
        -not [string]::Equals($_.Trim().TrimEnd('\', '/'), $pathEntry.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)
    })
    $env:Path = [string]::Join(';', $remainingProcessEntries)
}

foreach ($directory in @($ownedDirectories) | Sort-Object { $_.Length } -Descending) {
    if (Test-Path -LiteralPath $directory -PathType Container) {
        $hasChildren = $null -ne (Get-ChildItem -LiteralPath $directory -Force | Select-Object -First 1)
        if (-not $hasChildren) {
            Remove-Item -LiteralPath $directory -Force
        }
    }
}

if (Test-Path -LiteralPath $Destination -PathType Container) {
    Write-Host "Receipt-owned files were removed; '$Destination' remains because it contains unrelated files."
} else {
    Write-Host "Uninstalled Stark $version."
}

$null = $NonInteractive
