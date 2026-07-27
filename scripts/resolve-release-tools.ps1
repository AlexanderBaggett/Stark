#requires -Version 7.0

param(
    [string] $RepositoryRoot = "",

    [string] $DotNetPath = "dotnet",

    [string] $ReleaseToolsPath = "",

    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$rootCandidate = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    Join-Path $PSScriptRoot ".."
} else {
    $RepositoryRoot
}
$root = (Resolve-Path -LiteralPath $rootCandidate).Path

$dotnet = Get-Command $DotNetPath -CommandType Application -ErrorAction Stop |
    Select-Object -First 1
if ($null -eq $dotnet -or [string]::IsNullOrWhiteSpace($dotnet.Source)) {
    throw ".NET executable '$DotNetPath' could not be resolved."
}

$globalJson = Get-Content -LiteralPath (Join-Path $root "global.json") -Raw | ConvertFrom-Json
$requiredSdk = [string]$globalJson.sdk.version
$actualSdk = [string](& $dotnet.Source --version)
if ($LASTEXITCODE -ne 0 -or $actualSdk.Trim() -cne $requiredSdk) {
    throw "Stark.ReleaseTools requires the repository-pinned .NET SDK '$requiredSdk'; '$($dotnet.Source)' reported '$($actualSdk.Trim())'."
}

$project = Join-Path $root "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj"
$assembly = Join-Path $root "eng/release/Stark.ReleaseTools/bin/Release/net10.0/Stark.ReleaseTools.dll"
if (-not [string]::IsNullOrWhiteSpace($ReleaseToolsPath)) {
    $candidate = if ([System.IO.Path]::IsPathRooted($ReleaseToolsPath)) {
        [System.IO.Path]::GetFullPath($ReleaseToolsPath)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $root $ReleaseToolsPath))
    }
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [string]::Equals($candidate, $assembly, $comparison) -or
        -not (Test-Path -LiteralPath $candidate -PathType Leaf) -or
        ((Get-Item -LiteralPath $candidate -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Stark.ReleaseTools must be the regular repository build output '$assembly'; received '$candidate'."
    }
    Write-Output $candidate
    return
}

if (-not $NoBuild) {
    $buildOutput = @(& $dotnet.Source build $project --configuration Release --nologo --verbosity minimal 2>&1)
    foreach ($line in $buildOutput) {
        Write-Host $line
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Building Stark.ReleaseTools failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw "Stark.ReleaseTools output '$assembly' does not exist."
}
Write-Output $assembly
