param(
    [Parameter(Mandatory = $true)]
    [string] $StageRoot,

    [Parameter(Mandatory = $true)]
    [ValidateSet("linux-x64", "linux-arm64", "macos-x64", "macos-arm64", "windows-x64", "windows-arm64")]
    [string] $AssetSuffix
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$installerRoot = Join-Path $repositoryRoot "scripts/release-installers"
if (-not (Test-Path -LiteralPath $StageRoot -PathType Container)) {
    throw "Release stage '$StageRoot' does not exist."
}
$stagePath = (Resolve-Path -LiteralPath $StageRoot).Path

$scriptNames = if ($AssetSuffix.StartsWith("windows-", [StringComparison]::Ordinal)) {
    @("install.ps1", "uninstall.ps1")
} else {
    @("install.sh", "uninstall.sh")
}

foreach ($scriptName in $scriptNames) {
    $source = Join-Path $installerRoot $scriptName
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Release installer source '$source' is missing."
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stagePath $scriptName) -Force
}

if (-not $AssetSuffix.StartsWith("windows-", [StringComparison]::Ordinal)) {
    $isWindowsHost = $false
    try {
        $isWindowsHost = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)
    } catch {
        $isWindowsHost = [string]::Equals($env:OS, "Windows_NT", [StringComparison]::OrdinalIgnoreCase)
    }
    if (-not $isWindowsHost) {
        foreach ($scriptName in $scriptNames) {
            & chmod +x (Join-Path $stagePath $scriptName)
            if ($LASTEXITCODE -ne 0) {
                throw "Could not mark '$scriptName' executable."
            }
        }
    }
}

Write-Host "Staged $($scriptNames -join ', ') for $AssetSuffix."
