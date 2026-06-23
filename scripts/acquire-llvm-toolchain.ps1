param(
    [Parameter(Mandatory = $true)]
    [string] $AssetSuffix,

    [string] $ManifestPath = "scripts/llvm-22.1.8-assets.json",

    [string] $OutputDir = "artifacts/toolchain",

    [string] $CacheDir = "artifacts/llvm-cache",

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repositoryRoot $Path
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

    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null

    $name = [string] $Asset.name
    $url = [string] $Asset.url
    $sha256 = [string] $Asset.sha256
    $destination = Join-Path $DestinationDirectory $name

    if ($Force -or -not (Test-Path -LiteralPath $destination -PathType Leaf)) {
        Invoke-WebRequest -Uri $url -OutFile $destination
    }

    Assert-Sha256 -Path $destination -ExpectedSha256 $sha256
    return $destination
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

    $source = Join-Path $SourceRoot $RelativePath
    if (-not (Test-Path -LiteralPath $source)) {
        if ($Required) {
            throw "Required LLVM path '$RelativePath' was not found in '$SourceRoot'."
        }

        return @()
    }

    $destination = Join-Path $DestinationRoot $RelativePath
    if (Test-Path -LiteralPath $source -PathType Container) {
        Copy-DirectoryContents -Source $source -Destination $destination
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
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

    $matches = @(Get-ChildItem -Path (Join-Path $SourceRoot $Pattern) -Force -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) {
        if ($Required) {
            throw "Required LLVM path pattern '$Pattern' matched no files in '$SourceRoot'."
        }

        return @()
    }

    $copied = @()
    foreach ($item in ($matches | Sort-Object FullName)) {
        $relativePath = [System.IO.Path]::GetRelativePath($SourceRoot, $item.FullName).Replace('\', '/')
        $destination = Join-Path $DestinationRoot $relativePath

        if ($item.PSIsContainer) {
            Copy-DirectoryContents -Source $item.FullName -Destination $destination
        } else {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $item.FullName -Destination $destination -Force
        }

        $copied += $relativePath
    }

    return $copied
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
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName)) {
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $files += [ordered]@{
            path = $relativePath
            bytes = $file.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        }
    }

    return $files
}

$manifestFullPath = Resolve-RepositoryPath -Path $ManifestPath
$outputRoot = Resolve-RepositoryPath -Path $OutputDir
$cacheRoot = Resolve-RepositoryPath -Path $CacheDir

if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) {
    throw "LLVM asset manifest '$manifestFullPath' does not exist."
}

$manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json
$platforms = Get-JsonProperty -Object $manifest -Name "platforms"
$platform = Get-JsonProperty -Object $platforms -Name $AssetSuffix
$archive = Get-JsonProperty -Object $platform -Name "archive"
$llvmVersion = [string] (Get-JsonProperty -Object $manifest -Name "llvmVersion")

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null

$assetCacheRoot = Join-Path $cacheRoot $AssetSuffix
$archivePath = Save-Asset -Asset $archive -DestinationDirectory $assetCacheRoot
Save-Asset -Asset $archive.signature -DestinationDirectory $assetCacheRoot | Out-Null
Save-Asset -Asset $archive.attestation -DestinationDirectory $assetCacheRoot | Out-Null
Save-Asset -Asset $manifest.sourceArchive.signature -DestinationDirectory $assetCacheRoot | Out-Null
Save-Asset -Asset $manifest.sourceArchive.attestation -DestinationDirectory $assetCacheRoot | Out-Null

$workRoot = Join-Path $assetCacheRoot "work"
if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
$extractRoot = Join-Path $workRoot "extract"
New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null

tar -xf $archivePath -C $extractRoot
if ($LASTEXITCODE -ne 0) {
    throw "Failed to extract '$archivePath'."
}

$payloadRoot = Get-ExtractedRoot -ExtractDirectory $extractRoot

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$copiedRoots = @()
foreach ($root in (Get-ArrayValues -Value $manifest.copiedRoots)) {
    $copiedRoots += Copy-RelativePath -SourceRoot $payloadRoot -DestinationRoot $outputRoot -RelativePath ([string] $root) -Required $true
}

$requiredTools = @()
foreach ($tool in (Get-ArrayValues -Value $platform.requiredTools)) {
    $requiredTools += Copy-RelativePath -SourceRoot $payloadRoot -DestinationRoot $outputRoot -RelativePath ([string] $tool) -Required $true
}

$requiredPatternMatches = @()
foreach ($pattern in (Get-ArrayValues -Value $platform.requiredPatterns)) {
    $requiredPatternMatches += Copy-Pattern -SourceRoot $payloadRoot -DestinationRoot $outputRoot -Pattern ([string] $pattern) -Required $true
}

$provenanceRoot = Join-Path $outputRoot "provenance"
New-Item -ItemType Directory -Force -Path $provenanceRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $assetCacheRoot $archive.signature.name) -Destination (Join-Path $provenanceRoot $archive.signature.name) -Force
Copy-Item -LiteralPath (Join-Path $assetCacheRoot $archive.attestation.name) -Destination (Join-Path $provenanceRoot $archive.attestation.name) -Force
Copy-Item -LiteralPath (Join-Path $assetCacheRoot $manifest.sourceArchive.signature.name) -Destination (Join-Path $provenanceRoot $manifest.sourceArchive.signature.name) -Force
Copy-Item -LiteralPath (Join-Path $assetCacheRoot $manifest.sourceArchive.attestation.name) -Destination (Join-Path $provenanceRoot $manifest.sourceArchive.attestation.name) -Force

$licenseRoot = Join-Path $outputRoot "licenses"
New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null
$licenseFiles = @()
foreach ($pattern in (Get-ArrayValues -Value $manifest.licenseFilePatterns)) {
    foreach ($file in (Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like ([string] $pattern) } | Sort-Object FullName)) {
        $relativePath = [System.IO.Path]::GetRelativePath($payloadRoot, $file.FullName).Replace('\', '/')
        $destination = Join-Path $licenseRoot $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        $licenseFiles += $relativePath
    }
}

$toolchainManifest = [ordered]@{
    schemaVersion = 1
    llvmVersion = $llvmVersion
    releaseTag = [string] $manifest.releaseTag
    releaseUrl = [string] $manifest.releaseUrl
    assetSuffix = $AssetSuffix
    runtimeIdentifier = [string] $platform.runtimeIdentifier
    binaryArchive = [ordered]@{
        name = [string] $archive.name
        url = [string] $archive.url
        sha256 = [string] $archive.sha256
        size = [int64] $archive.size
        signature = [ordered]@{
            name = [string] $archive.signature.name
            url = [string] $archive.signature.url
            sha256 = [string] $archive.signature.sha256
            size = [int64] $archive.signature.size
        }
        attestation = [ordered]@{
            name = [string] $archive.attestation.name
            url = [string] $archive.attestation.url
            sha256 = [string] $archive.attestation.sha256
            size = [int64] $archive.attestation.size
        }
    }
    sourceArchive = [ordered]@{
        name = [string] $manifest.sourceArchive.name
        url = [string] $manifest.sourceArchive.url
        sha256 = [string] $manifest.sourceArchive.sha256
        size = [int64] $manifest.sourceArchive.size
    }
    copiedRoots = $copiedRoots
    requiredTools = $requiredTools
    requiredPatternMatches = $requiredPatternMatches
    licenseFiles = $licenseFiles
    files = Get-OutputFileManifest -Root $outputRoot
}

$toolchainManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $outputRoot "manifest.json") -Encoding utf8

Write-Host "Prepared LLVM $llvmVersion toolchain for $AssetSuffix at $outputRoot"
