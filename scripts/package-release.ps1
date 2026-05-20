param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $AssetSuffix,

    [Parameter(Mandatory = $true)]
    [string] $PublishDir,

    [Parameter(Mandatory = $true)]
    [string] $StdlibPackageDir,

    [string] $OutputDir = "artifacts/release",

    [ValidateSet("zip", "targz")]
    [string] $ArchiveKind = "zip"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishPath = (Resolve-Path $PublishDir).Path
$stdlibPackagePath = (Resolve-Path $StdlibPackageDir).Path
if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $outputPath = $OutputDir
} else {
    $outputPath = Join-Path $repositoryRoot $OutputDir
}
$assetBase = "stark-$Version-$AssetSuffix"
$stageParent = Join-Path $outputPath "stage"
$stageRoot = Join-Path $stageParent $assetBase

function Copy-TreeFiltered {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination,

        [string[]] $ExcludedDirectoryNames = @(".stark", ".git", ".vs", ".vscode", "bin", "obj")
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
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

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

Copy-TreeFiltered -Source $publishPath -Destination (Join-Path $stageRoot "compiler") -ExcludedDirectoryNames @()
Copy-TreeFiltered -Source $stdlibPackagePath -Destination (Join-Path $stageRoot "stdlib/dist") -ExcludedDirectoryNames @()
Copy-TreeFiltered -Source (Join-Path $repositoryRoot "stdlib/templates") -Destination (Join-Path $stageRoot "stdlib/templates")
Copy-TreeFiltered -Source (Join-Path $repositoryRoot "docs") -Destination (Join-Path $stageRoot "docs")
Copy-TreeFiltered -Source (Join-Path $repositoryRoot "examples") -Destination (Join-Path $stageRoot "examples")

Copy-Item -LiteralPath (Join-Path $repositoryRoot "README.md") -Destination $stageRoot -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") -Destination $stageRoot -Force
Set-Content -LiteralPath (Join-Path $stageRoot "VERSION") -Value $Version -Encoding utf8

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

if ($ArchiveKind -eq "zip") {
    $archivePath = Join-Path $outputPath "$assetBase.zip"
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Compress-Archive -LiteralPath $stageRoot -DestinationPath $archivePath -Force
} else {
    $archivePath = Join-Path $outputPath "$assetBase.tar.gz"
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    tar -czf $archivePath -C $stageParent $assetBase
}

$archiveFileName = Split-Path -Leaf $archivePath
$checksum = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
$checksumPath = "$archivePath.sha256"
Set-Content -LiteralPath $checksumPath -Value "$checksum  $archiveFileName" -Encoding ascii

if ($env:GITHUB_OUTPUT) {
    "archive_path=$archivePath" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "checksum_path=$checksumPath" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}

Write-Host "Packaged $archivePath"
Write-Host "Wrote $checksumPath"
