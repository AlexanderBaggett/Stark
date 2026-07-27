param(
    [Parameter(Mandatory = $true)]
    [string] $ArchivePath,

    [string] $TargetTriple = "",

    [string] $WorkDir = "",

    [string] $ReportPath = "",

    [string] $DiagnosticsDir = "",

    [switch] $KeepWorkDir,

    [switch] $KeepWorkDirOnFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$releaseDocumentationContractScript = Join-Path $PSScriptRoot "release-documentation-contract.ps1"
if (-not (Test-Path -LiteralPath $releaseDocumentationContractScript -PathType Leaf)) {
    throw "Release documentation contract helper '$releaseDocumentationContractScript' is missing."
}
. $releaseDocumentationContractScript

$releaseArchiveExtractionScript = Join-Path $PSScriptRoot "release-archive-extraction.ps1"
if (-not (Test-Path -LiteralPath $releaseArchiveExtractionScript -PathType Leaf)) {
    throw "Release archive extraction helper '$releaseArchiveExtractionScript' is missing."
}
. $releaseArchiveExtractionScript

$starkEnvironmentNames = @(
    "STARK_PATH",
    "STARK_HOME",
    "STARK_SDK_ROOT",
    "STARK_TOOLCHAIN_DIR",
    "STARK_LLVM_LIB",
    "STARK_CLANG",
    "STARK_LINKER",
    "STARK_ARCHIVER"
)

function Resolve-InputFile {
    param([Parameter(Mandatory = $true)] [string] $Path)

    $candidate = if ([System.IO.Path]::IsPathRooted($Path)) {
        $Path
    } else {
        Join-Path (Get-Location).Path $Path
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Release archive '$candidate' does not exist."
    }
    return (Resolve-Path -LiteralPath $candidate).Path
}

function Resolve-OutputPath {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
}

function New-InstallSmokeRoot {
    $parent = if ([string]::IsNullOrWhiteSpace($WorkDir)) {
        [System.IO.Path]::GetTempPath()
    } else {
        [System.IO.Path]::GetFullPath($WorkDir)
    }
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $root = Join-Path $parent "stark-install-smoke-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $root | Out-Null
    if (-not $IsWindows) {
        $physicalOutput = @(& /bin/sh -c 'cd -- "$1" && pwd -P' stark-install-smoke $root 2>$null)
        if ($LASTEXITCODE -ne 0 -or $physicalOutput.Count -ne 1 -or
            [string]::IsNullOrWhiteSpace([string]$physicalOutput[0])) {
            throw "Could not resolve physical installer-smoke path '$root'."
        }

        return ([string]$physicalOutput[0]).Trim()
    }

    return (Resolve-Path -LiteralPath $root).Path
}

function Get-ArchiveRoot {
    param([Parameter(Mandatory = $true)] [string] $ExtractRoot)

    $entries = @(Get-ChildItem -LiteralPath $ExtractRoot -Force | Sort-Object Name)
    if ($entries.Count -ne 1 -or -not $entries[0].PSIsContainer) {
        $names = if ($entries.Count -eq 0) { "<none>" } else { ($entries.Name -join ", ") }
        throw "Release archive must contain exactly one top-level SDK directory; found $($entries.Count): $names."
    }
    if (($entries[0].Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release archive root '$($entries[0].FullName)' must not be a reparse point."
    }
    return $entries[0].FullName
}

function Get-RequiredJsonString {
    param(
        [Parameter(Mandatory = $true)] [object] $Object,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $Context
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "$Context is missing required property '$Name'."
    }
    return [string]$property.Value
}

function Get-CurrentPowerShellPath {
    $processPath = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrWhiteSpace($processPath) -or
        -not (Test-Path -LiteralPath $processPath -PathType Leaf)) {
        throw "Could not resolve the current PowerShell executable."
    }
    return $processPath
}

function Invoke-IsolatedProcess {
    param(
        [Parameter(Mandatory = $true)] [string] $File,
        [string[]] $Arguments = @(),
        [string] $WorkingDirectory = "",
        [string] $StdoutPath = "",
        [string] $StderrPath = ""
    )

    $displayArguments = @($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    })
    Write-Host ">> $File $($displayArguments -join ' ')"

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $File
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    foreach ($name in $starkEnvironmentNames) {
        [void]$startInfo.Environment.Remove($name)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Could not start '$File'."
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()

    if (-not [string]::IsNullOrWhiteSpace($StdoutPath)) {
        [System.IO.File]::WriteAllText($StdoutPath, $stdout, [System.Text.UTF8Encoding]::new($false))
    }
    if (-not [string]::IsNullOrWhiteSpace($StderrPath)) {
        [System.IO.File]::WriteAllText($StderrPath, $stderr, [System.Text.UTF8Encoding]::new($false))
    }
    if (-not [string]::IsNullOrWhiteSpace($stdout)) {
        Write-Host $stdout.TrimEnd()
    }
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host $stderr.TrimEnd()
    }
    if ($process.ExitCode -ne 0) {
        throw "Command '$File' exited with code $($process.ExitCode)."
    }

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Test-SamePath {
    param([string] $Left, [string] $Right)

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    return [string]::Equals(
        [System.IO.Path]::GetFullPath($Left).TrimEnd('\', '/'),
        [System.IO.Path]::GetFullPath($Right).TrimEnd('\', '/'),
        $comparison)
}

function Test-PathWithinRoot {
    param([string] $Path, [string] $Root)

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $candidate = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    return [string]::Equals($candidate, $rootPath, $comparison) -or
        $candidate.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Write-DiagnosticInventory {
    param(
        [string] $Root,
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return
    }
    $lines = foreach ($item in (Get-ChildItem -LiteralPath $Root -Force -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName)) {
        $relative = [System.IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
        $kind = if ($item.PSIsContainer) { "directory" } elseif (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { "link" } else { "file" }
        "$kind`t$relative"
    }
    [System.IO.File]::WriteAllLines($Path, [string[]]$lines, [System.Text.UTF8Encoding]::new($false))
}

$archive = Resolve-InputFile -Path $ArchivePath
$reportOutput = Resolve-OutputPath -Path $ReportPath
$diagnosticOutput = Resolve-OutputPath -Path $DiagnosticsDir
$smokeRoot = New-InstallSmokeRoot
$extractRoot = Join-Path $smokeRoot "extract"
$installPrefix = Join-Path $smokeRoot "installed-sdk"
$sourceRoot = Join-Path $smokeRoot "external-source"
New-Item -ItemType Directory -Path $extractRoot, $sourceRoot | Out-Null
if ($null -ne $diagnosticOutput) {
    New-Item -ItemType Directory -Force -Path $diagnosticOutput | Out-Null
}

$startedUtc = [DateTime]::UtcNow
$archiveSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
$packageRoot = $null
$installedCompiler = $null
$receiptPath = $null
$installSucceeded = $false
$doctorSucceeded = $false
$documentationContractSucceeded = $false
$checkSucceeded = $false
$executableSucceeded = $false
$vendorSucceeded = $false
$uninstallSucceeded = $false
$cleanupError = $null
$failureMessage = $null
$releaseVersion = $null
$assetSuffix = $null
$effectiveTargetTriple = $TargetTriple.Trim()
$pathBefore = $env:PATH

try {
    Write-Host "Preflighting and safely extracting candidate archive '$archive' into isolated root '$extractRoot'."
    Expand-ValidatedReleaseArchive -ArchivePath $archive -DestinationPath $extractRoot | Out-Null

    $packageRoot = Get-ArchiveRoot -ExtractRoot $extractRoot
    $releasePath = Join-Path $packageRoot "release.json"
    if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf)) {
        throw "Extracted release is missing release.json."
    }
    $release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
    $releaseVersion = Get-RequiredJsonString -Object $release -Name "starkVersion" -Context "release.json"
    $assetSuffix = Get-RequiredJsonString -Object $release -Name "assetSuffix" -Context "release.json"
    $releaseTargetTriple = Get-RequiredJsonString -Object $release -Name "defaultTargetTriple" -Context "release.json"
    if ([string]::IsNullOrWhiteSpace($effectiveTargetTriple)) {
        $effectiveTargetTriple = $releaseTargetTriple
    } elseif (-not [string]::Equals($effectiveTargetTriple, $releaseTargetTriple, [StringComparison]::Ordinal)) {
        throw "Requested target '$effectiveTargetTriple' does not match release target '$releaseTargetTriple'."
    }
    $expectedRootName = "stark-$releaseVersion-$assetSuffix"
    if (-not [string]::Equals((Split-Path -Leaf $packageRoot), $expectedRootName, [StringComparison]::Ordinal)) {
        throw "Archive root does not match release identity '$expectedRootName'."
    }

    if ($IsWindows) {
        $installScript = Join-Path $packageRoot "install.ps1"
        $uninstallScript = Join-Path $packageRoot "uninstall.ps1"
        $installedCompiler = Join-Path $installPrefix "bin/stark.exe"
        $receiptPath = Join-Path $installPrefix ".stark-install-receipt.json"
        $powershellPath = Get-CurrentPowerShellPath
        $installCommand = $powershellPath
        $installArguments = @(
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $installScript,
            "-Destination", $installPrefix, "-NoPath", "-NonInteractive", "-ArchiveSha256", $archiveSha256)
        $uninstallCommand = $powershellPath
        $uninstallArguments = @(
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $uninstallScript,
            "-Destination", $installPrefix, "-NonInteractive")
    } else {
        $installScript = Join-Path $packageRoot "install.sh"
        $uninstallScript = Join-Path $packageRoot "uninstall.sh"
        $installedCompiler = Join-Path $installPrefix "bin/stark"
        $receiptPath = Join-Path $installPrefix ".stark-install-receipt"
        $installCommand = "/bin/sh"
        $installArguments = @(
            $installScript, "--prefix", $installPrefix, "--no-path", "--non-interactive",
            "--archive-sha256", $archiveSha256)
        $uninstallCommand = "/bin/sh"
        $uninstallArguments = @($uninstallScript, "--prefix", $installPrefix, "--non-interactive")
    }

    foreach ($script in @($installScript, $uninstallScript)) {
        if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
            throw "Archive-local lifecycle script '$script' is missing."
        }
    }

    $installStdout = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "installer.stdout.log" }
    $installStderr = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "installer.stderr.log" }
    Invoke-IsolatedProcess `
        -File $installCommand `
        -Arguments $installArguments `
        -WorkingDirectory $sourceRoot `
        -StdoutPath $installStdout `
        -StderrPath $installStderr | Out-Null
    $installSucceeded = $true

    if (-not (Test-Path -LiteralPath $installedCompiler -PathType Leaf) -or
        -not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "Installer did not create the installed compiler and ownership receipt."
    }
    if (Test-PathWithinRoot -Path $installedCompiler -Root $packageRoot) {
        throw "Installed compiler still resolves inside the extracted source SDK."
    }
    if (-not [string]::Equals($env:PATH, $pathBefore, [StringComparison]::Ordinal)) {
        throw "The no-PATH installer changed the current process PATH."
    }
    if ($null -ne $diagnosticOutput) {
        Copy-Item -LiteralPath $receiptPath -Destination (Join-Path $diagnosticOutput (Split-Path -Leaf $receiptPath)) -Force
    }

    $doctorStdout = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "doctor.json" }
    $doctorStderr = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "doctor.stderr.log" }
    $doctorResult = Invoke-IsolatedProcess `
        -File $installedCompiler `
        -Arguments @("doctor", "--strict", "--format", "json", "--target", $effectiveTargetTriple) `
        -WorkingDirectory $sourceRoot `
        -StdoutPath $doctorStdout `
        -StderrPath $doctorStderr
    try {
        $doctor = $doctorResult.Stdout | ConvertFrom-Json
    } catch {
        throw "Installed compiler doctor returned invalid JSON: $($doctorResult.Stdout)"
    }
    $doctorStatus = Get-RequiredJsonString -Object $doctor -Name "status" -Context "doctor report"
    $doctorSdk = $doctor.PSObject.Properties["sdk"].Value
    $doctorSdkStatus = Get-RequiredJsonString -Object $doctorSdk -Name "status" -Context "doctor sdk report"
    $doctorSdkRoot = Get-RequiredJsonString -Object $doctorSdk -Name "root" -Context "doctor sdk report"
    $doctorCompiler = $doctor.PSObject.Properties["compiler"].Value
    $doctorCompilerPath = Get-RequiredJsonString -Object $doctorCompiler -Name "path" -Context "doctor compiler report"
    if ($doctorStatus -ne "ok" -or $doctorSdkStatus -ne "ok") {
        throw "Installed compiler doctor did not report an ok compiler/SDK state."
    }
    if (-not (Test-SamePath -Left $doctorSdkRoot -Right $installPrefix) -or
        -not (Test-PathWithinRoot -Path $doctorCompilerPath -Root $installPrefix)) {
        throw "Installed compiler doctor selected SDK/compiler state outside '$installPrefix'."
    }
    $doctorSucceeded = $true

    $documentedCommandRoot = Copy-ReleaseDocumentationQuickStartInputs `
        -SdkRoot $installPrefix `
        -DestinationRoot (Join-Path $sourceRoot "documented-quick-start")
    [void](Invoke-ReleaseDocumentationCommandContract `
        -SdkRoot $installPrefix `
        -ExpectedTargetTriple $effectiveTargetTriple `
        -ExecutionRoot $documentedCommandRoot `
        -CompilerInvoker {
            param([string[]] $Arguments, [string] $WorkingDirectory)
            Invoke-IsolatedProcess `
                -File $installedCompiler `
                -Arguments $Arguments `
                -WorkingDirectory $WorkingDirectory
        })
    $documentationContractSucceeded = $true

    $sourcePath = Join-Path $sourceRoot "ReleaseInstallSmoke.stark"
    [System.IO.File]::WriteAllText(
        $sourcePath,
        @'
import System.Console
module ReleaseInstallSmoke

export fn i32[min max] main()
{
    WriteLine("installed SDK smoke");
    return 0;
}
'@,
        [System.Text.UTF8Encoding]::new($false))
    $checkStdout = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "check.stdout.log" }
    $checkStderr = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "check.stderr.log" }
    $checkResult = Invoke-IsolatedProcess `
        -File $installedCompiler `
        -Arguments @($sourcePath, "--check", "--target", $effectiveTargetTriple, "--no-stark-path") `
        -WorkingDirectory $sourceRoot `
        -StdoutPath $checkStdout `
        -StderrPath $checkStderr
    if (-not $checkResult.Stdout.Contains("Check succeeded.", [StringComparison]::Ordinal)) {
        throw "Installed compiler check did not report success."
    }
    $checkSucceeded = $true

    $executablePath = Join-Path $sourceRoot $(if ($IsWindows) { "ReleaseInstallSmoke.exe" } else { "ReleaseInstallSmoke" })
    $compileStdout = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "executable-build.stdout.log" }
    $compileStderr = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "executable-build.stderr.log" }
    Invoke-IsolatedProcess `
        -File $installedCompiler `
        -Arguments @(
            $sourcePath, "--emit-exe", "-o", $executablePath,
            "--target", $effectiveTargetTriple, "--no-stark-path") `
        -WorkingDirectory $sourceRoot `
        -StdoutPath $compileStdout `
        -StderrPath $compileStderr | Out-Null
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf) -or
        (Get-Item -LiteralPath $executablePath).Length -le 0) {
        throw "Installed compiler did not create the external executable '$executablePath'."
    }
    $runStdout = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "executable-run.stdout.log" }
    $runStderr = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "executable-run.stderr.log" }
    $runResult = Invoke-IsolatedProcess `
        -File $executablePath `
        -WorkingDirectory $sourceRoot `
        -StdoutPath $runStdout `
        -StderrPath $runStderr
    if (-not $runResult.Stdout.Contains("installed SDK smoke", [StringComparison]::Ordinal)) {
        throw "Installed-SDK executable did not produce its expected output."
    }
    $executableSucceeded = $true

    $vendorSourcePath = Join-Path $sourceRoot "ReleaseInstallVendorSQLiteSmoke.stark"
    [System.IO.File]::WriteAllText(
        $vendorSourcePath,
        @'
import Vendor.SQLite
module ReleaseInstallVendorSQLiteSmoke

export fn i32[min max] main()
{
    if (LibraryVersionNumber() <= 0)
    {
        return 1;
    }
    return 0;
}
'@,
        [System.Text.UTF8Encoding]::new($false))
    $vendorExecutablePath = Join-Path $sourceRoot $(if ($IsWindows) { "ReleaseInstallVendorSQLiteSmoke.exe" } else { "ReleaseInstallVendorSQLiteSmoke" })
    $vendorBuildStdout = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "vendor-sqlite-build.stdout.log" }
    $vendorBuildStderr = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "vendor-sqlite-build.stderr.log" }
    Invoke-IsolatedProcess `
        -File $installedCompiler `
        -Arguments @(
            $vendorSourcePath, "--emit-exe", "-o", $vendorExecutablePath,
            "--target", $effectiveTargetTriple, "--no-stark-path") `
        -WorkingDirectory $sourceRoot `
        -StdoutPath $vendorBuildStdout `
        -StderrPath $vendorBuildStderr | Out-Null
    if (-not (Test-Path -LiteralPath $vendorExecutablePath -PathType Leaf) -or
        (Get-Item -LiteralPath $vendorExecutablePath).Length -le 0) {
        throw "Installed compiler did not link the bundled Vendor.SQLite executable '$vendorExecutablePath'."
    }
    $vendorRunStdout = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "vendor-sqlite-run.stdout.log" }
    $vendorRunStderr = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "vendor-sqlite-run.stderr.log" }
    Invoke-IsolatedProcess `
        -File $vendorExecutablePath `
        -WorkingDirectory $sourceRoot `
        -StdoutPath $vendorRunStdout `
        -StderrPath $vendorRunStderr | Out-Null
    $vendorSucceeded = $true

    $uninstallStdout = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "uninstaller.stdout.log" }
    $uninstallStderr = if ($null -eq $diagnosticOutput) { "" } else { Join-Path $diagnosticOutput "uninstaller.stderr.log" }
    Invoke-IsolatedProcess `
        -File $uninstallCommand `
        -Arguments $uninstallArguments `
        -WorkingDirectory $sourceRoot `
        -StdoutPath $uninstallStdout `
        -StderrPath $uninstallStderr | Out-Null
    if ((Test-Path -LiteralPath $installPrefix) -or
        (Test-Path -LiteralPath $receiptPath) -or
        (Test-Path -LiteralPath $installedCompiler)) {
        throw "Uninstaller left receipt-owned installation state under '$installPrefix'."
    }
    $uninstallSucceeded = $true

    Write-Host "Release installer lifecycle smoke passed: $archive"
} catch {
    $failureMessage = $_.Exception.Message
    Write-Error -ErrorRecord $_ -ErrorAction Continue

    if ($installSucceeded -and -not $uninstallSucceeded -and
        $null -ne $receiptPath -and (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        try {
            Write-Host "Attempting receipt-owned cleanup after lifecycle failure."
            Invoke-IsolatedProcess -File $uninstallCommand -Arguments $uninstallArguments -WorkingDirectory $sourceRoot | Out-Null
        } catch {
            $cleanupError = $_.Exception.Message
            Write-Warning "Lifecycle cleanup failed: $cleanupError"
        }
    }

    throw
} finally {
    if ($null -ne $diagnosticOutput) {
        if ($null -ne $packageRoot) {
            foreach ($name in @("release.json", "sdk.json", "release-files.sha256")) {
                $path = Join-Path $packageRoot $name
                if (Test-Path -LiteralPath $path -PathType Leaf) {
                    Copy-Item -LiteralPath $path -Destination (Join-Path $diagnosticOutput $name) -Force
                }
            }
        }
        Write-DiagnosticInventory -Root $installPrefix -Path (Join-Path $diagnosticOutput "installed-tree.txt")
    }

    $status = if ($null -eq $failureMessage) { "passed" } else { "failed" }
    $report = [ordered]@{
        schemaVersion = 1
        status = $status
        archive = $archive
        archiveSha256 = $archiveSha256
        releaseVersion = $releaseVersion
        assetSuffix = $assetSuffix
        targetTriple = $effectiveTargetTriple
        isolatedWorkRoot = $smokeRoot
        installPrefix = $installPrefix
        pathMutationRequested = $false
        sourceEnvironmentOverridesCleared = $starkEnvironmentNames
        installSucceeded = $installSucceeded
        doctorSucceeded = $doctorSucceeded
        documentationContractSucceeded = $documentationContractSucceeded
        checkSucceeded = $checkSucceeded
        executableSucceeded = $executableSucceeded
        vendorSucceeded = $vendorSucceeded
        uninstallSucceeded = $uninstallSucceeded
        failure = $failureMessage
        cleanupFailure = $cleanupError
        startedUtc = $startedUtc.ToString("O")
        finishedUtc = [DateTime]::UtcNow.ToString("O")
    }
    if ($null -ne $reportOutput) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $reportOutput) | Out-Null
        $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportOutput -Encoding utf8
    }

    $keep = $KeepWorkDir -or ($KeepWorkDirOnFailure -and $null -ne $failureMessage)
    if ($keep) {
        Write-Host "Kept installer smoke work directory: $smokeRoot"
    } else {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
