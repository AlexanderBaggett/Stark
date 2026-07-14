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
    $parent = if ([string]::IsNullOrWhiteSpace($WorkDir)) {
        [System.IO.Path]::GetTempPath()
    } else {
        [System.IO.Path]::GetFullPath($WorkDir)
    }
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $path = Join-Path $parent "stark-release-smoke-$([Guid]::NewGuid().ToString('N'))"
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
        $binRoot = Join-Path $root.FullName "bin"
        if ((Test-Path -LiteralPath (Join-Path $root.FullName "sdk.json") -PathType Leaf) -and
            ((Test-Path -LiteralPath (Join-Path $binRoot "stark") -PathType Leaf) -or
             (Test-Path -LiteralPath (Join-Path $binRoot "stark.exe") -PathType Leaf))) {
            return $root.FullName
        }
    }

    throw "Could not identify an SDK root containing sdk.json and bin/stark[.exe] under '$ExtractRoot'."
}

function Get-CompilerPath {
    param([string] $PackageRoot)

    foreach ($rootCommandName in @("stark", "stark.exe")) {
        $rootCommandPath = Join-Path $PackageRoot $rootCommandName
        if (Test-Path -LiteralPath $rootCommandPath -PathType Leaf) {
            throw "The release SDK root '$PackageRoot' contains legacy root-level command '$rootCommandName'; official archives must place compiler commands under bin/."
        }
    }

    $binRoot = Join-Path $PackageRoot "bin"
    $commandName = if ($IsWindows) { "stark.exe" } else { "stark" }
    $candidate = Join-Path $binRoot $commandName
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        if (-not $IsWindows) {
            Invoke-CheckedProcess -File "chmod" -Arguments @("+x", $candidate) | Out-Null
        }

        return (Resolve-Path -LiteralPath $candidate).Path
    }

    throw "The unpacked release root '$PackageRoot' does not contain the host command 'bin/$commandName'."
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

function Invoke-StarkFromPath {
    param(
        [string[]] $Arguments,
        [string] $WorkingDirectory = ""
    )

    # On Unix, ProcessStartInfo may resolve a bare executable name against the
    # parent process's current directory before applying WorkingDirectory. Move
    # the parent lookup context to the external fixture so a checkout-local
    # launcher cannot shadow the command that PATH is meant to qualify.
    $originalCurrentDirectory = [Environment]::CurrentDirectory
    try {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            [Environment]::CurrentDirectory = [System.IO.Path]::GetFullPath($WorkingDirectory)
        }

        return Invoke-CheckedProcess `
            -File $script:CompilerCommand `
            -Arguments $Arguments `
            -WorkingDirectory $WorkingDirectory
    } finally {
        [Environment]::CurrentDirectory = $originalCurrentDirectory
    }
}

function Write-SmokeSource {
    param(
        [string] $Path,
        [string] $Text
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    Set-Content -LiteralPath $Path -Value $Text -Encoding utf8
}

function Write-SmokeExecutableProject {
    param(
        [string] $Directory,
        [string] $ProjectName,
        [string] $SourceText
    )

    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    Write-SmokeSource -Path (Join-Path $Directory "App.stark") -Text $SourceText
    Set-Content -LiteralPath (Join-Path $Directory "Stark.toml") -Value @"
[project]
name = "$ProjectName"
version = "0.1.0"
kind = "executable"

[executable]
root = "App.stark"
output = "$ProjectName"
"@ -Encoding utf8
}

function Assert-ProjectExecutable {
    param(
        [string] $ProjectDirectory,
        [string] $OutputName,
        [string] $Name
    )

    $expectedName = if ($IsWindows) { "$OutputName.exe" } else { $OutputName }
    $buildRoot = Join-Path $ProjectDirectory "build"
    if (-not (Test-Path -LiteralPath $buildRoot -PathType Container)) {
        throw "$Name build directory '$buildRoot' was not created."
    }

    $artifact = Get-ChildItem -LiteralPath $buildRoot -File -Recurse |
        Where-Object { $_.Name -ceq $expectedName } |
        Select-Object -First 1
    if ($null -eq $artifact) {
        throw "$Name executable '$expectedName' was not found under '$buildRoot'."
    }

    Assert-File -Path $artifact.FullName -Name $Name
}

function Get-JsonPropertyValue {
    param(
        [object] $Value,
        [string] $Name
    )

    if ($null -eq $Value) {
        return $null
    }

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Read-OptionalJson {
    param(
        [string] $Path,
        [string] $Name
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        throw "$Name '$Path' is not valid JSON: $($_.Exception.Message)"
    }
}

function Test-SdkAdvertisesRaylib {
    param([string] $SdkManifestPath)

    if (-not (Test-Path -LiteralPath $SdkManifestPath -PathType Leaf)) {
        return $false
    }

    $text = Get-Content -LiteralPath $SdkManifestPath -Raw
    return [Regex]::IsMatch(
        $text,
        '(?i)(Vendor\.Raylib|VendorRaylib|libVendorRaylib\.starkpkg)')
}

function Test-ReleaseAdvertisesRaylibForTarget {
    param(
        [object] $ReleaseMetadata,
        [string] $Target
    )

    $artifacts = Get-JsonPropertyValue -Value $ReleaseMetadata -Name "vendorArtifacts"
    if ($null -eq $artifacts) {
        return $false
    }

    foreach ($artifact in @($artifacts)) {
        $path = Get-JsonPropertyValue -Value $artifact -Name "path"
        if ([string]::IsNullOrWhiteSpace($path) -or
            $path -notmatch '(?i)Vendor\.?Raylib.*\.starkpkg$') {
            continue
        }

        $normalizedPath = $path.Replace('\', '/')
        $separator = $normalizedPath.IndexOf('/')
        if ($separator -lt 0 -or [string]::IsNullOrWhiteSpace($Target)) {
            return $true
        }

        $artifactTarget = $normalizedPath.Substring(0, $separator)
        if ($artifactTarget.Equals($Target, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-RaylibPackageImages {
    param([string] $VendorRoot)

    if (-not (Test-Path -LiteralPath $VendorRoot -PathType Container)) {
        return @()
    }

    return @(
        Get-ChildItem -LiteralPath $VendorRoot -File -Recurse |
            Where-Object {
                $_.Extension -ieq ".starkpkg" -and
                $_.Name -match '(?i)Vendor\.?Raylib'
            } |
            Sort-Object FullName
    )
}

function Test-RaylibPackageMatchesTarget {
    param(
        [System.IO.FileInfo] $Package,
        [string] $VendorDist,
        [string] $Target
    )

    if (-not (Test-Path -LiteralPath $VendorDist -PathType Container)) {
        return $false
    }

    $relativePath = [System.IO.Path]::GetRelativePath($VendorDist, $Package.FullName)
    if ([System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath -eq ".." -or
        $relativePath.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal)) {
        return $false
    }

    $directory = [System.IO.Path]::GetDirectoryName($relativePath)
    if ([string]::IsNullOrWhiteSpace($directory)) {
        return $true
    }

    if ([string]::IsNullOrWhiteSpace($Target)) {
        return $true
    }

    $targetDirectory = $directory.Split(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.StringSplitOptions]::RemoveEmptyEntries)[0]
    return $targetDirectory.Equals($Target, [StringComparison]::OrdinalIgnoreCase)
}

function Get-RaylibPackageVariants {
    param(
        [System.IO.FileInfo[]] $Packages,
        [string] $VendorDist
    )

    $variants = @()
    foreach ($package in $Packages) {
        $relativePath = [System.IO.Path]::GetRelativePath($VendorDist, $package.FullName)
        $directory = [System.IO.Path]::GetDirectoryName($relativePath)
        $variant = if ([string]::IsNullOrWhiteSpace($directory)) { "flat" } else { $directory.Replace('\', '/') }
        if ($variants -notcontains $variant) {
            $variants += $variant
        }
    }

    return $variants
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
        "STARK_HOME",
        "STARK_SDK_ROOT",
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

    $state["HadPath"] = $true
    $state["Path"] = $env:PATH
    $compilerBinRoot = Join-Path $PackageRoot "bin"
    $pathEntries = @($compilerBinRoot)
    if (-not $IsolatePath -and -not [string]::IsNullOrWhiteSpace($env:PATH)) {
        $pathEntries += $env:PATH
    }

    $env:PATH = [string]::Join([System.IO.Path]::PathSeparator, $pathEntries)
    if ($IsolatePath -and
        -not [string]::Equals($env:PATH, $compilerBinRoot, [StringComparison]::Ordinal)) {
        throw "Isolated release smoke PATH must contain only '$compilerBinRoot'."
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
    $script:CompilerCommand = Split-Path -Leaf $script:CompilerPath
    $stdlibDist = Join-Path $packageRoot "stdlib/dist"
    $vendorRoot = Join-Path $packageRoot "vendor"
    $vendorDist = Join-Path $vendorRoot "dist"
    Assert-Directory -Path $stdlibDist -Name "stdlib/dist"
    Assert-Directory -Path $vendorRoot -Name "vendor"

    $releaseMetadata = Read-OptionalJson `
        -Path (Join-Path $packageRoot "release.json") `
        -Name "Release manifest"
    $releasePaths = Get-JsonPropertyValue -Value $releaseMetadata -Name "paths"
    $releaseCompilerPath = [string](Get-JsonPropertyValue -Value $releasePaths -Name "compiler")
    $expectedReleaseCompilerPath = if ($IsWindows) { "bin/stark.exe" } else { "bin/stark" }
    if (-not [string]::Equals(
        $releaseCompilerPath,
        $expectedReleaseCompilerPath,
        [StringComparison]::Ordinal)) {
        throw "release.json compiler path '$releaseCompilerPath' does not match the official '$expectedReleaseCompilerPath' layout."
    }
    $effectiveTargetTriple = $TargetTriple.Trim()
    if ([string]::IsNullOrWhiteSpace($effectiveTargetTriple)) {
        $releaseTarget = Get-JsonPropertyValue -Value $releaseMetadata -Name "defaultTargetTriple"
        if (-not [string]::IsNullOrWhiteSpace($releaseTarget)) {
            $effectiveTargetTriple = $releaseTarget.Trim()
        }
    }

    $targetArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($effectiveTargetTriple)) {
        $targetArgs += "--target"
        $targetArgs += $effectiveTargetTriple
    }

    $sdkManifestPath = Join-Path $packageRoot "sdk.json"
    $sdkManifest = Read-OptionalJson -Path $sdkManifestPath -Name "SDK manifest"
    $sdkTarget = Get-JsonPropertyValue -Value $sdkManifest -Name "target"
    $sdkTargetId = [string](Get-JsonPropertyValue -Value $sdkTarget -Name "id")
    $releaseAssetSuffix = [string](Get-JsonPropertyValue -Value $releaseMetadata -Name "assetSuffix")
    $artifactTargetId = if (-not [string]::IsNullOrWhiteSpace($sdkTargetId)) {
        $sdkTargetId
    } else {
        $releaseAssetSuffix
    }
    if ([string]::IsNullOrWhiteSpace($artifactTargetId)) {
        throw "Release SDK metadata does not declare a stable target id or asset suffix."
    }

    Assert-Directory `
        -Path (Join-Path $stdlibDist $artifactTargetId) `
        -Name "target-scoped stdlib/dist/$artifactTargetId"
    $sdkAdvertisesRaylib = Test-SdkAdvertisesRaylib -SdkManifestPath $sdkManifestPath
    $releaseAdvertisesRaylib = Test-ReleaseAdvertisesRaylibForTarget `
        -ReleaseMetadata $releaseMetadata `
        -Target $artifactTargetId
    $raylibPackages = @(Get-RaylibPackageImages -VendorRoot $vendorRoot)
    $targetRaylibPackages = @(
        $raylibPackages |
            Where-Object {
                    Test-RaylibPackageMatchesTarget `
                        -Package $_ `
                        -VendorDist $vendorDist `
                        -Target $artifactTargetId
            }
    )
    $raylibAdvertised = $sdkAdvertisesRaylib -or $releaseAdvertisesRaylib
    if ($raylibAdvertised -and $raylibPackages.Count -eq 0) {
        throw "Vendor.Raylib is advertised by the release SDK metadata, but no Raylib package image exists under '$vendorRoot'."
    }

    if ($releaseAdvertisesRaylib -and $targetRaylibPackages.Count -eq 0 -and -not $sdkAdvertisesRaylib) {
        throw "release.json advertises Vendor.Raylib for target '$effectiveTargetTriple', but its target package image is missing under '$vendorDist'."
    }

    $smokeRaylib = $raylibAdvertised -or $targetRaylibPackages.Count -gt 0
    if ($smokeRaylib) {
        Assert-Directory `
            -Path (Join-Path $vendorDist $artifactTargetId) `
            -Name "target-scoped vendor/dist/$artifactTargetId"
    }

    $environmentState = Set-IsolatedEnvironment -PackageRoot $packageRoot
    try {
        # Run initial PATH discovery outside the checkout. ProcessStartInfo can
        # consult the current directory before PATH on some hosts; using the
        # external smoke source root prevents a repository launcher named
        # `stark` from shadowing the extracted native executable.
        Invoke-StarkFromPath -Arguments @("--help") -WorkingDirectory $sourceRoot | Out-Null
        $doctorResult = Invoke-StarkFromPath `
            -Arguments (@("doctor", "--strict", "--format", "json") + $targetArgs) `
            -WorkingDirectory $sourceRoot
        try {
            $doctor = $doctorResult.Stdout | ConvertFrom-Json
        } catch {
            throw "Release doctor did not produce valid JSON: $($doctorResult.Stdout)"
        }

        $doctorStatus = [string](Get-JsonPropertyValue -Value $doctor -Name "status")
        $doctorSdk = Get-JsonPropertyValue -Value $doctor -Name "sdk"
        $doctorSdkStatus = [string](Get-JsonPropertyValue -Value $doctorSdk -Name "status")
        if (-not [string]::Equals($doctorStatus, "ok", [StringComparison]::Ordinal) -or
            -not [string]::Equals($doctorSdkStatus, "ok", [StringComparison]::Ordinal)) {
            throw "Release doctor did not report an ok compiler/SDK state: $($doctorResult.Stdout)"
        }

        $systemProjectName = "ReleaseSmokeSystemProject"
        $systemProjectDir = Join-Path $sourceRoot "system-project"
        Write-SmokeExecutableProject `
            -Directory $systemProjectDir `
            -ProjectName $systemProjectName `
            -SourceText @'
import System.Console
module ReleaseSmokeSystemProject

export fn i32[min max] main()
{
    WriteLine("release SDK project smoke");
    return 0;
}
'@

        Invoke-StarkFromPath `
            -Arguments (@("build") + $targetArgs) `
            -WorkingDirectory $systemProjectDir | Out-Null
        Assert-ProjectExecutable `
            -ProjectDirectory $systemProjectDir `
            -OutputName $systemProjectName `
            -Name "System project executable"
        $systemRun = Invoke-StarkFromPath `
            -Arguments (@("run") + $targetArgs) `
            -WorkingDirectory $systemProjectDir
        if (-not $systemRun.Stdout.Contains("release SDK project smoke", [StringComparison]::Ordinal)) {
            throw "System project executable did not produce its expected output."
        }

        if ($smokeRaylib) {
            $raylibReason = if ($sdkAdvertisesRaylib) {
                "sdk.json advertisement"
            } elseif ($releaseAdvertisesRaylib) {
                "release.json target artifact"
            } else {
                "target package image"
            }
            Write-Host "Vendor.Raylib project smoke required by $raylibReason."

            $raylibProjectName = "ReleaseSmokeRaylibProject"
            $raylibProjectDir = Join-Path $sourceRoot "raylib-project"
            Write-SmokeExecutableProject `
                -Directory $raylibProjectDir `
                -ProjectName $raylibProjectName `
                -SourceText @'
import System.Console
import Vendor.Raylib
module ReleaseSmokeRaylibProject

export fn i32[min max] main()
{
    return GetScreenWidth();
}
'@

            Invoke-StarkFromPath `
                -Arguments (@("build") + $targetArgs) `
                -WorkingDirectory $raylibProjectDir | Out-Null
            Assert-ProjectExecutable `
                -ProjectDirectory $raylibProjectDir `
                -OutputName $raylibProjectName `
                -Name "Vendor.Raylib project executable"
            Write-Host "Vendor.Raylib project linked successfully; graphical execution intentionally skipped."
        } else {
            $variants = @(Get-RaylibPackageVariants -Packages $raylibPackages -VendorDist $vendorDist)
            $targetText = if ([string]::IsNullOrWhiteSpace($effectiveTargetTriple)) {
                "<compiler default>"
            } else {
                $effectiveTargetTriple
            }
            $variantText = if ($variants.Count -eq 0) { "none" } else { $variants -join ", " }
            Write-Host "Vendor.Raylib project smoke explicitly unsupported: sdk.json does not advertise it and no package image matches target '$targetText'. Available variants: $variantText."
        }

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
        Invoke-Stark -Arguments (@($runtimeSource, "--emit-exe", "-o", $runtimeExe) + $targetArgs) | Out-Null
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

        # Relocation is a release contract, not merely a property of the JSON
        # spelling. Move the extracted SDK after it has already been exercised,
        # then build fresh projects through PATH again so cached outputs or the
        # original extraction directory cannot hide an absolute path leak.
        $relocatedParent = Join-Path $smokeRoot "relocated"
        New-Item -ItemType Directory -Force -Path $relocatedParent | Out-Null
        $relocatedPackageRoot = Join-Path $relocatedParent (Split-Path -Leaf $packageRoot)
        Restore-IsolatedEnvironment -State $environmentState
        Move-Item -LiteralPath $packageRoot -Destination $relocatedPackageRoot
        $script:CompilerPath = Get-CompilerPath -PackageRoot $relocatedPackageRoot
        $script:CompilerCommand = Split-Path -Leaf $script:CompilerPath
        $relocatedEnvironmentState = Set-IsolatedEnvironment -PackageRoot $relocatedPackageRoot
        try {
            $relocatedSystemDir = Join-Path $sourceRoot "relocated-system-project"
            Write-SmokeExecutableProject `
                -Directory $relocatedSystemDir `
                -ProjectName "ReleaseSmokeRelocatedSystem" `
                -SourceText @'
import System.Console
module ReleaseSmokeRelocatedSystem

export fn i32[min max] main()
{
    WriteLine("relocated release SDK smoke");
    return 0;
}
'@
            Invoke-StarkFromPath `
                -Arguments (@("build") + $targetArgs) `
                -WorkingDirectory $relocatedSystemDir | Out-Null
            Assert-ProjectExecutable `
                -ProjectDirectory $relocatedSystemDir `
                -OutputName "ReleaseSmokeRelocatedSystem" `
                -Name "relocated System project executable"

            if ($smokeRaylib) {
                $relocatedRaylibDir = Join-Path $sourceRoot "relocated-raylib-project"
                Write-SmokeExecutableProject `
                    -Directory $relocatedRaylibDir `
                    -ProjectName "ReleaseSmokeRelocatedRaylib" `
                    -SourceText @'
import System.Console
import Vendor.Raylib
module ReleaseSmokeRelocatedRaylib

export fn i32[min max] main()
{
    return GetScreenWidth();
}
'@
                Invoke-StarkFromPath `
                    -Arguments (@("build") + $targetArgs) `
                    -WorkingDirectory $relocatedRaylibDir | Out-Null
                Assert-ProjectExecutable `
                    -ProjectDirectory $relocatedRaylibDir `
                    -OutputName "ReleaseSmokeRelocatedRaylib" `
                    -Name "relocated Vendor.Raylib project executable"
            }
        } finally {
            Restore-IsolatedEnvironment -State $relocatedEnvironmentState
        }
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
