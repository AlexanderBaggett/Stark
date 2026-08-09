#requires -Version 7.0

param(
    [Parameter(Mandatory = $true)][string] $CompilerPath,
    [Parameter(Mandatory = $true)][string] $TargetId,
    [Parameter(Mandatory = $true)][string] $TargetTriple,
    [Parameter(Mandatory = $true)][string] $ToolchainDir,
    [Parameter(Mandatory = $true)][string] $OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RequiredPath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Label,
        [switch] $Directory
    )

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $exists = if ($Directory) {
        Test-Path -LiteralPath $resolved -PathType Container
    } else {
        Test-Path -LiteralPath $resolved -PathType Leaf
    }
    if (-not $exists) {
        throw "$Label '$resolved' does not exist."
    }
    return $resolved
}

function Invoke-QualificationProcess {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $FileName,
        [string[]] $Arguments = @(),
        [Parameter(Mandatory = $true)][string] $LogPath,
        [int] $ExpectedExitCode = 0
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $workspace
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    Write-Host ">> [Assembly bridge/$TargetId] $Label"
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Could not start $Label."
    }

    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    } finally {
        $process.Dispose()
        $stopwatch.Stop()
    }

    $log = @(
        "label: $Label",
        "executable: $FileName",
        "arguments: $($Arguments -join ' ')",
        "exitCode: $exitCode",
        "durationMilliseconds: $($stopwatch.ElapsedMilliseconds)",
        "",
        "--- stdout ---",
        $stdout.TrimEnd(),
        "",
        "--- stderr ---",
        $stderr.TrimEnd(),
        ""
    ) -join [System.Environment]::NewLine
    [System.IO.File]::WriteAllText($LogPath, $log, [System.Text.UTF8Encoding]::new($false))

    if ($exitCode -ne $ExpectedExitCode) {
        throw "$Label exited with code $exitCode; expected $ExpectedExitCode. See '$LogPath'."
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        DurationMilliseconds = $stopwatch.ElapsedMilliseconds
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Test-LlvmBitcode {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $magic = [byte[]]::new(4)
        if ($stream.Read($magic, 0, $magic.Length) -ne $magic.Length) {
            return $false
        }
        return ($magic[0] -eq 0x42 -and $magic[1] -eq 0x43 -and $magic[2] -eq 0xC0 -and $magic[3] -eq 0xDE) -or
               ($magic[0] -eq 0xDE -and $magic[1] -eq 0xC0 -and $magic[2] -eq 0x17 -and $magic[3] -eq 0x0B)
    } finally {
        $stream.Dispose()
    }
}

if ($TargetId -notin @("linux-x64", "linux-arm64", "macos-x64", "macos-arm64", "windows-x64", "windows-arm64")) {
    throw "Unsupported 64-bit assembly-bridge qualification target '$TargetId'."
}

$compiler = Resolve-RequiredPath -Path $CompilerPath -Label "Stage0 compiler"
$toolchain = Resolve-RequiredPath -Path $ToolchainDir -Label "compiler-private toolchain" -Directory
$output = [System.IO.Path]::GetFullPath($OutputDir)
$workspace = Join-Path $output "workspace"
$packageDirectory = Join-Path $workspace "package"
$applicationDirectory = Join-Path $workspace "application"
$producerTemps = Join-Path $output "producer-temps"
$consumerTemps = Join-Path $output "consumer-temps"
foreach ($directory in @($output, $workspace, $packageDirectory, $applicationDirectory, $producerTemps, $consumerTemps)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$architecture = if ($TargetId.EndsWith("-x64", [StringComparison]::Ordinal)) { "x86_64" } else { "aarch64" }
$valueRegister = if ($architecture -ceq "x86_64") { "rax" } else { "x0" }
$libraryFileName = if ($TargetId.StartsWith("windows-", [StringComparison]::Ordinal)) { "Bridge.lib" } else { "libBridge.a" }
$executableFileName = if ($TargetId.StartsWith("windows-", [StringComparison]::Ordinal)) { "bridge-app.exe" } else { "bridge-app" }
$bridgePath = Join-Path $packageDirectory "Bridge.stark"
$disabledBridgePath = Join-Path $packageDirectory "Bridge.source-disabled"
$applicationPath = Join-Path $applicationDirectory "App.stark"
$libraryPath = Join-Path $packageDirectory $libraryFileName
$manifestPath = [System.IO.Path]::ChangeExtension($libraryPath, ".starkpkg")
$executablePath = Join-Path $applicationDirectory $executableFileName

$bridgeSource = @'
module Bridge

export fn void OpaqueTarget()
{
}

public unsafe ffi asm(__ARCH__) fn i64[min max] Identity(i64[min max] value)
    in("__REGISTER__") value,
    out("__REGISTER__") return,
    symbol(OpaqueTarget),
    memory(none)
{
    ""
}
'@.Replace("__ARCH__", $architecture, [StringComparison]::Ordinal).Replace("__REGISTER__", $valueRegister, [StringComparison]::Ordinal)
[System.IO.File]::WriteAllText($bridgePath, $bridgeSource, [System.Text.UTF8Encoding]::new($false))

$producer = Invoke-QualificationProcess `
    -Label "Build optimized package archive" `
    -FileName $compiler `
    -Arguments @(
        $bridgePath,
        "--emit-lib",
        "--target", $TargetTriple,
        "--package-profile", "release",
        "--toolchain-dir", $toolchain,
        "--save-temps", $producerTemps,
        "-o", $libraryPath) `
    -LogPath (Join-Path $output "producer-build.log")

foreach ($artifact in @($libraryPath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Assembly bridge producer did not create '$artifact'."
    }
}

$objectExtension = if ($TargetId.StartsWith("windows-", [StringComparison]::Ordinal)) { ".obj" } else { ".o" }
$producerObject = Join-Path $producerTemps "root$objectExtension"
if (-not (Test-LlvmBitcode -Path $producerObject)) {
    throw "Assembly bridge producer object '$producerObject' is not LLVM bitcode; the archive/ThinLTO path was not exercised."
}

if (Test-Path -LiteralPath $disabledBridgePath -PathType Leaf) {
    [System.IO.File]::Delete($disabledBridgePath)
}
[System.IO.File]::Move($bridgePath, $disabledBridgePath)

$applicationSource = @'
import Bridge
module App

export unsafe fn i32[min max] main()
{
    return (i32[min max])Bridge.Identity(73);
}
'@
[System.IO.File]::WriteAllText($applicationPath, $applicationSource, [System.Text.UTF8Encoding]::new($false))

$consumer = Invoke-QualificationProcess `
    -Label "Compile and link package consumer after source removal" `
    -FileName $compiler `
    -Arguments @(
        $applicationPath,
        "--emit-exe",
        "-I", $packageDirectory,
        "--target", $TargetTriple,
        "--package-profile", "release",
        "--toolchain-dir", $toolchain,
        "--save-temps", $consumerTemps,
        "-o", $executablePath) `
    -LogPath (Join-Path $output "consumer-build.log")

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Assembly bridge consumer executable '$executablePath' is missing."
}

$consumerLlvmPath = Join-Path $consumerTemps "root.ll"
$consumerLlvm = [System.IO.File]::ReadAllText((Resolve-RequiredPath -Path $consumerLlvmPath -Label "consumer LLVM IR"))
foreach ($requiredText in @(
    "call i64 asm sideeffect",
    "memory(none)",
    "declare void @OpaqueTarget()",
    "@llvm.used = appending global [1 x ptr] [ptr @OpaqueTarget]")) {
    if (-not $consumerLlvm.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Consumer LLVM IR is missing required assembly-bridge fact '$requiredText'."
    }
}
if ($consumerLlvm.Contains("~{memory}", [StringComparison]::Ordinal)) {
    throw "Consumer LLVM IR retained a universal memory clobber despite memory(none)."
}
if ($consumerLlvm.Contains("call i64 @Bridge_Identity", [StringComparison]::Ordinal)) {
    throw "Consumer LLVM IR routed the direct assembly call through a wrapper."
}

$application = Invoke-QualificationProcess `
    -Label "Run final linked executable" `
    -FileName $executablePath `
    -LogPath (Join-Path $output "application-run.log") `
    -ExpectedExitCode 73

$report = [ordered]@{
    schemaVersion = 1
    qualification = "stark-assembly-bridge-archive-thinlto"
    status = "passed"
    targetId = $TargetId
    targetTriple = $TargetTriple
    architecture = $architecture
    compilerSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $compiler).Hash.ToLowerInvariant()
    toolchainManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $toolchain "manifest.json")).Hash.ToLowerInvariant()
    packageManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant()
    archiveSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $libraryPath).Hash.ToLowerInvariant()
    executableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $executablePath).Hash.ToLowerInvariant()
    consumerLlvmSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $consumerLlvmPath).Hash.ToLowerInvariant()
    producerObjectIsLlvmBitcode = $true
    sourceRemovedBeforeConsumerBuild = $true
    directCallLoweredInline = $true
    opaqueSymbolRetained = $true
    preciseMemoryEffects = $true
    finalExitCode = $application.ExitCode
    durationsMilliseconds = [ordered]@{
        producer = $producer.DurationMilliseconds
        consumer = $consumer.DurationMilliseconds
        application = $application.DurationMilliseconds
    }
}
$reportPath = Join-Path $output "assembly-bridge-qualification.json"
[System.IO.File]::WriteAllText(
    $reportPath,
    (($report | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n"),
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Assembly bridge archive/ThinLTO qualification passed for $TargetId. Evidence: $reportPath"
