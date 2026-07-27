#requires -Version 7.0

param(
    [string] $RepositoryRoot = "",

    [string] $OutputDir = "artifacts/quality",

    [string] $DotNetPath = "dotnet",

    [string] $ReleaseToolsPath = "",

    [string] $BashPath = "bash"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-Executable {
    param(
        [Parameter(Mandatory = $true)][string] $Value,
        [Parameter(Mandatory = $true)][string] $Label
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Label executable was not specified."
    }

    if ([System.IO.Path]::IsPathRooted($Value)) {
        if (-not (Test-Path -LiteralPath $Value -PathType Leaf)) {
            throw "$Label executable '$Value' does not exist."
        }
        return [System.IO.Path]::GetFullPath($Value)
    }

    $command = Get-Command $Value -CommandType Application -ErrorAction Stop | Select-Object -First 1
    if ($null -eq $command -or [string]::IsNullOrWhiteSpace($command.Source)) {
        throw "$Label executable '$Value' could not be resolved."
    }
    return $command.Source
}

function Resolve-RootedPath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $Root $Path))
}

$rootCandidate = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    Join-Path $PSScriptRoot ".."
} else {
    $RepositoryRoot
}
$repositoryRootPath = (Resolve-Path -LiteralPath $rootCandidate).Path
$outputDirectory = Resolve-RootedPath -Root $repositoryRootPath -Path $OutputDir
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$steps = [System.Collections.Generic.List[object]]::new()
$gateStatus = "failed"
$failureMessage = $null
$expectedDotnetVersion = $null
$actualDotnetVersion = $null

function Invoke-QualityProcess {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $Executable,
        [string[]] $Arguments = @(),
        [Parameter(Mandatory = $true)][string] $LogName,
        [string] $ExpectedStdout = ""
    )

    $logPath = Join-Path $outputDirectory $LogName
    $displayArguments = @($Arguments | ForEach-Object {
        if ($_.Contains(' ', [StringComparison]::Ordinal)) { '"' + $_ + '"' } else { $_ }
    })
    $displayCommand = "$Executable $($displayArguments -join ' ')".Trim()
    $step = [ordered]@{
        label = $Label
        command = $displayCommand
        log = [System.IO.Path]::GetRelativePath($outputDirectory, $logPath).Replace('\', '/')
        status = "failed"
        exitCode = $null
        durationMilliseconds = 0L
    }
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    Write-Host ">> [Quality/repository] $Label"
    Write-Host "   $displayCommand"

    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $Executable
        $startInfo.WorkingDirectory = $repositoryRootPath
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true
        $startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        $startInfo.Environment["DOTNET_NOLOGO"] = "1"
        foreach ($argument in $Arguments) {
            [void]$startInfo.ArgumentList.Add($argument)
        }

        $process = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw "Failed to start '$Executable'."
        }
        try {
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            $step.exitCode = $process.ExitCode
        } finally {
            $process.Dispose()
        }

        $logText = @(
            "command: $displayCommand",
            "exitCode: $($step.exitCode)",
            "",
            "--- stdout ---",
            $stdout.TrimEnd(),
            "",
            "--- stderr ---",
            $stderr.TrimEnd(),
            ""
        ) -join [System.Environment]::NewLine
        [System.IO.File]::WriteAllText($logPath, $logText, [System.Text.UTF8Encoding]::new($false))

        if (-not [string]::IsNullOrWhiteSpace($stdout)) {
            Write-Host $stdout.TrimEnd()
        }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            [Console]::Error.WriteLine($stderr.TrimEnd())
        }
        if ($step.exitCode -ne 0) {
            throw "$Label exited with code $($step.exitCode). See '$logPath'."
        }
        if (-not [string]::IsNullOrEmpty($ExpectedStdout) -and $stdout.Trim() -cne $ExpectedStdout) {
            throw "$Label returned '$($stdout.Trim())'; expected '$ExpectedStdout'. See '$logPath'."
        }

        $step.status = "passed"
        return [pscustomobject]@{
            Stdout = $stdout
            Stderr = $stderr
            ExitCode = [int]$step.exitCode
        }
    } catch {
        if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
            [System.IO.File]::WriteAllText(
                $logPath,
                "command: $displayCommand$([System.Environment]::NewLine)error: $($_.Exception.Message)$([System.Environment]::NewLine)",
                [System.Text.UTF8Encoding]::new($false))
        }
        throw
    } finally {
        $stopwatch.Stop()
        $step.durationMilliseconds = $stopwatch.ElapsedMilliseconds
        $steps.Add([pscustomobject]$step)
    }
}

function Write-QualityReport {
    $report = [ordered]@{
        schemaVersion = 1
        gate = "stark-release-quality"
        status = $gateStatus
        repositoryRoot = $repositoryRootPath
        expectedDotnetVersion = $expectedDotnetVersion
        actualDotnetVersion = $actualDotnetVersion
        failure = $failureMessage
        steps = [object[]]$steps
    }
    $json = ($report | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n"
    [System.IO.File]::WriteAllText(
        (Join-Path $outputDirectory "quality-gate-report.json"),
        $json,
        [System.Text.UTF8Encoding]::new($false))
}

try {
    $dotnet = Resolve-Executable -Value $DotNetPath -Label ".NET"
    $bash = Resolve-Executable -Value $BashPath -Label "Bash"
    $powerShell = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrWhiteSpace($powerShell)) {
        throw "Could not resolve the current PowerShell executable."
    }

    $globalJsonPath = Join-Path $repositoryRootPath "global.json"
    $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
    if ($null -eq $globalJson.sdk -or [string]::IsNullOrWhiteSpace([string]$globalJson.sdk.version)) {
        throw "global.json does not declare an exact .NET SDK version."
    }
    $expectedDotnetVersion = [string]$globalJson.sdk.version
    $dotnetVersionResult = Invoke-QualityProcess `
        -Label "Verify exact .NET SDK" `
        -Executable $dotnet `
        -Arguments @("--version") `
        -LogName "dotnet-version.log" `
        -ExpectedStdout $expectedDotnetVersion
    $actualDotnetVersion = $dotnetVersionResult.Stdout.Trim()

    $resolvedReleaseTools = @(& (Join-Path $repositoryRootPath "scripts/resolve-release-tools.ps1") `
        -RepositoryRoot $repositoryRootPath `
        -DotNetPath $dotnet `
        -ReleaseToolsPath $ReleaseToolsPath) | Select-Object -Last 1
    [void](Invoke-QualityProcess `
        -Label "Validate release configuration with Stark.ReleaseTools" `
        -Executable $dotnet `
        -Arguments @($resolvedReleaseTools, "validate-config", "--root", $repositoryRootPath) `
        -LogName "release-tools-validation.log")

    [void](Invoke-QualityProcess `
        -Label "Audit the tracked public repository tree" `
        -Executable $powerShell `
        -Arguments @(
            "-NoProfile", "-NonInteractive", "-File", (Join-Path $repositoryRootPath "scripts/audit-public-repository.ps1"),
            "-RepositoryRoot", $repositoryRootPath,
            "-OutputPath", (Join-Path $outputDirectory "public-tree-audit.json"),
            "-TrackedOnly"
        ) `
        -LogName "public-tree-audit.log")

    [void](Invoke-QualityProcess `
        -Label "Check book and documentation structure" `
        -Executable $bash `
        -Arguments @((Join-Path $repositoryRootPath "scripts/check-book-structure.sh")) `
        -LogName "book-structure.log")

    [void](Invoke-QualityProcess `
        -Label "Restore the complete solution" `
        -Executable $dotnet `
        -Arguments @("restore", (Join-Path $repositoryRootPath "Stark.slnx"), "--nologo") `
        -LogName "solution-restore.log")

    [void](Invoke-QualityProcess `
        -Label "Build the complete solution in Release" `
        -Executable $dotnet `
        -Arguments @(
            "build", (Join-Path $repositoryRootPath "Stark.slnx"),
            "-c", "Release", "--no-restore", "--nologo", "-warnaserror",
            "-p:WriteCompilerLauncherToRepoRoot=false",
            "-bl:$((Join-Path $outputDirectory "release-build.binlog"))"
        ) `
        -LogName "release-build.log")

    $fullTestResults = Join-Path $outputDirectory "full-solution-test-results"
    New-Item -ItemType Directory -Force -Path $fullTestResults | Out-Null
    [void](Invoke-QualityProcess `
        -Label "Run the complete solution test suite in Release" `
        -Executable $dotnet `
        -Arguments @(
            "test", (Join-Path $repositoryRootPath "Stark.slnx"),
            "-c", "Release", "--no-build", "--no-restore", "--nologo",
            "--results-directory", $fullTestResults,
            "--logger", "trx"
        ) `
        -LogName "full-solution-tests.log")

    [void](Invoke-QualityProcess `
        -Label "Check supported book examples" `
        -Executable $bash `
        -Arguments @((Join-Path $repositoryRootPath "scripts/check-book-samples.sh")) `
        -LogName "book-samples.log")

    if (-not $IsWindows) {
        [void](Invoke-QualityProcess `
            -Label "Run standalone Unix installer lifecycle harness" `
            -Executable $bash `
            -Arguments @((Join-Path $repositoryRootPath "tests/release-installers/test-installers.sh")) `
            -LogName "unix-installer-lifecycle.log")
    }

    $installerTestResults = Join-Path $outputDirectory "installer-test-results"
    New-Item -ItemType Directory -Force -Path $installerTestResults | Out-Null
    [void](Invoke-QualityProcess `
        -Label "Run release installer contract and lifecycle tests" `
        -Executable $dotnet `
        -Arguments @(
            "test", (Join-Path $repositoryRootPath "tests/compiler.IntegrationTests/compiler.IntegrationTests.csproj"),
            "-c", "Release", "--no-build", "--no-restore", "--nologo",
            "--filter", "FullyQualifiedName~compiler.IntegrationTests.ReleaseInstallerContractTests",
            "--results-directory", $installerTestResults,
            "--logger", "trx"
        ) `
        -LogName "installer-tests.log")

    $gateStatus = "passed"
} catch {
    $failureMessage = $_.Exception.Message
    [Console]::Error.WriteLine("Release quality gate failed: $failureMessage")
    throw
} finally {
    Write-QualityReport
}

Write-Host "Release quality gate passed. Diagnostics: $outputDirectory"
