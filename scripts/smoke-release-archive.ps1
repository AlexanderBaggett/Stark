param(
    [Parameter(Mandatory = $true)]
    [string] $ArchivePath,

    [string] $TargetTriple = "",

    [string] $WorkDir = "",

    [switch] $KeepWorkDir,

    [switch] $IsolatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-ArchivePath {
    param([string] $Path)

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

function New-SmokeRoot {
    if (-not [string]::IsNullOrWhiteSpace($WorkDir)) {
        New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
        return (Resolve-Path -LiteralPath $WorkDir).Path
    }

    $path = Join-Path ([System.IO.Path]::GetTempPath()) "stark-release-smoke-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $File,

        [string[]] $Arguments = @(),

        [string] $WorkingDirectory = "",

        [int[]] $AllowedExitCodes = @(0)
    )

    $display = "$File $($Arguments -join ' ')".Trim()
    Write-Host ">> $display"

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $File
    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }

    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start '$File'."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()

    if (-not [string]::IsNullOrWhiteSpace($stdout)) {
        Write-Host $stdout.TrimEnd()
    }

    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host $stderr.TrimEnd()
    }

    if ($AllowedExitCodes -notcontains $process.ExitCode) {
        throw "Command '$display' exited with code $($process.ExitCode)."
    }

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Assert-Directory {
    param(
        [string] $Path,
        [string] $Name
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Name directory '$Path' was not found."
    }
}

function Assert-File {
    param(
        [string] $Path,
        [string] $Name
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name file '$Path' was not created."
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -le 0) {
        throw "$Name file '$Path' is empty."
    }
}

function Get-ArchiveRoot {
    param([string] $ExtractRoot)

    $roots = @(Get-ChildItem -LiteralPath $ExtractRoot -Directory | Sort-Object Name)
    foreach ($root in $roots) {
        if ((Test-Path -LiteralPath (Join-Path $root.FullName "stark") -PathType Leaf) -or
            (Test-Path -LiteralPath (Join-Path $root.FullName "stark.exe") -PathType Leaf)) {
            return $root.FullName
        }
    }

    if ($roots.Count -eq 1) {
        return $roots[0].FullName
    }

    throw "Could not identify the unpacked release root under '$ExtractRoot'."
}

function Get-CompilerPath {
    param([string] $PackageRoot)

    $candidates = @(
        (Join-Path $PackageRoot "stark.exe"),
        (Join-Path $PackageRoot "stark")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            if (-not $IsWindows) {
                Invoke-CheckedProcess -File "chmod" -Arguments @("+x", $candidate) | Out-Null
            }

            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "The unpacked release root '$PackageRoot' does not contain stark or stark.exe."
}

function Get-ExecutablePath {
    param(
        [string] $Directory,
        [string] $Name
    )

    if ($IsWindows) {
        return Join-Path $Directory "$Name.exe"
    }

    return Join-Path $Directory $Name
}

function Get-ObjectPath {
    param(
        [string] $Directory,
        [string] $Name
    )

    if ($IsWindows) {
        return Join-Path $Directory "$Name.obj"
    }

    return Join-Path $Directory "$Name.o"
}

function Get-LibraryPath {
    param(
        [string] $Directory,
        [string] $Name
    )

    if ($IsWindows) {
        return Join-Path $Directory "$Name.lib"
    }

    return Join-Path $Directory "lib$Name.a"
}

function Invoke-Stark {
    param(
        [string[]] $Arguments,
        [string] $WorkingDirectory = ""
    )

    return Invoke-CheckedProcess -File $script:CompilerPath -Arguments $Arguments -WorkingDirectory $WorkingDirectory
}

function Write-SmokeSource {
    param(
        [string] $Path,
        [string] $Text
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    Set-Content -LiteralPath $Path -Value $Text -Encoding utf8
}

function Set-IsolatedEnvironment {
    param([string] $PackageRoot)

    $state = @{
        Values = @{}
        HadPath = $false
        Path = $null
    }

    $names = @(
        "STARK_PATH",
        "STARK_TOOLCHAIN_DIR",
        "STARK_LLVM_LIB",
        "STARK_CLANG",
        "STARK_LINKER",
        "STARK_ARCHIVER"
    )

    foreach ($name in $names) {
        $state["Values"][$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $null)
    }

    if ($IsolatePath) {
        $state["HadPath"] = $true
        $state["Path"] = $env:PATH
        $pathEntries = @($PackageRoot)
        $toolchainRoot = Join-Path $PackageRoot "toolchain"
        if (Test-Path -LiteralPath $toolchainRoot -PathType Container) {
            $toolchainBin = Get-ChildItem -LiteralPath $toolchainRoot -Directory |
                Sort-Object Name |
                ForEach-Object { Join-Path $_.FullName "bin" } |
                Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
                Select-Object -First 1
            if ($null -ne $toolchainBin) {
                $pathEntries += $toolchainBin
            }
        }

        $env:PATH = [string]::Join([System.IO.Path]::PathSeparator, $pathEntries)
    }

    return ,$state
}

function Restore-IsolatedEnvironment {
    param([hashtable] $State)

    foreach ($entry in $State["Values"].GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }

    if ($State["HadPath"]) {
        $env:PATH = $State["Path"]
    }
}

$archive = Resolve-ArchivePath -Path $ArchivePath
$smokeRoot = New-SmokeRoot
$extractRoot = Join-Path $smokeRoot "extract"
$sourceRoot = Join-Path $smokeRoot "src"
$outputRoot = Join-Path $smokeRoot "out"
New-Item -ItemType Directory -Force -Path $extractRoot, $sourceRoot, $outputRoot | Out-Null

try {
    Write-Host "Extracting $archive"
    if ($archive.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) {
        Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot -Force
    } else {
        Invoke-CheckedProcess -File "tar" -Arguments @("-xzf", $archive, "-C", $extractRoot) | Out-Null
    }

    $packageRoot = Get-ArchiveRoot -ExtractRoot $extractRoot
    $script:CompilerPath = Get-CompilerPath -PackageRoot $packageRoot
    $stdlibDist = Join-Path $packageRoot "stdlib/dist"
    $vendorRoot = Join-Path $packageRoot "vendor"
    Assert-Directory -Path $stdlibDist -Name "stdlib/dist"
    Assert-Directory -Path $vendorRoot -Name "vendor"

    $targetArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($TargetTriple)) {
        $targetArgs += "--target"
        $targetArgs += $TargetTriple.Trim()
    }

    $environmentState = Set-IsolatedEnvironment -PackageRoot $packageRoot
    try {
        Invoke-Stark -Arguments @("--help") | Out-Null
        Invoke-Stark -Arguments (@("doctor") + $targetArgs) | Out-Null

        $basicSource = Join-Path $sourceRoot "ReleaseSmokeBasic.stark"
        Write-SmokeSource -Path $basicSource -Text @'
module ReleaseSmokeBasic

inline finite law i32[min max] Add(i32[min max] left, i32[min max] right)
{
    return left + right;
}

export fn i32[min max] main()
{
    return Add(20, 22) - 42;
}
'@

        $librarySource = Join-Path $sourceRoot "ReleaseSmokeLibrary.stark"
        Write-SmokeSource -Path $librarySource -Text @'
module ReleaseSmokeLibrary

public finite law i32[min max] Value()
{
    return 7;
}
'@

        Invoke-Stark -Arguments (@($basicSource, "--check") + $targetArgs) | Out-Null

        $mirPath = Join-Path $outputRoot "ReleaseSmokeBasic.mir"
        Invoke-Stark -Arguments (@($basicSource, "--emit-mir", "-o", $mirPath) + $targetArgs) | Out-Null
        Assert-File -Path $mirPath -Name "MIR"

        $ssaPath = Join-Path $outputRoot "ReleaseSmokeBasic.ssa"
        Invoke-Stark -Arguments (@($basicSource, "--emit-ssa", "-o", $ssaPath) + $targetArgs) | Out-Null
        Assert-File -Path $ssaPath -Name "SSA"

        $llvmPath = Join-Path $outputRoot "ReleaseSmokeBasic.ll"
        Invoke-Stark -Arguments (@($basicSource, "--emit-llvm", "-o", $llvmPath) + $targetArgs) | Out-Null
        Assert-File -Path $llvmPath -Name "LLVM IR"

        $objectPath = Get-ObjectPath -Directory $outputRoot -Name "ReleaseSmokeBasic"
        Invoke-Stark -Arguments (@($basicSource, "--emit-obj", "-o", $objectPath) + $targetArgs) | Out-Null
        Assert-File -Path $objectPath -Name "object"

        $libraryPath = Get-LibraryPath -Directory $outputRoot -Name "ReleaseSmokeLibrary"
        Invoke-Stark -Arguments (@($librarySource, "--emit-lib", "-o", $libraryPath) + $targetArgs) | Out-Null
        Assert-File -Path $libraryPath -Name "library"

        $exePath = Get-ExecutablePath -Directory $outputRoot -Name "ReleaseSmokeBasic"
        Invoke-Stark -Arguments (@($basicSource, "--emit-exe", "-o", $exePath) + $targetArgs) | Out-Null
        Assert-File -Path $exePath -Name "executable"
        Invoke-CheckedProcess -File $exePath -WorkingDirectory $outputRoot | Out-Null

        $runtimeDir = Join-Path $smokeRoot "runtime"
        New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
        $runtimeSource = Join-Path $sourceRoot "ReleaseSmokeRuntime.stark"
        Write-SmokeSource -Path $runtimeSource -Text @'
import System
import System.Text
module ReleaseSmokeRuntime

fn bool StatusOk(System.IO.IOStatus status)
{
    switch (status)
    {
        case System.IO.IOStatus.Ok:
            return true;
        case System.IO.IOStatus.Err(var error):
            return false;
    }
}

fn i32[min max] CheckText(System.IO.IOResult<System.Text.OwnedAscii> result)
{
    switch (result)
    {
        case System.IO.IOResult<System.Text.OwnedAscii>.Err(var error):
            return 10;
        case System.IO.IOResult<System.Text.OwnedAscii>.Ok(var text):
            if (text.Length() != 5)
            {
                return 11;
            }

            stack i8[min max][] bytes = text.AsSlice();
            if (bytes[0] != (i8[min max])115 || bytes[4] != (i8[min max])107)
            {
                return 12;
            }

            return 0;
    }
}

export unsafe fn i32[min max] main()
{
    if (System.Console.WriteLine("release smoke") != System.IO.IOStatus.Ok)
    {
        return 1;
    }

    if (System.Math.Sqrt(9.0) != 3.0)
    {
        return 2;
    }

    if (!StatusOk(System.IO.File.WriteAllText("release-smoke.txt", "stark")))
    {
        return 3;
    }

    stack i32[min max] textStatus = CheckText(System.IO.File.ReadAllText("release-smoke.txt"));
    if (textStatus != 0)
    {
        return textStatus;
    }

    return 0;
}
'@

        $runtimeExe = Get-ExecutablePath -Directory $runtimeDir -Name "ReleaseSmokeRuntime"
        Invoke-Stark -Arguments (@($runtimeSource, "--emit-exe", "-I", $stdlibDist, "-o", $runtimeExe) + $targetArgs) | Out-Null
        Assert-File -Path $runtimeExe -Name "runtime executable"
        Invoke-CheckedProcess -File $runtimeExe -WorkingDirectory $runtimeDir | Out-Null

        $nativePackageDir = Join-Path $smokeRoot "native-package"
        $nativeAppDir = Join-Path $smokeRoot "native-app"
        New-Item -ItemType Directory -Force -Path $nativePackageDir, $nativeAppDir | Out-Null

        $nativePackageSource = Join-Path $nativePackageDir "ReleaseSmokeNative.stark"
        $nativeSource = Join-Path $nativePackageDir "ReleaseSmokeNative.c"
        $nativeLibrary = Get-LibraryPath -Directory $nativePackageDir -Name "ReleaseSmokeNative"
        Write-SmokeSource -Path $nativePackageSource -Text @'
module ReleaseSmokeNative

unsafe ffi fn i32[min max] stark_release_smoke_native_value();

public fn i32[min max] GetNativeValue()
{
    unsafe
    {
        return stark_release_smoke_native_value();
    }
}
'@
        Set-Content -LiteralPath $nativeSource -Value @'
int stark_release_smoke_native_value(void) {
    return 44;
}
'@ -Encoding ascii

        Invoke-Stark -Arguments (@($nativePackageSource, "--emit-lib", "-o", $nativeLibrary, "--native-source", $nativeSource) + $targetArgs) | Out-Null
        Assert-File -Path $nativeLibrary -Name "native package library"
        Remove-Item -LiteralPath $nativePackageSource -Force

        $nativeAppSource = Join-Path $nativeAppDir "ReleaseSmokeNativeApp.stark"
        Write-SmokeSource -Path $nativeAppSource -Text @'
import ReleaseSmokeNative
module ReleaseSmokeNativeApp

export fn i32[min max] main()
{
    return GetNativeValue() - 44;
}
'@

        $nativeAppExe = Get-ExecutablePath -Directory $nativeAppDir -Name "ReleaseSmokeNativeApp"
        Invoke-Stark -Arguments (@($nativeAppSource, "--emit-exe", "-I", $nativePackageDir, "-o", $nativeAppExe) + $targetArgs) | Out-Null
        Assert-File -Path $nativeAppExe -Name "native package executable"
        Invoke-CheckedProcess -File $nativeAppExe -WorkingDirectory $nativeAppDir | Out-Null
    } finally {
        Restore-IsolatedEnvironment -State $environmentState
    }

    Write-Host "Release archive smoke passed: $archive"
} finally {
    if ($KeepWorkDir) {
        Write-Host "Kept smoke work directory: $smokeRoot"
    } else {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
