#requires -Version 7.0

param(
    [Parameter(Mandatory = $true)]
    [string] $InputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedSourceCommit,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedConfigurationSha256,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedReleasePlanSha256,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedTargets,

    [string] $ReleaseToolsPath = "",

    [string] $DotNetPath = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-DirectoryPath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label,
        [switch] $Create
    )

    $candidate = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
    }
    if ($Create) {
        New-Item -ItemType Directory -Force -Path $candidate | Out-Null
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
        throw "$Label '$candidate' does not exist."
    }
    return (Resolve-Path -LiteralPath $candidate).Path
}

function Get-OrdinalFiles {
    param([Parameter(Mandatory = $true)][string] $Root)

    $ordered = [System.Collections.Generic.SortedDictionary[string, System.IO.FileInfo]]::new(
        [StringComparer]::Ordinal)
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force)) {
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Downloaded release artifact '$($file.FullName)' is a link or reparse point."
        }
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $ordered.Add($relativePath, $file)
    }
    return @($ordered.Values)
}

function Read-RequiredJsonObject {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo] $File,
        [Parameter(Mandatory = $true)][string] $Kind
    )

    try {
        $document = [System.IO.File]::ReadAllText($File.FullName) | ConvertFrom-Json -Depth 100
    } catch {
        throw "$Kind '$($File.Name)' is not valid JSON: $($_.Exception.Message)"
    }
    if ($null -eq $document -or $document -isnot [System.Management.Automation.PSCustomObject]) {
        throw "$Kind '$($File.Name)' must contain a JSON object."
    }
    return $document
}

function ConvertTo-CanonicalJsonValue {
    param([AllowNull()][object] $Value)

    if ($null -eq $Value) {
        return $null
    }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $result = [ordered]@{}
        foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) {
            $result[$property.Name] = ConvertTo-CanonicalJsonValue -Value $property.Value
        }
        return [pscustomobject]$result
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in @($Value.Keys | ForEach-Object { [string]$_ } | Sort-Object)) {
            $result[$key] = ConvertTo-CanonicalJsonValue -Value $Value[$key]
        }
        return [pscustomobject]$result
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $Value) {
            $items.Add((ConvertTo-CanonicalJsonValue -Value $item))
        }
        return ,$items.ToArray()
    }
    return $Value
}

function ConvertTo-CanonicalJsonText {
    param([Parameter(Mandatory = $true)][object] $Value)

    return (ConvertTo-CanonicalJsonValue -Value $Value | ConvertTo-Json -Depth 30 -Compress)
}

function Get-ExpectedTargetSet {
    param([Parameter(Mandatory = $true)][string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Trim() -cne $Value) {
        throw "ExpectedTargets must be the prepare job's non-empty canonical comma-separated target list."
    }
    $result = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($target in $Value.Split(',')) {
        if ($target -cnotmatch '^(?:linux|macos|windows)-(?:x64|arm64)$') {
            throw "ExpectedTargets contains invalid target '$target'."
        }
        if (-not $result.Add($target)) {
            throw "ExpectedTargets duplicates target '$target'."
        }
    }
    return ,$result
}

function Invoke-CandidateBindingInspector {
    param(
        [Parameter(Mandatory = $true)][string] $DotNet,
        [Parameter(Mandatory = $true)][string] $Inspector,
        [Parameter(Mandatory = $true)][string] $Archive
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $DotNet
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @($Inspector, "candidate-evidence", "--archive", $Archive)) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Could not start release candidate binding inspector."
    }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Release candidate binding inspection failed for '$Archive': $($stderr.Trim())"
        }
    } finally {
        $process.Dispose()
    }
    try {
        $binding = $stdout | ConvertFrom-Json -Depth 30
    } catch {
        throw "Release candidate binding inspector returned invalid JSON for '$Archive': $($_.Exception.Message)"
    }
    if ($null -eq $binding -or $binding -isnot [System.Management.Automation.PSCustomObject]) {
        throw "Release candidate binding inspector did not return one JSON object for '$Archive'."
    }
    return $binding
}

function Assert-EvidenceReport {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo] $File,
        [Parameter(Mandatory = $true)][string] $Kind,
        [Parameter(Mandatory = $true)][string] $Target,
        [Parameter(Mandatory = $true)][string] $Version,
        [Parameter(Mandatory = $true)][object] $ExpectedCandidateBinding
    )

    $report = Read-RequiredJsonObject -File $File -Kind $Kind
    if ($report.schemaVersion -ne 1) {
        throw "$Kind '$($File.Name)' has unsupported or missing schemaVersion."
    }
    if ([string]$report.validationScope -cne "release-candidate") {
        throw "$Kind '$($File.Name)' is not scoped to a staged release candidate."
    }
    $bindingProperty = $report.PSObject.Properties["candidateBinding"]
    if ($null -eq $bindingProperty -or $null -eq $bindingProperty.Value) {
        throw "$Kind '$($File.Name)' has no candidateBinding."
    }
    $actualBinding = ConvertTo-CanonicalJsonText -Value $bindingProperty.Value
    $expectedBinding = ConvertTo-CanonicalJsonText -Value $ExpectedCandidateBinding
    if ($actualBinding -cne $expectedBinding) {
        throw "$Kind '$($File.Name)' candidateBinding does not exactly identify its release archive."
    }
    $validatedProperty = $report.PSObject.Properties["validatedCandidate"]
    if ($null -eq $validatedProperty -or $null -eq $validatedProperty.Value) {
        throw "$Kind '$($File.Name)' has no native staged-candidate validation subject."
    }
    $validated = $validatedProperty.Value
    if ([string]$validated.kind -cne "stark-staged-release-validation-subject" -or
        [int]$validated.schemaVersion -ne 1 -or
        [string]$validated.root -cne [string]$ExpectedCandidateBinding.stagedSdk.root -or
        [string]$validated.releaseJson.sha256 -cne [string]$ExpectedCandidateBinding.stagedSdk.releaseJsonSha256 -or
        [string]$validated.releaseFiles.sha256 -cne [string]$ExpectedCandidateBinding.stagedSdk.releaseFilesSha256 -or
        [string]$validated.release.version -cne [string]$ExpectedCandidateBinding.release.version -or
        [string]$validated.release.targetId -cne [string]$ExpectedCandidateBinding.release.targetId -or
        [string]$validated.release.runtimeIdentifier -cne [string]$ExpectedCandidateBinding.release.runtimeIdentifier -or
        [string]$validated.release.sourceCommit -cne [string]$ExpectedCandidateBinding.sourceCommit -or
        [string]$validated.release.configurationSha256 -cne [string]$ExpectedCandidateBinding.configuration.sha256 -or
        [string]$validated.release.planSha256 -cne [string]$ExpectedCandidateBinding.plan.sha256 -or
        [string]$validated.release.buildIdentity -cne [string]$ExpectedCandidateBinding.release.identity.value) {
        throw "$Kind '$($File.Name)' did not natively validate the bound staged release candidate."
    }
    switch ($Kind) {
        "Managed dependency report" {
            $runtimeIdentifiers = @{
                "linux-x64" = "linux-x64"
                "linux-arm64" = "linux-arm64"
                "macos-x64" = "osx-x64"
                "macos-arm64" = "osx-arm64"
                "windows-x64" = "win-x64"
                "windows-arm64" = "win-arm64"
            }
            if ([string]$report.status -cne "ready" -or
                [string]$report.targetId -cne $Target -or
                [string]$report.runtimeIdentifier -cne [string]$runtimeIdentifiers[$Target]) {
                throw "$Kind '$($File.Name)' is not a ready report for target '$Target'."
            }
            foreach ($pathProperty in @("nugetConfig", "lockFile")) {
                $path = [string]$report.$pathProperty
                if ([string]::IsNullOrWhiteSpace($path) -or
                    [System.IO.Path]::IsPathRooted($path) -or
                    $path.Contains('\') -or
                    $path.Split('/') -contains "..") {
                    throw "$Kind '$($File.Name)' has non-portable '$pathProperty' evidence."
                }
            }
        }
        "Native dependency report" {
            if ([string]$report.status -cne "ok" -or [string]$report.assetSuffix -cne $Target) {
                throw "$Kind '$($File.Name)' is not a successful report for target '$Target'."
            }
            if ([string]$report.sdkRoot -cne "stark-$Version-$Target") {
                throw "$Kind '$($File.Name)' identifies the wrong staged SDK root."
            }
        }
        "Stage validation report" {
            if ([string]$report.status -cne "ok" -or [string]$report.targetId -cne $Target) {
                throw "$Kind '$($File.Name)' is not a successful report for target '$Target'."
            }
            if ([string]$report.sdkRoot -cne "stark-$Version-$Target") {
                throw "$Kind '$($File.Name)' identifies the wrong staged SDK root."
            }
        }
        default {
            throw "Unknown release evidence kind '$Kind'."
        }
    }
    return $validated
}

$portableVersionPattern = '^[A-Za-z0-9][A-Za-z0-9._+\-]*$'
$sourceCommitPattern = '^(?:[0-9a-f]{40}|[0-9a-f]{64})$'
$sha256Pattern = '^[0-9a-f]{64}$'
if ($ExpectedVersion -cnotmatch $portableVersionPattern) {
    throw "ExpectedVersion '$ExpectedVersion' is not a portable release version."
}
if ($ExpectedSourceCommit -cnotmatch $sourceCommitPattern) {
    throw "ExpectedSourceCommit must be the prepare job's exact lowercase source commit."
}
if ($ExpectedConfigurationSha256 -cnotmatch $sha256Pattern) {
    throw "ExpectedConfigurationSha256 must be the prepare job's lowercase SHA-256."
}
if ($ExpectedReleasePlanSha256 -cnotmatch $sha256Pattern) {
    throw "ExpectedReleasePlanSha256 must be the prepare job's lowercase SHA-256."
}
$expectedTargetSet = Get-ExpectedTargetSet -Value $ExpectedTargets

$inputRoot = Resolve-DirectoryPath -Path $InputDirectory -Label "Downloaded release-asset directory"
$outputRoot = Resolve-DirectoryPath -Path $OutputDirectory -Label "Public release-asset directory" -Create
if ([string]::Equals($inputRoot, $outputRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "InputDirectory and OutputDirectory must be different directories."
}
if ((Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
    throw "Public release-asset directory '$outputRoot' must be empty."
}
$dotnetCommand = Get-Command $DotNetPath -CommandType Application -ErrorAction Stop | Select-Object -First 1
$inspectorPath = @(& (Join-Path $PSScriptRoot "resolve-release-tools.ps1") `
    -RepositoryRoot (Join-Path $PSScriptRoot "..") `
    -DotNetPath $dotnetCommand.Source `
    -ReleaseToolsPath $ReleaseToolsPath) | Select-Object -Last 1

$archivePattern = '^stark-(?<version>[A-Za-z0-9][A-Za-z0-9._+\-]*?)-(?<target>(?<os>linux|macos|windows)-(?<arch>x64|arm64))\.(?<extension>tar\.gz|zip)$'
$archiveChecksumPattern = '^stark-[A-Za-z0-9][A-Za-z0-9._+\-]*-(?:linux|macos|windows)-(?:x64|arm64)\.(?:tar\.gz|zip)\.sha256$'
$evidencePattern = '^(?<kind>managed-dependencies|native-dependencies|stage-validation)-(?<target>(?:linux|macos|windows)-(?:x64|arm64))\.json$'
$diagnosticPattern = '^(?:native-dependencies|stage-validation)-(?:linux|macos|windows)-(?:x64|arm64)\.log$'

$selected = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
$seenNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in (Get-OrdinalFiles -Root $inputRoot)) {
    if ($file.Name -cmatch $diagnosticPattern) {
        continue
    }
    if ($file.Name -cnotmatch $archivePattern `
        -and $file.Name -cnotmatch $archiveChecksumPattern `
        -and $file.Name -cnotmatch $evidencePattern) {
        throw "Downloaded release artifact '$($file.Name)' is not an approved public asset or diagnostic."
    }
    if (-not $seenNames.Add($file.Name)) {
        throw "Downloaded release artifacts contain duplicate or case-colliding name '$($file.Name)'."
    }
    $selected.Add($file)
}

$archives = @($selected | Where-Object { $_.Name -cmatch $archivePattern })
if ($archives.Count -eq 0) {
    throw "No target release archives were downloaded."
}

$versions = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$archiveTargets = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($archive in $archives) {
    $archiveMatch = [Regex]::Match($archive.Name, $archivePattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    $version = $archiveMatch.Groups["version"].Value
    $target = $archiveMatch.Groups["target"].Value
    $platform = $archiveMatch.Groups["os"].Value
    $extension = $archiveMatch.Groups["extension"].Value
    if ($version -cne $ExpectedVersion) {
        throw "Release archive '$($archive.Name)' does not use expected version '$ExpectedVersion'."
    }
    if (-not $expectedTargetSet.Contains($target)) {
        throw "Release archive '$($archive.Name)' targets unrequested target '$target'."
    }
    $versions.Add($version) | Out-Null
    if (-not $archiveTargets.Add($target)) {
        throw "Downloaded release artifacts contain more than one archive for target '$target'."
    }
    $expectedExtension = if ($platform -ceq "windows") { "zip" } else { "tar.gz" }
    if ($extension -cne $expectedExtension) {
        throw "Release archive '$($archive.Name)' must use '.$expectedExtension' for target '$target'."
    }

    $expectedCandidateBinding = Invoke-CandidateBindingInspector `
        -DotNet $dotnetCommand.Source `
        -Inspector $inspectorPath `
        -Archive $archive.FullName
    if ([string]$expectedCandidateBinding.archive.name -cne $archive.Name -or
        [int64]$expectedCandidateBinding.archive.bytes -ne [int64]$archive.Length) {
        throw "Release candidate binding inspector returned the wrong archive name or byte count for '$($archive.Name)'."
    }
    if ([string]$expectedCandidateBinding.release.version -cne $ExpectedVersion -or
        [string]$expectedCandidateBinding.sourceCommit -cne $ExpectedSourceCommit -or
        [string]$expectedCandidateBinding.configuration.sha256 -cne $ExpectedConfigurationSha256 -or
        [string]$expectedCandidateBinding.plan.sha256 -cne $ExpectedReleasePlanSha256 -or
        [string]$expectedCandidateBinding.release.targetId -cne $target) {
        throw "Release archive '$($archive.Name)' does not match the immutable prepare-job release identity."
    }

    $checksumName = "$($archive.Name).sha256"
    $checksum = @($selected | Where-Object { $_.Name -ceq $checksumName })
    if ($checksum.Count -ne 1) {
        throw "Release archive '$($archive.Name)' must have exactly one adjacent '$checksumName' asset."
    }

    $line = [System.IO.File]::ReadAllText($checksum[0].FullName).Trim()
    $match = [Regex]::Match($line, '^([0-9a-f]{64})  ([^/\\]+)$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success -or $match.Groups[2].Value -cne $archive.Name) {
        throw "Release archive checksum '$checksumName' is malformed or names another file."
    }
    $actualHash = [string]$expectedCandidateBinding.archive.sha256
    if ($actualHash -cne $match.Groups[1].Value) {
        throw "Release archive checksum mismatch for '$($archive.Name)'."
    }

    $validatedSubjectJson = $null
    foreach ($evidence in @(
        @{ Name = "managed-dependencies-$target.json"; Kind = "Managed dependency report" },
        @{ Name = "native-dependencies-$target.json"; Kind = "Native dependency report" },
        @{ Name = "stage-validation-$target.json"; Kind = "Stage validation report" }
    )) {
        $matchingEvidence = @($selected | Where-Object { $_.Name -ceq $evidence.Name })
        if ($matchingEvidence.Count -ne 1) {
            throw "Release target '$target' must have exactly one '$($evidence.Name)' evidence asset."
        }
        $validatedSubject = Assert-EvidenceReport `
            -File $matchingEvidence[0] `
            -Kind $evidence.Kind `
            -Target $target `
            -Version $version `
            -ExpectedCandidateBinding $expectedCandidateBinding
        $actualSubjectJson = ConvertTo-CanonicalJsonText -Value $validatedSubject
        if ($null -eq $validatedSubjectJson) {
            $validatedSubjectJson = $actualSubjectJson
        } elseif ($actualSubjectJson -cne $validatedSubjectJson) {
            throw "Release target '$target' evidence reports did not validate one identical staged candidate."
        }
    }
}
if ($versions.Count -ne 1) {
    throw "All release archives must use one identical release version."
}
if ($archiveTargets.Count -ne $expectedTargetSet.Count) {
    $missingTargets = @($expectedTargetSet | Where-Object { -not $archiveTargets.Contains($_) } | Sort-Object)
    throw "Downloaded release artifacts are missing expected target(s): $($missingTargets -join ', ')."
}

foreach ($checksum in @($selected | Where-Object { $_.Name -cmatch $archiveChecksumPattern })) {
    $archiveName = $checksum.Name.Substring(0, $checksum.Name.Length - ".sha256".Length)
    if (-not ($archives | Where-Object { $_.Name -ceq $archiveName })) {
        throw "Orphan release checksum '$($checksum.Name)' has no matching archive."
    }
}

foreach ($evidence in @($selected | Where-Object { $_.Name -cmatch $evidencePattern })) {
    $match = [Regex]::Match($evidence.Name, $evidencePattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $archiveTargets.Contains($match.Groups["target"].Value)) {
        throw "Orphan release evidence '$($evidence.Name)' has no matching target archive."
    }
}

$publicByName = [System.Collections.Generic.SortedDictionary[string, System.IO.FileInfo]]::new(
    [StringComparer]::Ordinal)
foreach ($file in $selected) {
    $publicByName.Add($file.Name, $file)
}
$publicAssets = @($publicByName.Values)
foreach ($file in $publicAssets) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $outputRoot $file.Name)
}

$checksumLines = foreach ($file in $publicAssets) {
    $publicPath = Join-Path $outputRoot $file.Name
    $hash = (Get-FileHash -LiteralPath $publicPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
[System.IO.File]::WriteAllText(
    (Join-Path $outputRoot "SHA256SUMS.txt"),
    (($checksumLines -join "`n") + "`n"),
    [System.Text.Encoding]::ASCII)

Write-Host "Prepared $($publicAssets.Count + 1) public release asset(s) in '$outputRoot'."
